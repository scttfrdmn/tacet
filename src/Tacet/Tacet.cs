using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Tacet.Internal;

namespace Tacet;

/// <summary>
/// Cloud bursting for .NET — distribute CPU-intensive work across AWS ECS/Fargate workers.
///
/// <para>Quickstart:</para>
/// <code>
/// // 1. Register your function (same process, same binary)
/// Tacet.Register("process", (MyInput x) => Process(x));
///
/// // 2. In main(), check if this invocation is a worker
/// if (Tacet.IsWorker)
///     Environment.Exit(await Tacet.RunWorkerAsync());
///
/// // 3. Distribute work
/// var results = await Tacet.MapAsync(items, (MyInput x) => Task.FromResult(Process(x)));
/// </code>
/// </summary>
public static class Tacet
{
    // -------------------------------------------------------------------------
    // Worker API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns <c>true</c> when the current process is running inside an ECS worker container
    /// (i.e., the environment variable <c>BURST_WORKER=1</c> is set).
    ///
    /// <para>Add this check as the first line of <c>main()</c>:</para>
    /// <code>
    /// if (Tacet.IsWorker)
    ///     Environment.Exit(await Tacet.RunWorkerAsync());
    /// </code>
    /// </summary>
    public static bool IsWorker => Worker.IsWorker;

    /// <summary>
    /// Executes the worker loop: downloads the task from S3, runs the registered function
    /// on each item, and uploads results. Should be called when <see cref="IsWorker"/> is true.
    /// </summary>
    /// <returns>Exit code: 0 on success, 1 on failure.</returns>
    public static Task<int> RunWorkerAsync() => Worker.RunAsync();

    // -------------------------------------------------------------------------
    // Function registration
    // -------------------------------------------------------------------------

    /// <summary>
    /// Registers a function under <paramref name="name"/> so it can be invoked by workers.
    /// Must be called before <see cref="Map{T,U}"/>, <see cref="MapAsync{T,U}"/>, etc.
    ///
    /// <para>Registration is global and thread-safe. Registering the same name twice
    /// overwrites the previous registration.</para>
    /// </summary>
    /// <typeparam name="T">Input type. Must be JSON-deserializable.</typeparam>
    /// <typeparam name="U">Output type. Must be JSON-serializable.</typeparam>
    /// <param name="name">The name workers will use to locate this function.</param>
    /// <param name="fn">The function to register.</param>
    public static void Register<T, U>(string name, Func<T, U> fn)
        where T : notnull
        where U : notnull
        => FunctionRegistry.Register(name, fn);

    // -------------------------------------------------------------------------
    // Map — synchronous
    // -------------------------------------------------------------------------

    /// <summary>
    /// Distributes <paramref name="items"/> synchronously across ECS workers,
    /// returning results in the same order as the input.
    ///
    /// <para>This is a blocking wrapper around <see cref="MapAsync{T,U}"/>.</para>
    /// </summary>
    /// <typeparam name="T">Input item type. Must be JSON-serializable.</typeparam>
    /// <typeparam name="U">Output result type. Must be JSON-serializable.</typeparam>
    /// <param name="items">Items to process.</param>
    /// <param name="fn">
    /// The local function reference. Used only to resolve the function name via
    /// <see cref="Register{T,U}"/>; actual execution happens in worker containers.
    /// </param>
    /// <param name="opts">Optional burst configuration overrides.</param>
    /// <returns>Results in original item order.</returns>
    /// <exception cref="TacetPartialException">Some tasks failed.</exception>
    /// <exception cref="TacetCostLimitException">Estimated cost exceeds <see cref="MapOptions.MaxCostUsd"/>.</exception>
    /// <exception cref="TacetSetupException">AWS resource setup or config error.</exception>
    public static List<U> Map<T, U>(IEnumerable<T> items, Func<T, U> fn, MapOptions? opts = null)
        where T : notnull where U : notnull
        => MapAsync(items, x => Task.FromResult(fn(x)), opts).GetAwaiter().GetResult();

    // -------------------------------------------------------------------------
    // MapAsync — async/await
    // -------------------------------------------------------------------------

    /// <summary>
    /// Distributes <paramref name="items"/> asynchronously across ECS workers,
    /// returning results in the same order as the input.
    ///
    /// <para>
    /// <paramref name="fn"/> must have been registered with <see cref="Register{T,U}"/>
    /// using the same method name. Pass a named method (not a lambda) so the name can be
    /// resolved automatically, or use the overload that accepts an explicit function name.
    /// </para>
    /// </summary>
    /// <typeparam name="T">Input item type. Must be JSON-serializable.</typeparam>
    /// <typeparam name="U">Output result type. Must be JSON-serializable.</typeparam>
    /// <param name="items">Items to process.</param>
    /// <param name="fn">
    /// The local async function reference. Used to derive the function name for dispatch.
    /// The function must have been registered with <see cref="Register{T,U}"/>.
    /// </param>
    /// <param name="opts">Optional burst configuration overrides.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Results in original item order.</returns>
    /// <exception cref="TacetPartialException">Some tasks failed.</exception>
    /// <exception cref="TacetCostLimitException">Estimated cost exceeds <see cref="MapOptions.MaxCostUsd"/>.</exception>
    /// <exception cref="TacetSetupException">AWS resource setup or config error.</exception>
    public static async Task<List<U>> MapAsync<T, U>(
        IEnumerable<T> items,
        Func<T, Task<U>> fn,
        MapOptions? opts = null,
        CancellationToken ct = default)
        where T : notnull where U : notnull
    {
        var fnName = ResolveFunctionName(fn);
        return await Session.RunAsync<T, U>(items, fnName, opts, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Distributes <paramref name="items"/> asynchronously across ECS workers,
    /// using an explicit <paramref name="functionName"/> to identify the registered function.
    /// Use this overload when passing a lambda or when the method name doesn't match the
    /// registration name.
    /// </summary>
    /// <typeparam name="T">Input item type. Must be JSON-serializable.</typeparam>
    /// <typeparam name="U">Output result type. Must be JSON-serializable.</typeparam>
    /// <param name="items">Items to process.</param>
    /// <param name="functionName">The name used in <see cref="Register{T,U}"/>.</param>
    /// <param name="opts">Optional burst configuration overrides.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Results in original item order.</returns>
    public static async Task<List<U>> MapAsync<T, U>(
        IEnumerable<T> items,
        string functionName,
        MapOptions? opts = null,
        CancellationToken ct = default)
        where T : notnull where U : notnull
    {
        if (!FunctionRegistry.IsRegistered(functionName))
            throw new TacetSetupException(
                "resolve function",
                $"no function registered under name '{functionName}'",
                $"call Tacet.Register(\"{functionName}\", fn) before calling MapAsync()");

        return await Session.RunAsync<T, U>(items, functionName, opts, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // MapStreamAsync — IAsyncEnumerable streaming
    // -------------------------------------------------------------------------

    /// <summary>
    /// Distributes <paramref name="items"/> across ECS workers and yields results
    /// as individual chunks complete, rather than waiting for all chunks to finish.
    ///
    /// <para>Results within a chunk are emitted in order, but chunks themselves
    /// may arrive out of order.</para>
    /// </summary>
    /// <typeparam name="T">Input item type. Must be JSON-serializable.</typeparam>
    /// <typeparam name="U">Output result type. Must be JSON-serializable.</typeparam>
    /// <param name="items">Items to process.</param>
    /// <param name="fn">The async function; must have been registered with <see cref="Register{T,U}"/>.</param>
    /// <param name="opts">Optional burst configuration overrides.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async IAsyncEnumerable<U> MapStreamAsync<T, U>(
        IEnumerable<T> items,
        Func<T, Task<U>> fn,
        MapOptions? opts = null,
        [EnumeratorCancellation] CancellationToken ct = default)
        where T : notnull where U : notnull
    {
        var fnName = ResolveFunctionName(fn);
        await foreach (var result in Session.StreamAsync<T, U>(items, fnName, opts, ct)
                           .ConfigureAwait(false))
        {
            yield return result;
        }
    }

    // -------------------------------------------------------------------------
    // MapChannel — System.Threading.Channels
    // -------------------------------------------------------------------------

    /// <summary>
    /// Distributes <paramref name="items"/> across ECS workers and returns a
    /// <see cref="ChannelReader{T}"/> that produces results as chunks complete.
    ///
    /// <para>The returned channel is bounded by the total number of results and will
    /// be completed (successfully or with an error) when all chunks finish.</para>
    /// </summary>
    /// <typeparam name="T">Input item type. Must be JSON-serializable.</typeparam>
    /// <typeparam name="U">Output result type. Must be JSON-serializable.</typeparam>
    /// <param name="items">Items to process.</param>
    /// <param name="fn">The async function; must have been registered with <see cref="Register{T,U}"/>.</param>
    /// <param name="opts">Optional burst configuration overrides.</param>
    /// <returns>A channel reader that yields results as workers complete.</returns>
    public static ChannelReader<U> MapChannel<T, U>(
        IEnumerable<T> items,
        Func<T, Task<U>> fn,
        MapOptions? opts = null)
        where T : notnull where U : notnull
    {
        var channel = Channel.CreateUnbounded<U>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = false,
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var result in MapStreamAsync(items, fn, opts).ConfigureAwait(false))
                    await channel.Writer.WriteAsync(result).ConfigureAwait(false);
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
            }
        });

        return channel.Reader;
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Resolves the registered name for a delegate by searching the function registry.
    /// If the delegate itself was used as a key during <see cref="Register{T,U}"/>,
    /// we use the method name as a fallback convention.
    /// </summary>
    private static string ResolveFunctionName<T, U>(Func<T, Task<U>> fn)
        where T : notnull where U : notnull
    {
        // Convention: use the method name of the delegate's target method.
        // Users should Register("methodName", fn) where methodName matches the method name.
        var methodName = fn.Method.Name;

        // Strip async state machine wrappers (e.g. "<ProcessAsync>b__0")
        if (methodName.StartsWith('<') && methodName.Contains('>'))
        {
            var start = 1;
            var end = methodName.IndexOf('>');
            methodName = methodName[start..end];
        }

        if (!FunctionRegistry.IsRegistered(methodName))
            throw new TacetSetupException(
                "resolve function",
                $"no function registered under name '{methodName}'",
                $"call Tacet.Register(\"{methodName}\", fn) before calling Map()");

        return methodName;
    }
}
