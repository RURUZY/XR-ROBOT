# Read-only feedback transport probe

`Tools/ros_feedback_probe.py` uses Python standard library only. It never opens Unity,
registers publishers, sends motion commands, or calls services. Its only outbound
frames are a bootstrap empty frame, `__subscribe` for the fixed feedback topics,
and optionally repeated empty eight-byte keepalives. It does not reconnect.

Protocol was checked against the installed ROS-TCP-Connector `c27f00c6cf`:
`Runtime/TcpConnector/SysCommand.cs` and `ROSConnection.cs` (v0.7.0). It requires a
v0.7.* handshake with metadata protocol `ROS1` before subscribing. ROS1 Header is
`uint32 seq, uint32 sec, uint32 nsec, string frame_id`. Full fixed Odometry and
TwistStamped payload lengths are validated; only fields needed for audit are decoded.

Run from the project root; use a **new output directory** for every run:

```powershell
python Tools/ros_feedback_probe.py --host 192.168.2.36 --port 10000 --duration 20 --keepalive-hz 0 --nodelay true --cmd-vel-out --outdir SupportPackage/FeedbackTransportAudit/run_0hz
```

For controlled comparisons change only `--keepalive-hz` to `20` or `200`, and the
output directory. `--nodelay false` disables TCP_NODELAY on this client only; it
does not change the endpoint's socket setting. Keepalive 0 sends no periodic
traffic after its initial bootstrap frame and feedback subscriptions. Actual
keepalive rate is recorded; delayed scheduling does not generate catch-up bursts.
The optional `--cmd-vel-out` is a subscription to
`/husky_velocity_controller/cmd_vel_out` (`geometry_msgs/TwistStamped`), never a
publication. Default subscription is `/odometry/filtered` (`nav_msgs/Odometry`).

Output: `feedback.csv` records complete socket-frame arrival monotonic/UTC times,
ROS timestamps, yaw/twist, arrival/source increments, and backlog change.
`summary.json` records protocol, counts by topic, arrival/source frequency, largest
gaps, keepalive counts/rate, errors and exit reason. Console output is one bounded
summary. Receipt measurements occur before CSV writing/decoding the next message;
the probe cannot measure OS/network arrival timestamps or separate its own scheduling
delay from endpoint/network buffering.

`backlog_delta_s = (receipt - first_receipt) - (source_stamp - first_source_stamp)`
within a nondecreasing source-time epoch. A constant robot/host clock offset cancels.
Positive growth means receipt time progressed more than source time; it can result
from queued feedback, paused source time, or clock-rate changes. It does not locate
the cause or prove physical motion. Source clock regression starts a new epoch;
zero stamps have no backlog estimate. `utc_minus_source_s` contains clock offset
and **must not be reported as measured one-way latency**. Source frequency uses
positive timestamp intervals, excluding duplicates and regression intervals.

Duration is restricted to `(0, 60]` seconds after subscriptions, plus at most a
3-second connection and 5-second handshake phase (socket polling granularity is
0.1 seconds). Numeric IPv4/IPv6 only avoids unbounded DNS lookup. Frame limits:
1024-byte topic, 1 MiB payload, 100,000 frames; at most 20 system texts (512 bytes
each) enter the summary. Existing output directories are rejected. Ctrl+C closes
the socket and writes partial evidence; protocol/socket errors exit nonzero.

Offline validation (literal `127.0.0.1` only; temporary outputs):

```powershell
python SupportPackage/FeedbackTransportAudit/test_ros_feedback_probe.py
```

These tests verify fragmented frames, the three send rates, both feedback layouts,
ROS2 rejection before subscription, oversized lengths, deadline with an incomplete
frame, outbound command rejection, source reset and clock-offset cancellation.
They do not validate the real endpoint, physical robot, or network performance.

## Compare simultaneous source-probe and Unity captures offline

```powershell
python Tools/compare_feedback_paths.py --source SupportPackage/FeedbackTransportAudit/source_during_play.csv --unity KATCaptures/REPLACE_WITH_CAPTURE_DIRECTORY --outdir SupportPackage/FeedbackTransportAudit/compare_during_play
```

The Unity input can be a capture directory or its `control_odometry.csv`. Source
input supports the direct ROS source probe's real CSV schema and the endpoint
probe's `feedback.csv`. The selected topic defaults to `/odometry/filtered`.
This script only reads files and creates a new report directory; it never connects
to a network or changes Unity/control settings.

- `matched.csv`: exact integer ROS sec/nsec matches, both host UTC receipt times,
  signed `unity_minus_source_s`, and each path's independently based backlog.
- `unmatched.csv`: every excluded row, distinguishing duplicate ambiguity and
  observations absent within/outside the shared numeric source timestamp span.
- `bins.csv`: common 5-second UTC bins with counts, nominal rates, partial-bin
  flags, backlog first/last/max, and matched delay statistics. `--bin-seconds`
  changes the default width.
- `summary.json`: delay min/median/nearest-rank p95/max, gaps, missing/duplicate
  counts, source/receipt regressions and wall-clock versus monotonic progression.

Both recordings must originate on the same PC for the direct UTC difference.
The result is the difference between two complete processing paths, **not** a
measurement that isolates Unity's internal queue. Negative differences are retained.
No clock-offset assumption between PC and robot is required. Wall-clock changes
between the two receipts remain a limitation. Each path's relative backlog is
recomputed from monotonic progression against its own first sample per epoch; a
source or receipt clock regression starts a new epoch. Backlog can be negative
when an initially delayed stream catches up. Duplicate source timestamp keys are
excluded instead of paired arbitrarily. Missing observations do not prove network
packet loss, and nonoverlapping captures produce `no_unique_timestamp_overlap`.

Offline comparison tests:

```powershell
python SupportPackage/FeedbackTransportAudit/test_compare_feedback_paths.py
```
