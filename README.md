# Desktop Computer Use (DCU)

<p align="left">
<img alt="License: MIT" src="https://img.shields.io/github/license/dev-willbird1936/Desktop-Computer-Use">
<img alt="Latest release" src="https://img.shields.io/github/v/release/dev-willbird1936/Desktop-Computer-Use">
<img alt="Platform: Windows" src="https://img.shields.io/badge/platform-Windows-0078D6">
</p>

**Desktop Computer Use**, or **DCU**, is a Windows computer-use MCP server. Background input is the default: accessibility patterns and window messages leave your real cursor available. Explicit foreground input uses the system cursor and keyboard for games or applications that reject background messages. Some accessibility providers can still activate their application; the optional UIA text fallback can briefly take focus.

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

This release line contains desktop control, normal/humanized foreground input, and native game tools. JEV, the browser adapter, learned visual discovery, and alternative driver/hardware/memory input backends remain experimental and are not included. See [release notes](CHANGELOG.md) and the [release checklist](docs/releasing.md).

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

All **21 desktop/game tools** are listed and callable. `GameMode` is an input-profile preference, not a tool-access gate. An agent can explicitly select input mode and pace without changing settings or restarting DCU.

### Desktop tools

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

Snapshot-based element clicks read the current UIA bounds before acting. Background snapshot coordinates and drag points are translated when the window moves. Take a new snapshot after a resize when the application uses a responsive layout.

### Game and native-surface tools

| Tool | What it does |
| --- | --- |
| `game_screenshot` | Returns a JPEG, `source_frame_id`, and `image_scale` without requiring an accessibility tree. `bring_to_front=true` explicitly permits activation for GPU capture. |
| `game_click` | Clicks in background messages or real/humanized foreground input. |
| `game_move` | Moves the background pointer or real cursor; relative mouse-look requires foreground input. |
| `game_drag` | Drags with the selected input mode. |
| `game_scroll` | Scrolls at a selected point without requiring a UIA tree. |
| `game_key` | Taps, holds, or repeats keys/chords; foreground also supports mouse-button chords. |
| `game_ocr` | Reads text using installed Windows OCR language support; reports unsupported configurations explicitly. |
| `game_bars` | Measures calibrated HUD bars; unknown fill is not reported as zero. |
| `game_wait` | Waits for text, frame changes/stability, or foreground without repeating the input. Returns its last screenshot on timeout. |

For image coordinates, pass the `source_frame_id` from the screenshot you used. Another agent's screenshot does not replace that frame's scale. Frames are specific to the server instance and selected window; resize or expired history requires a fresh screenshot. `coord_space="screen"` instead uses absolute screen pixels.

Game actions distinguish accepted input from observed effect: `frame_changed`, `minor_change`, `no_change`, or `unverified`. Set `then_screenshot=true` to get the result image in the same call. Do not blindly repeat a dispatched action because no visual change was detected. Keys are held only for a bounded call, then release is attempted on completion, cancellation, or failure. Rejected cleanup is reported; permanent `down/up` controller ownership is not exposed.

### Multiple agents

Background actions against different windows can progress independently. Actions against the same window are coordinated across DCU processes, and `execute_sequence` reserves that exact window for its entire sequence. Real-cursor actions also queue for the one shared Windows cursor. There is no persistent agent/window claim to release manually.

DCU retains 64 accessibility observations per window so another observation does not immediately invalidate your IDs. IDs are opaque and specific to the server instance; do not parse them or send another server's IDs. Share `window_id`, then obtain your own state when handing work to a separate client. Each window has an isolated UIA worker, so a wedged provider does not block other windows' accessibility work.

Your MCP host must still register/expose DCU to each agent. DCU cannot change host tool inheritance or create multiple independent system cursors.

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
  "PostActionDelayMs": 150,
  "UseRealCursor": false,
  "GameMode": false,
  "HumanizedInput": false,
  "FastInput": false
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

### Input toggles

| Key | Default | Meaning |
| --- | --- | --- |
| `UseRealCursor` | `false` | Off: background UIA/messages and a cosmetic cursor. On: activate the selected window and inject real system-cursor/keyboard input. |
| `GameMode` | `false` | Use shorter activation settles and retain foreground/cursor after real input, avoiding mouse-look camera resets. Does not enable real input or gate tool access. |
| `HumanizedInput` | `false` | Shape foreground motion and key timing. Never silently turns a background call into foreground input. |
| `FastInput` | `false` | Shorten fixed waits and pacing. Does not shorten an explicitly requested key-hold duration or observation timeout. |

All five toggles can be combined: no combination is rejected or silently changes the cursor choice. Humanization and game foreground-retention apply when real-cursor input is selected; the virtual marker is for background actions. Fast pacing works with either delivery mode. Automated checks cover all 32 setting combinations and their per-call overrides.

The background “own cursor” is a **cosmetic overlay**, not a second hardware pointer. `ShowVirtualCursor` controls its visibility; `UseRealCursor` controls how input is delivered.

Per-call `input_mode` overrides the cursor default:

- `"background"`: background input, even when `UseRealCursor=true`.
- `"foreground"`: system input, using the configured humanization preference.
- `"foreground_plain"`: system input without humanization.
- `"humanized"`: humanized system input, even when the preference is off.

Use `pace="fast"` or `pace="normal"` for a call. Choose one input mode for an entire sequence; its steps cannot change that reservation.

Additional numeric settings are `HumanizedMouseSpeed=1.0` and `HumanizedTypingSpeed=1.0` (0.25–4.0), and `HumanizedTypoRate=0.0` (0–0.2). Typos are off by default.

Run the bundled `dcu-settings.bat` to edit the toggles in a browser. This optional settings page requires Python 3; the MCP executable does not. It saves to an existing executable-side settings file when present, otherwise profile settings. Reconnect each DCU process after saving persistent settings. Per-call mode and pace do not need a restart.

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
agent-test.bat
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

- Raw-input games and applications that use `GetAsyncKeyState` cannot see background messages. Select foreground input; some protected games still reject normal `SendInput`. No driver or anti-cheat bypass is included.
- Background modifier shortcuts do not always fire because posted messages do not update system-level modifier state. Foreground input uses real key events. Chrome address-bar focus is handled through UIA as a background exception.
- A message click can rarely make an application activate itself.
- `PrintWindow` fails on some hardware-accelerated applications. DCU then returns no screenshot by default instead of capturing an unrelated window from the visible screen.
- The UIA text fallback can briefly foreground some applications.
- Minimized windows do not have a capturable backing surface.
- A non-elevated DCU process cannot control elevated targets.
- A timeout cannot abort an already-running UIA COM call. A permanently unresponsive target can require restarting that DCU process for that window; other windows have separate workers, and native discovery/game input remain available.
- Foreground input necessarily competes with the physical user's cursor/keyboard. It is explicit opt-in, not simultaneous independent hardware input. Normal mode attempts to restore the cursor/foreground; game profile retains them. If the user switches to another app, DCU does not force the old foreground back.

---

<p align="center">
<b>Desktop Computer Use (DCU)</b> — designed and built by <a href="https://github.com/dev-willbird1936">dev-willbird1936</a>.<br>
MIT licensed. Keep the LICENSE and NOTICE files when redistributing or rebranding.
</p>
