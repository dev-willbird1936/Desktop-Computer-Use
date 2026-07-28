#!/usr/bin/env python3
"""Isolated multi-window movement and concurrency regression for DCU.

The script launches and owns three harmless WinForms fixture windows. It never
closes, moves, resizes, snapshots, or controls any pre-existing window.
"""

from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
import ctypes
import json
import os
from pathlib import Path
import queue
import re
import subprocess
import tempfile
import threading
import time
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
TFM = "net10.0-windows10.0.22621.0"
DEFAULT_DCU = ROOT / "src" / "ShadowUse" / "bin" / "Release" / TFM / "dcu.exe"
DEFAULT_FIXTURE = ROOT / "tests" / "WindowStressFixture" / "bin" / "Release" / TFM / "WindowStressFixture.exe"
ARTIFACT_ROOT = Path(os.environ.get("TEMP", tempfile.gettempdir())) / "dcu-multi-window-stress"
user32 = ctypes.WinDLL("user32", use_last_error=True)


class Point(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


class Rect(ctypes.Structure):
    _fields_ = [
        ("left", ctypes.c_long),
        ("top", ctypes.c_long),
        ("right", ctypes.c_long),
        ("bottom", ctypes.c_long),
    ]


class ConcurrentMcpClient:
    def __init__(self, exe: Path, environment: dict[str, str], artifact_dir: Path):
        self._stderr = (artifact_dir / "dcu-stderr.log").open("w", encoding="utf-8")
        self._transcript = (artifact_dir / "mcp-transcript.jsonl").open("w", encoding="utf-8")
        self._lock = threading.Lock()
        self._next_id = 1
        self._pending: dict[int, queue.Queue[dict[str, Any] | BaseException]] = {}
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
        self._reader = threading.Thread(target=self._read_loop, daemon=True)
        self._reader.start()

    def _record(self, direction: str, message: dict[str, Any]) -> None:
        with self._lock:
            self._transcript.write(json.dumps({"direction": direction, "message": message}) + "\n")
            self._transcript.flush()

    def _read_loop(self) -> None:
        assert self.process.stdout is not None
        try:
            for line in self.process.stdout:
                response = json.loads(line)
                self._record("response", response)
                request_id = response.get("id")
                with self._lock:
                    destination = self._pending.get(request_id)
                if destination is not None:
                    destination.put(response)
            failure: BaseException = RuntimeError(f"DCU closed stdout (exit={self.process.poll()})")
        except BaseException as error:
            failure = error
        with self._lock:
            pending = list(self._pending.values())
        for destination in pending:
            destination.put(failure)

    def call(self, method: str, params: dict[str, Any] | None = None) -> dict[str, Any]:
        destination: queue.Queue[dict[str, Any] | BaseException] = queue.Queue(maxsize=1)
        with self._lock:
            request_id = self._next_id
            self._next_id += 1
            self._pending[request_id] = destination
            request = {"jsonrpc": "2.0", "id": request_id, "method": method, "params": params or {}}
            self._transcript.write(json.dumps({"direction": "request", "message": request}) + "\n")
            self._transcript.flush()
            assert self.process.stdin is not None
            self.process.stdin.write(json.dumps(request) + "\n")
            self.process.stdin.flush()
        try:
            response = destination.get(timeout=20)
        except queue.Empty as error:
            raise TimeoutError(f"MCP call timed out: {method}") from error
        finally:
            with self._lock:
                self._pending.pop(request_id, None)
        if isinstance(response, BaseException):
            raise response
        return response

    def notify(self, method: str) -> None:
        request = {"jsonrpc": "2.0", "method": method}
        with self._lock:
            self._transcript.write(json.dumps({"direction": "notification", "message": request}) + "\n")
            self._transcript.flush()
            assert self.process.stdin is not None
            self.process.stdin.write(json.dumps(request) + "\n")
            self.process.stdin.flush()

    def tool(self, name: str, arguments: dict[str, Any] | None = None) -> dict[str, Any]:
        response = self.call("tools/call", {"name": name, "arguments": arguments or {}})
        if "error" in response:
            return {"error": response["error"]}
        content = response.get("result", {}).get("content", [])
        if not content:
            return {"error": "empty MCP tool response"}
        return json.loads(content[0]["text"])

    def close(self) -> None:
        if self.process.stdin is not None:
            self.process.stdin.close()
        self.process.wait(timeout=15)
        self._transcript.close()
        self._stderr.close()

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


def window_rect(hwnd: int) -> Rect:
    rect = Rect()
    if not user32.GetWindowRect(hwnd, ctypes.byref(rect)):
        raise ctypes.WinError(ctypes.get_last_error())
    return rect


def move_window(hwnd: int, left: int, top: int, width: int, height: int) -> None:
    swp_no_activate = 0x0010
    swp_show_window = 0x0040
    if not user32.SetWindowPos(hwnd, 0, left, top, width, height, swp_no_activate | swp_show_window):
        raise ctypes.WinError(ctypes.get_last_error())


def wait_for_windows(process: subprocess.Popen[str], count: int = 1) -> list[int]:
    deadline = time.monotonic() + 10
    callback_type = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    while time.monotonic() < deadline:
        matches: list[int] = []

        @callback_type
        def callback(hwnd: int, _parameter: int) -> bool:
            pid = ctypes.c_ulong()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            if pid.value == process.pid and user32.IsWindowVisible(hwnd):
                matches.append(int(hwnd))
            return True

        user32.EnumWindows(callback, 0)
        if len(matches) >= count:
            return matches[:count]
        if process.poll() is not None:
            raise RuntimeError(f"fixture exited early: pid={process.pid} exit={process.returncode}")
        time.sleep(0.1)
    raise TimeoutError(f"{count} fixture windows did not appear: pid={process.pid}")


def visible_windows_for_pid(pid_value: int) -> list[int]:
    matches: list[int] = []
    callback_type = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    @callback_type
    def callback(hwnd: int, _parameter: int) -> bool:
        pid = ctypes.c_ulong()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value == pid_value and user32.IsWindowVisible(hwnd):
            matches.append(int(hwnd))
        return True

    user32.EnumWindows(callback, 0)
    return matches


def element(snapshot: dict[str, Any], name: str) -> dict[str, Any]:
    return next(item for item in snapshot["elements"] if item.get("name") == name)


def status(snapshot: dict[str, Any], instance: int) -> str:
    return str(element(snapshot, f"Status {instance}").get("value") or "")


def surface_event(value: str) -> tuple[int, int, int]:
    match = re.fullmatch(r"event (\d+): right (-?\d+),(-?\d+)", value)
    if not match:
        raise AssertionError(f"unexpected surface status: {value!r}")
    return tuple(int(group) for group in match.groups())


def snapshot(client: ConcurrentMcpClient, pid: int) -> dict[str, Any]:
    return snapshot_selector(client, str(pid))


def snapshot_selector(client: ConcurrentMcpClient, selector: str) -> dict[str, Any]:
    result = client.tool("get_app_state", {"app": selector, "include_screenshot": False, "max_elements": 50})
    if "error" in result:
        raise AssertionError(result["error"])
    return result


def main() -> int:
    if not DEFAULT_DCU.is_file():
        raise FileNotFoundError(f"DCU executable not found: {DEFAULT_DCU}")
    if not DEFAULT_FIXTURE.is_file():
        raise FileNotFoundError(f"fixture executable not found: {DEFAULT_FIXTURE}")

    run_id = f"multi-window-{int(time.time())}"
    artifact_dir = ARTIFACT_ROOT / run_id
    artifact_dir.mkdir(parents=True, exist_ok=False)
    appdata = artifact_dir / "appdata"
    settings_dir = appdata / "shadow-use"
    settings_dir.mkdir(parents=True)
    (settings_dir / "settings.json").write_text(
        json.dumps(
            {
                "AllowUiaTextFallback": True,
                "EnableBoundsGuard": False,
                "EnableDesktopCheck": False,
                "ShowVirtualCursor": True,
                "EnableFocusGuard": True,
                "PostActionDelayMs": 10,
            }
        ),
        encoding="utf-8",
    )
    environment = os.environ.copy()
    environment["APPDATA"] = str(appdata)

    original_foreground, original_cursor = foreground_and_cursor()
    fixtures: list[subprocess.Popen[str]] = []
    multi_fixture: subprocess.Popen[str] | None = None
    client: ConcurrentMcpClient | None = None
    result: dict[str, Any] = {"run_id": run_id, "artifact_dir": str(artifact_dir)}
    try:
        for instance, left in enumerate((80, 540, 1000), start=1):
            fixtures.append(
                subprocess.Popen(
                    [str(DEFAULT_FIXTURE), str(instance), str(left), "120"],
                    cwd=ROOT,
                    text=True,
                )
            )
        windows = [wait_for_windows(process)[0] for process in fixtures]
        multi_fixture = subprocess.Popen(
            [str(DEFAULT_FIXTURE), "4", "220", "300", "2"],
            cwd=ROOT,
            text=True,
        )
        multi_windows = wait_for_windows(multi_fixture, 2)
        client = ConcurrentMcpClient(DEFAULT_DCU, environment, artifact_dir)
        client.call(
            "initialize",
            {
                "protocolVersion": "2025-03-26",
                "capabilities": {},
                "clientInfo": {"name": "dcu-multi-window-stress", "version": "1.0"},
            },
        )
        client.notify("notifications/initialized")

        listed = client.tool("list_apps")
        same_process_windows = [
            app for app in listed.get("apps", []) if app.get("pid") == multi_fixture.pid
        ]
        if len(same_process_windows) != 2:
            raise AssertionError(
                f"same-process windows were not listed independently: {same_process_windows}"
            )
        if any(not app.get("window_id") for app in same_process_windows):
            raise AssertionError(f"same-process windows lack window_id: {same_process_windows}")
        same_process_windows.sort(key=lambda app: app["title"])
        with ThreadPoolExecutor(max_workers=2) as pool:
            same_process_snapshots = list(
                pool.map(
                    lambda app: snapshot_selector(client, app["window_id"]),
                    same_process_windows,
                )
            )
        for index, hwnd in enumerate(multi_windows):
            move_window(hwnd, 180 + index * 650, 250 + index * 80, 500, 360)
        with ThreadPoolExecutor(max_workers=2) as pool:
            same_process_results = list(
                pool.map(
                    lambda pair: client.tool(
                        "click",
                        {
                            "app": pair[0]["window_id"],
                            "element_id": element(
                                pair[1],
                                f"Increment {41 + pair[2]}",
                            )["id"],
                        },
                    ),
                    [
                        (same_process_windows[index], same_process_snapshots[index], index)
                        for index in range(2)
                    ],
                )
            )
        if any(not response.get("ok") for response in same_process_results):
            raise AssertionError(f"same-process window actions failed: {same_process_results}")

        with ThreadPoolExecutor(max_workers=3) as pool:
            initial = list(pool.map(lambda process: snapshot(client, process.pid), fixtures))

        moved = [(900, 520, 500, 360), (80, 520, 520, 380), (620, 70, 480, 400)]
        for hwnd, bounds in zip(windows, moved, strict=True):
            move_window(hwnd, *bounds)

        with ThreadPoolExecutor(max_workers=3) as pool:
            clicks = list(
                pool.map(
                    lambda pair: client.tool(
                        "click",
                        {
                            "app": str(pair[1].pid),
                            "element_id": element(pair[0], f"Stress surface {pair[2]}")["id"],
                            "button": "right",
                        },
                    ),
                    [(initial[index], fixtures[index], index + 1) for index in range(3)],
                )
            )
        with ThreadPoolExecutor(max_workers=3) as pool:
            after_element_clicks = list(pool.map(lambda process: snapshot(client, process.pid), fixtures))

        element_events = [surface_event(status(after_element_clicks[index], index + 1)) for index in range(3)]
        for index, (_, x, y) in enumerate(element_events):
            surface = element(after_element_clicks[index], f"Stress surface {index + 1}")
            if not (0 <= x < surface["w"] and 0 <= y < surface["h"]):
                raise AssertionError(f"stale element geometry on fixture {index + 1}: local=({x},{y})")

        raw_source = after_element_clicks[0]
        raw_surface = element(raw_source, "Stress surface 1")
        raw_x = raw_source["bounds"]["left"] + raw_surface["x"] + raw_surface["w"] // 2
        raw_y = raw_source["bounds"]["top"] + raw_surface["y"] + raw_surface["h"] // 2
        move_window(windows[0], 350, 180, 500, 360)
        raw_response = client.tool(
            "click",
            {"app": str(fixtures[0].pid), "x": raw_x, "y": raw_y, "button": "right"},
        )
        after_raw = snapshot(client, fixtures[0].pid)
        event_number, raw_local_x, raw_local_y = surface_event(status(after_raw, 1))
        if event_number != element_events[0][0] + 1:
            raise AssertionError(f"stale raw coordinate did not reach moved fixture: response={raw_response}")
        if not (0 <= raw_local_x < raw_surface["w"] and 0 <= raw_local_y < raw_surface["h"]):
            raise AssertionError(f"rebased raw coordinate was outside target: ({raw_local_x},{raw_local_y})")

        with ThreadPoolExecutor(max_workers=3) as pool:
            fresh = list(pool.map(lambda process: snapshot(client, process.pid), fixtures))
            semantic_results = list(
                pool.map(
                    lambda pair: client.tool(
                        "click",
                        {
                            "app": str(pair[1].pid),
                            "element_id": element(pair[0], f"Increment {pair[2]}")["id"],
                        },
                    ),
                    [(fresh[index], fixtures[index], index + 1) for index in range(3)],
                )
            )
        if any(not response.get("ok") for response in semantic_results):
            raise AssertionError(f"concurrent semantic actions failed: {semantic_results}")

        burst_results: list[dict[str, Any]] = []
        for cycle in range(6):
            cycle_moves = [
                (120 + cycle * 35, 100 + cycle * 22, 430 + cycle * 5, 330 + cycle * 4),
                (620 - cycle * 30, 420 - cycle * 18, 460 + cycle * 6, 350 + cycle * 3),
                (1040 - cycle * 24, 120 + cycle * 28, 440 + cycle * 4, 340 + cycle * 5),
            ]
            for hwnd, bounds in zip(windows, cycle_moves, strict=True):
                move_window(hwnd, *bounds)
            with ThreadPoolExecutor(max_workers=3) as pool:
                cycle_snapshots = list(pool.map(lambda process: snapshot(client, process.pid), fixtures))
                cycle_results = list(
                    pool.map(
                        lambda pair: client.tool(
                            "click",
                            {
                                "app": str(pair[1].pid),
                                "element_id": element(pair[0], f"Increment {pair[2]}")["id"],
                            },
                        ),
                        [(cycle_snapshots[index], fixtures[index], index + 1) for index in range(3)],
                    )
                )
            if any(not response.get("ok") for response in cycle_results):
                raise AssertionError(f"movement burst cycle {cycle} failed: {cycle_results}")
            for snapshot_result, expected in zip(cycle_snapshots, cycle_moves, strict=True):
                bounds = snapshot_result["bounds"]
                actual = (bounds["left"], bounds["top"], bounds["right"] - bounds["left"], bounds["bottom"] - bounds["top"])
                if actual != expected:
                    raise AssertionError(f"snapshot did not track moved window: expected={expected} actual={actual}")
            burst_results.extend(cycle_results)

        final_foreground, final_cursor = foreground_and_cursor()
        if final_foreground != original_foreground:
            raise AssertionError(f"foreground changed: {original_foreground} -> {final_foreground}")

        overlay_before_idle = visible_windows_for_pid(client.process.pid)
        if not overlay_before_idle:
            raise AssertionError("virtual cursor overlay was not visible after the action burst")
        idle_deadline = time.monotonic() + 65
        overlay_after_idle = overlay_before_idle
        while time.monotonic() < idle_deadline:
            overlay_after_idle = visible_windows_for_pid(client.process.pid)
            if not overlay_after_idle:
                break
            time.sleep(0.5)
        if overlay_after_idle:
            raise AssertionError("virtual cursor overlay remained visible after one minute of inactivity")

        result.update(
            {
                "fixture_pids": [process.pid for process in fixtures],
                "same_process_fixture_pid": multi_fixture.pid,
                "same_process_windows": same_process_windows,
                "same_process_results": same_process_results,
                "clicks": clicks,
                "element_events": element_events,
                "raw_response": raw_response,
                "semantic_results": semantic_results,
                "burst_action_count": len(burst_results),
                "overlay_before_idle": overlay_before_idle,
                "overlay_after_idle": overlay_after_idle,
                "foreground": [original_foreground, final_foreground],
                "cursor": [original_cursor, final_cursor],
            }
        )
        (artifact_dir / "result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        client.close()
        client = None
        print(f"PASS run={run_id} artifact_dir={artifact_dir}")
        return 0
    except BaseException as error:
        result["error"] = repr(error)
        (artifact_dir / "result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        print(f"FAIL run={run_id} error={error} artifact_dir={artifact_dir}", file=os.sys.stderr)
        return 1
    finally:
        if client is not None:
            client.abort()
        for process in fixtures:
            if process.poll() is None:
                process.terminate()
        if multi_fixture is not None and multi_fixture.poll() is None:
            multi_fixture.terminate()
        for process in fixtures:
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
        if multi_fixture is not None:
            try:
                multi_fixture.wait(timeout=5)
            except subprocess.TimeoutExpired:
                multi_fixture.kill()
                multi_fixture.wait(timeout=5)


if __name__ == "__main__":
    raise SystemExit(main())
