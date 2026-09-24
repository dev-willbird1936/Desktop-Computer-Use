using ShadowUse.Browser;

namespace ShadowUse.Tests;

/// <summary>F42-R01: each browser candidate must expose exactly what the existing browser_* tools
/// can actually do with it, derived from the real HTML tag/type.</summary>
public sealed class BrowserCapabilitiesTests
{
    [Theory]
    [InlineData("button", null, new[] { "click" })]
    [InlineData("a", null, new[] { "click" })]
    [InlineData("textarea", null, new[] { "fill", "click" })]
    [InlineData("input", null, new[] { "fill", "click" })]
    [InlineData("input", "text", new[] { "fill", "click" })]
    [InlineData("input", "email", new[] { "fill", "click" })]
    [InlineData("input", "password", new[] { "fill", "click" })]
    [InlineData("input", "checkbox", new[] { "click" })]
    [InlineData("input", "radio", new[] { "click" })]
    [InlineData("input", "submit", new[] { "click" })]
    [InlineData("input", "file", new[] { "upload" })]
    public void SupportedOperationsFor_MapsRoleAndTypeToRealTools(string role, string? type, string[] expected)
    {
        Assert.Equal(expected, BrowserCapabilities.SupportedOperationsFor(role, type));
    }

    [Fact]
    public void SupportedOperationsFor_Select_IsClickOnly()
    {
        // Playwright's generic FillAsync rejects <select> elements -- claiming "fill" here
        // would tell a caller to attempt an operation that is guaranteed to throw.
        Assert.Equal(["click"], BrowserCapabilities.SupportedOperationsFor("select", null));
    }

    [Fact]
    public void SupportedOperationsFor_UnknownRole_DefaultsToClickOnly()
    {
        Assert.Equal(["click"], BrowserCapabilities.SupportedOperationsFor("custom-element", null));
    }
}
