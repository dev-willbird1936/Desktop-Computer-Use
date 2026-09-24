# Native evolution validation

This phase adds reusable local WinForms regressions to `WindowStressFixture`. The
legacy numeric invocation remains unchanged; the new explicit modes are:

```text
WindowStressFixture.exe --scenario login
WindowStressFixture.exe --scenario ambiguity
```

Both scenarios use only the explicitly supplied DCU and fixture executables. They
create unique per-run artifact directories, select the fixture by its returned
`window_id`, and terminate only processes started by the scenario. They do not use
a browser, an installed account, or Jev. The login fixture uses the synthetic
values `fixture-user` and `synthetic-password-42!`.

## Native lifetime monitor fail-closed behavior

`NativeTargetMonitor` handles `EVENT_OBJECT_DESTROY` on its dedicated message-pumped
WinEvent thread. Before it retires a tracked reference, the callback requires the
tracked owner-thread match and checks that `IsWindow(hwnd)` reports the HWND as gone.
If the HWND is still live (including a possible same-HWND replacement), or the event
cannot be authenticated to a tracked owner thread, the monitor is quarantined. It
leaves all registry references unchanged and `TryTrack` refuses later dispatch while
the monitor is failed. A clean gone-HWND callback retires only the exact references
that were tracked for that owner thread. Tracking also prunes retired registry references
before matching and keeps a small bounded list per HWND. A post-registration health check
removes only the exact failed reference, and UIA dispatch validation confirms the monitor is
still healthy immediately before dispatch.

This is deliberately fail-closed rather than a claim of atomic lifetime protection.
WinEvent delivery is asynchronous, so the gone check and registry invalidation retain
a tiny OS-level TOCTOU residual. No `SetProp` witness is used: arbitrary foreign
windows do not provide guaranteed UIPI-compatible installation or pre-destruction
cleanup, and owner-thread serialization cannot prove that cleanup happened first.

## Lifetime-quarantine validation

The focused monitor/registry/cache/identity run passed 27/27 tests, including owned-window
destroy delivery, recreated-window epochs, disposal, registry reuse, retired-reference
pruning, live-HWND quarantine, post-track monitor quarantine refusal, and the positive
legacy owner-chain alias contract test. The alias test injects a relation predicate and
does not simulate an actual Win32 owner chain. The current full .NET run passed 75/75 tests
with crash diagnostics enabled. Its isolated TRX is
`C:\Users\warcr\AppData\Local\Temp\dcu-native-identity-review-fixes-full-20260921-1946\native-identity-review-fixes-full.trx`.

## Current measured run

Latest hardening build and integrated fixture:

- DCU: `C:\Users\warcr\AppData\Local\Temp\dcu-resolution-hardening-4756047c\bin\dcu.exe`
- Fixture: `C:\Users\warcr\AppData\Local\Temp\dcu-native-fixture-4756047c\bin\WindowStressFixture.exe`
- Login artifact: `C:\Users\warcr\AppData\Local\Temp\dcu-native-evolution-4756047c\login-1790001295733119300-52920-3d2b31c7\result.json`
- Ambiguity artifact: `C:\Users\warcr\AppData\Local\Temp\dcu-native-evolution-4756047c\ambiguity-1790001298784037700-52920-1d811fb7\result.json`

The login scenario passed. Both synthetic values were delivered with `uia_setvalue`,
Sign in used `uia_invoke`, and the fixture reached
`authenticated: fixture-user`. The ambiguity scenario passed: it waited for the
real `Rebuild complete` marker and the `2 duplicate replacements` marker, then a
stale row-2 element returned `Element 'e4' is ambiguous in the current UIA tree.
Re-run get_app_state.` and the fixture remained `untouched`.

Each scenario records foreground HWND and cursor samples before and after the
scenario. Both current endpoint pairs were unchanged (`foreground_hwnd=7018354`,
cursor `[2000, 786]`). These are endpoint observations only. User input can change
them, so they are not continuous strict-background or caret proof.

## Cleanup and assertion remediation run

The native runner was rerun after cleanup and evidence assertions were hardened. It
used a new unique artifact root and the existing latest builds:

- Root: `C:\Users\warcr\AppData\Local\Temp\dcu-native-evolution-remediation-0c7bcfa0`
- Login result: `C:\Users\warcr\AppData\Local\Temp\dcu-native-evolution-remediation-0c7bcfa0\login-1790002876640063700-66216-2b75e82f\result.json`
- Ambiguity result: `C:\Users\warcr\AppData\Local\Temp\dcu-native-evolution-remediation-0c7bcfa0\ambiguity-1790002880068085100-66216-12b2ec88\result.json`

The login result passed with `uia_setvalue` for both synthetic fields and
`uia_invoke` for the sign-in button. The ambiguity result passed with the exact
ambiguous stale-element error for `e4`, two replacement controls, and fixture
status `untouched`. A graceful DCU-close failure now records the error, aborts the
owned DCU, and still runs fixture cleanup; cleanup failures remain in
`cleanup_errors` and prevent a PASS line. The unit tests cover close-timeout
abort, abort-failure process fallback with fixture cleanup, non-native dispatches,
and generic timeout/not-found rejections.

The earlier cursor observations and the initial full-suite test-host crash remain
reported below. No .NET tests were run in this remediation; the parent is
investigating the prior crash in an isolated build.

## Baseline/current evidence

| Feature | Baseline evidence | Current native evidence | Honest limit |
|---|---|---|---|
| F01 | `dcu-multi-window-stress/multi-window-1789997802/result.json`: 27 actions, foreground `7018354 -> 7018354`, cursor `[1466,440] -> [1466,440]`. | `dcu-multi-window-stress/multi-window-1790001316/result.json`: 27 actions, foreground `7018354 -> 7018354`, cursor `[2000,786] -> [2000,786]`. | Endpoint samples do not prove continuous pointer, foreground, caret, or injected-user-input isolation; raw-input-only coverage is absent. |
| F03 | Baseline multi-window run addressed two visible windows in one PID through distinct `window_id` values. | Current run addressed two same-PID windows through distinct IDs, and both new scenarios selected their owned window IDs after `list_apps`. | Browser tabs, process birth identity, target epochs, and reused-handle invalidation are not tested. |
| F09 | `dcu-parent-ambiguity-aeed8a0b/baseline/result.json`: stale row-2 click was accepted and changed state to `row 1 acted`. | Current integrated fixture refused the stale duplicate with an ambiguity error, observed two replacements, and kept status `untouched`. | This covers the native duplicate/refusal seam only; full ancestry/context repair and browser locator coverage remain planned. |
| F37 | Parent baseline synthetic login passed with Jev off and status `authenticated: fixture-user`. | Current integrated login passed with exact username/password `uia_setvalue` operations and a native sign-in invoke. | F37 fast-loop/Jev-on and browser password routes are not tested. |

The F09 baseline failure is retained as evidence of the repaired regression, not
hidden by the current pass. No browser, Jev, or continuous strict-caret claim is
made by this document.

## Commands and outcomes

```text
dotnet build tests/WindowStressFixture/WindowStressFixture.csproj -c Debug --no-restore -o C:/Users/warcr/AppData/Local/Temp/dcu-native-fixture-4756047c/bin --verbosity minimal
# passed: 1 project, 0 errors, 0 warnings

PYTHONDONTWRITEBYTECODE=1 python tests/native_evolution_regression.py --dcu C:/Users/warcr/AppData/Local/Temp/dcu-resolution-hardening-4756047c/bin/dcu.exe --fixture C:/Users/warcr/AppData/Local/Temp/dcu-native-fixture-4756047c/bin/WindowStressFixture.exe --artifact-root C:/Users/warcr/AppData/Local/Temp/dcu-native-evolution-4756047c
# passed: login and ambiguity scenarios

PYTHONDONTWRITEBYTECODE=1 python -c "import sys; from pathlib import Path; sys.path.insert(0, 'tests'); import multi_window_stress_regression as m; m.DEFAULT_DCU=Path(r'C:/Users/warcr/AppData/Local/Temp/dcu-resolution-hardening-4756047c/bin/dcu.exe'); m.DEFAULT_FIXTURE=Path(r'C:/Users/warcr/AppData/Local/Temp/dcu-native-fixture-4756047c/bin/WindowStressFixture.exe'); raise SystemExit(m.main())"
# passed: legacy multi-window runner, 27 actions

dotnet test tests/ShadowUse.Tests/ShadowUse.Tests.csproj --filter FullyQualifiedName~ElementResolutionTests --no-restore --verbosity normal
# passed: 16/16

dotnet test tests/ShadowUse.Tests/ShadowUse.Tests.csproj --no-restore --verbosity normal
# passed on the full rerun: 47/47

PYTHONDONTWRITEBYTECODE=1 python -m unittest discover -s tests -p 'test_*.py' -v
# passed: 4/4

git diff --check
# passed
```

Remediation checks run without .NET tests:

```text
PYTHONDONTWRITEBYTECODE=1 python -m unittest discover -s tests -p 'test_*.py' -v
# passed: 8/8, including four native-evolution cleanup/assertion unit tests

PYTHONDONTWRITEBYTECODE=1 python tests/native_evolution_regression.py --dcu C:/Users/warcr/AppData/Local/Temp/dcu-resolution-hardening-4756047c/bin/dcu.exe --fixture C:/Users/warcr/AppData/Local/Temp/dcu-native-fixture-4756047c/bin/WindowStressFixture.exe --artifact-root C:/Users/warcr/AppData/Local/Temp/dcu-native-evolution-remediation-0c7bcfa0
# passed: login and ambiguity scenarios; result passed=true
```

Review-fix checks (current checkout):

```text
dotnet test tests/ShadowUse.Tests/ShadowUse.Tests.csproj --no-restore --filter 'FullyQualifiedName~NativeTargetMonitorTests|FullyQualifiedName~TargetRegistryTests|FullyQualifiedName~SnapshotCacheTests|FullyQualifiedName~NativeIdentityIntegrationTests' --logger 'trx;LogFileName=native-identity-review-fixes-focused.trx' --results-directory C:/Users/warcr/AppData/Local/Temp/dcu-native-identity-review-fixes-focused-20260921-1945 --verbosity minimal
# passed: 27/27; TRX: C:\Users\warcr\AppData\Local\Temp\dcu-native-identity-review-fixes-focused-20260921-1945\native-identity-review-fixes-focused.trx

dotnet test tests/ShadowUse.Tests/ShadowUse.Tests.csproj --no-restore --blame-crash --logger 'trx;LogFileName=native-identity-review-fixes-full.trx' --results-directory C:/Users/warcr/AppData/Local/Temp/dcu-native-identity-review-fixes-full-20260921-1946 --verbosity minimal
# passed: 75/75; crash-diagnostic attachment enabled; TRX: C:\Users\warcr\AppData\Local\Temp\dcu-native-identity-review-fixes-full-20260921-1946\native-identity-review-fixes-full.trx
```

An initial full-suite invocation aborted after reporting 42 passed tests because
the test host crashed; the immediate verbose rerun completed all 47 tests. The
failed invocation is retained in the phase report rather than represented as a
pass.
