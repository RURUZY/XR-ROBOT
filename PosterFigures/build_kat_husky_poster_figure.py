import re
from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np


LOG = Path(r"C:\Users\huppk\AppData\Local\Unity\Editor\Editor.log")
OUT = Path(__file__).resolve().parent
RX = re.compile(
    r"RawFwd=(?P<raw>-?\d+\.\d+) PulseStep=(?P<pulse>True|False).*?"
    r"BodyRaw=(?P<body>-?\d+\.\d+) YawRate=(?P<yaw>-?\d+\.\d+)deg/s "
    r"Linear=(?P<linear>-?\d+\.\d+) Angular=(?P<angular>-?\d+\.\d+).*?"
    r"Robot=(?P<robot>-?\d+\.\d+) Fresh=(?P<fresh>True|False)"
)


def latest_run():
    samples = []
    previous_line = None
    runs = [[]]
    for line_number, line in enumerate(LOG.read_text(errors="replace").splitlines(), 1):
        match = RX.search(line)
        if not match:
            continue
        if previous_line is not None and line_number - previous_line > 400:
            runs.append([])
        runs[-1].append(match.groupdict())
        previous_line = line_number
    samples = max(runs, key=len)
    if not samples:
        raise RuntimeError("No KAT DIRECT samples found")
    fields = {key: np.array([float(row[key]) for row in samples])
              for key in ("raw", "body", "yaw", "linear", "angular", "robot")}
    fields["pulse"] = np.array([row["pulse"] == "True" for row in samples])
    fields["t"] = np.arange(len(samples)) * 0.05
    return fields


def shade_boolean(ax, t, state, color="#DDEAF4"):
    active = False
    start = 0.0
    for i, value in enumerate(state):
        if value and not active:
            start, active = t[i], True
        if active and (not value or i == len(state) - 1):
            end = t[i] if not value else t[i] + 0.05
            ax.axvspan(start, end, color=color, alpha=0.42, linewidth=0, zorder=0)
            active = False


d = latest_run()
t = d["t"]
robot_unwrapped = np.rad2deg(np.unwrap(np.deg2rad(d["robot"])))
robot_delta = robot_unwrapped - robot_unwrapped[0]
commanded_delta = np.cumsum(d["angular"] * 0.05) * 180.0 / np.pi
reverse_idx = np.flatnonzero(d["linear"] < -0.05)
reverse_start = t[reverse_idx[0]] if len(reverse_idx) else None

plt.rcParams.update({
    "font.family": "DejaVu Sans",
    "font.size": 10.5,
    "axes.titlesize": 12,
    "axes.labelsize": 10.5,
    "legend.fontsize": 9.5,
    "axes.linewidth": 0.8,
    "svg.fonttype": "none",
})

blue = "#1769AA"
orange = "#D97706"
green = "#178554"
purple = "#7353BA"
grey = "#667085"

fig, axes = plt.subplots(3, 1, figsize=(11.7, 8.3), sharex=True,
                         gridspec_kw={"hspace": 0.24})
fig.suptitle("KAT VR-to-Husky Teleoperation Response", fontsize=17, fontweight="bold", y=0.985)

# A — locomotion input and command
ax = axes[0]
shade_boolean(ax, t, d["pulse"])
raw_line = ax.plot(t, d["raw"], color=blue, lw=1.8, label="KAT forward input")[0]
ax.set_ylabel("KAT input")
ax.set_title("A   Forward locomotion mapping", loc="left", fontweight="bold")
ax2 = ax.twinx()
cmd_line = ax2.plot(t, d["linear"], color=orange, lw=2.2, label="Linear command")[0]
ax2.axhline(0.6, color=orange, ls="--", lw=0.9, alpha=0.65)
ax2.axhline(-0.2, color=orange, ls="--", lw=0.9, alpha=0.65)
ax2.set_ylabel("Linear velocity (m/s)")
if reverse_start is not None:
    ax.axvline(reverse_start, color=grey, ls=":", lw=1.2)
    ax.annotate("Reverse mode activated", xy=(reverse_start, 0.93),
                xycoords=("data", "axes fraction"), xytext=(8, 0),
                textcoords="offset points", color=grey, va="top")
ax.legend([raw_line, cmd_line], ["KAT forward input", "Linear command"],
          loc="upper left", frameon=False, ncol=2)
ax.text(0.995, 0.95, "Blue shading: step signal active", transform=ax.transAxes,
        ha="right", va="top", color=grey, fontsize=9)

# B — turning input and command
ax = axes[1]
yaw_line = ax.plot(t, d["yaw"], color=purple, lw=1.4, label="Filtered body yaw rate")[0]
ax.set_ylabel("Yaw rate (deg/s)")
ax.set_yscale("symlog", linthresh=20, linscale=0.8)
ax.set_ylim(-1000, 1000)
ax.set_title("B   Turning-rate mapping", loc="left", fontweight="bold")
ax2 = ax.twinx()
turn_line = ax2.plot(t, d["angular"], color=green, lw=2.1, label="Angular command")[0]
ax2.axhline(0.6, color=green, ls="--", lw=0.9, alpha=0.65)
ax2.axhline(-0.6, color=green, ls="--", lw=0.9, alpha=0.65)
ax2.set_ylim(-0.72, 0.72)
ax2.set_ylabel("Angular velocity (rad/s)")
ax.legend([yaw_line, turn_line], ["Filtered body yaw rate", "Angular command"],
          loc="upper left", frameon=False, ncol=2)
ax.text(0.995, 0.06, "Symmetric-log scale preserves large yaw spikes",
        transform=ax.transAxes, ha="right", va="bottom", color=grey, fontsize=9)

# C — response
ax = axes[2]
ax.plot(t, commanded_delta, color=green, lw=2.0, label="Integrated angular command")
ax.plot(t, robot_delta, color=blue, lw=2.2, label="Robot odometry yaw")
ax.axhline(0, color=grey, lw=0.8)
ax.set_ylabel("Relative yaw (deg)")
ax.set_xlabel("Elapsed test time (s)")
ax.set_title("C   Robot heading response", loc="left", fontweight="bold")
ax.legend(loc="upper left", frameon=False, ncol=2)

for ax in axes:
    ax.grid(True, axis="y", color="#D0D5DD", lw=0.7, alpha=0.7)
    ax.spines["top"].set_visible(False)
    ax.spines["right"].set_visible(False)
    ax.set_xlim(t[0], t[-1])
    ax.tick_params(direction="out", length=3, width=0.8)

fig.text(0.01, 0.008,
         f"Latest continuous Unity Console segment: {len(t)} samples at approximately 20 Hz ({t[-1]:.1f} s). "
         "Dashed lines indicate configured command limits.",
         color=grey, fontsize=8.8)
fig.subplots_adjust(left=0.095, right=0.90, bottom=0.085, top=0.93)

for suffix in ("png", "svg", "pdf"):
    path = OUT / f"kat_husky_teleoperation_response.{suffix}"
    fig.savefig(path, dpi=300 if suffix == "png" else None,
                bbox_inches="tight", facecolor="white")
plt.close(fig)
print(f"samples={len(t)} duration={t[-1]:.2f}s reverse_start={reverse_start}")
