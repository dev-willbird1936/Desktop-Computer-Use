using System.Reflection;
using ShadowUse.Tools;

namespace ShadowUse.Tests;

public sealed class ElementResolutionTests
{
    [Fact]
    public void ResolveRequiredElement_RejectsMissingSnapshot()
    {
        var method = typeof(ComputerUseTools).GetMethod(
            "ResolveRequiredElement",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var error = Assert.Throws<TargetInvocationException>(
            () => method.Invoke(null, [null, "e1"]));

        Assert.IsType<InvalidOperationException>(error.InnerException);
    }
}
