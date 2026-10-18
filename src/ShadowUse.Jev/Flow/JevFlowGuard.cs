// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Text.RegularExpressions;

namespace ShadowUse.Jev.Flow;

/// <summary>
/// Static safety filters for learned or discovered actions. Learned menus are executable by the flow,
/// so anything that could spend money, destroy data, or leave the game is excluded before Jev can choose it.
/// The earlier live BTD2 run clicked a control that opened a paid-membership offer; this guard exists for that class.
/// In-world placement/aiming/dragging is excluded too: a live check showed a key-selected tower ghost landing on the wrong half
/// ("Wrong Side!"), which needs a fresh-frame placement validation this menu loop does not perform.
/// </summary>
public static class JevFlowGuard
{
    /// <summary>Same single-key allowlist as the guarded <c>game_jev_act</c> path.</summary>
    public static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "space", "enter", "escape", "tab", "up", "down", "left", "right",
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
    };

    /// <summary>
    /// Shared single-key policy for the guarded game-key paths: one unmodified key that is a named navigation key, a digit, or a single ASCII
    /// letter (tower hotkeys such as W/E/R). Modifier chords (Alt+F4, Ctrl+W), function keys, bare modifiers and mouse buttons are refused.
    /// Letters are normalized to lowercase.
    /// </summary>
    public static bool TryNormalizeSingleKey(string? key, out string normalized)
    {
        normalized = "";
        if (key is null) return false;
        if (AllowedKeys.Contains(key)) { normalized = key.ToLowerInvariant(); return true; }
        if (key.Length == 1 && char.IsAsciiLetter(key[0])) { normalized = char.ToLowerInvariant(key[0]).ToString(); return true; }
        return false;
    }

    private static readonly Regex RiskyText = new(
        @"\b(buy|purchase|pay|payment|checkout|subscribe|subscription|membership|premium|upgrade|shop|store|gem|gems|coin pack|real money|delete|erase|reset|uninstall|sign out|log ?out|quit|exit game|report|gift|redeem|promo|ad|ads|watch ad)\b|[$£€¥]\s?\d|\d\s?(usd|gbp|eur)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    /// <summary>In-world placement vocabulary. Refused for ordinary click/key actions, because an unreviewed click that "places" or "sells" something has no validity check.</summary>
    private static readonly Regex PlacementText = new(@"\b(place|placement|deploy|aim|drag|upgrade tower|sell)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public const double MaxPlacementRegionArea = 0.6;

    public static bool IsSafeId(string id) => id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    /// <summary>Null when the action may be executed by the flow; otherwise the refusal reason.</summary>
    public static string? Refusal(FlowAction action)
    {
        if (!IsSafeId(action.Id)) return "action id must be 1-64 characters of letters, digits, '_', '-' or '.'";
        if (action.Description.Length is 0 or > 300) return "description must contain 1-300 characters";
        if (action.TargetDescription is { Length: > 300 }) return "target description exceeds 300 characters";
        if (FlowControl.IsControl(action.Id)) return "action id collides with a flow control";
        bool placement = action.Kind == FlowActionKind.Place;
        switch (action.Kind)
        {
            case FlowActionKind.KeyTap:
                // Single letters (tower hotkeys) are accepted only from reviewed seeds, never from detectors or persisted-by-discovery entries.
                bool letter = action.Key is { Length: 1 } k && char.IsAsciiLetter(k[0]) && action.Source == "caller";
                if (action.Key is null || !(AllowedKeys.Contains(action.Key) || letter)) return "key is not in the single-key allowlist (letters need a reviewed caller seed)";
                break;
            case FlowActionKind.Click:
                if (action.Box is { } box && (!box.IsValid || box.Area > MaxBoxArea)) return "learned box is invalid or covers more than 15% of the window";
                if (string.IsNullOrWhiteSpace(action.TargetDescription)) return "click actions need a target description";
                break;
            case FlowActionKind.Place:
                if (action.Source != "caller") return "placement actions come only from reviewed caller seeds";
                if (action.ApprovedRegion is not { } region || !region.IsValid || region.Area > MaxPlacementRegionArea) return "placement needs an approved region covering at most 60% of the window";
                if (string.IsNullOrWhiteSpace(action.TargetDescription)) return "placement needs a target description";
                if (action.PlacementCheck is not { Length: > 0 and <= 300 }) return "placement needs a placement_check question of 1-300 characters";
                if (action.Box is not null) return "placement targets are never stored";
                break;
            default:
                return "kind must be click, key_tap or place";
        }
        try
        {
            if (RiskyText.IsMatch(action.Description) || (action.TargetDescription is { } target && RiskyText.IsMatch(target)))
                return "description looks like a purchase, account, destructive or exit control";
            if (!placement && (PlacementText.IsMatch(action.Description) || (action.TargetDescription is { } t2 && PlacementText.IsMatch(t2))))
                return "description looks like an in-world placement control; use a reviewed place action";
        }
        catch (RegexMatchTimeoutException)
        {
            return "description could not be screened";
        }
        return null;
    }

    public const double MaxBoxArea = 0.15;
}
