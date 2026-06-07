using System.Collections.Concurrent;
using System.Text.Json;

namespace Tacet.Internal;

/// <summary>
/// Thread-safe registry that maps function names to typed invocable delegates.
/// All registered functions accept and return <see cref="JsonElement"/> for
/// wire-protocol interoperability.
/// </summary>
internal static class FunctionRegistry
{
    private static readonly ConcurrentDictionary<string, Func<JsonElement, JsonElement>> _registry = new();

    /// <summary>
    /// Registers <paramref name="fn"/> under <paramref name="name"/>.
    /// Overwrites any existing registration with the same name.
    /// </summary>
    internal static void Register<T, U>(string name, Func<T, U> fn)
        where T : notnull
        where U : notnull
    {
        _registry[name] = item =>
        {
            var input = item.Deserialize<T>(Protocol.JsonOpts)
                ?? throw new InvalidOperationException($"failed to deserialize item as {typeof(T).Name}");
            var output = fn(input);
            return JsonSerializer.SerializeToElement(output, Protocol.JsonOpts);
        };
    }

    /// <summary>
    /// Invokes the function registered under <paramref name="name"/> with <paramref name="item"/>.
    /// </summary>
    /// <exception cref="TacetSetupException">Thrown if no function is registered under <paramref name="name"/>.</exception>
    internal static JsonElement Call(string name, JsonElement item)
    {
        if (!_registry.TryGetValue(name, out var fn))
            throw new TacetSetupException(
                "call",
                $"function '{name}' not registered",
                "call Tacet.Register() before Map()");
        return fn(item);
    }

    /// <summary>
    /// Returns true if a function is registered under <paramref name="name"/>.
    /// </summary>
    internal static bool IsRegistered(string name) => _registry.ContainsKey(name);

    /// <summary>
    /// Removes the registration for <paramref name="name"/>. Returns true if it was present.
    /// </summary>
    internal static bool Unregister(string name) => _registry.TryRemove(name, out _);

    /// <summary>
    /// Clears all registrations. Primarily used in tests.
    /// </summary>
    internal static void Clear() => _registry.Clear();
}
