#!/usr/bin/env python3
"""Fuse frame-bound local YOLO and edge proposals into a Jev candidate_set_json object."""
from __future__ import annotations
import argparse
import json
from pathlib import Path
from typing import Any


def load_input(path: Path, use_raw_candidates: bool = False) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    root = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(root, dict):
        raise ValueError(f"{path}: root must be a JSON object")
    if isinstance(root.get("content"), list):
        text = next((block.get("text") for block in root["content"]
                     if isinstance(block, dict) and block.get("type") == "text" and isinstance(block.get("text"), str)), None)
        if text is not None:
            root = json.loads(text)
            if not isinstance(root, dict):
                raise ValueError(f"{path}: MCP text payload must contain a JSON object")
    candidate_set = root
    nested = root.get("candidate_set_json")
    if isinstance(nested, str):
        candidate_set = json.loads(nested)
    elif isinstance(root.get("ui_observation"), dict):
        nested = root["ui_observation"].get("candidate_set_json")
        if not isinstance(nested, str):
            raise ValueError(f"{path}: ui_observation has no candidate_set_json")
        candidate_set = json.loads(nested)
    if not isinstance(candidate_set, dict):
        raise ValueError(f"{path}: candidate_set_json must be an object")
    candidates = root.get("candidates") if use_raw_candidates else None
    if not isinstance(candidates, list):
        candidates = root.get("detector_candidates") if use_raw_candidates else candidate_set.get("candidates")
    if not isinstance(candidates, list):
        raise ValueError(f"{path}: candidates must be an array")
    return candidate_set, candidates


def _priority(candidate: dict[str, Any], source: str) -> float:
    if source == "yolo":
        value = candidate.get("score", candidate.get("proposal_score", 0))
    else:
        value = candidate.get("proposal_score")
        if value is None:
            value = candidate.get("geometry_evidence", {}).get("value", 0)
    return float(value) if isinstance(value, (int, float)) else 0.0


def _bbox(candidate: dict[str, Any]) -> dict[str, int]:
    box = candidate.get("bbox")
    if not isinstance(box, dict):
        raise ValueError("each proposal must have a bbox object")
    values = {key: box.get(key) for key in ("x", "y", "width", "height")}
    if any(type(value) is not int for value in values.values()):
        raise ValueError("bbox x, y, width, and height must be integers")
    if values["x"] < 0 or values["y"] < 0 or values["width"] <= 0 or values["height"] <= 0:
        raise ValueError("bbox must have positive extent and non-negative origin")
    return values


def _iou(a: dict[str, int], b: dict[str, int]) -> float:
    left, top = max(a["x"], b["x"]), max(a["y"], b["y"])
    right = min(a["x"] + a["width"], b["x"] + b["width"])
    bottom = min(a["y"] + a["height"], b["y"] + b["height"])
    intersection = max(0, right - left) * max(0, bottom - top)
    area = a["width"] * a["height"] + b["width"] * b["height"] - intersection
    return intersection / max(1, area)


def _ocr_candidates(edge_candidates: list[dict[str, Any]]) -> list[tuple[str, dict[str, int]]]:
    return [(candidate["label"].strip(), _bbox(candidate)) for candidate in edge_candidates
            if isinstance(candidate.get("identity_source", candidate.get("method")), str)
            and "windows_media_ocr" in candidate.get("identity_source", candidate.get("method")).split("+")
            and isinstance(candidate.get("label"), str) and candidate["label"].strip()]


def _ocr_relation(box: dict[str, int], text_box: dict[str, int], image_width: int) -> tuple[float, int, float, bool]:
    left, top = max(box["x"], text_box["x"]), max(box["y"], text_box["y"])
    right = min(box["x"] + box["width"], text_box["x"] + text_box["width"])
    bottom = min(box["y"] + box["height"], text_box["y"] + text_box["height"])
    text_area = text_box["width"] * text_box["height"]
    overlap_fraction = max(0, right - left) * max(0, bottom - top) / text_area
    gap = (box["x"] - text_box["x"] - text_box["width"] if text_box["x"] + text_box["width"] < box["x"]
           else text_box["x"] - box["x"] - box["width"] if box["x"] + box["width"] < text_box["x"] else 0)
    vertical_distance = abs(text_box["y"] + text_box["height"] / 2 - box["y"] - box["height"] / 2)
    row_tolerance = min(48, max(24, max(box["height"], text_box["height"]) * 0.5))
    text_at_or_left = (text_box["x"] + text_box["width"] / 2 <= box["x"] + box["width"] / 2
                       or gap <= 32)
    similar_sized = box["width"] * box["height"] <= 4 * text_area
    same_row = (box["height"] <= 160 and text_box["height"] <= 80 and similar_sized and text_at_or_left
                and vertical_distance <= row_tolerance and gap <= max(80, image_width * 0.5))
    return overlap_fraction, gap, vertical_distance, same_row


def _overlapping_ocr_text(box: dict[str, int], ocr_candidates: list[tuple[str, dict[str, int]]],
                          image_width: int) -> list[str]:
    relations = [(text, _ocr_relation(box, text_box, image_width)[0], text_box["width"] * text_box["height"], text_box)
                 for text, text_box in ocr_candidates]
    overlapping = []
    for text, _, _, _ in sorted((item for item in relations if item[1] >= 0.6),
                                key=lambda item: (-item[1], item[2], item[3]["y"], item[3]["x"])):
        if text.casefold() not in {item.casefold() for item in overlapping}:
            overlapping.append(text)
        if len(overlapping) == 3:
            break
    return overlapping


def _nearby_ocr_by_key(detector_candidates: list[dict[str, Any]], ocr_candidates: list[tuple[str, dict[str, int]]],
                       image_width: int, max_detector: int, iou_threshold: float) -> dict[str, str]:
    if max_detector == 0:
        return {}
    selected: list[tuple[dict[str, Any], dict[str, int]]] = []
    retained_boxes: list[dict[str, int]] = []
    ranked = sorted(enumerate(detector_candidates), key=lambda item: (-_priority(item[1], "yolo"), item[0]))
    for _, candidate in ranked:
        if not isinstance(candidate, dict):
            continue
        box = _bbox(candidate)
        if any(_iou(box, old) > iou_threshold for old in retained_boxes):
            continue
        retained_boxes.append(box)
        selected.append((candidate, box))
        if len(selected) == max_detector:
            break

    overlapping_keys = {
        candidate.get("key") for candidate, box in selected
        if isinstance(candidate.get("key"), str)
        and _overlapping_ocr_text(box, ocr_candidates, image_width)
    }
    nearby: dict[str, str] = {}
    used_text: set[str] = set()
    for text, text_box in sorted(ocr_candidates,
                                key=lambda item: (item[1]["width"] * item[1]["height"], item[1]["y"], item[1]["x"])):
        if text.casefold() in used_text:
            continue
        options = []
        for candidate, box in selected:
            key = candidate.get("key")
            if not isinstance(key, str) or key in overlapping_keys or key in nearby:
                continue
            overlap, gap, vertical_distance, same_row = _ocr_relation(box, text_box, image_width)
            if same_row:
                center_distance = abs(box["x"] + box["width"] / 2 - text_box["x"] - text_box["width"] / 2)
                options.append((gap, vertical_distance, center_distance, -_priority(candidate, "yolo"), key))
        if options:
            key = min(options)[-1]
            nearby[key] = text
            used_text.add(text.casefold())
    return nearby


def fuse(detector_set: dict[str, Any], detector_candidates: list[dict[str, Any]],
         edge_set: dict[str, Any], edge_candidates: list[dict[str, Any]],
         max_detector: int = 160, max_edge: int = 27,
         detector_iou_threshold: float = 0.2) -> dict[str, Any]:
    for field in ("frame_id", "image_width", "image_height"):
        if detector_set.get(field) != edge_set.get(field):
            raise ValueError(f"detector and edge observations disagree on {field}")
    frame_id = detector_set.get("frame_id")
    width, height = detector_set.get("image_width"), detector_set.get("image_height")
    if not isinstance(frame_id, str) or not frame_id.strip():
        raise ValueError("frame_id must be a non-empty string")
    if type(width) is not int or type(height) is not int or width <= 0 or height <= 0:
        raise ValueError("image_width and image_height must be positive integers")
    if max_detector < 0 or max_edge < 0 or max_detector + max_edge > 255:
        raise ValueError("proposal limits must be non-negative and total at most 255")
    if not 0 <= detector_iou_threshold < 1:
        raise ValueError("detector_iou_threshold must be in [0,1)")

    ocr_candidates = _ocr_candidates(edge_candidates)
    nearby_ocr_by_key = _nearby_ocr_by_key(detector_candidates, ocr_candidates, width, max_detector,
                                           detector_iou_threshold)
    chosen: list[dict[str, Any]] = []
    for source, candidates, limit in (("yolo", detector_candidates, max_detector),
                                      ("edge", edge_candidates, max_edge)):
        ranked = sorted(enumerate(candidates), key=lambda item: (-_priority(item[1], source), item[0]))
        retained_detector_boxes: list[dict[str, int]] = []
        for _, candidate in ranked:
            if not isinstance(candidate, dict):
                raise ValueError(f"{source} candidate must be an object")
            bbox = _bbox(candidate)
            if bbox["x"] + bbox["width"] > width or bbox["y"] + bbox["height"] > height:
                raise ValueError(f"{source} candidate bbox is outside the source image")
            if source == "yolo":
                if any(_iou(bbox, old) > detector_iou_threshold for old in retained_detector_boxes):
                    continue
                retained_detector_boxes.append(bbox)
            key = candidate.get("key")
            role = candidate.get("role")
            label = candidate.get("label")
            if not isinstance(key, str) or not key.strip() or not isinstance(role, str) or not role.strip() \
                    or not isinstance(label, str) or not label.strip():
                raise ValueError(f"{source} candidate needs key, role, and label")
            identity_source = candidate.get("identity_source") or candidate.get("method") or source
            if not isinstance(identity_source, str):
                identity_source = source
            if source == "yolo":
                overlapping_text = _overlapping_ocr_text(bbox, ocr_candidates, width)
                nearby_text = nearby_ocr_by_key.get(key)
                if overlapping_text:
                    label += "; overlapping unverified OCR text: " + " | ".join(overlapping_text)
                if nearby_text:
                    label += "; nearby unverified OCR text: " + nearby_text
                if overlapping_text or nearby_text:
                    identity_source += "+windows_media_ocr"
            chosen.append({
                "key": f"{source}:{key}"[:80],
                "role": role[:80],
                "label": label[:240],
                "identity_source": identity_source[:100],
                "bbox": bbox,
            })
            if sum(1 for item in chosen if item["key"].startswith(f"{source}:")) >= limit:
                break
    keys = [candidate["key"] for candidate in chosen]
    if len(keys) != len(set(keys)):
        raise ValueError("proposal keys are not unique after source prefixes")

    detector_count = sum(item["key"].startswith("yolo:") for item in chosen)
    edge_count = sum(item["key"].startswith("edge:") for item in chosen)
    issue = (f"Fused local proposals: {detector_count} YOLO boxes after secondary NMS at IoU {detector_iou_threshold:.2f} "
             f"and {edge_count} edge boxes. The heuristic inventory is incomplete; recall, clickability, "
             "identity, and enabled state are not established.")
    observed_text = edge_set.get("observed_text", [])
    if not isinstance(observed_text, list) or any(not isinstance(text, str) for text in observed_text):
        observed_text = []
    context = edge_set.get("context_regions", [])
    if not isinstance(context, list):
        context = []
    return {
        "frame_id": frame_id,
        "image_width": width,
        "image_height": height,
        "scope": "Visible UI-like regions proposed by local YOLO and edge detectors",
        "candidate_set_complete": False,
        "candidate_set_issue": issue,
        "observed_text": observed_text[:40],
        "candidates": chosen,
        "context_regions": context[:32],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--yolo-json", type=Path, required=True, help="Output JSON from ui_candidate_detect.py")
    parser.add_argument("--edge-json", type=Path, required=True, help="game_screenshot or ui_candidates_detect_image JSON containing candidate_set_json")
    parser.add_argument("--max-detector", type=int, default=160, help="Highest-scored detector proposals after secondary NMS (default 160; total cap 187)")
    parser.add_argument("--max-edge", type=int, default=27, help="Highest-scored edge proposals (default 27; total cap 255)")
    parser.add_argument("--detector-iou-threshold", type=float, default=0.2, help="Suppress near-duplicate detector boxes above this IoU (default 0.2)")
    parser.add_argument("--output", type=Path, help="Optional new JSON file; existing files are never overwritten")
    args = parser.parse_args()
    try:
        detector_set, detector_candidates = load_input(args.yolo_json, use_raw_candidates=True)
        edge_set, edge_candidates = load_input(args.edge_json)
        output = fuse(detector_set, detector_candidates, edge_set, edge_candidates,
                      args.max_detector, args.max_edge, args.detector_iou_threshold)
    except (OSError, json.JSONDecodeError, ValueError) as error:
        parser.error(str(error))
    serialized = json.dumps(output, ensure_ascii=False, separators=(",", ":"))
    if args.output:
        with args.output.open("x", encoding="utf-8") as handle:
            handle.write(json.dumps(output, ensure_ascii=False, indent=2))
    print(serialized)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
