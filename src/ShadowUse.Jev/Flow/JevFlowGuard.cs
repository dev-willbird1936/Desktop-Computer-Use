// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Text.RegularExpressions;

namespace ShadowUse.Jev.Flow;

/// <summary>
/// Static safety filters for learned or discovered actions. Learned menus are executable by the flow,
/// so anything that could spend money, destroy data, or leave the game is excluded before Jev can choose it.
/// The earlier live BTD2 run clicked a control that opened a paid-membership offer; this guard exists for that class.
/// </summary>
public static class JevFlowGuard
{
    /// <summary>Same single-key allowlist as the guarded <c>game_jev_act</c> path.</summary>
    public static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "space", "enter", "escape", "tab", "up", "down", "left", "right",
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
    };

    private static readonly Regex RiskyText = new(
        @"\b(buy|purchase|pay|payment|checkout|subscribe|subscription|membership|premium|upgrade|shop|store|gem|gems|coin pack|real money|delete|erase|reset|uninstall|sign out|log ?out|quit|exit game|report|gift|redeem|promo|ad|ads|watch ad)\b|[$£€¥]\s?\d|\d\s?(usd|gbp|eur)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public static bool IsSafeId(string id) => id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    /// <summary>Null when the action may be executed by the flow; otherwise the refusal reason.</summary>
    public static string? Refusal(FlowAction action)
    {
        if (!IsSafeId(action.Id)) return "action id must be 1-64 characters of letters, digits, '_', '-' or '.'";
        if (action.Description.Length is 0 or > 300) return "description must contain 1-300 characters";
        if (action.TargetDescription is { Length: > 300 }) return "target description exceeds 300 characters";
        if (FlowControl.IsControl(action.Id)) return "action id collides with a flow control";
        switch (action.Kind)
        {
            case FlowActionKind.KeyTap:
                if (action.Key is null || !AllowedKeys.Contains(action.Key)) return "key is not in the single-key allowlist";
                break;
            case FlowActionKind.Click:
                if (action.Box is { } box && (!box.IsValid || box.Area > MaxBoxArea)) return "learned box is invalid or covers more than 15% of the window";
                if (string.IsNullOrWhiteSpace(action.TargetDescription)) return "click actions need a target description";
                break;
            default:
                return "kind must be click or key_tap";
        }
        try
        {
            if (RiskyText.IsMatch(action.Description) || (action.TargetDescription is { } target && RiskyText.IsMatch(target)))
                return "description looks like a purchase, account, destructive or exit control";
        }
        catch (RegexMatchTimeoutException)
        {
            return "description could not be screened";
        }
        return null;
    }

    public const double MaxBoxArea = 0.15;
}
