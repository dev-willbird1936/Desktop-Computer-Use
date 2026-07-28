namespace ShadowUse.Tools;

internal static class ToolValidation
{
    public static string? ValidateClick(string button, int clickCount)
    {
        if (button is not ("left" or "right" or "middle"))
            return $"Unknown button '{button}'. Use left | right | middle.";
        if (clickCount is < 1 or > 3)
            return "click_count must be between 1 and 3.";
        return null;
    }

    public static string? ValidateMaxElements(int maxElements)
        => maxElements is < 1 or > 2_000
            ? "max_elements must be between 1 and 2000."
            : null;

    public static string? ValidatePositiveFinite(double value, double maximum, string name)
        => !double.IsFinite(value) || value <= 0 || value > maximum
            ? $"{name} must be greater than 0 and no more than {maximum}."
            : null;
}
