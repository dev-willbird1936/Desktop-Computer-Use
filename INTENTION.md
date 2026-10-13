# INTENTION

Provide reliable Windows desktop control through MCP without moving the real cursor or redirecting user input. Keep every action scoped to the requested process and window. Fail safely when a target is ambiguous, stale, unresponsive, or not capturable.

# INTENTION MATCHED

- UI Automation runs on a dedicated STA worker with bounded queueing, cancellation, and operation timeouts.
- Text fallback verifies that the focused UIA element belongs to the requested process and root.
- Window messages use bounded timeouts where synchronous replies are required.
- Focus restoration occurs only when the automation target took foreground.
- Window capture does not read unrelated visible screen content unless explicitly allowed.
- App selection enumerates independent top-level windows, rejects ambiguous names and PIDs, and accepts stable `window_id` selection.
- Element frames and raw snapshot coordinates adjust when a target window moves.
- The cosmetic cursor disappears after one minute without an action.
- `tools/list` exposes the core desktop tools and a discovery gateway (`search_tools`, `describe_tool`, `call_tool`); the rest of the catalogue stays callable and carries namespace and safety annotations.
- Tool inputs, settings values, cached element IDs, and benchmark native structures are validated.
- Build, run, settings, standard test, and isolated multi-window stress-test batch files support the complete local workflow.
- `click`, `type_text`, `press_key`, `scroll`, and `drag` never move the real cursor or take the foreground unless a caller explicitly passes `background_mode="isolated_interactive"` or `"humanized"`, or the owner turns on the `HumanizedInput` setting; every other value, and every other tool, stays within the never-move-the-cursor guarantee.

# INTENTION NOT MATCHED

- Windows does not provide a safe way to terminate an arbitrary in-process COM call. DCU returns a timeout and bounds queued work, but a permanently blocked UIA provider can still occupy the single background STA thread until DCU restarts.
- Posted window messages cannot support raw-input applications or guarantee every system-level modifier shortcut. `isolated_interactive` is an explicit, opt-in exception for `click`/`type_text`/`press_key`/`scroll`/`drag`: it deliberately moves the real cursor and takes the foreground with `SendInput`, so the base intention (no real-cursor movement) is knowingly broken only when a caller asks for it by name. `humanized` is the same exception with human-seeming dynamics, and the `HumanizedInput` setting (default off) lets the owner make it the default for calls that pass no `background_mode`.
