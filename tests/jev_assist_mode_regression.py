#!/usr/bin/env python3
"""Live proof that jev=assist is a real, distinct mode: jev_act returns a recommendation
from a REAL round trip through a real Jev protocol endpoint (a local loopback server, since
no live TYPESAFE_API_KEY exists in this environment -- same constraint as bench/jev_mode_
benchmark.py's fast_loop proof), but NEVER executes anything itself. This is the live
counterpart to BrowserJevToolTests.cs's unit-test proof of the same contract, closing the
one remaining mode (of jev=off/assist/fast_loop) that previously had no live, real-process,
real-fixture evidence.

Usage: python tests/jev_assist_mode_regression.py
"""
from __future__ import annotations

import json
import os
import sys
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from uuid import uuid4

sys.path.insert(0, str(Path(__file__).resolve().parent))

from multi_window_stress_regression import (  # noqa: E402
    ConcurrentMcpClient,
    element,
    snapshot_selector,
    wait_for_windows,
)

ROOT = Path(__file__).resolve().parents[1]
TFM = "net10.0-windows10.0.22621.0"
DEFAULT_DCU = ROOT / "src" / "ShadowUse" / "bin" / "Debug" / TFM / "dcu.exe"
DEFAULT_FIXTURE = ROOT / "tests" / "WindowStressFixture" / "bin" / "Debug" / TFM / "WindowStressFixture.exe"
SYNTHETIC_USERNAME = "fixture-user"


class FakeJevHandler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_POST(self):  # noqa: N802
        length = int(self.headers.get("Content-Length", "0"))
        body = json.loads(self.rfile.read(length))
        answers = {}
        for key, question in body.get("questions", {}).items():
            qtype = question.get("type")
            if qtype == "choice":
                criteria = list(question["criteria"].keys())
                chosen = "set_value" if key == "operation" and "set_value" in criteria else criteria[0]
                probs = {c: (0.9 if c == chosen else 0.1 / max(1, len(criteria) - 1)) for c in criteria}
                answers[key] = {"choice": chosen, "confidence": 0.92, "probabilities": probs}
            elif qtype == "noul":
                answers[key] = {"noul": 0.8}
        payload = json.dumps({"answers": answers, "model": "fake-jev-loopback-v1"}).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)


def _settings_environment(artifact_dir: Path, jev_base_url: str) -> dict[str, str]:
    settings_dir = artifact_dir / "appdata" / "shadow-use"
    settings_dir.mkdir(parents=True)
    (settings_dir / "settings.json").write_text(
        json.dumps({
            "EnableBoundsGuard": False, "EnableDesktopCheck": False, "ShowVirtualCursor": False,
            "JevMode": "assist", "JevBaseUrl": jev_base_url, "JevTimeoutMs": 5000,
        }),
        encoding="utf-8",
    )
    env = dict(os.environ, APPDATA=str(artifact_dir / "appdata"))
    env["TYPESAFE_API_KEY"] = "loopback-fake-key-not-a-real-credential"
    return env


def main() -> int:
    server = HTTPServer(("127.0.0.1", 0), FakeJevHandler)
    port = server.server_address[1]
    threading.Thread(target=server.serve_forever, daemon=True).start()

    artifact_dir = Path(tempfile.gettempdir()) / "dcu-jev-assist-mode" / f"{time.time_ns()}-{uuid4().hex[:8]}"
    artifact_dir.mkdir(parents=True)
    import subprocess
    fixture = subprocess.Popen([str(DEFAULT_FIXTURE), "--scenario", "login"], cwd=ROOT, text=True)
    client: ConcurrentMcpClient | None = None
    try:
        wait_for_windows(fixture, 1)
        environment = _settings_environment(artifact_dir, f"http://127.0.0.1:{port}")
        client = ConcurrentMcpClient(DEFAULT_DCU, environment, artifact_dir)
        initialized = client.call(
            "initialize",
            {"protocolVersion": "2025-03-26", "capabilities": {}, "clientInfo": {"name": "jev-assist-mode", "version": "1.0"}},
        )
        if "error" in initialized:
            raise AssertionError(f"MCP initialize failed: {initialized}")
        client.notify("notifications/initialized")

        apps = client.tool("list_apps")
        owned = [a for a in apps.get("apps", []) if a.get("pid") == fixture.pid]
        if len(owned) != 1:
            raise AssertionError(f"expected one owned fixture window, got {owned}")
        window_id = owned[0]["window_id"]

        state = snapshot_selector(client, window_id)
        username = element(state, "Fixture username")
        value_before = username.get("value")

        result = client.tool("jev_act", {"app": window_id, "goal": "set the username field", "fill_value": "should-never-be-set-in-assist-mode"})
        if result.get("decision_source") != "jev_assist_recommendation":
            raise AssertionError(f"jev=assist should return a recommendation, not execute: {result}")
        if "error" in result:
            raise AssertionError(f"assist-mode recommendation should not be an error against a real loopback round trip: {result}")
        if not isinstance(result.get("latency_ms"), int) or result["latency_ms"] < 0:
            raise AssertionError(f"assist-mode recommendation should carry a real measured latency: {result}")

        state_after = snapshot_selector(client, window_id)
        username_after = element(state_after, "Fixture username")
        if username_after.get("value") != value_before:
            raise AssertionError(
                f"jev=assist must NEVER execute anything -- the field changed from "
                f"{value_before!r} to {username_after.get('value')!r}: {result}"
            )

        # F49: jev_inspect is a read-only, non-activating history over the exact decision that
        # was just made -- assert the most recent entry matches the assist recommendation above,
        # against a real native process (the browser adapter already had this proof; native did not).
        inspected = client.tool("jev_inspect", {"count": 5})
        entries = inspected.get("entries") or []
        if not entries:
            raise AssertionError(f"jev_inspect should have at least one entry after a real jev_act call: {inspected}")
        latest = entries[0]
        if latest.get("surface") != "native" or latest.get("decisionSource") != "jev_assist_recommendation" or latest.get("executed") is not False:
            raise AssertionError(f"jev_inspect's latest entry did not match the real assist decision just made: {latest}")
        if latest.get("operation") != result.get("operation") or latest.get("latencyMs") != result.get("latency_ms"):
            raise AssertionError(f"jev_inspect's entry should mirror the exact executed decision's fields: {latest} vs {result}")

        inspected_again = client.tool("jev_inspect", {"count": 5})
        if inspected_again.get("entries") != entries:
            raise AssertionError(f"jev_inspect must be read-only -- calling it twice changed the history: {entries} -> {inspected_again.get('entries')}")

        print(json.dumps({"passed": True, "decision_source": result["decision_source"], "operation": result.get("operation"),
                           "latency_ms": result["latency_ms"], "field_unchanged": True,
                           "jev_inspect_matched": True, "jev_inspect_read_only": True}, indent=2))
        return 0
    finally:
        server.shutdown()
        if client is not None:
            client.close()
        fixture.terminate()
        try:
            fixture.wait(timeout=5)
        except Exception:
            fixture.kill()


if __name__ == "__main__":
    sys.exit(main())
