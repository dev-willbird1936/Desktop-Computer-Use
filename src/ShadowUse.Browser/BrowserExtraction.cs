// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Globalization;
using System.Text.RegularExpressions;

namespace ShadowUse.Browser;

/// <summary>One requested field for browser_extract (F33). An optional exact Quote (F34)
/// locates that verbatim substring inside the field's raw text and returns its offset.</summary>
public sealed record BrowserExtractField(string Name, string Selector, string Type = "text", bool Required = false, string Frame = "main", string? Quote = null);

public sealed record BrowserExtractSource(string Selector, string Frame, string ObservationId);

/// <summary>
/// One field's extraction result. Status is always one of ok/missing/ambiguous/type_mismatch —
/// a required field that cannot be read never gets a fabricated Value (F33-R02). QuoteOffset/
/// QuoteText are only set when the field requested a Quote and it was found verbatim in RawText
/// (F34-R02): never a fuzzy match, corrected spelling or paraphrase.
/// </summary>
public sealed record BrowserExtractResult(
    string Name,
    string Status,
    string? RawText,
    object? Value,
    string? Unit,
    BrowserExtractSource Source,
    int? QuoteOffset = null,
    string? QuoteText = null,
    DateTimeOffset? CapturedAt = null);

/// <summary>
/// Pure, source-preserving type parsing for extracted text. Never invents a value: a field
/// that does not parse as its requested type comes back type_mismatch with the raw text kept,
/// not a best-guess number/date.
/// </summary>
public static class BrowserExtraction
{
    private static readonly Regex CurrencyPattern = new(
        @"^\s*(?<symbol>[$€£¥]|[A-Z]{3})?\s*(?<number>-?[\d,]+(?:\.\d+)?)\s*(?<suffix>[A-Z]{3})?\s*$",
        RegexOptions.Compiled);

    public static BrowserExtractResult Parse(BrowserExtractField field, int matchCount, string? rawText, string observationId)
    {
        var source = new BrowserExtractSource(field.Selector, field.Frame, observationId);
        var capturedAt = DateTimeOffset.UtcNow;
        if (matchCount == 0)
            return new(field.Name, "missing", null, null, null, source, CapturedAt: capturedAt);
        if (matchCount > 1)
            return new(field.Name, "ambiguous", null, null, null, source, CapturedAt: capturedAt);

        var text = rawText ?? "";
        var result = field.Type.ToLowerInvariant() switch
        {
            "text" => new BrowserExtractResult(field.Name, "ok", text, text, null, source),
            "number" => ParseNumber(field.Name, text, source),
            "currency" => ParseCurrency(field.Name, text, source),
            "date" => ParseDate(field.Name, text, source),
            _ => new BrowserExtractResult(field.Name, "type_mismatch", text, null, null, source),
        };
        return WithQuote(result, field.Quote);
    }

    /// <summary>Locates Quote as an exact (ordinal) substring of the result's raw text. Never
    /// fuzzy-matches, trims, or case-folds: a quote must match the source byte-for-byte (F34-T01).</summary>
    private static BrowserExtractResult WithQuote(BrowserExtractResult result, string? quote)
    {
        var captured = result with { CapturedAt = DateTimeOffset.UtcNow };
        if (quote is null || captured.RawText is null)
            return captured;
        var offset = captured.RawText.IndexOf(quote, StringComparison.Ordinal);
        return offset < 0 ? captured : captured with { QuoteOffset = offset, QuoteText = quote };
    }

    private static BrowserExtractResult ParseNumber(string name, string text, BrowserExtractSource source)
    {
        var stripped = text.Trim().Replace(",", "");
        return double.TryParse(stripped, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? new(name, "ok", text, value, null, source)
            : new(name, "type_mismatch", text, null, null, source);
    }

    private static BrowserExtractResult ParseCurrency(string name, string text, BrowserExtractSource source)
    {
        var match = CurrencyPattern.Match(text.Trim());
        if (!match.Success)
            return new(name, "type_mismatch", text, null, null, source);
        var numberText = match.Groups["number"].Value.Replace(",", "");
        if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return new(name, "type_mismatch", text, null, null, source);
        var unit = match.Groups["symbol"].Success ? match.Groups["symbol"].Value
            : match.Groups["suffix"].Success ? match.Groups["suffix"].Value
            : null;
        return new(name, "ok", text, value, unit, source);
    }

    private static BrowserExtractResult ParseDate(string name, string text, BrowserExtractSource source)
        => DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? new(name, "ok", text, value.ToString("O", CultureInfo.InvariantCulture), null, source)
            : new(name, "type_mismatch", text, null, null, source);
}
