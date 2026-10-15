"""Start-to-end drift of every pose topic in a ROS bag (ROS1 .bag or ROS2 folder).

The robot starts on a taped mark, drives a loop and stops near the mark. Each pose source
(FAST-LIVO2 odometry, wheel EKF /odometry/filtered, ...) reports where it thinks the robot ended
relative to the start. With the tape-measured real offset given via --measured, the difference
is the drift of that source.

Start/end poses are averaged over the first/last --window seconds, during which the robot must
stand still (the script warns when it did not).

Example:
    python slam_drift.py run.bag --measured 12 -5 3
      measured: end is 12 cm ahead of, 5 cm left of the start mark, heading 3 deg to the left
"""
import argparse
import math
import pathlib
import sys

# rosbags builds message names with PosixPath, which cannot be instantiated on Windows.
import rosbags.typesys.msg as _rosbags_msg
_rosbags_msg.PosixPath = pathlib.PurePosixPath

import numpy as np
from rosbags.highlevel import AnyReader

POSE_TYPES = {
    "nav_msgs/msg/Odometry": lambda m: m.pose.pose,
    "geometry_msgs/msg/PoseStamped": lambda m: m.pose,
    "geometry_msgs/msg/PoseWithCovarianceStamped": lambda m: m.pose.pose,
}


def yaw_of(q):
    return math.atan2(2 * (q.w * q.z + q.x * q.y), 1 - 2 * (q.y * q.y + q.z * q.z))


def read_tracks(bag, wanted):
    tracks = {}
    with AnyReader([pathlib.Path(bag)]) as reader:
        conns = [c for c in reader.connections
                 if c.msgtype in POSE_TYPES and (not wanted or c.topic in wanted)]
        for conn, stamp, raw in reader.messages(connections=conns):
            msg = reader.deserialize(raw, conn.msgtype)
            p = POSE_TYPES[conn.msgtype](msg)
            tracks.setdefault(conn.topic, []).append(
                (stamp * 1e-9, p.position.x, p.position.y, p.position.z, yaw_of(p.orientation)))
    return {t: np.array(v) for t, v in tracks.items() if len(v) >= 10}


def window_pose(track, from_end, seconds):
    t = track[:, 0]
    sel = t >= t[-1] - seconds if from_end else t <= t[0] + seconds
    w = track[sel]
    yaw = math.atan2(np.sin(w[:, 4]).mean(), np.cos(w[:, 4]).mean())
    spread = np.linalg.norm(w[:, 1:3] - w[:, 1:3].mean(axis=0), axis=1).max()
    return w[:, 1:4].mean(axis=0), yaw, spread


def analyse(topic, track, window, measured):
    p0, yaw0, s0 = window_pose(track, False, window)
    p1, yaw1, s1 = window_pose(track, True, window)
    # End pose expressed in the start pose's frame: x forward, y left (ROS convention).
    d = p1[:2] - p0[:2]
    c, s = math.cos(-yaw0), math.sin(-yaw0)
    fwd, left = c * d[0] - s * d[1], s * d[0] + c * d[1]
    dyaw = math.degrees(math.atan2(math.sin(yaw1 - yaw0), math.cos(yaw1 - yaw0)))
    steps = np.diff(track[:, 1:3], axis=0)
    length = np.linalg.norm(steps, axis=1).sum()
    r = dict(topic=topic, duration=track[-1, 0] - track[0, 0], length=length,
             fwd=fwd * 100, left=left * 100, dz=(p1[2] - p0[2]) * 100, dyaw=dyaw,
             still=max(s0, s1) * 100)
    if measured is not None:
        mf, ml, my = measured
        r["err_xy"] = math.hypot(r["fwd"] - mf, r["left"] - ml)
        r["err_yaw"] = (dyaw - my + 180) % 360 - 180
        r["err_pct"] = r["err_xy"] / max(length * 100, 1e-9) * 100
    return r


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("bag")
    ap.add_argument("--topic", action="append", help="only these topics (repeatable); default: all pose topics")
    ap.add_argument("--measured", nargs=3, type=float, metavar=("FWD_CM", "LEFT_CM", "YAW_DEG"),
                    help="tape-measured end pose relative to the start mark (left and CCW positive)")
    ap.add_argument("--window", type=float, default=3.0, help="seconds averaged at start and end (robot still)")
    ap.add_argument("--out", help="PNG path for the trajectory plot (default: next to the bag)")
    args = ap.parse_args()

    tracks = read_tracks(args.bag, set(args.topic or []))
    if not tracks:
        sys.exit("No Odometry/PoseStamped topics found. Use rosbag info to check topic names.")

    print(f"Bag: {args.bag}")
    if args.measured:
        print("Measured end vs start mark: fwd {:+.1f} cm, left {:+.1f} cm, yaw {:+.1f} deg".format(*args.measured))
    print()
    head = f"{'topic':38s} {'time s':>7s} {'path m':>7s} {'fwd cm':>8s} {'left cm':>8s} {'dz cm':>7s} {'dyaw':>7s}"
    if args.measured:
        head += f" {'ERR cm':>8s} {'ERR %':>6s} {'ERR yaw':>8s}"
    print(head)
    results = []
    for topic, track in sorted(tracks.items()):
        r = analyse(topic, track, args.window, args.measured)
        results.append(r)
        line = (f"{topic:38s} {r['duration']:7.1f} {r['length']:7.2f} {r['fwd']:+8.1f} {r['left']:+8.1f} "
                f"{r['dz']:+7.1f} {r['dyaw']:+7.1f}")
        if args.measured:
            line += f" {r['err_xy']:8.1f} {r['err_pct']:6.2f} {r['err_yaw']:+8.1f}"
        print(line)
    print()
    for r in results:
        if r["still"] > 5:
            print(f"WARNING {r['topic']}: moved {r['still']:.0f} cm inside the {args.window:.0f} s start/end window; "
                  "keep the robot still longer or reduce --window.")
    if not args.measured:
        print("fwd/left/dyaw = where each source thinks the robot ended relative to its start.")
        print("Add --measured FWD LEFT YAW (tape measure) to get the drift.")

    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    fig, ax = plt.subplots(figsize=(7, 7))
    for topic, track in sorted(tracks.items()):
        # Each source in its own start frame so they overlay: start at origin, initial heading +x.
        yaw0 = window_pose(track, False, args.window)[1]
        d = track[:, 1:3] - track[0, 1:3]
        c, s = math.cos(-yaw0), math.sin(-yaw0)
        x, y = c * d[:, 0] - s * d[:, 1], s * d[:, 0] + c * d[:, 1]
        ax.plot(x, y, label=topic, linewidth=1.2)
        ax.plot(x[-1], y[-1], "x", markersize=9)
    ax.plot(0, 0, "ko", label="start")
    if args.measured:
        ax.plot(args.measured[0] / 100, args.measured[1] / 100, "k*", markersize=13, label="measured end")
    ax.set_aspect("equal")
    ax.grid(True, alpha=0.3)
    ax.set_xlabel("forward at start (m)")
    ax.set_ylabel("left at start (m)")
    ax.set_title(pathlib.Path(args.bag).name)
    ax.legend(fontsize=8)
    out = args.out or str(pathlib.Path(args.bag).with_suffix("")) + "_trajectory.png"
    fig.savefig(out, dpi=150, bbox_inches="tight")
    print(f"Plot: {out}")


if __name__ == "__main__":
    main()
