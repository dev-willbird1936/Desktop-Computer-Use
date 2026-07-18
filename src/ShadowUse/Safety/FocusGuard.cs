// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
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
        if (_foreground == IntPtr.Zero || !NativeMethods.IsWindow(_foreground)) return;
        if (NativeMethods.GetForegroundWindow() == _foreground) return; // user's focus untouched

        uint cur = NativeMethods.GetCurrentThreadId();

        // SetForegroundWindow only succeeds for a thread attached to the thread that
        // CURRENTLY owns foreground (the "thief" that just grabbed it) — attaching to
        // _threadId (the ORIGINAL pre-action foreground thread, i.e. the one we're
        // restoring TO, not the one that took it) grants no standing for that call.
        // SetFocus separately needs the calling thread attached to the window's OWNING
        // thread, which is _threadId. Both attaches are needed; they serve two different calls.
        var thief = NativeMethods.GetForegroundWindow();
        uint thiefTid = thief != IntPtr.Zero ? NativeMethods.GetWindowThreadProcessId(thief, out _) : 0;

        bool attachedThief = false, attachedOriginal = false;
        try
        {
            if (thiefTid != 0 && thiefTid != cur)
                attachedThief = NativeMethods.AttachThreadInput(cur, thiefTid, true);
            if (_threadId != 0 && _threadId != cur && _threadId != thiefTid)
                attachedOriginal = NativeMethods.AttachThreadInput(cur, _threadId, true);

            NativeMethods.SetForegroundWindow(_foreground);
            var focusTarget = _focus != IntPtr.Zero ? _focus : _caret;
            if (focusTarget != IntPtr.Zero && NativeMethods.IsWindow(focusTarget))
                NativeMethods.SetFocus(focusTarget);
        }
        finally
        {
            if (attachedOriginal) NativeMethods.AttachThreadInput(cur, _threadId, false);
            if (attachedThief) NativeMethods.AttachThreadInput(cur, thiefTid, false);
        }
    }
}
