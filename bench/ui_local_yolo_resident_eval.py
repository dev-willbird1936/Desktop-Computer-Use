#!/usr/bin/env python3
"""Offline test of the persistent local YOLO worker and bounded YOLO+OCR/edge fusion.

This script uses only local images, weights, and marked-positive references. It makes no network calls.
"""
from __future__ import annotations

import argparse
import base64
from io import BytesIO
import hashlib
import json
from pathlib import Path
import statistics
import subprocess
import time

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
import sys
sys.path.insert(0, str(ROOT / "tools"))
import ui_candidate_fuse


def score_candidates(candidates: list[dict], target_set: dict, image_width: int, image_height: int) -> dict:
    reference_width = target_set.get("reference_width", target_set["width"])
    reference_height = target_set.get("reference_height", target_set["height"])
    targets = []
    for target in target_set["targets"]:
        box = target.get("bbox", target.get("reference_bbox"))
        normalized = [
            box["x"] / reference_width * 1000,
            box["y"] / reference_height * 1000,
            (box["x"] + box["width"]) / reference_width * 1000,
            (box["y"] + box["height"]) / reference_height * 1000,
        ]
        targets.append((target, normalized))

    pairs = []
    for target_index, (target, target_box) in enumerate(targets):
        x0, y0, x1, y1 = target_box
        area = max(1, (x1 - x0) * (y1 - y0))
        for candidate_index, candidate in enumerate(candidates):
            box = candidate["bbox"]
            predicted = [box["x"] / image_width * 1000, box["y"] / image_height * 1000,
                         (box["x"] + box["width"]) / image_width * 1000,
                         (box["y"] + box["height"]) / image_height * 1000]
            predicted_area = (predicted[2] - predicted[0]) * (predicted[3] - predicted[1])
            if target["type"] == "card":
                intersection = max(0, min(predicted[2], x1) - max(predicted[0], x0)) * max(0, min(predicted[3], y1) - max(predicted[1], y0))
                overlap = intersection / max(1, predicted_area + area - intersection)
                if overlap >= 0.5:
                    pairs.append((overlap, target_index, candidate_index))
            elif (predicted[0] <= (x0 + x1) / 2 <= predicted[2]
                  and predicted[1] <= (y0 + y1) / 2 <= predicted[3]
                  and predicted_area <= area * 8):
                pairs.append((1 - predicted_area / (area * 8), target_index, candidate_index))

    pairs.sort(reverse=True)
    assigned_targets, assigned_candidates, matches = set(), set(), []
    for score, target_index, candidate_index in pairs:
        if target_index in assigned_targets or candidate_index in assigned_candidates:
            continue
        assigned_targets.add(target_index)
        assigned_candidates.add(candidate_index)
        matches.append({"target": targets[target_index][0]["id"],
                        "candidate": candidates[candidate_index]["key"], "score": round(score, 4)})
    missed = [target["id"] for index, (target, _) in enumerate(targets) if index not in assigned_targets]
    return {"matched": len(assigned_targets), "total": len(targets), "missed": missed,
            "matches": matches, "false_positives_scored": False, "candidate_count": len(candidates)}


def edge_input(edge_results: dict, image_key: str, frame_id: str, width: int, height: int,
               local_method: str = "edge") -> tuple[dict, list[dict], int, str | None]:
    methods = edge_results["methods"][image_key]
    record = methods[local_method]
    template_record = methods.get("bloons-template", {})
    template_boxes = template_record.get("boxes", [])
    candidates = []
    for item in record["boxes"]:
        candidates.append({
            "key": item["key"], "role": item["role"], "label": item["label"],
            "identity_source": "windows_media_ocr" if item["key"].startswith("ocr-") else "fast_edge_component",
            "proposal_score": item.get("score", 0), "bbox": item["bbox"],
        })
    for item in template_boxes:
        candidates.append({
            "key": item["key"], "role": item.get("role", "calibrated_region"), "label": item["label"],
            "identity_source": "bloons_calibrated_pixel_anchor",
            "proposal_score": 2.0,  # Keep known-layout anchors ahead of generic local proposals, matching C# fusion.
            "bbox": item["bbox"],
        })
    return ({
        "frame_id": frame_id, "image_width": width, "image_height": height,
        "scope": "Local YOLO, OCR/edge and optional named-layout proposals from the exact clean source image",
        "candidate_set_complete": False,
        "candidate_set_issue": "Local heuristic proposals may be incomplete; visual coverage and actionability are unverified.",
        "observed_text": record.get("observed_text", []), "candidates": candidates,
    }, candidates, len(template_boxes), template_record.get("note"))


def run(args: argparse.Namespace) -> dict:
    references = json.loads(args.references.read_text(encoding="utf-8"))
    edge_results = json.loads(args.edge_results.read_text(encoding="utf-8"))
    process_started = time.perf_counter_ns()
    worker = subprocess.Popen(
        [str(args.python), "-u", str(ROOT / "tools" / "ui_candidate_detect.py"), "--serve",
         "--weights", str(args.weights), "--device", args.device,
         "--image-size", str(args.image_size), "--confidence", "0.001", "--iou", "0.45", "--max-raw-detections", "1024"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        text=True, encoding="utf-8", bufsize=1,
    )
    try:
        ready_line = worker.stdout.readline()
        if not ready_line:
            raise RuntimeError("Worker exited before ready: " + worker.stderr.read()[:1200])
        ready = json.loads(ready_line)
        if ready.get("ready") is not True or ready.get("protocol_version") != 1:
            raise RuntimeError(f"Invalid worker handshake: {ready}")
        ready_ms = (time.perf_counter_ns() - process_started) / 1e6
        results = {}
        for image_key in ("btd", "anvil"):
            target_set = references["images"][image_key]
            excluded_scoring_targets = []
            if image_key == "anvil":
                absent_target_id = "location-alert-action"
                if any(target["id"] == absent_target_id for target in target_set["targets"]):
                    excluded_scoring_targets.append({
                        "id": absent_target_id,
                        "reason": "Reference mark is absent from the clean inference screenshot; cross-frame overlap is not a true hit.",
                    })
                    target_set = {**target_set, "targets": [target for target in target_set["targets"] if target["id"] != absent_target_id]}
            image_path = Path(references["images"][image_key]["input"])
            raw = image_path.read_bytes()
            with Image.open(BytesIO(raw)) as source:
                image = source.convert("RGB")
            width, height = image.size
            if str(image_path) != edge_results["inputs"][image_key]:
                raise ValueError(f"{image_key} path does not match the saved OCR/edge results")
            buffer = BytesIO()
            image.save(buffer, format="JPEG", quality=args.jpeg_quality)
            jpeg = buffer.getvalue()
            frame_id = f"offline-{image_key}-" + hashlib.sha256(jpeg).hexdigest()[:20]
            encoded = base64.b64encode(jpeg).decode("ascii")
            sample_times, warmup_times, signatures = [], [], []
            round_trip_times, warmup_round_trip_times = [], []
            detection_runs, fused_runs = [], []
            detected_count = 0
            first_request_ms = first_round_trip_ms = None
            local_set, local_candidates, template_count, template_note = edge_input(
                edge_results, image_key, frame_id, width, height, args.local_method)
            calibrated_boxes = [candidate["bbox"] for candidate in local_candidates
                                if candidate.get("identity_source") == "bloons_calibrated_pixel_anchor"]
            for request_index in range(args.warmup_runs + args.runs):
                is_warmup = request_index < args.warmup_runs
                request_id = (f"{image_key}-warmup-{request_index + 1}" if is_warmup
                              else f"{image_key}-run-{request_index - args.warmup_runs + 1}")
                request_started = time.perf_counter_ns()
                worker.stdin.write(json.dumps({
                    "request_id": request_id, "frame_id": frame_id, "image_base64": encoded,
                }, separators=(",", ":")) + "\n")
                worker.stdin.flush()
                line = worker.stdout.readline()
                if not line:
                    raise RuntimeError("Worker exited during inference: " + worker.stderr.read()[:1200])
                request_round_trip_ms = (time.perf_counter_ns() - request_started) / 1e6
                response = json.loads(line)
                if response.get("error"):
                    raise RuntimeError(response["error"])
                if (response.get("request_id") != request_id or response.get("frame_id") != frame_id
                        or response.get("image_width") != width or response.get("image_height") != height):
                    raise ValueError("Worker returned a mismatched request, frame, or image geometry")
                detections = response["detections"]
                detected_count = response["detected_count"]
                if is_warmup:
                    warmup_times.append(response["latency"]["total_ms"])
                    warmup_round_trip_times.append(request_round_trip_ms)
                    if first_request_ms is None:
                        first_request_ms = response["latency"]["total_ms"]
                        first_round_trip_ms = request_round_trip_ms
                    continue

                detection_runs.append(detections)
                sample_times.append(response["latency"]["total_ms"])
                round_trip_times.append(request_round_trip_ms)
                signatures.append(json.dumps(detections, sort_keys=True, separators=(",", ":")))
                detector_set = {
                    "frame_id": frame_id, "image_width": width, "image_height": height,
                    "scope": "Visible UI-like regions proposed by resident local YOLOv9-E",
                    "candidate_set_complete": False,
                    "candidate_set_issue": "YOLO proposes visual regions; completeness, semantics, and actionability are unverified.",
                    "candidates": detections,
                }
                filtered_detections = [candidate for candidate in detections if not any(
                    ui_candidate_fuse._iou(candidate["bbox"], template_box) > 0.20
                    for template_box in calibrated_boxes)]
                detector_set["candidates"] = filtered_detections
                fused = ui_candidate_fuse.fuse(
                    detector_set, filtered_detections, local_set, local_candidates,
                    max_detector=args.max_yolo_candidates, max_edge=27, detector_iou_threshold=0.20)
                if template_count:
                    fused["scope"] = "Local YOLO, OCR/edge and calibrated Bloons-layout proposals"
                    fused["candidate_set_issue"] += (
                        f" Includes {template_count} named-layout anchors; coverage remains unverified. {template_note or ''}")
                fused_runs.append(fused)
            fused = fused_runs[-1]
            evaluations = [score_candidates(item["candidates"], target_set, width, height) for item in fused_runs]
            budget_sweep = []
            for yolo_limit in (16, 32, 48, 64, 96, 128, 160, 192, 228):
                budget_fused = ui_candidate_fuse.fuse(
                    detector_set, filtered_detections, local_set, local_candidates,
                    max_detector=yolo_limit, max_edge=27, detector_iou_threshold=0.20)
                budget_evaluation = score_candidates(budget_fused["candidates"], target_set, width, height)
                budget_sweep.append({
                    "yolo_limit": yolo_limit,
                    "candidate_count": len(budget_fused["candidates"]),
                    "matched": budget_evaluation["matched"],
                    "total": budget_evaluation["total"],
                    "missed": budget_evaluation["missed"],
                })
            def geometry_signature(items: list[dict], include_score: bool = False) -> list[tuple]:
                rows = []
                for item in items:
                    box = item["bbox"]
                    row = (box["x"], box["y"], box["width"], box["height"])
                    if include_score: row += (item.get("score", 0),)
                    rows.append(row)
                return sorted(rows)
            matched_id_sets = [tuple(sorted(match["target"] for match in item["matches"])) for item in evaluations]
            results[image_key] = {
                "source_image": str(image_path), "image_dimensions": {"width": width, "height": height},
                "frame_id": frame_id, "jpeg_quality": args.jpeg_quality,
                "detected_count": detected_count, "raw_candidate_count": len(detections),
                "candidate_set_count": len(fused["candidates"]),
                "detector_ms_first_request": first_request_ms,
                "detector_ms_warmup_samples": warmup_times,
                "detector_ms_warm_median": round(statistics.median(sample_times), 2),
                "detector_ms_warm_p95": round(float(np.percentile(sample_times, 95)), 2),
                "detector_ms_samples": sample_times,
                "worker_round_trip_first_request_ms": round(first_round_trip_ms, 2),
                "worker_round_trip_warmup_samples_ms": [round(value, 2) for value in warmup_round_trip_times],
                "worker_round_trip_warm_median_ms": round(statistics.median(round_trip_times), 2),
                "worker_round_trip_warm_p95_ms": round(float(np.percentile(round_trip_times, 95)), 2),
                "worker_round_trip_samples_ms": [round(value, 2) for value in round_trip_times],
                "identical_candidates_each_repeat": len(set(signatures)) == 1,
                "identical_detection_geometry_each_repeat": len({json.dumps(geometry_signature(items)) for items in detection_runs}) == 1,
                "identical_detection_geometry_and_scores_each_repeat": len({json.dumps(geometry_signature(items, include_score=True)) for items in detection_runs}) == 1,
                "identical_fused_geometry_each_repeat": len({json.dumps(geometry_signature(item["candidates"])) for item in fused_runs}) == 1,
                "detected_count_samples": [len(items) for items in detection_runs],
                "fused_count_samples": [len(item["candidates"]) for item in fused_runs],
                "combined_ocr_edge_latency_ms_reference": edge_results["methods"][image_key]["combined"]["latency"],
                "calibrated_template_candidate_count": template_count,
                "calibrated_template_note": template_note,
                "positive_only_evaluation": evaluations[-1],
                "excluded_scoring_targets": excluded_scoring_targets,
                "candidate_budget_sweep": budget_sweep,
                "positive_only_repeat_evaluations": [
                    {"run": index + 1, "matched": item["matched"], "total": item["total"], "missed": item["missed"]}
                    for index, item in enumerate(evaluations)
                ],
                "minimum_positive_matches": min(item["matched"] for item in evaluations),
                "maximum_positive_matches": max(item["matched"] for item in evaluations),
                "same_positive_targets_matched_each_repeat": len(set(matched_id_sets)) == 1,
                "candidate_set_complete": False,
                "candidate_set_issue": fused["candidate_set_issue"],
                "candidate_set": fused,
            }
        return {
            "method": "persistent local YOLOv9-E plus OCR/edge and opportunistic calibrated-layout fusion (Python mirror of C# candidate selection)",
            "image_size": args.image_size,
            "worker_device": ready.get("device"), "worker_ready_ms": round(ready_ms, 2),
            "warmup_runs_per_image": args.warmup_runs,
            "confidence_threshold": 0.001, "detector_iou_threshold": 0.45,
            "secondary_yolo_nms_iou": 0.20, "max_yolo_candidates": args.max_yolo_candidates,
            "local_proposal_method": args.local_method,
            "max_local_proposals": 27, "max_candidate_set": args.max_yolo_candidates + 27,
            "notes": [
                "The local marked-positive reference was read only for scoring and was not sent to any provider.",
                "Anvil marks are approximate transfer references from a different night-state frame.",
                "Extra detections are not scored; this does not establish exhaustive discovery or clickability.",
                "The local OCR/edge and named-layout candidates are reused from saved C# exact-source measurements. The Python mirror prioritizes named-layout anchors and suppresses overlapping YOLO boxes, but does not measure .NET serialization or coordinate mapping.",
                "The source PNGs were re-encoded to JPEG with Pillow quality settings; this approximates, but is not byte-identical to, DCU's System.Drawing JPEG encoder.",
                "Worker round-trip timing includes base64 JSON pipe exchange and inference, but excludes JPEG encoding, game capture, OCR/edge detection, and .NET fusion.",
                "Detector timings exclude process/model startup; fused end-to-end in-application latency was not measured.",
            ],
            "images": results,
        }
    finally:
        try:
            worker.stdin.close()
            worker.wait(timeout=5)
        except Exception:
            try: worker.kill()
            except Exception: pass
        try: worker.stdout.close()
        except Exception: pass
        try: worker.stderr.close()
        except Exception: pass


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--python", type=Path, required=True, help="Python executable with local Torch dependencies")
    parser.add_argument("--weights", type=Path, required=True)
    parser.add_argument("--references", type=Path, required=True, help="Local marked-positive reference JSON; never sent externally")
    parser.add_argument("--edge-results", type=Path, required=True, help="Saved C# exact-source local-results.json")
    parser.add_argument("--device", choices=("auto", "cpu", "cuda"), default="auto")
    parser.add_argument("--image-size", type=int, choices=(640, 960, 1280, 1536), default=1280)
    parser.add_argument("--max-yolo-candidates", type=int, default=160, help="Highest-scored YOLO proposals retained before adding up to 27 local/layout proposals")
    parser.add_argument("--local-method", choices=("edge", "ocr", "combined"), default="edge", help="Saved local proposal set to fuse, matching game_screenshot's default edge-only method")
    parser.add_argument("--runs", type=int, default=5, help="Measured repeats after per-image warmup")
    parser.add_argument("--warmup-runs", type=int, default=2, help="Untimed per-image requests to exclude GPU/model cold effects")
    parser.add_argument("--jpeg-quality", type=int, default=60)
    parser.add_argument("--output", type=Path, required=True, help="New JSON output path; existing files are not overwritten")
    args = parser.parse_args()
    if args.runs < 2: parser.error("runs must be at least 2 to measure warm latency")
    if args.warmup_runs < 1: parser.error("warmup-runs must be at least 1")
    if not 0 <= args.max_yolo_candidates <= 228: parser.error("max-yolo-candidates must be 0-228")
    if not 10 <= args.jpeg_quality <= 95: parser.error("jpeg-quality must be 10-95")
    if args.output.exists(): parser.error(f"output already exists: {args.output}")
    result = run(args)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(result, stream, ensure_ascii=False, indent=2)
    for image_key, data in result["images"].items():
        evaluation = data["positive_only_evaluation"]
        print(f"{image_key}: {evaluation['matched']}/{evaluation['total']} approximate positives; "
              f"count={data['candidate_set_count']}; warm median={data['detector_ms_warm_median']} ms; "
              f"repeat-identical={data['identical_candidates_each_repeat']}", flush=True)
    print(f"saved {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
