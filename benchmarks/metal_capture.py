"""Join Metal execution intervals to captured wall intervals using the native Mach clock.

This measures union GPU occupancy within a wall interval, not a submitted frame's
GPU latency. Encoders can overlap or cross frame boundaries; never sum durations.
"""

import xml.etree.ElementTree as ET


def table(path, schema):
    root = ET.parse(path).getroot()
    refs = {e.attrib["id"]: e for e in root.iter() if "id" in e.attrib}

    def resolve(element):
        while "ref" in element.attrib:
            element = refs[element.attrib["ref"]]
        return element

    node = next(n for n in root.findall("node") if n.find("schema").attrib["name"] == schema)
    columns = [c.findtext("mnemonic") for c in node.find("schema")]
    return [{name: resolve(e) for name, e in zip(columns, row)} for row in node.findall("row")], resolve


def union(intervals):
    merged = []
    for start, end in sorted(intervals):
        if merged and start <= merged[-1][1]:
            merged[-1] = (merged[-1][0], max(end, merged[-1][1]))
        else:
            merged.append((start, end))
    return merged


def add_gpu_busy(rows, metadata, gpu_path, time_path, toc_path):
    if metadata.get("nativeClock") != "mach_absolute_time":
        raise ValueError("GPU joining requires a capture with native Mach timestamps")
    times, resolve = table(time_path, "time-info")
    epoch = int(times[0]["mabs-epoch"].text)
    base = [int(resolve(e).text) for e in times[0]["timebase-info"]]
    numerator, denominator = base
    run = ET.parse(toc_path).getroot().find("run")
    process = run.find("info/target/process")
    if int(process.attrib["pid"]) != metadata["processId"]:
        raise ValueError("Trace target PID does not match capture PID")
    trace_end = float(run.findtext("info/summary/duration")) * 1_000_000_000
    gpu_rows, resolve = table(gpu_path, "metal-gpu-intervals")
    intervals = []
    for row in gpu_rows:
        process = row["process"]
        pid = process.find("pid")
        if pid is None or int(resolve(pid).text) != metadata["processId"]:
            continue
        # The table includes all processes and nested encoders. Only executing work
        # from the capture's PID contributes; a sentinel is not an execution state.
        if row["state"].text != "Active":
            continue
        start = int(row["start"].text)
        duration = int(row["duration"].text)
        if duration > 0:
            intervals.append((start, start + duration))
    if not intervals:
        raise ValueError("No executing GPU intervals for capture PID; check matching trace exports")
    intervals = union(intervals)
    for row in rows:
        start = (int(row["mach_start"]) - epoch) * numerator / denominator
        end = (int(row["mach_end"]) - epoch) * numerator / denominator
        if start < 0 or end <= start or end > trace_end:
            raise ValueError("Trace does not cover complete capture wall intervals")
        row["gpu_busy_ms"] = sum(max(0, min(end, b) - max(start, a))
                                 for a, b in intervals if a < end and b > start) / 1_000_000
