using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Tacet.Internal;
using Xunit;

namespace Tacet.Tests.Unit.Integration;

/// <summary>
/// Custom <see cref="FactAttribute"/> that skips the test unless
/// <c>BURST_INTEGRATION_TEST=1</c> is set in the environment.
/// </summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    private static readonly bool _enabled =
        Environment.GetEnvironmentVariable("BURST_INTEGRATION_TEST") == "1";

    public IntegrationFactAttribute()
    {
        if (!_enabled)
            Skip = "Set BURST_INTEGRATION_TEST=1 to run integration tests";
    }
}

/// <summary>
/// Integration tests that run against a real (or substrate-emulated) S3 endpoint.
///
/// <para>These tests are gated on <c>BURST_INTEGRATION_TEST=1</c> and are skipped
/// in CI unit test runs. Run them locally or in the integration CI job with:</para>
/// <code>
/// BURST_INTEGRATION_TEST=1 dotnet test --filter "Category=Integration"
/// </code>
/// </summary>
[Trait("Category", "Integration")]
public sealed class S3IntegrationTest : IAsyncLifetime
{
    private static readonly bool _enabled =
        Environment.GetEnvironmentVariable("BURST_INTEGRATION_TEST") == "1";

    private readonly string _bucket = $"tacet-integration-{Guid.NewGuid():N}";
    private readonly string _region;
    private readonly AmazonS3Client _s3;

    public S3IntegrationTest()
    {
        _region = Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION") ?? "us-east-1";
        _s3 = AwsHelpers.BuildS3Client(_region);
    }

    public async Task InitializeAsync()
    {
        if (!_enabled) return;
        await _s3.PutBucketAsync(new PutBucketRequest
        {
            BucketName = _bucket,
            UseClientRegion = true,
        });
    }

    public async Task DisposeAsync()
    {
        if (!_enabled) return;
        await DeleteBucketAsync(_bucket);
        _s3.Dispose();
    }

    // -------------------------------------------------------------------------
    // Session ID format validation (always runs — no S3 needed)
    // -------------------------------------------------------------------------

    [Fact]
    public void SessionId_MatchesExpectedPattern()
    {
        var id = Protocol.GenerateSessionId();
        Assert.Matches(@"^cs-\d{8}-[0-9a-f]{8}$", id);
    }

    // -------------------------------------------------------------------------
    // S3 round-trip: upload task, simulate worker writing result+status, collect
    // -------------------------------------------------------------------------

    [IntegrationFact]
    public async Task FullRoundTrip_UploadTaskAndCollectResult()
    {
        var sessionId = Protocol.GenerateSessionId();
        const int chunkCount = 3;
        const string fnName = "double";

        // 1. Upload task files (simulating what Session.RunAsync does)
        for (int i = 0; i < chunkCount; i++)
        {
            var items = new[]
            {
                JsonSerializer.SerializeToElement(i * 10, Protocol.JsonOpts),
                JsonSerializer.SerializeToElement(i * 10 + 1, Protocol.JsonOpts),
            };
            var payload = new TaskPayload(items, fnName, i);
            var json = JsonSerializer.Serialize(payload, Protocol.JsonOpts);
            await PutTextAsync(Protocol.TaskKey(sessionId, i), json);
        }

        // 2. Simulate workers writing result + status files
        for (int i = 0; i < chunkCount; i++)
        {
            var results = new JsonElement?[]
            {
                JsonSerializer.SerializeToElement((i * 10) * 2, Protocol.JsonOpts),
                JsonSerializer.SerializeToElement((i * 10 + 1) * 2, Protocol.JsonOpts),
            };
            var errors = new string?[] { null, null };
            var resultPayload = new ResultPayload(results, errors);
            await PutTextAsync(Protocol.ResultKey(sessionId, i),
                JsonSerializer.Serialize(resultPayload, Protocol.JsonOpts));
            await PutTextAsync(Protocol.StatusKey(sessionId, i), "done");
        }

        // 3. Poll status files — all should read "done"
        for (int i = 0; i < chunkCount; i++)
        {
            var status = await GetTextAsync(Protocol.StatusKey(sessionId, i));
            Assert.Equal("done", status.Trim());
        }

        // 4. Download and deserialize result files
        var allResults = new List<int>();
        for (int i = 0; i < chunkCount; i++)
        {
            var json = await GetTextAsync(Protocol.ResultKey(sessionId, i));
            var result = JsonSerializer.Deserialize<ResultPayload>(json, Protocol.JsonOpts)!;
            foreach (var r in result.Results)
            {
                if (r.HasValue)
                    allResults.Add(r.Value.GetInt32());
            }
        }

        // 5. Verify: each item was doubled
        Assert.Equal(chunkCount * 2, allResults.Count);
        // items were [0,1], [10,11], [20,21] → doubled [0,2], [20,22], [40,42]
        var expected = Enumerable.Range(0, chunkCount)
            .SelectMany(i => new[] { i * 10 * 2, (i * 10 + 1) * 2 })
            .OrderBy(x => x)
            .ToList();
        Assert.Equal(expected, allResults.OrderBy(x => x).ToList());
    }

    [IntegrationFact]
    public async Task StatusFile_Transitions_RunningToDone()
    {
        var sessionId = Protocol.GenerateSessionId();

        // Write "running" first
        await PutTextAsync(Protocol.StatusKey(sessionId, 0), "running");
        var status = await GetTextAsync(Protocol.StatusKey(sessionId, 0));
        Assert.Equal("running", status.Trim());

        // Transition to "done"
        await PutTextAsync(Protocol.StatusKey(sessionId, 0), "done");
        status = await GetTextAsync(Protocol.StatusKey(sessionId, 0));
        Assert.Equal("done", status.Trim());
    }

    [IntegrationFact]
    public async Task TaskPayload_RoundTrip_ViaS3()
    {
        var sessionId = Protocol.GenerateSessionId();
        var items = new[]
        {
            JsonSerializer.SerializeToElement("hello", Protocol.JsonOpts),
            JsonSerializer.SerializeToElement("world", Protocol.JsonOpts),
        };
        var original = new TaskPayload(items, "upper", 0);
        var json = JsonSerializer.Serialize(original, Protocol.JsonOpts);

        await PutTextAsync(Protocol.TaskKey(sessionId, 0), json);

        var downloaded = await GetTextAsync(Protocol.TaskKey(sessionId, 0));
        var roundTripped = JsonSerializer.Deserialize<TaskPayload>(downloaded, Protocol.JsonOpts)!;

        Assert.Equal("upper", roundTripped.Function);
        Assert.Equal(0, roundTripped.ChunkIndex);
        Assert.Equal(2, roundTripped.Items.Length);
        Assert.Equal("hello", roundTripped.Items[0].GetString());
        Assert.Equal("world", roundTripped.Items[1].GetString());
    }

    [IntegrationFact]
    public async Task ManifestKey_WrittenAndReadBack()
    {
        var sessionId = Protocol.GenerateSessionId();
        var manifest = new Manifest(
            SessionId: sessionId,
            Language: Protocol.Language,
            Status: "running",
            TasksTotal: 4,
            TasksComplete: 0,
            TasksFailed: 0,
            WorkersActive: 4,
            CostActual: 0.0,
            CostEstimatePerHour: 0.25,
            CreatedAt: DateTime.UtcNow.ToString("O"),
            ChunkCount: 4,
            TaskCount: 100,
            WorkersRequested: 4,
            WorkersActual: 4,
            Cpu: 1,
            MemoryGb: 2,
            Backend: "fargate",
            Spot: false,
            Region: _region,
            EnvHash: string.Empty,
            LibraryVersion: Protocol.LibraryVersion);

        var json = JsonSerializer.Serialize(manifest, Protocol.JsonOpts);
        await PutTextAsync(Protocol.ManifestKey(sessionId), json);

        var downloaded = await GetTextAsync(Protocol.ManifestKey(sessionId));
        var roundTripped = JsonSerializer.Deserialize<Manifest>(downloaded, Protocol.JsonOpts)!;

        Assert.Equal(sessionId, roundTripped.SessionId);
        Assert.Equal(Protocol.Language, roundTripped.Language);
        Assert.Equal("running", roundTripped.Status);
        Assert.Equal(4, roundTripped.TasksTotal);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task PutTextAsync(string key, string text)
    {
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName  = _bucket,
            Key         = key,
            ContentBody = text,
            ContentType = "text/plain",
        });
    }

    private async Task<string> GetTextAsync(string key)
    {
        var resp = await _s3.GetObjectAsync(_bucket, key);
        using var reader = new StreamReader(resp.ResponseStream);
        return await reader.ReadToEndAsync();
    }

    private async Task DeleteBucketAsync(string bucket)
    {
        // First delete all objects so the bucket can be removed
        ListObjectsV2Response listResp;
        string? continuationToken = null;
        do
        {
            listResp = await _s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName        = bucket,
                ContinuationToken = continuationToken,
            });

            if (listResp.S3Objects.Count > 0)
            {
                await _s3.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = bucket,
                    Objects = listResp.S3Objects
                        .Select(o => new KeyVersion { Key = o.Key })
                        .ToList(),
                });
            }

            continuationToken = listResp.IsTruncated ? listResp.NextContinuationToken : null;
        }
        while (continuationToken is not null);

        await _s3.DeleteBucketAsync(bucket);
    }
}
