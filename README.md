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

DCU has more than 100 tools. To keep the model's context small, `tools/list` shows only the **core tools** and **discovery tools** below. Every other tool is hidden from the list but stays available: find it with `search_tools`, then run it with `call_tool` or load it into the list with `describe_tool`.

### Core tools

| Tool | What it does |
| --- | --- |
| `list_apps` | Lists each visible top-level window with its PID, HWND, title, `window_id`, and lifetime-bound `target_id` when available. |
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
| `health_check` | Checks the desktop session, UIA thread, overlay, and active input and capture settings. Reports runtime identity (version, build timestamp, module id, process id, stale-executable flag) and the schema fingerprint of the full tool catalogue, including `game_input_ownership` and `game_release_input_ownership`; reconnect when they differ from the build you expect. |

### Discovery tools

| Tool | What it does |
| --- | --- |
| `search_tools` | Finds hidden tools from a plain-language `query`, optionally filtered by `namespace`. Returns names, namespaces, one-line summaries, and safety hints, but no schemas. An empty query lists every namespace with its tool count. |
| `describe_tool` | Returns one tool's full description, input schema, namespace, annotations, and schema hash. Also returns `catalog_schema_fingerprint` (the full catalogue) and `runtime` (version, build timestamp, module id, process id). `load=true` adds the tool to this session's `tools/list`; `load=false` removes it. DCU sends `notifications/tools/list_changed` when the list changes. |
| `call_tool` | Runs any tool by `name` with an `arguments` object. The result is the same as calling the tool directly. Use it when your client cannot refresh its tool list. |

Typical flow:

1. `search_tools(query: "fill a web form")` returns `browser_fill` in namespace `browser.input`.
2. `describe_tool(name: "browser_fill")` returns its input schema.
3. `call_tool(name: "browser_fill", arguments: {...})` runs it.

Clients that already know a tool's name can also call it directly, even when it is not listed.

### Namespaces and annotations

Every tool belongs to a namespace, such as `browser.input`, `browser.observe`, `workflow.dag`, `workflow.subgoal`, `memory.experience`, `files`, `network.http`, `data.grid`, `voice.session`, `review.room`, and `environment.isolated`. The registry is `src/ShadowUse/Tools/ToolCatalog.cs`.

Every tool carries MCP annotations:

- `readOnlyHint: true` — the tool only reads state.
- `destructiveHint: true` — the tool can delete or overwrite data, for example `write_file`, `undo_grid_batch`, or `destroy_isolated_environment`.
- `openWorldHint: true` — the tool reaches outside the local desktop, for example browser pages, HTTP, or the Jev service.

To list every tool instead, set `ToolExposure` to `"all"` (see [Settings](#settings)).

### Window selection

`list_apps` also returns an opaque `target_id` and a lifetime epoch for each window whose
process creation identity was read successfully. The target ID is bound to the current
process birth and HWND lifetime; it is not a PID alias. Cached element IDs are rejected
when that identity becomes stale. A missing process-birth value fails closed and is never
cached as a PID-only target.

Use `window_id` for the existing exact-window selector or the prefixed `target_id`
(for example, `dcu-target-...`) for an opaque lifetime-bound selector. A bare 32-character
hexadecimal title remains an ordinary title selector. Explicit selectors never fall through
to title or process-name matching. Legacy process-name selection can retain a cached element only when the
original owner is still current and a native owner-chain relation is proven; otherwise
DCU asks for a fresh snapshot. Filtered or hidden-window enumeration does not destroy a
tracked target.

If more than one window matches a name, title, or PID, DCU returns an ambiguity error. Use the `window_id` or `target_id` from `list_apps` to select the exact window.

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
  "PostActionDelayMs": 150,
  "ToolExposure": "core",
  "GameMode": false,
  "HumanizedInput": false,
  "FastInput": false,
  "JevMode": "off"
}
```

### Feature toggles

Four layers, each independent of the others. Base DCU works with everything else off. Toggles marked "restart" are read once at startup.

| Toggle | Default | What it does | Turn it on when |
| --- | --- | --- | --- |
| Base DCU | always on | UIA patterns and posted window messages. The real cursor and foreground never change. | Ordinary apps: browsers, editors, Office. |
| `JevMode` | `"off"` | Decision-speed helper (Jev), not a permission gate. `off`: Jev tools decline. `assist`: `jev_decide` returns a recommendation and never executes. `fast_loop`: `jev_act` executes when confidence clears the floor, else it returns the candidate for you to act on. Native UIA choices carry screen and window bounding boxes. Needs `TYPESAFE_API_KEY` in the environment. | The target exposes elements, actions or inputs DCU can read (a UIA tree or browser candidates), and you want a fast pick among them. UIA coverage does not include visually rendered controls the target does not expose. Not for games, which have no tree. |
| `GameMode` (restart) | `false` | Lists the ten `game_*` tools (hidden and refusing when off). An omitted `background_mode` becomes real input instead of `strict_background`. Uses the fast game input profile: no activation settle when the game already has the foreground, no cursor or foreground restore afterwards. Applies the `InputBackendMode` settings. | You are playing a game, or the target has no UIA tree: Unreal, Unity, and other games. |
| `HumanizedInput` (restart) | `false` | Only changes timing. Calls with no `background_mode` run humanized: curved cursor paths with overshoot, typing rhythm with occasional self-corrected typos, varied holds. It is real `SendInput`, so it takes the foreground and the real cursor. `game_*` tools follow it (no per-call flag). | A person might watch, or the target needs human-simulated input. Usually needed for games, which often reject robotic input. |
| `FastInput` (restart) | `false` | Shortens every DCU wait, hold and settle delay, and tightens humanized output: one curve segment, no typos, no hesitations, key holds 28-72 ms, faster typing and wheel steps. Timing stays randomised, just in lower ranges. `game_*` tools can override it per call with `pace`. | Lower latency matters more than looking human. Usually needed for games with real-time pressure (combat, dodging). |

Common combinations:

| Goal | Settings |
| --- | --- |
| Ordinary background automation | all off |
| Game, human-like | `GameMode` + `HumanizedInput` |
| Game, quick reactions (combat) | `GameMode` + `HumanizedInput` + `FastInput`, or `pace="fast"` on single calls |
| Game, no humanising | `GameMode` only |
| Fast humanized input in ordinary apps | `HumanizedInput` + `FastInput` |

### Which toggles to use

Pick by the target and by what the caller needs. The toggles stack: each one changes a different thing.

| Toggle | Use it when | Do not use it when |
| --- | --- | --- |
| `JevMode` | DCU has knowledgeable access to the elements, actions or inputs: a UIA tree or browser candidates it can list. Jev chooses among those known candidates. `assist` to see a recommendation first, `fast_loop` to let it act above the confidence floor. | The target is a game or any window with no readable elements. Jev has nothing to choose from; use `game_*` tools. |
| `GameMode` | You are playing a game, or the window has no UIA tree. It lists the `game_*` tools and makes real input the default. | The target is an ordinary app or a browser. Base DCU and `browser_*` are cheaper and do not take your cursor. |
| `HumanizedInput` | A person might watch, the game or site rejects robotic input, or you want the cursor and typing to look human. Usually on for games. | Nobody watches and the target accepts plain input. It adds travel time to every action. |
| `FastInput` | Lower latency matters: combat, dodging, timed prompts, long sequences. Usually on for games. It keeps timing randomised, just in lower ranges. | You want slow, clearly human pacing. For one quick action, pass `pace="fast"` instead of changing the setting. |

Quick picks:

- Game with real-time pressure: `GameMode` + `HumanizedInput` + `FastInput`.
- Game with menus and no time pressure: `GameMode` + `HumanizedInput`.
- Ordinary app where DCU can read the elements: `JevMode` (`assist` or `fast_loop`), everything else off.
- Anything else: leave all four off.

`jev_decide` builds native candidates from visible actionable controls exposed in the current UIA tree and includes screen- and window-relative bounding boxes. An opt-in fake-Jev smoke test verifies this route for a button, text field, and checkbox. `candidate_set_complete=true` describes only a complete UIA projection; it does not include canvas content or controls that an app does not expose.

### Setting reference

| Key | Default | Meaning |
| --- | --- | --- |
| `AllowUiaTextFallback` | `true` | Allows UIA `SetValue` as a typing fallback. It can briefly foreground some applications. |
| `EnableBoundsGuard` | `false` | Refuses coordinate input when the window moved after the last snapshot. |
| `EnableDesktopCheck` | `false` | Refuses actions on the lock screen or secure desktop. |
| `ShowVirtualCursor` | `true` | Shows the green cosmetic cursor during actions. It disappears after one minute without use. |
| `EnableFocusGuard` | `true` | Restores the foreground window and caret after an action takes them. Keep on. |
| `PostActionDelayMs` | `150` | Wait before the follow-up snapshot, 0-5000. `FastInput` scales it down (never below 15 ms). |
| `ToolExposure` | `"core"` | `core` lists the core and discovery tools; all other tools stay callable. `all` lists every tool, which uses more model context. Restart. |
| `GameMode` | `false` | See feature toggles. |
| `HumanizedInput` | `false` | See feature toggles. |
| `HumanizedMouseSpeed` | `1.0` | Humanized cursor speed multiplier, 0.25-4. Higher is faster. |
| `HumanizedTypingSpeed` | `1.0` | Humanized typing speed multiplier, 0.25-4. |
| `HumanizedTypoRate` | `0.015` | Per-letter chance of a self-corrected typo, 0-0.2. Secret values from `value_ref`/`value_env` never get typos. `FastInput` forces it to 0. |
| `FastInput` | `false` | See feature toggles. |
| `JevMode` | `"off"` | `off`, `assist`, or `fast_loop`. See feature toggles. |
| `JevModel` | `"jev-latest"` | Model name sent with each Jev request. |
| `JevBaseUrl` | `https://api.typesafe.ai/v1/systemone` | Jev endpoint. The API key is read from `TYPESAFE_API_KEY`, never stored. |
| `JevTimeoutMs` | `8000` | Per-call Jev timeout, 1000-60000. |
| `InputBackendMode` | `"SendInput"` | Injection path, used only when `GameMode` is on. `SendInput` (default, works everywhere), `KernelDriver`, `HardwareDevice`, `MemoryWrite`, or `Auto`. Independent of `HumanizedInput`, which controls timing. |
| `KernelDriverKind` | `"Interception"` | Driver for `KernelDriver`: `Interception` or `WdfHidMinidriver`. |
| `HardwareComPort` | unset | Serial port for `HardwareDevice`, e.g. `COM3`. |
| `HardwareBaudRate` | `115200` | Serial speed for `HardwareDevice`, 9600-2000000. |
| `MemoryWriteTargetPid` | `0` | Process id for `MemoryWrite`. `0` disables it. |
| `EnableRunTimeline` | `false` | Records actions to a JSON-lines file for `get_run_timeline` and `get_benchmark_report`. |
| `RunTimelinePath` | `%APPDATA%\shadow-use
un-timeline.jsonl` | Timeline file, used only when `EnableRunTimeline` is on. |
| `RemoteTransportUrl` | unset | A URL such as `http://127.0.0.1:8642` serves the full tool set over HTTP instead of stdio. Authentication is your job: a non-loopback address exposes full computer control to that network. |
| `EnableCatalogueComposition` | `false` | Allows `render_composition` and `dispatch_composition_action`. When off they return a blocked status. |

Per-call overrides on `game_*` tools: `pace` (`fast` or `human`), `max_latency_ms`, `observe`, `verify`, `then_screenshot`. Per-call `background_mode` on the core tools overrides the toggle defaults for that call.

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
Capture/ScreenshotService  PrintWindow capture with bounded worker isolation
Overlay/VirtualCursorOverlay cosmetic cursor and click animation
Safety/Guard               optional bounds and desktop guards
Tools/ToolCatalog          tool registry: namespaces, annotations, which tools are listed
Tools/DiscoveryTools       search_tools, describe_tool, and call_tool
```

## Known Limits

- Games and applications that read raw input or `GetAsyncKeyState` cannot see posted messages by default. An opt-in real-input mode exists for these cases. See [Games and raw-input applications](#games-and-raw-input-applications).
- Modifier shortcuts do not always fire because posted messages do not update system-level modifier state. Chrome address-bar focus is handled through UIA as a verified exception.
- A message click can rarely make an application activate itself.
- `PrintWindow` fails on some hardware-accelerated applications. DCU then returns no screenshot by default instead of capturing an unrelated window from the visible screen.
- The UIA text fallback can briefly foreground some applications.
- Minimized windows do not have a capturable backing surface.
- A non-elevated DCU process cannot control elevated targets.

## Games and raw-input applications

Many games accept DCU clicks in menus but ignore them in play, for example when placing a unit or tower. DCU posts `WM_LBUTTONDOWN` / `WM_LBUTTONUP` messages that carry their own coordinates. It never moves the real cursor or activates the window. Game engines usually read input in other ways:

| How the game reads input | Why a posted click fails |
| --- | --- |
| Raw Input or DirectInput (`WM_INPUT`) | Windows creates these events only from real mouse hardware. |
| Cursor position (`GetCursorPos`, engine mouse position) | The game uses your real cursor position, not the coordinates in the message. |
| Button state (`GetAsyncKeyState`) | Posted messages do not change the physical button state. |
| Focus check | Many engines, including Unity, drop input while their window is not in the foreground. |
| Hover and drag | Placement previews follow the real cursor, so a message drag moves nothing. |

Menus often work because a menu button reacts to one click event and needs no hover, cursor tracking, or held button.

**Detection.** An in-game click that changes nothing returns `effect: "none"`. Games expose no accessibility tree, so DCU cannot see in-game changes itself. Compare screenshots before and after the action to confirm a click failed.

### Real input: `isolated_interactive`

`click`, `type_text`, `press_key`, `scroll`, and `drag` accept `background_mode="isolated_interactive"`. It brings the target window to the foreground, moves the real mouse cursor, and injects hardware-level events with `SendInput` (real `WM_INPUT` events and real button/key state, not posted messages). It restores your cursor position and foreground window afterward. `set_value` and every other tool still return `UNSUPPORTED_INPUT_MODE` for this value.

> **This takes over your mouse and keyboard while the action runs**, the same as a person clicking. Use it only for a specific action you've chosen, never as a default. It is not automatic: `strict_background` never switches to it, and no tool falls back to it on its own.

Typical use: try `strict_background` first (the default). If the click's `effect` comes back `"none"` and a before/after screenshot confirms nothing changed, retry the same call with `background_mode="isolated_interactive"`.

```json
{ "tool": "click", "app": "MyGame", "x": 640, "y": 360, "background_mode": "isolated_interactive" }
```

### Human-seeming input: `humanized`

`background_mode="humanized"` is `isolated_interactive` played back like a person at the machine, for `click`, `type_text`, `press_key`, `scroll`, and `drag`. It uses the same real `SendInput` path, so it takes your cursor and foreground for the whole (longer) action. `click_visual_candidate` and `route_click` accept it too.

- **Cursor:** Fitts-law duration along curved sub-movements with a bell-shaped speed profile, occasional overshoot and small corrections. It is sent as small relative deltas every 2-5 ms (a 200-500 Hz mouse), steered by the real cursor position. Games that read Raw Input see genuine `WM_INPUT` deltas, and games that poll `GetCursorPos` see a real cursor. Buttons and the wheel carry no coordinates, so they act where the cursor is.
- **Locked cursor (mouse-look):** before moving, DCU nudges the cursor 1 px and checks whether it moved. If the game recentres or locks it, DCU makes no position-based move. `click` and `scroll` act in place, and `drag` sends its motion as raw deltas.
- **Click:** a point biased toward the approach side inside the element's padded frame (not always the centre), a resting pause with rare 1-2 px hand tremor, a 50-180 ms hold, and a 60-170 ms gap between double-click presses. Every wait is redrawn from a range; Fitts coefficients, path pauses, and the chance of a mid-move or pre-click hesitation also vary per action.
- **Typing and keys:** one key at a time using scan codes for the target window's keyboard layout. A typing burst picks one speed (about 70-130 WPM) and keeps it, with per-key interval jitter, same-hand/repeat/Shift timing, a left/right hand bias, rare mid-word hesitations, and rare adjacent-key typos that are backspaced and retyped. Word breaks sometimes pause; a sentence-ending pause is rarer and longer. Text from `value_ref`/`value_env` never gets typos. Characters the layout cannot type are sent as Unicode. No clipboard paste. Single keys hold about 45-220 ms with staggered modifiers.
- **Wheel:** paced notches of 120 units, faster at the start, slower near the end, with occasional longer pauses.
- **Safety:** cancellation releases any held button or key, and input stops if the target loses the foreground mid-action.
- **Limits:** Windows marks `SendInput` events as injected (`LLMHF_INJECTED` / `LLKHF_INJECTED`, Raw Input `hDevice = 0`). A low-level hook or kernel anti-cheat can see that. Valve Anti-Cheat (Foxhole) does not check those flags. Gamepad (XInput) input is not covered. Held-key auto-repeat is not emulated. Relative moves are subject to Windows pointer acceleration, so a visible cursor is corrected against its real position, while a locked cursor gets raw deltas as planned.

**Agent rule.** If the user says humanized, or the target is a game (Unreal, Unity, Raw Input, DirectInput), pass `background_mode="humanized"` on every `click`, `type_text`, `press_key`, `scroll`, and `drag`. Do not omit it. An omitted value is `strict_background` unless `settings.json` has `"HumanizedInput": true`, and `strict_background` only posts window messages. Unreal and Unity ignore those.

```json
{ "tool": "press_key", "app": "Foxhole", "key": "E", "background_mode": "humanized" }
```

Set `"HumanizedInput": true` in `settings.json` next to `dcu.exe` to make an omitted `background_mode` mean humanized. An explicit value always wins. Tune with `HumanizedMouseSpeed` and `HumanizedTypingSpeed` (0.25-4, default 1) and `HumanizedTypoRate` (0-0.2, default 0.015).

### If humanized input is ignored

The action can return success while the game does nothing. Check these in order:

1. The tool call must include `"background_mode": "humanized"`. `strict_background` and a missing value post `WM_KEY*` / `WM_*BUTTON*` messages. Unreal Engine reads Raw Input and drops those.
2. Run `dcu.exe` at the same integrity level as the game. A non-elevated DCU cannot foreground or inject into an elevated game. If the game is Run as administrator, start DCU the same way.
3. The game window must be visible on the current desktop, not minimized and not on another virtual desktop. Humanized mode calls `SetForegroundWindow` and stops if the window never becomes foreground.
4. Re-snapshot after the action. Games have no UIA tree, so `effect` cannot prove an in-game change. Compare screenshots.
5. A locked or recentred cursor (mouse-look) gets no absolute move. `click` and `scroll` act where the cursor already is; aim with `drag` or relative motion, not a screen `x,y` click.

`isolated_interactive` is the same real `SendInput` path without the human timing. Use `humanized` when the user asked for it or a person might be watching. Real-time games still lose while each action holds the foreground and cursor.

## Game mode

Toggles are layered: base DCU always works; `JevMode` and `GameMode` are independent switches; `HumanizedInput` only changes timing and works with or without game mode.

Set `"GameMode": true` (or use `dcu-settings.bat`) and restart DCU. This:

- lists the `game_*` tools (they are hidden and refuse to run while it is off);
- makes an omitted `background_mode` mean real input (`humanized` if `HumanizedInput` is also on) instead of `strict_background`;
- applies the input backend settings (`InputBackendMode`: `SendInput` default, `KernelDriver`, `HardwareDevice`, `MemoryWrite`, `Auto`; plus `KernelDriverKind`, `HardwareComPort`, `HardwareBaudRate`, `MemoryWriteTargetPid`). With game mode off the backend is always SendInput;
- uses a faster input profile: no activation settle when the game already has the foreground, and DCU does not move the cursor back or hand the foreground back after each action.

| Tool | Use |
| --- | --- |
| `game_screenshot` | Inline JPEG, no UIA tree, about 50-150 KB instead of 2-3 MB. `max_width` sets the size; `x,y,width,height,zoom` crop a region. Returns `image_scale`. |
| `game_click`, `game_move`, `game_drag`, `game_scroll` | Coordinates are pixels of the last full `game_screenshot` (`coord_space="screen"` for absolute). `game_move` hovers, or sends a relative delta (`dx`,`dy`) for mouse-look. |
| `game_key` | Scan-code keys: `tap`, `hold` (`duration_ms`), `down`/`up`, `repeat` (`interval_ms`); chords like `shift+w`, `w+d`. Mouse buttons are keys too (`lmb`, `rmb`, `mmb`), so `rmb` held aims and `rmb+lmb` aims and fires. `keys` is an alias for `key`. Held keys and buttons release on foreground loss, on error, after 60 s, and when DCU exits. |
| `game_release_keys` | Releases everything left down. |
| `game_input_ownership` | Reports which DCU process controls real input for a game window (`unowned`, `owner`, `conflict`, `unknown`) with the controller process id and window identity, or lists the leases this process holds. Ownership is a cross-process lease claimed before foreground activation or input; it ends on release or when the owning DCU exits. DCU never takes a window from another controller. |
| `game_release_input_ownership` | Releases this process's input lease for one game window. Refused while input runs or keys and buttons are held; never touches a lease held by another process. |
| `game_ocr` | OS OCR of the window or a crop; each line has a click point. |
| `game_bars` | Health/hunger/stamina bars as percentages from pixel colour. |
| `game_wait` | `text_appears`, `text_gone`, `frame_changes`, `frame_still`, `camera_settled`, `foreground`. `stable_ms` makes text conditions (default 0), `frame_still` and `camera_settled` (default 500) hold that long before they count, which stops a one-frame OCR flicker from ending the wait. `camera_settled` counts only confident stillness: a shift, an unexplained scene change (a turn, a cut, a new screen) or an unreadable comparison restarts the interval and never inherits earlier stability. On timeout the last evaluated frame comes back as an image (`final_screenshot=false` to skip), so you can re-observe without sending another input. |

Every action tool compares a small before/after frame of the game window and reports `effect`: `frame_changed`, `minor_change`, `no_change`, or `unverified` (the frame could not be captured, or `verify=false`). `ok: true` means the input was sent. `loop_guard.should_stop` fires only after three consecutive `no_change` results for the same action on the same window. `then_screenshot=true` returns the after frame in the same call, and a lost foreground is retried automatically up to twice.

Every action tool also takes:

- `pace`: `fast` or `human` for this call (default: the `FastInput` setting). `fast` is still randomised, roughly 65-130 ms per cursor move and short holds, with no typos or hesitations. `max_latency_ms` caps one cursor move; longer planned moves are compressed to fit.
- `observe`: `text` (OCR of the frame after the action, limited by `observe_box="x,y,width,height"` in image space), `motion` (how far the world scrolled, as `world_shift_px` with a `confidence`, or `kind: none`/`changed`), or `text,motion`. This saves the follow-up `game_ocr` or screenshot call. `camera_translation` is `true` only for a clean shift, `false` for confident stillness and `null` when unknown; `unclassified_change` marks a change no shift explains (a turn, a cut, an animation). A shift shows the view scrolled, not that the avatar moved, because mouse-look scrolls the view too.
- The `progress` result names what a satisfied check shows in `proves` (`camera_translation`, `region_pixels_changed`, `some_pixels_changed` or `text_visible`). None of them proves the avatar moved.
- `observe_timeout_ms`, `observe_settle_ms`, `observe_poll_ms`: how the post-action wait runs. The input is sent once; only the observation repeats. With `progress_box` or `expected_effect`, DCU watches for up to `observe_timeout_ms` (default 1500, 0-60000), polling every `observe_poll_ms` (default 100, 50-2000) after `observe_settle_ms` (default 120, 0-10000). The settle is wall-clock time and does not shrink with `pace`. Raise the timeout for slow menus, loads and server round trips (a UI that answers in about 10 seconds needs `observe_timeout_ms=12000`) instead of repeating the input. Without a check, the call takes one frame after the settle.
- `game_click` also takes `hold_ms` (1-60000, `click_count` 1) to hold a button down, for charging or holding a use key.

`game_screenshot` also takes `bars_json` to read calibrated HUD bars from the same frame, with no second capture. A bar that is covered, scattered or not matching its calibrated colour returns `percent: null` (unknown) with a low `confidence`; matching a colour is not enough for `reliable: true`.

### Calibrated visual candidates and Jev

For the calibrated English Bloons TD Battles 2 hero-selection screen, call `game_screenshot` with `include_ui_candidates=true`, `bring_to_front=false`, and a full frame. This mode never activates the game window; a blank/occluded capture fails the pixel/OCR coverage checks. The same-frame `ui_observation` returns image-space boxes, centers, pixel-anchor evidence, OCR text, and the candidate coverage scope. It is complete only for the listed 16 hero-card slots, two possible player-profile banners, Cancel/Ready, two map arrows, and the Heroes-page arrow. The map preview and large character art remain context regions. This is not a general game parser; identity, clickability, and game-enabled state remain unverified even when layout coverage passes.

Hero labels default to `Unknown hero option, row R column C`. A multimodal caller may provide short visual descriptions to `game_jev_decide` using `visual_labels_json`, keyed by the `key` values in that same observation. Descriptions are unverified, not official hero names. Pass the screenshot's `observation_id` to bind the recommendation to that frame. Jev receives each candidate's bounding box and screen/image coordinate mapping, plus `REOBSERVE`, `ASK_USER`, and `STOP` alternatives. The tool is **decision-only**: it never moves the cursor or dispatches game input. It refuses incomplete, expired, moved, resized, or DPI-changed observations. A real Jev request requires Jev to be configured and enabled.

```json
{
  "tool": "game_screenshot",
  "arguments": { "app": "Bloons TD Battles 2", "max_width": 1680, "bring_to_front": false, "include_ui_candidates": true }
}
```

Then pass the returned `observation_id` and (if needed) descriptions to `game_jev_decide`. This calibrated path handles one known menu layout; it does not assert that other game screens are fully parsed.

```json
{
  "tool": "game_jev_decide",
  "arguments": {
    "app": "Bloons TD Battles 2",
    "goal": "Recommend the cyan-bow archer. Do not ready or confirm.",
    "observation_id": "frame-...",
    "visual_labels_json": "{\"hero-r1-c1\":\"dark hooded archer with orange-red accents\",\"hero-r2-c1\":\"dark hooded archer with cyan-blue bow\"}"
  }
}
```

`JevMode` must be `assist` or `fast_loop` and the normal Typesafe API key must be configured; this setting does not turn the recommendation into an input action.

For other game/canvas screens, use the owned local `include_local_fast_candidates=true` or `include_local_yolo_candidates=true` discovery modes without caller-supplied candidate JSON, or inspect the JPEG with an authorized vision-capable caller/parser and pass its frame-bound `candidate_set_json` to `game_jev_decide_candidates`. The generic local detector is still incomplete; it does not establish that every interactable element was found. The caller-generated inventory must remain frame-bound and all claim statuses are unverified. The JSON must carry that `frame_id`, exact screenshot image dimensions, a declared scope, a `candidate_set_complete` boolean, and each candidate's image-space `{x,y,width,height}` box. Complete claims require a `coverage_evidence` note; incomplete sets require `candidate_set_issue`. DCU validates frame binding, schema, and bounds; preserves an empty inventory (no candidates found) and rejects inventories over 255 rather than truncating; caller-provided coverage remains unverified provenance. A screenshot with masked regions cannot be declared complete: `masked_image_boxes` are frame-bound and complete claims are refused until the caller marks coverage incomplete or recaptures without masks. It does not pretend to verify external-parser recall. Incomplete sets are refused by default. Set `allow_incomplete_coverage=true` only for a clearly marked advisory recommendation; the incomplete status and issue are sent to Jev, and no input is dispatched. When a Jev request is sent, each tool makes one HTTP POST; retry and Score follow-up are disabled. The frame must still be within DCU's 10-second freshness window after the visual pass. Keep every plausibly actionable region—including uncertain pictorial controls—as a candidate with actionability unverified. Use `context_regions` for non-target visual context, not as a way to silently omit a possible target. Mark labels as descriptions, not confirmed names.

DCU also has a model-free, local heuristic path for a generic candidate proposal set. Call `game_screenshot` with `include_local_fast_candidates=true`, `bring_to_front=false`, and optionally `local_candidate_method="edge"`, `"ocr"`, or `"combined"` (`edge` is the fast default). It uses the same captured full frame and stores incomplete, frame-bound proposals for `game_jev_decide_candidates`; it does not send pixels to a model or network. Masked screenshots are rejected for this mode. For an advisory Jev recommendation, pass the returned `frame_id` as `observation_id` and explicitly set `allow_incomplete_coverage=true`. The boxes are proposals, not a full inventory: local edge/OCR recall was low on the general Anvil screenshot, so this path must not be presented as all-interactable discovery or verified clickability.

```json
{
  "tool": "game_screenshot",
  "arguments": { "app": "Game window", "bring_to_front": false, "include_local_fast_candidates": true, "local_candidate_method": "edge" }
}
```

Then call `game_jev_decide_candidates` with the returned `frame_id`, `goal`, and `allow_incomplete_coverage=true`. Jev receives the local candidate boxes, image/screen coordinate mappings, and an explicit incomplete-coverage warning; no input is dispatched. The screenshot response also includes `ui_observation.candidate_set_json` for tools that need to fuse this edge list with another frame-bound detector.

### App actions and screen-state memory

`app_action_catalog_import` accepts a bounded JSON catalog of controls and provenance; `app_action_catalog_get` returns it by stable `app_id`, version, profile, and keyboard layout. The caller or its background research workflow discovers controls from app configuration, documentation, and web sources, then imports evidence-backed entries. Preconditions and provenance travel with each candidate. A source-attested saved binding does not prove the active profile, keyboard layout, or runtime effect. DCU does not scan arbitrary files or browse the web itself. Catalogs persist as user-local JSON in `%LOCALAPPDATA%\shadow-use\action-catalogs.json`; API keys are never stored there. Unverified bindings remain visible as research leads but are not offered as executable key choices.

`screen_state_get` reads the persisted app-agnostic screen graph, recent actions, controls, UI labels/normalized boxes, context regions, and transitions from `%LOCALAPPDATA%\shadow-use\screen-map.json`. DCU stores no screenshot pixels or raw OCR clues. A transition starts as unverified after input was reported and another screen was observed; `screen_transition_report` can add a caller-reported success/failure and confidence. `screen_state_invalidate` and `app_action_catalog_invalidate` remove stale app knowledge.

For a known menu with usable saved keybindings, `game_jev_decide_known_state` asks the configured JEV/OpenAI decision backend using text state only—no screenshot, image, or coordinates. The caller must supply the currently believed screen ID/state; the result is advisory and does not send input. For mapped static UI, `game_screenshot(use_cached_candidates=true, app_id=..., app_version=..., profile=..., keyboard_layout=..., screen_hint=...)` captures a fresh frame and hashes the saved regions locally. `screen_hint` must exactly match the persisted semantic screen to prevent reuse of shared navigation anchors from a different screen. DCU reuses boxes only when the app/layout fingerprint matches, every required anchor matches, and at least two anchors were stable across three observations. A cache miss returns the screenshot without candidates, so the caller can analyze that same frame once. Cached boxes remain advisory and incomplete; masked frames are refused. Set `requires_fresh_visual_localization=true` for dynamic/spatial actions such as placement, aiming, dragging, or moving-target selection; those actions require fresh visual candidates.

Set `DecisionBackend` to `jev` for text-only TypeSafe/JEV or `openai` for the OpenAI Responses API. This selector applies to native, browser, and game JEV decisions. `JevMode` must be `assist` or `fast_loop`. OpenAI uses `OPENAI_API_KEY` from the DCU process environment and the configurable `OpenAiModel`; fresh visual game decisions include the current JPEG, while cached-anchor and known-state decisions do not upload an image. Native and browser decisions use structured observations without screenshot input. The OpenAI request uses structured output constrained to listed action IDs. The backends only return recommendations; they never dispatch input. `game_jev_decide*` is recommendation-only. Existing native/browser `jev_act` may dispatch only through its explicit `fast_loop` flow, with the same freshness, confidence, and target checks as JEV mode; selecting a backend alone does not enable input. `get_app_state` continues to expose UIA accelerator/access-key properties; existing UIA and browser deterministic actions remain available without JEV.

To record native input, pass the matching `app_id`, `source_observation_id`, and `action_id` to `press_key` or `click`. To record game input, pass `app_id`, `source_frame_id`, and `action_id` to the `game_*` input tool. These arguments add history and frame checks; they do not grant action authorization.

For a locally installed OmniParser YOLOv9-E checkpoint, `tools/ui_candidate_detect.py` emits a frame-bound candidate set; pass `--frame-id` from the same `game_screenshot` call and use the exact saved JPEG bytes at their original dimensions. `tools/ui_candidate_fuse.py` validates the YOLO and edge frame IDs/dimensions, suppresses near-duplicate YOLO boxes, and returns one incomplete JSON inventory suitable for `game_jev_decide_candidates(candidate_set_json=..., allow_incomplete_coverage=true)`.

```powershell
python tools/ui_candidate_detect.py .\capture.jpg --weights .\local-model\model.pt --frame-id <frame_id> --confidence 0.001 --max-candidates 160 --output-json .\yolo.json
python tools/ui_candidate_fuse.py --yolo-json .\yolo.json --edge-json .\screenshot-result.json --output .\fused-candidates.json
```

The `0.001` confidence, fusion's secondary NMS IoU `0.20`, and 27 local-box limit are sample-tuned settings, not universal defaults. An earlier full-budget Anvil run contained 239 proposals and geometrically matched eight marks transferred from a different night-state screenshot. The `location-alert-action` mark is absent from the clean inference frame; count only the seven stable controls as approximate transfer evidence. The C# edge-only component test passed 7/7 at both resolutions; the saved Python edge-only mirror scored 6/7 at 1280 and 7/7 at 1536. Replaying the exact GDI+ JPEG used by the C# test through the same Python worker reproduced its chat-input box (718×48, secondary-NMS rank 156); Pillow-reencoded JPEGs omitted that box at the 160-YOLO cap, while 192 proposals recovered it. This localizes the mirror discrepancy to input encoding/ranking. Separately, the full GameTools screenshot-to-fake-Jev path on Anvil missed the chat input and lower-left control at 1280 input size, but reached all seven stable controls at 1536. These transfer marks remain approximate. The 1536 edge-only worker round-trip was about 158 ms slower than 1280 in the benchmark, with GPU load varying. Extra detections were not scored. These are not same-frame recall, exhaustive discovery, or proof of clickability. A prior standalone warm YOLO run measured 264 ms median / 411 ms p95, excluding model load and later persistent-worker latency.

For in-application local detection, `game_screenshot` has `include_local_yolo_candidates=true`. It starts the configured worker before capture, so cold model loading does not age the screenshot. The resident Python/TorchScript process analyzes the exact screenshot JPEG; the owned `ScreenDiscoveryService` fuses its proposals with generic OCR/edge candidates. The generic path never invokes Bloons-specific layout logic. The default `local_candidate_method="edge"` avoids a slow Windows OCR pass; set it to `"combined"` or `"ocr"` to add text proposals. OCR runs can exceed Jev's 10-second screenshot freshness window, so the follow-up correctly returns `reobserve` when that happens. DCU fuses at most 160 YOLO proposals and 27 local proposals (187 total), binds them to the captured frame, and caches the incomplete result for decision-only Jev. The explicit `include_ui_candidates=true` mode remains the only path that runs the named Bloons calibration. No screenshot pixels go over the network. In `combined` or `ocr` mode, overlapping text and a bounded same-row neighboring OCR phrase can be appended as explicitly unverified clues (for example, a checkbox candidate may carry `V Enable notifications`). Nearby association is limited to the nearest eligible candidate per OCR line and one nearby clue per candidate, with row-distance and 4:1 visual-box/text-box area bounds. Labels can still be wrong; identity remains unverified. The fast edge default performs no OCR, so textless detections stay generic. A post-refactor 960-input CPU smoke test on one unfamiliar non-Bloons synthetic fixture routed owned discovery through the fake Jev endpoint and matched its 13 marked positives under the 10-second freshness limit; no input or network occurred, and unmarked proposals were not scored. This single synthetic check does not establish exhaustive coverage, semantic identity, false-positive rate, or clickability.

`LocalUiYoloImageSize` defaults to 1280 for lower latency; use 1536 when small-control coverage matters more. On the full-resolution component test, C# edge-only fusion matched seven stable Anvil transfer marks at both sizes. On the actual GameTools capture path, a 1280×719 static window matched all seven only at 1536 input; 1280 input missed two. The settings fixture's full screenshot-to-fake-Jev path matched all 14 controls at both sizes without caller-supplied boxes. Three consecutive full-size `combined` OCR runs stayed under the 10-second freshness window and sent nearby clues for the checkbox, update toggle, and volume control; earlier combined runs have gone stale, so this is not a general latency guarantee. A full-width synthetic UI fixture reached all 13 marked targets in the outgoing fake-Jev request on three edge-only runs at 960 YOLO input; combined OCR at the same dimensions also passed three runs and carried Search/Continue clues. At 1280 input on the same edge capture, one run measured 10,839 ms and failed freshness; a 640-pixel capture passed once. Anvil marks come from a different frame; its absent `location-alert-action` mark is not counted. These limited positive-only results are not a general coverage guarantee.

Configure `settings.json` with an isolated Python environment and a separately obtained checkpoint. The environment needs `torch`, `torchvision`, `numpy`, and `Pillow`; DCU does not download Python packages or weights:

```json
{
  "LocalUiYoloPythonExecutable": "C:\\path\\to\\venv\\Scripts\\python.exe",
  "LocalUiYoloWeightsPath": "D:\\models\\icon_detect_v3\\model.pt",
  "LocalUiYoloDevice": "cuda",
  "LocalUiYoloImageSize": 1536
}
```

```json
{
  "tool": "game_screenshot",
  "arguments": {
    "app": "Game window",
    "max_width": 1920,
    "bring_to_front": false,
    "include_local_yolo_candidates": true
  }
}
```

For an advisory pick, immediately call `game_jev_decide_candidates` with the returned `frame_id` as `observation_id` and `allow_incomplete_coverage=true`; omit `candidate_set_json` to use the cached inventory. The normal 10-second freshness and window-geometry checks still apply. Startup failures return the screenshot and an explicit local-candidate error; DCU does not retry or substitute another model. The current generic local cap is 160 YOLO plus up to 27 local proposals (187 total); local edge proposals are the fast default, and this path does not run Bloons-specific layout logic. Separate legacy component tests exercise the explicit BTD2 calibration (23 anchors), plus clean Anvil (seven approximate stable controls) and two synthetic screens (13 selection targets plus OCR clues; 14 settings targets). A separate opt-in tool-level smoke test called `GameTools.GameScreenshot(include_local_yolo_candidates=true)` and then `GameJevTools.GameJevDecideCandidates` using the cached inventory without caller-supplied boxes; it passed on a static settings fixture at both 1280 and 1536 YOLO input sizes, matched all 14 known controls in the outgoing request, stayed inside the 10-second freshness limit, and sent no input. `local_candidate_method="combined"` adds OCR but may exceed that limit and trigger a safe reobserve response. A separate UIA fake-Jev smoke test verified button, text-field, and checkbox bounds with a complete UIA projection. These checks do not prove general visual completeness, semantic identity, or actionability. The running MCP server was not restarted; no live game capture, real Jev request, or game input was performed. A post-refactor CPU smoke test also ran the unfamiliar non-Bloons synthetic fixture through owned local discovery without caller candidate JSON; 13/13 and 14/14 marked positives matched on the synthetic and settings fixtures, respectively, with the 960-input passes under 10 seconds. Unmarked proposals were not scored. Florence captioning was evaluated locally but produced weak labels and was not integrated. Python dependencies and model weights are not distributed with DCU. See `docs/ui-perception-comparison.md` for methodology, results, and limitations.

```json
{
  "frame_id": "frame-...",
  "image_width": 1680,
  "image_height": 975,
  "scope": "All visible controls on the current game menu",
  "candidate_set_complete": true,
  "coverage_evidence": "Visual parser scanned the entire screenshot for text, pictorial controls, and uncertain regions.",
  "candidates": [
    {"key":"ready","role":"button","label":"Ready!","identity_source":"upstream_visual_parser","bbox":{"x":1185,"y":740,"width":290,"height":130}}
  ]
}
```

The general orchestrated path is one screenshot pass followed by one Jev POST. `game_screenshot` returns the JPEG, `frame_id`, and exact output dimensions; a vision-capable outer assistant can inspect that image and emit `candidate_set_json` without adding a local model. An optional `include_vlm_candidates=true` path sends the exact JPEG in a standard MCP `sampling/createMessage` request and caches a validated model-generated inventory; then `game_jev_decide_candidates` can omit `candidate_set_json`. This requires the connected MCP client to advertise sampling and handle image content. The Pi MCP client inspected for this setup does neither, so its optional sampling path fails safely with `visual_candidate_error`; the screenshot image is still returned for outer-assistant analysis. Regression coverage uses a local fake sampling handler and fake Jev endpoint, not a live model provider. Use a prompt such as:

> Inspect this exact full-frame screenshot. Return the required candidate-set JSON only. Enumerate every visible UI-like target in the declared scope, including textless icons, cards, possible profile controls, and arrows. Use screenshot-image pixel boxes; give pictorial targets short distinguishing descriptions; do not claim official identity, clickability, or enabled state. Include every plausibly actionable region as a candidate even when uncertain. Set `candidate_set_complete=false` and a specific `candidate_set_issue` for occlusion, masks, truncation, or any suspected miss. If claiming complete coverage, include a brief `coverage_evidence` note describing what you inspected. Use `context_regions` only for visual context that is not a possible target.

This is caller-generated evidence, not detector proof. Jev receives structured descriptions and geometry, not screenshot pixels; textless pictorial controls therefore need distinguishing upstream visual descriptions, which remain unverified.

DCU now bundles a model-free edge/OCR heuristic candidate stage and can route its frame-bound boxes into an explicitly incomplete, advisory Jev request. Its recall is not sufficient to claim general coverage. The opt-in MCP sampling path still delegates image understanding to the connected client/provider; no general caption model is bundled. A separate offline benchmark uses an OmniParser YOLOv9-E weight and Torch runtime installed outside this repository; that executable and checkpoint are not shipped with DCU. The detector checkpoint has a separate MIT license file, while the upstream source repository has a CC BY 4.0 root license despite an MIT README badge. A separately authorized Florence/OmniParser caption experiment used artifacts and dependencies outside the repository; its weak outputs were not integrated or included with DCU. An isolated `google/embeddinggemma-2` crop-labeling probe matched 13/27 known controls in a closed-set test and took 14.277 s to load, so it also was not integrated; this does not demonstrate open-world identification. No model weights or Python packages were added to the production project.

`execute_sequence` with `game_*` steps returns compact results by default. Target metadata that is identical on every step that reports it (`target_lifetime`, `input_ownership`, `image_scale`, `pace`, `frame_dimensions` and similar) appears once under `shared`. Null fields and zero timings are left out, and the step name is not repeated inside each result. Each step keeps its own interruption details, `elapsed_hold_ms`, `attempt_count`, `input_sent`, `frame_id` and `image_count`, and images attach in step order. Pass `compact=false` for every field on every step. When a game sequence stops on a failed step, the response attaches a last frame (a `game_wait` timeout supplies its own, otherwise one is captured without sending input) and `stop_frame.image_index` points to it.

Validation errors name the valid values and include a `usage` line. Capture and OCR are serialised and retried once, and an unexpected failure returns `retry_hint`, releases held keys, and a failed OCR call rebuilds the OCR engine and retries once.

Bare `shift`, `ctrl`, `alt`, `insert`, `capslock`, `numpad0-9` and `f1-f24` also work as `press_key` keys in every mode.

---

<p align="center">
<b>Desktop Computer Use (DCU)</b> — designed and built by <a href="https://github.com/dev-willbird1936">dev-willbird1936</a>.<br>
MIT licensed. Keep the LICENSE and NOTICE files when redistributing or rebranding.
</p>
