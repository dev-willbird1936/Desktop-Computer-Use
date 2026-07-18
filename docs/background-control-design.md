# Reverse-Engineering: Background Desktop Control (Windows) — Design Notes

How the reference client controls your desktop **in the background** — without moving your real
cursor, without stealing focus, and without the target app ever counting itself
as focused — while showing a second, fake cursor so you can watch it work.

Sources:
- `other-repos/reference-cu/bin/the reference helper binary` (1.69 MB Rust binary,
  "Windows Computer Use Helper" v0.1.0, `helper_symbols.pdb`) — string-table forensics
- `other-repos/reference-cu/plugin/` — official bundled plugin (`computer-use@official-bundled`,
  proprietary, from a private monorepo `project/cua/sky_js`)
- `other-repos/client-src` — open-source client repo (integration surface only)
- `other-repos/open-reference-cu` — third-party open-source recreation
  (confirms the mechanism independently)

---

## 1. Where it lives

the reference client's OSS repo contains **only hooks**: a `computer_use` feature flag
(`client-rs/features/src/lib.rs`), `allowLockedComputerUse` requirements
plumbing, a reserved `computer` tool namespace, and a hardcoded plugin ID
`computer-use@official-bundled`. The implementation is closed-source and ships as:

```
%LOCALAPPDATA%\the vendor\the reference client\runtimes\cua_node\<hash>\bin\node_modules\@oai\sky\bin\windows\the reference helper binary
```

It talks to the the reference client app over a **named pipe** (`\\.\pipe\<vendor>-computer-use-<uuid>`,
env `VENDOR_CUA_NATIVE_PIPE_DIRECTORY`). Turn lifecycle is tracked with named events
(`Local\the reference clientComputerUseTurnEnded-…`) and a `turn-ended` notify hook.

---

## 2. The core trick: never touch the hardware input pipeline

All conventional automation tools (`Windows-MCP`, `win32-mcp-server`, `WinapiMCP`,
and the physical fallbacks in `mcp-windows`) drive input through
`SetCursorPos` + `mouse_event`/`keybd_event`/`SendInput`. That goes through the
OS input queue, so it **moves the real cursor**, generates `WM_ACTIVATE` /
`WM_MOUSEACTIVATE`, and steals foreground. That's why those tools interrupt you.

the reference client never calls those. The binary's string table shows exactly two input channels:

### Channel A — UI Automation patterns (primary)

`uiautomationcore.dll`, "create UIAutomation", "accessibility monitor"
(`src\accessibility\monitor.rs`), a full UIA control-type table, and
element-targeted actions. UIA patterns like `InvokePattern.Invoke()`,
`TogglePattern.Toggle()`, `SelectionItemPattern.Select()`,
`ScrollPattern.Scroll()`, `ValuePattern.SetValue()` execute **inside the target
process** via COM. No message is posted to the OS input queue, no activation
occurs — the app handles a semantic action it cannot distinguish from an
accessibility-tool user.

### Channel B — window messages (fallback)

Confirmed by the open-source recreation (`open-reference-cu/runtime.ps1`),
which documents the same cascade "UIA patterns first, window messages fallback"
in its server instructions, verbatim, as a match for the official experience:

```powershell
PostMessage($hwnd, $WM_MOUSEMOVE, 0, $lParam)
PostMessage($hwnd, $WM_LBUTTONDOWN, $downFlag, $lParam)
PostMessage($hwnd, $WM_LBUTTONUP, 0, $lParam)
```

`PostMessage`/`SendMessage` deliver input **directly to the target window's
message queue**. The OS cursor, keyboard focus, and foreground state are never
involved. Screen coordinates become client coordinates via `ScreenToClient`.

Because the OS foreground never changes, an app that watches for focus loss
(e.g. "pause when I alt-tab") **never sees a focus event** — and you clicking
around in *other* apps doesn't take foreground away from the automation either,
because it never had it.

### The typing trick (focus-free text entry)

From the recreation (same constraint the official one solves): find the
**child edit HWND** under the target element, then:

```powershell
SendMessage($hwnd, $EM_SETSEL, -1, -1)      # caret to end — no focus needed
SendMessage($hwnd, $EM_REPLACESEL, 1, $text) # insert text at caret
```

`EM_REPLACESEL` mutates the edit control's buffer directly. The control never
needs keyboard focus, so your own typing elsewhere is undisturbed. Fallback
chain: UIA `ValuePattern` (careful — plain `SetValue` append was observed to
foreground Notepad in the recreation's testing, so it's gated) → `WM_CHAR`
stream to the main window.

### What does NOT work in background

- Apps reading raw input / `GetAsyncKeyState` (games, some global-hotkey apps)
  are blind to posted messages.
- A posted `WM_LBUTTONDOWN` can still make an app call `SetForegroundWindow`
  on itself — rare, but possible.
- UIA needs an interactive desktop session; lock screen / UAC secure desktop
  is a hard wall (the reference client detects this: "OpenInputDesktop failed", "unlock
  before using computer-use", "target window has higher Windows integrity").

---

## 3. The virtual cursor

The binary contains a dedicated overlay subsystem:

| String | Meaning |
|---|---|
| `the reference clientComputerUseCursorOverlay` | window class of the fake cursor |
| `src\overlay\mod.rs`, `src\overlay\cursor\motion.rs` | overlay + animated motion model |
| D3D11 / D2D1 / DirectWrite references | GPU-rendered overlay |
| `--system-cursor-manager` (spawned helper), `Local\the reference clientComputerUseSystemCursorManager-` mutex | a **separate helper process** that suppresses the real system cursor near the action and re-suppresses after display changes |
| `VENDOR_CUA_CURSOR_FORCE_WARP` | env escape hatch to use real cursor warping instead |
| `vendor-computer-use-status-pill`, Windows.UI.Composition | the "(vendor) is using your computer" pill |
| "exclude display overlay from capture" | overlay is hidden from its own screenshots |

So the cursor you "see the reference client move" is **pure theater** — a click-through,
topmost, non-activating layered window drawn where the agent is acting, with
an animated motion model (spring physics; the recreation's macOS port uses a
heading-driven path chooser with response/damping curve and a click pulse).
The real input bypasses the cursor entirely.

**Windows port recipe** (the recreation never built this — we will):
`WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE` top-level
window + `UpdateLayeredWindow` (per-pixel alpha) — no focus, click-through,
invisible to capture via `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`.

---

## 4. Screen capture

Not GDI. The strings show **Windows.Graphics.Capture**:
`Direct3D11CaptureFramePool`, `IGraphicsCaptureItemInterop.CreateForMonitor`,
`SetIsCursorCaptureEnabled`, "cached capture sessions", `FrameArrived`.

This gives per-window or per-monitor capture that works even when the window is
occluded (not minimized), is DPI-correct, GPU-fast, and can exclude the cursor
and its own overlay from frames. (`mcp-windows` achieves a lighter version of
the occluded-window case with `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)`.)

---

## 5. Tool surface (from the binary's string table)

Actions: `launch_app`, `activate_window`, `click`, `click_element`, `drag`,
`get_window_state`, `perform_secondary_action`, `press_key`, `scroll`,
`scroll_element`, `set_value`, `type_text`, `list_apps`, `list_windows`,
`get_window`, `close`, `end_turn`, `diagnostic_state`.

Param fields: `element_index`, `appId`, `processId`, `rootHwnd`, `inputHwnd`
(note: **separate root vs input HWND** — it does resolve child input windows,
unlike the recreation's main-HWND-only shortcut), `processName`, `title`,
`bounds`, `snapshotRevision`, `include_text`, `include_screenshot`, `scrollX/Y`,
`zIndex`, `url`.

Secondary actions (UIA): Raise / Scroll Up/Down/Left/Right / Expand / Collapse.

---

## 6. Safety & policy layer (worth copying)

- Per-app approval: `AppApprovalRequest`, `x-oai-cua-approved-app`,
  `riskLevel low/high`, "product policy blocks this app"
- Browser-URL allowlist ("Computer Use is not allowed on the current browser
  URL"), browsers enumerated: msedge, chrome, brave, opera, firefox
- Lock-screen / secure-desktop detection, UAC integrity-level check
- **Esc physical kill-switch** (`computer-use-escape-stop`)
- Stale-geometry guard: "window bounds changed; call get_window_state before
  issuing coordinate input" + `snapshotRevision` consistency
- Request budget header `x-oai-cua-request-budget-ms`

---

## 7. TL;DR for our build

1. **Input** = UIA patterns first → `PostMessage`/`SendMessage` to the window's
   queue second (child HWNDs for edit controls). Never `SendInput`.
2. **Text** = `EM_SETSEL`/`EM_REPLACESEL` on the child edit HWND; `WM_CHAR`
   fallback; UIA `ValuePattern` with care.
3. **Capture** = Windows.Graphics.Capture (or `PrintWindow` fallback) — sees
   occluded windows, excludes our overlay.
4. **Virtual cursor** = layered transparent topmost no-activate click-through
   window with spring-animated motion. Purely cosmetic.
5. **Identity** = UIA runtimeId + AutomationId/Name/ControlType fallback;
   revision-stamped snapshots; bounds-changed guard before coordinate input.
6. **Policy** = per-app approval, elevation/lock detection, Esc kill-switch.


---
*Documentation for Desktop-Computer-Use by [dev-willbird1936](https://github.com/dev-willbird1936/Desktop-Computer-Use) — MIT licensed.*
