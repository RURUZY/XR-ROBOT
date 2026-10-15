#!/usr/bin/env python3
"""Read-only ROS 1 Odometry timing probe, using only the Python standard library.

Bypasses ROS-TCP-Endpoint: lookupNode -> requestTopic -> TCPROS subscription.
Never registers a publisher, sends a ROS message, or requires a ROS installation.
The entire network session (including discovery) has a maximum 60-second budget.
Relative backlog is delay growth above the best observed offset; it does not
measure a constant initial delay or require synchronized ROS and PC clocks.

Protocol references (ROS authors' implementations, not copied implementation):
https://github.com/ros/ros_comm/blob/noetic-devel/clients/rospy/src/rospy/impl/tcpros_pubsub.py
https://github.com/ros/ros_comm/blob/noetic-devel/tools/rosgraph/src/rosgraph/network.py
https://github.com/ros/common_msgs/blob/noetic-devel/nav_msgs/msg/Odometry.msg
"""

import argparse
import csv
from datetime import datetime, timezone
import http.client
import json
import math
from pathlib import Path
import socket
import struct
import time
from urllib.parse import urlparse
import xmlrpc.client


MESSAGE_TYPE = "nav_msgs/Odometry"
MAX_FRAME_BYTES = 4 * 1024 * 1024
CSV_FIELDS = (
    "index", "receipt_elapsed_s", "receipt_monotonic_ns", "host_utc_ns",
    "seq", "ros_stamp_sec", "ros_stamp_nsec", "ros_stamp_ns",
    "source_delta_s", "receipt_delta_s", "relative_backlog_s",
    "source_epoch", "event", "message_bytes",
)


class DeadlineReached(TimeoutError):
    pass


class StopRequested(Exception):
    pass


class ProbeDeadline:
    def __init__(self, seconds, stop_file=None):
        self.expires = time.monotonic() + seconds
        self.stop_file = Path(stop_file) if stop_file else None

    def remaining(self):
        if self.stop_file is not None and self.stop_file.exists():
            raise StopRequested("owner requested stop")
        return self.expires - time.monotonic()


def remaining(deadline):
    value = deadline.remaining() if isinstance(deadline, ProbeDeadline) else deadline - time.monotonic()
    if value <= 0:
        raise DeadlineReached("probe time budget exhausted")
    return value


class DeadlineTransport(xmlrpc.client.Transport):
    def __init__(self, deadline):
        super().__init__()
        self.deadline = deadline

    def make_connection(self, host):
        # Each discovery operation gets a bounded connection; no global timeout.
        self.close()
        connection = http.client.HTTPConnection(host, timeout=min(5.0, remaining(self.deadline)))
        self._connection = (host, connection)
        return connection


def ros_rpc(uri, method, args, deadline):
    parsed = urlparse(uri)
    if parsed.scheme != "http" or not parsed.hostname:
        raise ValueError("ROS XML-RPC URI must be http://host:port/")
    with xmlrpc.client.ServerProxy(uri, transport=DeadlineTransport(deadline)) as proxy:
        response = getattr(proxy, method)(*args)
    if not isinstance(response, (list, tuple)) or len(response) != 3 or response[0] != 1:
        raise RuntimeError(f"{method} failed: {response!r}")
    return response[2]


def encode_header(fields):
    pieces = []
    for key, value in fields.items():
        field = f"{key}={value}".encode("utf-8")
        pieces.append(struct.pack("<I", len(field)) + field)
    body = b"".join(pieces)
    return struct.pack("<I", len(body)) + body


def decode_header(body):
    fields = {}
    offset = 0
    while offset < len(body):
        if offset + 4 > len(body):
            raise ValueError("truncated TCPROS header field length")
        length = struct.unpack_from("<I", body, offset)[0]
        offset += 4
        if length == 0 or offset + length > len(body):
            raise ValueError("invalid TCPROS header field length")
        field = body[offset:offset + length].decode("utf-8")
        offset += length
        key, separator, value = field.partition("=")
        if not separator or not key or key in fields:
            raise ValueError("invalid or duplicate TCPROS header field")
        fields[key] = value
    return fields


def read_exact(connection, length, deadline):
    chunks = bytearray()
    while len(chunks) < length:
        connection.settimeout(min(0.1, remaining(deadline)))
        try:
            part = connection.recv(length - len(chunks))
        except socket.timeout:
            continue
        if not part:
            raise EOFError(f"TCPROS stream closed ({len(chunks)}/{length} bytes read)")
        chunks.extend(part)
    return bytes(chunks)


def read_frame(connection, deadline):
    length = struct.unpack("<I", read_exact(connection, 4, deadline))[0]
    if length == 0 or length > MAX_FRAME_BYTES:
        raise ValueError(f"invalid TCPROS frame length: {length}")
    return read_exact(connection, length, deadline)


class TimingStats:
    def __init__(self):
        self.count = self.duplicates = self.resets = self.invalid_stamps = 0
        self.epoch = 0
        self.last_source_ns = self.last_receipt_ns = self.first_receipt_ns = None
        self.best_offset_ns = None
        self.positive_intervals = 0
        self.source_progress_ns = 0
        self.max_backlog_ns = self.last_backlog_ns = self.max_receipt_gap_ns = 0
        self.over_500ms = 0

    def observe(self, seq, sec, nsec, receipt_ns, utc_ns, start_ns, size):
        self.count += 1
        receipt_delta = None if self.last_receipt_ns is None else receipt_ns - self.last_receipt_ns
        if self.first_receipt_ns is None:
            self.first_receipt_ns = receipt_ns
        if receipt_delta is not None:
            self.max_receipt_gap_ns = max(self.max_receipt_gap_ns, receipt_delta)
        self.last_receipt_ns = receipt_ns
        source_ns = sec * 1_000_000_000 + nsec
        source_delta = None
        backlog = None
        event = "sample"
        if nsec >= 1_000_000_000 or source_ns <= 0:
            self.invalid_stamps += 1
            event = "invalid_stamp"
        else:
            if self.last_source_ns is not None:
                source_delta = source_ns - self.last_source_ns
                if source_delta < 0:
                    self.resets += 1
                    self.epoch += 1
                    self.best_offset_ns = None
                    event = "source_reset"
                elif source_delta == 0:
                    self.duplicates += 1
                    event = "duplicate_stamp"
                else:
                    self.positive_intervals += 1
                    self.source_progress_ns += source_delta
            offset = receipt_ns - source_ns
            self.best_offset_ns = offset if self.best_offset_ns is None else min(self.best_offset_ns, offset)
            backlog = offset - self.best_offset_ns
            self.last_backlog_ns = backlog
            self.max_backlog_ns = max(self.max_backlog_ns, backlog)
            self.over_500ms += backlog > 500_000_000
            self.last_source_ns = source_ns
        return dict(zip(CSV_FIELDS, (
            self.count, (receipt_ns - start_ns) / 1e9, receipt_ns, utc_ns,
            seq, sec, nsec, source_ns,
            "" if source_delta is None else source_delta / 1e9,
            "" if receipt_delta is None else receipt_delta / 1e9,
            "" if backlog is None else backlog / 1e9,
            self.epoch, event, size,
        )))

    def summary(self):
        receipt_span = 0 if self.count < 2 else self.last_receipt_ns - self.first_receipt_ns
        return {
            "messages_received": self.count,
            "receipt_rate_hz": (self.count - 1) * 1e9 / receipt_span if receipt_span > 0 else None,
            "source_interval_rate_hz": self.positive_intervals * 1e9 / self.source_progress_ns
            if self.source_progress_ns > 0 else None,
            "source_progress_s": self.source_progress_ns / 1e9,
            "duplicate_source_stamps": self.duplicates,
            "source_clock_resets": self.resets,
            "invalid_source_stamps": self.invalid_stamps,
            "max_relative_backlog_s": self.max_backlog_ns / 1e9,
            "last_relative_backlog_s": self.last_backlog_ns / 1e9,
            "messages_over_500ms_relative_backlog": self.over_500ms,
            "max_receipt_gap_s": self.max_receipt_gap_ns / 1e9,
        }


def run_probe(master_uri, publisher_node, topic, seconds, output_prefix, stop_file=None):
    if not math.isfinite(seconds) or not 0 < seconds <= 60:
        raise ValueError("seconds must be greater than 0 and at most 60")
    if not topic.startswith("/") or not publisher_node.startswith("/"):
        raise ValueError("topic and publisher-node must be absolute ROS names")
    prefix = Path(output_prefix)
    csv_path, json_path = Path(str(prefix) + ".csv"), Path(str(prefix) + ".json")
    if csv_path.exists() or json_path.exists():
        raise FileExistsError("output already exists; use a new output-prefix")
    prefix.parent.mkdir(parents=True, exist_ok=True)
    start_ns = time.monotonic_ns()
    deadline = ProbeDeadline(seconds, stop_file)
    caller = f"/codex_readonly_odom_probe_{time.time_ns()}"
    stats = TimingStats()
    summary = {
        "format": "ROS source timing probe v1",
        "started_utc": datetime.now(timezone.utc).isoformat(),
        "master_uri": master_uri, "publisher_node": publisher_node, "topic": topic,
        "requested_seconds_including_discovery": seconds, "callerid": caller,
        "read_only": True, "registered_publisher": False,
        "csv_path": str(csv_path), "end_reason": "unknown", "error": None,
        "timing_note": "Relative backlog uses the best receiver-monotonic minus source-stamp offset. "
        "Constant initial delay is unknown; PC/ROS clocks need not be synchronized. "
        "Source rate describes received stamp intervals, not unobserved dropped publications.",
    }
    try:
        with csv_path.open("x", newline="", encoding="utf-8") as output:
            writer = csv.DictWriter(output, fieldnames=CSV_FIELDS)
            writer.writeheader()
            node_uri = ros_rpc(master_uri, "lookupNode", (caller, publisher_node), deadline)
            summary["publisher_xmlrpc_uri"] = node_uri
            protocol = ros_rpc(node_uri, "requestTopic", (caller, topic, [["TCPROS"]]), deadline)
            if not isinstance(protocol, (list, tuple)) or len(protocol) != 3 or protocol[0] != "TCPROS":
                raise RuntimeError(f"unexpected requestTopic response: {protocol!r}")
            host, port = protocol[1], int(protocol[2])
            summary["publisher_tcpros_address"] = [host, port]
            with socket.create_connection((host, port), timeout=min(5.0, remaining(deadline))) as connection:
                connection.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                connection.settimeout(min(5.0, remaining(deadline)))
                connection.sendall(encode_header({
                    "callerid": caller, "topic": topic, "type": MESSAGE_TYPE,
                    "md5sum": "*", "tcp_nodelay": "1",
                }))
                header = decode_header(read_frame(connection, deadline))
                summary["publisher_header"] = {key: value for key, value in header.items()
                                               if key != "message_definition"}
                if "error" in header:
                    raise RuntimeError("publisher rejected subscription: " + header["error"])
                if header.get("type") != MESSAGE_TYPE:
                    raise RuntimeError(f"publisher type must be {MESSAGE_TYPE}, got {header.get('type')!r}")
                while True:
                    body = read_frame(connection, deadline)
                    receipt_ns, utc_ns = time.monotonic_ns(), time.time_ns()
                    # Odometry begins with std_msgs/Header: uint32 seq, sec, nsec.
                    if len(body) < 12:
                        raise ValueError("Odometry frame too short for ROS Header")
                    seq, sec, nsec = struct.unpack_from("<III", body)
                    writer.writerow(stats.observe(seq, sec, nsec, receipt_ns, utc_ns, start_ns, len(body)))
    except DeadlineReached:
        summary["end_reason"] = "time_budget_reached"
    except StopRequested:
        summary["end_reason"] = "stop_requested"
    except KeyboardInterrupt:
        summary["end_reason"] = "interrupted"
    except Exception as error:
        summary["end_reason"] = "error"
        summary["error"] = f"{type(error).__name__}: {error}"
    finally:
        summary["elapsed_seconds"] = (time.monotonic_ns() - start_ns) / 1e9
        summary.update(stats.summary())
        with json_path.open("x", encoding="utf-8") as output:
            json.dump(summary, output, indent=2, allow_nan=False)
            output.write("\n")
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--master-uri", default="http://192.168.2.36:11311")
    parser.add_argument("--publisher-node", default="/ekf_localization")
    parser.add_argument("--topic", default="/odometry/filtered")
    parser.add_argument("--seconds", type=float, default=30.0, help="total network budget, >0 and <=60 (default: 30)")
    parser.add_argument("--output-prefix", required=True, help="new path prefix for .csv and .json; never overwrites")
    parser.add_argument("--stop-file", help="optional owner-created stop marker; cooperatively saves partial results")
    args = parser.parse_args()
    try:
        result = run_probe(args.master_uri, args.publisher_node, args.topic, args.seconds, args.output_prefix, args.stop_file)
    except (ValueError, FileExistsError) as error:
        parser.error(str(error))
    print(json.dumps(result, indent=2, allow_nan=False))
    return 1 if result["error"] or result["messages_received"] == 0 else 0


if __name__ == "__main__":
    raise SystemExit(main())
