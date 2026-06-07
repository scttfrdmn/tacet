using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tacet.Internal;

/// <summary>
/// JSON payload written to S3 as <c>task-{index:0000}.task</c>.
/// </summary>
internal sealed record TaskPayload(
    [property: JsonPropertyName("items")] JsonElement[] Items,
    [property: JsonPropertyName("function")] string Function,
    [property: JsonPropertyName("chunk_index")] int ChunkIndex);

/// <summary>
/// JSON payload written to S3 as <c>task-{index:0000}.result</c>.
/// </summary>
internal sealed record ResultPayload(
    [property: JsonPropertyName("results")] JsonElement?[] Results,
    [property: JsonPropertyName("errors")] string?[] Errors);

/// <summary>
/// Manifest written to S3 at <c>sessions/{sessionId}/manifest.json</c>.
/// </summary>
internal sealed record Manifest(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("tasks_total")] int TasksTotal,
    [property: JsonPropertyName("tasks_complete")] int TasksComplete,
    [property: JsonPropertyName("tasks_failed")] int TasksFailed,
    [property: JsonPropertyName("workers_active")] int WorkersActive,
    [property: JsonPropertyName("cost_actual")] double CostActual,
    [property: JsonPropertyName("cost_estimate_per_hour")] double CostEstimatePerHour,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("chunk_count")] int ChunkCount,
    [property: JsonPropertyName("task_count")] int TaskCount,
    [property: JsonPropertyName("workers_requested")] int WorkersRequested,
    [property: JsonPropertyName("workers_actual")] int WorkersActual,
    [property: JsonPropertyName("cpu")] int Cpu,
    [property: JsonPropertyName("memory_gb")] int MemoryGb,
    [property: JsonPropertyName("backend")] string Backend,
    [property: JsonPropertyName("spot")] bool Spot,
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("env_hash")] string EnvHash,
    [property: JsonPropertyName("library_version")] string LibraryVersion);

/// <summary>
/// S3 key helpers and session ID generation for the burst wire protocol.
/// All language libraries in the burst family use identical key paths and session ID formats.
/// </summary>
internal static class Protocol
{
    internal const string LibraryVersion = "0.1.0";
    internal const string Language = "csharp";

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // -------------------------------------------------------------------------
    // S3 key helpers
    // -------------------------------------------------------------------------

    /// <summary>Returns the S3 manifest key for a session.</summary>
    internal static string ManifestKey(string sessionId) =>
        $"sessions/{sessionId}/manifest.json";

    /// <summary>Returns the S3 key for a task input payload.</summary>
    internal static string TaskKey(string sessionId, int index) =>
        $"sessions/{sessionId}/tasks/task-{index:0000}.task";

    /// <summary>Returns the S3 key for a task result payload.</summary>
    internal static string ResultKey(string sessionId, int index) =>
        $"sessions/{sessionId}/tasks/task-{index:0000}.result";

    /// <summary>Returns the S3 key for a task status file.</summary>
    internal static string StatusKey(string sessionId, int index) =>
        $"sessions/{sessionId}/tasks/task-{index:0000}.status";

    /// <summary>Returns the S3 key for a task error file.</summary>
    internal static string ErrorKey(string sessionId, int index) =>
        $"sessions/{sessionId}/tasks/task-{index:0000}.error";

    // -------------------------------------------------------------------------
    // Task ID helpers
    // -------------------------------------------------------------------------

    /// <summary>Returns the canonical task ID string for the nth task (e.g., "task-0000").</summary>
    internal static string TaskId(int index) => $"task-{index:0000}";

    // -------------------------------------------------------------------------
    // Session ID generation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Generates a new unique session ID in the format <c>cs-{yyyyMMdd}-{random8hex}</c>.
    /// </summary>
    internal static string GenerateSessionId()
    {
        var date = DateTime.UtcNow.ToString("yyyyMMdd");
        var rand = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        return $"cs-{date}-{rand}";
    }

    // -------------------------------------------------------------------------
    // Cost estimation (simple Fargate pricing approximation)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Estimates cost per hour in USD for the given number of workers at the specified
    /// vCPU and memory configuration. Uses approximate Fargate on-demand pricing.
    /// </summary>
    internal static double EstimateCostPerHour(int cpu, int memoryGb, int workers)
    {
        // Approximate Fargate on-demand pricing (us-east-1, 2024):
        // vCPU: $0.04048/vCPU/hr, Memory: $0.004445/GB/hr
        const double cpuPricePerVcpuHr = 0.04048;
        const double memPricePerGbHr = 0.004445;
        return workers * (cpu * cpuPricePerVcpuHr + memoryGb * memPricePerGbHr);
    }
}
