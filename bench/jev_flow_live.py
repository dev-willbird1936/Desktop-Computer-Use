#!/usr/bin/env python3
"""Live Windows harness for the experimental Jev flow loop (`game_jev_flow`).

NOT RUN IN CI and not exercised by the cloud session that wrote it: it needs Windows, the target game window, a Box-capable
Jev model and TYPESAFE_API_KEY. It starts an isolated DCU stdio process (isolated APPDATA/LOCALAPPDATA, so the action book
starts empty and your real settings and books are untouched), then:

  1. health check (confirms the flow is enabled and the model supports Box),
  2. `game_jev_flow_book show` and a preview (`execute=false`: no Jev request, no input),
  3. N real runs of the same goal. Run 1 is COLD (empty book: images, mapping, learning). Runs 2..N are WARM.
     Between runs it waits for you to put the game back on the starting screen (or use --no-prompt if a run ends there itself).

Every run's wall time and the engine's own counters (Jev calls, image calls, bytes, local captures, learned items) are written to
a JSON report. Compare these with the same goal driven by your normal agent-driven DCU loop; time that loop yourself,
because the normal path is an LLM agent that this script cannot drive.

Example (PowerShell):
  $env:TYPESAFE_API_KEY = "..."
  py bench\\jev_flow_live.py --window "Bloons TD Battles 2" --app-id bloons-td-battles-2 `
      --goal "Start a single-player match" --seed-screen main_menu --goal-screen in_game --runs 3

In-match tower placement (game already inside a single-player match, paused at round 1; review bench\seeds\btd2-practice-placement.json first):
  py bench\jev_flow_live.py --window "Bloons TD Battles 2" --app-id bloons-td-battles-2 --seed-file bench\seeds\btd2-practice-placement.json `
      --goal "Select the Dart Monkey, place one on our half of the map, then start the round" --runs 1 --no-prompt
Each call is a bounded serial run of at most --max-actions (default 4) dispatched inputs with a fresh local frame before every decision and no
verification screenshot between them; run it again to continue. Safe refusals (stale frame, ambiguity, low-confidence or out-of-region Box,
invalid placement preview, missing API key) stop the run without guessing and are recorded in the report.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import tempfile
import time
from pathlib import Path
from uuid import uuid4

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tests"))

from multi_window_stress_regression import ConcurrentMcpClient  # noqa: E402

ROOT = Path(__file__).resolve().parents[1]
TFM = "net10.0-windows10.0.22621.0"
DEFAULT_DCU = ROOT / "src" / "ShadowUse" / "bin" / "Release" / TFM / "dcu.exe"


def isolated_environment(artifact_dir: Path, model: str, allow_missing_key: bool) -> dict[str, str]:
    appdata = artifact_dir / "appdata"
    localappdata = artifact_dir / "localappdata"
    (appdata / "shadow-use").mkdir(parents=True)
    localappdata.mkdir(parents=True)
    (appdata / "shadow-use" / "settings.json").write_text(json.dumps({
        "GameMode": True,
        "JevMode": "fast_loop",
        "DecisionBackend": "jev",
        "JevModel": model,
        "JevTimeoutMs": 8000,
        "EnableExperimentalGameJevFlow": True,
        "FastInput": False,
        "HumanizedInput": False,
        "ShowVirtualCursor": False,
        "EnableFocusGuard": True,
    }), encoding="utf-8")
    env = os.environ.copy()
    env["APPDATA"] = str(appdata)
    env["LOCALAPPDATA"] = str(localappdata)
    if not env.get("TYPESAFE_API_KEY") and not allow_missing_key:
        raise SystemExit(
            "TYPESAFE_API_KEY is not set in this shell, so DCU would refuse every Jev call. Refresh the environment of the shell that launches this "
            "script (never put the key in settings or the repo), or pass --allow-missing-key to record the safe-refusal behaviour instead.")
    return env


def need(client: ConcurrentMcpClient, name: str, arguments: dict | None = None, timeout: float = 20) -> dict:
    result = client.tool(name, arguments or {}, timeout=timeout)
    if "error" in result and "ok" not in result:
        raise SystemExit(f"{name} failed: {result['error']}")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dcu", type=Path, default=DEFAULT_DCU)
    parser.add_argument("--window", required=True, help="exact window title of the game")
    parser.add_argument("--app-id", required=True)
    parser.add_argument("--goal", required=True)
    parser.add_argument("--seed-screen", help="screen id the game starts on (e.g. main_menu)")
    parser.add_argument("--goal-screen", help="screen id that means done (optional; learned from the first verified completion otherwise)")
    parser.add_argument("--seed-actions-json", help="optional JSON array of seed actions for --seed-screen")
    parser.add_argument("--seed-file", type=Path, help="JSON file {seed_screen, dynamic_screens, actions[]} (see bench/seeds/); overrides --seed-screen/--seed-actions-json")
    parser.add_argument("--dynamic-screens", nargs="*", default=[], help="screen ids that are animated gameplay screens (in-match)")
    parser.add_argument("--runs", type=int, default=3)
    parser.add_argument("--max-actions", type=int, default=4, choices=[1, 2, 3, 4], help="dispatched inputs per call (bounded serial run)")
    parser.add_argument("--max-runtime-ms", type=int, default=30_000)
    parser.add_argument("--model", default="jev-1.14.0", choices=["jev-1.14.0", "jev-preview"])
    parser.add_argument("--no-prompt", action="store_true", help="do not wait for a manual reset between runs")
    parser.add_argument("--allow-missing-key", action="store_true",
                        help="run without TYPESAFE_API_KEY to record that DCU refuses before any capture, Jev request or input")
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()

    if args.seed_file:
        seed = json.loads(args.seed_file.read_text(encoding="utf-8"))
        args.seed_screen = seed.get("seed_screen", args.seed_screen)
        args.dynamic_screens = seed.get("dynamic_screens", args.dynamic_screens)
        args.seed_actions_json = json.dumps(seed["actions"])
    if not args.dcu.is_file():
        raise SystemExit(f"DCU executable not found (build Release first): {args.dcu}")
    if (args.dcu.parent / "settings.json").exists():
        raise SystemExit(f"Refusing to run with executable-local settings present: {args.dcu.parent / 'settings.json'}")

    artifact_dir = Path(tempfile.gettempdir()) / "dcu-jev-flow-live" / uuid4().hex
    artifact_dir.mkdir(parents=True)
    environment = isolated_environment(artifact_dir, args.model, args.allow_missing_key)
    key_present = bool(environment.get("TYPESAFE_API_KEY"))
    client = ConcurrentMcpClient(args.dcu, environment, artifact_dir)
    report: dict = {
        "kind": "live", "goal": args.goal, "window": args.window, "model": args.model, "runs": [],
        "jev_key_present_in_dcu_process_env": key_present,   # presence only; the value is never read into the report
        "locally_observed_before_this_harness": [
            "A key-only Jev Choice selected W (tower 1) at confidence 1.0; normal DCU game_key sent W and the Dart Monkey was selected.",
            "The tower ghost initially landed on the opponent half ('Wrong Side!'); moving it to the player's half showed a valid preview and a click placed it. Hence the approved region and the fresh-frame preview check.",
            "One masked full-frame Box+Choice call returned the right Choice (1.0) but a Box at confidence 0.74 that was shifted off the intended card. The 0.90 Box confidence gate and, for unconfirmed boxes, the local-proposal agreement check exist to refuse that.",
            "One Dart Monkey alone lost the practice challenge at round 3; this harness tests the action loop, not strategy.",
        ],
        "local_runtime_limitations": [
            "The Windows machine, the game window and the real TypeSafe service are not reachable from the cloud session that wrote this harness; results below are the first real-environment data.",
            "An older local jev.cjs validator rejects Box/assets (its saved contract predates current TypeSafe docs). This harness does not use it: DCU calls the documented endpoint directly.",
            "In-match placement uses reviewed 'place' seeds only (approved region + preview check). Seed regions in bench/seeds/ are examples to be reviewed against your window, not calibrated values.",
        ] + ([] if key_present else [
            "TYPESAFE_API_KEY was absent from the DCU process environment (a key can exist in a user shell and still be missing here). Execute runs are expected to be refused before any capture, Jev request or input.",
        ]),
    }
    try:
        init = client.call("initialize", {"protocolVersion": "2025-03-26", "capabilities": {}, "clientInfo": {"name": "jev-flow-live", "version": "1.0"}})
        if "error" in init:
            raise SystemExit(f"initialize failed: {init}")
        client.notify("notifications/initialized")

        decision = need(client, "health_check").get("decision_settings", {})
        report["decision_settings"] = decision
        if not decision.get("experimental_game_jev_flow_enabled") or not decision.get("jev_model_supports_box"):
            raise SystemExit(f"flow is not enabled or the model lacks Box: {decision}")

        apps = need(client, "list_apps").get("apps", [])
        matches = [a for a in apps if a.get("title") == args.window]
        if len(matches) != 1:
            raise SystemExit(f"expected exactly one window titled {args.window!r}; found {len(matches)}")
        app = matches[0]["window_id"]

        base = {"app": app, "goal": args.goal, "app_id": args.app_id}
        report["preview"] = need(client, "game_jev_flow", {**base, "execute": False, "seed_screen": args.seed_screen}, timeout=60)
        print("preview:", json.dumps(report["preview"])[:400])

        for index in range(1, args.runs + 1):
            if index > 1 and not args.no_prompt:
                input(f"\nPut the game back on its starting screen, then press Enter for run {index}/{args.runs}... ")
            arguments = {**base, "execute": True, "max_actions": args.max_actions, "max_runtime_ms": args.max_runtime_ms,
                         "seed_screen": args.seed_screen, "goal_screen": args.goal_screen,
                         "dynamic_screens": args.dynamic_screens or None}
            if args.seed_actions_json and (index == 1 or args.max_actions < 4):
                arguments["seed_actions_json"] = args.seed_actions_json
            started = time.perf_counter()
            result = need(client, "game_jev_flow", {k: v for k, v in arguments.items() if v is not None},
                          timeout=args.max_runtime_ms / 1000 + 30)
            wall_s = time.perf_counter() - started
            refused = result.get("ok") is False and result.get("no_input_sent") is True
            entry = {"run": index, "phase": "cold" if index == 1 else "warm", "client_wall_s": round(wall_s, 2),
                     "safe_refusal": refused, "no_input_sent": result.get("no_input_sent"),
                     "goal_reached": result.get("goal_reached"), "stop_reason": result.get("stop_reason"),
                     "final_screen": result.get("final_screen"), "stats": result.get("stats"), "steps": result.get("steps"),
                     "error": result.get("error"), "reobserve": result.get("reobserve"), "continue_hint": result.get("continue_hint")}
            report["runs"].append(entry)
            if refused:
                print(f"run {index}: refused safely before any input: {result.get('error')}")
                if not key_present:
                    break   # every further run would be refused identically
            print(f"run {index}: {entry['phase']} wall={entry['client_wall_s']}s goal={entry['goal_reached']} stop={entry['stop_reason']} stats={entry['stats']}")
        report["book"] = need(client, "game_jev_flow_book", {"app_id": args.app_id, "operation": "show"})
    finally:
        client.close()

    out = args.out or (artifact_dir / "report.json")
    out.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(f"report: {out}\ntranscript: {artifact_dir / 'mcp-transcript.jsonl'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
