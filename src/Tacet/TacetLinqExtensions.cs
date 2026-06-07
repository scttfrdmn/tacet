namespace Tacet;

/// <summary>
/// LINQ-style extension methods for cloud bursting collections.
/// </summary>
public static class TacetLinqExtensions
{
    /// <summary>
    /// Distributes the source sequence across ECS workers using the burst protocol.
    /// Equivalent to calling <see cref="Tacet.Map{T,U}"/> with the sequence as items.
    ///
    /// <para>Usage:</para>
    /// <code>
    /// Tacet.Register("double", (int x) => x * 2);
    /// var results = myList.SelectBurst((int x) => x * 2);
    /// </code>
    /// </summary>
    /// <typeparam name="T">Input element type. Must be JSON-serializable.</typeparam>
    /// <typeparam name="U">Output element type. Must be JSON-serializable.</typeparam>
    /// <param name="source">The source collection.</param>
    /// <param name="fn">The transformation function; must have been registered with <see cref="Tacet.Register{T,U}"/>.</param>
    /// <param name="opts">Optional burst options.</param>
    /// <returns>Results in the same order as <paramref name="source"/>.</returns>
    public static List<U> SelectBurst<T, U>(
        this IEnumerable<T> source,
        Func<T, U> fn,
        MapOptions? opts = null)
        where T : notnull where U : notnull
        => Tacet.Map(source, fn, opts);

    /// <summary>
    /// Distributes the source sequence across ECS workers asynchronously.
    /// Equivalent to calling <see cref="Tacet.MapAsync{T,U}"/> with the sequence as items.
    ///
    /// <para>Usage:</para>
    /// <code>
    /// Tacet.Register("process", (MyItem x) => Process(x));
    /// var results = await myList.SelectBurstAsync((MyItem x) => Task.FromResult(Process(x)));
    /// </code>
    /// </summary>
    /// <typeparam name="T">Input element type. Must be JSON-serializable.</typeparam>
    /// <typeparam name="U">Output element type. Must be JSON-serializable.</typeparam>
    /// <param name="source">The source collection.</param>
    /// <param name="fn">The async transformation function; must have been registered with <see cref="Tacet.Register{T,U}"/>.</param>
    /// <param name="opts">Optional burst options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Results in the same order as <paramref name="source"/>.</returns>
    public static Task<List<U>> SelectBurstAsync<T, U>(
        this IEnumerable<T> source,
        Func<T, Task<U>> fn,
        MapOptions? opts = null,
        CancellationToken ct = default)
        where T : notnull where U : notnull
        => Tacet.MapAsync(source, fn, opts, ct);

    /// <summary>
    /// Wraps a source sequence for cloud bursting. Returns an <see cref="AsyncBurstQuery{T}"/>
    /// that supports LINQ-style chaining before launching the burst job.
    ///
    /// <para>Usage:</para>
    /// <code>
    /// var results = await myList
    ///     .AsParallelBurst()
    ///     .WithOptions(new MapOptions { Workers = 16 })
    ///     .Select((int x) => x * 2)
    ///     .ToListAsync();
    /// </code>
    /// </summary>
    /// <typeparam name="T">Element type. Must be JSON-serializable.</typeparam>
    /// <param name="source">The source collection.</param>
    /// <param name="opts">Optional burst options.</param>
    /// <returns>An <see cref="AsyncBurstQuery{T}"/> that can be further configured.</returns>
    public static AsyncBurstQuery<T> AsParallelBurst<T>(
        this IEnumerable<T> source,
        MapOptions? opts = null)
        where T : notnull
        => new(source.ToList(), opts);
}

/// <summary>
/// A fluent builder for a burst job. Created via <see cref="TacetLinqExtensions.AsParallelBurst{T}"/>.
/// </summary>
/// <typeparam name="T">Source element type.</typeparam>
public sealed class AsyncBurstQuery<T> where T : notnull
{
    private readonly List<T> _items;
    private MapOptions _opts;

    internal AsyncBurstQuery(List<T> items, MapOptions? opts)
    {
        _items = items;
        _opts  = opts ?? new MapOptions();
    }

    /// <summary>Sets or replaces the burst options for this query.</summary>
    public AsyncBurstQuery<T> WithOptions(MapOptions opts)
    {
        _opts = opts;
        return this;
    }

    /// <summary>
    /// Applies <paramref name="fn"/> to each element on ECS workers and returns
    /// an awaitable task of the result list.
    /// </summary>
    /// <typeparam name="U">Output element type. Must be JSON-serializable.</typeparam>
    /// <param name="fn">Synchronous transformation function.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<List<U>> Select<U>(Func<T, U> fn, CancellationToken ct = default)
        where U : notnull
        => Tacet.MapAsync(_items, x => Task.FromResult(fn(x)), _opts, ct);

    /// <summary>
    /// Applies async <paramref name="fn"/> to each element on ECS workers and returns
    /// an awaitable task of the result list.
    /// </summary>
    /// <typeparam name="U">Output element type. Must be JSON-serializable.</typeparam>
    /// <param name="fn">Async transformation function.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<List<U>> SelectAsync<U>(Func<T, Task<U>> fn, CancellationToken ct = default)
        where U : notnull
        => Tacet.MapAsync(_items, fn, _opts, ct);
}
