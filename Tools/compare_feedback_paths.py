#!/usr/bin/env python3
"""Offline exact-ROS-timestamp comparison of a source probe and Unity callbacks.

Reads existing CSVs only. No networking, Unity APIs, or robot control.
"""
import argparse
import collections
import csv
import datetime as dt
from decimal import Decimal, InvalidOperation
import json
import math
import pathlib
import statistics
import sys

NS = 1000000000
MAX_ROWS = 1000000


def distribution(values):
    ordered = sorted(values)
    if not ordered:
        return {"count": 0, "min_s": None, "median_s": None, "p95_s": None, "max_s": None}
    # Nearest-rank p95, deliberately no dependency on numpy.
    return {"count": len(values), "min_s": ordered[0], "median_s": statistics.median(ordered),
            "p95_s": ordered[max(0, math.ceil(.95*len(ordered))-1)], "max_s": ordered[-1]}


def load_csv(path, topic, role):
    rows, rejected = [], collections.Counter()
    previous_stamp = base_stamp = base_mono = previous_mono = None
    epoch = 0
    with pathlib.Path(path).open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        fields = set(reader.fieldnames or [])
        if {"host_utc_ns", "ros_stamp_sec", "ros_stamp_nsec"} <= fields:
            utc_field, sec_field, nsec_field = "host_utc_ns", "ros_stamp_sec", "ros_stamp_nsec"
        elif {"receipt_utc_ns", "stamp_sec", "stamp_nsec"} <= fields:
            utc_field, sec_field, nsec_field = "receipt_utc_ns", "stamp_sec", "stamp_nsec"
        else:
            raise ValueError(f"{path}: unsupported CSV header")
        mono_field = "receipt_monotonic_ns" if "receipt_monotonic_ns" in fields else "unity_receipt_s"
        if mono_field not in fields:
            raise ValueError(f"{path}: no monotonic receipt timestamp")
        for line, item in enumerate(reader, 2):
            if line > MAX_ROWS+1:
                raise ValueError(f"{path}: exceeds {MAX_ROWS} input rows")
            if item.get("topic", topic) != topic:
                rejected["other_topic"] += 1
                continue
            try:
                sec, nsec, utc = int(item[sec_field]), int(item[nsec_field]), int(item[utc_field])
                mono = (int(item[mono_field]) if mono_field.endswith("_ns")
                        else int(Decimal(item[mono_field])*NS))
                if sec < 0 or not 0 <= nsec < NS or utc <= 0:
                    raise ValueError("invalid timestamp")
            except (ValueError, TypeError, InvalidOperation, OverflowError):
                rejected["invalid_timestamp"] += 1
                continue
            stamp = sec*NS+nsec
            if stamp == 0:
                rejected["zero_source_stamp"] += 1
                continue
            source_regression = previous_stamp is not None and stamp < previous_stamp
            receipt_regression = previous_mono is not None and mono < previous_mono
            if source_regression or receipt_regression:
                epoch += 1
                base_stamp = base_mono = None
            if base_stamp is None:
                base_stamp, base_mono = stamp, mono
            rows.append({"line": line, "topic": topic, "stamp_ns": stamp, "utc_ns": utc,
                         "mono_ns": mono, "epoch": epoch,
                         "source_regression": source_regression, "receipt_regression": receipt_regression,
                         "backlog_s": ((mono-base_mono)-(stamp-base_stamp))/NS})
            previous_stamp, previous_mono = stamp, mono
    return rows, {"path": str(pathlib.Path(path).resolve()), "role": role,
                  "valid_rows": len(rows), "skipped_rows": dict(rejected),
                  "utc_field": utc_field, "monotonic_field": mono_field,
                  "source_regressions": sum(r["source_regression"] for r in rows),
                  "receipt_regressions": sum(r["receipt_regression"] for r in rows)}


def path_summary(rows):
    if not rows:
        return {"count": 0}
    arrival = [(b["mono_ns"]-a["mono_ns"])/NS for a, b in zip(rows, rows[1:])
               if b["mono_ns"] >= a["mono_ns"]]
    source = [(b["stamp_ns"]-a["stamp_ns"])/NS for a, b in zip(rows, rows[1:])
              if b["stamp_ns"] > a["stamp_ns"]]
    continuous = not any(r["receipt_regression"] for r in rows)
    span = (rows[-1]["mono_ns"]-rows[0]["mono_ns"])/NS if continuous else None
    wall_vs_mono = ([((r["utc_ns"]-rows[0]["utc_ns"])-(r["mono_ns"]-rows[0]["mono_ns"]))/NS for r in rows]
                    if continuous else [])
    return {"count": len(rows), "first_receipt_utc_ns": min(r["utc_ns"] for r in rows),
            "last_receipt_utc_ns": max(r["utc_ns"] for r in rows),
            "receipt_span_s": span, "receipt_clock_continuous": continuous,
            "arrival_hz_first_to_last": (len(rows)-1)/span if span is not None and span > 0 else None,
            "source_hz_positive_intervals": len(source)/sum(source) if source else None,
            "arrival_gap": distribution(arrival), "relative_backlog": distribution([r["backlog_s"] for r in rows]),
            "relative_backlog_last_s": rows[-1]["backlog_s"],
            "utc_progress_minus_monotonic_progress": distribution(wall_vs_mono)}


def write_csv(path, fields, rows):
    with pathlib.Path(path).open("x", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)


def compare(source_path, unity_path, outdir, topic="/odometry/filtered", bin_seconds=5):
    unity_path = pathlib.Path(unity_path)
    if unity_path.is_dir():
        unity_path /= "control_odometry.csv"
    if not math.isfinite(bin_seconds) or not .1 <= bin_seconds <= 60:
        raise ValueError("bin_seconds must be finite and between .1 and 60")
    source, source_info = load_csv(source_path, topic, "source")
    unity, unity_info = load_csv(unity_path, topic, "unity")
    if not source or not unity:
        raise ValueError("both inputs must contain valid rows for the selected topic")
    grouped = {}
    for label, rows in (("source", source), ("unity", unity)):
        grouped[label] = collections.defaultdict(list)
        for row in rows:
            grouped[label][row["stamp_ns"]].append(row)
    source_keys, unity_keys = set(grouped["source"]), set(grouped["unity"])
    shared = source_keys & unity_keys
    ambiguous = {key for key in source_keys | unity_keys
                 if len(grouped["source"].get(key, [])) > 1 or len(grouped["unity"].get(key, [])) > 1}
    match_keys = shared-ambiguous
    overlap_lo = max(min(source_keys), min(unity_keys))
    overlap_hi = min(max(source_keys), max(unity_keys))
    matches = []
    for key in sorted(match_keys, key=lambda stamp: grouped["source"][stamp][0]["utc_ns"]):
        s, u = grouped["source"][key][0], grouped["unity"][key][0]
        matches.append({"topic": topic, "ros_stamp_sec": key//NS, "ros_stamp_nsec": key % NS,
                        "source_line": s["line"], "unity_line": u["line"],
                        "source_receipt_utc_ns": s["utc_ns"], "unity_callback_utc_ns": u["utc_ns"],
                        "unity_minus_source_s": (u["utc_ns"]-s["utc_ns"])/NS,
                        "source_epoch": s["epoch"], "unity_epoch": u["epoch"],
                        "source_relative_backlog_s": s["backlog_s"], "unity_relative_backlog_s": u["backlog_s"]})
    unmatched, missing_counts = [], {}
    for label, rows, other_keys in (("source", source, unity_keys), ("unity", unity, source_keys)):
        counts = collections.Counter()
        for row in rows:
            key = row["stamp_ns"]
            if key in match_keys:
                continue
            in_span = overlap_lo <= key <= overlap_hi
            reason = ("ambiguous_duplicate_timestamp" if key in ambiguous
                      else "not_in_other_within_shared_stamp_span" if in_span
                      else "not_in_other_outside_shared_stamp_span")
            counts[reason] += 1
            unmatched.append({"path": label, "line": row["line"], "topic": topic,
                              "ros_stamp_sec": key//NS, "ros_stamp_nsec": key % NS,
                              "receipt_utc_ns": row["utc_ns"], "source_epoch": row["epoch"], "reason": reason})
        missing_counts[label] = dict(counts)

    origin = min(min(r["utc_ns"] for r in source), min(r["utc_ns"] for r in unity))
    final = max(max(r["utc_ns"] for r in source), max(r["utc_ns"] for r in unity))
    bin_ns = round(bin_seconds*NS)
    num_bins = (final-origin)//bin_ns+1
    if num_bins > 20000:
        raise ValueError("inputs span over 20,000 bins; choose overlapping sessions")
    bins = []
    by_bin = {label: collections.defaultdict(list) for label in ("source", "unity")}
    observed_spans = {}
    for label, rows in (("source", source), ("unity", unity)):
        observed_spans[label] = (min(r["utc_ns"] for r in rows), max(r["utc_ns"] for r in rows))
        for row in rows:
            by_bin[label][(row["utc_ns"]-origin)//bin_ns].append(row)
    match_bins = collections.defaultdict(list)
    for match in matches:
        match_bins[(match["unity_callback_utc_ns"]-origin)//bin_ns].append(match["unity_minus_source_s"])
    for index in range(num_bins):
        start, end = origin+index*bin_ns, origin+(index+1)*bin_ns
        record = {"bin_index": index, "start_utc_ns": start, "end_utc_ns": end,
                  "start_elapsed_s": index*bin_seconds, "bin_seconds": bin_seconds}
        for label, rows in (("source", source), ("unity", unity)):
            selected = by_bin[label].get(index, [])
            record[label+"_count"] = len(selected)
            record[label+"_count_per_bin_second"] = len(selected)/bin_seconds
            first_utc, last_utc = observed_spans[label]
            record[label+"_entire_bin_inside_observed_span"] = start >= first_utc and end <= last_utc
            record[label+"_max_arrival_gap_within_bin_s"] = max(
                ((b["mono_ns"]-a["mono_ns"])/NS for a, b in zip(selected, selected[1:])
                 if b["mono_ns"] >= a["mono_ns"]), default=None)
            record[label+"_relative_backlog_first_s"] = selected[0]["backlog_s"] if selected else None
            record[label+"_relative_backlog_last_s"] = selected[-1]["backlog_s"] if selected else None
            record[label+"_relative_backlog_max_s"] = max((r["backlog_s"] for r in selected), default=None)
            record[label+"_epochs"] = ";".join(str(e) for e in sorted({r["epoch"] for r in selected}))
        delays = distribution(match_bins.get(index, []))
        record.update({"matched_callback_count": delays["count"], "unity_minus_source_median_s": delays["median_s"],
                       "unity_minus_source_p95_s": delays["p95_s"], "unity_minus_source_max_s": delays["max_s"]})
        bins.append(record)
    summary = {"topic": topic, "comparison_status": "matched_unique_packets" if matches else "no_unique_timestamp_overlap",
               "source_input": source_info, "unity_input": unity_info,
               "source": path_summary(source), "unity": path_summary(unity),
               "matched_unique_timestamps": len(matches), "ambiguous_timestamp_keys": len(ambiguous),
               "source_unique_timestamp_count": len(source_keys), "unity_unique_timestamp_count": len(unity_keys),
               "shared_stamp_span_start_ns": overlap_lo if overlap_lo <= overlap_hi else None,
               "shared_stamp_span_end_ns": overlap_hi if overlap_lo <= overlap_hi else None,
               "unmatched_rows": missing_counts, "unity_minus_source": distribution([m["unity_minus_source_s"] for m in matches]),
               "unity_arrived_before_source_count": sum(m["unity_minus_source_s"] < 0 for m in matches),
               "bin_seconds": bin_seconds, "bin_origin_utc_ns": origin, "bin_count": len(bins),
               "notes": ["Matches require identical integer ROS sec/nsec and the selected topic; there is no nearest-time matching.",
                         "A repeated timestamp on either path is ambiguous and excluded from latency matching; all excluded rows are preserved.",
                         "UTC subtraction assumes both CSVs were recorded on the same PC without a wall-clock step between receipts. Timestamp precision and scheduling still apply.",
                         "Positive unity_minus_source_s means the Unity callback trace occurred later than the independent source-probe complete read. It is a path difference, not pure Unity-internal delay or robot execution latency.",
                         "Relative backlog uses each path's own first monotonic receipt/source stamp per epoch; constant clock offset cancels. Epoch changes follow source or receipt clock regression.",
                         "Independent per-path backlog baselines are not directly equal; use matched receipt differences for cross-path delay.",
                         "Whole-path receipt rate/span and UTC-versus-monotonic drift are omitted if the receipt clock regresses; per-bin counts remain available.",
                         "Unmatched rows are observations missing from the other recording, not proven packet loss; subscription periods, duplicate stamps, clock resets and logging can explain them.",
                         "Shared-stamp-span classification is a numeric range, not proof of continuous source coverage when stamps reset.",
                         "Five-second throughput uses common UTC bins. First/last partial bins are flagged and still divide by nominal bin width; no sample interval is invented outside observed spans.",
                         "Maximum gap within a bin excludes gaps crossing its edge; whole-path gap statistics include them."]}
    outdir = pathlib.Path(outdir)
    outdir.mkdir(parents=True, exist_ok=False)
    match_fields = ["topic", "ros_stamp_sec", "ros_stamp_nsec", "source_line", "unity_line",
                    "source_receipt_utc_ns", "unity_callback_utc_ns", "unity_minus_source_s",
                    "source_epoch", "unity_epoch", "source_relative_backlog_s", "unity_relative_backlog_s"]
    write_csv(outdir/"matched.csv", match_fields, matches)
    write_csv(outdir/"unmatched.csv", ["path", "line", "topic", "ros_stamp_sec", "ros_stamp_nsec",
                                     "receipt_utc_ns", "source_epoch", "reason"], unmatched)
    write_csv(outdir/"bins.csv", list(bins[0]), bins)
    with (outdir/"summary.json").open("x", encoding="utf-8") as stream:
        json.dump(summary, stream, indent=2, allow_nan=False)
        stream.write("\n")
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", "--source-csv", dest="source", type=pathlib.Path, required=True)
    parser.add_argument("--unity", "--capture", dest="unity", type=pathlib.Path, required=True,
                        help="KAT capture directory or control_odometry.csv")
    parser.add_argument("--topic", default="/odometry/filtered")
    parser.add_argument("--bin-seconds", type=float, default=5)
    parser.add_argument("--outdir", type=pathlib.Path)
    args = parser.parse_args()
    outdir = args.outdir or pathlib.Path("SupportPackage/FeedbackTransportAudit") / dt.datetime.now(dt.timezone.utc).strftime("compare_%Y%m%d_%H%M%S_%fZ")
    try:
        result = compare(args.source, args.unity, outdir, args.topic, args.bin_seconds)
    except (OSError, ValueError, InvalidOperation) as exc:
        print("Comparison failed: " + str(exc)[:500], file=sys.stderr)
        return 2
    print(json.dumps({"outdir": str(outdir.resolve()), "comparison_status": result["comparison_status"],
                      "matched_unique_timestamps": result["matched_unique_timestamps"],
                      "unity_minus_source": result["unity_minus_source"], "unmatched_rows": result["unmatched_rows"],
                      "source_rows": result["source"]["count"], "unity_rows": result["unity"]["count"]}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
