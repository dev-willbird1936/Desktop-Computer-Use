// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Interop.UIAutomationClient;
using ShadowUse.Native;

namespace ShadowUse.Automation;

/// <summary>One interactive element in a snapshot, addressable by label id.</summary>
public sealed class ElementInfo
{
    public required string Id;              // session id e.g. "e42"
    public int[] RuntimeId = [];
    public string AutomationId = "";
    public string Name = "";
    public string ControlType = "";
    public string ClassName = "";
    public string Value = "";
    public int NativeWindowHandle;
    public int X, Y, Width, Height;         // window-relative frame
    public int ScreenX, ScreenY;            // absolute frame origin
    public string[] Actions = [];
}

public readonly record struct ElementFrame(
    int ScreenX,
    int ScreenY,
    int Width,
    int Height,
    int NativeWindowHandle)
{
    public int CenterX => ScreenX + Width / 2;
    public int CenterY => ScreenY + Height / 2;
}

public sealed class Snapshot
{
    public required int Revision;
    public required string App;
    public required int Pid;
    public required IntPtr Hwnd;
    public required string Title;
    public required NativeMethods.RECT Bounds;
    public List<ElementInfo> Elements = [];
    public string TreeText = "";
    public DateTimeOffset CreatedAt = DateTimeOffset.UtcNow;
}

/// <summary>Resolved target app.</summary>
public sealed class AppTarget
{
    public required int Pid;
    public required string ProcessName;
    public required IntPtr Hwnd;
    public required string Title;
    public string WindowId => $"{Pid}:0x{Hwnd.ToInt64():X}";
}

internal sealed class SnapshotCache
{
    private readonly ConcurrentDictionary<IntPtr, Snapshot> _byWindow = new();

    public void Store(Snapshot snapshot) => _byWindow[snapshot.Hwnd] = snapshot;

    public void RemoveInvalidWindows(Func<IntPtr, bool> isWindow)
    {
        foreach (var hwnd in _byWindow.Keys)
            if (!isWindow(hwnd))
                _byWindow.TryRemove(hwnd, out _);
    }

    public Snapshot? GetForTarget(AppTarget target)
        => _byWindow.TryGetValue(target.Hwnd, out var snapshot)
            && snapshot.Pid == target.Pid
            ? snapshot
            : null;

    public Snapshot? GetForElement(AppTarget target, string elementId)
    {
        var targetSnapshot = GetForTarget(target);
        if (targetSnapshot != null)
            return targetSnapshot.Elements.Any(element => element.Id == elementId)
                ? targetSnapshot
                : null;
        return _byWindow.Values.SingleOrDefault(snapshot =>
            snapshot.Pid == target.Pid
            && snapshot.Elements.Any(element => element.Id == elementId));
    }
}

/// <summary>
/// UI Automation engine: app resolution, accessibility tree snapshots with
/// Set-of-Marks labeling, and element re-resolution. All COM calls run on <see cref="UiaThread"/>.
/// </summary>
public sealed class UiaService
{
    private readonly UiaThread _uia;
    private readonly SnapshotCache _snapshots = new();
    private int _revision;
    private int _elementCounter;

    public UiaService(UiaThread uia) => _uia = uia;

    private static IUIAutomation CreateUia()
    {
        try { return new CUIAutomation8(); }
        catch { return new CUIAutomation(); }
    }

    // ---------- App resolution ----------

    public Task<AppTarget[]> ListAppsAsync(CancellationToken ct = default)
        => _uia.InvokeAsync(() =>
        {
            var list = new List<AppTarget>();
            var processNames = new Dictionary<int, string>();
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                if (NativeMethods.DwmGetWindowAttribute(
                        hwnd,
                        NativeMethods.DWMWA_CLOAKED,
                        out int cloaked,
                        sizeof(int)) == 0
                    && cloaked != 0)
                    return true;
                var title = NativeMethods.GetWindowTextString(hwnd);
                if (string.IsNullOrWhiteSpace(title)) return true;
                NativeMethods.GetWindowThreadProcessId(hwnd, out var rawPid);
                int pid = unchecked((int)rawPid);
                if (pid <= 0) return true;
                try
                {
                    if (!processNames.TryGetValue(pid, out var processName))
                    {
                        using var process = Process.GetProcessById(pid);
                        processName = process.ProcessName;
                        processNames[pid] = processName;
                    }
                    list.Add(new AppTarget
                    {
                        Pid = pid,
                        ProcessName = processName,
                        Hwnd = hwnd,
                        Title = title,
                    });
                }
                catch { /* process exited or is inaccessible */ }
                return true;
            }, IntPtr.Zero);
            _snapshots.RemoveInvalidWindows(NativeMethods.IsWindow);
            return list.ToArray();
        }, ct);

    public async Task<AppTarget> ResolveAppAsync(string app, CancellationToken ct = default)
    {
        var apps = await ListAppsAsync(ct).ConfigureAwait(false);
        return SelectApp(apps, app);
    }

    private static AppTarget SelectApp(AppTarget[] apps, string app)
    {
        AppTarget? match = null;
        if (TryParseWindowId(app, out var windowPid, out var windowHandle))
            match = apps.SingleOrDefault(a => a.Pid == windowPid && a.Hwnd == windowHandle);
        else if (int.TryParse(app, out var pid))
            match = SelectSingle(apps.Where(a => a.Pid == pid));
        var appNoExeSuffix = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;
        if (match == null)
        {
            match = SelectSingle(apps.Where(a =>
                        a.ProcessName.Equals(app, StringComparison.OrdinalIgnoreCase)
                     || a.ProcessName.Equals(appNoExeSuffix, StringComparison.OrdinalIgnoreCase)))
                ?? SelectSingle(apps.Where(a => a.Title.Equals(app, StringComparison.OrdinalIgnoreCase)))
                ?? SelectSingle(apps.Where(a => a.Title.Contains(app, StringComparison.OrdinalIgnoreCase)))
                ?? SelectSingle(apps.Where(a => a.ProcessName.Contains(app, StringComparison.OrdinalIgnoreCase)));
        }
        if (match == null)
            throw new InvalidOperationException($"App not found: '{app}'. Running apps: {string.Join(", ", apps.Take(20).Select(a => $"{a.ProcessName} ({a.Pid})"))}");
        return match;

        AppTarget? SelectSingle(IEnumerable<AppTarget> candidates)
        {
            var matches = candidates.ToArray();
            if (matches.Length > 1)
                throw new InvalidOperationException(
                    $"App selector '{app}' is ambiguous. Use window_id: {string.Join(", ", matches.Select(a => $"{a.Title} (PID {a.Pid}, window_id {a.WindowId})"))}");
            return matches.SingleOrDefault();
        }
    }

    private static bool TryParseWindowId(string selector, out int pid, out IntPtr hwnd)
    {
        pid = 0;
        hwnd = IntPtr.Zero;
        int separator = selector.IndexOf(":0x", StringComparison.OrdinalIgnoreCase);
        if (separator <= 0
            || !int.TryParse(selector[..separator], out pid)
            || !long.TryParse(
                selector[(separator + 3)..],
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var rawHwnd))
            return false;
        hwnd = (IntPtr)rawHwnd;
        return true;
    }

    // ---------- Snapshot ----------

    // Ids must match ControlTypeName below exactly (they previously drifted, silently
    // swapping in the wrong control types — e.g. Image instead of Hyperlink).
    private static readonly HashSet<int> InteractiveTypes =
    [
        50000, // Button
        50001, // Calendar
        50002, // CheckBox
        50003, // ComboBox
        50004, // Edit
        50005, // Hyperlink
        50007, // ListItem
        50009, // Menu
        50010, // MenuBar
        50011, // MenuItem
        50012, // ProgressBar
        50013, // RadioButton
        50015, // Slider
        50016, // Spinner
        50018, // Tab
        50019, // TabItem
        50023, // Tree
        50024, // TreeItem
        50025, // Custom
        50026, // Group
        50028, // DataGrid
        50029, // DataItem
        50030, // Document
        50031, // SplitButton
        50033, // Pane
    ];

    public Task<Snapshot> SnapshotAsync(AppTarget app, int maxNodes = 1200, int maxDepth = 48, int textLimit = 500, CancellationToken ct = default)
        => _uia.InvokeAsync(() =>
        {
            var uia = CreateUia();
            IUIAutomationElement root = uia.ElementFromHandle(app.Hwnd);
            var revision = Interlocked.Increment(ref _revision);
            NativeMethods.GetWindowRect(app.Hwnd, out var bounds);

            var snap = new Snapshot
            {
                Revision = revision,
                App = app.ProcessName,
                Pid = app.Pid,
                Hwnd = app.Hwnd,
                Title = app.Title,
                Bounds = bounds,
            };

            var seen = new HashSet<string>();
            var sb = new System.Text.StringBuilder();
            int nodes = 0;

            // Control-view walk with per-field fault isolation (one broken element never kills the snapshot)
            void Walk(IUIAutomationElement el, int depth)
            {
                if (nodes >= maxNodes || depth > maxDepth) return;
                nodes++;
                int[] runtimeId;
                try { runtimeId = (int[])el.GetRuntimeId(); }
                catch { return; }
                string key = string.Join(",", runtimeId);
                if (!seen.Add(key)) return;

                int controlType = Safe(() => el.CurrentControlType);
                string name = SafeStr(() => el.CurrentName);
                string automationId = SafeStr(() => el.CurrentAutomationId);
                string className = SafeStr(() => el.CurrentClassName);
                bool offscreen = Safe(() => el.CurrentIsOffscreen) == 1;
                var r = SafeRect(el);
                bool interactive = InteractiveTypes.Contains(controlType) || HasActionablePattern(el);
                // informative text (article paragraphs, labels) — readable content, not clickable
                bool informative = controlType == 50020 && name.Length >= 30;

                if ((interactive || informative) && !offscreen && (r.right - r.left) > 0 && (r.bottom - r.top) > 0)
                {
                    var id = "e" + Interlocked.Increment(ref _elementCounter);
                    var info = new ElementInfo
                    {
                        Id = id,
                        RuntimeId = runtimeId,
                        AutomationId = automationId,
                        Name = name,
                        ControlType = ControlTypeName(controlType),
                        ClassName = className,
                        Value = GetValue(uia, el),
                        NativeWindowHandle = (int)Safe(() => el.CurrentNativeWindowHandle),
                        X = r.left - bounds.Left,
                        Y = r.top - bounds.Top,
                        Width = r.right - r.left,
                        Height = r.bottom - r.top,
                        ScreenX = r.left,
                        ScreenY = r.top,
                        Actions = GetPatternNames(el),
                    };
                    snap.Elements.Add(info);
                    sb.AppendLine($"[{id}] {info.ControlType} \"{Truncate(name, 60)}\" @({info.X},{info.Y} {info.Width}x{info.Height})" +
                        (info.Actions.Length > 0 ? $" actions:{string.Join("|", info.Actions)}" : "") +
                        (!string.IsNullOrEmpty(info.Value) ? $" value:\"{Truncate(info.Value, 80)}\"" : ""));
                }

                IUIAutomationElementArray? children = null;
                try { children = el.FindAll(TreeScope.TreeScope_Children, uia.CreateTrueCondition()); }
                catch { return; }
                if (children == null) return;
                int count = children.Length;
                for (int i = 0; i < count && nodes < maxNodes; i++)
                {
                    try { Walk(children.GetElement(i), depth + 1); }
                    catch { /* skip broken child */ }
                }
            }

            Walk(root, 0);
            snap.TreeText = sb.ToString();
            if (snap.TreeText.Length > textLimit * 40) snap.TreeText = snap.TreeText[..(textLimit * 40)] + "\n... (truncated)";
            // Store by HWND so multiple independent top-level windows from one process
            // retain separate element-id namespaces and snapshots.
            _snapshots.Store(snap);
            return snap;
        }, ct);

    /// <summary>Cached snapshot for the exact resolved top-level window.</summary>
    public Snapshot? GetSnapshot(AppTarget target)
    {
        return _snapshots.GetForTarget(target);
    }

    /// <summary>Snapshot containing an element id for this target. If the resolved
    /// window already has a snapshot, ids from sibling windows are rejected. A
    /// same-process fallback is used only for a transient replacement window that
    /// has no snapshot of its own.</summary>
    public Snapshot? GetSnapshotForElement(AppTarget target, string elementId)
        => _snapshots.GetForElement(target, elementId);

    // ---------- Element resolution ----------

    public Task<IUIAutomationElement?> ResolveElementAsync(IntPtr hwnd, ElementInfo info, CancellationToken ct = default)
        => _uia.InvokeAsync(() =>
        {
            var uia = CreateUia();
            var root = uia.ElementFromHandle(hwnd);
            var all = root.FindAll(TreeScope.TreeScope_Descendants, uia.CreateTrueCondition());
            // Primary: runtimeId exact match
            for (int i = 0; i < all.Length; i++)
            {
                var el = all.GetElement(i);
                int[] rid;
                try { rid = (int[])el.GetRuntimeId(); } catch { continue; }
                if (rid.SequenceEqual(info.RuntimeId)) return el;
            }
            // Fallback: (AutomationId OR Name) AND ControlType
            for (int i = 0; i < all.Length; i++)
            {
                var el = all.GetElement(i);
                try
                {
                    bool ctMatch = ControlTypeName(el.CurrentControlType) == info.ControlType;
                    bool idMatch = !string.IsNullOrEmpty(info.AutomationId) && el.CurrentAutomationId == info.AutomationId;
                    bool nameMatch = !string.IsNullOrEmpty(info.Name) && el.CurrentName == info.Name;
                    if (ctMatch && (idMatch || nameMatch)) return el;
                }
                catch { }
            }
            return (IUIAutomationElement?)null;
        }, ct);

    public Task<ElementFrame?> GetElementFrameAsync(
        IUIAutomationElement element,
        CancellationToken ct = default)
        => _uia.InvokeAsync(() =>
        {
            try
            {
                var rectangle = element.CurrentBoundingRectangle;
                int width = rectangle.right - rectangle.left;
                int height = rectangle.bottom - rectangle.top;
                if (width <= 0 || height <= 0) return (ElementFrame?)null;
                return (ElementFrame?)new ElementFrame(
                    rectangle.left,
                    rectangle.top,
                    width,
                    height,
                    element.CurrentNativeWindowHandle.ToInt32());
            }
            catch
            {
                return (ElementFrame?)null;
            }
        }, ct);

    public Task<IUIAutomationElement?> FindByTextAsync(IntPtr hwnd, string text, CancellationToken ct = default)
        => _uia.InvokeAsync(() =>
        {
            var uia = CreateUia();
            var root = uia.ElementFromHandle(hwnd);
            var all = root.FindAll(TreeScope.TreeScope_Descendants, uia.CreateTrueCondition());
            IUIAutomationElement? partial = null;
            for (int i = 0; i < all.Length; i++)
            {
                var el = all.GetElement(i);
                string name;
                try { name = el.CurrentName; } catch { continue; }
                if (name.Equals(text, StringComparison.OrdinalIgnoreCase)) return el;
                if (partial == null && name.Contains(text, StringComparison.OrdinalIgnoreCase)) partial = el;
            }
            return partial;
        }, ct);

    // ---------- Pattern helpers ----------

    public static string[] GetPatternNames(IUIAutomationElement el)
    {
        var names = new List<string>();
        void Has(int pid, string name)
        {
            try { if (el.GetCurrentPattern(pid) != null) names.Add(name); } catch { }
        }
        Has(UiaIds.InvokePattern, "Invoke");
        Has(UiaIds.TogglePattern, "Toggle");
        Has(UiaIds.SelectionItemPattern, "Select");
        Has(UiaIds.ExpandCollapsePattern, "ExpandCollapse");
        Has(UiaIds.ScrollPattern, "Scroll");
        Has(UiaIds.ScrollItemPattern, "ScrollIntoView");
        Has(UiaIds.ValuePattern, "Value");
        return names.ToArray();
    }

    private static bool HasActionablePattern(IUIAutomationElement el)
    {
        try
        {
            return el.GetCurrentPattern(UiaIds.InvokePattern) != null
                  || el.GetCurrentPattern(UiaIds.TogglePattern) != null
                  || el.GetCurrentPattern(UiaIds.SelectionItemPattern) != null;
        }
        catch { return false; }
    }

    public static string GetValue(IUIAutomation uia, IUIAutomationElement el)
    {
        try
        {
            if (el.GetCurrentPattern(UiaIds.ValuePattern) is IUIAutomationValuePattern vp)
                return vp.CurrentValue ?? "";
        }
        catch { }
        return "";
    }

    private static T Safe<T>(Func<T> f) { try { return f(); } catch { return default!; } }
    private static string SafeStr(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }
    private static tagRECT SafeRect(IUIAutomationElement el)
    {
        try { return el.CurrentBoundingRectangle; } catch { return new tagRECT(); }
    }

    public static string ControlTypeName(int id) => id switch
    {
        50000 => "Button",
        50001 => "Calendar",
        50002 => "CheckBox",
        50003 => "ComboBox",
        50004 => "Edit",
        50005 => "Hyperlink",
        50006 => "Image",
        50007 => "ListItem",
        50008 => "List",
        50009 => "Menu",
        50010 => "MenuBar",
        50011 => "MenuItem",
        50012 => "ProgressBar",
        50013 => "RadioButton",
        50014 => "ScrollBar",
        50015 => "Slider",
        50016 => "Spinner",
        50017 => "StatusBar",
        50018 => "Tab",
        50019 => "TabItem",
        50020 => "Text",
        50021 => "ToolBar",
        50022 => "ToolTip",
        50023 => "Tree",
        50024 => "TreeItem",
        50025 => "Custom",
        50026 => "Group",
        50027 => "Thumb",
        50028 => "DataGrid",
        50029 => "DataItem",
        50030 => "Document",
        50031 => "SplitButton",
        50032 => "Window",
        50033 => "Pane",
        50034 => "Header",
        50035 => "HeaderItem",
        50036 => "Table",
        50037 => "TitleBar",
        50038 => "Separator",
        _ => $"Type{id}"
    };

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
