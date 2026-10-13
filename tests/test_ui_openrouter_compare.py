"""Offline contract tests; no credentials read and no OpenRouter requests made."""
import json
from pathlib import Path
import sys
import tempfile
import unittest
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'bench'))
from ui_openrouter_compare import payload_for, parse_inventory, NoRedirect
from ui_openrouter_normalized_compare import parse_normalized


class OpenRouterComparisonContractTests(unittest.TestCase):
    def response(self, **changes):
        inventory={'frame_id':'f','image_width':100,'image_height':80,
                   'candidate_set_complete':False,'candidate_set_issue':'coverage unknown',
                   'candidates':[{'key':'a','role':'icon','label':'unknown icon',
                                  'bbox':{'x':10,'y':10,'width':20,'height':20}}]}
        inventory.update(changes)
        return {'choices':[{'finish_reason':'stop','message':{'content':json.dumps(inventory)}}]}

    def test_valid_inventory_is_bound_and_does_not_establish_clickability(self):
        parsed,boxes=parse_inventory(self.response(),'f',100,80)
        self.assertFalse(parsed['candidate_set_complete'])
        self.assertEqual(len(boxes),1)
        self.assertEqual(boxes[0]['bbox']['x'],10)

    def test_wrong_frame_is_rejected(self):
        with self.assertRaisesRegex(ValueError,'frame identity'):
            parse_inventory(self.response(frame_id='other'),'f',100,80)

    def test_out_of_bounds_is_rejected_not_rescaled(self):
        item={'key':'a','role':'icon','label':'x','bbox':{'x':95,'y':0,'width':20,'height':20}}
        with self.assertRaisesRegex(ValueError,'Out-of-bounds'):
            parse_inventory(self.response(candidates=[item]),'f',100,80)

    def test_duplicate_key_is_rejected(self):
        item={'key':'a','role':'icon','label':'x','bbox':{'x':0,'y':0,'width':20,'height':20}}
        with self.assertRaisesRegex(ValueError,'Duplicate'):
            parse_inventory(self.response(candidates=[item,item]),'f',100,80)

    def test_truncated_output_is_rejected_even_if_content_is_json(self):
        r=self.response();r['choices'][0]['finish_reason']='length'
        with self.assertRaisesRegex(ValueError,'truncated'):
            parse_inventory(r,'f',100,80)

    def test_payload_uses_image_format_and_thinking_privacy_settings(self):
        with tempfile.TemporaryDirectory() as d:
            # File extension is intentionally misleading; MIME must follow the actual codec.
            path=Path(d)/'fixture.png';Image.new('RGB',(16,10)).save(path,format='JPEG')
            payload,frame,width,height=payload_for({'model':'test','reasoning_effort':'minimal'},path)
        self.assertEqual((width,height),(16,10))
        self.assertTrue(frame.startswith('image-'))
        self.assertEqual(payload['reasoning'],{'effort':'minimal','exclude':True})
        self.assertEqual(payload['provider']['data_collection'],'deny')
        self.assertFalse(payload['provider']['allow_fallbacks'])
        self.assertEqual(payload['max_tokens'],6144)
        self.assertTrue(payload['messages'][1]['content'][1]['image_url']['url'].startswith('data:image/jpeg;base64,'))
        self.assertFalse('tools' in payload)

    def test_redirect_never_forwards_request(self):
        self.assertIsNone(NoRedirect().redirect_request(None,None,302,'redirect',{},'https://elsewhere.invalid'))

    def normalized_response(self, space, coords):
        content={'frame_id':'f','coordinate_space':space,'candidates':[{'label':'unknown icon','box_2d':coords}]}
        return {'choices':[{'finish_reason':'stop','message':{'content':json.dumps(content)}}]}

    def test_declared_normalized_yxyx_maps_to_pixels(self):
        r=self.normalized_response('normalized_1000_yxyx',[100,200,900,800])
        boxes=parse_normalized(r,'f',100,80,'normalized_1000_yxyx')
        self.assertEqual(boxes[0]['bbox'],{'x':20,'y':8,'width':60,'height':64})
        self.assertEqual(boxes[0]['source_normalized_box'],[100,200,900,800])

    def test_declared_normalized_xyxy_maps_to_same_pixels(self):
        r=self.normalized_response('normalized_1000_xyxy',[200,100,800,900])
        boxes=parse_normalized(r,'f',100,80,'normalized_1000_xyxy')
        self.assertEqual(boxes[0]['bbox'],{'x':20,'y':8,'width':60,'height':64})

    def test_mismatched_coordinate_convention_is_not_guessed(self):
        r=self.normalized_response('normalized_1000_xyxy',[200,100,800,900])
        with self.assertRaisesRegex(ValueError,'mismatched coordinate'):
            parse_normalized(r,'f',100,80,'normalized_1000_yxyx')

    def test_normalized_out_of_range_and_nonpositive_are_rejected(self):
        for coords in ([0,0,1001,1000],[500,500,400,400]):
            with self.assertRaises(ValueError):
                parse_normalized(self.normalized_response('normalized_1000_yxyx',coords),'f',100,80,'normalized_1000_yxyx')


if __name__=='__main__':unittest.main()
