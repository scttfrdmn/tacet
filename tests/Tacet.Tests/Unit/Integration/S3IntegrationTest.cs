using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Tacet.Internal;
using Xunit;
using TacetProtocol = Tacet.Internal.Protocol;

namespace Tacet.Tests.Unit.Integration;

/// <summary>
/// Custom <see cref="FactAttribute"/> that skips the test unless
/// <c>BURST_INTEGRATION_TEST=1</c> is set in the environment.
/// </summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    // S3 integration tests against substrate are blocked pending substrate issue #321:
    // The .NET AWS SDK uses SigV4 aws-chunked encoding which substrate stores verbatim
    // instead of decoding. Tests pass against real AWS.
    private static readonly bool _enabled =
        Environment.GetEnvironmentVariable("BURST_INTEGRATION_TEST") == "1" &&
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL"));

    public IntegrationFactAttribute()
    {
        if (!_enabled)
            Skip = "Set BURST_INTEGRATION_TEST=1 without AWS_ENDPOINT_URL to run against real AWS (substrate blocked by issue #321)";
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
        var id = TacetProtocol.GenerateSessionId();
        Assert.Matches(@"^cs-\d{8}-[0-9a-f]{8}$", id);
    }

    // -------------------------------------------------------------------------
    // S3 round-trip: upload task, simulate worker writing result+status, collect
    // -------------------------------------------------------------------------

    [IntegrationFact]
    public async Task FullRoundTrip_UploadTaskAndCollectResult()
    {
        var sessionId = TacetProtocol.GenerateSessionId();
        const int chunkCount = 3;
        const string fnName = "double";

        // 1. Upload task files (simulating what Session.RunAsync does)
        for (int i = 0; i < chunkCount; i++)
        {
            var items = new[]
            {
                JsonSerializer.SerializeToElement(i * 10, TacetProtocol.JsonOpts),
                JsonSerializer.SerializeToElement(i * 10 + 1, TacetProtocol.JsonOpts),
            };
            var payload = new TaskPayload(items, fnName, i);
            var json = JsonSerializer.Serialize(payload, TacetProtocol.JsonOpts);
            await PutTextAsync(TacetProtocol.TaskKey(sessionId, i), json);
        }

        // 2. Simulate workers writing result + status files
        for (int i = 0; i < chunkCount; i++)
        {
            var results = new JsonElement?[]
            {
                JsonSerializer.SerializeToElement((i * 10) * 2, TacetProtocol.JsonOpts),
                JsonSerializer.SerializeToElement((i * 10 + 1) * 2, TacetProtocol.JsonOpts),
            };
            var errors = new string?[] { null, null };
            var resultPayload = new ResultPayload(results, errors);
            await PutTextAsync(TacetProtocol.ResultKey(sessionId, i),
                JsonSerializer.Serialize(resultPayload, TacetProtocol.JsonOpts));
            await PutTextAsync(TacetProtocol.StatusKey(sessionId, i), "done");
        }

        // 3. Poll status files — all should read "done"
        for (int i = 0; i < chunkCount; i++)
        {
            var status = await GetTextAsync(TacetProtocol.StatusKey(sessionId, i));
            Assert.Equal("done", status.Trim());
        }

        // 4. Download and deserialize result files
        var allResults = new List<int>();
        for (int i = 0; i < chunkCount; i++)
        {
            var json = await GetTextAsync(TacetProtocol.ResultKey(sessionId, i));
            var result = JsonSerializer.Deserialize<ResultPayload>(json, TacetProtocol.JsonOpts)!;
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
        var sessionId = TacetProtocol.GenerateSessionId();

        // Write "running" first
        await PutTextAsync(TacetProtocol.StatusKey(sessionId, 0), "running");
        var status = await GetTextAsync(TacetProtocol.StatusKey(sessionId, 0));
        Assert.Equal("running", status.Trim());

        // Transition to "done"
        await PutTextAsync(TacetProtocol.StatusKey(sessionId, 0), "done");
        status = await GetTextAsync(TacetProtocol.StatusKey(sessionId, 0));
        Assert.Equal("done", status.Trim());
    }

    [IntegrationFact]
    public async Task TaskPayload_RoundTrip_ViaS3()
    {
        var sessionId = TacetProtocol.GenerateSessionId();
        var items = new[]
        {
            JsonSerializer.SerializeToElement("hello", TacetProtocol.JsonOpts),
            JsonSerializer.SerializeToElement("world", TacetProtocol.JsonOpts),
        };
        var original = new TaskPayload(items, "upper", 0);
        var json = JsonSerializer.Serialize(original, TacetProtocol.JsonOpts);

        await PutTextAsync(TacetProtocol.TaskKey(sessionId, 0), json);

        var downloaded = await GetTextAsync(TacetProtocol.TaskKey(sessionId, 0));
        var roundTripped = JsonSerializer.Deserialize<TaskPayload>(downloaded, TacetProtocol.JsonOpts)!;

        Assert.Equal("upper", roundTripped.Function);
        Assert.Equal(0, roundTripped.ChunkIndex);
        Assert.Equal(2, roundTripped.Items.Length);
        Assert.Equal("hello", roundTripped.Items[0].GetString());
        Assert.Equal("world", roundTripped.Items[1].GetString());
    }

    [IntegrationFact]
    public async Task ManifestKey_WrittenAndReadBack()
    {
        var sessionId = TacetProtocol.GenerateSessionId();
        var manifest = new Manifest(
            SessionId: sessionId,
            Language: TacetProtocol.Language,
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
            LibraryVersion: TacetProtocol.LibraryVersion);

        var json = JsonSerializer.Serialize(manifest, TacetProtocol.JsonOpts);
        await PutTextAsync(TacetProtocol.ManifestKey(sessionId), json);

        var downloaded = await GetTextAsync(TacetProtocol.ManifestKey(sessionId));
        var roundTripped = JsonSerializer.Deserialize<Manifest>(downloaded, TacetProtocol.JsonOpts)!;

        Assert.Equal(sessionId, roundTripped.SessionId);
        Assert.Equal(TacetProtocol.Language, roundTripped.Language);
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
        using var ms = new MemoryStream();
        await resp.ResponseStream.CopyToAsync(ms);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
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
