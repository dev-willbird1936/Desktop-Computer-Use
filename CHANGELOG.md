# Changelog

## 0.1.2 (release candidate)

This candidate updates desktop control and adds explicit foreground/game input.
JEV, the browser adapter, learned visual discovery, and alternative
kernel/hardware/memory input backends remain experimental.

### Added

- Independent `UseRealCursor`, `GameMode`, `HumanizedInput`, and `FastInput`
  settings on main. Background remains the default; game/humanization settings
  never silently take the real cursor.
- Per-call background, foreground, plain-foreground, and humanized-foreground
  mode selection plus pacing overrides without a configuration restart.
- Nine always-callable game/native-surface tools for screenshots, input,
  Windows OCR, HUD bars, and visual/text waiting. No JEV/model/driver dependency.
- Cross-process per-window action coordination and same-window atomic sequences;
  only foreground actions additionally share the one Windows cursor.
- Isolated window UIA workers, bounded observation history, instance-specific
  element IDs, and per-frame image scale so other agents' observations do not
  immediately invalidate an existing action reference.
- A shipped settings launcher/script and an owned multi-agent regression launcher.

### Improved

- Select exact windows when one process owns multiple windows; reject ambiguous
  selectors instead of controlling the wrong target.
- Resolve element bounds again before acting, and translate snapshot coordinates
  after a window moves.
- Bound screenshot workers and window-message waits; keep visible-screen capture
  disabled unless explicitly requested.
- Hide the virtual cursor after one minute of inactivity.
- Provide Windows setup, test, stress-test, and local release-packaging launchers.

### Fixed

- Send Ctrl/Alt-modified character mappings as text rather than simulated shortcut
  keys. In particular, typing a newline must not send an unintended Enter keydown
  to a Chromium form.
- Stop per-character typing promptly when the caller cancels.
- Remove address-bar tracking entries after their windows close.
- Separate late-bitmap cleanup tests from concurrent GDI+ reads.
- Handle nameless UIA nodes during text waits instead of aborting with a null
  reference error.
- Release bounded key/button holds on interruption and report rejected cleanup.
- Preserve game frame metadata across request-scoped MCP tool instances.
- Honor process-level APPDATA overrides for independent client profiles.

### Release preparation

- Build and test Windows candidates in GitHub Actions, with read-only repository
  permissions and commit-pinned actions.
- Validate the packaged executable through MCP initialization, the 21-tool
  desktop/game catalogue, a read-only health check, and clean shutdown.
- Package a self-contained Windows x64 executable with license, notice, README,
  optional Python settings utility, and SHA-256 checksums.
- Keep the MCP-reported version consistent with the assembly version.

### Limitations

The executable is unsigned. Raw-input games need foreground input; protected
applications can still reject normal SendInput. Elevated/minimized targets and
some captures retain documented limits. The UIA text fallback can activate a
target. Desktop/bounds guards remain opt-in; password fields are accessible.
A permanently blocked UIA provider can require a restart for its window, but
other windows have separate workers. MCP host registration/tool inheritance
remains outside DCU's control; simultaneous independent system cursors do not
exist. Foreground input is an explicit takeover, not background user coexistence.

## 0.1.1

Previous published release. See the GitHub release history for its original notes.
