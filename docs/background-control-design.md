# Background Desktop Control (Windows) — Design Notes

How dcu controls a Windows desktop **in the background** — without moving the real
cursor, without stealing focus, and without the target app ever counting itself as
focused — while a cosmetic second cursor shows what it's doing. This is a deeper
technical companion to the [README](../README.md); `src/ShadowUse/` is the actual
implementation these notes describe.

---

## 1. The core idea: never touch the hardware input pipeline

Most Windows automation tools drive input through `SetCursorPos` +
`mouse_event`/`keybd_event`/`SendInput`. Those go through the real OS input queue,
so they move the physical cursor, generate `WM_ACTIVATE`/`WM_MOUSEACTIVATE`, and
steal foreground — which is why those tools interrupt whatever you're doing.

dcu never calls those APIs. It uses two channels instead:

### Channel A — UI Automation patterns (primary)

`InvokePattern.Invoke()`, `TogglePattern.Toggle()`, `SelectionItemPattern.Select()`,
`ScrollPattern.Scroll()`, `ValuePattern.SetValue()`, `ExpandCollapsePattern.Expand()`
execute **inside the target process** via COM. No message reaches the OS input
queue and no activation occurs — the app handles a semantic action indistinguishable
from an accessibility tool driving it.

### Channel B — window messages (fallback)

```
PostMessage(hwnd, WM_MOUSEMOVE, 0, lParam)
PostMessage(hwnd, WM_LBUTTONDOWN, downFlag, lParam)
PostMessage(hwnd, WM_LBUTTONUP, 0, lParam)
```

`PostMessage`/`SendMessage` deliver input straight to the target window's message
queue — the actual child window under the point, resolved via `WindowFromPoint`.
The OS cursor, keyboard focus, and foreground state are never touched. Screen
coordinates become client coordinates via `ScreenToClient`.

Because foreground never changes, an app that watches for focus loss never sees a
focus event — and clicking around in *other* apps doesn't take foreground away
from dcu either, because it never had it.

### Focus-free typing

Find the child edit HWND under the target element, then:

```
SendMessage(hwnd, EM_SETSEL, -1, -1)       // caret to end, no focus needed
SendMessage(hwnd, EM_REPLACESEL, 1, text)  // insert text at caret
```

`EM_REPLACESEL` mutates the edit control's buffer directly, so the control never
needs keyboard focus and your own typing elsewhere is undisturbed. Fallback chain:
UIA `ValuePattern` (gated behind `AllowUiaTextFallback` — plain `SetValue` append
can briefly foreground some apps) → `WM_CHAR` stream to the target window.

Chromium's omnibox is a special case: its frame and render-widget have separate
native key routing, so a posted key stream aimed at the render widget can't reach
the address bar. Address-bar edits and navigation go through UIA `ValuePattern`
directly instead, and the result is verified against the control's own value (and
the eventual `RootWebArea` URL) rather than assumed — see
`BackgroundInput.FindChromiumAddressBar`.

### What does not work in background

- Apps reading raw input / `GetAsyncKeyState` (games, some global-hotkey systems)
  are blind to posted messages.
- A posted `WM_LBUTTONDOWN` can still make an app call `SetForegroundWindow` on
  itself — rare, but possible.
- UIA needs an interactive desktop session; lock screen / UAC secure desktop is a
  hard wall.

---

## 2. The virtual cursor

A layered, click-through, non-activating overlay window shows where dcu is acting,
entirely separate from real input:

```
WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE
```

top-level window + `UpdateLayeredWindow` (per-pixel alpha) for the ghost cursor
shape, spring-physics motion between action points, and a click-pulse animation.
`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` hides it from dcu's own
screenshots and from screen recording/sharing. It is pure theater — the real
input bypasses it completely. See `Overlay/VirtualCursorOverlay.cs`.

---

## 3. Screen capture

`PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)` captures a window's own backing
surface even when another window covers it on screen — occlusion doesn't blind
the capture the way a naive `CopyFromScreen` would. `CopyFromScreen` is kept as a
fallback for the (rare) hardware-accelerated surfaces `PrintWindow` can't render.
See `Capture/ScreenshotService.cs`.

---

## 4. Element identity & snapshots

Elements are addressed by UIA `runtimeId`, with `AutomationId`/`Name`/`ControlType`
as a re-resolution fallback when the tree shifts between actions. Every snapshot
carries a monotonic `revision`; element ids from an older revision are rejected
with a structured error rather than silently walking the tree and risking a
wrong-target action. An optional bounds-changed guard (`EnableBoundsGuard`) can
refuse coordinate input if the window moved since the snapshot that produced it.

---

## 5. How this compares to other open-source approaches

See [repo-feature-analysis.md](repo-feature-analysis.md) for a feature-by-feature
comparison against other public Windows-automation MCP servers, and which
patterns dcu adopts from each.

---
*Documentation for Desktop-Computer-Use by [dev-willbird1936](https://github.com/dev-willbird1936/Desktop-Computer-Use) — MIT licensed.*
