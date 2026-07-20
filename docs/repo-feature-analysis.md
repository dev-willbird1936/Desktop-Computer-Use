# Cross-Repo Feature Analysis

How dcu compares to other public Windows-automation MCP servers, and which
patterns it adopts from each.

## Input mechanism comparison

| Project | Click | Type | Real cursor moved? | Steals focus? | Background-capable? |
|---|---|---|---|---|---|
| **dcu (this project)** | UIA patterns → `PostMessage` | `EM_REPLACESEL` → UIA `ValuePattern` (gated) → `WM_CHAR` | **No** | **No** | **Yes** |
| **mcp-windows** (C#) | UIA patterns first, `SendInput` fallback | `ValuePattern` → focus + `SendInput` UNICODE | only on fallback | **Yes** (deliberate, 5-strategy activation) | partially (pattern path only) |
| **Windows-MCP** (Python) | `SetCursorPos`+`mouse_event` | `keybd_event`/`SendInput`, clipboard paste ≥20 chars | **always** | always | No |
| **win32-mcp-server** (Python) | PyAutoGUI (real injection) | PyAutoGUI / paste | **always** | always | No (except a UIA Invoke tool) |
| **WinapiMCP** (C#) | `SetCursorPos`+`mouse_event` | `WM_CHAR` (but foregrounds first — wasted) | **always** | always | No (message path sabotaged) |
| **e2b open-computer-use** | `xdotool` in a cloud Ubuntu VM | `xdotool type` | n/a (remote VM) | n/a | n/a — different machine model |

## Patterns adopted from each

### mcp-windows (C#) — engineering-quality reference
- Post-action verification: toggle/select state checks, tree-fingerprint change
  detection, typed-value read-back
- Bulk UIA fetch (one COM call per tree) instead of per-element round trips
- Framework-aware traversal depth (Chromium/content-view, WPF, Win32 differ)
- Short element IDs with staged stale re-resolution (runtimeId → tree path →
  name+type)
- `PrintWindow(PW_RENDERFULLCONTENT)` for occluded windows
- Secure-desktop + higher-integrity (elevated target) detection
- The one thing deliberately **not** adopted: its focus-stealing activation.

### Windows-MCP (Python) — feature-breadth reference
- Set-of-Marks annotation: numbered boxes + click-by-label
- Server-side polling for text/element existence and window-active state
- App-launcher and multi-monitor display-filtering patterns

### win32-mcp-server (Python) — safety & batching reference
- `execute_sequence`: batch steps server-side with per-step delay and
  `stop_on_error`
- Coordinate validation against real monitor rects; `health_check`
- Serialized mutation locks to avoid overlapping actions on one target

### WinapiMCP (C#) — UX ideas
- Agent-workflow hints in tool responses
- (Most of its input path foregrounds before messaging, which is exactly the
  interruption dcu is built to avoid.)

### e2b open-computer-use — agent-loop reference
- Perceive → verify → act framing: structured per-step reasoning about what
  changed and whether the objective is complete
- Semantic click interface (name an element, resolve it to coordinates) — UIA
  is dcu's resolver for this

---

## dcu's own design choices

**Core (background-safe input & capture):**
1. UIA-pattern-first input, `PostMessage` fallback, child-HWND targeting
2. Focus-free typing (`EM_REPLACESEL` → gated UIA `SetValue` → `WM_CHAR`)
3. UIA tree snapshot w/ Set-of-Marks labels + framework-aware depth + bulk cache
   fetch
4. `PrintWindow(PW_RENDERFULLCONTENT)` capture with `CopyFromScreen` fallback
5. Virtual cursor overlay (layered/transparent/topmost/no-activate/click-through,
   spring-animated, click pulse), excluded from its own capture
6. Element identity: runtimeId + path + selector re-resolution; revision-stamped
   snapshots; optional bounds-changed guard on coordinate input
7. `wait_for` (tree conditions) + post-action verification + `execute_sequence`
   batching

**Architecture:** resident single-process daemon (no per-call process spawns),
MCP over stdio, C# / .NET — the official MCP C# SDK, UIA COM interop, trivial
P/Invoke for the message layer, single-file self-contained exe.

---
*Documentation for Desktop-Computer-Use by [dev-willbird1936](https://github.com/dev-willbird1936/Desktop-Computer-Use) — MIT licensed.*
