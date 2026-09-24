using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace ShadowUse.Browser;

/// <summary>F42-R02: best-effort discovery of the real OS window backing a non-headless Chromium
/// session, so a future native coordinate-bridge fallback has a genuine HWND to act on instead of
/// the never-populated BrowserSessionOptions.HostHwnd hint. Headless sessions are never attempted
/// -- there is no OS window at all to find. Uses a real Chrome DevTools Protocol call
/// (SystemInfo.getProcessInfo) to get the browser process's actual OS pid, then plain Win32 window
/// enumeration to find its visible top-level window. Any failure returns null, never a guess.</summary>
internal static class BrowserHostWindow
{
    public static async Task<string?> TryDiscoverAsync(IBrowser? browser, bool headless, CancellationToken ct)
    {
        // SystemInfo.getProcessInfo is a BROWSER-target CDP method (rejected on a page-level
        // session), and a persistent-context launch can leave IBrowserContext.Browser null -- both
        // are genuine "cannot discover" cases, not headless-only ones.
        if (headless || browser is null || !OperatingSystem.IsWindows())
            return null;
        try
        {
            var cdp = await browser.NewBrowserCDPSessionAsync().ConfigureAwait(false);
            var result = await cdp.SendAsync("SystemInfo.getProcessInfo").ConfigureAwait(false);
            if (result is null || !result.Value.TryGetProperty("processInfo", out var processInfo))
                return null;
            var browserPid = 0;
            foreach (var entry in processInfo.EnumerateArray())
            {
                if (entry.TryGetProperty("type", out var type) && type.GetString() == "browser"
                    && entry.TryGetProperty("id", out var id))
                {
                    browserPid = id.GetInt32();
                    break;
                }
            }
            if (browserPid == 0)
                return null;
            ct.ThrowIfCancellationRequested();
            return FindVisibleTopLevelWindow(browserPid);
        }
        catch (Exception ex) when (ex is PlaywrightException or JsonException or NotSupportedException)
        {
            // CDP unavailable (e.g. a non-Chromium future engine) or an unexpected response shape --
            // best-effort discovery, never fatal to opening the session.
            return null;
        }
    }

    private static string? FindVisibleTopLevelWindow(int pid)
    {
        nint found = 0;
        bool Callback(nint hwnd, nint _)
        {
            GetWindowThreadProcessId(hwnd, out var windowPid);
            if (windowPid != (uint)pid || !IsWindowVisible(hwnd))
                return true;
            var length = GetWindowTextLength(hwnd);
            if (length == 0)
                return true; // skip hidden helper/tooltip windows with no title
            found = hwnd;
            return false;
        }
        EnumWindows(Callback, 0);
        return found == 0 ? null : $"0x{found:X}";
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowTextLength(nint hWnd);
}
