using ShadowUse.Automation;
using ShadowUse.Config;

namespace ShadowUse.Tests;

public sealed class InputModeTests
{
    [Fact]
    public void OmittedSettingsDefaultToVirtualCursorWithOptionalInputModesDisabled()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<ShadowSettings>("{}")!;
        Assert.True(settings.ShowVirtualCursor);
        Assert.False(settings.UseRealCursor);
        Assert.False(settings.GameMode);
        Assert.False(settings.HumanizedInput);
        Assert.False(settings.FastInput);
        Assert.Equal(new InputMode(false, false), InputMode.Resolve(settings));
        Assert.True(settings.EnableFocusGuard);
    }

    public static IEnumerable<object[]> ToggleCombinations()
    {
        for (int mask = 0; mask < 32; mask++)
            yield return Enumerable.Range(0, 5).Select(bit => (object)((mask & (1 << bit)) != 0)).ToArray();
    }

    private static ShadowSettings Settings(bool cursor, bool game, bool human, bool fast, bool show)
        => new() { UseRealCursor = cursor, GameMode = game, HumanizedInput = human, FastInput = fast, ShowVirtualCursor = show };

    [Theory]
    [MemberData(nameof(ToggleCombinations))]
    public void EveryToggleCombinationPreservesIndependentDefaults(bool cursor, bool game, bool human, bool fast, bool show)
    {
        var settings = Settings(cursor, game, human, fast, show);
        // Match a saved settings file rather than testing only object initializers.
        settings = System.Text.Json.JsonSerializer.Deserialize<ShadowSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        var mode = InputMode.Resolve(settings);
        Assert.Equal(cursor, mode.RealCursor);
        Assert.Equal(cursor && human, mode.Humanized);
        Assert.Equal(game, settings.GameMode);
        Assert.Equal(show, settings.ShowVirtualCursor);
        Assert.Equal(mode, InputMode.Resolve(settings, mode.DispatchName));
        using (InputPace.Scope(settings.FastInput ? "fast" : "normal"))
        {
            Assert.Equal(fast, InputPace.Fast);
            Assert.Equal(fast ? 35 : 100, InputPace.Ms(100));
        }
    }

    [Theory]
    [MemberData(nameof(ToggleCombinations))]
    public void PerCallOverridesWorkWithEveryToggleCombination(bool cursor, bool game, bool human, bool fast, bool show)
    {
        var settings = Settings(cursor, game, human, fast, show);
        Assert.Equal(new InputMode(false, false), InputMode.Resolve(settings, "background"));
        Assert.Equal(new InputMode(true, human), InputMode.Resolve(settings, "foreground"));
        Assert.Equal(new InputMode(true, false), InputMode.Resolve(settings, "foreground_plain"));
        Assert.Equal(new InputMode(true, true), InputMode.Resolve(settings, "humanized"));
        using (InputPace.Scope("fast")) Assert.True(InputPace.Fast);
        using (InputPace.Scope("normal")) Assert.False(InputPace.Fast);
        Assert.Equal(cursor, settings.UseRealCursor);
        Assert.Equal(game, settings.GameMode);
        Assert.Equal(human, settings.HumanizedInput);
        Assert.Equal(fast, settings.FastInput);
        Assert.Equal(show, settings.ShowVirtualCursor);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void CursorSelectionIsIndependentOfHumanization(bool cursor, bool humanized, bool expected)
    {
        var mode = InputMode.Resolve(new ShadowSettings { UseRealCursor = cursor, HumanizedInput = humanized });
        Assert.Equal(expected, mode.RealCursor);
        Assert.Equal(expected && humanized, mode.Humanized);
    }

    [Fact]
    public void GameModeNeverSilentlyTakesTheRealCursor()
        => Assert.False(InputMode.Resolve(new ShadowSettings { GameMode = true, HumanizedInput = true, FastInput = true }).RealCursor);

    [Fact]
    public void ExplicitBackgroundOverridesEveryForegroundDefault()
        => Assert.False(InputMode.Resolve(new ShadowSettings { UseRealCursor = true, HumanizedInput = true, GameMode = true }, "background").RealCursor);

    [Fact]
    public void ForegroundCanBeSelectedWithoutRestartOrSettingChanges()
        => Assert.True(InputMode.Resolve(new(), "foreground").RealCursor);

    [Fact]
    public void HumanizationCanBeSelectedAndDisabledWithoutEditingSettings()
    {
        Assert.True(InputMode.Resolve(new(), "humanized").Humanized);
        Assert.False(InputMode.Resolve(new ShadowSettings { UseRealCursor = true, HumanizedInput = true }, "foreground_plain").Humanized);
    }

    [Fact]
    public void InvalidModeFailsRatherThanFallingThroughToRealInput()
        => Assert.Throws<ArgumentException>(() => InputMode.Resolve(new(), "typo"));

    [Fact]
    public void PerCallPaceOverridesWithoutAffectingOtherCalls()
    {
        using (InputPace.Scope("fast")) Assert.True(InputPace.Fast);
        using (InputPace.Scope("normal")) Assert.False(InputPace.Fast);
    }
}
