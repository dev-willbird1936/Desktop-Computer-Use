using System.Collections.Concurrent;
using System.Diagnostics;
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
    public required Process Process;
    public required IntPtr Hwnd;
    public required string Title;
}

/// <summary>
/// UI Automation engine: app resolution, accessibility tree snapshots with
/// Set-of-Marks labeling, and element re-resolution. All COM calls run on <see cref="UiaThread"/>.
/// </summary>
public sealed class UiaService
{
    private readonly UiaThread _uia;
    private readonly ConcurrentDictionary<string, Snapshot> _snapshots = new();
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
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    var title = p.MainWindowTitle;
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    list.Add(new AppTarget { Process = p, Hwnd = p.MainWindowHandle, Title = title });
                }
                catch { /* process exited */ }
            }
            return list.ToArray();
        }, ct);

    public async Task<AppTarget> ResolveAppAsync(string app, CancellationToken ct = default)
    {
        var apps = await ListAppsAsync(ct).ConfigureAwait(false);
        AppTarget? match = null;
        if (int.TryParse(app, out var pid))
            match = apps.FirstOrDefault(a => a.Process.Id == pid);
        match ??= apps.FirstOrDefault(a =>
                a.Process.ProcessName.Equals(app, StringComparison.OrdinalIgnoreCase)
             || a.Process.ProcessName.Equals(app.TrimEnd(".exe".ToCharArray()), StringComparison.OrdinalIgnoreCase))
            ?? apps.FirstOrDefault(a => a.Title.Equals(app, StringComparison.OrdinalIgnoreCase))
            ?? apps.FirstOrDefault(a => a.Title.Contains(app, StringComparison.OrdinalIgnoreCase))
            ?? apps.FirstOrDefault(a => a.Process.ProcessName.Contains(app, StringComparison.OrdinalIgnoreCase));
        if (match == null)
            throw new InvalidOperationException($"App not found: '{app}'. Running apps: {string.Join(", ", apps.Take(20).Select(a => $"{a.Process.ProcessName} ({a.Process.Id})"))}");
        return match;
    }

    // ---------- Snapshot ----------

    private static readonly HashSet<int> InteractiveTypes =
    [
        50000, // Button
        50001, // Calendar
        50002, // CheckBox
        50003, // ComboBox
        50004, // Edit
        50006, // Hyperlink
        50008, // ListItem
        50010, // Menu
        50011, // MenuBar
        50012, // MenuItem
        50013, // ProgressBar
        50015, // Slider
        50016, // Spinner
        50017, // RadioButton (actually 50013? kept defensive)
        50018, // Tab
        50019, // TabItem
        50022, // Tree
        50023, // TreeItem
        50025, // Custom
        50026, // Group
        50030, // DataGrid
        50031, // DataItem
        50033, // Document
        50035, // SplitButton
        50037, // Pane
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
                App = app.Process.ProcessName,
                Pid = app.Process.Id,
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
            _snapshots[app.Process.ProcessName] = snap;
            _snapshots[app.Process.Id.ToString()] = snap;
            return snap;
        }, ct);

    public Snapshot? GetSnapshot(string app)
        => _snapshots.TryGetValue(app, out var s) ? s
         : int.TryParse(app, out _) && _snapshots.TryGetValue(app, out var s2) ? s2
         : _snapshots.FirstOrDefault(kv => kv.Key.Equals(app, StringComparison.OrdinalIgnoreCase)).Value;

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
        try { return el.GetCurrentPattern(UiaIds.InvokePattern) != null
                  || el.GetCurrentPattern(UiaIds.TogglePattern) != null
                  || el.GetCurrentPattern(UiaIds.SelectionItemPattern) != null; }
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
        50000 => "Button", 50001 => "Calendar", 50002 => "CheckBox", 50003 => "ComboBox",
        50004 => "Edit", 50005 => "Hyperlink", 50006 => "Image", 50007 => "ListItem",
        50008 => "List", 50009 => "Menu", 50010 => "MenuBar", 50011 => "MenuItem",
        50012 => "ProgressBar", 50013 => "RadioButton", 50014 => "ScrollBar", 50015 => "Slider",
        50016 => "Spinner", 50017 => "StatusBar", 50018 => "Tab", 50019 => "TabItem",
        50020 => "Text", 50021 => "ToolBar", 50022 => "ToolTip", 50023 => "Tree",
        50024 => "TreeItem", 50025 => "Custom", 50026 => "Group", 50027 => "Thumb",
        50028 => "DataGrid", 50029 => "DataItem", 50030 => "Document", 50031 => "SplitButton",
        50032 => "Window", 50033 => "Pane", 50034 => "Header", 50035 => "HeaderItem",
        50036 => "Table", 50037 => "TitleBar", 50038 => "Separator", _ => $"Type{id}"
    };

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
