# Preparing a release

The release candidate is the stable background desktop MCP server. Do not merge
experimental JEV, browser, game-input, or perception changes into a release merely
to make the branch current.

## Local validation and packaging

Requirements: Windows x64, an interactive desktop, the .NET 10 SDK, Python 3, and
PowerShell 7. Packaging does not require a JEV API key, browser installation, model
download, or driver.

From the release checkout:

```powershell
.\release.bat
.\stress-test.bat
```

`release.bat` runs the Release .NET suite and Python harness tests, publishes a
self-contained executable, and checks the executable over MCP stdio. It produces
a new directory under `publish/candidate-*` containing:

- `dcu.exe`, for existing executable-based MCP configurations.
- `dcu-[VERSION]-win-x64.zip`, including the executable, LICENSE, NOTICE.md,
  and README.md.
- `SHA256SUMS.txt`, covering the loose executable and ZIP.

The script refuses to reuse an output directory and does not copy local
`settings.json`, credentials, reference clones, or build caches into the archive.
A failed check leaves its output available for investigation and does not
publish anything.

`stress-test.bat` uses only windows it creates. It checks independent windows,
movement, resize, concurrent actions, foreground preservation, and virtual-cursor
idle cleanup. Do not operate those fixture windows while the test runs.

Run `dcu.exe --doctor` from the candidate output on the intended target machine.
A successful build alone does not establish desktop access or application
compatibility.

## GitHub checks

The Windows workflow runs for pull requests to `main`, pushes to `main` or
`release/**`, and manual dispatch. It uploads candidate files and test results;
it has no release-publication step or repository write permission.

Hosted-runner checks are not proof that every supported application works. Keep
interactive regression evidence separate from hosted CI results.

## Publication checklist

1. Review the candidate diff against GitHub `main`, its version, changelog,
   checksums, and package contents.
2. Confirm the experimental branch stays local. Push only the reviewed release
   branch and open a pull request against GitHub `main`; do not use
   `git push --all`. An older local `main` can still contain experiments.
3. Require a passing GitHub Windows workflow and review before merging.
4. Verify the final merged commit and rebuild the candidate from that commit.
5. With explicit publication approval, create the matching version tag and
   GitHub release. Attach the ZIP, loose executable, and checksums from that
   exact build. Mark a candidate as a prerelease when appropriate.
6. Explain that the executable is unsigned and retain the README's operational
   limits. Test download, checksum verification, extraction, and MCP setup.

Branch pushes, merge, tag creation, and hosted release creation are separate
actions. None is performed by the preparation script. Keep the previous release
available for rollback; binaries can be swapped back without deleting local
settings.
