using System.Reflection;
using ShadowUse.Automation;

namespace ShadowUse.Tests;

public sealed class WindowMessageTimeoutTests
{
    [Fact]
    public void TrySendMessageWithTimeout_RejectsInvalidWindow()
    {
        var method = typeof(BackgroundInput).GetMethod(
            "TrySendMessageWithTimeout",
            BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(IntPtr), typeof(uint), typeof(IntPtr), typeof(IntPtr), typeof(IntPtr).MakeByRefType()]);

        Assert.NotNull(method);
        object?[] arguments = [IntPtr.Zero, (uint)0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero];
        var sent = Assert.IsType<bool>(method.Invoke(null, arguments));

        Assert.False(sent);
    }
}
