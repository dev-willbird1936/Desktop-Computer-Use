"""Read-only MCP stdio smoke test for a packaged DCU executable."""
import argparse
import json
from pathlib import Path
import queue
import subprocess
import threading
import time


def smoke(executable, version):
    executable = str(Path(executable).resolve())
    messages = queue.Queue()
    process = subprocess.Popen(
        [executable], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1,
    )

    def read_stdout():
        try:
            for line in process.stdout:
                messages.put(line)
        finally:
            messages.put(None)

    reader = threading.Thread(target=read_stdout, daemon=True)
    reader.start()

    def send(message):
        process.stdin.write(json.dumps(message) + "\n")
        process.stdin.flush()

    def call(request_id, method, params=None):
        send({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params or {}})
        deadline = time.monotonic() + 20
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError(f"MCP {method} timed out")
            try:
                line = messages.get(timeout=remaining)
            except queue.Empty as error:
                raise TimeoutError(f"MCP {method} timed out") from error
            if line is None:
                raise RuntimeError(f"DCU closed stdout during {method}")
            response = json.loads(line)  # Also rejects non-protocol stdout logging.
            if response.get("id") != request_id:
                continue
            if "error" in response:
                raise RuntimeError(f"MCP {method} failed: {response['error']}")
            return response["result"]

    try:
        initialized = call(1, "initialize", {
            "protocolVersion": "2025-03-26", "capabilities": {},
            "clientInfo": {"name": "dcu-release-smoke", "version": "1.0"},
        })
        info = initialized["serverInfo"]
        if info["name"] != "dcu" or info["version"] != version:
            raise RuntimeError(f"Unexpected server identity: {info}")
        send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        tools = call(2, "tools/list")["tools"]
        names = {tool["name"] for tool in tools}
        expected = {
            "list_apps", "get_app_state", "click", "type_text", "press_key",
            "scroll", "drag", "set_value", "wait_for", "execute_sequence",
            "hide_cursor", "health_check",
            "game_screenshot", "game_click", "game_move", "game_drag", "game_scroll",
            "game_key", "game_ocr", "game_bars", "game_wait",
        }
        if names != expected:
            raise RuntimeError(f"Unexpected tool catalogue: {sorted(names)}")
        result = call(3, "tools/call", {"name": "health_check", "arguments": {}})
        if result.get("isError"):
            raise RuntimeError("health_check returned an MCP error")
        contents = [item["text"] for item in result.get("content", []) if item.get("type") == "text"]
        if not contents:
            raise RuntimeError("health_check returned no text result")
        health = json.loads(contents[0])
        if health.get("ok") is not True or health.get("interactive_desktop") is not True:
            raise RuntimeError(f"Desktop health check failed: {health}")
        print(f"MCP smoke passed: dcu {version}, {len(names)} desktop/game tools, interactive desktop available")
    finally:
        process.stdin.close()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)
        reader.join(timeout=5)
        process.stdout.close()
    if process.returncode != 0:
        raise RuntimeError(f"DCU exited with code {process.returncode}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable")
    parser.add_argument("--version", required=True)
    args = parser.parse_args()
    smoke(args.executable, args.version)
