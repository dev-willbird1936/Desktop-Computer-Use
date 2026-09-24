#!/usr/bin/env python3
"""F13 (application API and GUI mixing) regression for the current DCU build.

Launches the owned "api_mixing" WindowStressFixture: a native ListBox reachable through TWO
routes -- an ordinary GUI button click, and a loopback-only HTTP "documented API" that mutates
the SAME underlying list directly, bypassing the GUI entirely. DCU's existing bounded
`http_request` tool (F32) is reused as the "application API" call mechanism -- no new DCU tool
was added for this feature, only the fixture's own out-of-band API surface.

Verifies:
  - F13-T01: an item added via the API route is visible through a fresh UIA get_app_state
    snapshot with the same verified shape as an item added via the GUI route (click "Add via
    GUI"). Both routes reach the same underlying list; DCU observes the postcondition through
    ONE consistent shape (native UIA), not two different response formats.
  - F13-T02: an application-API call to an unavailable port fails cleanly (a real connection
    refusal, not a silent no-op), and the caller can fall back to the equivalent GUI route
    without losing the intended value (captured via a workflow value, never re-typed as a new
    literal) -- the fallback item is verified present afterward.
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

from multi_window_stress_regression import ConcurrentMcpClient, wait_for_windows


ROOT = Path(__file__).resolve().parents[1]
TFM = "net10.0-windows10.0.22621.0"
DEFAULT_DCU = ROOT / "src" / "ShadowUse" / "bin" / "Debug" / TFM / "dcu.exe"
DEFAULT_FIXTURE = ROOT / "tests" / "WindowStressFixture" / "bin" / "Debug" / TFM / "WindowStressFixture.exe"
DEFAULT_ARTIFACT_ROOT = Path(tempfile.gettempdir()) / "dcu-api-gui-mixing"
API_PORT = 58734
UNAVAILABLE_PORT = 58735


def _settings_environment(artifact_dir: Path) -> dict[str, str]:
    appdata = artifact_dir / "appdata"
    settings_dir = appdata / "shadow-use"
    settings_dir.mkdir(parents=True)
    (settings_dir / "settings.json").write_text(
        json.dumps({"EnableBoundsGuard": False, "EnableDesktopCheck": False, "ShowVirtualCursor": False, "PostActionDelayMs": 0}),
        encoding="utf-8",
    )
    env = dict(os.environ, APPDATA=str(appdata))
    env["DCU_API_MIXING_PORT"] = str(API_PORT)
    return env


def _new_artifact_dir(root: Path) -> Path:
    root.mkdir(parents=True, exist_ok=True)
    while True:
        candidate = root / f"api-gui-mixing-{time.time_ns()}-{os.getpid()}"
        try:
            candidate.mkdir()
            return candidate
        except FileExistsError:
            time.sleep(0.001)


def _require_ok(response: dict[str, Any], operation: str) -> None:
    if "error" in response:
        raise AssertionError(f"{operation} failed: {response}")


def _get_state(client: ConcurrentMcpClient, window_id: str) -> dict[str, Any]:
    result = client.tool("get_app_state", {"app": window_id, "include_screenshot": False, "max_elements": 50})
    _require_ok(result, "get_app_state")
    return result


def _element(state: dict[str, Any], automation_id: str) -> dict[str, Any]:
    for item in state["elements"]:
        if item.get("automation_id") == automation_id:
            return item
    raise AssertionError(f"no element with automation_id={automation_id!r} in: {state['elements']}")


def _list_item_names(state: dict[str, Any]) -> list[str]:
    # WinForms ListBox items surface as child ListItem elements named after their text.
    return [item.get("name") for item in state["elements"] if item.get("type") == "ListItem"]


def _select_owned_window(client: ConcurrentMcpClient, process: subprocess.Popen[str], expected_hwnd: int) -> str:
    listed = client.tool("list_apps")
    if "error" in listed:
        raise AssertionError(f"list_apps failed: {listed}")
    owned = [app for app in listed.get("apps", []) if app.get("pid") == process.pid]
    if len(owned) != 1:
        raise AssertionError(f"expected one owned fixture window, got {owned}")
    expected_hwnd_text = f"0x{expected_hwnd:X}"
    if str(owned[0].get("hwnd", "")).lower() != expected_hwnd_text.lower():
        raise AssertionError(f"fixture window changed between enumeration and list_apps: {owned[0]}")
    window_id = owned[0].get("window_id")
    if not isinstance(window_id, str) or not window_id:
        raise AssertionError(f"owned fixture window has no window_id: {owned[0]}")
    return window_id


def _stop_owned_process(process: subprocess.Popen[str] | None) -> None:
    if process is None or process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)


def _run(client: ConcurrentMcpClient, window_id: str) -> dict[str, Any]:
    state = _get_state(client, window_id)
    if _list_item_names(state) != ["seed-item"]:
        raise AssertionError(f"fixture should start with exactly the seed item: {_list_item_names(state)}")

    # F13-T01: application-API route (reuses F32's bounded http_request, not a new DCU tool).
    api_item = f"api-item-{uuid4().hex[:8]}"
    api_call = client.tool("http_request", {
        "url": f"http://127.0.0.1:{API_PORT}/additem?name={api_item}",
        "allowed_hosts": "127.0.0.1",
        "name": "api_mixing_response",
    })
    _require_ok(api_call, "application-API add via http_request")
    if api_call.get("status_code") != 200:
        raise AssertionError(f"application API add should return 200: {api_call}")

    after_api = _get_state(client, window_id)
    if api_item not in _list_item_names(after_api):
        raise AssertionError(f"API-added item not visible via UIA after refresh: {_list_item_names(after_api)}")

    # F13-T01 (continued): GUI route, verified through the SAME UIA shape.
    gui_item = f"gui-item-{uuid4().hex[:8]}"
    input_field = _element(after_api, "FixtureNewItemName")
    set_input = client.tool("set_value", {"app": window_id, "element_id": input_field["id"], "value": gui_item})
    _require_ok(set_input, "set GUI input field")
    after_input = _get_state(client, window_id)
    add_button = _element(after_input, "FixtureAddViaGui")
    click_add = client.tool("click", {"app": window_id, "element_id": add_button["id"]})
    _require_ok(click_add, "click Add via GUI")

    after_gui = _get_state(client, window_id)
    names_after_gui = _list_item_names(after_gui)
    if gui_item not in names_after_gui:
        raise AssertionError(f"GUI-added item not visible via UIA after refresh: {names_after_gui}")
    if api_item not in names_after_gui:
        raise AssertionError("API-added item disappeared after the GUI route ran -- routes should not interfere")

    # F13-T02: an unavailable application-API route fails cleanly, then falls back to the GUI
    # route without losing the intended value (captured via set_workflow_value, never re-typed
    # as a fresh literal -- proves task state survives the fallback).
    fallback_item = f"fallback-item-{uuid4().hex[:8]}"
    store = client.tool("set_workflow_value", {"name": "api_mixing_fallback_value", "value": fallback_item})
    _require_ok(store, "set_workflow_value for fallback item")
    unavailable_call = client.tool("http_request", {
        "url": f"http://127.0.0.1:{UNAVAILABLE_PORT}/additem?name={fallback_item}",
        "allowed_hosts": "127.0.0.1",
        "name": "api_mixing_unavailable_response",
    })
    if "error" not in unavailable_call:
        raise AssertionError(f"a call to an unavailable application-API port should fail cleanly: {unavailable_call}")

    set_fallback_input = client.tool("set_value", {
        "app": window_id, "element_id": input_field["id"], "value_ref": "api_mixing_fallback_value",
    })
    _require_ok(set_fallback_input, "set GUI input field from the fallback workflow value")
    click_fallback_add = client.tool("click", {"app": window_id, "element_id": add_button["id"]})
    _require_ok(click_fallback_add, "click Add via GUI (fallback route)")

    final_state = _get_state(client, window_id)
    final_names = _list_item_names(final_state)
    if fallback_item not in final_names:
        raise AssertionError(f"fallback-via-GUI item not visible via UIA after refresh: {final_names}")

    return {
        "seed_item_present": True,
        "api_item": api_item,
        "api_call_status_code": api_call.get("status_code"),
        "gui_item": gui_item,
        "both_routes_coexist": True,
        "unavailable_api_call_refused": True,
        "fallback_item": fallback_item,
        "fallback_via_gui_verified": True,
        "final_items": final_names,
    }


def _run_scenario(dcu_executable: Path, fixture_executable: Path, artifact_root: Path) -> dict[str, Any]:
    artifact_dir = _new_artifact_dir(artifact_root)
    result: dict[str, Any] = {
        "scenario": "api_mixing",
        "jev": "off",
        "dcu_executable": str(dcu_executable),
        "fixture_executable": str(fixture_executable),
        "artifact_dir": str(artifact_dir),
    }
    fixture: subprocess.Popen[str] | None = None
    client: ConcurrentMcpClient | None = None
    passed = False
    try:
        fixture = subprocess.Popen([str(fixture_executable), "--scenario", "api_mixing"], cwd=ROOT, text=True)
        hwnd = wait_for_windows(fixture, 1)[0]
        result["fixture_pid"] = fixture.pid
        result["fixture_hwnd"] = hwnd
        environment = _settings_environment(artifact_dir)
        client = ConcurrentMcpClient(dcu_executable, environment, artifact_dir)
        initialized = client.call(
            "initialize",
            {"protocolVersion": "2025-03-26", "capabilities": {}, "clientInfo": {"name": "dcu-api-gui-mixing", "version": "1.0"}},
        )
        if "error" in initialized:
            raise AssertionError(f"MCP initialize failed: {initialized}")
        client.notify("notifications/initialized")
        window_id = _select_owned_window(client, fixture, hwnd)
        result["window_id"] = window_id
        result.update(_run(client, window_id))
        result["passed"] = True
        passed = True
    except BaseException as error:
        result["passed"] = False
        result["error"] = repr(error)
        result["traceback"] = traceback.format_exc()
    finally:
        if client is not None:
            try:
                client.call("shutdown", {})
                client.notify("exit")
                client.close()
            except BaseException:
                pass
        try:
            _stop_owned_process(fixture)
        except BaseException:
            pass
        (artifact_dir / "result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        if result.get("passed") is True:
            print(f"PASS scenario=api_mixing artifact_dir={artifact_dir}")
        else:
            print(f"FAIL scenario=api_mixing artifact_dir={artifact_dir}", file=os.sys.stderr)
            if result.get("traceback"):
                print(result["traceback"], file=os.sys.stderr, end="")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dcu", type=Path, default=Path(os.environ.get("DCU_EXE", DEFAULT_DCU)))
    parser.add_argument("--fixture", type=Path, default=Path(os.environ.get("WINDOW_STRESS_FIXTURE_EXE", DEFAULT_FIXTURE)))
    parser.add_argument("--artifact-root", type=Path, default=DEFAULT_ARTIFACT_ROOT)
    args = parser.parse_args()
    dcu_executable = args.dcu.resolve()
    fixture_executable = args.fixture.resolve()
    if not dcu_executable.is_file():
        raise FileNotFoundError(f"DCU executable not found: {dcu_executable}")
    if not fixture_executable.is_file():
        raise FileNotFoundError(f"fixture executable not found: {fixture_executable}")
    result = _run_scenario(dcu_executable, fixture_executable, args.artifact_root.resolve())
    print(json.dumps(result, indent=2))
    return 0 if result.get("passed") is True else 1


if __name__ == "__main__":
    raise SystemExit(main())
