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
}
