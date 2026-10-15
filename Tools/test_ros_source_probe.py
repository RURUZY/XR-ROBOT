"""Offline protocol and timing tests. All servers bind only to 127.0.0.1."""

import csv
import importlib.util
import json
from pathlib import Path
import socket
import struct
import tempfile
import threading
import time
import unittest
from xmlrpc.server import SimpleXMLRPCServer

spec = importlib.util.spec_from_file_location("ros_source_probe", Path(__file__).with_name("ros_source_probe.py"))
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


def odometry(seq):
    def ros_string(value):
        data = value.encode()
        return struct.pack("<I", len(data)) + data
    return (struct.pack("<III", seq, 1_700_000_000 + seq // 50, (seq % 50) * 20_000_000)
            + ros_string("odom") + ros_string("base_link") + struct.pack("<85d", *([0.0] * 85)))


class FakeRos:
    def __init__(self, message_type="nav_msgs/Odometry", send_messages=True):
        self.message_type = message_type
        self.send_messages = send_messages
        self.calls = []
        self.header = None
        self.errors = []
        self.stop = threading.Event()
        self.tcp = socket.socket()
        self.tcp.bind(("127.0.0.1", 0))
        self.tcp.listen(1)
        self.tcp.settimeout(0.1)
        self.node = SimpleXMLRPCServer(("127.0.0.1", 0), logRequests=False, allow_none=True)
        self.master = SimpleXMLRPCServer(("127.0.0.1", 0), logRequests=False, allow_none=True)
        self.node_uri = f"http://127.0.0.1:{self.node.server_address[1]}"
        self.master_uri = f"http://127.0.0.1:{self.master.server_address[1]}"
        self.master.register_function(self.lookup_node, "lookupNode")
        self.node.register_function(self.request_topic, "requestTopic")

    def lookup_node(self, caller, node):
        self.calls.append(("lookupNode", caller, node))
        return [1, "ok", self.node_uri]

    def request_topic(self, caller, topic, protocols):
        self.calls.append(("requestTopic", caller, topic, protocols))
        return [1, "ok", ["TCPROS", "127.0.0.1", self.tcp.getsockname()[1]]]

    def serve_tcp(self):
        try:
            while not self.stop.is_set():
                try:
                    connection, _ = self.tcp.accept()
                    break
                except socket.timeout:
                    continue
            else:
                return
            with connection:
                self.header = probe.decode_header(probe.read_frame(connection, time.monotonic() + 2))
                header = probe.encode_header({"type": self.message_type, "md5sum": "test", "callerid": "/fake_ekf"})
                # Deliberately split the framing and header across TCP writes.
                for part in (header[:2], header[2:7], header[7:]):
                    connection.sendall(part)
                    time.sleep(.003)
                seq = 0
                target = time.monotonic()
                while not self.stop.is_set():
                    if self.send_messages:
                        body = odometry(seq)
                        packet = struct.pack("<I", len(body)) + body
                        connection.sendall(packet[:3])
                        connection.sendall(packet[3:17])
                        connection.sendall(packet[17:])
                        seq += 1
                    target += .02
                    self.stop.wait(max(0, target - time.monotonic()))
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass  # Probe correctly closes its subscription at the time limit.
        except Exception as error:
            self.errors.append(error)

    def __enter__(self):
        self.threads = [threading.Thread(target=lambda server=server: server.serve_forever(.01), daemon=True)
                        for server in (self.master, self.node)]
        self.threads.append(threading.Thread(target=self.serve_tcp, daemon=True))
        for thread in self.threads:
            thread.start()
        return self

    def __exit__(self, *_):
        self.stop.set()
        for server in (self.master, self.node):
            server.shutdown()
            server.server_close()
        self.tcp.close()
        for thread in self.threads:
            thread.join(1)


class ProbeTests(unittest.TestCase):
    def test_direct_subscription_fragmentation_timestamps_and_outputs(self):
        with tempfile.TemporaryDirectory() as directory, FakeRos() as ros:
            prefix = Path(directory) / "direct"
            result = probe.run_probe(ros.master_uri, "/ekf_localization", "/odometry/filtered", .7, prefix)
            self.assertIsNone(result["error"])
            self.assertEqual(result["end_reason"], "time_budget_reached")
            self.assertGreaterEqual(result["messages_received"], 15)
            self.assertAlmostEqual(result["source_interval_rate_hz"], 50.0)
            self.assertLess(result["elapsed_seconds"], 1.0)
            self.assertEqual([call[0] for call in ros.calls], ["lookupNode", "requestTopic"])
            self.assertEqual(ros.calls[1][3], [["TCPROS"]])
            self.assertEqual(ros.header["topic"], "/odometry/filtered")
            self.assertEqual(ros.header["type"], "nav_msgs/Odometry")
            self.assertEqual(ros.header["md5sum"], "*")
            self.assertEqual(ros.header["tcp_nodelay"], "1")
            self.assertEqual(ros.errors, [])
            with Path(str(prefix) + ".csv").open(newline="", encoding="utf-8") as stream:
                rows = list(csv.DictReader(stream))
            self.assertEqual(len(rows), result["messages_received"])
            self.assertEqual(int(rows[1]["ros_stamp_ns"]) - int(rows[0]["ros_stamp_ns"]), 20_000_000)
            self.assertEqual(json.loads(Path(str(prefix) + ".json").read_text())["read_only"], True)
            with self.assertRaises(FileExistsError):
                probe.run_probe(ros.master_uri, "/ekf_localization", "/odometry/filtered", .1, prefix)

    def test_silent_publisher_still_obeys_total_budget(self):
        with tempfile.TemporaryDirectory() as directory, FakeRos(send_messages=False) as ros:
            result = probe.run_probe(ros.master_uri, "/ekf_localization", "/odometry/filtered", .2,
                                     Path(directory) / "silent")
            self.assertEqual(result["end_reason"], "time_budget_reached")
            self.assertEqual(result["messages_received"], 0)
            self.assertLess(result["elapsed_seconds"], .5)

    def test_wrong_type_is_rejected_before_decoding_messages(self):
        with tempfile.TemporaryDirectory() as directory, FakeRos(message_type="geometry_msgs/Twist") as ros:
            result = probe.run_probe(ros.master_uri, "/ekf_localization", "/odometry/filtered", .5,
                                     Path(directory) / "wrong")
            self.assertEqual(result["end_reason"], "error")
            self.assertIn("publisher type must be nav_msgs/Odometry", result["error"])
            self.assertEqual(result["messages_received"], 0)

    def test_clock_offset_backlog_duplicate_catchup_and_reset(self):
        stats = probe.TimingStats()
        def observe(index, source_ns, receipt_ns):
            return stats.observe(index, source_ns // 1_000_000_000, source_ns % 1_000_000_000,
                                 receipt_ns, 0, 10_000_000_000, 700)
        source = 1_700_000_000_000_000_000
        self.assertEqual(observe(0, source, 10_000_000_000)["relative_backlog_s"], 0)
        self.assertAlmostEqual(observe(1, source + 20_000_000, 10_620_000_000)["relative_backlog_s"], .6)
        self.assertAlmostEqual(observe(2, source + 20_000_000, 10_720_000_000)["relative_backlog_s"], .7)
        self.assertEqual(observe(3, source + 740_000_000, 10_740_000_000)["relative_backlog_s"], 0)
        self.assertEqual(observe(4, 1_000_000_000, 10_750_000_000)["event"], "source_reset")
        self.assertEqual(stats.summary()["source_clock_resets"], 1)
        self.assertEqual(stats.summary()["duplicate_source_stamps"], 1)
        self.assertAlmostEqual(stats.summary()["max_relative_backlog_s"], .7)
        self.assertEqual(observe(5, 0, 10_760_000_000)["event"], "invalid_stamp")

    def test_rejects_overlong_budget_without_network(self):
        with self.assertRaises(ValueError):
            probe.run_probe("http://127.0.0.1:1", "/ekf_localization", "/odometry/filtered", 61, "unused")

    def test_stop_file_saves_partial_capture(self):
        with tempfile.TemporaryDirectory() as directory, FakeRos() as ros:
            stop_file = Path(directory) / "stop.flag"
            timer = threading.Timer(.25, lambda: stop_file.write_text("owner_stop"))
            timer.start()
            try:
                result = probe.run_probe(ros.master_uri, "/ekf_localization", "/odometry/filtered", 60,
                                         Path(directory) / "stopped", stop_file)
            finally:
                timer.cancel()
                timer.join()
            self.assertEqual(result["end_reason"], "stop_requested")
            self.assertGreater(result["messages_received"], 0)
            self.assertLess(result["elapsed_seconds"], .6)
            self.assertIsNone(result["error"])


if __name__ == "__main__":
    unittest.main()
