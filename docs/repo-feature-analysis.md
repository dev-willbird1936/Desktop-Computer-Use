# Cross-Repo Feature Analysis

Six reference implementations analyzed (plus the official reference plugin). What each
does, how it works, and what the merged tool takes from each.

## Input mechanism comparison

| Repo | Click | Type | Real cursor moved? | Steals focus? | Background-capable? |
|---|---|---|---|---|---|
| **reference-client-computer-use (official)** | UIA patterns → window messages | child-HWND `EM_REPLACESEL` / UIA | **No** | **No** | **Yes — the whole point** |
| **open-reference-client-computer-use** | UIA patterns → `PostMessage` | `EM_SETSEL`/`EM_REPLACESEL` → `WM_CHAR` | **No** | **No** | **Yes** |
| **mcp-windows** (C#) | UIA patterns first, `SendInput` fallback | `ValuePattern` → focus + `SendInput` UNICODE | only on fallback | **Yes (deliberate, 5-strategy activation)** | partially (pattern path only) |
| **Windows-MCP** (Python) | `SetCursorPos`+`mouse_event` | `keybd_event`/`SendInput`, clipboard paste ≥20 chars | **always** | always | No |
| **win32-mcp-server** (Python) | PyAutoGUI (real injection) | PyAutoGUI / paste | **always** | always | No (except UIA Invoke tool) |
| **WinapiMCP** (C#) | `SetCursorPos`+`mouse_event` | `WM_CHAR` (but foregrounds first — wasted) | **always** | always | No (message path sabotaged) |
| **e2b open-computer-use** | `xdotool` in cloud Ubuntu VM | `xdotool type` | n/a (remote VM) | n/a | n/a — wrong machine |

## What to steal from each

### open-reference-client-computer-use — the background-input reference
- **UIA-pattern-first → PostMessage-fallback cascade** (verified focus-free)
- Child-HWND `EM_SETSEL`/`EM_REPLACESEL` focus-free typing
- runtimeId-based element re-resolution + AutomationId/Name+ControlType fallback
- Env-gated policy: no app launch / no SetFocus / no UIA-text-append by default
- Per-field fault isolation when rendering the a11y tree (one broken element
  never kills a snapshot)
- Post-action: rebuild snapshot and return it with every action result
- **Gaps we must fix:** per-call PowerShell spawn latency (we use a resident
  process); `CopyFromScreen` blind to occluded/minimized windows; clicks posted
  to main HWND only (Electron/multi-HWND apps can drop them); no `WM_CHAR` with
  key presses; no DPI awareness; no virtual cursor on Windows; naive drag.

### official reference plugin — the target experience
- Windows.Graphics.Capture (`Direct3D11CaptureFramePool`), cursor-exclusion
- Virtual cursor overlay (`the reference clientComputerUseCursorOverlay`, D3D/D2D) + status pill
- Separate root vs input HWND (`rootHwnd`/`inputHwnd` fields)
- `snapshotRevision` + "window bounds changed" stale-guard
- Per-app approval, risk levels, browser-URL policy, UAC/lockscreen detection,
  Esc kill-switch, request budgets
- Windows.Graphics.Capture / `PrintWindow` for occluded windows

### mcp-windows (C#) — the engineering-quality reference
- **Post-action verification**: toggle/select state checks, tree-fingerprint
  change detection, typed-value read-back
- `FindAllBuildCache` bulk UIA fetch (one COM call per tree)
- Framework-aware traversal depth (Chromium 15/content-view, WPF 10, Win32 5)
- Short element IDs with 3-level stale re-resolution (runtimeId → tree path → name+type)
- `PrintWindow(PW_RENDERFULLCONTENT)` for occluded windows
- WinRT `Windows.Media.Ocr` — zero-install OCR fallback
- `expectedWindowTitle`/`expectedProcessName` pre-flight before mouse actions;
  `target_window` echo in every input response
- Secure-desktop + higher-integrity (elevated target) detection
- `file_save` dialog automation; app launcher with `--force-renderer-accessibility`
  for Chromium; held-key tracking; deterministic waits
- Window enum: `DWMWA_CLOAKED` filtering, `DWMWA_EXTENDED_FRAME_BOUNDS`,
  `SendMessageTimeout` hang detection

### Windows-MCP (Python) — the feature-breadth reference
- **Set-of-Marks annotation**: numbered colored boxes + `Click(label=N)`
- **`WaitFor` tool**: server-side polling (text_exists / element_exists /
  active_window / element_enabled / focused_element)
- App launcher (Get-StartApps + .lnk + fuzzy + UWP `shell:AppsFolder`)
- Screenshot backend chain (dxcam → mss → Pillow)
- Virtual-desktop awareness; multi-monitor `display=[...]` filtering
- Clipboard-paste typing for long text; capture flash overlay; grid lines

### win32-mcp-server (Python) — the safety & batching reference
- Security profiles (read_only / interactive / unrestricted), `dry_run`,
  confirmation tokens, output redaction, serialized mutation locks, per-tool timeouts
- **`execute_sequence`**: batch up to 50 steps server-side with per-step delay
  and stop_on_error
- `wait_for_text` / `wait_for_window` / `wait_for_idle`, `compare_screenshots`
- OCR engineering: dual-pass mixed-brightness, perceptual-hash cache,
  structured word boxes → `click_text`, `fill_field`, `right_click_menu`
- Coordinate validation against real monitor rects; `health_check`

### WinapiMCP (C#) — UX ideas (only)
- Human-approval permission dialog per operation
- Activity tracking with live monitor GUI
- Agent-workflow hints in tool responses (`typical_next_actions`)
- (Everything else is a cautionary tale: screenshot truncates its own base64,
  no UIA, foregrounds before messaging, CORS wide open with no auth.)

### e2b open-computer-use — the agent-loop reference
- **Perceive → verify → act**: structured per-step thought (what I see / is the
  objective complete / next action + expected outcome)
- Semantic click interface (`grounding(query, screenshot) -> (x,y)`) — the LLM
  names elements, a grounder resolves coordinates; UIA is our grounder
- Annotated action log (screenshot + red dot per action, reviewable HTML)

---

## Merged tool: feature selection

**Core (the reference client parity — background-safe):**
1. UIA-pattern-first input, PostMessage fallback, child-HWND targeting (`inputHwnd`)
2. Focus-free typing (`EM_REPLACESEL` → `WM_CHAR` → gated UIA SetValue)
3. UIA tree snapshot w/ Set-of-Marks labels + framework-aware depth + bulk cache fetch
4. Screenshots: Windows.Graphics.Capture-style per-window capture with
   `PrintWindow(PW_RENDERFULLCONTENT)` fallback → `CopyFromScreen` last resort
5. **Virtual cursor overlay** (layered/transparent/topmost/no-activate/click-through,
   spring-animated, click pulse) + status pill, both `WDA_EXCLUDEFROMCAPTURE`
6. Element identity: session ID + runtimeId + path + selector re-resolution;
   `snapshotRevision`; bounds-changed guard on coordinate input
7. `WaitFor` (tree conditions) + post-action verification + `execute_sequence` batching

**Extras from the breadth repos:** app launcher (Start-Apps + fuzzy + UWP),
window management, clipboard get/set, process list/kill, WinRT OCR
(`click_text`, `find_text`), `compare_screenshots`, health check,
agent-workflow hints in responses.

**Safety:** per-app approval list, elevation + secure-desktop detection,
Esc kill-switch, optional dry_run, coordinate validation, mutation serialization.

**Architecture decision:** resident single-process daemon (no per-call process
spawns), MCP over stdio. Implementation language: **C# / .NET** — official MCP
C# SDK, best-in-class UIA COM interop (proven by mcp-windows), trivial P/Invoke
for the message layer, WinRT OCR for free, single-file exe. The one thing we
deliberately do NOT copy from mcp-windows: its focus-stealing activation.


---
*Documentation for Desktop-Computer-Use by [dev-willbird1936](https://github.com/dev-willbird1936/Desktop-Computer-Use) — MIT licensed.*
