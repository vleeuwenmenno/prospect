"""No-GPU regression fixtures for current-run provenance and incomplete recordings."""
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


runner = load('populated_runner', 'populated-benchmark.py')
summary = load('populated_summary', 'summarize-populated.py')


class OutputTests(unittest.TestCase):
    def test_success_then_failed_rerun_cannot_reuse_old_csv(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            name = '30-clean-near-small-default'
            script = '''import os,json,pathlib
p=pathlib.Path(os.environ['PROSPECT_VISUAL_REPORT'])
p.write_text('seconds,cpu_frame_ms,render_bytes,damage_draws,spark_vertices,drawn\\n11,999,7,0,0,1\\n')
pathlib.Path(str(p)+'.meta.json').write_text(json.dumps(dict(samples=1,overflow=0,end_seconds=11,complete=True,verified=True)))
'''
            runner.run_scene(root, name, [sys.executable, '-c', script], os.environ, run_id='first')
            first = summary.summarize(root)[name]
            self.assertEqual(first['state'], 'complete')
            self.assertEqual(first['final_five_seconds']['max_ms'], 999)
            runner.run_scene(root, name, [sys.executable, '-c', 'raise SystemExit(7)'], os.environ, run_id='second')
            result = summary.summarize(root)[name]
            self.assertEqual(result['state'], 'failed_or_unfinished')
            self.assertEqual(result['run_status']['exit_code'], 7)
            self.assertIsNone(result['final_five_seconds'])
            self.assertFalse((root / (name+'.csv')).exists())
            self.assertFalse((root / (name+'.csv.meta.json')).exists())
            timed = runner.run_scene(root, name, [sys.executable, '-c', 'import time;time.sleep(5)'], os.environ, timeout=.05)
            self.assertTrue(timed['timed_out'])
            self.assertEqual(summary.summarize(root)[name]['state'], 'timeout')

    def test_overflow_prefix_never_becomes_final_and_missing_scene_is_reported(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            name = '30-dents-near-small-default'
            (root/'run.json').write_text(json.dumps(dict(run_id='current', scenes=[name, '50-clean-near-small-default'])))
            (root/(name+'.status.json')).write_text(json.dumps(dict(run_id='current',state='finished',exit_code=0)))
            (root/(name+'.csv')).write_text('seconds,cpu_frame_ms,render_bytes,damage_draws,spark_vertices,drawn\n1,1,0,29,0,1\n80,5000,10,0,0,0\n')
            (root/(name+'.csv.meta.json')).write_text(json.dumps(dict(samples=2,overflow=90000,end_seconds=120,complete=False,verified=False)))
            result = summary.summarize(root)
            self.assertEqual(result[name]['state'], 'incomplete_or_unverified')
            self.assertIsNone(result[name]['final_five_seconds'])
            self.assertEqual(result[name]['recorded_prefix']['max_ms'], 5000)
            self.assertEqual(result[name]['recorded_prefix']['drawn_frames'], 1)
            self.assertEqual(result['50-clean-near-small-default']['state'], 'missing_status')
            (root/(name+'.status.json')).write_text(json.dumps(dict(run_id='old',state='finished',exit_code=0)))
            self.assertEqual(summary.summarize(root)[name]['state'], 'not_current_run')


if __name__ == '__main__':
    unittest.main()
