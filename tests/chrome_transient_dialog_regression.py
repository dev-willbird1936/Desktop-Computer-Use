#!/usr/bin/env python3
"""Regression for successful clicks on transient Chromium confirmation roots.

The test owns a disposable Chrome profile, a loopback effect oracle, and one DCU
stdio process. It never reads an existing Chrome profile or extension. A single
DCU click must close the JavaScript confirmation, trigger the accepted effect,
and return ok=true without moving the real cursor or changing foreground.
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
DEFAULT_DCU = ROOT / "src" / "ShadowUse" / "bin" / "Debug" / "net10.0-windows10.0.22621.0" / "shadow-use.exe"
DEFAULT_CHROME = Path(os.environ.get("LOCALAPPDATA", "")) / "Google" / "Chrome" / "Application" / "chrome.exe"
ARTIFACT_ROOT = Path(os.environ.get("TEMP", tempfile.gettempdir())) / "dcu-chrome-transient-dialog-regression"

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

        body = b""
        if parsed.path == "/fixture":
            body = b"""<!doctype html>
<html><head><meta charset='utf-8'><title>DCU transient confirmation fixture</title></head>
<body>
  <button id='open-confirm' onclick='openConfirmation()'>Open confirmation popup</button>
  <button id='no-effect' onclick='void 0'>No effect</button>
  <script>
    function openConfirmation() {
      window.open('/popup', 'dcu-confirm', 'popup,width=520,height=280');
    }
  </script>
</body></html>"""
        elif parsed.path == "/popup":
            body = b"""<!doctype html>
<html><head><meta charset='utf-8'><title>DCU transient confirmation</title></head>
<body>
  <p>Confirm the synthetic action.</p>
  <button id='ok' onclick='accept()'>OK</button>
  <button id='cancel' onclick='window.close()'>Cancel</button>
  <script>
    function accept() {
      fetch('/effect?accepted=true&nonce=' + Date.now(), {keepalive: true})
        .finally(() => window.close());
    }
  </script>
</body></html>"""
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, _format: str, *_args: object) -> None:
        return


class McpClient:
    def __init__(self, exe: Path, environment: dict[str, str], artifact_dir: Path):
        self._stderr = (artifact_dir / "dcu-stderr.log").open("w", encoding="utf-8")
        self._transcript = (artifact_dir / "mcp-transcript.jsonl").open("w", encoding="utf-8")
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

    def _record(self, direction: str, message: dict[str, Any]) -> None:
        self._transcript.write(json.dumps({"direction": direction, "message": message}) + "\n")
        self._transcript.flush()

    def call(self, method: str, params: dict[str, Any] | None = None) -> dict[str, Any]:
        request_id = self._next_id
        self._next_id += 1
        message = {"jsonrpc": "2.0", "id": request_id, "method": method, "params": params or {}}
        assert self.process.stdin is not None
        assert self.process.stdout is not None
        self._record("request", message)
        self.process.stdin.write(json.dumps(message) + "\n")
        self.process.stdin.flush()
        while True:
            line = self.process.stdout.readline()
            if not line:
                raise RuntimeError(f"DCU closed stdout (exit={self.process.poll()})")
            response = json.loads(line)
            self._record("response", response)
            if response.get("id") == request_id:
                return response

    def notify(self, method: str) -> None:
        message = {"jsonrpc": "2.0", "method": method}
        assert self.process.stdin is not None
        self._record("notification", message)
        self.process.stdin.write(json.dumps(message) + "\n")
        self.process.stdin.flush()

    def tool(self, name: str, args: dict[str, Any] | None = None) -> dict[str, Any]:
        response = self.call("tools/call", {"name": name, "arguments": args or {}})
        if "error" in response:
            return {"error": response["error"]}
        result = response.get("result", {})
        content = result.get("content", [])
        if not content:
            return {"error": "empty MCP tool response"}
        if result.get("isError") is True:
            return {"ok": False, "is_error": True, "error": content[0].get("text", "MCP tool error")}
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


def windows_for_pid(pid: int) -> list[dict[str, Any]]:
    matches: list[dict[str, Any]] = []
    callback_type = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    @callback_type
    def callback(hwnd: int, _lparam: int) -> bool:
        window_pid = ctypes.c_ulong()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(window_pid))
        if window_pid.value == pid and user32.IsWindowVisible(hwnd):
            title = ctypes.create_unicode_buffer(512)
            class_name = ctypes.create_unicode_buffer(256)
            user32.GetWindowTextW(hwnd, title, len(title))
            user32.GetClassNameW(hwnd, class_name, len(class_name))
            matches.append({"hwnd": int(hwnd), "title": title.value, "class": class_name.value})
        return True

    user32.EnumWindows(callback, 0)
    return matches


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
    parser.add_argument("--port", type=int, default=18766)
    args = parser.parse_args()

    if not args.dcu.is_file():
        raise FileNotFoundError(f"DCU executable not found: {args.dcu}")
    if not args.chrome.is_file():
        raise FileNotFoundError(f"Chrome executable not found: {args.chrome}")
    choose_port(args.port)

    run_id = f"chrome-dcu-dialog-{int(time.time() * 1000)}"
    fixture_url = f"http://127.0.0.1:{args.port}/fixture?run={run_id}"
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
    threading.Thread(target=oracle.serve_forever, daemon=True).start()
    chrome: subprocess.Popen[str] | None = None
    mcp: McpClient | None = None
    outcome: dict[str, Any] = {
        "run_id": run_id,
        "fixture_url": fixture_url,
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
                fixture_url,
            ],
            stdout=subprocess.DEVNULL,
            stderr=(artifact_dir / "chrome-stderr.log").open("w", encoding="utf-8"),
            text=True,
        )
        wait_until(lambda: windows_for_pid(chrome.pid), 20, "owned Chrome window")
        wait_until(
            lambda: any(record["path"] == "/fixture" for record in oracle.requests),
            20,
            "fixture load",
        )

        environment = os.environ.copy()
        environment["APPDATA"] = str(appdata_dir)
        mcp = McpClient(args.dcu, environment, artifact_dir)
        mcp.call(
            "initialize",
            {
                "protocolVersion": "2025-03-26",
                "capabilities": {},
                "clientInfo": {"name": "chrome-transient-dialog-regression", "version": "1.0"},
            },
        )
        mcp.notify("notifications/initialized")

        app = str(chrome.pid)
        initial = mcp.tool("get_app_state", {"app": app, "include_screenshot": False, "max_elements": 600})
        windows_before_popup = windows_for_pid(chrome.pid)
        opener = next(
            (
                element
                for element in initial.get("elements", [])
                if element.get("type") == "Button" and element.get("name") == "Open confirmation popup"
            ),
            None,
        )
        if opener is None:
            raise AssertionError(f"fixture button was not exposed by UIA: {initial}")
        opened = mcp.tool("click", {"app": app, "element_id": opener["id"]})
        if opened.get("ok") is not True:
            raise AssertionError(f"opening confirmation failed: {opened}")

        def snapshot_with_ok() -> dict[str, Any] | None:
            snapshot = mcp.tool("get_app_state", {"app": app, "include_screenshot": False, "max_elements": 600})
            ok = next(
                (
                    element
                    for element in snapshot.get("elements", [])
                    if element.get("type") == "Button" and (element.get("name") or "").strip() == "OK"
                ),
                None,
            )
            return {"snapshot": snapshot, "ok": ok} if ok is not None else None

        dialog = wait_until(snapshot_with_ok, 10, "fresh Chromium OK element")
        windows_while_dialog_open = windows_for_pid(chrome.pid)
        original_hwnds = {window["hwnd"] for window in windows_before_popup}
        transient_hwnds = {
            window["hwnd"] for window in windows_while_dialog_open if window["hwnd"] not in original_hwnds
        }
        if not transient_hwnds:
            raise AssertionError(
                f"popup did not create a distinct Chromium root: before={windows_before_popup}, "
                f"open={windows_while_dialog_open}"
            )
        if not restore_foreground(original_foreground):
            raise AssertionError("could not restore the pre-fixture foreground window")
        time.sleep(0.2)
        foreground_before, cursor_before = foreground_and_cursor()

        # Exactly one click is intentional. A retry after a false-negative response can
        # duplicate a security-sensitive action that already took effect.
        clicked = mcp.tool("click", {"app": app, "element_id": dialog["ok"]["id"]})
        effect = wait_until(
            lambda: next(
                (
                    record
                    for record in oracle.requests
                    if record["path"] == "/effect" and record["query"].get("accepted") == ["true"]
                ),
                None,
            ),
            10,
            "accepted confirmation effect",
        )
        foreground_after, cursor_after = foreground_and_cursor()
        after = mcp.tool("get_app_state", {"app": app, "include_screenshot": False, "max_elements": 600})
        dialog_still_present = any(
            element.get("type") == "Button" and (element.get("name") or "").strip() == "OK"
            for element in after.get("elements", [])
        )
        no_effect = next(
            (
                element
                for element in after.get("elements", [])
                if element.get("type") == "Button" and element.get("name") == "No effect"
            ),
            None,
        )
        if no_effect is None:
            raise AssertionError(f"no-effect control was not exposed after popup closed: {after}")
        no_effect_clicked = mcp.tool("click", {"app": app, "element_id": no_effect["id"]})

        outcome.update(
            {
                "chrome_pid": chrome.pid,
                "dcu_pid": mcp.process.pid,
                "initial_revision": initial.get("revision"),
                "dialog_snapshot": dialog["snapshot"],
                "windows_before_popup": windows_before_popup,
                "windows_while_dialog_open": windows_while_dialog_open,
                "transient_hwnds": sorted(transient_hwnds),
                "click_response": clicked,
                "effect": effect,
                "after": after,
                "no_effect_click_response": no_effect_clicked,
                "foreground_before": foreground_before,
                "foreground_after": foreground_after,
                "cursor_before": cursor_before,
                "cursor_after": cursor_after,
                "original_foreground": original_foreground,
                "original_cursor": original_cursor,
            }
        )
        if clicked.get("ok") is not True:
            raise AssertionError(f"confirmation took effect but click returned a false negative: {clicked}")
        if no_effect_clicked.get("ok") is not False:
            raise AssertionError(f"no-effect Invoke was reported as successful: {no_effect_clicked}")
        if dialog_still_present:
            raise AssertionError("confirmation OK button remained after the accepted effect")
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
                outcome["dcu_eof_exit_code"] = mcp.close_via_eof()
            except Exception as close_exc:
                outcome["dcu_close_failure"] = f"{type(close_exc).__name__}: {close_exc}"
                mcp.abort()
        if chrome is not None and chrome.poll() is None:
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
