using ShadowUse.Automation;
using ShadowUse.Native;

namespace ShadowUse.Safety;

/// <summary>
/// Optional safety rails — ALL are off by default (see ShadowSettings).
/// When enabled:
/// - stale-geometry guard: coordinate input is refused if the window moved/resized
///   since the snapshot the model is looking at
/// - secure-desktop / lock detection: refuse to act when the input desktop isn't the user's
/// </summary>
internal static partial class Guard
{
    public static string? CheckInteractiveDesktop()
    {
        var desktop = OpenInputDesktop(0, false, 0x0001 /*GENERIC_READ*/);
        if (desktop == IntPtr.Zero)
            return "Input desktop unavailable (locked screen or secure desktop). Unlock before using computer control.";
        CloseDesktop(desktop);
        return null;
    }

    /// <summary>Returns an error string if the window's bounds changed since the snapshot.</summary>
    public static string? CheckBounds(Snapshot snap, IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var current))
            return "Target window no longer exists; call get_app_state to re-resolve.";
        if (current.Left != snap.Bounds.Left || current.Top != snap.Bounds.Top
         || current.Right != snap.Bounds.Right || current.Bottom != snap.Bounds.Bottom)
            return $"Window bounds changed since snapshot r{snap.Revision} " +
                   $"(({snap.Bounds.Left},{snap.Bounds.Top})→({current.Left},{current.Top})). " +
                   "Call get_app_state before issuing coordinate input.";
        return null;
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr OpenInputDesktop(uint dwFlags, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool CloseDesktop(IntPtr hDesktop);
}
