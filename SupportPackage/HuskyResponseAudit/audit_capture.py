"""Read-only coverage audit for KAT Play Capture v2. Never connects to ROS."""
import argparse
import csv
import json
import math
from collections import Counter, defaultdict
from pathlib import Path


def audit(folder):
    folder = Path(folder)
    session_path = folder / "session.json"
    session = json.loads(session_path.read_text(encoding="utf-8-sig")) if session_path.exists() else {}
    entries, counts, issues = [], {}, []
    for path in sorted(folder.glob("control_*.csv")):
        stage = path.stem.removeprefix("control_")
        with path.open(encoding="utf-8-sig", newline="") as stream:
            rows = list(csv.DictReader(stream))
        counts[stage] = len(rows)
        for row in rows:
            try:
                if None in row or any(value is None for value in row.values()):
                    raise ValueError("incomplete CSV row")
                seq = int(row["event_seq"])
                elapsed = float(row["elapsed_s"])
                if not math.isfinite(elapsed):
                    raise ValueError("nonfinite receipt time")
                entries.append((seq, elapsed, stage, row))
            except (KeyError, ValueError) as exc:
                issues.append(f"{path.name}: {exc}")
    entries.sort(key=lambda item: item[0])
    seen, latest_input, topic_counts = set(), {}, Counter()
    ranges = defaultdict(lambda: {"max_abs_linear_command": 0.0, "max_abs_angular_command": 0.0})
    sources, moving_sources = set(), set()
    previous_time = -math.inf
    for seq, elapsed, stage, row in entries:
        if seq in seen:
            issues.append(f"duplicate event sequence {seq}")
        seen.add(seq)
        if elapsed < previous_time:
            issues.append(f"nonmonotonic capture time at event {seq}")
        previous_time = elapsed
        source, topic = row["source"], row["topic"]
        topic_counts[f"{stage}: {topic}"] += 1
        if stage in ("input", "keyboard_input"):
            latest_input[source] = row["input_seq"]
        if stage in ("request", "publish"):
            if stage == "request" or row.get("activated", "").lower() == "true":
                if latest_input.get(source) != row["input_seq"]:
                    issues.append(f"event {seq}: no matching preceding controller input")
        if stage in ("publish", "chassis_command"):
            try:
                linear, angular = float(row["linear_x"]), float(row["angular_z"])
                if not math.isfinite(linear) or not math.isfinite(angular):
                    raise ValueError("nonfinite command")
                observed = ranges[f"{source}: {topic}"]
                observed["max_abs_linear_command"] = max(observed["max_abs_linear_command"], abs(linear))
                observed["max_abs_angular_command"] = max(observed["max_abs_angular_command"], abs(angular))
                if stage == "publish":
                    sources.add(source)
                    if abs(linear) > 1e-6 or abs(angular) > 1e-6:
                        moving_sources.add(source)
            except (KeyError, ValueError) as exc:
                issues.append(f"event {seq}: {exc}")
    required = ["publish", "odometry", "configuration"]
    if "KAT DirectAngle" in sources:
        required += ["input", "request"]
    if "Keyboard" in sources:
        required += ["keyboard_input"]
    missing = [name for name in required if not counts.get(name)]
    for suffix in ("/cmd_vel", "/cmd_vel_out"):
        key = "chassis_command: /husky_velocity_controller" + suffix
        if not topic_counts[key]:
            missing.append(key)
    if session.get("controlCaptureError"):
        issues.append("capture reported write failure: " + session["controlCaptureError"])
    if not session:
        issues.append("session.json missing")
    return {
        "folder": str(folder), "format": session.get("format", "unknown"),
        "row_counts": counts, "received_topics": dict(topic_counts),
        "publish_sources": sorted(sources), "sources_with_nonzero_commands": sorted(moving_sources),
        "command_ranges": dict(ranges), "missing_channels": missing, "issues": issues,
        "required_channels_present": not missing,
        "independent_motion_accuracy": "NOT VERIFIED: requires external measurement and measured time alignment",
        "timing": "Unity receipt times; ROS source stamps retained in CSV. No video/frame synchronization is inferred.",
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folder", type=Path, help="Unpacked KATCaptures session directory")
    parser.add_argument("--require-chassis", action="store_true", help="Fail if any required control/ROS channel is absent")
    args = parser.parse_args()
    report = audit(args.folder)
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 1 if report["issues"] or (args.require_chassis and report["missing_channels"]) else 0


if __name__ == "__main__":
    raise SystemExit(main())
