using System.Text.Json;
using Tacet.Internal;
using Xunit;

namespace Tacet.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="FunctionRegistry"/>: registration, invocation, and error paths.
/// </summary>
public sealed class RegistryTests : IDisposable
{
    // Each test gets a clean registry state
    public RegistryTests() => FunctionRegistry.Clear();
    public void Dispose() => FunctionRegistry.Clear();

    // -------------------------------------------------------------------------
    // Registration
    // -------------------------------------------------------------------------

    [Fact]
    public void Register_StoresFunction()
    {
        FunctionRegistry.Register("double", (int x) => x * 2);
        Assert.True(FunctionRegistry.IsRegistered("double"));
    }

    [Fact]
    public void Register_OverwritesPreviousRegistration()
    {
        FunctionRegistry.Register("fn", (int x) => x + 1);
        FunctionRegistry.Register("fn", (int x) => x + 100);

        var item = JsonSerializer.SerializeToElement(5, Protocol.JsonOpts);
        var result = FunctionRegistry.Call("fn", item);
        Assert.Equal(105, result.GetInt32());
    }

    [Fact]
    public void Unregister_RemovesFunction()
    {
        FunctionRegistry.Register("temp", (string s) => s.ToUpper());
        Assert.True(FunctionRegistry.Unregister("temp"));
        Assert.False(FunctionRegistry.IsRegistered("temp"));
    }

    [Fact]
    public void Unregister_ReturnsFalse_WhenNotRegistered()
    {
        Assert.False(FunctionRegistry.Unregister("nonexistent"));
    }

    // -------------------------------------------------------------------------
    // Call — success
    // -------------------------------------------------------------------------

    [Fact]
    public void Call_InvokesRegisteredFunction_WithIntInput()
    {
        FunctionRegistry.Register("triple", (int x) => x * 3);

        var item = JsonSerializer.SerializeToElement(7, Protocol.JsonOpts);
        var result = FunctionRegistry.Call("triple", item);
        Assert.Equal(21, result.GetInt32());
    }

    [Fact]
    public void Call_InvokesRegisteredFunction_WithStringInput()
    {
        FunctionRegistry.Register("upper", (string s) => s.ToUpperInvariant());

        var item = JsonSerializer.SerializeToElement("hello", Protocol.JsonOpts);
        var result = FunctionRegistry.Call("upper", item);
        Assert.Equal("HELLO", result.GetString());
    }

    [Fact]
    public void Call_InvokesRegisteredFunction_WithComplexInput()
    {
        FunctionRegistry.Register("len", (List<int> lst) => lst.Count);

        var numbers = new List<int> { 1, 2, 3, 4, 5 };
        var item = JsonSerializer.SerializeToElement(numbers, Protocol.JsonOpts);
        var result = FunctionRegistry.Call("len", item);
        Assert.Equal(5, result.GetInt32());
    }

    // -------------------------------------------------------------------------
    // Call — error paths
    // -------------------------------------------------------------------------

    [Fact]
    public void Call_ThrowsTacetSetupException_WhenNotRegistered()
    {
        var item = JsonSerializer.SerializeToElement(1, Protocol.JsonOpts);
        var ex = Assert.Throws<TacetSetupException>(() => FunctionRegistry.Call("missing", item));
        Assert.Equal("call", ex.Step);
        Assert.Contains("missing", ex.Cause);
        Assert.Contains("Register", ex.Remediation);
    }

    [Fact]
    public void Call_ThrowsTacetSetupException_AfterUnregister()
    {
        FunctionRegistry.Register("ephemeral", (int x) => x);
        FunctionRegistry.Unregister("ephemeral");

        var item = JsonSerializer.SerializeToElement(1, Protocol.JsonOpts);
        Assert.Throws<TacetSetupException>(() => FunctionRegistry.Call("ephemeral", item));
    }

    // -------------------------------------------------------------------------
    // Thread safety
    // -------------------------------------------------------------------------

    [Fact]
    public void Register_IsThreadSafe()
    {
        const int n = 50;
        var threads = Enumerable.Range(0, n).Select(i =>
            new Thread(() => FunctionRegistry.Register($"fn_{i}", (int x) => x + i))
        ).ToList();

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        for (int i = 0; i < n; i++)
            Assert.True(FunctionRegistry.IsRegistered($"fn_{i}"));
    }

    // -------------------------------------------------------------------------
    // Public API — Tacet.Register
    // -------------------------------------------------------------------------

    [Fact]
    public void TacetRegister_DelegatesToRegistry()
    {
        Tacet.Register("square", (int x) => x * x);
        Assert.True(FunctionRegistry.IsRegistered("square"));
    }
}
