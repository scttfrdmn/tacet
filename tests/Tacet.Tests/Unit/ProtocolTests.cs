using System.Text.Json;
using System.Text.RegularExpressions;
using Tacet.Internal;
using Xunit;

namespace Tacet.Tests.Unit;

/// <summary>
/// Unit tests for the burst wire protocol: session IDs, task IDs, S3 keys,
/// and JSON round-trip for TaskPayload and ResultPayload.
/// </summary>
public sealed class ProtocolTests
{
    // -------------------------------------------------------------------------
    // Session ID format
    // -------------------------------------------------------------------------

    [Fact]
    public void GenerateSessionId_HasCorrectFormat()
    {
        var id = Protocol.GenerateSessionId();
        // Expected: cs-{yyyyMMdd}-{8 lowercase hex chars}
        Assert.Matches(@"^cs-\d{8}-[0-9a-f]{8}$", id);
    }

    [Fact]
    public void GenerateSessionId_StartsWithCsPrefix()
    {
        var id = Protocol.GenerateSessionId();
        Assert.StartsWith("cs-", id);
    }

    [Fact]
    public void GenerateSessionId_DateSegmentMatchesToday()
    {
        var id = Protocol.GenerateSessionId();
        var datePart = id.Split('-')[1];
        var today = DateTime.UtcNow.ToString("yyyyMMdd");
        Assert.Equal(today, datePart);
    }

    [Fact]
    public void GenerateSessionId_IsUnique()
    {
        var ids = Enumerable.Range(0, 100).Select(_ => Protocol.GenerateSessionId()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    // -------------------------------------------------------------------------
    // Task ID format
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(0,    "task-0000")]
    [InlineData(1,    "task-0001")]
    [InlineData(9,    "task-0009")]
    [InlineData(42,   "task-0042")]
    [InlineData(999,  "task-0999")]
    [InlineData(9999, "task-9999")]
    public void TaskId_ZeroPadsFourDigits(int index, string expected)
    {
        Assert.Equal(expected, Protocol.TaskId(index));
    }

    // -------------------------------------------------------------------------
    // S3 key patterns
    // -------------------------------------------------------------------------

    [Fact]
    public void ManifestKey_HasCorrectPattern()
    {
        var sessionId = "cs-20240101-abcd1234";
        var key = Protocol.ManifestKey(sessionId);
        Assert.Equal($"sessions/{sessionId}/manifest.json", key);
    }

    [Theory]
    [InlineData(0,    "sessions/cs-20240101-abcd1234/tasks/task-0000.task")]
    [InlineData(1,    "sessions/cs-20240101-abcd1234/tasks/task-0001.task")]
    [InlineData(99,   "sessions/cs-20240101-abcd1234/tasks/task-0099.task")]
    public void TaskKey_HasCorrectPattern(int index, string expected)
    {
        var sessionId = "cs-20240101-abcd1234";
        Assert.Equal(expected, Protocol.TaskKey(sessionId, index));
    }

    [Theory]
    [InlineData(0,  "sessions/cs-20240101-abcd1234/tasks/task-0000.result")]
    [InlineData(7,  "sessions/cs-20240101-abcd1234/tasks/task-0007.result")]
    public void ResultKey_HasCorrectPattern(int index, string expected)
    {
        var sessionId = "cs-20240101-abcd1234";
        Assert.Equal(expected, Protocol.ResultKey(sessionId, index));
    }

    [Theory]
    [InlineData(0,  "sessions/cs-20240101-abcd1234/tasks/task-0000.status")]
    [InlineData(3,  "sessions/cs-20240101-abcd1234/tasks/task-0003.status")]
    public void StatusKey_HasCorrectPattern(int index, string expected)
    {
        var sessionId = "cs-20240101-abcd1234";
        Assert.Equal(expected, Protocol.StatusKey(sessionId, index));
    }

    [Theory]
    [InlineData(0, "sessions/cs-20240101-abcd1234/tasks/task-0000.error")]
    [InlineData(5, "sessions/cs-20240101-abcd1234/tasks/task-0005.error")]
    public void ErrorKey_HasCorrectPattern(int index, string expected)
    {
        var sessionId = "cs-20240101-abcd1234";
        Assert.Equal(expected, Protocol.ErrorKey(sessionId, index));
    }

    // -------------------------------------------------------------------------
    // TaskPayload JSON round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public void TaskPayload_SerializesAndDeserializesCorrectly()
    {
        var items = new[]
        {
            JsonSerializer.SerializeToElement(42, Protocol.JsonOpts),
            JsonSerializer.SerializeToElement(99, Protocol.JsonOpts),
        };
        var original = new TaskPayload(items, "my_function", 3);

        var json = JsonSerializer.Serialize(original, Protocol.JsonOpts);
        var roundTripped = JsonSerializer.Deserialize<TaskPayload>(json, Protocol.JsonOpts);

        Assert.NotNull(roundTripped);
        Assert.Equal("my_function", roundTripped!.Function);
        Assert.Equal(3, roundTripped.ChunkIndex);
        Assert.Equal(2, roundTripped.Items.Length);
        Assert.Equal(42, roundTripped.Items[0].GetInt32());
        Assert.Equal(99, roundTripped.Items[1].GetInt32());
    }

    [Fact]
    public void TaskPayload_JsonFieldNames_MatchProtocol()
    {
        var items = new[] { JsonSerializer.SerializeToElement("hello", Protocol.JsonOpts) };
        var payload = new TaskPayload(items, "fn", 0);
        var json = JsonSerializer.Serialize(payload, Protocol.JsonOpts);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("items", out _));
        Assert.True(root.TryGetProperty("function", out _));
        Assert.True(root.TryGetProperty("chunk_index", out _));
    }

    // -------------------------------------------------------------------------
    // ResultPayload JSON round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public void ResultPayload_SerializesAndDeserializesCorrectly()
    {
        var results = new JsonElement?[]
        {
            JsonSerializer.SerializeToElement(100, Protocol.JsonOpts),
            null,
        };
        var errors = new string?[] { null, "some error" };
        var original = new ResultPayload(results, errors);

        var json = JsonSerializer.Serialize(original, Protocol.JsonOpts);
        var roundTripped = JsonSerializer.Deserialize<ResultPayload>(json, Protocol.JsonOpts);

        Assert.NotNull(roundTripped);
        Assert.Equal(2, roundTripped!.Results.Length);
        Assert.Equal(2, roundTripped.Errors.Length);
        Assert.True(roundTripped.Results[0].HasValue);
        Assert.Equal(100, roundTripped.Results[0]!.Value.GetInt32());
        Assert.Null(roundTripped.Results[1]);
        Assert.Null(roundTripped.Errors[0]);
        Assert.Equal("some error", roundTripped.Errors[1]);
    }

    [Fact]
    public void ResultPayload_JsonFieldNames_MatchProtocol()
    {
        var payload = new ResultPayload(
            new JsonElement?[] { JsonSerializer.SerializeToElement(1, Protocol.JsonOpts) },
            new string?[] { null });
        var json = JsonSerializer.Serialize(payload, Protocol.JsonOpts);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("results", out _));
        Assert.True(root.TryGetProperty("errors", out _));
    }

    // -------------------------------------------------------------------------
    // Cost estimation (sanity check)
    // -------------------------------------------------------------------------

    [Fact]
    public void EstimateCostPerHour_IsPositive()
    {
        var cost = Protocol.EstimateCostPerHour(1, 2, 4);
        Assert.True(cost > 0);
    }

    [Fact]
    public void EstimateCostPerHour_ScalesWithWorkers()
    {
        var cost1 = Protocol.EstimateCostPerHour(1, 2, 1);
        var cost4 = Protocol.EstimateCostPerHour(1, 2, 4);
        Assert.Equal(cost1 * 4, cost4, precision: 6);
    }
}
