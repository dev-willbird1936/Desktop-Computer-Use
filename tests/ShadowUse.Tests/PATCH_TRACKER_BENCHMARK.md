# PatchTracker synthetic stress benchmark

This is an opt-in benchmark of the real `PatchTracker.Update` implementation. It feeds deterministic, self-generated grayscale frames directly to the tracker. It does not capture a desktop or game, send cursor input, or measure `GameVision` capture/preprocessing, coordinate mapping, or `game_follow` cadence.

## Run

PowerShell:

```powershell
$env:DCU_TRACKING_BENCHMARK = '1'
$env:DCU_TRACKING_BENCHMARK_OUTPUT_DIR = 'C:\path\to\benchmark-output'
$env:DCU_TRACKING_BENCHMARK_BASE_COMMIT = (git rev-parse HEAD)
$env:DCU_TRACKING_BENCHMARK_WORKTREE_STATE = 'clean or dirty; describe uncommitted changes'
dotnet test tests/ShadowUse.Tests/ShadowUse.Tests.csproj `
  --configuration Release --no-restore `
  --filter FullyQualifiedName~OptIn_PatchTrackerStressBenchmarkEmitsGroundTruthTrace `
  --logger 'console;verbosity=detailed'
```

Without `DCU_TRACKING_BENCHMARK=1`, xUnit skips the opt-in run. The benchmark emits a run folder containing `manifest.json`, one `trace.jsonl` row per seed/update frame, and sampled pre-tracker input frames as PGM files.

## Workload and measurements

Version `patch-tracker-stress-v1` runs 10 deterministic seeds over 10 families at 640×360: easy translation, diagonal/axial motion up to and beyond the ±24-pixel search radius, structured clutter with near-matches, low local contrast, affine illumination and clipping, scale/shear changes, graduated occlusion and loss/recovery, exact distractors and reacquisition, equal-luma chroma metamers, and frame/lifetime changes. Each sequence has 30 frames: 100 seeded sequences and 2,900 timed updates.

Each update trace records ground truth, target presence/visibility/identity ambiguity, distractors, tracker state/reason, confidence, margin, returned box, **current accepted box** versus **last-known box**, IoU, center error, elapsed update time, allocation, and a sample-frame path when retained. Annotation is separate from tracker input.

The summary reports denominators, correct localizations, false acceptances when the target is absent, wrong localizations, ambiguous/low-visibility acceptances, abstentions, IoU/center error, p50/p95/p99/max update latency, updates/second, allocations/update, GC counts, seeds, runtime, machine details, source hashes, and the base revision/worktree state. Timing is update-only; frame generation and trace/video rendering are excluded. No timing threshold is asserted in CI.

## Interpretation limits

- The tracker receives grayscale pixels. The metamer case maps distinct RGB triples `(v,v,v)` and `(v+30,v-15,v-1)` through the production `299/587/114` integer-luma weights; the grayscale bytes are equal, so color identity is unavailable to this tracker.
- The template and box size are fixed after initialization. Scale, shape, deformation, occlusion, and lighting cases measure degradation; they are not supported guarantees.
- Confidence is a remapped normalized-correlation score, not a calibrated probability.
- Results are synthetic evidence for this exact source hash, seed set, and machine. They do not prove live-game robustness or follow-input safety. A convincing exact distractor can be accepted as the target; a stale last-known box is not a current estimate.
