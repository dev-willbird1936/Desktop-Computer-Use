# Desktop Computer Use (DCU)

<p align="left">
<img alt="License: MIT" src="https://img.shields.io/github/license/dev-willbird1936/Desktop-Computer-Use">
<img alt="Latest release" src="https://img.shields.io/github/v/release/dev-willbird1936/Desktop-Computer-Use">
<img alt="Platform: Windows" src="https://img.shields.io/badge/platform-Windows-0078D6">
</p>

**Desktop Computer Use**, or **DCU**, is a background Windows computer-use MCP server. It uses accessibility patterns and window messages instead of moving your real cursor. It is designed to preserve your foreground window; some applications can still activate themselves, and the UIA text fallback can briefly take focus.

Use the short name **DCU** in agent instructions to distinguish it from built-in foreground computer-use tools:

> Use DCU to control the Windows desktop in the background.

A cosmetic green virtual cursor shows DCU actions while you continue working normally.

## Why DCU

| Capability | Result |
| --- | --- |
| Background control | Designed to preserve your active application and real cursor; focus-related exceptions are listed below. |
| UIA-first actions | DCU uses accessibility patterns before window-message fallbacks. |
| Background screenshots | It can capture many visible or occluded windows without activating them. |
| Observable actions | Action results report detected effects instead of dispatch success alone. |
| MCP-native interface | Claude Code, Codex, and other MCP clients can use the same tool contract. |

The benchmark suite and current results are documented in [`docs/benchmarks.md`](docs/benchmarks.md).

This release line contains the stable desktop core. Experimental JEV, browser-adapter, game-input, and perception work is not included. See [release notes](CHANGELOG.md) and the [release checklist](docs/releasing.md).

## Quick Start

Download the Windows x64 ZIP from the [latest release](https://github.com/dev-willbird1936/Desktop-Computer-Use/releases/latest), verify it against `SHA256SUMS.txt`, and extract it. Keep LICENSE and NOTICE.md with `dcu.exe`. The executable is self-contained and does not require a separate .NET installation. Earlier releases provide only the loose executable.

DCU requires an interactive Windows desktop. The executable is unsigned, so Windows may show a reputation warning. Only install binaries from a source you trust. Connect it through MCP stdio; this release does not expose an HTTP server.

### Claude Code

```bash
claude mcp add dcu -- "C:\path\to\dcu.exe"
```

### Codex or another MCP client

```toml
[mcp_servers.dcu]
command = 'C:\path\to\dcu.exe'
```

Run diagnostics at any time:

```powershell
.\dcu.exe --doctor
```

## Tools

| Tool | What it does |
| --- | --- |
| `list_apps` | Lists each visible top-level window with its PID, HWND, title, and stable `window_id`. |
| `get_app_state` | Returns an accessibility-tree snapshot, labeled elements, actions, values, and an optional annotated screenshot. |
| `click` | Uses UIA patterns or a message click and reports an observed effect. |
| `type_text` | Types through control messages or UIA value patterns. |
| `press_key` | Posts a key or key combination. |
| `scroll` | Uses the UIA scroll pattern, then a wheel-message fallback. |
| `drag` | Performs a message-based drag with interpolated movement. |
| `set_value` | Sets a value or toggle state directly. |
| `wait_for` | Polls for text, elements, or window state on the server. |
| `execute_sequence` | Runs several steps in one server-side batch. |
| `hide_cursor` | Hides the cosmetic virtual cursor. |
| `health_check` | Checks the desktop session, UIA thread, and overlay. |

If more than one window matches a name, title, or PID, DCU returns an ambiguity error. Use the `window_id` from `list_apps` to select the exact window.

Snapshot-based element clicks read the current UIA bounds before acting. Raw snapshot coordinates and drag points are translated when the window moves. Take a new snapshot after a resize when the application uses a responsive layout.

## Settings

DCU reads `settings.json` next to the executable, then falls back to `%APPDATA%\shadow-use\settings.json`.

> **Security:** Trust gates are disabled by default. DCU acts on the target you select, including password fields.

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
| --- | --- | --- |
| `AllowUiaTextFallback` | `true` | Allows UIA `SetValue` as a typing fallback. It can briefly foreground some applications. |
| `EnableBoundsGuard` | `false` | Refuses coordinate input when the window moved after the last snapshot. |
| `EnableDesktopCheck` | `false` | Refuses actions on the lock screen or secure desktop. |
| `ShowVirtualCursor` | `true` | Shows the green cosmetic cursor during actions. It disappears after one minute without use. |
| `EnableFocusGuard` | `true` | Restores the foreground window and caret after an action takes them. |
| `PostActionDelayMs` | `150` | Wait time before the follow-up snapshot. |

Run `dcu-settings.bat` to edit these settings in a browser and save them.

## Build From Source

Requirements:

- Windows
- .NET 10 SDK

```powershell
dotnet build src/ShadowUse/ShadowUse.csproj
dotnet run --project src/ShadowUse/ShadowUse.csproj -- --doctor
```

Windows shortcuts are also included:

```powershell
setup.bat
run.bat --doctor
test.bat
stress-test.bat
release.bat
```

Run `release.bat` to test and package a release candidate with its license and checksums. It requires Python 3 and PowerShell 7 in addition to the .NET SDK and never pushes, tags, or publishes. See [Preparing a release](docs/releasing.md).

For a standalone development publish:

```powershell
dotnet publish src/ShadowUse/ShadowUse.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## Architecture

```text
Program.cs                 MCP stdio host; logs go to stderr
Config/Settings            settings.json loader
Automation/UiaThread       dedicated STA thread for UIA COM
Automation/UiaService      snapshots, element IDs, and re-resolution
Automation/BackgroundInput UIA-pattern-first input with message fallbacks
Capture/ScreenshotService  PrintWindow capture with bounded worker isolation
Overlay/VirtualCursorOverlay cosmetic cursor and click animation
Safety/Guard               optional bounds and desktop guards
```

## Known Limits

- Games and applications that read raw input or `GetAsyncKeyState` cannot see posted messages.
- Modifier shortcuts do not always fire because posted messages do not update system-level modifier state. Chrome address-bar focus is handled through UIA as a verified exception.
- A message click can rarely make an application activate itself.
- `PrintWindow` fails on some hardware-accelerated applications. DCU then returns no screenshot by default instead of capturing an unrelated window from the visible screen.
- The UIA text fallback can briefly foreground some applications.
- Minimized windows do not have a capturable backing surface.
- A non-elevated DCU process cannot control elevated targets.
- A timeout cannot abort an already-running UIA COM call. A permanently unresponsive target can block later UIA work until DCU is restarted.

---

<p align="center">
<b>Desktop Computer Use (DCU)</b> — designed and built by <a href="https://github.com/dev-willbird1936">dev-willbird1936</a>.<br>
MIT licensed. Keep the LICENSE and NOTICE files when redistributing or rebranding.
</p>
