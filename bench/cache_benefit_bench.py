#!/usr/bin/env python3
"""Real wall-clock latency benchmark: does F31's remember/resolve_cached_action recipe
cache actually make repeated element resolution faster than the plain cold path (a fresh
get_app_state snapshot scan + set_value by element_id) on the SAME real target?

This is not a pass/fail regression -- it prints and records honest measured numbers,
whichever way they land. Runs against the repository's own owned WindowStressFixture,
same pattern as tests/native_evolution_regression.py, reusing its MCP transport helpers.

Usage: python bench/cache_benefit_bench.py [--iterations N]
"""
from __future__ import annotations

import argparse
import json
import os
import statistics
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from uuid import uuid4

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))

from multi_window_stress_regression import (  # noqa: E402
    ConcurrentMcpClient,
    element,
    snapshot_selector,
    wait_for_windows,
)

TFM = "net10.0-windows10.0.22621.0"
DEFAULT_DCU = ROOT / "src" / "ShadowUse" / "bin" / "Debug" / TFM / "dcu.exe"
DEFAULT_FIXTURE = ROOT / "tests" / "WindowStressFixture" / "bin" / "Debug" / TFM / "WindowStressFixture.exe"
SYNTHETIC_USERNAME = "fixture-user"


def _settings_environment(artifact_dir: Path) -> dict[str, str]:
    settings_dir = artifact_dir / "appdata" / "shadow-use"
    settings_dir.mkdir(parents=True)
    (settings_dir / "settings.json").write_text(
        json.dumps({"EnableBoundsGuard": False, "EnableDesktopCheck": False, "ShowVirtualCursor": False}),
        encoding="utf-8",
    )
    return dict(os.environ, APPDATA=str(artifact_dir / "appdata"))


def _cold_path_latency_ms(client: ConcurrentMcpClient, window_id: str, value: str) -> float:
    """The path an agent with no cache takes every single time: full snapshot, scan for
    the element by name, then set_value by the id that scan just found."""
    started = time.perf_counter()
    state = snapshot_selector(client, window_id)
    target = element(state, "Fixture username")
    result = client.tool("set_value", {"app": window_id, "element_id": target["id"], "value": value})
    elapsed = (time.perf_counter() - started) * 1000
    if result.get("ok") is not True:
        raise AssertionError(f"cold-path set_value failed: {result}")
    return elapsed


def _warm_path_latency_ms(client: ConcurrentMcpClient, window_id: str, task_intent: str, value: str) -> float:
    """The path an agent that already called remember_action_resolution once takes on
    every later occurrence of the same intent: one resolve_cached_action re-resolution,
    then set_value. resolve_cached_action still does a live re-resolution (F31's own
    honesty constraint), so this measures real resolution cost, not a replayed no-op."""
    started = time.perf_counter()
    resolved = client.tool("resolve_cached_action", {"app": window_id, "task_intent": task_intent})
    if not resolved.get("hit"):
        raise AssertionError(f"warm-path resolve_cached_action missed unexpectedly: {resolved}")
    result = client.tool("set_value", {"app": window_id, "element_id": resolved["element_id"], "value": value})
    elapsed = (time.perf_counter() - started) * 1000
    if result.get("ok") is not True:
        raise AssertionError(f"warm-path set_value failed: {result}")
    return elapsed


def run(iterations: int) -> dict:
    artifact_dir = Path(tempfile.gettempdir()) / "dcu-cache-benefit-bench" / f"{time.time_ns()}-{uuid4().hex[:8]}"
    artifact_dir.mkdir(parents=True)
    fixture = subprocess.Popen([str(DEFAULT_FIXTURE), "--scenario", "login"], cwd=ROOT, text=True)
    client: ConcurrentMcpClient | None = None
    try:
        wait_for_windows(fixture, 1)
        environment = _settings_environment(artifact_dir)
        client = ConcurrentMcpClient(DEFAULT_DCU, environment, artifact_dir)
        initialized = client.call(
            "initialize",
            {"protocolVersion": "2025-03-26", "capabilities": {}, "clientInfo": {"name": "cache-bench", "version": "1.0"}},
        )
        if "error" in initialized:
            raise AssertionError(f"MCP initialize failed: {initialized}")
        client.notify("notifications/initialized")

        apps = client.tool("list_apps")
        owned = [a for a in apps.get("apps", []) if a.get("pid") == fixture.pid]
        if len(owned) != 1:
            raise AssertionError(f"expected one owned fixture window, got {owned}")
        window_id = owned[0]["window_id"]

        task_intent = f"cache-bench-{uuid4().hex[:8]}"
        state = snapshot_selector(client, window_id)
        target = element(state, "Fixture username")
        remembered = client.tool("remember_action_resolution", {"app": window_id, "element_id": target["id"], "task_intent": task_intent})
        if remembered.get("ok") is not True:
            raise AssertionError(f"remember_action_resolution failed: {remembered}")

        cold_ms = [_cold_path_latency_ms(client, window_id, f"cold-{i}-{uuid4().hex[:6]}") for i in range(iterations)]
        warm_ms = [_warm_path_latency_ms(client, window_id, task_intent, f"warm-{i}-{uuid4().hex[:6]}") for i in range(iterations)]
        client.tool("set_value", {"app": window_id, "element_id": target["id"], "value": SYNTHETIC_USERNAME})

        report = {
            "iterations": iterations,
            "cold_path_ms": {"mean": statistics.mean(cold_ms), "min": min(cold_ms), "max": max(cold_ms), "samples": cold_ms},
            "warm_path_ms": {"mean": statistics.mean(warm_ms), "min": min(warm_ms), "max": max(warm_ms), "samples": warm_ms},
        }
        report["warm_faster_than_cold"] = report["warm_path_ms"]["mean"] < report["cold_path_ms"]["mean"]
        report["delta_ms"] = report["cold_path_ms"]["mean"] - report["warm_path_ms"]["mean"]
        return report
    finally:
        if client is not None:
            client.close()
        fixture.terminate()
        try:
            fixture.wait(timeout=5)
        except subprocess.TimeoutExpired:
            fixture.kill()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--iterations", type=int, default=15)
    args = parser.parse_args()
    report = run(args.iterations)
    print(json.dumps(report, indent=2))
    print()
    print(
        f"cache benefit: warm mean {report['warm_path_ms']['mean']:.1f}ms vs "
        f"cold mean {report['cold_path_ms']['mean']:.1f}ms "
        f"({'warm faster' if report['warm_faster_than_cold'] else 'warm NOT faster'} by {abs(report['delta_ms']):.1f}ms)"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
