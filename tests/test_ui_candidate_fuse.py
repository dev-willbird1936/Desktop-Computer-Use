import json
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import ui_candidate_fuse as fuse


class FuseCandidateTests(unittest.TestCase):
    def setUp(self):
        self.detector = {
            "frame_id": "frame-a", "image_width": 800, "image_height": 600,
            "candidate_set_complete": False, "candidate_set_issue": "detector proposals are incomplete",
            "candidates": [
                {"key": "d1", "role": "region", "label": "high", "score": .9,
                 "bbox": {"x": 10, "y": 20, "width": 30, "height": 40}},
                {"key": "d2", "role": "region", "label": "low", "score": .1,
                 "bbox": {"x": 50, "y": 60, "width": 30, "height": 40}},
            ],
        }
        edge_set = {
            "frame_id": "frame-a", "image_width": 800, "image_height": 600,
            "candidate_set_complete": False, "candidate_set_issue": "edge proposals are incomplete",
            "observed_text": ["Chat"],
            "candidates": [
                {"key": "e1", "role": "region", "label": "edge high", "proposal_score": .8,
                 "bbox": {"x": 100, "y": 120, "width": 20, "height": 24}},
                {"key": "e2", "role": "region", "label": "edge low", "proposal_score": .2,
                 "bbox": {"x": 200, "y": 220, "width": 20, "height": 24}},
            ],
        }
        self.edge_root = {"ui_observation": {"candidate_set_json": json.dumps(edge_set)}}

    def test_selects_ranked_proposals_and_emits_incomplete_frame_bound_set(self):
        result = fuse.fuse(self.detector, self.detector["candidates"],
                           json.loads(self.edge_root["ui_observation"]["candidate_set_json"]),
                           self.edge_set_candidates(), max_detector=1, max_edge=1)
        self.assertEqual(result["frame_id"], "frame-a")
        self.assertFalse(result["candidate_set_complete"])
        self.assertEqual(2, len(result["candidates"]))
        self.assertEqual(["yolo:d1", "edge:e1"], [item["key"] for item in result["candidates"]])
        self.assertEqual(["Chat"], result["observed_text"])

    def test_attaches_adjacent_same_row_ocr_as_an_unverified_clue(self):
        detector = {
            "frame_id": "frame-a", "image_width": 1280, "image_height": 600,
            "candidates": [
                {"key": "toggle", "role": "region", "label": "generic control", "score": .9,
                 "bbox": {"x": 600, "y": 100, "width": 50, "height": 44}},
                {"key": "wide-panel", "role": "region", "label": "broad panel", "score": .8,
                 "bbox": {"x": 600, "y": 100, "width": 300, "height": 44}},
                {"key": "left-item", "role": "region", "label": "other control", "score": .7,
                 "bbox": {"x": 10, "y": 100, "width": 50, "height": 44}},
                {"key": "far-right", "role": "region", "label": "farther control", "score": .6,
                 "bbox": {"x": 900, "y": 100, "width": 50, "height": 44}},
            ],
        }
        edge = {
            "frame_id": "frame-a", "image_width": 1280, "image_height": 600,
            "observed_text": ["Automatic updates"],
            "candidates": [{"key": "ocr-auto-updates", "role": "text_region", "label": "Automatic updates",
                            "identity_source": "windows_media_ocr", "bbox": {"x": 100, "y": 105, "width": 160, "height": 20}}],
        }

        result = fuse.fuse(detector, detector["candidates"], edge, edge["candidates"], max_detector=4, max_edge=1)

        self.assertIn("nearby unverified OCR text: Automatic updates", result["candidates"][0]["label"])
        self.assertEqual("yolo+windows_media_ocr", result["candidates"][0]["identity_source"])
        self.assertNotIn("Automatic updates", result["candidates"][1]["label"])
        self.assertNotIn("Automatic updates", result["candidates"][2]["label"])
        self.assertNotIn("Automatic updates", result["candidates"][3]["label"])
        self.assertEqual("edge:ocr-auto-updates", result["candidates"][4]["key"])

    def test_secondary_nms_removes_detector_duplicates_before_budget(self):
        edge = json.loads(self.edge_root["ui_observation"]["candidate_set_json"])
        detector_candidates = [
            self.detector["candidates"][0],
            {"key": "d1-near-duplicate", "role": "region", "label": "same control", "score": .8,
             "bbox": {"x": 11, "y": 21, "width": 30, "height": 40}},
            self.detector["candidates"][1],
        ]
        result = fuse.fuse(self.detector, detector_candidates, edge, [],
                           max_detector=10, max_edge=0, detector_iou_threshold=.2)
        self.assertEqual(["yolo:d1", "yolo:d2"], [item["key"] for item in result["candidates"]])

    def test_loads_candidate_set_from_mcp_text_payload(self):
        payload = {"ui_observation": {"candidate_set_json": self.edge_root["ui_observation"]["candidate_set_json"]}}
        envelope = {"content": [{"type": "text", "text": json.dumps(payload)}, {"type": "image", "data": "omitted"}]}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "screenshot-result.json"
            path.write_text(json.dumps(envelope), encoding="utf-8")
            candidate_set, candidates = fuse.load_input(path)
        self.assertEqual("frame-a", candidate_set["frame_id"])
        self.assertEqual(2, len(candidates))

    def test_refuses_mismatched_frame_and_geometry(self):
        edge = json.loads(self.edge_root["ui_observation"]["candidate_set_json"])
        edge["frame_id"] = "different-frame"
        with self.assertRaisesRegex(ValueError, "frame_id"):
            fuse.fuse(self.detector, self.detector["candidates"], edge, self.edge_set_candidates(), 1, 1)
        edge["frame_id"] = "frame-a"
        edge["image_width"] = 801
        with self.assertRaisesRegex(ValueError, "image_width"):
            fuse.fuse(self.detector, self.detector["candidates"], edge, self.edge_set_candidates(), 1, 1)

    def test_refuses_invalid_boxes_and_over_limit(self):
        edge_set = json.loads(self.edge_root["ui_observation"]["candidate_set_json"])
        edge = edge_set["candidates"]
        bad = [dict(self.detector["candidates"][0], bbox={"x": 790, "y": 0, "width": 20, "height": 2})]
        with self.assertRaisesRegex(ValueError, "outside"):
            fuse.fuse(self.detector, bad, edge_set, edge, 1, 1)
        with self.assertRaisesRegex(ValueError, "at most 255"):
            fuse.fuse(self.detector, [], edge_set, edge, 200, 56)

    def edge_set_candidates(self):
        return json.loads(self.edge_root["ui_observation"]["candidate_set_json"])["candidates"]


if __name__ == "__main__":
    unittest.main()
