# Desktop Computer Use (DCU)

<p align="left">
<img alt="License: MIT" src="https://img.shields.io/github/license/dev-willbird1936/Desktop-Computer-Use">
<img alt="Latest release" src="https://img.shields.io/github/v/release/dev-willbird1936/Desktop-Computer-Use">
<img alt="Platform: Windows" src="https://img.shields.io/badge/platform-Windows-0078D6">
</p>

**Desktop Computer Use**, or **DCU**, is a background Windows computer-use MCP server. It controls desktop applications without moving your real cursor, stealing focus, or making the target application count itself as focused.

Use the short name **DCU** in agent instructions to distinguish it from built-in foreground computer-use tools:

> Use DCU to control the Windows desktop in the background.

A cosmetic green virtual cursor shows DCU actions while you continue working normally.

## Why DCU

| Capability | Result |
| --- | --- |
| Focus-free control | Your active application, keyboard focus, and real cursor remain available. |
| UIA-first actions | DCU uses accessibility patterns before window-message fallbacks. |
| Background screenshots | It can capture many visible or occluded windows without activating them. |
| Observable actions | Action results report detected effects instead of dispatch success alone. |
| MCP-native interface | Claude Code, Codex, and other MCP clients can use the same tool contract. |

The benchmark suite and current results are documented in [`docs/benchmarks.md`](docs/benchmarks.md).

## Quick Start

Download `dcu.exe` from the [latest release](https://github.com/dev-willbird1936/Desktop-Computer-Use/releases/latest). It is a self-contained Windows executable and does not require a separate .NET installation.

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
| `list_apps` | Lists running applications with visible windows. |
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
| `ShowVirtualCursor` | `true` | Shows the green cosmetic cursor during actions. |
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

Create a self-contained release executable:

```powershell
dotnet publish src/ShadowUse/ShadowUse.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -o publish
```

## Architecture

```text
Program.cs                 MCP stdio host; logs go to stderr
Config/Settings            settings.json loader
Automation/UiaThread       dedicated STA thread for UIA COM
Automation/UiaService      snapshots, element IDs, and re-resolution
Automation/BackgroundInput UIA-pattern-first input with message fallbacks
Capture/ScreenshotService  PrintWindow and visible-region capture
Overlay/VirtualCursorOverlay cosmetic cursor and click animation
Safety/Guard               optional bounds and desktop guards
```

## Known Limits

- Games and applications that read raw input or `GetAsyncKeyState` cannot see posted messages.
- Modifier shortcuts do not always fire because posted messages do not update system-level modifier state. Chrome address-bar focus is handled through UIA as a verified exception.
- A message click can rarely make an application activate itself.
- `PrintWindow` fails on some hardware-accelerated applications and then falls back to visible-region capture.
- The UIA text fallback can briefly foreground some applications.
- Minimized windows do not have a capturable backing surface.
- A non-elevated DCU process cannot control elevated targets.

---

<p align="center">
<b>Desktop Computer Use (DCU)</b> — designed and built by <a href="https://github.com/dev-willbird1936">dev-willbird1936</a>.<br>
MIT licensed. Keep the LICENSE and NOTICE files when redistributing or rebranding.
</p>
