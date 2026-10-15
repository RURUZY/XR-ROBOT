"""Offline/127.0.0.1-only tests. Never connects to the robot."""
import importlib.util
import json
import pathlib
import socket
import struct
import tempfile
import threading
import time
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("probe", ROOT / "Tools/ros_feedback_probe.py")
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)


def ros_string(value):
    raw = value.encode()
    return struct.pack("<I", len(raw)) + raw


def odometry(seq, stamp_ns):
    sec, nsec = divmod(stamp_ns, 10**9)
    return (struct.pack("<III", seq, sec, nsec) + ros_string("odom") + ros_string("base_link")
            + struct.pack("<7d", 0, 0, 0, 0, 0, 0, 1) + bytes(36*8)
            + struct.pack("<6d", .1, 0, 0, 0, 0, .2) + bytes(36*8))


class ProbeTests(unittest.TestCase):
    def run_endpoint(self, rate=0, nodelay=True, protocol="ROS1", bad_length=False,
                     silent=False, include_output=False):
        """Listener address is a literal loopback; all outputs live in a temp dir."""
        captured, server_errors = [], []
        listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        listener.bind(("127.0.0.1", 0))
        listener.listen(1)
        listener.settimeout(2)
        port = listener.getsockname()[1]

        def server():
            try:
                with listener:
                    conn, _ = listener.accept()
                    with conn:
                        conn.settimeout(.1)
                        conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                        captured.append(p.read_frame(conn, time.monotonic()+2)[:2])
                        handshake = json.dumps({"version": "v0.7.0", "metadata": json.dumps({"protocol": protocol})}).encode()
                        conn.sendall(p.frame("__handshake", handshake))
                        if protocol != "ROS1":
                            try:
                                captured.append(p.read_frame(conn, time.monotonic()+1)[:2])
                            except EOFError:
                                pass
                            return
                        for _ in range(2 if include_output else 1):
                            captured.append(p.read_frame(conn, time.monotonic()+2)[:2])
                        if bad_length:
                            conn.sendall(struct.pack("<I", p.MAX_TOPIC+1))
                        elif silent:
                            conn.sendall(b"\x01\x00")  # Incomplete length prefix until timeout.
                        else:
                            for i in range(8):
                                packet = p.frame("/odometry/filtered", odometry(i, 100000000000+i*2000000))
                                # Exercise fragmentation without relying on TCP packet boundaries.
                                conn.sendall(packet[:3])
                                conn.sendall(packet[3:11])
                                conn.sendall(packet[11:])
                                if include_output:
                                    stamped = struct.pack("<III", i, 100, i*2000000) + ros_string("base_link") + bytes(48)
                                    conn.sendall(p.frame("/husky_velocity_controller/cmd_vel_out", stamped))
                                time.sleep(.008)
                        while True:
                            try:
                                captured.append(p.read_frame(conn, time.monotonic()+2)[:2])
                            except EOFError:
                                break
            except Exception as exc:
                server_errors.append(repr(exc))

        thread = threading.Thread(target=server, daemon=True)
        thread.start()
        with tempfile.TemporaryDirectory(prefix="ros_probe_loopback_") as temp:
            result = p.probe("127.0.0.1", port, .25, rate, nodelay,
                             pathlib.Path(temp)/"result", include_output)
            saved = json.loads((pathlib.Path(temp)/"result/summary.json").read_text())
            rows = (pathlib.Path(temp)/"result/feedback.csv").read_text().splitlines()
        thread.join(2)
        self.assertFalse(thread.is_alive())
        self.assertEqual(server_errors, [])
        self.assertEqual(result, saved)
        for name, payload in captured:
            if name:
                self.assertEqual(name, "__subscribe")
                data = json.loads(payload)
                self.assertEqual(set(data), {"topic", "message_name"})
                self.assertIn(data["topic"], p.TOPICS)
                self.assertEqual(data["message_name"], p.TOPICS[data["topic"]])
            else:
                self.assertEqual(payload, b"")
        return result, captured, rows

    def test_loopback_0hz_and_fragmentation_and_backlog(self):
        result, wire, rows = self.run_endpoint()
        self.assertEqual(result["stop_reason"], "duration_complete")
        self.assertEqual(result["outbound_periodic_keepalives"], 0)
        self.assertEqual(len(wire), 2)  # One bootstrap empty frame + one subscription.
        stats = result["topics"]["/odometry/filtered"]
        self.assertEqual(stats["count"], 8)
        self.assertEqual(len(rows), 9)
        self.assertAlmostEqual(stats["source_hz_positive_intervals"], 500)
        self.assertGreater(stats["backlog_delta_last_s"], .025)

    def test_loopback_20hz_two_topics_nodelay_off(self):
        result, _, rows = self.run_endpoint(rate=20, nodelay=False, include_output=True)
        self.assertEqual(result["outbound_subscriptions"], 2)
        self.assertGreaterEqual(result["outbound_periodic_keepalives"], 3)
        self.assertEqual(result["topics"]["/husky_velocity_controller/cmd_vel_out"]["count"], 8)
        self.assertEqual(len(rows), 17)

    def test_loopback_200hz(self):
        result, _, _ = self.run_endpoint(rate=200)
        self.assertGreaterEqual(result["outbound_periodic_keepalives"], 15)
        self.assertLessEqual(result["outbound_periodic_keepalives"], 50)

    def test_reject_ros2_before_subscribe(self):
        result, wire, _ = self.run_endpoint(protocol="ROS2")
        self.assertEqual(result["stop_reason"], "error")
        self.assertEqual(result["outbound_subscriptions"], 0)
        self.assertEqual(wire, [("", b"")])

    def test_reject_oversize_frame(self):
        result, _, _ = self.run_endpoint(bad_length=True)
        self.assertEqual(result["stop_reason"], "error")
        self.assertIn("topic length", result["errors"][0])

    def test_partial_prefix_has_bounded_deadline(self):
        before = time.monotonic()
        result, _, _ = self.run_endpoint(silent=True)
        self.assertEqual(result["stop_reason"], "duration_complete")
        self.assertLess(time.monotonic()-before, 1.5)
        self.assertEqual(result["topics"]["/odometry/filtered"]["count"], 0)

    def test_outbound_allowlist_blocks_commands_and_services(self):
        class ForbiddenSocket:
            def sendall(self, _):
                raise AssertionError("must reject before socket send")
        for topic in ("/cmd_vel", "__publish", "__request", "__ros_service", "__remove_subscriber"):
            with self.assertRaises(p.ProtocolError):
                p.send_readonly(ForbiddenSocket(), p.frame(topic, b"{}"))
        with self.assertRaises(p.ProtocolError):
            p.send_readonly(ForbiddenSocket(), p.frame("__subscribe", b'{"topic":"/cmd_vel","message_name":"geometry_msgs/Twist"}'))

    def test_header_and_payload_validation(self):
        valid = odometry(7, 123456789012)
        row = p.decode_feedback("/odometry/filtered", valid)
        self.assertEqual(row["stamp_s"], "123.456789012")
        self.assertEqual(row["seq"], 7)
        self.assertEqual(row["yaw_deg"], 0)
        self.assertEqual(row["linear_x"], .1)
        for malformed in (b"", valid[:-1], valid+bytes(1), struct.pack("<III", 0, 1, 10**9)+valid[12:]):
            with self.assertRaises(p.ProtocolError):
                p.decode_feedback("/odometry/filtered", malformed)

    def test_source_epoch_and_constant_clock_offset(self):
        stats = p.TopicStats()
        self.assertEqual(stats.observe(1000000000, 999000000000)[3], 0)
        self.assertAlmostEqual(stats.observe(1100000000, 999020000000)[3], .08)
        result = stats.observe(1200000000, 1000000000)
        self.assertEqual(result[2:], (1, 0))
        self.assertIsNone(stats.observe(1300000000, 0)[3])


if __name__ == "__main__":
    unittest.main(verbosity=2)
