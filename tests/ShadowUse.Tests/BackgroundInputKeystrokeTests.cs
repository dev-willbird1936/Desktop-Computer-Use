using System.Reflection;
using ShadowUse.Automation;

namespace ShadowUse.Tests;

public sealed class BackgroundInputKeystrokeTests
{
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0x020D, true)] // LF mapped to Ctrl+Enter
    [InlineData(0x0441, true)] // Alt-modified character
    [InlineData(0x0641, true)] // Ctrl+Alt-modified character
    [InlineData(0x0041, false)]
    [InlineData(0x0141, false)] // Shift alone is supported
    public void ShouldSendCharOnly_PreservesTextWithoutSendingShortcuts(int mapping, bool expected)
    {
        var method = typeof(BackgroundInput).GetMethod(
            "ShouldSendCharOnly", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(expected, (bool)method.Invoke(null, [(short)mapping])!);
    }
}
