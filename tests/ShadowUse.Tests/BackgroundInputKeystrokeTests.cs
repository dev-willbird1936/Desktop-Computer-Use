// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Reflection;
using System.Runtime.InteropServices;
using ShadowUse.Automation;
using Xunit;

namespace ShadowUse.Tests;

/// <summary>Regression coverage for a real bug found during a bug-hunt pass: BackgroundInput's
/// render-widget typing tier used to send a real VK_RETURN keydown/keyup for any '\n' character
/// in typed text, because VkKeyScanW('\n') maps to VK_RETURN with the Ctrl modifier bit set on a
/// US layout, and the old code only ever inspected the shift bit. A real Enter keydown reaching a
/// web page can submit its form -- an unintended action a text-typing tool must never trigger.</summary>
public sealed class BackgroundInputKeystrokeTests
{
    [DllImport("user32.dll")]
    private static extern short VkKeyScanW(char ch);

    private static MethodInfo ShouldSendCharOnlyMethod() =>
        typeof(BackgroundInput).GetMethod("ShouldSendCharOnly", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BackgroundInput.ShouldSendCharOnly not found");

    [Fact]
    public void VkKeyScanW_Newline_RequiresCtrlOnThisLayout()
    {
        // Documents the actual environment fact the bug depended on, independent of the fix.
        short vk = VkKeyScanW('\n');
        Assert.NotEqual(-1, vk);
        bool ctrlOrAlt = (vk & 0x0600) != 0;
        Assert.True(ctrlOrAlt, $"expected VkKeyScanW('\\n')={vk} to require ctrl/alt on this layout");
    }

    [Fact]
    public void ShouldSendCharOnly_TrueForNewlineVk_FalseForOrdinaryLetters()
    {
        var method = ShouldSendCharOnlyMethod();
        short newlineVk = VkKeyScanW('\n');
        short letterVk = VkKeyScanW('a');
        short upperVk = VkKeyScanW('A');

        Assert.True((bool)method.Invoke(null, [newlineVk])!,
            "a newline's vk (ctrl-modified on this layout) must be routed to WM_CHAR-only, never a real keydown");
        Assert.False((bool)method.Invoke(null, [letterVk])!,
            "a plain letter must still use the normal keydown/char/keyup path");
        Assert.False((bool)method.Invoke(null, [upperVk])!,
            "a shift-only uppercase letter must still use the normal keydown/char/keyup path, not be treated as ctrl/alt");
    }

    [Fact]
    public void ShouldSendCharOnly_TrueForUnmappedVk()
    {
        var method = ShouldSendCharOnlyMethod();
        Assert.True((bool)method.Invoke(null, [(short)(-1)])!,
            "an unmapped vk (-1) must be sent as WM_CHAR only, matching the prior behavior for unmapped characters");
    }
}
