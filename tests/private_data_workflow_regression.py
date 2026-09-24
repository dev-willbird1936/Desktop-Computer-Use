#!/usr/bin/env python3
"""F40 (rich private-data workflows): copies a field from an owned native desktop
fixture into an authenticated web form fixture, content-verified via a real page
effect, without the value ever passing through Jev or a second literal argument.

Runs jev=off (no Jev API key is present in this process' environment), which is
F40-T02's required baseline; a Jev-on speed comparison is a separate, not-yet-run
benchmark and is not claimed here.
"""

from __future__ import annotations

import argparse
import http.server
import json
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import time
import traceback
from typing import Any
from uuid import uuid4

from multi_window_stress_regression import ConcurrentMcpClient, wait_for_windows
from native_evolution_regression import (
    _new_artifact_dir,
    _select_owned_window,
    _settings_environment,
    _stop_owned_process,
)


ROOT = Path(__file__).resolve().parents[1]
TFM = "net10.0-windows10.0.22621.0"
DEFAULT_DCU = ROOT / "src" / "ShadowUse" / "bin" / "Debug" / TFM / "dcu.exe"
DEFAULT_FIXTURE = ROOT / "tests" / "WindowStressFixture" / "bin" / "Debug" / TFM / "WindowStressFixture.exe"
DEFAULT_ARTIFACT_ROOT = Path(tempfile.gettempdir()) / "dcu-private-data-workflow"
ACCOUNT_FIELD_VALUE = "acct-88213-fixture"

ACCOUNT_FORM_HTML = """<!doctype html>
<html><body>
<div id="banner">Signed in as fixture-user</div>
<input id="account" type="text" value="" />
<button id="save" onclick="document.getElementById('status').textContent = 'Saved: ' + document.getElementById('account').value">Save</button>
<button id="status" disabled>unsaved</button>
</body></html>
"""


class _AccountFormHandler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *_args: object) -> None:  # silence default stderr logging
        pass

    def do_GET(self) -> None:  # noqa: N802 (stdlib override)
        body = ACCOUNT_FORM_HTML.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


class _LoopbackAccountForm:
    def __init__(self) -> None:
        self._server = http.server.HTTPServer(("127.0.0.1", 0), _AccountFormHandler)
        self._thread = threading.Thread(target=self._server.serve_forever, daemon=True)
        self._thread.start()

    @property
    def url(self) -> str:
        return f"http://127.0.0.1:{self._server.server_port}/"

    def stop(self) -> None:
        self._server.shutdown()
        self._server.server_close()
        self._thread.join(timeout=5)


def _require_ok(response: dict[str, Any], operation: str) -> None:
    if response.get("ok") is not True:
        raise AssertionError(f"{operation} failed: {response}")


def _find_candidate(observation: dict[str, Any], role: str, text: str) -> dict[str, Any]:
    # The MCP wire shape exposes no selector, only id/role/name/text, so this fixture-only
    # helper distinguishes same-role controls by their known initial text.
    candidates = observation.get("candidates") or []
    matches = [item for item in candidates if item.get("role") == role and item.get("text") == text]
    if len(matches) != 1:
        raise AssertionError(f"expected exactly one {role!r} candidate with text {text!r}: {candidates}")
    return matches[0]


def _run(dcu_executable: Path, fixture_executable: Path, artifact_root: Path) -> dict[str, Any]:
    artifact_dir = _new_artifact_dir(artifact_root, "private-data-workflow")
    result: dict[str, Any] = {
        "scenario": "private_data_workflow",
        "jev": "off",
        "artifact_dir": str(artifact_dir),
    }
    fixture: subprocess.Popen[str] | None = None
    client: ConcurrentMcpClient | None = None
    form: _LoopbackAccountForm | None = None
    try:
        fixture = subprocess.Popen([str(fixture_executable), "--scenario", "login"], cwd=ROOT, text=True)
        hwnd = wait_for_windows(fixture, 1)[0]
        environment = _settings_environment(artifact_dir)
        client = ConcurrentMcpClient(dcu_executable, environment, artifact_dir)
        initialized = client.call(
            "initialize",
            {
                "protocolVersion": "2025-03-26",
                "capabilities": {},
                "clientInfo": {"name": "dcu-private-data-workflow", "version": "1.0"},
            },
        )
        if "error" in initialized:
            raise AssertionError(f"MCP initialize failed: {initialized}")
        client.notify("notifications/initialized")
        window_id = _select_owned_window(client, fixture, hwnd)
        result["window_id"] = window_id

        state = client.tool("get_app_state", {"app": window_id, "include_screenshot": False, "max_elements": 50})
        if "error" in state:
            raise AssertionError(f"native get_app_state failed: {state['error']}")
        username_element = next(item for item in state["elements"] if item.get("name") == "Fixture username")

        seed = client.tool(
            "set_value",
            {"app": window_id, "element_id": username_element["id"], "value": ACCOUNT_FIELD_VALUE},
        )
        _require_ok(seed, "seed native account field")

        capture_name = f"private-data-account-{uuid4().hex[:8]}"
        capture = client.tool(
            "capture_workflow_value",
            {"app": window_id, "element_id": username_element["id"], "name": capture_name},
        )
        _require_ok(capture, "capture native account field")
        if capture.get("length") != len(ACCOUNT_FIELD_VALUE):
            raise AssertionError(f"captured value has unexpected length: {capture}")

        form = _LoopbackAccountForm()
        opened = client.tool("browser_open", {"url": form.url, "headless": True})
        _require_ok(opened, "browser_open account form")
        session_id = opened["session_id"]
        result["browser_session_id"] = session_id

        account_candidate = _find_candidate(opened, "input", "")

        fill = client.tool(
            "browser_fill",
            {"session_id": session_id, "candidate_id": account_candidate["id"], "value_ref": capture_name},
        )
        _require_ok(fill, "browser_fill account field from captured native value")
        _find_candidate(fill, "input", ACCOUNT_FIELD_VALUE)

        save_after_fill = _find_candidate(fill, "button", "Save")
        clicked = client.tool("browser_click", {"session_id": session_id, "candidate_id": save_after_fill["id"]})
        _require_ok(clicked, "browser_click save")
        _find_candidate(clicked, "button", f"Saved: {ACCOUNT_FIELD_VALUE}")

        closed = client.tool("browser_close", {"session_id": session_id})
        _require_ok(closed, "browser_close")

        result.update(
            {
                "captured_name": capture_name,
                "captured_length": capture.get("length"),
                "account_value": ACCOUNT_FIELD_VALUE,
                "content_verified_fill": True,
                "content_verified_save": True,
                "passed": True,
            }
        )
    except BaseException as error:
        result["passed"] = False
        result["error"] = repr(error)
        result["traceback"] = traceback.format_exc()
    finally:
        if client is not None:
            try:
                if result.get("passed") is True:
                    client.close()
                else:
                    client.abort()
            except BaseException as error:
                result.setdefault("cleanup_errors", []).append(repr(error))
            try:
                if client.process.poll() is None:
                    _stop_owned_process(client.process)
            except BaseException as error:
                result.setdefault("cleanup_errors", []).append(repr(error))
        if form is not None:
            form.stop()
        try:
            _stop_owned_process(fixture)
        except BaseException as error:
            result.setdefault("cleanup_errors", []).append(repr(error))
        (artifact_dir / "result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        if result.get("passed") is True:
            print(f"PASS scenario=private_data_workflow artifact_dir={artifact_dir}")
        else:
            print(f"FAIL scenario=private_data_workflow artifact_dir={artifact_dir}", file=os.sys.stderr)
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
    result = _run(dcu_executable, fixture_executable, args.artifact_root.resolve())
    print(json.dumps(result, indent=2))
    return 0 if result.get("passed") is True else 1


if __name__ == "__main__":
    raise SystemExit(main())
