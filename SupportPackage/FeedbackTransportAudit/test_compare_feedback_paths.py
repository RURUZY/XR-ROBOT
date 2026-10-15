"""Offline synthetic CSV tests; no network and no Unity API access."""
import csv
import importlib.util
import json
import pathlib
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("compare", ROOT / "Tools/compare_feedback_paths.py")
c = importlib.util.module_from_spec(spec)
spec.loader.exec_module(c)
BASE = 1791947011000000000
STAMP = 1791947000000000000


def save(path, rows):
    with path.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)


def source_row(stamp, receipt, mono, **extra):
    return dict(host_utc_ns=BASE+receipt, ros_stamp_sec=stamp//c.NS, ros_stamp_nsec=stamp % c.NS,
                receipt_monotonic_ns=10000000000+mono, **extra)


def unity_row(stamp, receipt, mono, topic="/odometry/filtered"):
    return dict(host_utc_ns=BASE+receipt, ros_stamp_sec=stamp//c.NS, ros_stamp_nsec=stamp % c.NS,
                unity_receipt_s=str(c.Decimal(mono)/c.NS+30), topic=topic)


class ComparisonTests(unittest.TestCase):
    def compare(self, source, unity):
        temp = tempfile.TemporaryDirectory(prefix="feedback_compare_")
        self.addCleanup(temp.cleanup)
        root = pathlib.Path(temp.name)
        save(root/"source.csv", source)
        (root/"capture").mkdir()
        save(root/"capture/control_odometry.csv", unity)
        result = c.compare(root/"source.csv", root/"capture", root/"out")
        self.assertEqual(result, json.loads((root/"out/summary.json").read_text()))
        return result, root

    def test_exact_integer_stamp_and_signed_delay_and_topic_filter(self):
        result, root = self.compare(
            [source_row(STAMP, 1000000, 0), source_row(STAMP+1, 2000000, 1000000)],
            [unity_row(STAMP, 1000180, 180), unity_row(STAMP+1, 1999980, 999980),
             unity_row(STAMP, 9000000, 8000000, "/other")])
        self.assertEqual(result["matched_unique_timestamps"], 2)
        self.assertAlmostEqual(result["unity_minus_source"]["max_s"], .000000180)
        self.assertAlmostEqual(result["unity_minus_source"]["min_s"], -.000000020)
        self.assertEqual(result["unity_arrived_before_source_count"], 1)
        self.assertEqual(result["unity_input"]["skipped_rows"]["other_topic"], 1)
        with (root/"out/matched.csv").open(newline="") as stream:
            rows = list(csv.DictReader(stream))
        self.assertEqual(rows[1]["ros_stamp_nsec"], "1")

    def test_duplicate_keys_excluded_and_missing_classified(self):
        result, _ = self.compare(
            [source_row(STAMP+i*c.NS, i*c.NS, i*c.NS) for i in (0, 1, 1, 2, 3, 4)],
            [unity_row(STAMP+i*c.NS, i*c.NS+100000000, i*c.NS+100000000) for i in (1, 2, 4, 5)])
        self.assertEqual(result["matched_unique_timestamps"], 2)
        self.assertEqual(result["ambiguous_timestamp_keys"], 1)
        self.assertEqual(result["unmatched_rows"]["source"]["ambiguous_duplicate_timestamp"], 2)
        self.assertEqual(result["unmatched_rows"]["source"]["not_in_other_within_shared_stamp_span"], 1)
        self.assertEqual(result["unmatched_rows"]["source"]["not_in_other_outside_shared_stamp_span"], 1)

    def test_five_second_counts_and_signed_backlog(self):
        source = [source_row(STAMP+i*c.NS, i*c.NS, i*c.NS) for i in range(12)]
        unity = [unity_row(STAMP+i*c.NS, i*c.NS+500000000, i*c.NS+500000000) for i in range(12)]
        result, root = self.compare(source, unity)
        with (root/"out/bins.csv").open(newline="") as stream:
            bins = list(csv.DictReader(stream))
        self.assertEqual([int(b["source_count"]) for b in bins], [5, 5, 2])
        self.assertEqual(bins[-1]["source_entire_bin_inside_observed_span"], "False")
        self.assertEqual(result["source"]["relative_backlog_last_s"], 0)
        self.assertEqual(result["unity_minus_source"]["median_s"], .5)

    def test_source_reset_and_utc_clock_change_keep_backlog_monotonic(self):
        source = [source_row(STAMP, 0, 0), source_row(STAMP+c.NS, 20*c.NS, c.NS),
                  source_row(STAMP-2*c.NS, 21*c.NS, 2*c.NS)]
        unity = [unity_row(STAMP, 200000000, 0), unity_row(STAMP+c.NS, 20200000000, c.NS)]
        result, _ = self.compare(source, unity)
        self.assertEqual(result["source_input"]["source_regressions"], 1)
        self.assertEqual(result["source"]["relative_backlog_last_s"], 0)
        self.assertEqual(result["source"]["utc_progress_minus_monotonic_progress"]["max_s"], 19)

    def test_nonoverlap_is_not_reported_as_packet_loss(self):
        result, _ = self.compare([source_row(STAMP, 0, 0)], [unity_row(STAMP+100*c.NS, 100*c.NS, 0)])
        self.assertEqual(result["comparison_status"], "no_unique_timestamp_overlap")
        self.assertIsNone(result["unity_minus_source"]["median_s"])
        self.assertIsNone(result["shared_stamp_span_start_ns"])

    def test_receipt_clock_reset_does_not_create_bogus_aggregate_rate(self):
        result, _ = self.compare(
            [source_row(STAMP+i*c.NS, i*c.NS, i*c.NS) for i in range(3)],
            [unity_row(STAMP+i*c.NS, i*c.NS, mono*c.NS) for i, mono in enumerate((0, 10, 5))])
        self.assertFalse(result["unity"]["receipt_clock_continuous"])
        self.assertIsNone(result["unity"]["arrival_hz_first_to_last"])
        self.assertEqual(result["unity"]["utc_progress_minus_monotonic_progress"]["count"], 0)

    def test_invalid_rows_skipped_and_no_overwrite(self):
        source = [source_row(0, 0, 0), source_row(STAMP, 0, 0)]
        source[0]["ros_stamp_nsec"] = 1000000000
        result, root = self.compare(source, [unity_row(STAMP, 1, 1)])
        self.assertEqual(result["source_input"]["skipped_rows"]["invalid_timestamp"], 1)
        with self.assertRaises(FileExistsError):
            c.compare(root/"source.csv", root/"capture", root/"out")

    def test_actual_local_schema_read_only(self):
        for path, role in ((ROOT/"SupportPackage/FeedbackTransportAudit/source_baseline.csv", "source"),
                           (ROOT/"KATCaptures/KAT_MiniS_20261013_210331_136_ebf172/control_odometry.csv", "unity"),
                           (ROOT/"SupportPackage/FeedbackTransportAudit/endpoint_baseline/feedback.csv", "source")):
            if not path.exists():
                continue
            rows, info = c.load_csv(path, "/odometry/filtered", role)
            self.assertGreater(len(rows), 0)
            self.assertEqual(info["skipped_rows"].get("invalid_timestamp", 0), 0)


if __name__ == "__main__":
    unittest.main(verbosity=2)
