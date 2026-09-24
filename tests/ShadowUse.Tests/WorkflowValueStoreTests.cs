// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Automation;

namespace ShadowUse.Tests;

public sealed class WorkflowValueStoreTests
{
    [Fact]
    public void SetThenGet_ReturnsStoredValue()
    {
        var store = new WorkflowValueStore();
        store.Set("account-id", "12345");

        Assert.True(store.TryGet("account-id", out var value));
        Assert.Equal("12345", value);
    }

    [Fact]
    public void Set_RejectsEmptyName()
    {
        var store = new WorkflowValueStore();
        Assert.Throws<ArgumentException>(() => store.Set("", "x"));
        Assert.Throws<ArgumentException>(() => store.Set("   ", "x"));
    }

    [Fact]
    public void Delete_RemovesValue()
    {
        var store = new WorkflowValueStore();
        store.Set("name", "value");

        Assert.True(store.Delete("name"));
        Assert.False(store.TryGet("name", out _));
        Assert.False(store.Delete("name"));
    }

    [Fact]
    public void Resolve_PrefersLiteral()
    {
        var store = new WorkflowValueStore();
        store.Set("ref", "from-ref");

        var resolved = WorkflowValueStore.Resolve("literal", null, null, store);

        Assert.Equal("literal", resolved);
    }

    [Fact]
    public void Resolve_ReadsFromNamedStore()
    {
        var store = new WorkflowValueStore();
        store.Set("ref", "from-ref");

        var resolved = WorkflowValueStore.Resolve(null, "ref", null, store);

        Assert.Equal("from-ref", resolved);
    }

    [Fact]
    public void Resolve_ReadsFromEnvironmentVariable()
    {
        var store = new WorkflowValueStore();
        var name = "DCU_TEST_ENV_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "from-env");
        try
        {
            var resolved = WorkflowValueStore.Resolve(null, null, name, store);
            Assert.Equal("from-env", resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void Resolve_ThrowsWhenNoSourceSupplied()
    {
        var store = new WorkflowValueStore();
        Assert.Throws<InvalidOperationException>(() => WorkflowValueStore.Resolve(null, null, null, store));
    }

    [Fact]
    public void Resolve_ThrowsWhenMultipleSourcesSupplied()
    {
        var store = new WorkflowValueStore();
        store.Set("ref", "from-ref");
        Assert.Throws<InvalidOperationException>(() => WorkflowValueStore.Resolve("literal", "ref", null, store));
    }

    [Fact]
    public void Resolve_ThrowsForUnknownReference()
    {
        var store = new WorkflowValueStore();
        Assert.Throws<InvalidOperationException>(() => WorkflowValueStore.Resolve(null, "missing", null, store));
    }

    [Fact]
    public void Resolve_ThrowsForUnsetEnvironmentVariable()
    {
        var store = new WorkflowValueStore();
        var name = "DCU_TEST_ENV_MISSING_" + Guid.NewGuid().ToString("N");
        Assert.Throws<InvalidOperationException>(() => WorkflowValueStore.Resolve(null, null, name, store));
    }
}
