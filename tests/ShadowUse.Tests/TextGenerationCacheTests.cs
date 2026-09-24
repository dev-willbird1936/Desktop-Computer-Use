// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Automation;
using Xunit;

namespace ShadowUse.Tests;

/// <summary>F24-R02/T02 pure cache-state coverage: unchanged generation_input reuses a cached
/// value; a changed generation_input is a distinct key (a miss), never a silent reuse of stale
/// prose. The live-generation half (does DCU ever make a model call) is deliberately out of
/// scope here -- no live model key exists in this environment -- and is covered instead by
/// native_evolution_regression.py's proof that DCU refuses to fabricate content.</summary>
public sealed class TextGenerationCacheTests
{
    [Fact]
    public void SameInputIsACacheHit()
    {
        var cache = new TextGenerationCache();
        var key = new TextGenerationKey("bio_field", TextGenerationCache.HashInput("write a short bio"));
        cache.Remember(key, "Once upon a time...");

        Assert.True(cache.TryGet(key, out var cached));
        Assert.Equal("Once upon a time...", cached.Text);
    }

    [Fact]
    public void ChangedInputIsATotallyDifferentKeyAndMisses()
    {
        var cache = new TextGenerationCache();
        var original = new TextGenerationKey("bio_field", TextGenerationCache.HashInput("write a short bio"));
        cache.Remember(original, "Once upon a time...");

        var changed = new TextGenerationKey("bio_field", TextGenerationCache.HashInput("write a long bio"));
        Assert.False(cache.TryGet(changed, out _));
    }

    [Fact]
    public void DifferentContextKeysWithSameInputAreDistinct()
    {
        var cache = new TextGenerationCache();
        var input = "write a short bio";
        var keyA = new TextGenerationKey("bio_field", TextGenerationCache.HashInput(input));
        var keyB = new TextGenerationKey("tagline_field", TextGenerationCache.HashInput(input));
        cache.Remember(keyA, "bio text");

        Assert.True(cache.TryGet(keyA, out _));
        Assert.False(cache.TryGet(keyB, out _));
    }

    [Fact]
    public void HashInputIsDeterministicAndCaseSensitive()
    {
        var h1 = TextGenerationCache.HashInput("same input");
        var h2 = TextGenerationCache.HashInput("same input");
        var h3 = TextGenerationCache.HashInput("Same Input");
        Assert.Equal(h1, h2);
        Assert.NotEqual(h1, h3);
    }

    [Fact]
    public void RememberOverwritesAnExistingEntryForTheSameKey()
    {
        var cache = new TextGenerationCache();
        var key = new TextGenerationKey("bio_field", TextGenerationCache.HashInput("write a short bio"));
        cache.Remember(key, "first attempt");
        cache.Remember(key, "second attempt");

        Assert.True(cache.TryGet(key, out var cached));
        Assert.Equal("second attempt", cached.Text);
    }
}
