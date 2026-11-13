#!/usr/bin/env python3
"""Run bounded, sequential private scenes with explicit current-run provenance."""
import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import time
import uuid


def run_scene(output, name, command, env, timeout=90, run_id=None):
    output.mkdir(parents=True, exist_ok=True)
    csv = output / (name + '.csv')
    csv.unlink(missing_ok=True)
    Path(str(csv) + '.meta.json').unlink(missing_ok=True)
    status_path = output / (name + '.status.json')
    status = dict(run_id=run_id or uuid.uuid4().hex, state='running', started=time.time(),
                  exit_code=None, timed_out=False, command=command)
    status_path.write_text(json.dumps(status, indent=2))
    child_env = dict(env, PROSPECT_VISUAL_REPORT=str(csv.resolve()))
    with (output / (name + '.log')).open('w') as log:
        try:
            process = subprocess.Popen(command, env=child_env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            try:
                status['exit_code'] = process.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()
                status.update(exit_code=124, timed_out=True)
        except OSError as error:
            status.update(exit_code=127, error=str(error))
    status.update(state='finished', ended=time.time(), csv_present=csv.exists())
    status_path.write_text(json.dumps(status, indent=2))
    return status


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, default=Path('/tmp/prospect-populated'))
    parser.add_argument('--dense-only', action='store_true')
    parser.add_argument('--seconds', type=int, default=32)
    args = parser.parse_args()
    if not 28 <= args.seconds <= 60:
        parser.error('use 28..60 seconds so cosmetic recovery can settle')
    args.output.mkdir(parents=True, exist_ok=True)
    scenes = [(n, a, 'near', size, False) for n in (30, 50) for size in ('small', 'large') for a in ('clean', 'dents')]
    scenes += [(50, 'dents', view, 'large', False) for view in ('far', 'behind')]
    scenes += [(n, 'impacts', 'near', 'small', False) for n in (30, 50)]
    scenes += [(50, a, 'near', 'large', True) for a in ('clean', 'dents')]
    if args.dense_only:
        scenes = [s for s in scenes if s[-1]]
    def name_of(s):
        n, activity, view, size, dense = s
        return f'{n}-{activity}-{view}-{size}-' + ('all5x' if dense else 'default')
    run_id = uuid.uuid4().hex
    (args.output / 'run.json').write_text(json.dumps(dict(run_id=run_id, scenes=[name_of(s) for s in scenes]), indent=2))
    failed = False
    with tempfile.TemporaryDirectory(prefix='prospect-benchmark-') as scratch:
        env = dict(os.environ, XDG_CONFIG_HOME=scratch + '/config', XDG_DATA_HOME=scratch + '/data')
        for scene in scenes:
            n, activity, view, size, dense = scene
            name = name_of(scene)
            spec = f'{n},{activity},{view}' + (',large' if size == 'large' else '')
            runargs = f'--visual-scene {spec} --bench {args.seconds} --listing off --no-validation'
            if dense:
                runargs += ' --set asteroidAmount=5 --set asteroidSize=5 --set asteroidFieldSize=5 --set asteroidFrequency=5 --set asteroidSpacing=5 --set asteroidOreShare=5 --set asteroidOreGrade=5'
            status = run_scene(args.output, name, ['make', 'run', 'CONFIG=Release', 'ARGS=' + runargs], env, run_id=run_id)
            failed |= status['exit_code'] != 0
            print(name, status['exit_code'], flush=True)
    return 1 if failed else 0


if __name__ == '__main__':
    raise SystemExit(main())
