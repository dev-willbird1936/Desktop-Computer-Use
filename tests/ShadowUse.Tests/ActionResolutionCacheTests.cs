// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Automation;

namespace ShadowUse.Tests;

/// <summary>F31 coverage: the cache abstraction itself, independent of any adapter.</summary>
public sealed class ActionResolutionCacheTests
{
    private static ActionResolutionKey Key(string taskIntent = "submit", string schema = "schema-a") =>
        new("native", "v1", "app.exe", schema, taskIntent);

    [Fact]
    public void Remember_ThenTryGet_ReturnsTheStoredRecipe()
    {
        var cache = new ActionResolutionCache();
        var recipe = new ActionResolutionRecipe("id1", "Button", "Sign in", null, DateTimeOffset.UtcNow);

        cache.Remember(Key(), recipe);

        Assert.True(cache.TryGet(Key(), out var found));
        Assert.Equal(recipe, found);
    }

    [Fact]
    public void TryGet_ReturnsFalse_WhenNothingRemembered()
    {
        var cache = new ActionResolutionCache();
        Assert.False(cache.TryGet(Key(), out _));
    }

    [Fact]
    public void Invalidate_RemovesTheEntry()
    {
        var cache = new ActionResolutionCache();
        cache.Remember(Key(), new ActionResolutionRecipe(null, "Button", "Sign in", null, DateTimeOffset.UtcNow));

        cache.Invalidate(Key());

        Assert.False(cache.TryGet(Key(), out _));
    }

    [Fact]
    public void DifferentTaskIntent_IsADifferentKey_EvenWithinTheSameAppAndSchema()
    {
        var cache = new ActionResolutionCache();
        cache.Remember(Key(taskIntent: "submit"), new ActionResolutionRecipe(null, "Button", "Submit", null, DateTimeOffset.UtcNow));

        Assert.False(cache.TryGet(Key(taskIntent: "cancel"), out _));
    }

    [Fact]
    public void DifferentSchema_IsADifferentKey_SoAStructuralChangeMissesRatherThanReusingAStaleRecipe()
    {
        var cache = new ActionResolutionCache();
        cache.Remember(Key(schema: "schema-a"), new ActionResolutionRecipe(null, "Button", "Submit", null, DateTimeOffset.UtcNow));

        // F31-R02: on-disk/browser structure changing (a new schema fingerprint) must not
        // silently reuse a recipe computed against the old structure.
        Assert.False(cache.TryGet(Key(schema: "schema-b"), out _));
    }

    [Fact]
    public void ComputeNativeSchema_IsOrderIndependent()
    {
        var a = ActionResolutionCache.ComputeNativeSchema([("id1", "Button"), ("id2", "Edit")]);
        var b = ActionResolutionCache.ComputeNativeSchema([("id2", "Edit"), ("id1", "Button")]);

        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeNativeSchema_ChangesWhenElementSetChanges()
    {
        var before = ActionResolutionCache.ComputeNativeSchema([("id1", "Button"), ("id2", "Edit")]);
        var afterAdd = ActionResolutionCache.ComputeNativeSchema([("id1", "Button"), ("id2", "Edit"), ("id3", "CheckBox")]);
        var afterRetype = ActionResolutionCache.ComputeNativeSchema([("id1", "Button"), ("id2", "ComboBox")]);

        Assert.NotEqual(before, afterAdd);
        Assert.NotEqual(before, afterRetype);
    }

    [Fact]
    public void ComputeBrowserSchema_IsOrderIndependentAndChangesWithCandidates()
    {
        var a = ActionResolutionCache.ComputeBrowserSchema([("button", "#save"), ("textbox", "#name")]);
        var b = ActionResolutionCache.ComputeBrowserSchema([("textbox", "#name"), ("button", "#save")]);
        var changed = ActionResolutionCache.ComputeBrowserSchema([("textbox", "#name")]);

        Assert.Equal(a, b);
        Assert.NotEqual(a, changed);
    }

    [Fact]
    public void RememberOverwritesAPriorRecipeForTheSameKey()
    {
        var cache = new ActionResolutionCache();
        var key = Key();
        cache.Remember(key, new ActionResolutionRecipe("old", "Button", "Old", null, DateTimeOffset.UtcNow));
        cache.Remember(key, new ActionResolutionRecipe("new", "Button", "New", null, DateTimeOffset.UtcNow));

        Assert.True(cache.TryGet(key, out var found));
        Assert.Equal("new", found.AutomationId);
    }
}
