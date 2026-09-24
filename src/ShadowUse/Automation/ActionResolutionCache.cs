// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ShadowUse.Automation;

/// <summary>
/// Cache key for a resolved action recipe (F31). Every dimension the spec requires
/// (adapter/version, page/app identity, structural schema, task intent) is part of
/// the key itself, so a change to any of them is a natural cache miss rather than
/// something invalidation logic has to detect after the fact.
/// </summary>
public readonly record struct ActionResolutionKey(
    string Adapter,
    string AdapterVersion,
    string TargetIdentity,
    string Schema,
    string TaskIntent);

/// <summary>
/// A cached recipe for finding a target again: stable identifying attributes, never a
/// live element id and never a claim that the prior action succeeded. A cache hit is
/// only ever "worth trying"; it is always re-resolved against the current UI/DOM before
/// use, and removed on a miss.
/// </summary>
public sealed record ActionResolutionRecipe(
    string? AutomationId,
    string? ControlType,
    string? Name,
    string? Selector,
    DateTimeOffset CachedAt);

/// <summary>
/// Process-lifetime cache of resolved-action recipes (F31), shared by the native and
/// browser tool surfaces. Stores what to look for, not a trusted result: every read
/// still requires the caller to re-resolve against a fresh observation.
/// </summary>
public sealed class ActionResolutionCache
{
    private readonly ConcurrentDictionary<ActionResolutionKey, ActionResolutionRecipe> _entries = new();

    public void Remember(ActionResolutionKey key, ActionResolutionRecipe recipe) => _entries[key] = recipe;

    public bool TryGet(ActionResolutionKey key, out ActionResolutionRecipe recipe) => _entries.TryGetValue(key, out recipe!);

    public void Invalidate(ActionResolutionKey key) => _entries.TryRemove(key, out _);

    public int Count => _entries.Count;

    /// <summary>Order-independent structural fingerprint of an app's actionable elements.
    /// Changes whenever elements are added/removed/retyped, naturally invalidating any
    /// cache entry keyed against the prior schema (F31-R02).</summary>
    public static string ComputeNativeSchema(IEnumerable<(string AutomationId, string ControlType)> elements)
        => Hash(string.Join('\n', elements
            .Select(e => $"{e.AutomationId}|{e.ControlType}")
            .OrderBy(s => s, StringComparer.Ordinal)));

    /// <summary>Order-independent structural fingerprint of a page's real candidates.</summary>
    public static string ComputeBrowserSchema(IEnumerable<(string Role, string Selector)> candidates)
        => Hash(string.Join('\n', candidates
            .Select(c => $"{c.Role}|{c.Selector}")
            .OrderBy(s => s, StringComparer.Ordinal)));

    private static string Hash(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16];
}
