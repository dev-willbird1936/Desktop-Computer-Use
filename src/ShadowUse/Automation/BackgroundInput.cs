using Interop.UIAutomationClient;
using ShadowUse.Native;

namespace ShadowUse.Automation;

/// <summary>
/// Focus-free input: UIA patterns first, PostMessage/SendMessage window messages second.
/// Never SendInput, never SetCursorPos, never SetFocus — the real cursor and the OS
/// foreground are never touched, so the user can keep working in other apps and the
/// target app never counts itself as focused.
/// </summary>
public sealed class BackgroundInput
{
    private readonly UiaThread _uia;
    public BackgroundInput(UiaThread uia) => _uia = uia;

    public enum MouseButton { Left, Right, Middle }

    public sealed record InputResult(bool Success, string Method, string Detail = "");

    // ---------- Click ----------

    /// <summary>Click an element: UIA Invoke/Toggle/Select (left only) → message click at element center.</summary>
    public async Task<InputResult> ClickElementAsync(IntPtr hwnd, IUIAutomationElement? element, ElementInfo? info,
        MouseButton button, int clickCount, CancellationToken ct)
    {
        // Tier 1: UIA patterns (left button only; right/middle have no semantic pattern)
        if (element != null && button == MouseButton.Left)
        {
            var viaPattern = await _uia.InvokeAsync(() =>
            {
                if (element.GetCurrentPattern(UiaIds.InvokePattern) is IUIAutomationInvokePattern invoke)
                { invoke.Invoke(); return "uia_invoke"; }
                if (element.GetCurrentPattern(UiaIds.SelectionItemPattern) is IUIAutomationSelectionItemPattern sel)
                { sel.Select(); return "uia_select"; }
                if (element.GetCurrentPattern(UiaIds.TogglePattern) is IUIAutomationTogglePattern tog)
                { tog.Toggle(); return "uia_toggle"; }
                return null;
            }, ct).ConfigureAwait(false);
            if (viaPattern != null)
            {
                if (clickCount > 1)
                    for (int i = 1; i < clickCount; i++)
                    {
                        await _uia.InvokeAsync(() =>
                        {
                            if (element.GetCurrentPattern(UiaIds.InvokePattern) is IUIAutomationInvokePattern inv) inv.Invoke();
                            return 0;
                        }, ct).ConfigureAwait(false);
                        await Task.Delay(60, ct).ConfigureAwait(false);
                    }
                return new InputResult(true, viaPattern);
            }
        }

        // Tier 2: message click — at element center if known, else needs coordinates
        if (info == null) return new InputResult(false, "none", "no UIA pattern and no element frame");
        return await ClickAtAsync(hwnd, info.ScreenX + info.Width / 2, info.ScreenY + info.Height / 2,
            button, clickCount, ct, ResolveInputHwnd(hwnd, info)).ConfigureAwait(false);
    }

    /// <summary>Message-based click at screen coordinates. Never moves the real cursor.
    /// Targets the actual child window under the point (Chrome's render widget, edit
    /// controls) — posting to the top-level frame alone is ignored by apps like Chrome.</summary>
    public Task<InputResult> ClickAtAsync(IntPtr hwnd, int screenX, int screenY, MouseButton button, int clickCount,
        CancellationToken ct, IntPtr? inputHwnd = null)
    {
        var target = inputHwnd ?? ChildWindowFromPoint(hwnd, screenX, screenY);
        return Task.Run(() =>
        {
            var pt = new NativeMethods.POINT { X = screenX, Y = screenY };
            NativeMethods.ScreenToClient(target, ref pt);
            var lParam = NativeMethods.MakeLParam(pt.X, pt.Y);
            (int down, int up, int flag) = button switch
            {
                MouseButton.Right => (NativeMethods.WM_RBUTTONDOWN, NativeMethods.WM_RBUTTONUP, NativeMethods.MK_RBUTTON),
                MouseButton.Middle => (NativeMethods.WM_MBUTTONDOWN, NativeMethods.WM_MBUTTONUP, NativeMethods.MK_MBUTTON),
                _ => (NativeMethods.WM_LBUTTONDOWN, NativeMethods.WM_LBUTTONUP, NativeMethods.MK_LBUTTON),
            };
            NativeMethods.PostMessageW(target, NativeMethods.WM_MOUSEMOVE, IntPtr.Zero, lParam);
            for (int i = 0; i < clickCount; i++)
            {
                NativeMethods.PostMessageW(target, (uint)down, (IntPtr)flag, lParam);
                Thread.Sleep(35);
                NativeMethods.PostMessageW(target, (uint)up, IntPtr.Zero, lParam);
                if (i + 1 < clickCount) Thread.Sleep(50);
            }
            Thread.Sleep(50);
            return new InputResult(true, "postmessage", $"({screenX},{screenY}) x{clickCount}");
        }, ct);
    }

    /// <summary>Deepest child of hwnd containing the screen point (Chrome_RenderWidgetHostHWND,
    /// edit controls, etc.), falling back to hwnd itself.</summary>
    private static IntPtr ChildWindowFromPoint(IntPtr hwnd, int screenX, int screenY)
    {
        var pt = new NativeMethods.POINT { X = screenX, Y = screenY };
        var child = NativeMethods.WindowFromPoint(pt);
        // WindowFromPoint can return a window from another process/thread — only accept
        // descendants of our target
        if (child != IntPtr.Zero && (child == hwnd || IsDescendant(hwnd, child)))
            return child;
        return hwnd;
    }

    private static bool IsDescendant(IntPtr ancestor, IntPtr hwnd)
    {
        var cur = hwnd;
        for (int i = 0; i < 32 && cur != IntPtr.Zero; i++)
        {
            if (cur == ancestor) return true;
            cur = NativeMethods.GetParent(cur);
        }
        return false;
    }

    /// <summary>Resolve the child HWND that should receive input — the element's own
    /// native window handle when it has one (edit controls, Electron sub-windows),
    /// else the window under the point, else the main window.</summary>
    public IntPtr ResolveInputHwnd(IntPtr hwnd, ElementInfo? info)
    {
        if (info?.NativeWindowHandle is int h and > 0 && h != hwnd.ToInt32())
            return (IntPtr)h;
        return hwnd;
    }

    // ---------- Typing ----------

    /// <summary>
    /// Focus-free text entry cascade:
    /// 1. child edit HWND → EM_SETSEL(end) + EM_REPLACESEL (no focus needed)
    /// 2. UIA ValuePattern.SetValue(append) — only if explicitly allowed (can foreground the app)
    /// 3. WM_CHAR stream to the main window
    /// </summary>
    public async Task<InputResult> TypeTextAsync(IntPtr hwnd, string text, CancellationToken ct, bool allowUiaTextFallback = false)
    {
        // Tier 1: find a text-entry child HWND (edit/richtext/document) and EM_REPLACESEL into it
        var editHwnd = await _uia.InvokeAsync(() => FindTextEntryHwnd(hwnd), ct).ConfigureAwait(false);
        if (editHwnd != IntPtr.Zero)
        {
            NativeMethods.SendMessageW(editHwnd, NativeMethods.EM_SETSEL, (IntPtr)(-1), (IntPtr)(-1));
            var r = NativeMethods.SendMessageW(editHwnd, NativeMethods.EM_REPLACESEL, (IntPtr)1, text);
            if (r == IntPtr.Zero) // EM_REPLACESEL returns 0 on success in most controls; some return non-zero
                return new InputResult(true, "em_replacesel", $"hwnd=0x{editHwnd.ToInt64():X}");
            return new InputResult(true, "em_replacesel", $"hwnd=0x{editHwnd.ToInt64():X} (rc={r})");
        }

        // Tier 2: UIA SetValue append (gated — observed to foreground some apps)
        if (allowUiaTextFallback)
        {
            var ok = await _uia.InvokeAsync(() =>
            {
                var el = FindTextEntryElement(hwnd);
                if (el?.GetCurrentPattern(UiaIds.ValuePattern) is IUIAutomationValuePattern vp
                    && vp.CurrentIsReadOnly == 0)
                {
                    vp.SetValue((vp.CurrentValue ?? "") + text);
                    return true;
                }
                return false;
            }, ct).ConfigureAwait(false);
            if (ok) return new InputResult(true, "uia_setvalue_append");
        }

        // Tier 3: WM_CHAR stream to main window
        await Task.Run(() =>
        {
            foreach (char c in text)
            {
                NativeMethods.PostMessageW(hwnd, NativeMethods.WM_CHAR, (IntPtr)c, IntPtr.Zero);
                Thread.Sleep(8);
            }
        }, ct).ConfigureAwait(false);
        return new InputResult(true, "wm_char", $"{text.Length} chars");
    }

    private IntPtr FindTextEntryHwnd(IntPtr hwnd)
    {
        // Score candidates: the real edit control must win over wrapper classes like
        // Notepad's "NotepadTextBox" (which contains "Text" but ignores EM_REPLACESEL).
        // RichEdit* > *Edit* > *Text*. Visible wins ties.
        IntPtr best = IntPtr.Zero;
        int bestScore = -1;
        NativeMethods.EnumChildWindows(hwnd, (child, _) =>
        {
            var cls = NativeMethods.GetClassNameString(child);
            int score = cls.Contains("RichEdit", StringComparison.OrdinalIgnoreCase) ? 3
                : cls.Contains("Edit", StringComparison.OrdinalIgnoreCase) ? 2
                : cls.Contains("Text", StringComparison.OrdinalIgnoreCase) ? 1 : -1;
            if (score < 0) return true;
            if (NativeMethods.IsWindowVisible(child)) score += 10;
            if (score > bestScore) { bestScore = score; best = child; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    private IUIAutomationElement? FindTextEntryElement(IntPtr hwnd)
    {
        var uia = new CUIAutomation8();
        var root = uia.ElementFromHandle(hwnd);
        // focused element first
        try
        {
            var focused = uia.GetFocusedElement();
            if (focused?.GetCurrentPattern(UiaIds.ValuePattern) is IUIAutomationValuePattern fvp
                && fvp.CurrentIsReadOnly == 0)
                return focused;
        }
        catch { }
        var editCondition = uia.CreateOrCondition(
            uia.CreatePropertyCondition(UiaIds.ControlTypeProperty, 50004), // Edit
            uia.CreatePropertyCondition(UiaIds.ControlTypeProperty, 50030)); // Document
        var edits = root.FindAll(TreeScope.TreeScope_Descendants, editCondition);
        for (int i = 0; i < edits.Length; i++)
        {
            var el = edits.GetElement(i);
            try
            {
                if (el.GetCurrentPattern(UiaIds.ValuePattern) is IUIAutomationValuePattern vp
                    && vp.CurrentIsReadOnly == 0)
                    return el;
            }
            catch { }
        }
        return null;
    }

    // ---------- Keys ----------

    /// <summary>Key press via posted WM_KEYDOWN/WM_KEYUP (+WM_CHAR for text keys). No focus steal.</summary>
    public Task<InputResult> PressKeyAsync(IntPtr hwnd, string key, CancellationToken ct)
    {
        var (vk, ch, mods) = ParseKey(key);
        return Task.Run(() =>
        {
            foreach (var m in mods)
                NativeMethods.PostMessageW(hwnd, NativeMethods.WM_KEYDOWN, (IntPtr)m, IntPtr.Zero);
            NativeMethods.PostMessageW(hwnd, NativeMethods.WM_KEYDOWN, (IntPtr)vk, IntPtr.Zero);
            Thread.Sleep(25);
            if (ch != 0)
                NativeMethods.PostMessageW(hwnd, NativeMethods.WM_CHAR, (IntPtr)ch, IntPtr.Zero);
            NativeMethods.PostMessageW(hwnd, NativeMethods.WM_KEYUP, (IntPtr)vk, IntPtr.Zero);
            foreach (var m in mods.Reverse())
                NativeMethods.PostMessageW(hwnd, NativeMethods.WM_KEYUP, (IntPtr)m, IntPtr.Zero);
            return new InputResult(true, "postmessage_key", key);
        }, ct);
    }

    private static (int vk, int ch, int[] mods) ParseKey(string key)
    {
        var mods = new List<int>();
        var parts = key.ToLowerInvariant().Split('+', '-');
        foreach (var p in parts[..^1])
        {
            switch (p.Trim())
            {
                case "ctrl" or "control": mods.Add(NativeMethods.VK_CONTROL); break;
                case "shift": mods.Add(NativeMethods.VK_SHIFT); break;
                case "alt": mods.Add(NativeMethods.VK_MENU); break;
                case "win" or "super": mods.Add(NativeMethods.VK_LWIN); break;
            }
        }
        var last = parts[^1].Trim();
        int vk = last switch
        {
            "return" or "enter" => 0x0D, "tab" => 0x09, "esc" or "escape" => 0x1B,
            "space" => 0x20, "back" or "backspace" => 0x08, "delete" => 0x2E,
            "home" => 0x24, "end" => 0x23, "pageup" => 0x21, "pagedown" => 0x22,
            "up" => 0x26, "down" => 0x28, "left" => 0x25, "right" => 0x27,
            _ when last.Length == 1 => char.ToUpperInvariant(last[0]),
            _ when last.StartsWith('f') && int.TryParse(last[1..], out var f) && f is >= 1 and <= 12 => 0x6F + f,
            _ => 0
        };
        int ch = mods.Count == 0 && last.Length == 1 && vk != 0 ? last[0] : 0;
        if (vk == 0) throw new ArgumentException($"Unknown key: '{key}'");
        return (vk, ch, mods.ToArray());
    }

    // ---------- Scroll ----------

    /// <summary>Scroll: UIA ScrollPattern first, WM_MOUSEWHEEL at element center as fallback.</summary>
    public async Task<InputResult> ScrollAsync(IntPtr hwnd, IUIAutomationElement? element, ElementInfo? info,
        string direction, double pages, CancellationToken ct)
    {
        if (element != null)
        {
            var viaPattern = await _uia.InvokeAsync(() =>
            {
                if (element.GetCurrentPattern(UiaIds.ScrollPattern) is IUIAutomationScrollPattern sp)
                {
                    var (h, v) = direction.ToLowerInvariant() switch
                    {
                        "down" => (ScrollAmount.ScrollAmount_NoAmount, ScrollAmount.ScrollAmount_LargeIncrement),
                        "up" => (ScrollAmount.ScrollAmount_NoAmount, ScrollAmount.ScrollAmount_LargeDecrement),
                        "right" => (ScrollAmount.ScrollAmount_LargeIncrement, ScrollAmount.ScrollAmount_NoAmount),
                        "left" => (ScrollAmount.ScrollAmount_LargeDecrement, ScrollAmount.ScrollAmount_NoAmount),
                        _ => throw new ArgumentException($"direction must be up/down/left/right, got '{direction}'")
                    };
                    int times = Math.Max(1, (int)Math.Ceiling(pages));
                    for (int i = 0; i < times; i++)
                    {
                        sp.Scroll(h, v);
                        Thread.Sleep(40);
                    }
                    return true;
                }
                return false;
            }, ct).ConfigureAwait(false);
            if (viaPattern) return new InputResult(true, "uia_scroll");
        }

        // Fallback: wheel message at element center (or window center)
        int x = info != null ? info.ScreenX + info.Width / 2 : 0;
        int y = info != null ? info.ScreenY + info.Height / 2 : 0;
        if (info == null)
        {
            NativeMethods.GetWindowRect(hwnd, out var r);
            x = r.Left + r.Width / 2; y = r.Top + r.Height / 2;
        }
        return await Task.Run(() =>
        {
            var pt = new NativeMethods.POINT { X = x, Y = y };
            var target = info?.NativeWindowHandle is int nh and > 0 ? (IntPtr)nh : hwnd;
            NativeMethods.ScreenToClient(target, ref pt);
            int delta = (int)(NativeMethods.WHEEL_DELTA * pages) * (direction is "up" or "left" ? 1 : -1);
            uint msg = direction is "left" or "right" ? (uint)NativeMethods.WM_MOUSEHWHEEL : (uint)NativeMethods.WM_MOUSEWHEEL;
            NativeMethods.PostMessageW(target, msg, NativeMethods.WheelWParam(delta), NativeMethods.MakeLParam(x, y));
            return new InputResult(true, "wm_wheel", $"{direction} {pages} pages");
        }, ct).ConfigureAwait(false);
    }

    // ---------- Drag ----------

    /// <summary>Message-based drag with interpolated move steps. Real cursor untouched.</summary>
    public Task<InputResult> DragAsync(IntPtr hwnd, int fromX, int fromY, int toX, int toY, CancellationToken ct)
        => Task.Run(() =>
        {
            var pt = new NativeMethods.POINT { X = fromX, Y = fromY };
            NativeMethods.ScreenToClient(hwnd, ref pt);
            var start = NativeMethods.MakeLParam(pt.X, pt.Y);
            NativeMethods.PostMessageW(hwnd, NativeMethods.WM_MOUSEMOVE, IntPtr.Zero, start);
            NativeMethods.PostMessageW(hwnd, NativeMethods.WM_LBUTTONDOWN, (IntPtr)NativeMethods.MK_LBUTTON, start);
            const int steps = 12;
            for (int i = 1; i <= steps; i++)
            {
                int ix = fromX + (toX - fromX) * i / steps;
                int iy = fromY + (toY - fromY) * i / steps;
                var mp = new NativeMethods.POINT { X = ix, Y = iy };
                NativeMethods.ScreenToClient(hwnd, ref mp);
                NativeMethods.PostMessageW(hwnd, NativeMethods.WM_MOUSEMOVE, (IntPtr)NativeMethods.MK_LBUTTON, NativeMethods.MakeLParam(mp.X, mp.Y));
                Thread.Sleep(20);
            }
            var end = new NativeMethods.POINT { X = toX, Y = toY };
            NativeMethods.ScreenToClient(hwnd, ref end);
            NativeMethods.PostMessageW(hwnd, NativeMethods.WM_LBUTTONUP, IntPtr.Zero, NativeMethods.MakeLParam(end.X, end.Y));
            return new InputResult(true, "postmessage_drag", $"({fromX},{fromY})→({toX},{toY})");
        }, ct);

    // ---------- Set value ----------

    public Task<InputResult> SetValueAsync(IUIAutomationElement element, string value, CancellationToken ct)
        => _uia.InvokeAsync(() =>
        {
            if (element.GetCurrentPattern(UiaIds.ValuePattern) is IUIAutomationValuePattern vp
                && vp.CurrentIsReadOnly == 0)
            {
                vp.SetValue(value);
                return new InputResult(true, "uia_setvalue");
            }
            if (element.GetCurrentPattern(UiaIds.TogglePattern) is IUIAutomationTogglePattern tp
                && bool.TryParse(value, out var on))
            {
                var state = tp.CurrentToggleState;
                bool isOn = state == ToggleState.ToggleState_On;
                if (isOn != on) tp.Toggle();
                return new InputResult(true, "uia_toggle_to", value);
            }
            return new InputResult(false, "none", "element supports neither ValuePattern nor TogglePattern");
        }, ct);
}
