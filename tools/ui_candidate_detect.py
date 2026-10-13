#!/usr/bin/env python3
"""Fast local interactive-region proposals using OmniParser's YOLOv9-E detector only.

No OCR, caption model, agent call, or network access is performed. Install torch and
TorchVision in an isolated environment, download the MIT `icon_detect_v3/model.pt`
weight separately, and pass its path with --weights.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import io
import json
import statistics
import sys
import time
from pathlib import Path
from typing import Any

import numpy as np
import torch
from PIL import Image, ImageDraw, ImageFont
from torchvision.ops import nms

IMGSZ = 1280
STRIDES = (8, 16, 32)


def load_model(path: Path, device: torch.device) -> torch.jit.ScriptModule:
    if not path.is_file():
        raise FileNotFoundError(f"Detector weights not found: {path}")
    model = torch.jit.load(str(path), map_location=device).eval()
    return model


def letterbox(image: Image.Image, device: torch.device) -> tuple[torch.Tensor, float, int, int]:
    image = image.convert("RGB")
    width, height = image.size
    scale = min(IMGSZ / width, IMGSZ / height)
    resized = image.resize((round(width * scale), round(height * scale)), Image.Resampling.BILINEAR)
    canvas = Image.new("RGB", (IMGSZ, IMGSZ), (114, 114, 114))
    canvas.paste(resized, (0, 0))
    array = np.asarray(canvas, dtype=np.uint8).copy()
    tensor = torch.from_numpy(array).to(device).permute(2, 0, 1).float().unsqueeze(0).div_(255.0)
    return tensor, scale, width, height


def decode(outputs: Any, scale: float, width: int, height: int, confidence: float, iou: float) -> tuple[list[dict], dict[str, float]]:
    started = time.perf_counter_ns()
    boxes, scores = [], []
    for index, stride in enumerate(STRIDES):
        cls = outputs[2 * index].sigmoid()[0, 0]
        ltrb = outputs[2 * index + 1][0]
        grid = cls.shape[-1]
        gy, gx = torch.meshgrid(
            torch.arange(grid, dtype=torch.float32, device=cls.device),
            torch.arange(grid, dtype=torch.float32, device=cls.device),
            indexing="ij",
        )
        cx, cy = gx + 0.5, gy + 0.5
        left, top, right, bottom = ltrb
        boxes.append(torch.stack(((cx - left) * stride, (cy - top) * stride,
                                  (cx + right) * stride, (cy + bottom) * stride), dim=-1).reshape(-1, 4))
        scores.append(cls.reshape(-1))
    boxes_tensor, scores_tensor = torch.cat(boxes), torch.cat(scores)
    keep = scores_tensor > confidence
    boxes_tensor, scores_tensor = boxes_tensor[keep], scores_tensor[keep]
    if boxes_tensor.numel():
        indices = nms(boxes_tensor, scores_tensor, iou)
        boxes_tensor, scores_tensor = boxes_tensor[indices], scores_tensor[indices]
        boxes_tensor[:, 0::2].div_(scale).clamp_(0, width)
        boxes_tensor[:, 1::2].div_(scale).clamp_(0, height)
        cpu_boxes = boxes_tensor.detach().cpu().tolist()
        cpu_scores = scores_tensor.detach().cpu().tolist()
    else:
        cpu_boxes, cpu_scores = [], []
    elapsed = (time.perf_counter_ns() - started) / 1_000_000
    results = [
        {
            "id": index + 1,
            "key": f"interactive-region-{index + 1:03d}",
            "role": "interactive_region",
            "label": "possible interactive region; semantic identity unknown",
            "bbox": {
                "x": int(round(box[0])),
                "y": int(round(box[1])),
                "width": max(1, int(round(box[2] - box[0]))),
                "height": max(1, int(round(box[3] - box[1]))),
            },
            "score": round(float(score), 4),
            "actionability": "unverified visual proposal",
        }
        for index, (box, score) in enumerate(zip(cpu_boxes, cpu_scores))
    ]
    for item in results:
        box = item["bbox"]
        box["x"] = min(max(0, box["x"]), width - 1)
        box["y"] = min(max(0, box["y"]), height - 1)
        box["width"] = min(box["width"], width - box["x"])
        box["height"] = min(box["height"], height - box["y"])
    return results, {"postprocess_ms": elapsed}


def detect_once(model: torch.jit.ScriptModule, image: Image.Image, device: torch.device,
                confidence: float, iou: float) -> tuple[list[dict], dict[str, float]]:
    if device.type == "cuda":
        torch.cuda.synchronize(device)
    start = time.perf_counter_ns()
    tensor, scale, width, height = letterbox(image, device)
    if device.type == "cuda":
        torch.cuda.synchronize(device)
    prepared = time.perf_counter_ns()
    with torch.inference_mode():
        outputs = model(tensor)
    if device.type == "cuda":
        torch.cuda.synchronize(device)
    inferred = time.perf_counter_ns()
    detections, _ = decode(outputs, scale, width, height, confidence, iou)
    if device.type == "cuda":
        torch.cuda.synchronize(device)
    done = time.perf_counter_ns()
    return detections, {
        "preprocess_ms": (prepared - start) / 1e6,
        "inference_ms": (inferred - prepared) / 1e6,
        "postprocess_ms": (done - inferred) / 1e6,
        "total_ms": (done - start) / 1e6,
    }


def detect(model: torch.jit.ScriptModule, image: Image.Image, device: torch.device,
           confidence: float, iou: float, repeats: int) -> tuple[list[dict], dict[str, float]]:
    # Warm all stages, including decoding/NMS. Never mix cold stage costs with warm forward time.
    _, cold = detect_once(model, image, device, confidence, iou)
    for _ in range(4):
        detect_once(model, image, device, confidence, iou)
    samples = [detect_once(model, image, device, confidence, iou) for _ in range(max(1, repeats))]
    detections = samples[-1][0]
    timings = [
        [row[1]["preprocess_ms"], row[1]["inference_ms"], row[1]["postprocess_ms"], row[1]["total_ms"]]
        for row in samples
    ]
    return detections, {
        "preprocess_ms": round(statistics.median(row[0] for row in timings), 2),
        "inference_ms_median": round(statistics.median(row[1] for row in timings), 2),
        "postprocess_ms": round(statistics.median(row[2] for row in timings), 2),
        "total_ms_median": round(statistics.median(row[3] for row in timings), 2),
        "total_ms_p95": round(float(np.percentile([row[3] for row in timings], 95)), 2),
        "cold_first_pipeline_ms": cold["total_ms"],
        "samples_total_ms": [row[3] for row in timings],
        "timed_repeats": max(1, repeats),
        "warmup_runs": 5,
        "scope": "resident RGB image to boxes; excludes disk decode, model loading, startup and annotation",
    }


def annotate(image: Image.Image, detections: list[dict], output: Path, method: str,
             latency: dict[str, float], confidence: float, iou: float) -> None:
    canvas = image.convert("RGB")
    draw = ImageDraw.Draw(canvas)
    try:
        font = ImageFont.truetype("arial.ttf", max(14, canvas.width // 110))
    except OSError:
        font = ImageFont.load_default()
    for item in detections:
        box = item["bbox"]
        x0, y0 = box["x"], box["y"]
        x1, y1 = x0 + box["width"], y0 + box["height"]
        draw.rectangle((x0, y0, x1, y1), outline=(255, 64, 64), width=max(2, canvas.width // 900))
        label = f"{item['id']} {item['score']:.2f}"
        text_box = draw.textbbox((x0, max(0, y0 - 24)), label, font=font)
        draw.rectangle(text_box, fill=(20, 20, 20))
        draw.text((x0, max(0, y0 - 24)), label, font=font, fill=(255, 255, 255))
    header = (f"OmniParser YOLOv9-E detector-only | {len(detections)} boxes | "
              f"warm total={latency['total_ms_median']:.1f}ms | conf={confidence:.2f} IoU={iou:.2f}")
    draw.rectangle((0, 0, canvas.width, max(32, canvas.height // 22)), fill=(12, 17, 23))
    draw.text((10, 7), header, font=font, fill=(255, 255, 255))
    canvas.save(output)


def run_worker(args: argparse.Namespace, device: torch.device) -> int:
    torch.set_num_threads(2)
    model = load_model(args.weights, device)
    print(json.dumps({"ready": True, "protocol_version": 1, "device": str(device)}), flush=True)

    for line in sys.stdin:
        request_id: str | None = None
        try:
            request = json.loads(line)
            request_id = request.get("request_id")
            if not isinstance(request_id, str) or not request_id:
                raise ValueError("request_id must be a non-empty string")
            frame_id = request.get("frame_id")
            image_data = request.get("image_base64")
            if not isinstance(frame_id, str) or not frame_id or not isinstance(image_data, str):
                raise ValueError("frame_id and image_base64 are required")
            image_bytes = base64.b64decode(image_data, validate=True)
            with Image.open(io.BytesIO(image_bytes)) as source:
                image = source.convert("RGB")
            detections, latency = detect_once(model, image, device, args.confidence, args.iou)
            count = len(detections)
            detections = detections[:args.max_raw_detections]
            print(json.dumps({
                "request_id": request_id,
                "frame_id": frame_id,
                "image_width": image.width,
                "image_height": image.height,
                "detected_count": count,
                "detections_truncated": count > len(detections),
                "detections": detections,
                "latency": {key: round(value, 2) for key, value in latency.items()},
            }, separators=(",", ":")), flush=True)
        except Exception as error:
            print(json.dumps({
                "request_id": request_id,
                "error": f"{type(error).__name__}: {error}"[:500],
            }, separators=(",", ":")), flush=True)
    return 0


def main() -> int:
    global IMGSZ
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image", type=Path, nargs="?")
    parser.add_argument("--serve", action="store_true", help="Keep one model loaded and process JSON-lines image requests on stdin")
    parser.add_argument("--weights", type=Path, required=True, help="Local icon_detect_v3/model.pt TorchScript weight")
    parser.add_argument("--output-image", type=Path)
    parser.add_argument("--output-json", type=Path)
    parser.add_argument("--frame-id", help="Frame id to bind to (for example the game_screenshot frame_id); defaults to a hash of the input bytes")
    parser.add_argument("--scope", default="Visible UI-like regions proposed by OmniParser YOLOv9-E")
    parser.add_argument("--max-candidates", type=int, default=255, help="Maximum returned proposals, 1-255")
    parser.add_argument("--device", choices=("auto", "cpu", "cuda"), default="auto")
    parser.add_argument("--image-size", type=int, choices=(640, 960, 1280, 1536), default=1280,
                        help="Square detector input resolution; larger sizes improve small-target recall at higher latency")
    parser.add_argument("--confidence", type=float, default=0.05)
    parser.add_argument("--iou", type=float, default=0.45)
    parser.add_argument("--repeats", type=int, default=5)
    parser.add_argument("--max-raw-detections", type=int, default=1024, help="Maximum raw proposals per persistent-worker response")
    args = parser.parse_args()
    if args.serve:
        if args.image is not None or args.output_image is not None or args.output_json is not None:
            parser.error("--serve cannot be combined with image or output-file arguments")
        if not 1 <= args.max_raw_detections <= 4096:
            parser.error("max-raw-detections must be between 1 and 4096")
    elif args.image is None or not args.image.is_file():
        parser.error(f"image not found: {args.image}")
    if not 0 < args.confidence < 1 or not 0 < args.iou < 1 or args.repeats < 1:
        parser.error("confidence/iou must be in (0,1) and repeats >= 1")
    if not 1 <= args.max_candidates <= 255:
        parser.error("max-candidates must be between 1 and 255")
    if not args.scope.strip() or len(args.scope) > 500:
        parser.error("scope must contain 1-500 non-whitespace characters")
    if args.frame_id is not None and (not args.frame_id.strip() or len(args.frame_id) > 200):
        parser.error("frame-id must contain 1-200 non-whitespace characters")
    IMGSZ = args.image_size
    device = torch.device("cuda" if args.device == "auto" and torch.cuda.is_available() else "cpu" if args.device == "auto" else args.device)
    if device.type == "cuda" and not torch.cuda.is_available():
        parser.error("CUDA requested but unavailable")
    if args.serve:
        return run_worker(args, device)
    torch.set_num_threads(2)
    model = load_model(args.weights, device)
    image_bytes = args.image.read_bytes()
    image = Image.open(args.image).convert("RGB")
    frame_id = args.frame_id or "image-" + hashlib.sha256(image_bytes).hexdigest()[:20].lower()
    detections, latency = detect(model, image, device, args.confidence, args.iou, args.repeats)
    detected_count = len(detections)
    candidate_set_detections = detections[:args.max_candidates]
    issue = "YOLOv9-E visual-region proposals; interactivity, semantic identity, recall, and completeness are unverified."
    if detected_count > len(candidate_set_detections):
        issue += f" Candidate set contains only the highest-ranked {len(candidate_set_detections)} of {detected_count} detections."
    candidate_set = {
        "frame_id": frame_id,
        "image_width": image.width,
        "image_height": image.height,
        "scope": args.scope,
        "candidate_set_complete": False,
        "candidate_set_issue": issue,
        "observed_text": [],
        "candidates": [
            {"key": item["key"], "role": item["role"], "label": item["label"],
             "identity_source": "omniparser_icon_detect_v3_yolov9e", "bbox": item["bbox"]}
            for item in candidate_set_detections
        ],
    }
    result = {
        "method": "OmniParser icon_detect_v3 YOLOv9-E detector-only",
        "source_image": str(args.image),
        "frame_id": frame_id,
        "scope": args.scope,
        "image_dimensions": {"width": image.width, "height": image.height},
        "device": str(device),
        "model_license": "MIT (Microsoft icon_detect_v3/LICENSE)",
        "confidence_threshold": args.confidence,
        "iou_threshold": args.iou,
        "candidate_set_complete": False,
        "candidate_set_issue": issue,
        "latency": latency,
        "detected_count": detected_count,
        "candidate_set_count": len(candidate_set_detections),
        "candidate_count": detected_count,
        "candidates": detections,
        "candidate_set_json": json.dumps(candidate_set, separators=(",", ":")),
    }
    if args.output_json:
        args.output_json.parent.mkdir(parents=True, exist_ok=True)
        args.output_json.write_text(json.dumps(result, indent=2), encoding="utf-8")
    if args.output_image:
        args.output_image.parent.mkdir(parents=True, exist_ok=True)
        annotate(image, detections, args.output_image, result["method"], latency, args.confidence, args.iou)
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
