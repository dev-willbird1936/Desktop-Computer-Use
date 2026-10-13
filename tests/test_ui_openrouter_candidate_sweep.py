"""Offline marked-positive target evaluator checks; no provider calls."""
import importlib.util
import json
import sys
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT/'bench'))
SPEC = importlib.util.spec_from_file_location('candidate_sweep', ROOT/'bench/ui_openrouter_candidate_sweep.py')
SWEEP = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SWEEP)
import ui_openrouter_candidate_sweep_resume as RESUME
REF = json.loads(Path(r'C:/Users/warcr/AppData/Local/Temp/ui-perception-offline-compare-r1/marked-positive-target-reference.json').read_text())


class CandidateSweepTests(unittest.TestCase):
    def test_reference_counts_and_sources(self):
        self.assertEqual(len(REF['images']['btd']['targets']), 21)
        self.assertEqual(len([t for t in REF['images']['btd']['targets'] if t['type']=='card']), 16)
        self.assertEqual(len(REF['images']['anvil']['targets']), 8)
        self.assertIn('same-frame ground truth', REF['images']['anvil']['reference_transfer'])

    def test_all_btd_target_boxes_are_inside_screenshot(self):
        info=REF['images']['btd']
        for target in info['targets']:
            b=target['bbox']
            self.assertGreater(b['width'],0);self.assertGreater(b['height'],0)
            self.assertGreaterEqual(b['x'],0);self.assertGreaterEqual(b['y'],0)
            self.assertLessEqual(b['x']+b['width'],info['width'])
            self.assertLessEqual(b['y']+b['height'],info['height'])

    def test_complete_btd_cards_require_distinct_iou_matches(self):
        info=REF['images']['btd']
        boxes=[]
        for target in info['targets']:
            if target['type']=='card':
                b=target['bbox'];boxes.append({'key':target['id'],'label':'card','bbox_norm':[b['x']/info['width']*1000,b['y']/info['height']*1000,(b['x']+b['width'])/info['width']*1000,(b['y']+b['height'])/info['height']*1000]})
        result=SWEEP.evaluate(boxes,info,'btd')
        self.assertEqual((result['matched'],result['total']),(16,21))
        self.assertEqual(len({m['candidate'] for m in result['matches']}),16)

    def test_exact_btd_reference_boxes_find_all_21_targets(self):
        info=REF['images']['btd'];boxes=[]
        for target in info['targets']:
            b=target['bbox'];boxes.append({'key':target['id'],'label':'reference','bbox_norm':[b['x']/info['width']*1000,b['y']/info['height']*1000,(b['x']+b['width'])/info['width']*1000,(b['y']+b['height'])/info['height']*1000]})
        result=SWEEP.evaluate(boxes,info,'btd')
        self.assertEqual((result['matched'],result['total']),(21,21))

    def test_extras_are_not_penalized_but_do_not_replace_omissions(self):
        info=REF['images']['btd'];targets=[t for t in info['targets'] if t['type']=='card']
        boxes=[]
        for target in targets:
            b=target['bbox'];boxes.append({'key':target['id'],'label':'card','bbox_norm':[b['x']/info['width']*1000,b['y']/info['height']*1000,(b['x']+b['width'])/info['width']*1000,(b['y']+b['height'])/info['height']*1000]})
        boxes.append({'key':'unmarked-extra','label':'possible control','bbox_norm':[0,0,20,20]})
        result=SWEEP.evaluate(boxes,info,'btd')
        self.assertEqual(result['matched'],16)
        self.assertEqual(result['total'],21)
        self.assertFalse(result['false_positives_scored'])
        self.assertEqual(set(result['missed']),{'preview-left-arrow','preview-right-arrow','heroes-next-arrow','cancel','ready'})

    def test_anvil_anchor_transfer_is_normalized(self):
        info=REF['images']['anvil'];target=info['targets'][0];b=target['reference_bbox']
        cx=(b['x']+b['width']/2)/info['reference_width']*1000
        cy=(b['y']+b['height']/2)/info['reference_height']*1000
        candidate={'key':'popup','label':'notification action','bbox_norm':[cx-1,cy-1,cx+1,cy+1]}
        result=SWEEP.evaluate([candidate],info,'anvil')
        self.assertEqual(result['matched'],1)
        self.assertEqual(result['missed'],[t['id'] for t in info['targets'][1:]])

    def test_large_broad_box_does_not_claim_all_anvil_targets(self):
        result=SWEEP.evaluate([{'key':'whole-screen','label':'screen','bbox_norm':[0,0,1000,1000]}],REF['images']['anvil'],'anvil')
        self.assertEqual(result['matched'],0)

    def test_prompt_contains_no_reference_target_counts(self):
        prompt=SWEEP.prompt_for('test-frame',2559,1439)
        self.assertIn('normalized_1000_xyxy',prompt)
        self.assertNotIn('21',prompt)
        self.assertNotIn('16',prompt)

    def test_resume_request_cap_is_strictly_within_remaining_budget(self):
        config=SWEEP.CONFIGS[-1]
        prefs=RESUME.provider_preferences(config,0.19790334)
        self.assertLessEqual(prefs['max_price']['request'],0.19790334)
        self.assertEqual(prefs['max_price']['prompt'],config['in']*1_000_000)
        self.assertEqual(prefs['max_price']['completion'],config['out']*1_000_000)
        self.assertFalse(prefs['allow_fallbacks'])
        self.assertEqual(prefs['data_collection'],'deny')

    def test_resume_request_cap_is_zero_when_budget_is_exhausted(self):
        self.assertEqual(RESUME.provider_preferences(SWEEP.CONFIGS[0],0)['max_price']['request'],0)

    def test_baseline_model_order_is_cheapest_first_by_reference_rates(self):
        baseline=SWEEP.CONFIGS[:9]
        costs=[1600*c['in']+800*c['out'] for c in baseline]
        self.assertEqual(costs,sorted(costs))
        self.assertEqual([c['think'] for c in SWEEP.CONFIGS[9:]], [
            'provider default (reasoning enabled)','provider default','low','low'])


if __name__ == '__main__':
    unittest.main()
