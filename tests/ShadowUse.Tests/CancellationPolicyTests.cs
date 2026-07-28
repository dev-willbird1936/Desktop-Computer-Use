using System.Reflection;
using ShadowUse.Tools;

namespace ShadowUse.Tests;

public sealed class CancellationPolicyTests
{
    [Fact]
    public void IsCallerCancellation_RecognizesCanceledRequest()
    {
        var method = typeof(ComputerUseTools).GetMethod(
            "IsCallerCancellation",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        using var source = new CancellationTokenSource();
        source.Cancel();

        var recognized = Assert.IsType<bool>(
            method.Invoke(null, [new OperationCanceledException(source.Token), source.Token]));

        Assert.True(recognized);
    }
}
