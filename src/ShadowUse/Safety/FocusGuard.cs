using ShadowUse.Native;

namespace ShadowUse.Safety;

/// <summary>
/// Focus guard: captures the user's foreground window, keyboard-focus HWND and caret
/// owner before an action; afterwards, if the target app grabbed any of them (UIA
/// Invoke and posted clicks can make apps SetFocus internally — e.g. a browser moving
/// the caret into its address bar), puts everything back. Restore uses the
/// AttachThreadInput trick so it works even though we never owned foreground.
/// </summary>
internal sealed class FocusGuard
{
    private readonly IntPtr _foreground;
    private readonly IntPtr _focus;
    private readonly IntPtr _caret;
    private readonly uint _threadId;

    private FocusGuard(IntPtr foreground, IntPtr focus, IntPtr caret, uint threadId)
    {
        _foreground = foreground;
        _focus = focus;
        _caret = caret;
        _threadId = threadId;
    }

    public static FocusGuard Capture()
    {
        var fg = NativeMethods.GetForegroundWindow();
        IntPtr focus = IntPtr.Zero, caret = IntPtr.Zero;
        uint tid = 0;
        if (fg != IntPtr.Zero)
        {
            tid = NativeMethods.GetWindowThreadProcessId(fg, out _);
            var info = new NativeMethods.GUITHREADINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
            if (NativeMethods.GetGUIThreadInfo(tid, ref info))
            {
                focus = info.hwndFocus;
                caret = info.hwndCaret;
            }
        }
        return new FocusGuard(fg, focus, caret, tid);
    }

    /// <summary>Restore foreground/focus/caret if the action moved them. No-op if nothing changed.</summary>
    public void Restore()
    {
        if (_foreground == IntPtr.Zero) return;
        if (NativeMethods.GetForegroundWindow() == _foreground) return; // user's focus untouched

        uint cur = NativeMethods.GetCurrentThreadId();
        bool attached = false;
        try
        {
            if (_threadId != 0 && _threadId != cur)
                attached = NativeMethods.AttachThreadInput(cur, _threadId, true);

            NativeMethods.SetForegroundWindow(_foreground);
            var focusTarget = _focus != IntPtr.Zero ? _focus : _caret;
            if (focusTarget != IntPtr.Zero && NativeMethods.IsWindow(focusTarget))
                NativeMethods.SetFocus(focusTarget);
        }
        finally
        {
            if (attached)
                NativeMethods.AttachThreadInput(cur, _threadId, false);
        }
    }
}
