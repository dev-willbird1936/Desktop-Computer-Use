// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ShadowUse.Automation;

public sealed record TextGenerationKey(string ContextKey, string InputHash);
public sealed record CachedGeneration(string Text, DateTimeOffset CachedAt);

/// <summary>F24-R02/T02: caches an externally-supplied generated value keyed by (context_key,
/// hash-of-generation-input), so the SAME input reuses the SAME text across stale-observation
/// retries, while a changed input is simply a different key -- a miss that requires fresh
/// generated_text, never a silent reuse of stale prose under a new context. Pure storage: DCU
/// never generates text itself (no live model key is configured anywhere in this environment,
/// same documented constraint as F27/F46/F47/F55) -- the caller supplies generated_text from
/// its own configured generator, and this cache is only responsible for not re-requesting it
/// needlessly.</summary>
public sealed class TextGenerationCache
{
    private readonly ConcurrentDictionary<TextGenerationKey, CachedGeneration> _entries = new();

    public static string HashInput(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16];

    public bool TryGet(TextGenerationKey key, out CachedGeneration cached) => _entries.TryGetValue(key, out cached!);

    public void Remember(TextGenerationKey key, string text) => _entries[key] = new CachedGeneration(text, DateTimeOffset.UtcNow);
}
