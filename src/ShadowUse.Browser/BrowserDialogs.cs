namespace ShadowUse.Browser;

public static class BrowserDialogs
{
    public const string MismatchDismissed = " The mismatched dialog was dismissed so the page could continue.";

    public static string Validate(string? type, bool accept, string? promptText)
    {
        if (type is not ("alert" or "confirm" or "prompt"))
            throw new BrowserValidationException("A JavaScript dialog request must name alert, confirm, or prompt.");
        if (type == "prompt")
        {
            if (accept && promptText is null)
                throw new BrowserValidationException("Accepting a prompt requires the exact prompt text, including an empty string.");
            if (!accept && promptText is not null)
                throw new BrowserValidationException("Dismissing a prompt does not take prompt text.");
        }
        else if (promptText is not null)
        {
            throw new BrowserValidationException("Prompt text is only used when accepting a prompt.");
        }
        return type;
    }
}
