"""Regression checks for capture attribution and manual warmed gates."""

import json
import csv
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from metal_capture import add_gpu_busy, union
from summarize_client_capture import frame_window, validate_replay_gate


class CaptureTests(unittest.TestCase):
    def test_union_does_not_double_count_nested_or_parallel_encoders(self):
        self.assertEqual(union([(4, 8), (1, 6), (2, 3), (9, 10), (8, 9)]), [(1, 10)])

    def test_join_filters_pid_resolves_references_and_clips_at_wall_boundaries(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            time = root / "time.xml"
            time.write_text('<trace-query-result><node><schema name="time-info">'
                            '<col><mnemonic>mabs-epoch</mnemonic></col>'
                            '<col><mnemonic>timebase-info</mnemonic></col></schema>'
                            '<row><epoch>100</epoch><base><n>2</n><d>1</d></base></row>'
                            '</node></trace-query-result>')
            gpu = root / "gpu.xml"
            gpu.write_text('<trace-query-result><node><schema name="metal-gpu-intervals">' +
                           ''.join(f'<col><mnemonic>{c}</mnemonic></col>' for c in
                                   ('start', 'duration', 'process', 'state')) + '</schema>'
                           '<row><s>0</s><d>20</d><process id="p"><pid id="pid">7</pid></process>'
                           '<state id="active">Active</state></row>'
                           '<row><s>10</s><d>20</d><process ref="p"/><state ref="active"/></row>'
                           '<row><s>30</s><d>10</d><process><pid>8</pid></process><state ref="active"/></row>'
                           '<row><s>30</s><d>10</d><process><pid ref="pid"/></process><state>Idle</state></row>'
                           '</node></trace-query-result>')
            toc = root / "toc.xml"
            toc.write_text('<trace-toc><run><info><target><process pid="7"/></target>'
                           '<summary><duration>0.000001</duration></summary></info></run></trace-toc>')
            rows = [{'mach_start': 105, 'mach_end': 110}, {'mach_start': 110, 'mach_end': 120}]
            metadata = {'nativeClock': 'mach_absolute_time', 'processId': 7}
            add_gpu_busy(rows, metadata, gpu, time, toc)
            self.assertAlmostEqual(rows[0]['gpu_busy_ms'], 10 / 1_000_000)
            self.assertAlmostEqual(rows[1]['gpu_busy_ms'], 10 / 1_000_000)
            with self.assertRaisesRegex(ValueError, 'PID'):
                add_gpu_busy(rows, dict(metadata, processId=8), gpu, time, toc)
            with self.assertRaisesRegex(ValueError, 'cover'):
                add_gpu_busy([{'mach_start': 99, 'mach_end': 110}], metadata, gpu, time, toc)

    def test_manual_capture_cannot_pass_a_warmed_gate_without_selection(self):
        with tempfile.TemporaryDirectory() as directory:
            capture = Path(directory) / 'manual.csv'
            capture.write_text('run,frame,wall_ms\n0,10,100\n')
            Path(str(capture) + '.json').write_text(json.dumps({'errors': [], 'replay': False}))
            completed = subprocess.run([sys.executable, str(Path(__file__).with_name('summarize_client_capture.py')),
                                        str(capture), '--max-warm-p95-ms', '25'], capture_output=True, text=True)
            self.assertEqual(completed.returncode, 2)
            self.assertIn('Manual captures require explicit', completed.stderr)

    def test_frame_ranges_are_inclusive_and_validated(self):
        self.assertEqual(frame_window('push:100:120'), ('push', 100, 120))
        import argparse
        for invalid in ('push:120:100', ':1:2', 'push:-1:2', 'push:1'):
            with self.assertRaises(argparse.ArgumentTypeError):
                frame_window(invalid)

    def test_manual_gate_excludes_startup_and_rejects_slow_warmed_frames(self):
        with tempfile.TemporaryDirectory() as directory:
            capture = Path(directory) / 'manual.csv'
            fields = ('run frame server_head_before wall_ms server_update_ms client_update_ms '
                      'interpolation_ms views_ms physics_debug_ms nav_debug_ms game_process_ms '
                      'server_physics_ms client_physics_ms client_replay_ms render_cpu_ms '
                      'render_gpu_ms render_setup_ms server_ticks client_forward_ticks '
                      'client_replay_ticks player_x player_y player_z').split()
            with capture.open('w', newline='') as stream:
                writer = csv.DictWriter(stream, fieldnames=fields)
                writer.writeheader()
                for frame, wall in ((1, 100), (2, 10), (3, 30)):
                    writer.writerow(dict.fromkeys(fields, 0) | {'frame': frame, 'wall_ms': wall})
            Path(str(capture) + '.json').write_text(json.dumps(
                {'errors': [], 'replay': False, 'routeStartTick': 60}))
            command = [sys.executable, str(Path(__file__).with_name('summarize_client_capture.py')),
                       str(capture), '--json', '--window', 'startup:1:1', '--warm-window', 'push',
                       '--max-warm-p95-ms', '25', '--window']
            passed = subprocess.run(command + ['push:2:2'], capture_output=True, text=True)
            self.assertEqual(passed.returncode, 0, passed.stderr)
            windows = json.loads(passed.stdout)['runs']['0']
            self.assertEqual(windows['startup']['timings']['wall_ms']['p95'], 100)
            self.assertEqual(windows['push']['frames'], 1)
            failed = subprocess.run(command + ['push:3:3'], capture_output=True, text=True)
            self.assertEqual(failed.returncode, 1)
            self.assertIn('Warmed p95 exceeds', failed.stderr)

    def test_replay_gate_rejects_missing_warm_runs_and_partial_routes(self):
        metadata = {'routeStartTick': 60, 'routeTicks': 480,
                    'completedRuns': [{'run': run, 'serverHead': 540, 'clientHead': 543} for run in range(3)]}
        windows = {f'route_{tick}-{tick + 119}': {} for tick in range(0, 480, 120)}
        rows = [{'run': run, 'server_head_after': 538, 'client_head_after': 541} for run in range(3)]
        validate_replay_gate(rows, metadata, windows)
        with self.assertRaisesRegex(ValueError, 'three runs'):
            validate_replay_gate(rows[:1], metadata, windows)
        with self.assertRaisesRegex(ValueError, 'every final-run'):
            validate_replay_gate(rows, metadata, {'startup': {}})
        with self.assertRaisesRegex(ValueError, 'completed final'):
            validate_replay_gate(rows, dict(metadata, completedRuns=metadata['completedRuns'][:2]), windows)
        with self.assertRaisesRegex(ValueError, 'completed final'):
            validate_replay_gate(rows, dict(metadata, completedRuns=metadata['completedRuns'][:2] +
                                           [{'run': 2, 'serverHead': 540, 'clientHead': 421}]), windows)


if __name__ == '__main__':
    unittest.main()
