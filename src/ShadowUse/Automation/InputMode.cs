using ShadowUse.Config;

namespace ShadowUse.Automation;

public readonly record struct InputMode(bool RealCursor, bool Humanized)
{
    public static InputMode Resolve(ShadowSettings settings, string? inputMode = null)
    {
        bool real = inputMode switch
        {
            null or "" => settings.UseRealCursor,
            "background" => false,
            "foreground" or "foreground_plain" or "humanized" => true,
            _ => throw new ArgumentException("input_mode must be background, foreground, foreground_plain or humanized.")
        };
        return new(real, real && (inputMode == "humanized" || inputMode != "foreground_plain" && settings.HumanizedInput));
    }

    public string Name => RealCursor ? "foreground" : "background";
    public string DispatchName => RealCursor ? Humanized ? "humanized" : "foreground_plain" : "background";
}
