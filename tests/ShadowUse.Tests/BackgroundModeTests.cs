using System.Reflection;
using ShadowUse.Tools;

namespace ShadowUse.Tests;

/// <summary>F01-R01/F01-T02: background_mode must be an explicit, checked contract -- strict_background
/// (the only implemented mode) always passes, the two named-but-unimplemented modes always return a
/// structured UNSUPPORTED_INPUT_MODE error, and nothing outside these three strings is silently accepted.</summary>
public sealed class BackgroundModeTests
{
    private static object? Invoke(string backgroundMode)
    {
        var method = typeof(ComputerUseTools).GetMethod(
            "CheckBackgroundMode",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(null, [backgroundMode]);
    }

    [Fact]
    public void StrictBackground_PassesWithNoError()
    {
        Assert.Null(Invoke("strict_background"));
    }

    [Theory]
    [InlineData("best_effort_background")]
    [InlineData("isolated_interactive")]
    public void UnimplementedNamedModes_ReturnStructuredUnsupportedInputMode(string mode)
    {
        var result = Invoke(mode);
        Assert.NotNull(result);
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("UNSUPPORTED_INPUT_MODE", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(mode, doc.RootElement.GetProperty("background_mode").GetString());
    }

    [Fact]
    public void UnknownMode_ReturnsPlainValidationErrorNotUnsupportedInputMode()
    {
        var result = Invoke("banana_mode");
        Assert.NotNull(result);
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        // A garbage string is a validation mistake, not a genuine request for a named
        // unimplemented mode -- it must not be conflated with UNSUPPORTED_INPUT_MODE.
        Assert.False(doc.RootElement.TryGetProperty("error", out var err) && err.GetString() == "UNSUPPORTED_INPUT_MODE");
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }
}
