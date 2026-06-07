namespace Tacet;

/// <summary>
/// A reusable burst pool that amortizes configuration loading across multiple
/// <see cref="Map{T,U}"/> and <see cref="MapAsync{T,U}"/> calls.
///
/// Create a pool once with your shared options, then reuse it for every job:
/// <code>
/// var pool = new TacetPool(new MapOptions { Workers = 8, Cpu = 2 });
/// var r1 = await pool.MapAsync(batch1, "process");
/// var r2 = await pool.MapAsync(batch2, "process");
/// </code>
/// </summary>
public sealed class TacetPool : IDisposable
{
    private readonly MapOptions _opts;
    private bool _disposed;

    /// <summary>
    /// Creates a new <see cref="TacetPool"/> with the given default options.
    /// </summary>
    /// <param name="opts">
    /// Options applied as defaults for all jobs run through this pool.
    /// Per-call options passed to <see cref="Map{T,U}"/> or <see cref="MapAsync{T,U}"/> take precedence.
    /// </param>
    public TacetPool(MapOptions? opts = null)
    {
        _opts = opts ?? new MapOptions();
    }

    /// <summary>
    /// Distributes <paramref name="items"/> synchronously across ECS workers,
    /// applying the pool's default options merged with <paramref name="callOpts"/>.
    /// </summary>
    /// <typeparam name="T">Input item type. Must be JSON-serializable.</typeparam>
    /// <typeparam name="U">Output result type. Must be JSON-serializable.</typeparam>
    /// <param name="items">The items to process.</param>
    /// <param name="fn">The local function; must have been registered with <see cref="Tacet.Register{T,U}"/>.</param>
    /// <param name="callOpts">Per-call option overrides.</param>
    /// <returns>Results in the same order as <paramref name="items"/>.</returns>
    public List<U> Map<T, U>(IEnumerable<T> items, Func<T, U> fn, MapOptions? callOpts = null)
        where T : notnull where U : notnull
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Tacet.Map(items, fn, Merge(callOpts));
    }

    /// <summary>
    /// Distributes <paramref name="items"/> asynchronously across ECS workers,
    /// applying the pool's default options merged with <paramref name="callOpts"/>.
    /// </summary>
    /// <typeparam name="T">Input item type. Must be JSON-serializable.</typeparam>
    /// <typeparam name="U">Output result type. Must be JSON-serializable.</typeparam>
    /// <param name="items">The items to process.</param>
    /// <param name="fn">The local function; must have been registered with <see cref="Tacet.Register{T,U}"/>.</param>
    /// <param name="callOpts">Per-call option overrides.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Results in the same order as <paramref name="items"/>.</returns>
    public Task<List<U>> MapAsync<T, U>(
        IEnumerable<T> items,
        Func<T, Task<U>> fn,
        MapOptions? callOpts = null,
        CancellationToken ct = default)
        where T : notnull where U : notnull
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Tacet.MapAsync(items, fn, Merge(callOpts), ct);
    }

    /// <summary>
    /// Releases resources held by this pool. Subsequent calls to <see cref="Map{T,U}"/>
    /// or <see cref="MapAsync{T,U}"/> will throw <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose() => _disposed = true;

    private MapOptions Merge(MapOptions? callOpts)
    {
        if (callOpts is null) return _opts;
        return new MapOptions
        {
            Workers      = callOpts.Workers      ?? _opts.Workers,
            Cpu          = callOpts.Cpu          ?? _opts.Cpu,
            MemoryGb     = callOpts.MemoryGb     ?? _opts.MemoryGb,
            Backend      = callOpts.Backend      ?? _opts.Backend,
            Spot         = callOpts.Spot         ?? _opts.Spot,
            MaxCostUsd   = callOpts.MaxCostUsd   ?? _opts.MaxCostUsd,
            CostAlertUsd = callOpts.CostAlertUsd ?? _opts.CostAlertUsd,
            Region       = callOpts.Region       ?? _opts.Region,
            ImageUri     = callOpts.ImageUri     ?? _opts.ImageUri,
            Arch         = callOpts.Arch ?? _opts.Arch,
        };
    }
}
