#!/usr/bin/env python3
"""F44: optional remote DCU transport regression.

Proves settings.json's RemoteTransportUrl genuinely switches DCU from local stdio to a real
HTTP MCP server exposing the SAME desktop tool set (F44-R01/R02), by driving a real owned
WindowStressFixture login through the official `mcp` Python SDK's streamable-HTTP client --
not DCU's own custom stdio test harness. Also proves the default (RemoteTransportUrl omitted)
never binds any network listener at all (F44-T02).
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import socket
import subprocess
import tempfile
import time
import traceback
from pathlib import Path
from typing import Any
from uuid import uuid4

from mcp import ClientSession
from mcp.client.streamable_http import streamablehttp_client

from multi_window_stress_regression import wait_for_windows

ROOT = Path(__file__).resolve().parents[1]
TFM = "net10.0-windows10.0.22621.0"
DEFAULT_DCU = ROOT / "src" / "ShadowUse" / "bin" / "Debug" / TFM / "dcu.exe"
DEFAULT_FIXTURE = ROOT / "tests" / "WindowStressFixture" / "bin" / "Debug" / TFM / "WindowStressFixture.exe"
DEFAULT_ARTIFACT_ROOT = Path(tempfile.gettempdir()) / "dcu-remote-transport"
# Match native_evolution_regression.py's exact accepted credentials -- the fixture's login
# validation checks these specific values, not just "any non-empty string".
SYNTHETIC_USERNAME = "fixture-user"
SYNTHETIC_PASSWORD = "synthetic-password-42!"


def _free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def _port_open(host: str, port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.settimeout(0.25)
        try:
            s.connect((host, port))
            return True
        except OSError:
            return False


def _write_settings(appdata: Path, remote_url: str | None) -> None:
    settings_dir = appdata / "shadow-use"
    settings_dir.mkdir(parents=True)
    payload: dict[str, Any] = {
        "EnableBoundsGuard": False,
        "EnableDesktopCheck": False,
        "ShowVirtualCursor": False,
        "EnableFocusGuard": True,
        "PostActionDelayMs": 0,
    }
    if remote_url:
        payload["RemoteTransportUrl"] = remote_url
    (settings_dir / "settings.json").write_text(json.dumps(payload), encoding="utf-8")


def _start_dcu(dcu_exe: Path, artifact_dir: Path, remote_url: str | None) -> tuple[subprocess.Popen, Path]:
    appdata = artifact_dir / "appdata"
    _write_settings(appdata, remote_url)
    stderr_path = artifact_dir / "dcu-stderr.log"
    process = subprocess.Popen(
        [str(dcu_exe)],
        cwd=ROOT,
        env=dict(os.environ, APPDATA=str(appdata)),
        stdout=None if remote_url else subprocess.PIPE,
        stdin=None if remote_url else subprocess.PIPE,
        stderr=open(stderr_path, "w", encoding="utf-8"),
        text=True,
    )
    return process, stderr_path


def _stop(process: subprocess.Popen) -> None:
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


def _element(snapshot: dict[str, Any], name: str) -> dict[str, Any]:
    return next(item for item in snapshot["elements"] if item.get("name") == name)


async def _call(session: ClientSession, name: str, arguments: dict[str, Any]) -> dict[str, Any]:
    result = await session.call_tool(name, arguments)
    if not result.content or not hasattr(result.content[0], "text"):
        raise AssertionError(f"remote {name} returned no text content: {result}")
    return json.loads(result.content[0].text)


async def _run_http_mode(dcu_exe: Path, fixture_exe: Path, artifact_dir: Path) -> dict[str, Any]:
    port = _free_port()
    url = f"http://127.0.0.1:{port}/"
    process, stderr_path = _start_dcu(dcu_exe, artifact_dir, url)
    fixture: subprocess.Popen | None = None
    try:
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline and not _port_open("127.0.0.1", port):
            if process.poll() is not None:
                raise AssertionError(
                    f"DCU exited before binding the remote transport port (exit={process.returncode}); "
                    f"stderr={stderr_path.read_text(encoding='utf-8', errors='replace')}"
                )
            time.sleep(0.2)
        if not _port_open("127.0.0.1", port):
            raise AssertionError(f"DCU never bound {url}; stderr={stderr_path.read_text(encoding='utf-8', errors='replace')}")

        fixture = subprocess.Popen([str(fixture_exe), "--scenario", "login"], cwd=ROOT, text=True)
        hwnd = wait_for_windows(fixture, 1)[0]

        async with streamablehttp_client(url) as (read, write, _get_session_id):
            async with ClientSession(read, write) as session:
                init_result = await session.initialize()

                listed = await _call(session, "list_apps", {})
                owned = [a for a in listed.get("apps", []) if a.get("pid") == fixture.pid]
                if len(owned) != 1:
                    raise AssertionError(f"remote list_apps did not find exactly one owned fixture window: {owned}")
                window_id = owned[0]["window_id"]

                state = await _call(session, "get_app_state", {"app": window_id, "include_screenshot": False, "max_elements": 50})
                username = _element(state, "Fixture username")
                password = _element(state, "Fixture password")

                set_user = await _call(session, "set_value", {"app": window_id, "element_id": username["id"], "value": SYNTHETIC_USERNAME})
                set_pass = await _call(session, "set_value", {"app": window_id, "element_id": password["id"], "value": SYNTHETIC_PASSWORD})

                state = await _call(session, "get_app_state", {"app": window_id, "include_screenshot": False, "max_elements": 50})
                sign_in = _element(state, "Fixture sign in")
                clicked = await _call(session, "click", {"app": window_id, "element_id": sign_in["id"]})

                for label, payload in (("set_value(username)", set_user), ("set_value(password)", set_pass), ("click(sign_in)", clicked)):
                    if not payload.get("ok"):
                        raise AssertionError(f"remote {label} failed: {payload}")

                after = await _call(session, "get_app_state", {"app": window_id, "include_screenshot": False, "max_elements": 50})
                status_value = _element(after, "Fixture status").get("value")
                if status_value != f"authenticated: {SYNTHETIC_USERNAME}":
                    raise AssertionError(f"remote login did not authenticate over HTTP: {status_value!r}")

        return {
            "remote_url": url,
            "server_name": init_result.serverInfo.name,
            "window_id": window_id,
            "authenticated_status": status_value,
        }
    finally:
        if fixture is not None:
            _stop(fixture)
        _stop(process)


def _run_stdio_default_binds_nothing(dcu_exe: Path, artifact_dir: Path, probe_port: int) -> dict[str, Any]:
    process, stderr_path = _start_dcu(dcu_exe, artifact_dir, None)
    try:
        time.sleep(1.0)
        if process.poll() is not None:
            raise AssertionError(
                f"default (stdio) DCU exited unexpectedly (exit={process.returncode}); "
                f"stderr={stderr_path.read_text(encoding='utf-8', errors='replace')}"
            )
        if _port_open("127.0.0.1", probe_port):
            raise AssertionError(f"default DCU (no RemoteTransportUrl) is listening on {probe_port}, but should bind nothing")

        init = {
            "jsonrpc": "2.0", "id": 1, "method": "initialize",
            "params": {"protocolVersion": "2025-03-26", "capabilities": {}, "clientInfo": {"name": "dcu-remote-transport-stdio-check", "version": "1.0"}},
        }
        assert process.stdin is not None and process.stdout is not None
        process.stdin.write(json.dumps(init) + "\n")
        process.stdin.flush()
        line = process.stdout.readline()
        response = json.loads(line)
        if "result" not in response:
            raise AssertionError(f"default stdio DCU did not respond to initialize: {response}")

        return {"probed_port_closed": True, "stdio_initialize_ok": True}
    finally:
        _stop(process)


def _new_artifact_dir(root: Path) -> Path:
    root.mkdir(parents=True, exist_ok=True)
    while True:
        candidate = root / f"remote-{time.time_ns()}-{os.getpid()}-{uuid4().hex[:8]}"
        try:
            candidate.mkdir()
            return candidate
        except FileExistsError:
            time.sleep(0.001)


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

    result: dict[str, Any] = {"dcu_executable": str(dcu_executable)}
    try:
        result["http_mode"] = asyncio.run(_run_http_mode(dcu_executable, fixture_executable, _new_artifact_dir(artifact_root)))
        result["stdio_default"] = _run_stdio_default_binds_nothing(dcu_executable, _new_artifact_dir(artifact_root), _free_port())
        result["passed"] = True
    except BaseException as error:  # noqa: BLE001 - top-level regression harness
        result["passed"] = False
        result["error"] = repr(error)
        result["traceback"] = traceback.format_exc()

    if result.get("passed") is True:
        print("PASS remote_transport")
    else:
        print("FAIL remote_transport", file=os.sys.stderr)
        if result.get("traceback"):
            print(result["traceback"], file=os.sys.stderr, end="")
    return result


def main() -> int:
    result = _run()
    print(json.dumps(result, indent=2, default=str))
    return 0 if result.get("passed") is True else 1


if __name__ == "__main__":
    raise SystemExit(main())
