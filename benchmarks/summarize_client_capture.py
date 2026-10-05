"""Summarize an opt-in Godot client capture without treating tick timings as FPS."""

import argparse
import csv
import json
import math
from collections import Counter
from pathlib import Path
from statistics import mean


WORKLOAD_COUNTERS = ("awake_bodies", "sleeping_bodies", "moved_proxies", "pairs_created",
                     "contacts_updated", "contacts_sleeping", "touching_contacts", "constraints",
                     "ccd_bodies", "ccd_hits", "islands", "islands_fell_asleep", "bodies_woken",
                     "sat_full_searches", "sat_fallbacks", "sat_separation_hits", "sat_face_hits", "sat_edge_hits")


def distribution(values):
    return {"mean": mean(values), "p50": percentile(values, 50), "p95": percentile(values, 95),
            "p99": percentile(values, 99), "max": max(values)}


def join_callbacks(rows, callbacks):
    """Validate actual callbacks for complete frames; exclude unfinished terminal frames."""
    keys = {(int(r["run"]), int(r["frame"])) for r in rows}
    joined = {key: [] for key in keys}
    for callback in callbacks:
        key = (int(callback["run"]), int(callback["frame"]))
        if callback["kind"] not in ("server", "forward", "replay"):
            raise ValueError("Unknown callback kind")
        if callback["input_valid"] and bool(callback["input_fresh"]) != (callback["input_age"] == 0):
            raise ValueError("Callback input freshness disagrees with age")
        if key in joined:
            joined[key].append(callback)
    for row in rows:
        counts = Counter(c["kind"] for c in joined[(int(row["run"]), int(row["frame"]))])
        for kind, field in (("server", "server_ticks"), ("forward", "client_forward_ticks"), ("replay", "client_replay_ticks")):
            if counts[kind] != row[field]:
                raise ValueError(f"Callback count mismatch for run {row['run']}, frame {row['frame']}, {kind}")
    return joined


def summarize_callbacks(callbacks, movement_end):
    result = {}
    for kind in ("server", "forward", "replay"):
        samples = [c for c in callbacks if c["kind"] == kind]
        if not samples:
            continue
        valid = [c for c in samples if c["input_valid"]]
        result[kind] = {
            "callbacks": len(samples),
            "timings": {field: distribution([c[field] for c in samples]) for field in
                        ("simulation_ms", "physics_ms", "prepare_ms", "solve_ms", "narrowphase_ms")},
            "counters": {field: {"total": int(sum(c[field] for c in samples)),
                                 **distribution([c[field] for c in samples])} for field in WORKLOAD_COUNTERS},
            "input": {"valid": len(valid), "fresh": sum(bool(c["input_fresh"]) for c in valid),
                      "approved": sum(bool(c["input_approved"]) for c in valid),
                      "age": distribution([c["input_age"] for c in valid]) if valid else None,
                      "source_tick_range": [min(c["tick"] - c["input_age"] for c in valid if c["input_age"] < 65535),
                                            max(c["tick"] - c["input_age"] for c in valid if c["input_age"] < 65535)]
                      if any(c["input_age"] < 65535 for c in valid) else None,
                      "move_histogram": dict(Counter(f"{c['move_x']:g},{c['move_y']:g}" for c in valid)),
                      "moving_after_nominal_stop": sum(c["tick"] >= movement_end and (c["move_x"] != 0 or c["move_y"] != 0) for c in valid)},
            "player_last_xyz": [samples[-1][f"player_{axis}"] for axis in "xyz"],
        }
    return result


def percentile(values, percent):
    values = sorted(values)
    return values[max(0, math.ceil(len(values) * percent / 100) - 1)]


def summarize(rows):
    fields = ["wall_ms", "server_update_ms", "client_update_ms", "interpolation_ms",
              "views_ms", "physics_debug_ms", "nav_debug_ms", "game_process_ms",
              "server_physics_ms", "client_physics_ms", "client_replay_ms",
              "render_cpu_ms", "render_gpu_ms", "render_setup_ms"]
    fields += [field for field in ("server_simulation_ms", "client_forward_ms",
               "server_narrowphase_ms", "client_narrowphase_ms", "server_solve_ms", "client_solve_ms", "workload_probe_ms")
               if field in rows[0]]
    result = {
        "frames": len(rows),
        "timings": {field: {
            "mean": round(mean(r[field] for r in rows), 4),
            "p50": round(percentile([r[field] for r in rows], 50), 4),
            "p95": round(percentile([r[field] for r in rows], 95), 4),
            "p99": round(percentile([r[field] for r in rows], 99), 4),
            "max": round(max(r[field] for r in rows), 4),
        } for field in fields},
        "ticks_per_frame": {field: {
            "total": int(sum(r[field] for r in rows)),
            "mean": round(mean(r[field] for r in rows), 3),
            "p95": percentile([r[field] for r in rows], 95),
            "max": max(r[field] for r in rows),
            "histogram": dict(sorted(Counter(int(r[field]) for r in rows).items())),
        } for field in ("server_ticks", "client_forward_ticks", "client_replay_ticks")},
        "frames_over_25ms": sum(r["wall_ms"] > 25 for r in rows),
        "frames_over_33_3ms": sum(r["wall_ms"] > 33.3 for r in rows),
        "player_last_xyz": [rows[-1][f"player_{axis}"] for axis in "xyz"],
        "gpu_nonzero_samples": sum(r["render_gpu_ms"] > 0 for r in rows),
        "slowest": [{k: r[k] for k in (
            "frame", "server_head_before", "wall_ms", "server_update_ms", "client_update_ms",
            "interpolation_ms", "views_ms", "physics_debug_ms", "nav_debug_ms",
            "server_ticks", "client_forward_ticks", "client_replay_ticks", "render_cpu_ms")}
            for r in sorted(rows, key=lambda r: r["wall_ms"], reverse=True)[:3]],
    }
    if "physics_overlay" in rows[0]:
        result["overlay_states"] = dict(Counter(
            f"physics={int(r['physics_overlay'])},flags={int(r['physics_overlay_flags'])};"
            f"nav={int(r['nav_overlay'])},flags={int(r['nav_overlay_flags'])},agent={int(r['nav_selected_agent'])}"
            for r in rows))
    if "interpolation_calls" in rows[0]:
        calls = sum(r["interpolation_calls"] for r in rows)
        result["interpolation_copy"] = {
            "calls": int(calls),
            "allocated_bytes": int(sum(r["interpolation_allocated_bytes"] for r in rows)),
            "snapshot_bytes": int(sum(r["interpolation_snapshot_bytes"] for r in rows)),
            "ms_per_call": sum(r["interpolation_ms"] for r in rows) / calls if calls else None,
            "allocated_bytes_per_call": sum(r["interpolation_allocated_bytes"] for r in rows) / calls if calls else None,
            "snapshot_bytes_per_call": sum(r["interpolation_snapshot_bytes"] for r in rows) / calls if calls else None,
        }
    if "gpu_busy_ms" in rows[0]:
        values = [r["gpu_busy_ms"] for r in rows]
        result["gpu_wall_interval_busy_ms"] = {
            "mean": mean(values), **{f"p{p}": percentile(values, p) for p in (50, 95, 99)},
            "max": max(values),
        }
        for sample in result["slowest"]:
            sample["gpu_busy_ms"] = next(r["gpu_busy_ms"] for r in rows if r["frame"] == sample["frame"])
    return result


def frame_window(value):
    try:
        name, start, end = value.split(":")
        start, end = int(start), int(end)
        if not name or start < 0 or end < start:
            raise ValueError()
        return name, start, end
    except ValueError:
        raise argparse.ArgumentTypeError("Use NAME:FIRST_FRAME:LAST_FRAME (inclusive)")


def validate_replay_gate(rows, metadata, windows):
    # Completion is recorded before discarding the final unfinished wall interval.
    # That final frame can advance multiple ticks during catch-up.
    if {int(r["run"]) for r in rows} != {0, 1, 2}:
        raise ValueError("Warmed replay gate requires all three runs")
    expected = {f"route_{tick}-{tick + 119}" for tick in range(0, metadata["routeTicks"], 120)}
    if not expected or expected - set(windows):
        raise ValueError("Warmed replay gate requires every final-run route window")
    completions = {r["run"]: r for r in metadata.get("completedRuns", [])}
    end = metadata["routeStartTick"] + metadata["routeTicks"]
    if set(completions) != {0, 1, 2} or any(min(r["serverHead"], r["clientHead"]) < end for r in completions.values()):
        raise ValueError("Warmed replay gate requires a completed final route")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--json", action="store_true", help="Print all counters and slow-frame details")
    parser.add_argument("--max-warm-p95-ms", type=float, help="Optional diagnostic threshold, not a CI budget")
    parser.add_argument("--window", type=frame_window, action="append", default=[],
                        help="Select a named inclusive engine-frame range; repeat for startup and warmed pushes")
    parser.add_argument("--warm-window", action="append", default=[],
                        help="Name a --window eligible for the warmed p95 gate; required for manual captures")
    parser.add_argument("--metal-gpu", type=Path, help="xctrace metal-gpu-intervals XML export")
    parser.add_argument("--metal-time", type=Path, help="Matching xctrace time-info XML export")
    parser.add_argument("--metal-toc", type=Path, help="Matching xctrace table of contents XML export")
    parser.add_argument("--callbacks", type=Path, help="Per-callback workload CSV; defaults to CAPTURE.ticks.csv when metadata declares it")
    args = parser.parse_args()
    metadata = json.loads(Path(str(args.capture) + ".json").read_text())
    with args.capture.open(newline="") as source:
        rows = [{key: float(value) for key, value in row.items()} for row in csv.DictReader(source)]
    if metadata["errors"]:
        raise ValueError(f"Invalid replay: {metadata['errors']}")
    if not rows:
        parser.error("Capture has no complete frames")
    joined = None
    if args.callbacks or metadata.get("workloadDiagnostics"):
        path = args.callbacks or Path(str(args.capture) + ".ticks.csv")
        with path.open(newline="") as source:
            callbacks = [{key: value if key == "kind" else int(value) if key == "player_gid" else float(value)
                          for key, value in row.items()} for row in csv.DictReader(source)]
        joined = join_callbacks(rows, callbacks)
    names = [w[0] for w in args.window]
    if len(set(names)) != len(names) or set(args.warm_window) - set(names):
        parser.error("Window names must be unique; --warm-window must name a --window")
    if args.max_warm_p95_ms is not None and not args.warm_window:
        if not metadata["replay"]:
            parser.error("Manual captures require explicit --window and --warm-window for a warmed gate")
        if args.window:
            parser.error("Explicit frame windows require --warm-window for a warmed gate")
    if any((args.metal_gpu, args.metal_time, args.metal_toc)) and not all((args.metal_gpu, args.metal_time, args.metal_toc)):
        parser.error("--metal-gpu, --metal-time and --metal-toc must be supplied together")
    if args.metal_gpu:
        from metal_capture import add_gpu_busy
        add_gpu_busy(rows, metadata, args.metal_gpu, args.metal_time, args.metal_toc)
    start = metadata["routeStartTick"]
    result = {"metadata": metadata, "runs": {}}
    for run in sorted({int(r["run"]) for r in rows}):
        run_rows = [r for r in rows if r["run"] == run]
        windows = {"manual": run_rows}
        if args.window:
            windows = {name: [r for r in run_rows if first <= r["frame"] <= last]
                       for name, first, last in args.window}
        elif metadata["replay"]:
            windows = {"startup": [r for r in run_rows if r["server_head_before"] < start]}
            for tick in range(0, metadata["routeTicks"], 120):
                windows[f"route_{tick}-{tick + 119}"] = [r for r in run_rows
                    if start + tick <= r["server_head_before"] < start + tick + 120]
        result["runs"][run] = {name: summarize(window) for name, window in windows.items() if window}
        if joined is not None:
            for name, window in windows.items():
                if window:
                    result["runs"][run][name]["callbacks"] = summarize_callbacks(
                        [c for r in window for c in joined[(int(r["run"]), int(r["frame"]))]],
                        metadata["routeStartTick"] + metadata["routeMovementTicks"])
    if set(names) - {name for windows in result["runs"].values() for name in windows}:
        parser.error("A requested window contains no complete frames")
    result["runs"] = {run: windows for run, windows in result["runs"].items() if windows}
    if args.max_warm_p95_ms is not None and metadata["replay"] and not args.window:
        try:
            validate_replay_gate(rows, metadata, result["runs"][max(result["runs"])])
        except ValueError as error:
            parser.error(str(error))
    if args.json:
        print(json.dumps(result, indent=2, allow_nan=True))
    else:
        print(f"{metadata['configuration']} | {metadata['runtime']} | {metadata['gpu']} | "
              f"{metadata['window']} | VSync={metadata['vsync']} | overlays={metadata['debugDrawing']}")
        print("run window frames | wall p50/p95/p99/max | server/client/interpolation/views/debug/renderCPU means | ticks server/forward/replay means")
        for run, windows in result["runs"].items():
            for name, window in windows.items():
                timings = window["timings"]
                wall = timings["wall_ms"]
                times = [timings[k]["mean"] for k in ("server_update_ms", "client_update_ms",
                         "interpolation_ms", "views_ms")]
                times += [timings["physics_debug_ms"]["mean"] + timings["nav_debug_ms"]["mean"],
                          timings["render_cpu_ms"]["mean"] + timings["render_setup_ms"]["mean"]]
                ticks = [v["mean"] for v in window["ticks_per_frame"].values()]
                print(f"{run} {name} {window['frames']} | " + "/".join(f"{wall[k]:.3f}" for k in ("p50", "p95", "p99", "max")) +
                      " | " + "/".join(f"{v:.3f}" for v in times) + " | " + "/".join(f"{v:.2f}" for v in ticks))
        print(f"setup ms: {[round(r['setupMs'], 3) for r in metadata['runs']]}; "
              f"first _EnterTree engine uptime ms: {metadata['firstEnterMs']:.3f}; "
                 f"GPU nonzero samples: {sum(r['render_gpu_ms'] > 0 for r in rows)}")
        for run, windows in result["runs"].items():
            for name, window in windows.items():
                if "overlay_states" in window:
                    print(f"{run} {name}: overlay states {window['overlay_states']}")
                if "interpolation_copy" in window:
                    print(f"{run} {name}: interpolation copy {window['interpolation_copy']}")
                if "gpu_wall_interval_busy_ms" in window:
                    print(f"{run} {name}: Metal GPU union busy ms per wall interval "
                          f"{window['gpu_wall_interval_busy_ms']}")
        warm = result["runs"][max(result["runs"])]
        for name, window in warm.items():
            if name == "startup":
                continue
            print(f"{name}: physics mean server/client="
                  f"{window['timings']['server_physics_ms']['mean']:.3f}/"
                  f"{window['timings']['client_physics_ms']['mean']:.3f}ms; "
                  f"replay={window['timings']['client_replay_ms']['mean']:.3f}ms; "
                  f"frames>25ms={window['frames_over_25ms']}; "
                  f"forward max={window['ticks_per_frame']['client_forward_ticks']['max']}; "
                  f"replay max={window['ticks_per_frame']['client_replay_ticks']['max']}")
    if args.max_warm_p95_ms is not None:
        warm = result["runs"][max(result["runs"])]
        eligible = args.warm_window or [name for name in warm if name.startswith("route_")]
        if set(eligible) - set(warm):
            parser.error("A warmed window has no frames in the final selected run")
        failed = [name for name, window in warm.items() if name in eligible
                  and window["timings"]["wall_ms"]["p95"] > args.max_warm_p95_ms]
        if failed:
            parser.exit(1, f"Warmed p95 exceeds {args.max_warm_p95_ms}ms in {failed}\n")


if __name__ == "__main__":
    main()
