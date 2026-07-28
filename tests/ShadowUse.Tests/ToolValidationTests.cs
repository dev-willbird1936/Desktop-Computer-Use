using System.Reflection;
using ShadowUse.Tools;

namespace ShadowUse.Tests;

public sealed class ToolValidationTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidatePositiveFinite_RejectsInvalidValues(double value)
    {
        var validationType = typeof(ComputerUseTools).Assembly.GetType("ShadowUse.Tools.ToolValidation");
        Assert.NotNull(validationType);
        var method = validationType.GetMethod(
            "ValidatePositiveFinite",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var error = Assert.IsType<string>(method.Invoke(null, [value, 100d, "value"]));

        Assert.Contains("value", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateMaxElements_RejectsNegativeValue()
    {
        var validationType = typeof(ComputerUseTools).Assembly.GetType("ShadowUse.Tools.ToolValidation");
        Assert.NotNull(validationType);
        var method = validationType.GetMethod(
            "ValidateMaxElements",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var error = Assert.IsType<string>(method.Invoke(null, [-1]));

        Assert.Contains("max_elements", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateClick_RejectsUnknownButton()
    {
        var validationType = typeof(ComputerUseTools).Assembly.GetType("ShadowUse.Tools.ToolValidation");
        Assert.NotNull(validationType);
        var method = validationType.GetMethod(
            "ValidateClick",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var error = Assert.IsType<string>(method.Invoke(null, ["purple", 1]));

        Assert.Contains("button", error, StringComparison.OrdinalIgnoreCase);
    }
}
