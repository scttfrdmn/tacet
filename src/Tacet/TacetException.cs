using System.Text.Json;

namespace Tacet;

/// <summary>
/// Base class for all Tacet exceptions.
/// </summary>
public abstract class TacetException : Exception
{
    /// <inheritdoc/>
    protected TacetException(string message) : base(message) { }

    /// <inheritdoc/>
    protected TacetException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when some tasks complete successfully and some fail.
/// <see cref="Results"/> contains null entries where tasks failed;
/// <see cref="Errors"/> contains null entries where tasks succeeded.
/// </summary>
public sealed class TacetPartialException : TacetException
{
    /// <summary>Results in original item order; null where the corresponding task failed.</summary>
    public IReadOnlyList<JsonElement?> Results { get; }

    /// <summary>Error messages in original item order; null where the corresponding task succeeded.</summary>
    public IReadOnlyList<string?> Errors { get; }

    /// <summary>Number of tasks that failed.</summary>
    public int FailedCount { get; }

    /// <summary>Number of tasks that succeeded.</summary>
    public int SuccessCount { get; }

    /// <summary>Creates a new <see cref="TacetPartialException"/>.</summary>
    public TacetPartialException(
        IReadOnlyList<JsonElement?> results,
        IReadOnlyList<string?> errors,
        int failedCount,
        int successCount)
        : base($"tacet: {failedCount}/{failedCount + successCount} tasks failed")
    {
        Results = results;
        Errors = errors;
        FailedCount = failedCount;
        SuccessCount = successCount;
    }
}

/// <summary>
/// Thrown when the estimated job cost exceeds the configured <see cref="MapOptions.MaxCostUsd"/> limit.
/// No workers are launched.
/// </summary>
public sealed class TacetCostLimitException : TacetException
{
    /// <summary>The configured cost limit in USD.</summary>
    public double Limit { get; }

    /// <summary>The estimated cost per hour in USD.</summary>
    public double EstimatedCost { get; }

    /// <summary>Creates a new <see cref="TacetCostLimitException"/>.</summary>
    public TacetCostLimitException(double limit, double estimatedCost)
        : base($"tacet: estimated cost ${estimatedCost:F2}/hr exceeds limit ${limit:F2}")
    {
        Limit = limit;
        EstimatedCost = estimatedCost;
    }
}

/// <summary>
/// Thrown when AWS quota prevents launching the requested number of workers.
/// The job continues with a reduced worker count.
/// </summary>
public sealed class TacetQuotaException : TacetException
{
    /// <summary>The number of workers originally requested.</summary>
    public int RequestedWorkers { get; }

    /// <summary>The actual number of workers launched after quota enforcement.</summary>
    public int ActualWorkers { get; }

    /// <summary>Creates a new <see cref="TacetQuotaException"/>.</summary>
    public TacetQuotaException(int requested, int actual)
        : base($"tacet: quota limited to {actual} workers (requested {requested})")
    {
        RequestedWorkers = requested;
        ActualWorkers = actual;
    }
}

/// <summary>
/// Thrown when a context cancellation or timeout occurs while waiting for workers.
/// The session remains in S3 and can be inspected later.
/// </summary>
public sealed class TacetTimeoutException : TacetException
{
    /// <summary>The session ID that timed out.</summary>
    public string SessionId { get; }

    /// <summary>Creates a new <see cref="TacetTimeoutException"/>.</summary>
    public TacetTimeoutException(string sessionId)
        : base($"tacet: session {sessionId} timed out")
    {
        SessionId = sessionId;
    }
}

/// <summary>
/// Thrown when AWS resource provisioning fails or the burst configuration is missing or invalid.
/// Check <see cref="Step"/>, <see cref="Cause"/>, and <see cref="Remediation"/> for guidance.
/// </summary>
public sealed class TacetSetupException : TacetException
{
    /// <summary>The setup step that failed (e.g., "load config", "register task definition").</summary>
    public string Step { get; }

    /// <summary>A description of what went wrong.</summary>
    public string Cause { get; }

    /// <summary>A human-readable suggestion for how to fix the problem.</summary>
    public string Remediation { get; }

    /// <summary>Creates a new <see cref="TacetSetupException"/>.</summary>
    public TacetSetupException(string step, string cause, string remediation)
        : base($"tacet setup failed at \"{step}\": {cause} — {remediation}")
    {
        Step = step;
        Cause = cause;
        Remediation = remediation;
    }
}
