#!/usr/bin/env python3
"""Isolated Chrome regression for native extension context-menu submenus.

The test owns a disposable Chrome-for-Testing profile, an unpacked synthetic
extension, a loopback effect oracle, and one DCU process. It verifies native
popup HWND routing, submenu appearance, the extension callback, foreground,
the real cursor, and clean DCU EOF shutdown.
"""

from __future__ import annotations

import argparse
import ctypes
import http.server
import importlib.util
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
COMMON_PATH = ROOT / "tests" / "chrome_transient_dialog_regression.py"
COMMON_SPEC = importlib.util.spec_from_file_location("chrome_dialog_common", COMMON_PATH)
if COMMON_SPEC is None or COMMON_SPEC.loader is None:
    raise RuntimeError(f"could not load test helpers: {COMMON_PATH}")
common = importlib.util.module_from_spec(COMMON_SPEC)
COMMON_SPEC.loader.exec_module(common)

DEFAULT_DCU = ROOT / "src" / "ShadowUse" / "bin" / "Debug" / "net10.0-windows10.0.22621.0" / "shadow-use.exe"
DEFAULT_CHROME = Path(os.environ.get("LOCALAPPDATA", "")) / "Google" / "Chrome" / "Application" / "chrome.exe"
ARTIFACT_ROOT = Path(os.environ.get("TEMP", tempfile.gettempdir())) / "dcu-chrome-context-menu-regression"

user32 = ctypes.WinDLL("user32", use_last_error=True)
user32.WindowFromPoint.argtypes = [common.Point]
user32.WindowFromPoint.restype = ctypes.c_void_p
user32.RealChildWindowFromPoint.argtypes = [ctypes.c_void_p, common.Point]
user32.RealChildWindowFromPoint.restype = ctypes.c_void_p
user32.ScreenToClient.argtypes = [ctypes.c_void_p, ctypes.POINTER(common.Point)]
user32.ScreenToClient.restype = ctypes.c_bool
user32.GetParent.argtypes = [ctypes.c_void_p]
user32.GetParent.restype = ctypes.c_void_p


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
        record = {"path": parsed.path, "query": parse_qs(parsed.query), "time": time.time()}
        with self.server.lock:
            self.server.requests.append(record)
        body = b""
        if parsed.path == "/fixture":
            body = b"""<!doctype html>
<html><head><meta charset='utf-8'><title>DCU context menu fixture</title></head>
<body><label>Target <input id='target' aria-label='Context target' value='synthetic'></label></body></html>"""
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, _format: str, *_args: object) -> None:
        return


def choose_port(port: int) -> None:
    probe = socket.socket()
    try:
        probe.bind(("127.0.0.1", port))
    finally:
        probe.close()


def window_info(hwnd: int) -> dict[str, Any]:
    if not hwnd:
        return {"hwnd": 0, "class": "", "title": "", "pid": 0, "parent": 0}
    title = ctypes.create_unicode_buffer(512)
    class_name = ctypes.create_unicode_buffer(256)
    pid = ctypes.c_ulong()
    user32.GetWindowTextW(hwnd, title, len(title))
    user32.GetClassNameW(hwnd, class_name, len(class_name))
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    return {
        "hwnd": int(hwnd),
        "class": class_name.value,
        "title": title.value,
        "pid": int(pid.value),
        "parent": int(user32.GetParent(hwnd) or 0),
    }


def local_child_target(root_hwnd: int, screen_x: int, screen_y: int) -> int:
    current = root_hwnd
    for _ in range(32):
        point = common.Point(screen_x, screen_y)
        if not user32.ScreenToClient(current, ctypes.byref(point)):
            break
        child = int(user32.RealChildWindowFromPoint(current, point) or 0)
        if not child or child == current or not user32.IsWindow(child):
            break
        current = child
    return current


def write_extension(extension_dir: Path, port: int) -> None:
    extension_dir.mkdir(parents=True)
    (extension_dir / "manifest.json").write_text(
        json.dumps(
            {
                "manifest_version": 3,
                "name": "DCU Synthetic Context Menu",
                "version": "1.0",
                "permissions": ["contextMenus"],
                "host_permissions": ["http://127.0.0.1/*"],
                "background": {"service_worker": "worker.js"},
            },
            indent=2,
        ),
        encoding="utf-8",
    )
    (extension_dir / "worker.js").write_text(
        f"""chrome.runtime.onInstalled.addListener(() => {{
  chrome.contextMenus.create({{id: 'parent', title: 'DCU Parent Menu', contexts: ['editable']}});
  chrome.contextMenus.create({{id: 'child', parentId: 'parent', title: 'DCU Child Action', contexts: ['editable']}});
}});
chrome.contextMenus.onClicked.addListener((info) => {{
  if (info.menuItemId === 'child') {{
    fetch('http://127.0.0.1:{port}/effect?clicked=child&nonce=' + Date.now(), {{keepalive: true}});
  }}
}});""",
        encoding="utf-8",
    )


UIA_DIAGNOSTIC = r"""param([long]$RootHwnd, [string]$TargetName)
Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$RootHwnd)
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$target = $null
foreach ($candidate in $all) {
  try { if ($candidate.Current.Name -eq $TargetName) { $target = $candidate; break } } catch {}
}
if ($null -eq $target) { throw "UIA target not found: $TargetName" }
$patterns = @()
foreach ($pattern in $target.GetSupportedPatterns()) { $patterns += $pattern.ProgrammaticName }
$ancestors = @()
$cursor = $target
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
for ($depth = 0; $depth -lt 16 -and $null -ne $cursor; $depth++) {
  try {
    $ancestors += [pscustomobject]@{
      depth = $depth
      name = $cursor.Current.Name
      control_type = $cursor.Current.ControlType.ProgrammaticName
      class_name = $cursor.Current.ClassName
      native_hwnd = $cursor.Current.NativeWindowHandle
    }
  } catch {}
  try { $cursor = $walker.GetParent($cursor) } catch { break }
}
$rect = $target.Current.BoundingRectangle
[pscustomobject]@{
  name = $target.Current.Name
  automation_id = $target.Current.AutomationId
  class_name = $target.Current.ClassName
  control_type = $target.Current.ControlType.ProgrammaticName
  native_hwnd = $target.Current.NativeWindowHandle
  bounds = [pscustomobject]@{left=$rect.Left;top=$rect.Top;width=$rect.Width;height=$rect.Height}
  patterns = $patterns
  ancestors = $ancestors
} | ConvertTo-Json -Depth 8 -Compress
"""


def uia_diagnostic(artifact_dir: Path, root_hwnd: int, name: str) -> dict[str, Any]:
    script = artifact_dir / "uia-diagnostic.ps1"
    script.write_text(UIA_DIAGNOSTIC, encoding="utf-8-sig")
    completed = subprocess.run(
        [
            "powershell",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(script),
            "-RootHwnd",
            str(root_hwnd),
            "-TargetName",
            name,
        ],
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=15,
    )
    if completed.returncode != 0:
        raise AssertionError(f"UIA diagnostic failed: {completed.stderr.strip()}")
    return json.loads(completed.stdout)


def find_named(snapshot: dict[str, Any], name: str) -> dict[str, Any] | None:
    return next((element for element in snapshot.get("elements", []) if element.get("name") == name), None)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dcu", type=Path, default=Path(os.environ.get("DCU_EXE", DEFAULT_DCU)))
    parser.add_argument("--chrome", type=Path, default=Path(os.environ.get("CHROME_EXE", DEFAULT_CHROME)))
    parser.add_argument("--port", type=int, default=18780)
    args = parser.parse_args()
    if not args.dcu.is_file():
        raise FileNotFoundError(f"DCU executable not found: {args.dcu}")
    if not args.chrome.is_file():
        raise FileNotFoundError(f"Chrome executable not found: {args.chrome}")
    choose_port(args.port)

    run_id = f"chrome-dcu-menu-{int(time.time() * 1000)}"
    artifact_dir = ARTIFACT_ROOT / run_id
    artifact_dir.mkdir(parents=True, exist_ok=False)
    profile_dir = artifact_dir / "chrome-profile"
    extension_dir = artifact_dir / "extension"
    appdata_dir = artifact_dir / "appdata"
    settings_dir = appdata_dir / "shadow-use"
    settings_dir.mkdir(parents=True)
    write_extension(extension_dir, args.port)
    (settings_dir / "settings.json").write_text(
        json.dumps(
            {
                "EnableBoundsGuard": False,
                "EnableDesktopCheck": False,
                "ShowVirtualCursor": False,
                "EnableFocusGuard": True,
                "PostActionDelayMs": 150,
            }
        ),
        encoding="utf-8",
    )

    fixture_url = f"http://127.0.0.1:{args.port}/fixture?run={run_id}"
    oracle = Oracle(("127.0.0.1", args.port))
    threading.Thread(target=oracle.serve_forever, daemon=True).start()
    chrome: subprocess.Popen[str] | None = None
    mcp: common.McpClient | None = None
    original_foreground, original_cursor = common.foreground_and_cursor()
    outcome: dict[str, Any] = {"run_id": run_id, "artifact_dir": str(artifact_dir)}

    try:
        chrome = subprocess.Popen(
            [
                str(args.chrome),
                f"--user-data-dir={profile_dir}",
                f"--disable-extensions-except={extension_dir}",
                f"--load-extension={extension_dir}",
                "--new-window",
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-sync",
                "--disable-component-update",
                "--window-position=80,80",
                "--window-size=1000,760",
                fixture_url,
            ],
            stdout=subprocess.DEVNULL,
            stderr=(artifact_dir / "chrome-stderr.log").open("w", encoding="utf-8"),
            text=True,
        )
        windows_initial = common.wait_until(lambda: common.windows_for_pid(chrome.pid), 20, "owned Chrome window")
        common.wait_until(
            lambda: any(record["path"] == "/fixture" for record in oracle.requests),
            20,
            "fixture load",
        )
        time.sleep(2)

        environment = os.environ.copy()
        environment["APPDATA"] = str(appdata_dir)
        mcp = common.McpClient(args.dcu, environment, artifact_dir)
        mcp.call(
            "initialize",
            {
                "protocolVersion": "2025-03-26",
                "capabilities": {},
                "clientInfo": {"name": "chrome-context-menu-regression", "version": "1.0"},
            },
        )
        mcp.notify("notifications/initialized")
        app = str(chrome.pid)
        initial = mcp.tool("get_app_state", {"app": app, "include_screenshot": False, "max_elements": 800})
        target = find_named(initial, "Context target")
        if target is None:
            raise AssertionError(f"editable context target not exposed: {initial}")
        main_window = common.wait_until(
            lambda: next(
                (
                    window
                    for window in common.windows_for_pid(chrome.pid)
                    if "DCU context menu fixture" in window["title"]
                ),
                None,
            ),
            10,
            "titled Chrome fixture window",
        )
        main_hwnd = main_window["hwnd"]

        opened = mcp.tool("click", {"app": app, "element_id": target["id"], "button": "right"})
        if opened.get("ok") is not True:
            raise AssertionError(f"right-click did not expose native context menu: {opened}")

        def snapshot_with_parent() -> dict[str, Any] | None:
            snapshot = mcp.tool("get_app_state", {"app": app, "include_screenshot": False, "max_elements": 800})
            parent = find_named(snapshot, "DCU Parent Menu")
            return {"snapshot": snapshot, "parent": parent} if parent is not None else None

        menu = common.wait_until(snapshot_with_parent, 10, "fresh extension parent context-menu item")
        parent = menu["parent"]
        bounds = menu["snapshot"]["bounds"]
        screen_x = int(bounds["left"] + parent["x"] + parent["w"] // 2)
        screen_y = int(bounds["top"] + parent["y"] + parent["h"] // 2)
        point_hwnd = int(user32.WindowFromPoint(common.Point(screen_x, screen_y)) or 0)
        local_target_hwnd = local_child_target(main_hwnd, screen_x, screen_y)
        point_window = window_info(point_hwnd)
        local_target_window = window_info(local_target_hwnd)
        windows_menu_open = common.windows_for_pid(chrome.pid)
        uia_parent = uia_diagnostic(artifact_dir, main_hwnd, "DCU Parent Menu")

        if point_hwnd == main_hwnd or point_window["class"] != "Chrome_WidgetWin_1":
            raise AssertionError(f"menu point was not a distinct Chrome popup root: {point_window}")

        if not common.restore_foreground(original_foreground):
            raise AssertionError("could not restore pre-fixture foreground window")
        time.sleep(0.2)
        foreground_before, cursor_before = common.foreground_and_cursor()

        parent_clicked = mcp.tool("click", {"app": app, "element_id": parent["id"]})
        foreground_after_parent, cursor_after_parent = common.foreground_and_cursor()

        def snapshot_with_child() -> dict[str, Any] | None:
            snapshot = mcp.tool("get_app_state", {"app": app, "include_screenshot": False, "max_elements": 800})
            child = find_named(snapshot, "DCU Child Action")
            return {"snapshot": snapshot, "child": child} if child is not None else None

        submenu = None
        try:
            submenu = common.wait_until(snapshot_with_child, 3, "extension submenu item")
        except AssertionError:
            submenu = None

        child_clicked: dict[str, Any] | None = None
        effect: dict[str, Any] | None = None
        foreground_before_child: int | None = None
        cursor_before_child: tuple[int, int] | None = None
        foreground_after_child: int | None = None
        cursor_after_child: tuple[int, int] | None = None
        if submenu is not None:
            foreground_before_child, cursor_before_child = common.foreground_and_cursor()
            child_clicked = mcp.tool("click", {"app": app, "element_id": submenu["child"]["id"]})
            foreground_after_child, cursor_after_child = common.foreground_and_cursor()
            effect = common.wait_until(
                lambda: next(
                    (
                        record
                        for record in oracle.requests
                        if record["path"] == "/effect" and record["query"].get("clicked") == ["child"]
                    ),
                    None,
                ),
                10,
                "extension child callback",
            )
        outcome.update(
            {
                "chrome_pid": chrome.pid,
                "main_hwnd": main_hwnd,
                "windows_initial": windows_initial,
                "windows_menu_open": windows_menu_open,
                "menu_snapshot_revision": menu["snapshot"].get("revision"),
                "parent_element": parent,
                "parent_screen_center": [screen_x, screen_y],
                "uia_parent": uia_parent,
                "window_from_point": point_window,
                "main_root_local_fallback_target": local_target_window,
                "open_response": opened,
                "parent_click_response": parent_clicked,
                "submenu_snapshot": submenu["snapshot"] if submenu else None,
                "child_click_response": child_clicked,
                "effect": effect,
                "foreground_before": foreground_before,
                "foreground_after_parent": foreground_after_parent,
                "cursor_before": cursor_before,
                "cursor_after_parent": cursor_after_parent,
                "foreground_before_child": foreground_before_child,
                "foreground_after_child": foreground_after_child,
                "cursor_before_child": cursor_before_child,
                "cursor_after_child": cursor_after_child,
            }
        )
        if (
            parent_clicked.get("ok") is not True
            or parent_clicked.get("method") != "uia_expand"
            or parent_clicked.get("effect") != "submenu_expanded"
            or submenu is None
        ):
            raise AssertionError(
                f"fresh parent click did not expose submenu: response={parent_clicked}, "
                f"point_hwnd={point_window}, local_fallback={local_target_window}, uia={uia_parent}"
            )
        if child_clicked is None or child_clicked.get("ok") is not True:
            raise AssertionError(f"submenu child click failed: {child_clicked}")
        if foreground_after_parent != foreground_before:
            raise AssertionError(f"parent click changed foreground: {foreground_before} -> {foreground_after_parent}")
        if cursor_after_parent != cursor_before:
            raise AssertionError(f"parent click moved cursor: {cursor_before} -> {cursor_after_parent}")
        if foreground_after_child != foreground_before_child:
            raise AssertionError(f"child click changed foreground: {foreground_before_child} -> {foreground_after_child}")
        if cursor_after_child != cursor_before_child:
            raise AssertionError(f"child click moved cursor: {cursor_before_child} -> {cursor_after_child}")

        eof_code = mcp.close_via_eof()
        mcp = None
        outcome["dcu_eof_exit_code"] = eof_code
        if eof_code != 0:
            raise AssertionError(f"DCU EOF exit was {eof_code}")
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
        common.restore_foreground(original_foreground)
        oracle.shutdown()
        oracle.server_close()
        outcome["requests"] = oracle.requests
        (artifact_dir / "result.json").write_text(json.dumps(outcome, indent=2), encoding="utf-8")
        if profile_dir.exists():
            shutil.rmtree(profile_dir, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
