#!/usr/bin/env python3
"""Summarize current, complete CPU recordings; never label a truncated prefix final."""
import csv
import json
from pathlib import Path
import statistics
import sys


def sample_summary(rows):
    if not rows:
        return None
    ms = sorted(r['cpu_frame_ms'] for r in rows)
    return dict(frames=len(rows), drawn_frames=sum(r['drawn'] != 0 for r in rows),
                mean_ms=statistics.mean(ms), p95_ms=ms[int((len(ms)-1)*.95)],
                p99_ms=ms[int((len(ms)-1)*.99)], max_ms=ms[-1],
                render_bytes=sum(r['render_bytes'] for r in rows),
                counts={k: dict(min=min(r[k] for r in rows), max=max(r[k] for r in rows))
                        for k in rows[0] if k not in ('seconds', 'cpu_frame_ms', 'render_bytes')})


def summarize(root):
    manifest_path = root / 'run.json'
    manifest = json.loads(manifest_path.read_text()) if manifest_path.exists() else {}
    names = set(manifest.get('scenes', []))
    names.update(p.name.removesuffix('.status.json') for p in root.glob('*.status.json'))
    names.update(p.stem for p in root.glob('*.csv'))
    results = {}
    for name in sorted(names):
        result = dict(state='missing_status', final_five_seconds=None)
        results[name] = result
        status_path = root / (name + '.status.json')
        if not status_path.exists():
            continue
        try:
            status = json.loads(status_path.read_text())
            result['run_status'] = status
            if manifest and (name not in manifest['scenes'] or status.get('run_id') != manifest['run_id']):
                result['state'] = 'not_current_run'
                continue
            if status.get('state') != 'finished' or status.get('exit_code') != 0:
                result['state'] = 'timeout' if status.get('timed_out') else 'failed_or_unfinished'
                continue
            path = root / (name + '.csv')
            metadata = Path(str(path) + '.meta.json')
            if not path.exists() or not metadata.exists():
                result['state'] = 'missing_output'
                continue
            meta = json.loads(metadata.read_text())
            result['recording'] = meta
            with path.open() as source:
                rows = [{k: float(v) for k, v in row.items()} for row in csv.DictReader(source)]
            if not rows or 'drawn' not in rows[0]:
                result['state'] = 'missing_samples_or_drawn_flag'
                continue
            result['recorded_prefix'] = sample_summary(rows)
            complete = (meta.get('complete') is True and meta.get('overflow') == 0
                        and meta.get('samples') == len(rows) and abs(rows[-1]['seconds'] - meta['end_seconds']) < .001)
            if not complete or not meta.get('verified'):
                result['state'] = 'incomplete_or_unverified'
                continue
            final = meta['end_seconds']
            expected = int(name.split('-')[0])-1
            result.update(state='complete', end_seconds=final,
                          first_full_damage_draw_seconds=next((r['seconds'] for r in rows if r['drawn'] and r['damage_draws'] == expected), None),
                          cold_first_two_seconds=sample_summary([r for r in rows if r['seconds'] < 2]),
                          final_five_seconds=sample_summary([r for r in rows if r['seconds'] >= final-5]))
        except (OSError, ValueError, KeyError, TypeError) as error:
            result.update(state='invalid_output', error=str(error), final_five_seconds=None)
    return results


def main():
    root = Path(sys.argv[1] if len(sys.argv) > 1 else '/tmp/prospect-populated')
    results = summarize(root)
    (root / 'summary.json').write_text(json.dumps(results, indent=2) + '\n')
    for name, result in results.items():
        warm = result['final_five_seconds']
        print(name, result['state'], warm)
    return 0 if results and all(r['state'] == 'complete' for r in results.values()) else 1


if __name__ == '__main__':
    raise SystemExit(main())
