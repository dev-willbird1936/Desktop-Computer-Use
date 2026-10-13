using ShadowUse.Automation;
using ShadowUse.Config;

namespace ShadowUse.Tests;

public sealed class InputModeTests
{
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
