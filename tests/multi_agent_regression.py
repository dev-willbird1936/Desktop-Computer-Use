"""Owned-window two-agent/two-process regression; never controls an unrelated app.

Foreground checks activate only an owned fixture, then restore the original
foreground/cursor when the fixture still owns foreground. DCU_EXE overrides the
binary under test. No network, JEV, driver, or model dependency.
"""
import ctypes
from concurrent.futures import ThreadPoolExecutor
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
from input_event_probe import InputEventProbe


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("owned_window_stress", ROOT / "tests/multi_window_stress_regression.py")
STRESS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(STRESS)
KERNEL = ctypes.WinDLL("kernel32", use_last_error=True)
KERNEL.OpenMutexW.argtypes = [ctypes.c_uint, ctypes.c_bool, ctypes.c_wchar_p]
KERNEL.OpenMutexW.restype = ctypes.c_void_p
KERNEL.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_uint]
KERNEL.ReleaseMutex.argtypes = [ctypes.c_void_p]
KERNEL.CloseHandle.argtypes = [ctypes.c_void_p]


def mutex_is_owned(window_id):
    pid, hwnd = window_id.split(":0x")
    session = ctypes.c_uint()
    if not KERNEL.ProcessIdToSessionId(os.getpid(), ctypes.byref(session)):
        raise ctypes.WinError(ctypes.get_last_error())
    name = f"Local\\DCU.Action.{session.value}.window-{pid}-{int(hwnd, 16):X}"
    handle = KERNEL.OpenMutexW(0x100001, False, name)
    if not handle:
        return False
    try:
        result = KERNEL.WaitForSingleObject(handle, 0)
        if result in (0, 0x80):
            KERNEL.ReleaseMutex(handle)
        return result == 0x102
    finally:
        KERNEL.CloseHandle(handle)


def wait_for_ownership(window_id):
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        if mutex_is_owned(window_id):
            return
        time.sleep(0.02)
    raise AssertionError("Sequence did not reserve its window")


def restore_user_foreground(original, owned, cursor):
    current = int(STRESS.user32.GetForegroundWindow())
    if current not in owned:
        return  # Never undo a user's unrelated app switch.
    thread = KERNEL.GetCurrentThreadId()
    target_thread = STRESS.user32.GetWindowThreadProcessId(current, None)
    attached = bool(target_thread and STRESS.user32.AttachThreadInput(thread, target_thread, True))
    try:
        STRESS.user32.SetForegroundWindow(original)
        STRESS.user32.SetCursorPos(*cursor)
    finally:
        if attached:
            STRESS.user32.AttachThreadInput(thread, target_thread, False)


def main():
    exe = Path(os.environ.get("DCU_EXE", str(STRESS.DEFAULT_DCU))).resolve()
    artifact = Path(tempfile.mkdtemp(prefix="dcu-multi-agent-"))
    appdata = artifact / "appdata/shadow-use"
    appdata.mkdir(parents=True)
    (appdata / "settings.json").write_text(json.dumps({
        "UseRealCursor": False, "GameMode": False, "HumanizedInput": False,
        "FastInput": False, "ShowVirtualCursor": False, "AllowUiaTextFallback": False,
        "PostActionDelayMs": 10, "EnableFocusGuard": True,
    }))
    environment = os.environ.copy()
    environment["APPDATA"] = str(appdata.parent)
    original_foreground, original_cursor = STRESS.foreground_and_cursor()
    fixtures, clients, windows = [], [], []
    probe = InputEventProbe()
    result = {"passed": False}
    try:
        for instance, left in [(81, 80), (82, 560)]:
            fixture = subprocess.Popen([str(STRESS.DEFAULT_FIXTURE), str(instance), str(left), "120", "--interactive"])
            fixtures.append(fixture)
            windows.append(STRESS.wait_for_windows(fixture)[0])
        for index in range(2):
            directory = artifact / f"agent-{index}"
            directory.mkdir()
            client = STRESS.ConcurrentMcpClient(exe, environment, directory)
            clients.append(client)
            client.call("initialize", {"protocolVersion": "2025-03-26", "capabilities": {},
                "clientInfo": {"name": f"agent-{index}", "version": "1"}})
            client.notify("notifications/initialized")
        first, second = clients
        probe.pids = {client.process.pid for client in clients}
        probe.phase = "background"
        selector = f"{fixtures[0].pid}:0x{windows[0]:X}"
        other = f"{fixtures[1].pid}:0x{windows[1]:X}"
        saved = first.tool("get_app_state", {"app": selector, "include_screenshot": False})
        old_id = STRESS.element(saved, "Increment 81")["id"]
        first.tool("get_app_state", {"app": selector, "include_screenshot": False})
        survived = first.tool("click", {"app": selector, "element_id": old_id, "input_mode": "background"})
        if not survived.get("ok"):
            raise AssertionError(f"Another observation invalidated saved ids: {survived}")

        # An id from a different DCU process must not alias that process's own e1.
        second.tool("get_app_state", {"app": selector, "include_screenshot": False})
        foreign = second.tool("click", {"app": selector, "element_id": old_id, "input_mode": "background"})
        if foreign.get("ok"):
            raise AssertionError("Foreign-server element id was accepted")

        steps = json.dumps([{"tool": "wait_for", "args": {"condition": "text_exists", "text": "not-present-in-owned-fixture", "timeout_s": 2}}])
        with ThreadPoolExecutor(max_workers=3) as pool:
            sequence = pool.submit(first.tool, "execute_sequence", {"app": selector, "steps_json": steps, "input_mode": "background"})
            wait_for_ownership(selector)
            same_window = pool.submit(second.tool, "press_key", {"app": selector, "key": "Tab", "input_mode": "background"})
            started = time.monotonic()
            other_result = second.tool("press_key", {"app": other, "key": "Tab", "input_mode": "background"})
            other_seconds = time.monotonic() - started
            if not other_result.get("ok") or other_seconds > 1.5:
                raise AssertionError(f"Unrelated window blocked: {other_result}, {other_seconds}")
            if same_window.done():
                raise AssertionError("Same-window mutation interleaved with reserved sequence")
            sequence.result(timeout=10)
            if not same_window.result(timeout=10).get("ok"):
                raise AssertionError("Waiting second agent did not resume")
        background_state = STRESS.foreground_and_cursor()
        result.update(original_foreground=original_foreground, original_cursor=original_cursor,
            background_foreground=background_state[0], background_cursor=background_state[1], owned_windows=windows)
        if background_state[0] in windows or probe.count("background"):
            raise AssertionError(f"Background agents took fixture foreground or injected real-pointer input: {background_state}")

        # Game tools remain usable with GameMode off, and image observations survive another capture.
        probe.phase = "background-game"
        frame = first.tool("game_screenshot", {"app": selector, "max_width": 420})
        if not frame.get("ok"):
            raise AssertionError(f"Game screenshot unavailable: {frame}")
        first.tool("game_screenshot", {"app": selector, "max_width": 200})
        rect = STRESS.window_rect(windows[0])
        surface = STRESS.element(saved, "Stress surface 81")
        x = int((surface["x"] + surface["w"] / 2) * frame["image_scale"])
        y = int((surface["y"] + surface["h"] / 2) * frame["image_scale"])
        game_background = first.tool("game_click", {"app": selector, "x": x, "y": y,
            "source_frame_id": frame["source_frame_id"], "input_mode": "background", "then_screenshot": True})
        if not game_background.get("ok") or game_background.get("effect") not in ("frame_changed", "minor_change"):
            raise AssertionError(f"Background game click failed: {game_background}")
        if STRESS.foreground_and_cursor()[0] in windows or probe.count("background-game"):
            raise AssertionError("Background game mode took foreground or injected real-pointer input")

        # Explicit humanized foreground input only touches the owned input field.
        probe.phase = "foreground"
        snap = first.tool("get_app_state", {"app": selector, "include_screenshot": False})
        field = STRESS.element(snap, "Input 81")
        selected = first.tool("click", {"app": selector, "element_id": field["id"], "input_mode": "humanized", "pace": "fast"})
        if not selected.get("ok"):
            raise AssertionError(f"Humanized real-cursor click failed: {selected}")
        typed = first.tool("type_text", {"app": selector, "text": "DCU-owned-input", "input_mode": "humanized", "pace": "fast"})
        if not typed.get("ok"):
            raise AssertionError(f"Humanized typing failed: {typed}")
        observed = first.tool("get_app_state", {"app": selector, "include_screenshot": False})
        observed_text = STRESS.element(observed, "Input 81").get("value") or ""
        if observed_text.count("DCU-owned-input") != 1:
            raise AssertionError("The requested real-input text did not reach the owned field exactly once")
        result["foreground_extra_input_observed"] = observed_text != "DCU-owned-input"
        if STRESS.foreground_and_cursor()[0] in windows:
            raise AssertionError("Foreground input left the fixture active")
        if not probe.count("foreground"):
            raise AssertionError("Foreground test did not observe tagged DCU real-cursor events")

        # Persistent toggles work together, while an explicit background call
        # still wins over a foreground default. This is a third independent DCU process.
        profile = artifact / "game-profile"
        settings_dir = profile / "shadow-use"
        settings_dir.mkdir(parents=True)
        (settings_dir / "settings.json").write_text(json.dumps({
            "UseRealCursor": True, "GameMode": True, "HumanizedInput": True, "FastInput": True,
            "ShowVirtualCursor": False, "PostActionDelayMs": 10, "EnableFocusGuard": True,
        }))
        game_environment = environment.copy()
        game_environment["APPDATA"] = str(profile)
        profile_client = STRESS.ConcurrentMcpClient(exe, game_environment, profile)
        clients.append(profile_client)
        probe.pids.add(profile_client.process.pid)
        profile_client.call("initialize", {"protocolVersion": "2025-03-26", "capabilities": {},
            "clientInfo": {"name": "game-profile-agent", "version": "1"}})
        profile_client.notify("notifications/initialized")
        health = profile_client.tool("health_check")
        if not all(health.get(key) for key in ("use_real_cursor", "game_mode", "humanized_input", "fast_input")):
            raise AssertionError(f"Persistent input toggles did not load: {health}")
        probe.phase = "background-game"
        override = profile_client.tool("game_click", {"app": selector, "x": rect.left + surface["x"] + 30,
            "y": rect.top + surface["y"] + 30, "coord_space": "screen", "input_mode": "background"})
        if not override.get("ok") or override.get("input_mode") != "background" or probe.count("background-game"):
            raise AssertionError(f"Foreground settings overrode explicit background: {override}")
        probe.phase = "game-profile"
        retained = profile_client.tool("game_move", {"app": selector, "x": rect.left + field["x"] + 40,
            "y": rect.top + field["y"] + 10, "coord_space": "screen"})
        if not retained.get("ok") or STRESS.foreground_and_cursor()[0] != windows[0]:
            raise AssertionError(f"Game profile failed to keep its requested foreground: {retained}")
        restore_user_foreground(original_foreground, windows, original_cursor)

        ocr = first.tool("game_ocr", {"app": selector})
        result.update(passed=True, other_window_seconds=other_seconds, game_background_effect=game_background["effect"],
            humanized_method=typed["method"], ocr_supported=ocr.get("ok", False), ocr_unsupported=ocr.get("unsupported"),
            background_injected_mouse_events=probe.count("background") + probe.count("background-game"),
            foreground_injected_mouse_events=probe.count("foreground"),
            game_profile_injected_mouse_events=probe.count("game-profile"), game_profile_verified=True)
        print(f"PASS multi-agent regression: {artifact}")
        return 0
    except BaseException as error:
        result["error"] = repr(error)
        print(f"FAIL multi-agent regression: {error}; evidence={artifact}")
        return 1
    finally:
        restore_user_foreground(original_foreground, windows, original_cursor)
        for client in clients:
            try:
                client.close()
            except Exception:
                client.abort()
        for fixture in fixtures:
            if fixture.poll() is None:
                fixture.terminate()
            fixture.wait(timeout=5)
        probe.close()
        (artifact / "result.json").write_text(json.dumps(result, indent=2))


if __name__ == "__main__":
    raise SystemExit(main())
