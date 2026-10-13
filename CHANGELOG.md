# Changelog

## 0.1.2 (release candidate)

This candidate updates the stable background desktop server. Experimental JEV,
browser-adapter, game-input, and perception work is not included.

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

### Release preparation

- Build and test Windows candidates in GitHub Actions, with read-only repository
  permissions and commit-pinned actions.
- Validate the packaged executable through MCP initialization, the 12-tool
  catalogue, a read-only health check, and clean shutdown.
- Package a self-contained Windows x64 executable with the license, notice, and
  README, plus SHA-256 checksums.
- Keep the MCP-reported version consistent with the assembly version.

### Limitations

The executable is unsigned. Raw-input games, elevated targets, minimized windows,
and some hardware-accelerated captures remain subject to the README's known
limits. The UIA text fallback can briefly activate a target. Desktop checks and
bounds guards remain opt-in; automation can interact with password fields. A
permanently blocked UIA COM call can require restarting DCU; worker-replacement
experiments are not part of this candidate.

## 0.1.1

Previous published release. See the GitHub release history for its original notes.
