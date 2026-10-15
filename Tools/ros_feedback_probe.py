#!/usr/bin/env python3
"""Bounded, subscription-only ROS-TCP-Endpoint v0.7 / ROS1 timing probe.

Outbound allowlist: __subscribe for the two fixed feedback topics; 8 zero bytes.
No publishers, motion commands, services, reconnects, or third-party packages.
Timestamps measure complete socket reads, NOT physical robot execution times.
"""
import argparse
import csv
import datetime as dt
import ipaddress
import json
import math
import pathlib
import socket
import statistics
import struct
import sys
import threading
import time

TOPICS = {
    "/odometry/filtered": "nav_msgs/Odometry",
    "/husky_velocity_controller/cmd_vel_out": "geometry_msgs/TwistStamped",
}
MAX_TOPIC = 1024
MAX_PAYLOAD = 1024 * 1024
MAX_FRAMES = 100000
KEEPALIVE = bytes(8)
FIELDS = ["topic", "message_type", "payload_bytes", "receipt_monotonic_ns",
          "receipt_utc_ns", "elapsed_s", "seq", "stamp_sec", "stamp_nsec",
          "stamp_s", "arrival_dt_s", "source_dt_s", "source_epoch",
          "backlog_delta_s", "utc_minus_source_s", "frame_id", "child_frame_id",
          "yaw_deg", "linear_x", "angular_z"]


class ProtocolError(Exception):
    pass


class Deadline(Exception):
    pass


def frame(topic, payload):
    name = topic.encode("utf-8")
    if len(name) > MAX_TOPIC or len(payload) > MAX_PAYLOAD:
        raise ProtocolError("outbound frame too large")
    return struct.pack("<I", len(name)) + name + struct.pack("<I", len(payload)) + payload


def send_readonly(sock, packet):
    """All production sends pass this allowlist, including keepalives."""
    if packet != KEEPALIVE:
        expected = [frame("__subscribe", json.dumps(
            {"topic": topic, "message_name": kind}, separators=(",", ":")).encode())
            for topic, kind in TOPICS.items()]
        if packet not in expected:
            raise ProtocolError("outbound packet is not an allowed feedback subscription")
    sock.sendall(packet)


def read_exact(sock, count, deadline):
    data = bytearray()
    while len(data) < count:
        if time.monotonic() >= deadline:
            raise Deadline()
        try:
            part = sock.recv(count - len(data))
        except socket.timeout:
            continue
        if not part:
            raise EOFError("peer closed socket")
        data.extend(part)
    return bytes(data)


def read_frame(sock, deadline):
    name_len = struct.unpack("<I", read_exact(sock, 4, deadline))[0]
    if name_len > MAX_TOPIC:
        raise ProtocolError("topic length exceeds 1024 bytes")
    name = read_exact(sock, name_len, deadline).decode("utf-8", errors="strict")
    payload_len = struct.unpack("<I", read_exact(sock, 4, deadline))[0]
    if payload_len > MAX_PAYLOAD:
        raise ProtocolError("payload length exceeds 1 MiB")
    if not name and payload_len:
        raise ProtocolError("empty topic with nonempty payload")
    payload = read_exact(sock, payload_len, deadline)
    mono, utc = time.monotonic_ns(), time.time_ns()
    return name, payload, mono, utc


def validate_handshake(name, payload):
    if name != "__handshake":
        raise ProtocolError("first inbound frame is not __handshake")
    data = json.loads(payload.decode("utf-8"))
    version = data.get("version", "")
    metadata = json.loads(data.get("metadata", "{}"))
    if not isinstance(version, str) or not version.startswith("v0.7."):
        raise ProtocolError("unsupported endpoint version (requires v0.7.*)")
    if metadata.get("protocol") != "ROS1":
        raise ProtocolError("endpoint handshake is not ROS1")
    return {"version": version, "protocol": "ROS1"}


def ros_string(payload, offset):
    if offset + 4 > len(payload):
        raise ProtocolError("truncated ROS string length")
    length = struct.unpack_from("<I", payload, offset)[0]
    offset += 4
    if length > 4096 or offset + length > len(payload):
        raise ProtocolError("invalid ROS frame_id length")
    return payload[offset:offset + length].decode("utf-8", errors="strict"), offset + length


def decode_feedback(topic, payload):
    if len(payload) < 16:
        raise ProtocolError("truncated ROS1 header")
    seq, sec, nsec = struct.unpack_from("<III", payload)
    if nsec >= 1000000000:
        raise ProtocolError("ROS1 nanoseconds outside [0, 1e9)")
    frame_id, offset = ros_string(payload, 12)
    result = {"seq": seq, "stamp_sec": sec, "stamp_nsec": nsec,
              "stamp_s": f"{sec}.{nsec:09d}", "frame_id": frame_id,
              "child_frame_id": "", "yaw_deg": ""}
    if topic == "/odometry/filtered":
        child, offset = ros_string(payload, offset)
        if len(payload) != offset + 680:
            raise ProtocolError("Odometry payload size does not match ROS1 layout")
        x, y, z, w = struct.unpack_from("<4d", payload, offset + 24)
        norm = x*x + y*y + z*z + w*w
        if math.isfinite(norm) and norm > 1e-12:
            result["yaw_deg"] = math.degrees(math.atan2(2*(w*z+x*y), norm-2*(y*y+z*z)))
        result["child_frame_id"] = child
        twist_offset = offset + 56 + 288
    else:
        if len(payload) != offset + 48:
            raise ProtocolError("TwistStamped payload size does not match ROS1 layout")
        twist_offset = offset
    twist = struct.unpack_from("<6d", payload, twist_offset)
    result.update(linear_x=twist[0], angular_z=twist[5])
    return result


class TopicStats:
    def __init__(self):
        self.count = 0
        self.epoch = 0
        self.first = self.last = None
        self.last_stamp = self.base_mono = self.base_stamp = None
        self.gaps = []
        self.source_steps = []
        self.backlogs = []
        self.duplicate_stamps = 0
        self.zero_stamps = 0

    def observe(self, mono, stamp):
        arrival_dt = (mono-self.last)/1e9 if self.last is not None else None
        source_dt = (stamp-self.last_stamp)/1e9 if self.last_stamp is not None and stamp else None
        if arrival_dt is not None:
            self.gaps.append(arrival_dt)
        if stamp == 0:
            self.zero_stamps += 1
            self.last_stamp = self.base_mono = self.base_stamp = None
        else:
            if source_dt is not None and source_dt < 0:
                self.epoch += 1
                self.base_mono = self.base_stamp = None
            if source_dt == 0:
                self.duplicate_stamps += 1
            if source_dt is not None and source_dt > 0:
                self.source_steps.append(source_dt)
            if self.base_mono is None:
                self.base_mono, self.base_stamp = mono, stamp
            self.last_stamp = stamp
        backlog = ((mono-self.base_mono)-(stamp-self.base_stamp))/1e9 if stamp else None
        if backlog is not None:
            self.backlogs.append(backlog)
        self.count += 1
        if self.first is None:
            self.first = mono
        self.last = mono
        return arrival_dt, source_dt, self.epoch, backlog

    def summary(self, duration):
        span = (self.last-self.first)/1e9 if self.count > 1 else 0
        return {"count": self.count, "count_per_observation_second": self.count/duration if duration else None,
                "arrival_hz_first_to_last": (self.count-1)/span if span else None,
                "source_hz_positive_intervals": len(self.source_steps)/sum(self.source_steps) if self.source_steps else None,
                "source_time_regressions": self.epoch, "duplicate_source_stamps": self.duplicate_stamps,
                "zero_source_stamps": self.zero_stamps, "max_arrival_gap_s": max(self.gaps, default=None),
                "median_arrival_gap_s": statistics.median(self.gaps) if self.gaps else None,
                "max_source_gap_s": max(self.source_steps, default=None),
                "backlog_delta_last_s": self.backlogs[-1] if self.backlogs else None,
                "backlog_delta_min_s": min(self.backlogs, default=None),
                "backlog_delta_max_s": max(self.backlogs, default=None)}


def probe(host, port, duration, keepalive_hz, nodelay, outdir, cmd_vel_out=False):
    ipaddress.ip_address(host)  # Numeric IP only: avoids an unbounded DNS lookup.
    if not 0 < duration <= 60 or keepalive_hz not in (0, 20, 200) or not 1 <= port <= 65535:
        raise ValueError("duration must be (0, 60], keepalive-hz 0/20/200, port 1..65535")
    outdir = pathlib.Path(outdir)
    outdir.mkdir(parents=True, exist_ok=False)
    chosen = list(TOPICS) if cmd_vel_out else ["/odometry/filtered"]
    stats = {topic: TopicStats() for topic in chosen}
    summary = {"host": host, "port": port, "duration_requested_s": duration,
               "keepalive_requested_hz": keepalive_hz, "tcp_nodelay": nodelay,
               "subscriptions": {t: TOPICS[t] for t in chosen}, "handshake": None,
               "outbound_subscriptions": 0, "outbound_initial_keepalives": 0,
               "outbound_periodic_keepalives": 0, "outbound_motion_messages": 0,
               "frames_received": 0, "inbound_topic_counts": {}, "system_messages": [],
               "stop_reason": "not_started", "errors": [],
               "notes": ["Receipt timestamps are taken immediately after a complete socket frame read.",
                         "utc_minus_source_s includes host/robot clock offset: not a measured one-way latency.",
                         "backlog_delta_s=(receipt_elapsed-source_elapsed) within each monotonic source epoch; constant clock offset cancels.",
                         "Clock rate changes/source pauses also affect backlog_delta; it does not locate the queue or prove robot motion.",
                         "One initial empty keepalive bootstraps every connection, including keepalive-hz=0.",
                         "Subscriptions modify only endpoint feedback registrations; no publishers, services, or motion sends."]}
    sock = None
    sender = None
    stop = threading.Event()
    started = None
    end = None
    start_utc = time.time_ns()
    summary["start_utc_ns"] = start_utc
    try:
        with (outdir / "feedback.csv").open("x", newline="", encoding="utf-8") as stream:
            writer = csv.DictWriter(stream, fieldnames=FIELDS)
            writer.writeheader()
            sock = socket.create_connection((host, port), timeout=3)
            sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, int(nodelay))
            sock.settimeout(0.1)
            send_readonly(sock, KEEPALIVE)
            summary["outbound_initial_keepalives"] = 1
            name, payload, _, _ = read_frame(sock, time.monotonic()+5)
            summary["handshake"] = validate_handshake(name, payload)
            for topic in chosen:
                send_readonly(sock, frame("__subscribe", json.dumps(
                    {"topic": topic, "message_name": TOPICS[topic]}, separators=(",", ":")).encode()))
                summary["outbound_subscriptions"] += 1
            started = time.monotonic_ns()
            deadline = started/1e9 + duration

            def keepalives():
                interval = 1/keepalive_hz
                next_send = time.monotonic()+interval
                while not stop.wait(max(0, next_send-time.monotonic())):
                    if time.monotonic() >= deadline:
                        return
                    try:
                        send_readonly(sock, KEEPALIVE)
                        summary["outbound_periodic_keepalives"] += 1
                    except OSError as exc:
                        summary["errors"].append("keepalive: " + str(exc)[:300])
                        stop.set()
                        return
                    # No burst catch-up if this thread was delayed.
                    next_send = time.monotonic()+interval

            if keepalive_hz:
                sender = threading.Thread(target=keepalives, daemon=True, name="feedback-keepalive")
                sender.start()
            summary["stop_reason"] = "duration_complete"
            while time.monotonic() < deadline and not stop.is_set():
                try:
                    name, payload, mono, utc = read_frame(sock, deadline)
                except Deadline:
                    break
                summary["frames_received"] += 1
                counts = summary["inbound_topic_counts"]
                # Bound distinct unexpected names as well as total frame count.
                key = name if name in counts or len(counts) < 32 else "<other>"
                counts[key] = counts.get(key, 0)+1
                if summary["frames_received"] >= MAX_FRAMES:
                    summary["stop_reason"] = "frame_limit"
                    break
                if name in stats:
                    row = decode_feedback(name, payload)
                    stamp = row["stamp_sec"]*1000000000+row["stamp_nsec"]
                    adt, sdt, epoch, backlog = stats[name].observe(mono, stamp)
                    row.update(topic=name, message_type=TOPICS[name], payload_bytes=len(payload),
                               receipt_monotonic_ns=mono, receipt_utc_ns=utc, elapsed_s=(mono-started)/1e9,
                               arrival_dt_s=adt, source_dt_s=sdt, source_epoch=epoch, backlog_delta_s=backlog,
                               utc_minus_source_s=(utc-stamp)/1e9 if stamp else None)
                    writer.writerow(row)
                elif name.startswith("__") and len(summary["system_messages"]) < 20:
                    summary["system_messages"].append({"topic": name[:128], "text": payload[:512].decode("utf-8", errors="replace")})
            if stop.is_set():
                summary["stop_reason"] = "keepalive_error"
    except KeyboardInterrupt:
        summary["stop_reason"] = "interrupted"
    except (OSError, EOFError, ValueError, ProtocolError, Deadline, TypeError, AttributeError) as exc:
        summary["stop_reason"] = "error"
        summary["errors"].append(type(exc).__name__ + ": " + str(exc)[:500])
    finally:
        end = time.monotonic_ns()
        stop.set()
        if sock is not None:
            try:
                sock.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            sock.close()
        if sender is not None:
            sender.join(timeout=1)
        observed = (end-started)/1e9 if started else 0
        summary["observation_duration_s"] = observed
        summary["end_utc_ns"] = time.time_ns()
        summary["keepalive_actual_hz"] = summary["outbound_periodic_keepalives"]/observed if observed else 0
        summary["topics"] = {t: value.summary(observed) for t, value in stats.items()}
        with (outdir / "summary.json").open("x", encoding="utf-8") as stream:
            json.dump(summary, stream, indent=2, allow_nan=False)
            stream.write("\n")
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, help="Numeric IPv4/IPv6 address (no DNS)")
    parser.add_argument("--port", type=int, default=10000)
    parser.add_argument("--duration", type=float, default=20)
    parser.add_argument("--keepalive-hz", type=int, choices=(0, 20, 200), default=0)
    parser.add_argument("--nodelay", choices=("true", "false"), default="true")
    parser.add_argument("--cmd-vel-out", action="store_true", help="Also subscribe to stamped controller OUTPUT feedback")
    parser.add_argument("--outdir", type=pathlib.Path, default=None, help="New directory; existing directory is rejected")
    args = parser.parse_args()
    if not math.isfinite(args.duration) or not 0 < args.duration <= 60:
        parser.error("--duration must be finite and in (0, 60]")
    try:
        ipaddress.ip_address(args.host)
    except ValueError:
        parser.error("--host must be a numeric IP address")
    if not 1 <= args.port <= 65535:
        parser.error("--port must be 1..65535")
    outdir = args.outdir or pathlib.Path("SupportPackage/FeedbackTransportAudit") / (
        dt.datetime.now(dt.timezone.utc).strftime("probe_%Y%m%d_%H%M%S_%fZ"))
    try:
        result = probe(args.host, args.port, args.duration, args.keepalive_hz,
                       args.nodelay == "true", outdir, args.cmd_vel_out)
    except (OSError, ValueError) as exc:
        print("Probe setup failed: " + str(exc)[:500], file=sys.stderr)
        return 2
    print(json.dumps({"outdir": str(outdir.resolve()), "stop_reason": result["stop_reason"],
                      "handshake": result["handshake"], "topics": result["topics"],
                      "keepalive_actual_hz": result["keepalive_actual_hz"], "errors": result["errors"]}, indent=2))
    return 0 if result["stop_reason"] == "duration_complete" else 1


if __name__ == "__main__":
    raise SystemExit(main())
