> **Revision 1.1:** DCU remains fully capable without Jev; Jev is a deeply integrated optional fast controller. Passwords, logins, authenticated sessions and sensitive/private data are normal supported automation use cases.

# DCU implementation specification bundle

Start with **SPEC_DCU_JEV_EVOLUTION.md**. It is self-contained and maps all 60 capabilities to donor mechanisms, checkout paths, implementation requirements and acceptance tests.

## Contents

| File | Purpose |
|---|---|
| `SPEC_DCU_JEV_EVOLUTION.md` | Main implementation specification: 54 core and 6 optional capabilities, 28 reference repositories, 120 specified acceptance cases. |
| `reference-repositories.json` | Machine-readable clone register, donor mechanisms, code-reading anchors and source links. |
| `clone_references.py` | Python/Git helper for cloning or validating read-only reference checkouts under the explicit source root; writes resolved commit locks. |
| `test_clone_references.py` | Offline helper safety tests using temporary synthetic Git repositories. |
| `CODE_MAP.md` | Initial donor source-reading guide. Resolve paths/symbols at the locked checkout before implementation. |
| `capability-status.json` | All 60 features start unassessed. Fill with actual implementation and verification evidence. |
| `DCU_Jev_Computer_Use_Feature_Research.md` | Earlier source catalogue supplied as background. |
| `validation-report.json`, `test-results.txt` | Checks performed while preparing this bundle. These are not DCU runtime results. |
| `SHA256SUMS.txt` | SHA-256 hashes of the bundled files, excluding the hash list itself. |

## Obtain the reference sources

Requires Python 3.10+ and Git. On Windows, supply an explicit absolute root:

```powershell
py -3 .\clone_references.py --root C:\other-software --dry-run
py -3 .\clone_references.py --root C:\other-software
```

On a machine with the requested mount:

```bash
python3 ./clone_references.py --root /other-software --dry-run
python3 ./clone_references.py --root /other-software
```

Use `--only cua,jev-ultrafast,playwright,playwright-mcp,agent-browser` to start with a subset. Run `python -m unittest -v test_clone_references.py` to repeat the helper safety checks.

The helper does not install dependencies, download model weights, recurse through submodules, run upstream programs or update existing checkouts. Wrong origins, dirty trees and locked-commit mismatches are reported, not reset. The first successful checkout records its actual resolved commit under `_index/source-lock.json`; subsequent use preserves that pin. A failed partial clone is left for inspection.

## Evidence boundary

No DCU implementation changes, live Windows desktop tests, paid API calls or donor runtime installations were performed in preparing this bundle. The full clone register was dry-run without network access; helper safety tests used temporary local fixtures. The specification contains source/documentation-derived design references and test requirements, not a claim that all capabilities are already implemented or that every upstream branch has been runtime-validated.
