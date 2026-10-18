using System.Reflection;
using ShadowUse.Config;

namespace ShadowUse.Tests;

public sealed class SettingsTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(9_000, 5_000)]
    public void Normalize_ClampsPostActionDelay(int configured, int expected)
    {
        var settings = new ShadowSettings { PostActionDelayMs = configured };
        var normalize = typeof(ShadowSettings).GetMethod(
            "Normalize",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(normalize);
        normalize.Invoke(settings, null);

        Assert.Equal(expected, settings.PostActionDelayMs);
    }

    [Fact]
    public void EnableCatalogueComposition_DefaultsToFalse()
    {
        // F59-R02: catalogue composition stays behind a feature flag, off by default.
        Assert.False(new ShadowSettings().EnableCatalogueComposition);
    }

    [Fact]
    public void ExperimentalGameJevAct_DefaultsToFalse()
    {
        Assert.False(new ShadowSettings().EnableExperimentalGameJevAct);
    }

    [Fact]
    public void ExperimentalGameJevActThresholds_DefaultAndNormalize()
    {
        var defaults = new ShadowSettings();
        Assert.Equal(0.85, defaults.ExperimentalGameJevActChoiceThreshold);
        Assert.Equal(0.90, defaults.ExperimentalGameJevActClickThreshold);
        Assert.Equal(0.10, defaults.ExperimentalGameJevActAmbiguityMargin);

        var settings = new ShadowSettings
        {
            ExperimentalGameJevActChoiceThreshold = -1,
            ExperimentalGameJevActClickThreshold = 2,
            ExperimentalGameJevActAmbiguityMargin = double.NaN,
        };
        var normalize = typeof(ShadowSettings).GetMethod("Normalize", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(normalize);
        normalize.Invoke(settings, null);

        Assert.Equal(0, settings.ExperimentalGameJevActChoiceThreshold);
        Assert.Equal(1, settings.ExperimentalGameJevActClickThreshold);
        Assert.Equal(0.10, settings.ExperimentalGameJevActAmbiguityMargin);
    }
}
