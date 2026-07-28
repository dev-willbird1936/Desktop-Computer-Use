// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
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
    private readonly ConcurrentDictionary<IntPtr, AddressBarInput> _addressBarInputs = new();
    public BackgroundInput(UiaThread uia) => _uia = uia;

    public enum MouseButton { Left, Right, Middle }

    public sealed record InputResult(bool Success, string Method, string Detail = "");
    private sealed record AddressBarInput(string? Text);

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
                if (element.GetCurrentPattern(UiaIds.ExpandCollapsePattern) is IUIAutomationExpandCollapsePattern expand)
                { expand.Expand(); return "uia_expand"; }
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
            if (!NativeMethods.IsWindow(target))
                return new InputResult(false, "none", "target window no longer exists");
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
    /// edit controls, etc.), falling back to hwnd itself. Uses RealChildWindowFromPoint — a
    /// purely local hit test walking hwnd's own child tree — rather than WindowFromPoint, which
    /// is a global z-order query: if hwnd is occluded by an unrelated window at that screen
    /// point (the exact background/occluded-window scenario this tool exists for),
    /// WindowFromPoint returns the occluder, not our target's child. RealChildWindowFromPoint
    /// never looks outside hwnd's own subtree, so occlusion by other windows can't affect it.</summary>
    private static IntPtr ChildWindowFromPoint(IntPtr hwnd, int screenX, int screenY)
    {
        var current = hwnd;
        for (int depth = 0; depth < 32; depth++)
        {
            var pt = new NativeMethods.POINT { X = screenX, Y = screenY };
            if (!NativeMethods.ScreenToClient(current, ref pt)) break;
            var child = NativeMethods.RealChildWindowFromPoint(current, pt);
            if (child == IntPtr.Zero || child == current || !NativeMethods.IsWindow(child)) break;
            current = child;
        }
        return current;
    }

    /// <summary>Resolve the child HWND that should receive input — the element's own
    /// native window handle when it has one (edit controls, Electron sub-windows), else
    /// null so the caller falls back to a point-based lookup (ChildWindowFromPoint).
    /// Chromium DOM elements report NativeWindowHandle == 0 (UIA only populates it for
    /// hwnd-backed fragment roots), so returning the top-level hwnd here — instead of
    /// null — used to skip the point-based descent entirely for every such element.</summary>
    public IntPtr? ResolveInputHwnd(IntPtr hwnd, ElementInfo? info)
    {
        if (info?.NativeWindowHandle is int h and > 0 && h != hwnd.ToInt32())
            return (IntPtr)h;
        return null;
    }

    // ---------- Typing ----------

    /// <summary>
    /// Focus-free text entry cascade:
    /// 1. child edit HWND → EM_SETSEL(end) + EM_REPLACESEL (no focus needed)
    /// 2. key-event stream (WM_KEYDOWN/WM_CHAR/WM_KEYUP) to a render-widget child
    ///    (Chromium/Electron — fires real DOM input events, unlike UIA SetValue)
    /// 3. UIA ValuePattern.SetValue(append) — only if explicitly allowed (can foreground the app)
    /// 4. WM_CHAR stream to the main window
    /// </summary>
    public async Task<InputResult> TypeTextAsync(IntPtr hwnd, string text, CancellationToken ct, bool allowUiaTextFallback = false)
    {
        // Ctrl+L establishes an explicit omnibox target. Chromium has separate native
        // routing for its frame and page render widget, so a key stream aimed at the
        // render widget cannot type into the omnibox selected by the frame accelerator.
        // Replace the address value through UIA and verify the observable value instead.
        if (_addressBarInputs.ContainsKey(hwnd))
        {
            var addressValue = await _uia.InvokeAsync(() =>
            {
                var address = FindChromiumAddressBar(hwnd);
                if (address?.GetCurrentPattern(UiaIds.ValuePattern) is not IUIAutomationValuePattern value
                    || value.CurrentIsReadOnly != 0)
                    return (Found: false, Value: "");
                value.SetValue(text);
                return (Found: true, Value: value.CurrentValue ?? "");
            }, ct).ConfigureAwait(false);
            if (!addressValue.Found)
            {
                _addressBarInputs.TryRemove(hwnd, out _);
                return new InputResult(false, "uia_address_value", "Chrome address bar disappeared before text entry");
            }
            if (!string.Equals(addressValue.Value, text, StringComparison.Ordinal))
            {
                _addressBarInputs.TryRemove(hwnd, out _);
                return new InputResult(false, "uia_address_value",
                    $"Chrome address bar did not accept the complete value ({addressValue.Value.Length}/{text.Length} characters)");
            }
            _addressBarInputs[hwnd] = new AddressBarInput(text);
            return new InputResult(true, "uia_address_value", $"verified {text.Length} characters");
        }

        // Tier 1: find a text-entry child HWND (edit/richtext/document) and EM_REPLACESEL into it
        var editHwnd = await _uia.InvokeAsync(() => FindTextEntryHwnd(hwnd), ct).ConfigureAwait(false);
        if (editHwnd != IntPtr.Zero)
        {
            // (-1,-1) is NOT "caret to end" — that's EM_SETSEL's documented "deselect in
            // place" case, which leaves the caret wherever it already was (offset 0 on a
            // control we never focused), so EM_REPLACESEL inserted at the START instead of
            // appending. WM_GETTEXTLENGTH must be sent as a message (not GetWindowTextLength,
            // which skips messaging entirely for windows outside our process and returns a
            // stale/zero cached caption length) to find the real end offset first.
            if (!TrySendMessageWithTimeout(editHwnd, NativeMethods.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, out var lengthResult))
                return new InputResult(false, "em_replacesel", "target did not respond while reading text length");
            int len = checked((int)lengthResult.ToInt64());
            if (!TrySendMessageWithTimeout(editHwnd, NativeMethods.EM_SETSEL, (IntPtr)len, (IntPtr)len, out _))
                return new InputResult(false, "em_replacesel", "target did not respond while setting text selection");
            if (!TrySendMessageWithTimeout(editHwnd, NativeMethods.EM_REPLACESEL, (IntPtr)1, text, out var replaceResult))
                return new InputResult(false, "em_replacesel", "target did not respond while replacing text");
            return new InputResult(true, "em_replacesel",
                replaceResult == IntPtr.Zero
                    ? $"hwnd=0x{editHwnd.ToInt64():X}"
                    : $"hwnd=0x{editHwnd.ToInt64():X} (rc={replaceResult})");
        }

        // Tier 2: key-event stream into a render widget (Chromium/Electron) — the DOM
        // sees genuine key/input events, so web forms register the text
        var renderHwnd = await _uia.InvokeAsync(() => FindRenderWidgetHwnd(hwnd), ct).ConfigureAwait(false);
        if (renderHwnd != IntPtr.Zero)
        {
            await Task.Run(() =>
            {
                foreach (char c in text)
                {
                    short vk = NativeMethods.VkKeyScanW((ushort)c);
                    if (vk != -1)
                    {
                        int key = vk & 0xFF;
                        bool shift = (vk & 0x100) != 0;
                        if (shift) NativeMethods.PostMessageW(renderHwnd, NativeMethods.WM_KEYDOWN, (IntPtr)NativeMethods.VK_SHIFT, IntPtr.Zero);
                        NativeMethods.PostMessageW(renderHwnd, NativeMethods.WM_KEYDOWN, (IntPtr)key, IntPtr.Zero);
                        NativeMethods.PostMessageW(renderHwnd, NativeMethods.WM_CHAR, (IntPtr)c, IntPtr.Zero);
                        NativeMethods.PostMessageW(renderHwnd, NativeMethods.WM_KEYUP, (IntPtr)key, IntPtr.Zero);
                        if (shift) NativeMethods.PostMessageW(renderHwnd, NativeMethods.WM_KEYUP, (IntPtr)NativeMethods.VK_SHIFT, IntPtr.Zero);
                    }
                    else
                    {
                        NativeMethods.PostMessageW(renderHwnd, NativeMethods.WM_CHAR, (IntPtr)c, IntPtr.Zero);
                    }
                    Thread.Sleep(4);
                }
            }, ct).ConfigureAwait(false);
            return new InputResult(true, "render_widget_keys", $"hwnd=0x{renderHwnd.ToInt64():X}");
        }

        // Tier 3: UIA SetValue append (gated — observed to foreground some apps)
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

        // Tier 4: WM_CHAR stream to main window
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

    /// <summary>Find a Chromium/Electron render-widget child window (the HWND that
    /// actually receives keyboard input), if this is that kind of app.</summary>
    private IntPtr FindRenderWidgetHwnd(IntPtr hwnd)
    {
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumChildWindows(hwnd, (child, _) =>
        {
            var cls = NativeMethods.GetClassNameString(child);
            if (cls.Contains("RenderWidgetHost", StringComparison.OrdinalIgnoreCase))
            {
                if (NativeMethods.IsWindowVisible(child)) { found = child; return false; }
                if (found == IntPtr.Zero) found = child;
            }
            return true;
        }, IntPtr.Zero);
        return found;
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
            if (focused != null
                && CanUseFocusedElement(root.CurrentProcessId, focused.CurrentProcessId)
                && IsElementWithinRoot(uia, root, focused)
                && focused.GetCurrentPattern(UiaIds.ValuePattern) is IUIAutomationValuePattern fvp
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

    private const uint WindowMessageTimeoutMs = 1_000;

    private static bool TrySendMessageWithTimeout(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        out IntPtr result)
        => NativeMethods.SendMessageTimeoutW(
               hwnd,
               message,
               wParam,
               lParam,
               NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_ERRORONEXIT,
               WindowMessageTimeoutMs,
               out result) != IntPtr.Zero;

    private static bool TrySendMessageWithTimeout(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        string lParam,
        out IntPtr result)
        => NativeMethods.SendMessageTimeoutW(
               hwnd,
               message,
               wParam,
               lParam,
               NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_ERRORONEXIT,
               WindowMessageTimeoutMs,
               out result) != IntPtr.Zero;

    private static bool CanUseFocusedElement(int targetProcessId, int focusedProcessId)
        => targetProcessId > 0 && targetProcessId == focusedProcessId;

    private static bool IsElementWithinRoot(
        IUIAutomation uia,
        IUIAutomationElement root,
        IUIAutomationElement candidate)
    {
        var walker = uia.ControlViewWalker;
        IUIAutomationElement? current = candidate;
        for (int depth = 0; current != null && depth < 128; depth++)
        {
            if (uia.CompareElements(current, root) != 0) return true;
            current = walker.GetParentElement(current);
        }
        return false;
    }

    /// <summary>Find Chrome/Chromium's native omnibox accessibility element. The stable
    /// view automation id is preferred so this also works when the accessible name is
    /// localized; the English name is retained as a compatibility fallback.</summary>
    private static IUIAutomationElement? FindChromiumAddressBar(IntPtr hwnd)
    {
        if (!NativeMethods.GetClassNameString(hwnd).Contains("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase))
            return null;
        var uia = new CUIAutomation8();
        var root = uia.ElementFromHandle(hwnd);
        var edits = root.FindAll(
            TreeScope.TreeScope_Descendants,
            uia.CreatePropertyCondition(UiaIds.ControlTypeProperty, 50004)); // Edit
        IUIAutomationElement? namedFallback = null;
        for (int i = 0; i < edits.Length; i++)
        {
            var element = edits.GetElement(i);
            try
            {
                if (element.CurrentAutomationId.Equals("view_1012", StringComparison.OrdinalIgnoreCase))
                    return element;
                if (element.CurrentName.Contains("Address and search bar", StringComparison.OrdinalIgnoreCase))
                    namedFallback = element;
            }
            catch { /* element disappeared while walking */ }
        }
        return namedFallback;
    }

    // ---------- Keys ----------

    /// <summary>Key press via posted WM_KEYDOWN/WM_KEYUP (+WM_CHAR for text keys). No focus steal.</summary>
    public async Task<InputResult> PressKeyAsync(IntPtr hwnd, string key, CancellationToken ct)
    {
        var (vk, ch, mods) = ParseKey(key);
        bool isSelectAddress = vk == 'L' && mods.Length == 1 && mods[0] == NativeMethods.VK_CONTROL;
        if (isSelectAddress)
        {
            var selected = await _uia.InvokeAsync(() =>
            {
                var address = FindChromiumAddressBar(hwnd);
                if (address == null) return false;
                address.SetFocus();
                return true;
            }, ct).ConfigureAwait(false);
            if (selected)
            {
                _addressBarInputs[hwnd] = new AddressBarInput(null);
                return new InputResult(true, "uia_address_focus", "Chrome address bar selected");
            }
        }

        if (vk == 0x0D && mods.Length == 0 && _addressBarInputs.TryGetValue(hwnd, out var pending))
        {
            if (string.IsNullOrWhiteSpace(pending.Text))
            {
                _addressBarInputs.TryRemove(hwnd, out _);
                return new InputResult(false, "uia_address_navigate", "No verified address-bar text is pending");
            }

            var addressFocused = await _uia.InvokeAsync(() =>
            {
                var address = FindChromiumAddressBar(hwnd);
                if (address == null) return false;
                address.SetFocus();
                return true;
            }, ct).ConfigureAwait(false);
            if (!addressFocused)
            {
                _addressBarInputs.TryRemove(hwnd, out _);
                return new InputResult(false, "uia_address_navigate", "Chrome address bar disappeared before navigation");
            }

            bool posted = await Task.Run(() => PostKeyMessages(hwnd, vk, ch, mods), ct).ConfigureAwait(false);
            if (!posted)
            {
                _addressBarInputs.TryRemove(hwnd, out _);
                return new InputResult(false, "uia_address_navigate", "PostMessage rejected the Return key");
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                bool navigated = await _uia.InvokeAsync(
                    () => ChromiumDocumentHasUrl(hwnd, pending.Text), ct).ConfigureAwait(false);
                if (navigated)
                {
                    _addressBarInputs.TryRemove(hwnd, out _);
                    return new InputResult(true, "uia_address_navigate", "RootWebArea URL verified");
                }
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
            _addressBarInputs.TryRemove(hwnd, out _);
            return new InputResult(false, "uia_address_navigate",
                "Return was posted, but Chrome RootWebArea did not reach the requested URL within 5 seconds");
        }

        bool accepted = await Task.Run(() => PostKeyMessages(hwnd, vk, ch, mods), ct).ConfigureAwait(false);
        return accepted
            ? new InputResult(true, "postmessage_key", key)
            : new InputResult(false, "postmessage_key", "PostMessage rejected one or more key messages");
    }

    private static bool PostKeyMessages(IntPtr hwnd, int vk, int ch, int[] mods)
    {
        bool accepted = true;
        foreach (var m in mods)
            accepted &= NativeMethods.PostMessageW(hwnd, NativeMethods.WM_KEYDOWN, (IntPtr)m, IntPtr.Zero);
        accepted &= NativeMethods.PostMessageW(hwnd, NativeMethods.WM_KEYDOWN, (IntPtr)vk, IntPtr.Zero);
        Thread.Sleep(25);
        if (ch != 0)
            accepted &= NativeMethods.PostMessageW(hwnd, NativeMethods.WM_CHAR, (IntPtr)ch, IntPtr.Zero);
        accepted &= NativeMethods.PostMessageW(hwnd, NativeMethods.WM_KEYUP, (IntPtr)vk, IntPtr.Zero);
        foreach (var m in mods.Reverse())
            accepted &= NativeMethods.PostMessageW(hwnd, NativeMethods.WM_KEYUP, (IntPtr)m, IntPtr.Zero);
        return accepted;
    }

    private static bool ChromiumDocumentHasUrl(IntPtr hwnd, string expected)
    {
        var uia = new CUIAutomation8();
        var root = uia.ElementFromHandle(hwnd);
        var documents = root.FindAll(
            TreeScope.TreeScope_Descendants,
            uia.CreatePropertyCondition(UiaIds.ControlTypeProperty, 50030)); // Document
        for (int i = 0; i < documents.Length; i++)
        {
            try
            {
                if (documents.GetElement(i).GetCurrentPattern(UiaIds.ValuePattern) is IUIAutomationValuePattern value
                    && UrlsEqual(value.CurrentValue, expected))
                    return true;
            }
            catch { /* document changed during navigation */ }
        }
        return false;
    }

    private static bool UrlsEqual(string? actual, string expected)
    {
        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return true;
        return Uri.TryCreate(actual, UriKind.Absolute, out var actualUri)
            && Uri.TryCreate(expected, UriKind.Absolute, out var expectedUri)
            && actualUri.Equals(expectedUri);
    }

    private static (int vk, int ch, int[] mods) ParseKey(string key)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("key must not be empty");
        var mods = new List<int>();
        // Split on '+'/'-' only when followed by another character, so a trailing lone
        // '+' or '-' (e.g. "ctrl++" / "ctrl+-" — the zoom-in/zoom-out shortcuts) is kept
        // as the literal key instead of being consumed as a separator with nothing after it.
        var parts = Regex.Split(key.ToLowerInvariant(), "[+-](?=.)");
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
            "return" or "enter" => 0x0D,
            "tab" => 0x09,
            "esc" or "escape" => 0x1B,
            "space" => 0x20,
            "back" or "backspace" => 0x08,
            "delete" => 0x2E,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            "up" => 0x26,
            "down" => 0x28,
            "left" => 0x25,
            "right" => 0x27,
            _ when last.Length == 1 && char.IsLetterOrDigit(last[0]) => char.ToUpperInvariant(last[0]),
            _ when last.StartsWith('f') && int.TryParse(last[1..], out var f) && f is >= 1 and <= 12 => 0x6F + f,
            _ => 0
        };
        // Punctuation single chars: char.ToUpperInvariant only makes sense for letters — it
        // previously mapped '.' to VK_DELETE (0x2E), ',' to VK_SNAPSHOT, '\'' to VK_RIGHT, etc.
        // (whatever virtual-key numeric value happened to equal the uppercased char code).
        // VkKeyScanW maps a character to the actual key + shift state that produces it on
        // the current keyboard layout.
        if (vk == 0 && last.Length == 1 && !char.IsLetterOrDigit(last[0]))
        {
            short scan = NativeMethods.VkKeyScanW((ushort)last[0]);
            if (scan != -1)
            {
                vk = scan & 0xFF;
                int shiftState = (scan >> 8) & 0xFF;
                if ((shiftState & 1) != 0 && !mods.Contains(NativeMethods.VK_SHIFT)) mods.Add(NativeMethods.VK_SHIFT);
                if ((shiftState & 2) != 0 && !mods.Contains(NativeMethods.VK_CONTROL)) mods.Add(NativeMethods.VK_CONTROL);
                if ((shiftState & 4) != 0 && !mods.Contains(NativeMethods.VK_MENU)) mods.Add(NativeMethods.VK_MENU);
            }
        }
        int ch = mods.Count == 0 && last.Length == 1 && vk != 0 ? last[0] : 0;
        if (vk == 0) throw new ArgumentException($"Unknown key: '{key}'");
        return (vk, ch, mods.ToArray());
    }

    // ---------- Scroll ----------

    private static readonly string[] ValidDirections = ["up", "down", "left", "right"];

    /// <summary>Scroll: UIA ScrollPattern first, wheel messages as fallback.</summary>
    public async Task<InputResult> ScrollAsync(IntPtr hwnd, IUIAutomationElement? element, ElementInfo? info,
        string direction, double pages, CancellationToken ct)
    {
        direction = direction.ToLowerInvariant();
        if (!ValidDirections.Contains(direction))
            return new InputResult(false, "none", $"direction must be up/down/left/right, got '{direction}'");

        if (element != null)
        {
            var viaPattern = await _uia.InvokeAsync(() =>
            {
                if (element.GetCurrentPattern(UiaIds.ScrollPattern) is IUIAutomationScrollPattern sp)
                {
                    var (h, v) = direction switch
                    {
                        "down" => (ScrollAmount.ScrollAmount_NoAmount, ScrollAmount.ScrollAmount_LargeIncrement),
                        "up" => (ScrollAmount.ScrollAmount_NoAmount, ScrollAmount.ScrollAmount_LargeDecrement),
                        "right" => (ScrollAmount.ScrollAmount_LargeIncrement, ScrollAmount.ScrollAmount_NoAmount),
                        _ => (ScrollAmount.ScrollAmount_LargeDecrement, ScrollAmount.ScrollAmount_NoAmount),
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

        // Fallback: wheel messages at element center (or window center).
        // WM_MOUSEWHEEL lParam is SCREEN coordinates (not client), and the message must
        // reach the content window (render widget), not the top-level frame.
        int x = info != null ? info.ScreenX + info.Width / 2 : 0;
        int y = info != null ? info.ScreenY + info.Height / 2 : 0;
        if (info == null)
        {
            NativeMethods.GetWindowRect(hwnd, out var r);
            x = r.Left + r.Width / 2; y = r.Top + r.Height / 2;
        }
        return await Task.Run(() =>
        {
            var target = info?.NativeWindowHandle is int nh and > 0 ? (IntPtr)nh : ChildWindowFromPoint(hwnd, x, y);
            int notches = Math.Max(1, (int)Math.Ceiling(pages * 3)); // ~3 notches per "page"
            uint msg = direction is "left" or "right" ? (uint)NativeMethods.WM_MOUSEHWHEEL : (uint)NativeMethods.WM_MOUSEWHEEL;
            // Vertical: positive delta = wheel tilted away from the user = scroll up.
            // Horizontal: positive delta = wheel tilted right = scroll right (not left —
            // this was inverted before, so "left" scrolled right and vice versa).
            int sign = direction is "up" or "right" ? 1 : -1;
            var screenLParam = NativeMethods.MakeLParam(x, y);
            for (int i = 0; i < notches; i++)
            {
                NativeMethods.PostMessageW(target, msg, NativeMethods.WheelWParam(NativeMethods.WHEEL_DELTA * sign), screenLParam);
                Thread.Sleep(30);
            }
            return new InputResult(true, "wm_wheel", $"{direction} {pages} pages ({notches} notches)");
        }, ct).ConfigureAwait(false);
    }

    // ---------- Drag ----------

    /// <summary>Message-based drag with interpolated move steps. Real cursor untouched.
    /// Resolves the actual child window under the drag's start point once, occlusion-safe
    /// (same descent as ClickAtAsync), and posts the whole down/move/up stream to that one
    /// window — re-descending per step could switch recipients mid-drag.</summary>
    public Task<InputResult> DragAsync(IntPtr hwnd, int fromX, int fromY, int toX, int toY, CancellationToken ct)
        => Task.Run(() =>
        {
            var target = ChildWindowFromPoint(hwnd, fromX, fromY);
            if (!NativeMethods.IsWindow(target))
                return new InputResult(false, "none", "target window no longer exists");

            var pt = new NativeMethods.POINT { X = fromX, Y = fromY };
            NativeMethods.ScreenToClient(target, ref pt);
            var start = NativeMethods.MakeLParam(pt.X, pt.Y);
            NativeMethods.PostMessageW(target, NativeMethods.WM_MOUSEMOVE, IntPtr.Zero, start);
            NativeMethods.PostMessageW(target, NativeMethods.WM_LBUTTONDOWN, (IntPtr)NativeMethods.MK_LBUTTON, start);
            const int steps = 12;
            for (int i = 1; i <= steps; i++)
            {
                int ix = fromX + (toX - fromX) * i / steps;
                int iy = fromY + (toY - fromY) * i / steps;
                var mp = new NativeMethods.POINT { X = ix, Y = iy };
                NativeMethods.ScreenToClient(target, ref mp);
                NativeMethods.PostMessageW(target, NativeMethods.WM_MOUSEMOVE, (IntPtr)NativeMethods.MK_LBUTTON, NativeMethods.MakeLParam(mp.X, mp.Y));
                Thread.Sleep(20);
            }
            var end = new NativeMethods.POINT { X = toX, Y = toY };
            NativeMethods.ScreenToClient(target, ref end);
            NativeMethods.PostMessageW(target, NativeMethods.WM_LBUTTONUP, IntPtr.Zero, NativeMethods.MakeLParam(end.X, end.Y));
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
