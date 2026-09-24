// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Browser;

namespace ShadowUse.Tests;

public sealed class BrowserExtractionTests
{
    private static BrowserExtractField Field(string type = "text", bool required = false)
        => new("qty", "#qty", type, required);

    [Fact]
    public void ZeroMatches_ReturnsMissing()
    {
        var result = BrowserExtraction.Parse(Field(), 0, null, "obs-1");
        Assert.Equal("missing", result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public void MultipleMatches_ReturnsAmbiguous()
    {
        var result = BrowserExtraction.Parse(Field(), 3, null, "obs-1");
        Assert.Equal("ambiguous", result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Text_ReturnsRawStringAsIs()
    {
        var result = BrowserExtraction.Parse(Field("text"), 1, "  Hello world  ", "obs-1");
        Assert.Equal("ok", result.Status);
        Assert.Equal("  Hello world  ", result.Value);
        Assert.Equal("  Hello world  ", result.RawText);
    }

    [Fact]
    public void Number_ParsesThousandsSeparatedValue()
    {
        var result = BrowserExtraction.Parse(Field("number"), 1, "1,234.5", "obs-1");
        Assert.Equal("ok", result.Status);
        Assert.Equal(1234.5, result.Value);
        Assert.Null(result.Unit);
    }

    [Fact]
    public void Number_NonNumericText_ReturnsTypeMismatchNotAGuess()
    {
        var result = BrowserExtraction.Parse(Field("number"), 1, "out of stock", "obs-1");
        Assert.Equal("type_mismatch", result.Status);
        Assert.Null(result.Value);
        Assert.Equal("out of stock", result.RawText);
    }

    [Theory]
    [InlineData("$1,234.56", 1234.56, "$")]
    [InlineData("€99.00", 99.0, "€")]
    [InlineData("120.00 USD", 120.0, "USD")]
    public void Currency_ExtractsValueAndPreservesSymbolAsUnit(string text, double expectedValue, string expectedUnit)
    {
        var result = BrowserExtraction.Parse(Field("currency"), 1, text, "obs-1");
        Assert.Equal("ok", result.Status);
        Assert.Equal(expectedValue, result.Value);
        Assert.Equal(expectedUnit, result.Unit);
        Assert.Equal(text, result.RawText);
    }

    [Fact]
    public void Currency_UnparseableText_ReturnsTypeMismatch()
    {
        var result = BrowserExtraction.Parse(Field("currency"), 1, "call for pricing", "obs-1");
        Assert.Equal("type_mismatch", result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Date_ParsesToIsoAndKeepsRawText()
    {
        var result = BrowserExtraction.Parse(Field("date"), 1, "2026-03-14", "obs-1");
        Assert.Equal("ok", result.Status);
        Assert.Equal("2026-03-14", result.RawText);
        Assert.NotNull(result.Value);
        Assert.StartsWith("2026-03-14", (string)result.Value!);
    }

    [Fact]
    public void Date_UnparseableText_ReturnsTypeMismatch()
    {
        var result = BrowserExtraction.Parse(Field("date"), 1, "whenever", "obs-1");
        Assert.Equal("type_mismatch", result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Quote_ExactSubstring_ReturnsOffsetAndVerbatimText()
    {
        var field = new BrowserExtractField("terms", "#terms", "text", Quote: "no refunds after 30 days");
        var result = BrowserExtraction.Parse(field, 1, "Store policy: no refunds after 30 days from purchase.", "obs-1");

        Assert.Equal("ok", result.Status);
        Assert.Equal(14, result.QuoteOffset);
        Assert.Equal("no refunds after 30 days", result.QuoteText);
        Assert.Equal(result.RawText!.Substring(result.QuoteOffset!.Value, result.QuoteText!.Length), result.QuoteText);
    }

    [Fact]
    public void Quote_NotPresentVerbatim_ReturnsNullOffsetRatherThanAFuzzyMatch()
    {
        var field = new BrowserExtractField("terms", "#terms", "text", Quote: "no refund after 30 days"); // missing the 's'
        var result = BrowserExtraction.Parse(field, 1, "Store policy: no refunds after 30 days from purchase.", "obs-1");

        Assert.Equal("ok", result.Status);
        Assert.Null(result.QuoteOffset);
        Assert.Null(result.QuoteText);
    }

    [Fact]
    public void Quote_IsCaseSensitiveAndNeverNormalizes()
    {
        var field = new BrowserExtractField("terms", "#terms", "text", Quote: "NO REFUNDS");
        var result = BrowserExtraction.Parse(field, 1, "no refunds after 30 days", "obs-1");

        Assert.Null(result.QuoteOffset);
    }

    [Fact]
    public void Quote_OnAMissingField_NeverFabricatesAnOffset()
    {
        var field = new BrowserExtractField("terms", "#terms", "text", Quote: "anything");
        var result = BrowserExtraction.Parse(field, 0, null, "obs-1");

        Assert.Equal("missing", result.Status);
        Assert.Null(result.QuoteOffset);
    }

    [Fact]
    public void CapturedAt_IsSetOnEveryResultIncludingMissingAndAmbiguous()
    {
        var missing = BrowserExtraction.Parse(Field(), 0, null, "obs-1");
        var ambiguous = BrowserExtraction.Parse(Field(), 2, null, "obs-1");
        var ok = BrowserExtraction.Parse(Field(), 1, "hello", "obs-1");

        Assert.NotNull(missing.CapturedAt);
        Assert.NotNull(ambiguous.CapturedAt);
        Assert.NotNull(ok.CapturedAt);
    }

    [Fact]
    public void Source_CarriesSelectorFrameAndObservationId()
    {
        var field = new BrowserExtractField("price", "#price", "currency", Frame: "iframe-0");
        var result = BrowserExtraction.Parse(field, 1, "$5.00", "obs-42");

        Assert.Equal("#price", result.Source.Selector);
        Assert.Equal("iframe-0", result.Source.Frame);
        Assert.Equal("obs-42", result.Source.ObservationId);
    }
}
