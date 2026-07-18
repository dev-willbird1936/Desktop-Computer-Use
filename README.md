# shadow-use

**Background computer control for Windows** — an MCP server that drives desktop apps
*without* moving your real cursor, stealing focus, or making the target app count
itself as focused. You keep working; it works alongside you. A cosmetic virtual
cursor (green ghost) shows what it's doing.

Design notes live in `docs/background-control-design.md` and
`docs/repo-feature-analysis.md`.

## Why it doesn't interrupt you

Most Windows automation servers inject input through `SetCursorPos`/`mouse_event`/
`SendInput` — the hardware input pipeline. That teleports your cursor and steals
foreground. shadow-use never calls those APIs:

1. **UIA patterns first** — `InvokePattern.Invoke()`, `TogglePattern`, `SelectionItemPattern`,
   `ScrollPattern`, `ValuePattern` execute *inside* the target process. No activation, by design.
2. **Window messages second** — `PostMessage`/`SendMessage` (`WM_LBUTTONDOWN/UP`,
   `WM_MOUSEWHEEL`, `WM_KEYDOWN/UP`, `WM_CHAR`) delivered straight to the target
   window's queue (the actual child window under the point, via `WindowFromPoint`).
   The OS cursor and keyboard focus are never involved.
3. **Focus-free typing** — finds the real edit control (scored: `RichEdit*` > `*Edit*` > `*Text*`,
   so wrapper classes don't shadow the true input) and inserts via
   `EM_SETSEL` + `EM_REPLACESEL`. The control never needs keyboard focus.
4. **Screenshots that see through occlusion** — `PrintWindow(PW_RENDERFULLCONTENT)`
   captures the window's own backing surface even when it's covered by other windows;
   GDI `CopyFromScreen` as fallback.
5. **Virtual cursor** — a layered, per-pixel-alpha, topmost, click-through,
   non-activating overlay (`WS_EX_LAYERED|TRANSPARENT|TOPMOST|NOACTIVATE`,
   `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`). Pure theater: it shows
   actions, it never *is* the input.

## Tools

| Tool | What it does |
|---|---|
| `list_apps` | Running apps with visible windows (name, pid, title) |
| `get_app_state` | Accessibility-tree snapshot: labeled interactive elements (`e12`…), frames, actions, values; optional annotated Set-of-Marks screenshot. Returns a `revision` |
| `click` | By element id (UIA Invoke/Toggle/Select → message click at element center) or screen x,y. left/right/middle, click counts |
| `type_text` | `EM_REPLACESEL` → UIA SetValue-append → `WM_CHAR` stream |
| `press_key` | `'Return'`, `'ctrl+s'`, `'F5'`… via posted key messages |
| `scroll` | UIA `ScrollPattern` → wheel message fallback |
| `drag` | Message-based drag with interpolated moves |
| `set_value` | Direct `ValuePattern.SetValue` / toggle-to-bool |
| `wait_for` | Server-side polling: `text_exists`, `element_exists`, `window_active` |
| `execute_sequence` | Batch steps server-side (`stop_on_error`) |
| `hide_cursor` | Hide the virtual cursor overlay |
| `health_check` | Desktop session / UIA / overlay diagnostics |

## Settings (settings.json)

Optional. Read from next to the exe, else `%APPDATA%\shadow-use\settings.json`.
**All trust gates are OFF by default** — the tool acts on whatever you point it at,
password fields included.

```json
{
  "AllowUiaTextFallback": true,
  "EnableBoundsGuard": false,
  "EnableDesktopCheck": false,
  "ShowVirtualCursor": true,
  "PostActionDelayMs": 150
}
```

| Key | Default | Meaning |
|---|---|---|
| `AllowUiaTextFallback` | `true` | UIA `SetValue` append as typing fallback (can briefly foreground some apps) |
| `EnableBoundsGuard` | `false` | Refuse coordinate input if the window moved since the last snapshot |
| `EnableDesktopCheck` | `false` | Refuse to act on lock screen / secure desktop |
| `ShowVirtualCursor` | `true` | Green ghost cursor during actions |
| `PostActionDelayMs` | `150` | Settle time before the follow-up snapshot |

## Build & run

```bash
cd src/ShadowUse
dotnet build            # needs .NET 10 SDK
dotnet run -- --doctor  # diagnostics
```

### Wire into Claude Code

```bash
claude mcp add shadow-use -- "C:\SyncedProjects\Scripting\Windows-Computer-Use\src\ShadowUse\bin\Debug\net10.0-windows10.0.22621.0\shadow-use.exe"
```

### Wire into another MCP client (config.toml style)

```toml
[mcp_servers.shadow-use]
command = 'C:\SyncedProjects\Scripting\Windows-Computer-Use\src\ShadowUse\bin\Debug\net10.0-windows10.0.22621.0\shadow-use.exe'
```

## Architecture

```
Program.cs               MCP stdio host (ModelContextProtocol SDK), logs→stderr
Config/Settings          settings.json loader (trust gates off by default)
Automation/UiaThread     dedicated STA thread — all UIA COM lives here
Automation/UiaService    app resolution, tree snapshots w/ Set-of-Marks ids, runtimeId re-resolution
Automation/BackgroundInput  UIA-pattern-first → PostMessage cascade (no SendInput anywhere)
Capture/ScreenshotService   PrintWindow → CopyFromScreen; SoM annotation
Overlay/VirtualCursorOverlay cosmetic layered cursor w/ spring motion + click pulse
Safety/Guard             optional bounds/lock-screen guards (disabled by default)
```

## Known limits (honest list)

- Apps reading raw input / `GetAsyncKeyState` (games, some hotkey systems) can't see posted messages.
- A `WM_LBUTTONDOWN` can still make an app self-activate (rare).
- `PrintWindow` fails on some hardware-accelerated apps → falls back to visible-region capture.
- UIA `SetValue` append can briefly foreground some apps (disable via `AllowUiaTextFallback: false`).
- Minimized windows can't be captured (no backing surface); restore them first.
- Elevated (admin) targets can't be reached from a non-elevated shadow-use.
