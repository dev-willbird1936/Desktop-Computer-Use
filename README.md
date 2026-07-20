# shadow-use

<p align="left">
<img alt="License: MIT" src="https://img.shields.io/github/license/dev-willbird1936/Desktop-Computer-Use">
<img alt="Latest release" src="https://img.shields.io/github/v/release/dev-willbird1936/Desktop-Computer-Use">
<img alt="Platform: Windows" src="https://img.shields.io/badge/platform-Windows-0078D6">
</p>

**Background computer control for Windows** — an MCP server that drives desktop apps
*without* moving your real cursor, stealing focus, or making the target app count
itself as focused. You keep working; it works alongside you. A cosmetic virtual
cursor (green ghost) shows what it's doing.

The benchmark suite and its results are in [docs/benchmarks.md](docs/benchmarks.md).

## Contents

- [Quick start](#quick-start)
- [Tools](#tools)
- [Settings](#settings-settingsjson)
- [Build from source](#build-from-source)
- [Architecture](#architecture)
- [Known limits](#known-limits-honest-list)

## Quick start

Download `dcu.exe` from the [latest release](https://github.com/dev-willbird1936/Desktop-Computer-Use/releases/latest) —
self-contained single file, no .NET install required.

### Wire into Claude Code

```bash
claude mcp add dcu -- "C:\path\to\dcu.exe"
```

### Wire into another MCP client (config.toml style)

```toml
[mcp_servers.dcu]
command = 'C:\path\to\dcu.exe'
```

Run `dcu.exe --doctor` any time for a diagnostics check (desktop session, UIA,
overlay).

## Tools

| Tool | What it does |
|---|---|
| `list_apps` | Running apps with visible windows (name, pid, title) |
| `get_app_state` | Accessibility-tree snapshot: labeled interactive elements (`e12`…), frames, actions, values; optional annotated Set-of-Marks screenshot. Returns a `revision` |
| `click` | By element id (UIA Invoke/Toggle/Select/Expand → message click at element center) or screen x,y. left/right/middle, click counts. Reports an observed `effect` (`expanded` / `uia_state_changed` / `element_disappeared` / `none`), not just dispatch success |
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
  "EnableFocusGuard": true,
  "PostActionDelayMs": 150
}
```

| Key | Default | Meaning |
|---|---|---|
| `AllowUiaTextFallback` | `true` | UIA `SetValue` append as typing fallback (can briefly foreground some apps) |
| `EnableBoundsGuard` | `false` | Refuse coordinate input if the window moved since the last snapshot |
| `EnableDesktopCheck` | `false` | Refuse to act on lock screen / secure desktop |
| `ShowVirtualCursor` | `true` | Green ghost cursor during actions |
| `EnableFocusGuard` | `true` | Restore your foreground window + caret after actions that grab them |
| `PostActionDelayMs` | `150` | Settle time before the follow-up snapshot |

A browser-based settings page is included: run `dcu-settings.bat`, toggle, save.

## Build from source

```bash
cd src/ShadowUse
dotnet build            # needs .NET 10 SDK
dotnet run -- --doctor  # diagnostics
```

To produce a release-style self-contained single-file exe:

```bash
dotnet publish src/ShadowUse/ShadowUse.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true -o publish
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
- Modifier-key accelerators (`ctrl+s`, `ctrl+f`, etc.) posted via `press_key` don't always
  fire: posted `WM_KEYDOWN` doesn't update `GetKeyState`, which is what most apps'
  accelerator tables check for the modifier. Fixing this properly would mean calling
  `SendInput`/`keybd_event` to set real OS-level key state — exactly the hardware input
  pipeline this tool exists to avoid touching. Single keys and most non-modifier shortcuts
  are unaffected. (Chrome's `ctrl+l` address-bar focus is a specific, verified exception —
  it's handled through UIA instead of posted keys.)
- A `WM_LBUTTONDOWN` can still make an app self-activate (rare).
- `PrintWindow` fails on some hardware-accelerated apps → falls back to visible-region capture.
- UIA `SetValue` append can briefly foreground some apps (disable via `AllowUiaTextFallback: false`).
- Minimized windows can't be captured (no backing surface); restore them first.
- Elevated (admin) targets can't be reached from a non-elevated shadow-use.

---

<p align="center">
<b>Desktop-Computer-Use</b> — designed and built by <a href="https://github.com/dev-willbird1936">dev-willbird1936</a>.<br>
MIT licensed. If you redistribute or rebrand, keep the LICENSE and NOTICE intact.
</p>
