# Preparing a release

The release candidate includes desktop, normal/humanized foreground input, and
native game tools. JEV, browser adaptation, learned visual discovery, and
alternative driver/hardware/memory injection remain experimental. Do not merge
the experimental branch wholesale merely to make the release current.

## Local validation and packaging

Requirements: Windows x64, an interactive desktop, the .NET 10 SDK, Python 3, and
PowerShell 7. Packaging does not require a JEV API key, browser installation,
model download, or driver. Windows OCR language availability is reported by the
OCR tool; missing language support does not block unrelated actions.

Use `main` for normal development and release preparation. Keep experiments on
`experimental/jev`; do not merge that branch into `main` unless those changes
have been reviewed for release.

From the `main` checkout:

```powershell
.\release.bat
.\stress-test.bat
.\agent-test.bat
```

`release.bat` runs the Release .NET suite and Python harness tests, publishes a
self-contained executable, and checks the executable over MCP stdio. It produces
a new directory under `publish/candidate-*` containing:

- `dcu.exe`, for existing executable-based MCP configurations.
- `dcu-[VERSION]-win-x64.zip`, including the executable, LICENSE, NOTICE.md,
  README.md, dcu-settings.bat, and scripts/settings_server.py. The optional
  settings page requires Python; the MCP executable remains self-contained.
- `SHA256SUMS.txt`, covering the loose executable and ZIP.

The script refuses to reuse an output directory and does not copy local
`settings.json`, credentials, reference clones, or build caches into the archive.
A failed check leaves its output available for investigation and does not
publish anything.

`stress-test.bat` uses only windows it creates. It checks independent windows,
movement, resize, concurrent actions, foreground preservation, and virtual-cursor
idle cleanup. Do not operate those fixture windows while the test runs.

`agent-test.bat` starts independent MCP processes and owned windows. It checks
snapshot retention, cross-process sequence coordination, independent background
progress, game frame history, explicit foreground/humanized input, and all four
input toggles. A read-only hook verifies zero DCU-injected mouse events during
background phases while allowing the physical user to move their mouse. Only
owned fixtures receive input; original foreground is restored unless the user
switches elsewhere. Avoid operating the system cursor during its short
foreground phase.

Run `dcu.exe --doctor` from the candidate output on the intended target machine.
A successful build alone does not establish desktop access or application
compatibility.

## GitHub checks

The Windows workflow runs for pull requests to `main`, pushes to `main`, and
manual dispatch. It uploads candidate files and test results;
it has no release-publication step or repository write permission.

Hosted-runner checks are not proof that every supported application works. Keep
interactive regression evidence separate from hosted CI results.

## Publication checklist

1. Review the candidate diff against GitHub `main`, its version, changelog,
   checksums, and package contents.
2. With push approval, push only the reviewed `main` branch. Keep
   `experimental/jev` local; do not use `git push --all`. Follow any GitHub branch
   protection or pull-request requirements.
3. Require a passing GitHub Windows workflow and review before publication.
4. Verify the final `main` commit and rebuild the candidate from that commit.
5. With explicit publication approval, create the matching version tag and
   GitHub release. Attach the ZIP, loose executable, and checksums from that
   exact build. Mark a candidate as a prerelease when appropriate.
6. Explain that the executable is unsigned and retain the README's operational
   limits. Test download, checksum verification, extraction, and MCP setup.

Branch pushes, merge, tag creation, and hosted release creation are separate
actions. None is performed by the preparation script. Keep the previous release
available for rollback; binaries can be swapped back without deleting local
settings.
