#!/usr/bin/env python3
"""Isolated Chrome regression for DCU address-bar navigation.

The test owns a Chrome-for-Testing profile, a two-page loopback oracle, and one
DCU stdio process. It verifies the browser effect instead of trusting ok=true.
No existing Chrome window or profile is inspected or controlled.
"""

from __future__ import annotations

import argparse
import ctypes
import http.server
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
from typing import Any
from urllib.parse import parse_qs, urlparse


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_DCU = ROOT / "src" / "ShadowUse" / "bin" / "Debug" / "net10.0-windows10.0.22621.0" / "dcu.exe"
DEFAULT_CHROME = Path(os.environ.get("LOCALAPPDATA", "")) / "Google" / "Chrome" / "Application" / "chrome.exe"
ARTIFACT_ROOT = Path(os.environ.get("TEMP", tempfile.gettempdir())) / "dcu-chrome-navigation-regression"

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)


class Point(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


class Oracle(http.server.ThreadingHTTPServer):
    allow_reuse_address = False

    def __init__(self, address: tuple[str, int]):
        super().__init__(address, OracleHandler)
        self.requests: list[dict[str, Any]] = []
        self.lock = threading.Lock()


class OracleHandler(http.server.BaseHTTPRequestHandler):
    server: Oracle

    def do_GET(self) -> None:  # noqa: N802 - stdlib handler API
        parsed = urlparse(self.path)
        query = parse_qs(parsed.query)
        record = {"path": parsed.path, "query": query, "time": time.time()}
        with self.server.lock:
            self.server.requests.append(record)

        title = "DCU navigation target" if parsed.path == "/target" else "DCU baseline tab"
        body = (
            "<!doctype html><html><head><meta charset='utf-8'>"
            f"<title>{title}</title></head><body><h1>{title}</h1>"
            f"<p id='path'>{parsed.path}</p></body></html>"
        ).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, _format: str, *_args: object) -> None:
        return


class McpClient:
    def __init__(self, exe: Path, environment: dict[str, str], artifact_dir: Path):
        self._stderr_path = artifact_dir / "dcu-stderr.log"
        self._transcript_path = artifact_dir / "mcp-transcript.jsonl"
        self._stderr = self._stderr_path.open("w", encoding="utf-8")
        self._transcript = self._transcript_path.open("w", encoding="utf-8")
        self.process = subprocess.Popen(
            [str(exe)],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=self._stderr,
            text=True,
            encoding="utf-8",
            bufsize=1,
            cwd=ROOT,
            env=environment,
        )
        self._next_id = 1

    def _write_transcript(self, direction: str, message: dict[str, Any]) -> None:
        self._transcript.write(json.dumps({"direction": direction, "message": message}) + "\n")
        self._transcript.flush()

    def call(self, method: str, params: dict[str, Any] | None = None) -> dict[str, Any]:
        request_id = self._next_id
        self._next_id += 1
        message = {"jsonrpc": "2.0", "id": request_id, "method": method, "params": params or {}}
        assert self.process.stdin is not None
        assert self.process.stdout is not None
        self._write_transcript("request", message)
        self.process.stdin.write(json.dumps(message) + "\n")
        self.process.stdin.flush()
        while True:
            line = self.process.stdout.readline()
            if not line:
                raise RuntimeError(f"DCU closed stdout (exit={self.process.poll()})")
            response = json.loads(line)
            self._write_transcript("response", response)
            if response.get("id") == request_id:
                return response

    def notify(self, method: str) -> None:
        message = {"jsonrpc": "2.0", "method": method}
        assert self.process.stdin is not None
        self._write_transcript("notification", message)
        self.process.stdin.write(json.dumps(message) + "\n")
        self.process.stdin.flush()

    def tool(self, name: str, args: dict[str, Any] | None = None) -> dict[str, Any]:
        response = self.call("tools/call", {"name": name, "arguments": args or {}})
        if "error" in response:
            return {"error": response["error"]}
        content = response.get("result", {}).get("content", [])
        if not content:
            return {"error": "empty MCP tool response"}
        return json.loads(content[0]["text"])

    def close_via_eof(self) -> int:
        if self.process.stdin is not None and not self.process.stdin.closed:
            self.process.stdin.close()
        code = self.process.wait(timeout=15)
        self._transcript.close()
        self._stderr.close()
        return code

    def abort(self) -> None:
        if self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=5)
        self._transcript.close()
        self._stderr.close()


def foreground_and_cursor() -> tuple[int, tuple[int, int]]:
    point = Point()
    if not user32.GetCursorPos(ctypes.byref(point)):
        raise ctypes.WinError(ctypes.get_last_error())
    return int(user32.GetForegroundWindow()), (point.x, point.y)


def restore_foreground(hwnd: int) -> bool:
    """Restore the pre-fixture foreground window without sending input."""
    if not hwnd or not user32.IsWindow(hwnd):
        return False
    current_thread = kernel32.GetCurrentThreadId()
    foreground_thread = user32.GetWindowThreadProcessId(hwnd, None)
    attached = bool(
        foreground_thread
        and foreground_thread != current_thread
        and user32.AttachThreadInput(current_thread, foreground_thread, True)
    )
    try:
        user32.SetForegroundWindow(hwnd)
    finally:
        if attached:
            user32.AttachThreadInput(current_thread, foreground_thread, False)
    return int(user32.GetForegroundWindow()) == hwnd


def find_window_for_pid(pid: int) -> int:
    matches: list[int] = []
    callback_type = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    @callback_type
    def callback(hwnd: int, _lparam: int) -> bool:
        window_pid = ctypes.c_ulong()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(window_pid))
        if window_pid.value == pid and user32.IsWindowVisible(hwnd):
            matches.append(int(hwnd))
        return True

    user32.EnumWindows(callback, 0)
    return matches[0] if matches else 0


def wait_until(predicate, timeout: float, description: str):
    deadline = time.monotonic() + timeout
    last = None
    while time.monotonic() < deadline:
        last = predicate()
        if last:
            return last
        time.sleep(0.2)
    raise AssertionError(f"timed out waiting for {description}; last={last!r}")


def choose_port(requested: int) -> int:
    probe = socket.socket()
    try:
        probe.bind(("127.0.0.1", requested))
    finally:
        probe.close()
    return requested


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dcu", type=Path, default=Path(os.environ.get("DCU_EXE", DEFAULT_DCU)))
    parser.add_argument("--chrome", type=Path, default=Path(os.environ.get("CHROME_EXE", DEFAULT_CHROME)))
    parser.add_argument("--port", type=int, default=18765)
    args = parser.parse_args()

    if not args.dcu.is_file():
        raise FileNotFoundError(f"DCU executable not found: {args.dcu}")
    if not args.chrome.is_file():
        raise FileNotFoundError(f"isolated Chrome-for-Testing executable not found: {args.chrome}")

    choose_port(args.port)
    run_id = f"chrome-dcu-regression-{int(time.time())}"
    baseline_url = f"http://127.0.0.1:{args.port}/baseline?run={run_id}&scenario=preserved"
    target_url = f"http://127.0.0.1:{args.port}/target?run={run_id}&scenario=baseline"
    artifact_dir = ARTIFACT_ROOT / run_id
    artifact_dir.mkdir(parents=True, exist_ok=False)
    profile_dir = artifact_dir / "chrome-profile"
    appdata_dir = artifact_dir / "appdata"
    settings_dir = appdata_dir / "shadow-use"
    settings_dir.mkdir(parents=True)
    (settings_dir / "settings.json").write_text(
        json.dumps(
            {
                "AllowUiaTextFallback": True,
                "EnableBoundsGuard": False,
                "EnableDesktopCheck": False,
                "ShowVirtualCursor": False,
                "EnableFocusGuard": True,
                "PostActionDelayMs": 150,
            }
        ),
        encoding="utf-8",
    )

    oracle = Oracle(("127.0.0.1", args.port))
    server_thread = threading.Thread(target=oracle.serve_forever, daemon=True)
    server_thread.start()
    chrome: subprocess.Popen[str] | None = None
    mcp: McpClient | None = None
    eof_code: int | None = None
    outcome: dict[str, Any] = {
        "run_id": run_id,
        "baseline_url": baseline_url,
        "target_url": target_url,
        "artifact_dir": str(artifact_dir),
    }
    original_foreground, original_cursor = foreground_and_cursor()

    try:
        chrome = subprocess.Popen(
            [
                str(args.chrome),
                f"--user-data-dir={profile_dir}",
                "--new-window",
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-extensions",
                "--disable-sync",
                "--disable-background-networking",
                "--disable-component-update",
                "--window-position=80,80",
                "--window-size=1000,760",
                baseline_url,
            ],
            stdout=subprocess.DEVNULL,
            stderr=(artifact_dir / "chrome-stderr.log").open("w", encoding="utf-8"),
            text=True,
        )
        chrome_hwnd = wait_until(lambda: find_window_for_pid(chrome.pid), 20, "owned Chrome window")
        wait_until(
            lambda: any(r["path"] == "/baseline" for r in oracle.requests),
            20,
            "baseline loopback request",
        )

        environment = os.environ.copy()
        environment["APPDATA"] = str(appdata_dir)
        mcp = McpClient(args.dcu, environment, artifact_dir)
        mcp.call(
            "initialize",
            {
                "protocolVersion": "2025-03-26",
                "capabilities": {},
                "clientInfo": {"name": "chrome-navigation-regression", "version": "1.0"},
            },
        )
        mcp.notify("notifications/initialized")

        app = str(chrome.pid)
        before = mcp.tool("get_app_state", {"app": app, "include_screenshot": False, "max_elements": 600})
        if "error" in before:
            raise AssertionError(f"initial snapshot failed: {before['error']}")
        new_tab = next(
            (
                element
                for element in before.get("elements", [])
                if element.get("type") == "Button"
                and (element.get("name") or "").strip().lower() in {"new tab", "new tab button"}
            ),
            None,
        )
        if new_tab is None:
            raise AssertionError("Chrome New Tab button was not exposed by UIA")
        clicked = mcp.tool("click", {"app": app, "element_id": new_tab["id"]})
        if clicked.get("ok") is not True:
            raise AssertionError(f"New Tab click failed: {clicked}")

        def snapshot_with_two_tabs() -> dict[str, Any] | None:
            snapshot = mcp.tool(
                "get_app_state", {"app": app, "include_screenshot": False, "max_elements": 600}
            )
            tabs = [
                element
                for element in snapshot.get("elements", [])
                if element.get("type") == "TabItem"
            ]
            return snapshot if len(tabs) == 2 else None

        two_tabs = wait_until(snapshot_with_two_tabs, 10, "exactly two Chrome tabs")
        tabs_before_navigation = [
            element.get("name")
            for element in two_tabs.get("elements", [])
            if element.get("type") == "TabItem"
        ]
        if not any("DCU baseline tab" in (name or "") for name in tabs_before_navigation):
            raise AssertionError(f"original baseline tab was not preserved: {tabs_before_navigation}")

        if not restore_foreground(original_foreground):
            raise AssertionError("could not restore the pre-fixture foreground window")
        time.sleep(0.2)

        foreground_before, cursor_before = foreground_and_cursor()
        if foreground_before == chrome_hwnd:
            raise AssertionError("owned Chrome was foreground; regression requires background control")
        steps = json.dumps(
            [
                {"tool": "press_key", "args": {"key": "ctrl+l"}},
                {"tool": "type_text", "args": {"text": target_url}},
                {"tool": "press_key", "args": {"key": "Return"}},
            ]
        )
        sequence = mcp.tool("execute_sequence", {"app": app, "steps_json": steps})
        effect = None
        try:
            effect = wait_until(
                lambda: next(
                    (
                        record
                        for record in oracle.requests
                        if record["path"] == "/target" and record["query"].get("run") == [run_id]
                    ),
                    None,
                ),
                10,
                "target loopback request",
            )
        finally:
            foreground_after, cursor_after = foreground_and_cursor()

        after = mcp.tool("get_app_state", {"app": app, "include_screenshot": False, "max_elements": 600})
        tabs_after_navigation = [
            element.get("name")
            for element in after.get("elements", [])
            if element.get("type") == "TabItem"
        ]
        documents_after = [
            {"name": element.get("name"), "value": element.get("value")}
            for element in after.get("elements", [])
            if element.get("type") == "Document"
        ]
        outcome.update(
            {
                "chrome_pid": chrome.pid,
                "chrome_hwnd": chrome_hwnd,
                "dcu_pid": mcp.process.pid,
                "sequence": sequence,
                "effect": effect,
                "tabs_before_navigation": tabs_before_navigation,
                "tabs_after_navigation": tabs_after_navigation,
                "documents_after": documents_after,
                "foreground_before": foreground_before,
                "foreground_after": foreground_after,
                "cursor_before": cursor_before,
                "cursor_after": cursor_after,
                "original_foreground": original_foreground,
                "original_cursor": original_cursor,
                "requests": oracle.requests,
            }
        )
        if sequence.get("ok") is not True:
            raise AssertionError(f"execute_sequence failed: {sequence}")
        if len(tabs_after_navigation) != 2:
            raise AssertionError(f"expected two tabs after navigation, got {tabs_after_navigation}")
        if not any("DCU baseline tab" in (name or "") for name in tabs_after_navigation):
            raise AssertionError(f"original tab was not preserved after navigation: {tabs_after_navigation}")
        if not any("DCU navigation target" in (name or "") for name in tabs_after_navigation):
            raise AssertionError(f"target tab title did not appear: {tabs_after_navigation}")
        if foreground_after != foreground_before:
            raise AssertionError(f"foreground changed: {foreground_before} -> {foreground_after}")
        if cursor_after != cursor_before:
            raise AssertionError(f"cursor moved: {cursor_before} -> {cursor_after}")

        eof_code = mcp.close_via_eof()
        mcp = None
        outcome["dcu_eof_exit_code"] = eof_code
        if eof_code != 0:
            raise AssertionError(f"DCU EOF shutdown exit code was {eof_code}, expected 0")
        print(f"PASS run={run_id} artifact_dir={artifact_dir}")
        return 0
    except Exception as exc:
        outcome["failure"] = f"{type(exc).__name__}: {exc}"
        print(f"FAIL run={run_id}: {exc}", file=sys.stderr)
        print(f"artifacts={artifact_dir}", file=sys.stderr)
        return 1
    finally:
        if mcp is not None:
            try:
                eof_code = mcp.close_via_eof()
                outcome["dcu_eof_exit_code"] = eof_code
            except Exception as close_exc:
                outcome["dcu_close_failure"] = f"{type(close_exc).__name__}: {close_exc}"
                mcp.abort()
        if chrome is not None and chrome.poll() is None:
            # This PID is the browser root created with our verified disposable profile.
            chrome.terminate()
            try:
                chrome.wait(timeout=10)
            except subprocess.TimeoutExpired:
                chrome.kill()
                chrome.wait(timeout=5)
        restore_foreground(original_foreground)
        oracle.shutdown()
        oracle.server_close()
        outcome["requests"] = oracle.requests
        (artifact_dir / "result.json").write_text(json.dumps(outcome, indent=2), encoding="utf-8")
        if profile_dir.exists():
            shutil.rmtree(profile_dir, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
