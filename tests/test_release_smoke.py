import importlib.util
import io
import json
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location(
    "release_smoke", Path(__file__).resolve().parents[1] / "scripts/release_smoke.py"
)
SMOKE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SMOKE)
CORE = [
    "list_apps", "get_app_state", "click", "type_text", "press_key", "scroll",
    "drag", "set_value", "wait_for", "execute_sequence", "hide_cursor", "health_check",
    "game_screenshot", "game_click", "game_move", "game_drag", "game_scroll",
    "game_key", "game_ocr", "game_bars", "game_wait",
]


class ReleaseSmokeTests(unittest.TestCase):
    def responses(self):
        return [
            {"id": 1, "result": {"serverInfo": {"name": "dcu", "version": "0.1.2"}}},
            {"id": 2, "result": {"tools": [{"name": name} for name in CORE]}},
            {"id": 3, "result": {"content": [{"type": "text", "text": json.dumps({
                "ok": True, "interactive_desktop": True,
            })}]}},
        ]

    def run_smoke(self, responses, exit_code=0):
        process = SimpleNamespace(
            stdin=io.StringIO(), stdout=io.StringIO("".join(json.dumps(r) + "\n" for r in responses)),
            returncode=exit_code, wait=lambda timeout: exit_code,
        )
        with patch.object(SMOKE.subprocess, "Popen", return_value=process), patch("sys.stdout", new=io.StringIO()):
            SMOKE.smoke("dcu.exe", "0.1.2")
        self.assertTrue(process.stdin.closed)
        self.assertTrue(process.stdout.closed)

    def test_accepts_core_server_and_notifications(self):
        responses = self.responses()
        responses.insert(1, {"jsonrpc": "2.0", "method": "notifications/tools/list_changed"})
        self.run_smoke(responses)

    def test_rejects_wrong_version(self):
        responses = self.responses()
        responses[0]["result"]["serverInfo"]["version"] = "0.1.1"
        with self.assertRaisesRegex(RuntimeError, "Unexpected server identity"):
            self.run_smoke(responses)

    def test_rejects_experimental_tools(self):
        responses = self.responses()
        responses[1]["result"]["tools"].append({"name": "jev_act"})
        with self.assertRaisesRegex(RuntimeError, "Unexpected tool catalogue"):
            self.run_smoke(responses)

    def test_rejects_rpc_error(self):
        with self.assertRaisesRegex(RuntimeError, "MCP initialize failed"):
            self.run_smoke([{"id": 1, "error": {"code": -32603, "message": "failed"}}])

    def test_rejects_noninteractive_desktop(self):
        responses = self.responses()
        responses[2]["result"]["content"][0]["text"] = '{"ok": false, "interactive_desktop": false}'
        with self.assertRaisesRegex(RuntimeError, "Desktop health check failed"):
            self.run_smoke(responses)

    def test_rejects_early_exit(self):
        with self.assertRaisesRegex(RuntimeError, "DCU closed stdout"):
            self.run_smoke([])

    def test_rejects_failed_shutdown(self):
        with self.assertRaisesRegex(RuntimeError, "DCU exited with code 1"):
            self.run_smoke(self.responses(), exit_code=1)


if __name__ == "__main__":
    unittest.main()
