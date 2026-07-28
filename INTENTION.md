# INTENTION

Provide reliable Windows desktop control through MCP without moving the real cursor or redirecting user input. Keep every action scoped to the requested process and window. Fail safely when a target is ambiguous, stale, unresponsive, or not capturable.

# INTENTION MATCHED

- UI Automation runs on a dedicated STA worker with bounded queueing, cancellation, and operation timeouts.
- Text fallback verifies that the focused UIA element belongs to the requested process and root.
- Window messages use bounded timeouts where synchronous replies are required.
- Focus restoration occurs only when the automation target took foreground.
- Window capture does not read unrelated visible screen content unless explicitly allowed.
- App selection rejects ambiguous process names and accepts PID selection.
- Tool inputs, settings values, cached element IDs, and benchmark native structures are validated.
- Build, run, settings, and test batch files support the complete local workflow.

# INTENTION NOT MATCHED

- Windows does not provide a safe way to terminate an arbitrary in-process COM call. DCU returns a timeout and bounds queued work, but a permanently blocked UIA provider can still occupy the single background STA thread until DCU restarts.
- Posted window messages cannot support raw-input applications or guarantee every system-level modifier shortcut.
