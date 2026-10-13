#!/usr/bin/env python3
"""Read-only live Bloons capture -> candidate mapping -> local Jev-protocol smoke test.

Starts an isolated DCU stdio process from the current Release build and a loopback fake
Jev endpoint. It never changes the game, foreground, cursor, active DCU server, or user settings.
"""
from __future__ import annotations

import base64
import json
import os
import re
import sys
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from uuid import uuid4

sys.path.insert(0, str(Path(__file__).resolve().parent))

from multi_window_stress_regression import ConcurrentMcpClient, foreground_and_cursor  # noqa: E402

ROOT = Path(__file__).resolve().parents[1]
TFM = "net10.0-windows10.0.22621.0"
DCU = ROOT / "src" / "ShadowUse" / "bin" / "Release" / TFM / "dcu.exe"
LABELS = {
    "hero-r1-c1": "dark hooded archer using a crossbow, orange-red accents",
    "hero-r1-c2": "red-haired character holding a black handheld device",
    "hero-r1-c3": "blue-green character wearing a leafy headpiece, arms raised",
    "hero-r1-c4": "green-uniformed commander with a brass horn",
    "hero-r1-c5": "tan character with spiky hair and blue goggles",
    "hero-r1-c6": "green tracked tank with a goggled driver and cannon",
    "hero-r1-c7": "brown-haired character holding an open green book",
    "hero-r1-c8": "purple-hooded mage holding a staff",
    "hero-r2-c1": "dark hooded archer using a cyan-blue bow",
    "hero-r2-c2": "blue-haired character holding a black handheld device",
    "hero-r2-c3": "pale blue-white winged character with horns or tufts and a teal outfit",
    "hero-r2-c4": "yellow-orange helmeted character with round goggles and a small propeller",
    "hero-r2-c5": "yellow armored insect-like character with antennae and dark goggles",
    "hero-r2-c6": "bright-green armored vehicle or character with a black visor and cannon",
    "hero-r2-c7": "white-helmeted character with red headphones at a DJ console",
    "hero-r2-c8": "orange-blue catlike character wearing a blue suit",
}
REQUESTS: list[dict] = []
REQUEST_LOCK = threading.Lock()
SAMPLING_REQUESTS: list[dict] = []


class SamplingMcpClient(ConcurrentMcpClient):
    """Regression client that answers MCP sampling requests locally, without a model provider."""

    def __init__(self, exe: Path, environment: dict[str, str], artifact_dir: Path, handler):
        self._sampling_handler = handler
        super().__init__(exe, environment, artifact_dir)

    @staticmethod
    def _redact_images(message: dict) -> dict:
        redacted = json.loads(json.dumps(message))

        def redact(value):
            if isinstance(value, dict):
                if value.get("type") == "image" and isinstance(value.get("data"), str):
                    value["data"] = f"<redacted image: {len(value['data'])} base64 characters>"
                else:
                    for child in value.values():
                        redact(child)
            elif isinstance(value, list):
                for child in value:
                    redact(child)

        redact(redacted)
        return redacted

    def _read_loop(self) -> None:
        assert self.process.stdout is not None
        try:
            for line in self.process.stdout:
                message = json.loads(line)
                if "method" in message and "id" in message:
                    self._record("server-request", self._redact_images(message))
                    try:
                        if message["method"] != "sampling/createMessage":
                            raise AssertionError(f"unexpected server request: {message['method']}")
                        result = self._sampling_handler(message.get("params", {}))
                        reply = {"jsonrpc": "2.0", "id": message["id"], "result": result}
                    except BaseException as error:
                        reply = {"jsonrpc": "2.0", "id": message["id"], "error": {"code": -32603, "message": str(error)}}
                    with self._lock:
                        self._transcript.write(json.dumps({"direction": "client-response", "message": reply}) + "\n")
                        self._transcript.flush()
                        assert self.process.stdin is not None
                        self.process.stdin.write(json.dumps(reply) + "\n")
                        self.process.stdin.flush()
                    continue
                self._record("response", self._redact_images(message))
                with self._lock:
                    destination = self._pending.get(message.get("id"))
                if destination is not None:
                    destination.put(message)
            failure: BaseException = RuntimeError(f"DCU closed stdout (exit={self.process.poll()})")
        except BaseException as error:
            failure = error
        with self._lock:
            pending = list(self._pending.values())
        for destination in pending:
            destination.put(failure)


def fake_visual_sampling(params: dict) -> dict:
    messages = params.get("messages", [])
    blocks = [block for message in messages for block in message.get("content", [])]
    images = [block for block in blocks if block.get("type") == "image"]
    texts = [block.get("text", "") for block in blocks if block.get("type") == "text"]
    if len(images) != 1 or not texts:
        raise AssertionError("sampling request must contain one image and the discovery prompt")
    jpeg = base64.b64decode(images[0].get("data", ""), validate=True)
    if not jpeg.startswith(b"\xff\xd8"):
        raise AssertionError("sampling image is not a JPEG")
    prompt = "\n".join(texts)
    frame_match = re.search(r'frame_id must be exactly "([^"]+)"', prompt)
    dimensions_match = re.search(r"image_width=(\d+); image_height=(\d+);", prompt)
    scope_match = re.search(r"scope must be exactly (.+?)\. Every candidate", prompt, re.S)
    if not frame_match or not dimensions_match or not scope_match:
        raise AssertionError(f"sampling prompt omitted frame binding or scope: {prompt}")
    width, height = map(int, dimensions_match.groups())
    frame_id = frame_match.group(1)
    scope = json.loads(scope_match.group(1))
    box_width, box_height = max(1, width // 12), max(1, height // 8)
    candidate_set = {
        "frame_id": frame_id,
        "image_width": width,
        "image_height": height,
        "scope": scope,
        "candidate_set_complete": True,
        "coverage_evidence": "Fake test handler response; not an actual coverage assessment.",
        "observed_text": [],
        "candidates": [
            {"key": "hero-r2-c1", "role": "hero card", "label": "dark archer with a cyan-blue bow", "identity_source": "fake_test_sampling_handler", "bbox": {"x": width // 5, "y": height // 2, "width": box_width, "height": box_height}},
            {"key": "hero-r1-c1", "role": "hero card", "label": "dark archer with orange-red accents", "identity_source": "fake_test_sampling_handler", "bbox": {"x": width // 3, "y": height // 2, "width": box_width, "height": box_height}},
        ],
        "context_regions": [],
    }
    with REQUEST_LOCK:
        SAMPLING_REQUESTS.append({"frame_id": frame_id, "width": width, "height": height, "jpeg_bytes": len(jpeg), "params": params})
    return {"role": "assistant", "model": "fake-mcp-vision-v1", "stopReason": "endTurn", "content": [{"type": "text", "text": json.dumps(candidate_set)}]}


class FakeJevHandler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_POST(self):  # noqa: N802
        length = int(self.headers.get("Content-Length", "0"))
        body = json.loads(self.rfile.read(length))
        with REQUEST_LOCK:
            REQUESTS.append(body)
        questions = body["questions"]
        operation = "RECOMMEND_TARGET" if body["state"].get("candidate_set_complete") else "REOBSERVE"
        operation_options = list(questions["operation"]["criteria"])
        operation_probs = {
            key: (0.9 if key == operation else 0.1 / (len(operation_options) - 1))
            for key in operation_options
        }
        answers = {"operation": {"choice": operation, "confidence": 0.9, "probabilities": operation_probs}}
        if operation == "RECOMMEND_TARGET":
            target_question = questions["recommend_target_target"]["criteria"]
            chosen = next(
                key for key, value in target_question.items()
                if value["metadata"]["candidate_key"] == "hero-r2-c1"
            )
            options = list(target_question)
            target_probs = {key: (0.9 if key == chosen else 0.1 / (len(options) - 1)) for key in options}
            answers["recommend_target_target"] = {"choice": chosen, "confidence": 0.9, "probabilities": target_probs}
        payload = json.dumps({"model": "local-fake-jev-v1", "answers": answers}).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)


def _settings_environment(artifact_dir: Path, base_url: str) -> dict[str, str]:
    appdata = artifact_dir / "appdata"
    settings_dir = appdata / "shadow-use"
    settings_dir.mkdir(parents=True)
    (settings_dir / "settings.json").write_text(json.dumps({
        "GameMode": True,
        "JevMode": "assist",
        "JevModel": "local-fake-jev-v1",
        "JevBaseUrl": base_url,
        "JevTimeoutMs": 8000,
        "FastInput": False,
        "HumanizedInput": False,
        "ShowVirtualCursor": False,
        "EnableFocusGuard": True,
        "RemoteTransportUrl": None,
    }), encoding="utf-8")
    env = os.environ.copy()
    env["APPDATA"] = str(appdata)
    env["TYPESAFE_API_KEY"] = "local-loopback-only-not-a-real-credential"
    return env


def _tool_or_raise(client: ConcurrentMcpClient, name: str, arguments: dict | None = None) -> dict:
    result = client.tool(name, arguments or {})
    if "error" in result:
        raise AssertionError(f"{name} failed: {result['error']}")
    return result


def main() -> int:
    if not DCU.is_file():
        raise FileNotFoundError(f"Current Release apphost not found; build first: {DCU}")
    local_settings = DCU.parent / "settings.json"
    if local_settings.exists():
        raise RuntimeError(f"Refusing to override executable-local settings: {local_settings}")

    server = HTTPServer(("127.0.0.1", 0), FakeJevHandler)
    port = server.server_address[1]
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    artifact_dir = Path(tempfile.gettempdir()) / "dcu-bloons-jev-regression" / uuid4().hex
    artifact_dir.mkdir(parents=True)
    client: ConcurrentMcpClient | None = None
    foreground_before, cursor_before = foreground_and_cursor()
    try:
        test_environment = _settings_environment(artifact_dir, f"http://127.0.0.1:{port}/v1/systemone")
        client = SamplingMcpClient(DCU, test_environment, artifact_dir, fake_visual_sampling)
        initialized = client.call("initialize", {
            "protocolVersion": "2025-03-26",
            "capabilities": {"sampling": {}}, 
            "clientInfo": {"name": "bloons-jev-regression", "version": "1.0"},
        })
        if "error" in initialized:
            raise AssertionError(f"MCP initialize failed: {initialized}")
        client.notify("notifications/initialized")

        health = _tool_or_raise(client, "health_check")
        if not health.get("input_settings", {}).get("game_mode"):
            raise AssertionError(f"isolated server did not enable GameMode: {health}")
        apps = _tool_or_raise(client, "list_apps").get("apps", [])
        game = [item for item in apps if item.get("title") == "Bloons TD Battles 2"]
        if len(game) != 1:
            raise AssertionError(f"expected exactly one open Bloons TD Battles 2 window; found {game}")
        app = game[0]["window_id"]
        ownership_before = _tool_or_raise(client, "game_input_ownership", {"app": app}).get("input_ownership", {})

        screenshot = _tool_or_raise(client, "game_screenshot", {
            "app": app,
            "max_width": 1680,
            "quality": 88,
            "bring_to_front": False,
            "include_ui_candidates": True,
        })
        ui = screenshot.get("ui_observation")
        if ui is None:
            raise AssertionError(f"screenshot omitted ui_observation: {screenshot}")
        candidates = ui.get("candidates", [])
        if not ui.get("candidate_set_complete"):
            raise AssertionError(f"calibrated detector correctly refused incomplete coverage: {ui}")
        if len(candidates) != 23:
            raise AssertionError(f"expected 16 hero cards, two profile-banner candidates, and five controls; found {len(candidates)}")
        keys = {candidate["key"] for candidate in candidates}
        if {f"hero-r{row}-c{col}" for row in (1, 2) for col in range(1, 9)} - keys:
            raise AssertionError(f"missing hero card slots: {keys}")
        for candidate in candidates:
            box = candidate["bbox"]
            if box["width"] <= 0 or box["height"] <= 0 or box["x"] < 0 or box["y"] < 0:
                raise AssertionError(f"invalid candidate bbox: {candidate}")

        decision = _tool_or_raise(client, "game_jev_decide", {
            "app": app,
            "goal": "Recommend the cyan-blue-bow archer, not the orange-red-bow archer. Do not ready or confirm.",
            "observation_id": screenshot["frame_id"],
            "visual_labels_json": json.dumps(LABELS),
        })
        if decision.get("decision_only") is not True or decision.get("no_input_sent") is not True:
            raise AssertionError(f"Jev response was not decision-only: {decision}")
        target = decision.get("target") or {}
        if target.get("key") != "hero-r2-c1" or target.get("bbox", {}).get("width", 0) <= 0:
            raise AssertionError(f"fake Jev did not map back to the expected frame-bound card: {decision}")
        chosen_candidate = next(candidate for candidate in candidates if candidate["key"] == "hero-r2-c1")
        window = screenshot["window"]
        scale = ui["image_scale_exact"]
        expected_screen_bbox = {
            "x": window["left"] + round(chosen_candidate["bbox"]["x"] / scale),
            "y": window["top"] + round(chosen_candidate["bbox"]["y"] / scale),
            "width": max(1, round(chosen_candidate["bbox"]["width"] / scale)),
            "height": max(1, round(chosen_candidate["bbox"]["height"] / scale)),
        }
        if target.get("bbox") != chosen_candidate["bbox"] or target.get("screen_bbox") != expected_screen_bbox:
            raise AssertionError(f"Jev target coordinate mapping disagrees with screenshot evidence: {target}")
        generic_set = {
            "frame_id": screenshot["frame_id"],
            "image_width": screenshot["width"],
            "image_height": screenshot["height"],
            "scope": ui["scope"],
            "candidate_set_complete": ui["candidate_set_complete"],
            "coverage_evidence": "Transport fixture generated candidates from the same-frame calibrated detector; this validates serialization, not general parser recall.",
            "observed_text": ui["observed_text"],
            "candidates": [
                {
                    "key": candidate["key"],
                    "role": candidate["role"],
                    "label": candidate["label"] + (" — " + LABELS[candidate["key"]] if candidate["key"] in LABELS else ""),
                    "identity_source": "upstream_visual_description" if candidate["key"] in LABELS else "calibrated_pixel_detector",
                    "bbox": candidate["bbox"],
                }
                for candidate in candidates
            ],
            "context_regions": [
                {"key": region["key"], "label": region["label"], "bbox": region["bbox"], "note": region["note"]}
                for region in ui["context_regions"]
            ],
        }
        generic_decision = _tool_or_raise(client, "game_jev_decide_candidates", {
            "app": app,
            "goal": "Recommend the cyan-blue-bow archer, not the orange-red-bow archer. Do not ready or confirm.",
            "observation_id": screenshot["frame_id"],
            "candidate_set_json": json.dumps(generic_set),
        })
        if generic_decision.get("decision_only") is not True or generic_decision.get("no_input_sent") is not True:
            raise AssertionError(f"generic Jev response was not decision-only: {generic_decision}")
        if generic_decision.get("candidate_set_completeness_source") != "caller_declared_coverage_unverified":
            raise AssertionError(f"generic path omitted caller coverage provenance: {generic_decision}")
        map_region = next(region for region in ui["context_regions"] if region["key"] == "map-preview")
        expected_context_box = f"{map_region['label']} [{map_region['bbox']['x']},{map_region['bbox']['y']},{map_region['bbox']['width']},{map_region['bbox']['height']}]"
        generic_context = REQUESTS[1]["questions"]["operation"]["instructions"]["context"]
        if expected_context_box not in generic_context:
            raise AssertionError(f"context-region bbox was omitted from the Jev call: {expected_context_box}")
        generic_target = generic_decision.get("target") or {}
        if generic_target.get("key") != "hero-r2-c1":
            raise AssertionError(f"generic parser bridge selected the wrong frame-bound candidate: {generic_decision}")
        if generic_target.get("bbox") != chosen_candidate["bbox"] or generic_target.get("screen_bbox") != expected_screen_bbox:
            raise AssertionError(f"generic Jev coordinate mapping disagrees with screenshot evidence: {generic_target}")

        incomplete_set = generic_set | {
            "candidate_set_complete": False,
            "candidate_set_issue": "unrecognized visual regions may contain additional controls",
        }
        strict_result = client.tool("game_jev_decide_candidates", {
            "app": app,
            "goal": "Recommend the cyan-blue-bow archer, not the orange-red-bow archer.",
            "observation_id": screenshot["frame_id"],
            "candidate_set_json": json.dumps(incomplete_set),
        })
        if strict_result.get("candidate_set_complete") is not False or strict_result.get("no_input_sent") is not True:
            raise AssertionError(f"strict mode did not reject incomplete candidates: {strict_result}")
        if len(REQUESTS) != 2:
            raise AssertionError("strict incomplete mode unexpectedly sent a Jev request")

        advisory = _tool_or_raise(client, "game_jev_decide_candidates", {
            "app": app,
            "goal": "Recommend the cyan-blue-bow archer, not the orange-red-bow archer.",
            "observation_id": screenshot["frame_id"],
            "candidate_set_json": json.dumps(incomplete_set),
            "allow_incomplete_coverage": True,
        })
        if advisory.get("candidate_set_complete") is not False or advisory.get("advisory_only") is not True:
            raise AssertionError(f"incomplete advisory status was not preserved: {advisory}")
        if advisory.get("operation") != "REOBSERVE" or advisory.get("target") is not None:
            raise AssertionError(f"fake Jev should abstain on incomplete coverage: {advisory}")
        if len(REQUESTS) != 3:
            raise AssertionError(f"expected three loopback Jev POSTs, got {len(REQUESTS)}")
        for index, request in enumerate(REQUESTS):
            if request["state"]["candidate_count"] != 23:
                raise AssertionError(f"Jev request did not contain all 23 scoped candidates: {request['state'].get('candidate_count')}")
            expected_frame_id = masked_screenshot["frame_id"] if index == 3 else screenshot["frame_id"]
            if request["state"]["observation_id"] != expected_frame_id:
                raise AssertionError("screenshot and Jev request frame IDs differ")
        if REQUESTS[2]["state"]["candidate_set_complete"] is not False:
            raise AssertionError("incomplete coverage status was not transmitted to Jev")
        if REQUESTS[2]["state"]["candidate_set_issue"] != incomplete_set["candidate_set_issue"]:
            raise AssertionError("incomplete coverage issue was not transmitted to Jev")
        ownership_after = _tool_or_raise(client, "game_input_ownership", {"app": app}).get("input_ownership", {})
        if ownership_before.get("state") != ownership_after.get("state"):
            raise AssertionError(f"main recommendation calls changed game input ownership: {ownership_before} -> {ownership_after}")

        masked_screenshot = _tool_or_raise(client, "game_screenshot", {
            "app": app,
            "max_width": 1680,
            "quality": 88,
            "bring_to_front": False,
            "exclude_boxes": json.dumps([{"x": 5, "y": 5, "width": 40, "height": 20}]),
            "include_ui_candidates": True,
        })
        if masked_screenshot.get("masked_regions") != 1 or len(masked_screenshot.get("masked_image_boxes", [])) != 1:
            raise AssertionError(f"masked screenshot did not preserve excluded-region provenance: {masked_screenshot}")
        calibrated_masked_result = client.tool("game_jev_decide", {
            "app": app,
            "goal": "Recommend the cyan-blue-bow archer.",
            "observation_id": masked_screenshot["frame_id"],
            "visual_labels_json": json.dumps(LABELS),
        })
        if calibrated_masked_result.get("candidate_set_complete") is not False or "masked" not in calibrated_masked_result.get("error", "").lower():
            raise AssertionError(f"calibrated path accepted masked screenshot coverage: {calibrated_masked_result}")
        if len(REQUESTS) != 3:
            raise AssertionError("masked calibrated observation unexpectedly triggered a Jev POST")

        masked_set = generic_set | {
            "frame_id": masked_screenshot["frame_id"],
            "image_width": masked_screenshot["width"],
            "image_height": masked_screenshot["height"],
            "coverage_evidence": "Test fixture reused a previous unmasked inventory only to validate mask handling.",
        }
        masked_result = client.tool("game_jev_decide_candidates", {
            "app": app,
            "goal": "Recommend the cyan-blue-bow archer.",
            "observation_id": masked_screenshot["frame_id"],
            "candidate_set_json": json.dumps(masked_set),
        })
        if masked_result.get("candidate_set_complete") is not False or "masked" not in masked_result.get("error", "").lower():
            raise AssertionError(f"complete coverage claim passed despite masked screenshot regions: {masked_result}")
        if len(REQUESTS) != 3:
            raise AssertionError("masked complete-coverage claim unexpectedly triggered a Jev POST")
        masked_incomplete_set = masked_set | {
            "candidate_set_complete": False,
            "candidate_set_issue": "the inventory was reused from a prior unmasked frame, so this frame is not complete",
        }
        masked_advisory = _tool_or_raise(client, "game_jev_decide_candidates", {
            "app": app,
            "goal": "Recommend the cyan-blue-bow archer.",
            "observation_id": masked_screenshot["frame_id"],
            "candidate_set_json": json.dumps(masked_incomplete_set),
            "allow_incomplete_coverage": True,
        })
        if masked_advisory.get("operation") != "REOBSERVE" or masked_advisory.get("candidate_set_complete") is not False:
            raise AssertionError(f"masked advisory did not preserve its incomplete status: {masked_advisory}")
        if len(REQUESTS) != 4 or "masked image-space region" not in REQUESTS[3]["state"]["candidate_set_issue"]:
            raise AssertionError("masked-region details were not included in the advisory Jev request")

        if SAMPLING_REQUESTS:
            raise AssertionError("unexpected MCP sampling call before the explicit opt-in screenshot")
        automatic_screenshot = _tool_or_raise(client, "game_screenshot", {
            "app": app,
            "max_width": 1680,
            "quality": 88,
            "bring_to_front": False,
            "include_vlm_candidates": True,
            "visual_scope": "Visible hero cards and selection controls",
        })
        automatic_ui = automatic_screenshot.get("ui_observation") or {}
        if len(SAMPLING_REQUESTS) != 1 or SAMPLING_REQUESTS[0]["frame_id"] != automatic_screenshot["frame_id"]:
            raise AssertionError("opt-in visual discovery did not sample the exact returned frame")
        if automatic_ui.get("candidate_generator") != "fake-mcp-vision-v1" or len(automatic_ui.get("candidates", [])) != 2:
            raise AssertionError(f"MCP client candidate output was not parsed and cached: {automatic_ui}")
        if automatic_ui.get("candidate_set_completeness_source") != "mcp_client_model_asserted_coverage_unverified":
            raise AssertionError("MCP client coverage claim was not marked unverified")
        if SAMPLING_REQUESTS[0]["jpeg_bytes"] <= 4 or SAMPLING_REQUESTS[0]["width"] != automatic_screenshot["width"]:
            raise AssertionError("sampling request did not contain the screenshot JPEG and matching image dimensions")
        if len(REQUESTS) != 4:
            raise AssertionError("candidate discovery unexpectedly sent a Jev request before the decision call")
        automatic_decision = _tool_or_raise(client, "game_jev_decide_candidates", {
            "app": app,
            "goal": "Recommend the cyan-blue-bow archer. Do not confirm or ready.",
            "observation_id": automatic_screenshot["frame_id"],
        })
        if automatic_decision.get("decision_only") is not True or automatic_decision.get("no_input_sent") is not True:
            raise AssertionError(f"cached MCP visual inventory did not reach decision-only Jev: {automatic_decision}")
        if automatic_decision.get("candidate_set_completeness_source") != "mcp_client_model_asserted_coverage_unverified":
            raise AssertionError("JeV result omitted MCP-model coverage provenance")
        automatic_target = automatic_decision.get("target") or {}
        selected_auto_candidate = next(item for item in automatic_ui["candidates"] if item["key"] == "hero-r2-c1")
        if automatic_target.get("key") != "hero-r2-c1" or automatic_target.get("bbox") != selected_auto_candidate["bbox"]:
            raise AssertionError(f"MCP visual candidate was not mapped to its frame-bound Jev target: {automatic_decision}")
        if automatic_decision.get("coverage_status") != "mcp_client_model_asserted_complete_unverified":
            raise AssertionError("Jev response did not distinguish the MCP model's unverified completeness assertion")
        if len(REQUESTS) != 5 or REQUESTS[4]["state"].get("candidate_count") != 2:
            raise AssertionError("Jev did not receive the sampled visual candidate inventory")
        if REQUESTS[4]["state"].get("observation_id") != automatic_screenshot["frame_id"]:
            raise AssertionError("automatic visual discovery and Jev request frame IDs differ")
        automatic_option = next(
            item for item in REQUESTS[4]["questions"]["recommend_target_target"]["criteria"].values()
            if item["metadata"].get("candidate_key") == "hero-r2-c1"
        )
        if automatic_option["metadata"].get("source") != "mcp_client_multimodal_visual_parser":
            raise AssertionError("Jev candidate provenance did not identify MCP-client visual parsing")
        if automatic_option["metadata"].get("coverage_status") != "mcp_client_model_asserted_complete_unverified":
            raise AssertionError("Jev candidate metadata overstated MCP model coverage")

        no_sampling_dir = artifact_dir / "no-sampling-client"
        no_sampling_dir.mkdir()
        unsupported_client = ConcurrentMcpClient(DCU, test_environment, no_sampling_dir)
        try:
            initialized_without_sampling = unsupported_client.call("initialize", {
                "protocolVersion": "2025-03-26",
                "capabilities": {},
                "clientInfo": {"name": "bloons-jev-no-sampling", "version": "1.0"},
            })
            if "error" in initialized_without_sampling:
                raise AssertionError(f"non-sampling MCP initialize failed: {initialized_without_sampling}")
            unsupported_client.notify("notifications/initialized")
            unsupported_response = unsupported_client.call("tools/call", {"name": "game_screenshot", "arguments": {
                "app": app, "max_width": 1680, "quality": 88, "bring_to_front": False,
                "include_vlm_candidates": True,
            }})
            unsupported_content = unsupported_response.get("result", {}).get("content", [])
            unsupported_screenshot = json.loads(unsupported_content[0]["text"])
            if not unsupported_screenshot.get("visual_candidate_error") or unsupported_screenshot.get("ui_observation") is not None:
                raise AssertionError(f"unsupported MCP sampling did not fail closed: {unsupported_screenshot}")
            if not any(block.get("type") == "image" and block.get("data") for block in unsupported_content):
                raise AssertionError("sampling failure did not return the captured image for outer-model analysis")
            if len(REQUESTS) != 5:
                raise AssertionError("unsupported MCP sampling unexpectedly sent a Jev request")
        finally:
            unsupported_client.close()
        unsupported_sampling_fails_closed = True

        latest_foreground, latest_cursor = foreground_and_cursor()

        print(json.dumps({
            "passed": True,
            "source": "current Release DCU process",
            "game_window": app,
            "observation_id": screenshot["frame_id"],
            "frame_dimensions": screenshot["frame_dimensions"],
            "image_dimensions": [screenshot["width"], screenshot["height"]],
            "image_scale_exact": ui["image_scale_exact"],
            "candidate_count": len(candidates),
            "candidate_set_complete": ui["candidate_set_complete"],
            "perception_ms": ui.get("perception_ms"),
            "jev_latency_ms": decision.get("latency_ms"),
            "generic_jev_latency_ms": generic_decision.get("latency_ms"),
            "selected_key": target["key"],
            "generic_selected_key": (generic_decision.get("target") or {}).get("key"),
            "incomplete_advisory_operation": advisory.get("operation"),
            "incomplete_candidate_set_complete": advisory.get("candidate_set_complete"),
            "masked_regions_tested": masked_screenshot.get("masked_regions"),
            "calibrated_masked_claim_refused": calibrated_masked_result.get("candidate_set_complete") is False,
            "masked_complete_claim_refused": masked_result.get("candidate_set_complete") is False,
            "masked_advisory_operation": masked_advisory.get("operation"),
            "mcp_sampling_calls": len(SAMPLING_REQUESTS),
            "automatic_candidate_count": len(automatic_ui.get("candidates", [])),
            "automatic_candidate_generator": automatic_ui.get("candidate_generator"),
            "automatic_jev_target": automatic_target.get("key"),
            "unsupported_sampling_fails_closed": unsupported_sampling_fails_closed,
            "selected_bbox": target["bbox"],
            "selected_screen_bbox": target["screen_bbox"],
            "loopback_posts": len(REQUESTS),
            "game_input_sent": False,
            "foreground_unchanged_during_test": latest_foreground == foreground_before,
            "foreground_before": foreground_before,
            "foreground_after": latest_foreground,
            "input_ownership_before": ownership_before,
            "input_ownership_after": ownership_after,
            "cursor_position_changed_during_test": latest_cursor != cursor_before,
            "artifact_dir": str(artifact_dir),
        }, indent=2))
        return 0
    except BaseException as error:
        (artifact_dir / "failure.txt").write_text(repr(error), encoding="utf-8")
        print(f"FAIL artifact_dir={artifact_dir} error={error}", file=sys.stderr)
        return 1
    finally:
        server.shutdown()
        server.server_close()
        if client is not None:
            try:
                client.close()
            except Exception:
                client.abort()


if __name__ == "__main__":
    raise SystemExit(main())
