"""Offline report-layout/rating tests; no inference, input or provider calls."""
import copy
import hashlib
from html.parser import HTMLParser
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'bench'))
from ui_perception_compare import report_html


class Groups(HTMLParser):
    def __init__(self, document):
        super().__init__()
        self.groups = []
        self.feed(document)

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == 'section' and 'data-method' in attrs:
            self.groups.append({'method': attrs['data-method'], 'rating': attrs['data-rating'], 'images': []})
        if tag == 'article':
            self.groups[-1]['images'].append(attrs['data-image'])


class ReportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.folder = Path(self.temp.name)
        names = {'untested': 'Untested', 'helper': 'Cache helper', 'rejected': 'Rejected', 'poor': 'Poor', 'good': 'Good'}
        records = {k: {'name': v, 'status': 'not run' if k == 'untested' else 'tested'} for k, v in names.items()}
        records['rejected'].update(model='offline-fixture', reasoning_effort='minimal', status='failed / no valid inventory',
                                   runs_completed=1, valid_runs=0, runs_requested=1, providers=[], trials=[])
        self.report = {'method_names': names, 'hardware': 'Offline fixture', 'repeats': 1,
                       'weights_path': 'No model loaded', 'weights_sha256': 'fixture', 'torch_version': 'fixture',
                       'torchvision_version': 'fixture', 'images': {
                           key: {'title': key, 'source': key + '.png', 'dimensions': [10, 10], 'methods': copy.deepcopy(records)}
                           for key in ('btd', 'anvil')}}
        score = lambda n: {'overall': n, 'note': 'Offline fixture rating'}
        self.review = {'methods': {
            'good': {'btd': score(9.0), 'anvil': score(8.0)},
            'poor': {'btd': score(2.0), 'anvil': score(3.0)},
            'helper': {'kind': 'helper', 'btd': score(8.0), 'anvil': score(8.0)}}}

    def render(self):
        self.review['report_sha256'] = hashlib.sha256(json.dumps(self.report, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
        (self.folder / 'comparison-ratings.json').write_text(json.dumps(self.review), encoding='utf-8')
        report_html(self.report, self.folder)
        return (self.folder / 'comparison.html').read_text(encoding='utf-8')

    def test_method_pairs_and_best_first(self):
        groups = Groups(self.render()).groups
        self.assertEqual([g['method'] for g in groups[:3]], ['good', 'poor', 'rejected'])
        self.assertTrue(all(g['images'] == ['btd', 'anvil'] for g in groups))
        self.assertEqual(groups[0]['rating'], '8.5')

    def test_rejected_is_zero_and_untested_is_unrated(self):
        groups = {g['method']: g for g in Groups(self.render()).groups}
        self.assertEqual(groups['rejected']['rating'], '0.0')
        self.assertEqual(groups['untested']['rating'], '')

    def test_helper_is_not_ranked_as_discovery(self):
        document = self.render()
        groups = {g['method']: g for g in Groups(document).groups}
        self.assertEqual(groups['helper']['rating'], '')
        self.assertIn('Helper only — not ranked as discovery', document)

    def test_missing_image_keeps_placeholder_and_no_paired_rating(self):
        del self.report['images']['anvil']['methods']['good']
        del self.review['methods']['good']['anvil']
        groups = {g['method']: g for g in Groups(self.render()).groups}
        self.assertEqual(groups['good']['images'], ['btd', 'anvil'])
        self.assertEqual(groups['good']['rating'], '')

    def test_changed_data_invalidates_old_ratings(self):
        self.render()
        self.report['hardware'] = 'Changed benchmark'
        report_html(self.report, self.folder)
        document = (self.folder / 'comparison.html').read_text(encoding='utf-8')
        self.assertIn('benchmark data changed', document)
        self.assertTrue(all(g['rating'] == '' for g in Groups(document).groups))

    def test_gemini_pair_cost_excludes_earlier_failure(self):
        self.report['openrouter_test'] = {'gemini_current_pair': {
            'btd_cost_usd': 0.00142025, 'anvil_redo_cost_usd': 0.00146475,
            'two_call_total_usd': '0.00288500', 'earlier_anvil_reported_cost_usd': '0.0013245',
            'note': 'Existing BTD plus latest Anvil; earlier failure is separate.'}}
        document = self.render()
        self.assertIn('both: $0.00288500 (0.2885 cents)', document)
        self.assertIn('Earlier failed Anvil call(s): $0.00132450, separate', document)

    def test_render_does_not_mutate_benchmark(self):
        original = copy.deepcopy(self.report)
        self.render()
        self.assertEqual(self.report, original)


if __name__ == '__main__':
    unittest.main()
