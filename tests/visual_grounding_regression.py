#!/usr/bin/env python3
"""F05 visual-grounding (OCR fallback) regression for the current DCU build.

Launches the owned "canvas" WindowStressFixture: a single owner-drawn Panel with no
UIA-exposed text or actionable children, so get_app_state's structured UIA evidence is
genuinely inadequate and OCR-based visual grounding must run. Verifies:

  - get_app_state reports visual_grounding.invoked=true with candidates for "Save" (unique,
    confidence 1.0) and two duplicate "Cancel" regions (confidence 0.5 each).
  - click_visual_candidate on the "Save" candidate dispatches a real click: the fixture's
    on-screen "Count: N" text advances (re-observed via a fresh OCR capture) AND an
    out-of-band oracle log file the fixture itself writes on click gets a new line -- two
    independent confirmations that the click landed on the intended control, not just that a
    string re-appeared (F05-T01).
  - After moving the fixture window, click_visual_candidate on the stale (pre-move) candidate
    id is rejected with "reobserve" rather than dispatching a geometrically-wrong click, and
    neither the on-screen counter nor the oracle log advances (F05-T02).
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import traceback
from typing import Any
from uuid import uuid4

from concurrent.futures import ThreadPoolExecutor

from multi_window_stress_regression import (
    ConcurrentMcpClient,
    move_window,
    wait_for_windows,
)


ROOT = Path(__file__).resolve().parents[1]
TFM = "net10.0-windows10.0.22621.0"
DEFAULT_DCU = ROOT / "src" / "ShadowUse" / "bin" / "Debug" / TFM / "dcu.exe"
DEFAULT_FIXTURE = ROOT / "tests" / "WindowStressFixture" / "bin" / "Debug" / TFM / "WindowStressFixture.exe"
DEFAULT_ARTIFACT_ROOT = Path(tempfile.gettempdir()) / "dcu-visual-grounding"


def _settings_environment(artifact_dir: Path, log_path: Path) -> dict[str, str]:
    appdata = artifact_dir / "appdata"
    settings_dir = appdata / "shadow-use"
    settings_dir.mkdir(parents=True)
    (settings_dir / "settings.json").write_text(
        json.dumps(
            {
                "EnableBoundsGuard": False,
                "EnableDesktopCheck": False,
                "ShowVirtualCursor": False,
                "EnableFocusGuard": True,
                "PostActionDelayMs": 0,
            }
        ),
        encoding="utf-8",
    )
    env = dict(os.environ, APPDATA=str(appdata))
    env["DCU_CANVAS_LOG_PATH"] = str(log_path)
    return env


def _new_artifact_dir(root: Path) -> Path:
    root.mkdir(parents=True, exist_ok=True)
    while True:
        candidate = root / f"canvas-{time.time_ns()}-{os.getpid()}-{uuid4().hex[:8]}"
        try:
            candidate.mkdir()
            return candidate
        except FileExistsError:
            time.sleep(0.001)


def _require_ok(response: dict[str, Any], operation: str) -> None:
    if response.get("ok") is not True:
        raise AssertionError(f"{operation} failed: {response}")


def _stop_owned_process(process: subprocess.Popen[str] | None) -> None:
    if process is None or process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)


def _select_owned_window(client: ConcurrentMcpClient, process: subprocess.Popen[str], expected_hwnd: int) -> str:
    listed = client.tool("list_apps")
    if "error" in listed:
        raise AssertionError(f"list_apps failed: {listed}")
    owned = [app for app in listed.get("apps", []) if app.get("pid") == process.pid]
    if len(owned) != 1:
        raise AssertionError(f"expected one owned fixture window, got {owned}")
    expected_hwnd_text = f"0x{expected_hwnd:X}"
    if str(owned[0].get("hwnd", "")).lower() != expected_hwnd_text.lower():
        raise AssertionError(
            f"fixture window changed between enumeration and list_apps: "
            f"expected={expected_hwnd_text} actual={owned[0].get('hwnd')}"
        )
    window_id = owned[0].get("window_id")
    if not isinstance(window_id, str) or not window_id:
        raise AssertionError(f"owned fixture window has no window_id: {owned[0]}")
    return window_id


def _get_state(client: ConcurrentMcpClient, window_id: str) -> dict[str, Any]:
    result = client.tool("get_app_state", {"app": window_id, "include_screenshot": False, "max_elements": 50})
    if "error" in result:
        raise AssertionError(f"get_app_state failed: {result}")
    return result


def _candidates_by_text(state: dict[str, Any], text: str) -> list[dict[str, Any]]:
    grounding = state.get("visual_grounding") or {}
    candidates = grounding.get("candidates") or []
    return [c for c in candidates if c.get("text") == text]


def _log_lines(log_path: Path) -> list[str]:
    if not log_path.exists():
        return []
    return [line for line in log_path.read_text(encoding="utf-8").splitlines() if line.strip()]


def _run_canvas(client: ConcurrentMcpClient, window_id: str, hwnd: int, log_path: Path) -> dict[str, Any]:
    state = _get_state(client, window_id)
    grounding = state.get("visual_grounding") or {}
    if grounding.get("invoked") is not True:
        raise AssertionError(f"visual grounding did not run against the canvas fixture: {grounding}")
    if "visual_ocr" not in (state.get("sources") or []):
        raise AssertionError(f"observation sources did not include visual_ocr: {state.get('sources')}")

    save_candidates = _candidates_by_text(state, "Save")
    if len(save_candidates) != 1:
        raise AssertionError(f"expected exactly one unique 'Save' candidate, got {save_candidates}")
    save = save_candidates[0]
    if save.get("confidence") != 1.0:
        raise AssertionError(f"unique 'Save' candidate should have confidence 1.0: {save}")

    cancel_candidates = _candidates_by_text(state, "Cancel")
    if len(cancel_candidates) != 2:
        raise AssertionError(f"expected two duplicate 'Cancel' candidates, got {cancel_candidates}")
    for cancel in cancel_candidates:
        if cancel.get("confidence") != 0.5:
            raise AssertionError(f"duplicate 'Cancel' candidate should have confidence 0.5: {cancel}")

    if _log_lines(log_path):
        raise AssertionError("oracle log had entries before any click was dispatched")

    invalid_mode = client.tool("wait_for_visual_state", {"app": window_id, "text": "Save", "mode": "sideways", "timeout_s": 5})
    if "error" not in invalid_mode:
        raise AssertionError(f"wait_for_visual_state should reject an unknown mode before polling: {invalid_mode}")

    # F42-T02: the fixture's real UIA-exposed "CanvasCaption" label supports no click
    # operation (informative text only), so route_click must fall back to the visual/OCR
    # adapter for the SAME window rather than terminating -- proves adapter fallback without
    # losing task state, not just that click_visual_candidate works when called directly.
    caption = next((e for e in state["elements"] if e.get("automation_id") == "CanvasCaption"), None)
    if caption is None:
        raise AssertionError(f"expected a UIA-exposed 'CanvasCaption' label in elements: {state['elements']}")
    if caption.get("supported_operations"):
        raise AssertionError(f"CanvasCaption label should support no operations: {caption}")

    # F47-T02: start a REAL polling wait for the post-click counter state BEFORE the click
    # happens (on a background thread via the same concurrent MCP connection), then dispatch
    # the click on the main thread after a deliberate delay. The wait must not resolve until
    # the delayed click actually lands -- proves this is genuine polling, not an instant
    # false-positive match against a state that was already true.
    click_delay_s = 0.6
    wait_started_at = time.monotonic()
    with ThreadPoolExecutor(max_workers=1) as pool:
        wait_future = pool.submit(
            client.tool, "wait_for_visual_state", {"app": window_id, "text": "1", "mode": "appears", "timeout_s": 5}
        )
        time.sleep(click_delay_s)
        click_result = client.tool(
            "route_click",
            {"app": window_id, "element_id": caption["id"], "text_hint": "Save"},
        )
        wait_result = wait_future.result()
    wait_elapsed_s = time.monotonic() - wait_started_at

    _require_ok(click_result, "route_click fallback to visual 'Save' candidate")
    if click_result.get("route") != "visual_ocr":
        raise AssertionError(f"route_click should report route=visual_ocr for an unsupported native target: {click_result}")
    if click_result.get("actionability") != "unverified":
        raise AssertionError(f"visual click result should be marked unverified: {click_result}")

    if wait_result.get("ok") is not True or wait_result.get("found") is not True:
        raise AssertionError(f"wait_for_visual_state(mode=appears) did not find the post-click state: {wait_result}")
    if "1" not in (wait_result.get("matched_text") or ""):
        raise AssertionError(f"wait_for_visual_state matched the wrong text: {wait_result}")
    if wait_elapsed_s < click_delay_s:
        raise AssertionError(
            f"wait_for_visual_state resolved ({wait_elapsed_s:.2f}s) before the delayed click ({click_delay_s}s) "
            "could have landed -- this would mean it matched a pre-existing state, not a genuine wait"
        )

    after_log = _log_lines(log_path)
    if len(after_log) != 1 or "count=1" not in after_log[0]:
        raise AssertionError(f"oracle log did not record exactly one count=1 click: {after_log}")

    # F47: mode=disappears -- the "0" digit that was present before the click must now be
    # reported as gone (proves the negative-match branch, not only the positive one).
    disappears_result = client.tool("wait_for_visual_state", {"app": window_id, "text": "0", "mode": "disappears", "timeout_s": 3})
    if disappears_result.get("ok") is not True or disappears_result.get("found") is not False:
        raise AssertionError(f"wait_for_visual_state(mode=disappears) should report the '0' digit as gone: {disappears_result}")

    # F47: a real timeout -- text that will never appear must report ok:false after roughly
    # the requested timeout, not hang and not return an instant false positive/negative.
    timeout_started_at = time.monotonic()
    timeout_result = client.tool(
        "wait_for_visual_state", {"app": window_id, "text": "no-such-text-ever-xyz", "mode": "appears", "timeout_s": 1}
    )
    timeout_elapsed_s = time.monotonic() - timeout_started_at
    if timeout_result.get("ok") is not False:
        raise AssertionError(f"wait_for_visual_state should time out for text that never appears: {timeout_result}")
    if timeout_elapsed_s < 0.9:
        raise AssertionError(f"wait_for_visual_state returned too quickly for a 1s timeout ({timeout_elapsed_s:.2f}s): {timeout_result}")

    # OCR sometimes splits "Count :" and the digit into separate lines depending on glyph
    # spacing, so match on the bare digit rather than requiring one merged string.
    after_state = _get_state(client, window_id)
    if not _candidates_by_text(after_state, "1"):
        raise AssertionError(f"fresh OCR capture did not show a '1' counter digit after the click: {after_state.get('visual_grounding')}")
    if _candidates_by_text(after_state, "0"):
        raise AssertionError("fresh OCR capture still showed a '0' counter digit after the click")

    # F05-T02: move the window, then try to click a candidate id captured before the move.
    move_window(hwnd, 260, 260, 420, 220)
    time.sleep(0.2)
    stale_click = client.tool(
        "click_visual_candidate",
        {"app": window_id, "candidate_id": save["id"]},
    )
    if stale_click.get("error") != "reobserve":
        raise AssertionError(f"click against a moved window should return 'reobserve', got: {stale_click}")

    post_move_log = _log_lines(log_path)
    if post_move_log != after_log:
        raise AssertionError(f"reobserve rejection must not dispatch a click: log changed to {post_move_log}")

    final_state = _get_state(client, window_id)
    if _candidates_by_text(final_state, "2"):
        raise AssertionError("counter advanced even though the stale click should have been rejected")

    return {
        "save_candidate": save,
        "cancel_candidates": cancel_candidates,
        "click_result": click_result,
        "stale_click_result": stale_click,
        "oracle_log_after_click": after_log,
        "oracle_log_after_stale_click": post_move_log,
        "visual_wait_appears": wait_result,
        "visual_wait_appears_elapsed_s": wait_elapsed_s,
        "visual_wait_disappears": disappears_result,
        "visual_wait_timeout": timeout_result,
        "visual_wait_timeout_elapsed_s": timeout_elapsed_s,
    }


def _run() -> dict[str, Any]:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dcu", type=Path, default=Path(os.environ.get("DCU_EXE", DEFAULT_DCU)))
    parser.add_argument("--fixture", type=Path, default=Path(os.environ.get("WINDOW_STRESS_FIXTURE_EXE", DEFAULT_FIXTURE)))
    parser.add_argument("--artifact-root", type=Path, default=DEFAULT_ARTIFACT_ROOT)
    args = parser.parse_args()

    dcu_executable = args.dcu.resolve()
    fixture_executable = args.fixture.resolve()
    artifact_root = args.artifact_root.resolve()
    if not dcu_executable.is_file():
        raise FileNotFoundError(f"DCU executable not found: {dcu_executable}")
    if not fixture_executable.is_file():
        raise FileNotFoundError(f"fixture executable not found: {fixture_executable}")

    artifact_dir = _new_artifact_dir(artifact_root)
    log_path = artifact_dir / "canvas-clicks.log"
    result: dict[str, Any] = {
        "jev": "off",
        "dcu_executable": str(dcu_executable),
        "fixture_executable": str(fixture_executable),
        "artifact_dir": str(artifact_dir),
    }
    fixture: subprocess.Popen[str] | None = None
    client: ConcurrentMcpClient | None = None
    passed = False
    try:
        fixture = subprocess.Popen(
            [str(fixture_executable), "--scenario", "canvas"],
            cwd=ROOT,
            text=True,
            env=dict(os.environ, DCU_CANVAS_LOG_PATH=str(log_path)),
        )
        hwnd = wait_for_windows(fixture, 1)[0]
        result["fixture_pid"] = fixture.pid
        result["fixture_hwnd"] = hwnd
        environment = _settings_environment(artifact_dir, log_path)
        client = ConcurrentMcpClient(dcu_executable, environment, artifact_dir)
        initialized = client.call(
            "initialize",
            {
                "protocolVersion": "2025-03-26",
                "capabilities": {},
                "clientInfo": {"name": "dcu-visual-grounding", "version": "1.0"},
            },
        )
        if "error" in initialized:
            raise AssertionError(f"MCP initialize failed: {initialized}")
        client.notify("notifications/initialized")
        window_id = _select_owned_window(client, fixture, hwnd)
        result["window_id"] = window_id
        result.update(_run_canvas(client, window_id, hwnd, log_path))
        result["passed"] = True
        passed = True
    except BaseException as error:
        result["passed"] = False
        result["error"] = repr(error)
        result["traceback"] = traceback.format_exc()
    finally:
        if client is not None:
            try:
                if passed:
                    client.close()
                else:
                    client.abort()
            except BaseException as error:
                result.setdefault("cleanup_errors", []).append(repr(error))
        try:
            _stop_owned_process(fixture)
        except BaseException as error:
            result.setdefault("cleanup_errors", []).append(repr(error))
        (artifact_dir / "result.json").write_text(json.dumps(result, indent=2, default=str), encoding="utf-8")
        if result.get("passed") is True:
            print(f"PASS visual_grounding artifact_dir={artifact_dir}")
        else:
            print(f"FAIL visual_grounding artifact_dir={artifact_dir}", file=os.sys.stderr)
            if result.get("traceback"):
                print(result["traceback"], file=os.sys.stderr, end="")
    return result


def main() -> int:
    result = _run()
    print(json.dumps(result, indent=2, default=str))
    return 0 if result.get("passed") is True else 1


if __name__ == "__main__":
    raise SystemExit(main())
