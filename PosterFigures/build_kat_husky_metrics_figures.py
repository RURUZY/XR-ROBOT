from pathlib import Path
import sys

import matplotlib.pyplot as plt
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import build_kat_husky_poster_figure as source


OUT = Path(__file__).resolve().parent
d = source.latest_run()
forward = d["linear"] > 0.02
turn = np.abs(d["angular"]) > 0.01

modes = {
    "Idle": np.mean(~forward & ~turn) * 100,
    "Forward only": np.mean(forward & ~turn) * 100,
    "Turn only": np.mean(~forward & turn) * 100,
    "Forward + turn": np.mean(forward & turn) * 100,
}
saturation = {
    "Linear command": np.mean(d["linear"] >= 0.599) * 100,
    "Angular command": np.mean(np.abs(d["angular"]) >= 0.599) * 100,
}

plt.rcParams.update({
    "font.family": "DejaVu Sans",
    "font.size": 11,
    "axes.titlesize": 13,
    "axes.labelsize": 11,
    "svg.fonttype": "none",
})
colors = ["#98A2B3", "#1769AA", "#7353BA", "#178554"]

# Figure 1: exclusive control modes and saturation
fig, axes = plt.subplots(1, 2, figsize=(11.7, 4.4), gridspec_kw={"width_ratios": [1.7, 1]})
fig.suptitle("KAT-Husky Control Utilization", fontsize=17, fontweight="bold", y=0.98)

ax = axes[0]
left = 0
for (label, value), color in zip(modes.items(), colors):
    ax.barh([0], [value], left=left, height=0.42, color=color, edgecolor="white", linewidth=1.5, label=label)
    if value >= 7:
        ax.text(left + value / 2, 0, f"{value:.1f}%", ha="center", va="center",
                color="white" if label != "Idle" else "#344054", fontweight="bold")
    left += value
ax.set_xlim(0, 100)
ax.set_yticks([])
ax.set_xlabel("Share of recorded control samples (%)")
ax.set_title("A   Mutually exclusive operating modes", loc="left", fontweight="bold")
ax.legend(loc="lower center", bbox_to_anchor=(0.5, -0.40), ncol=2, frameon=False)
ax.spines[["left", "right", "top"]].set_visible(False)
ax.grid(False)

ax = axes[1]
labels = list(saturation)
values = list(saturation.values())
bars = ax.bar(labels, values, color=["#D97706", "#178554"], width=0.58)
for bar, value in zip(bars, values):
    ax.text(bar.get_x() + bar.get_width()/2, value + 1.3, f"{value:.1f}%",
            ha="center", va="bottom", fontweight="bold")
ax.set_ylim(0, max(values) * 1.25)
ax.set_ylabel("Samples at configured limit (%)")
ax.set_title("B   Command saturation", loc="left", fontweight="bold")
ax.grid(axis="y", color="#D0D5DD", linewidth=0.7)
ax.spines[["right", "top"]].set_visible(False)

fig.text(0.01, 0.015,
         f"Latest continuous Unity Console segment: {len(d['t'])} samples, approximately {d['t'][-1]:.1f} s at 20 Hz. "
         "Active thresholds: linear > 0.02 m/s; |angular| > 0.01 rad/s.",
         fontsize=8.7, color="#667085")
fig.subplots_adjust(left=0.07, right=0.98, bottom=0.25, top=0.82, wspace=0.28)
for suffix in ("png", "svg", "pdf"):
    fig.savefig(OUT / f"kat_husky_control_utilization.{suffix}", dpi=300 if suffix == "png" else None,
                bbox_inches="tight", facecolor="white")
plt.close(fig)

# Figure 2: yaw signal distribution and extreme events
yaw = d["yaw"]
t = d["t"]
fig, axes = plt.subplots(1, 2, figsize=(11.7, 4.8), gridspec_kw={"width_ratios": [1.1, 1.8]})
fig.suptitle("Body Yaw-Rate Signal Quality", fontsize=17, fontweight="bold", y=0.98)

ax = axes[0]
bins = np.linspace(-300, 300, 41)
ax.hist(np.clip(yaw, -300, 300), bins=bins, color="#7353BA", alpha=0.88, edgecolor="white")
ax.axvspan(-90, 90, color="#178554", alpha=0.10, linewidth=0)
ax.axvline(-90, color="#667085", ls="--", lw=1)
ax.axvline(90, color="#667085", ls="--", lw=1)
ax.set_xlabel("Filtered body yaw rate (deg/s)")
ax.set_ylabel("Number of samples")
ax.set_title("A   Distribution", loc="left", fontweight="bold")
ax.text(0.98, 0.95, f"|rate| > 90 deg/s: {np.mean(np.abs(yaw)>90)*100:.1f}%\n"
        f"|rate| > 180 deg/s: {np.sum(np.abs(yaw)>180)} samples",
        transform=ax.transAxes, ha="right", va="top", color="#344054")
ax.grid(axis="y", color="#D0D5DD", linewidth=0.7)
ax.spines[["right", "top"]].set_visible(False)

ax = axes[1]
ax.plot(t, yaw, color="#7353BA", lw=1.5, label="Filtered body yaw rate")
extreme = np.abs(yaw) > 180
ax.scatter(t[extreme], yaw[extreme], s=26, color="#D92D20", zorder=3, label="Extreme event (>180 deg/s)")
ax.axhline(90, color="#667085", ls="--", lw=1)
ax.axhline(-90, color="#667085", ls="--", lw=1)
ax.set_xlabel("Elapsed test time (s)")
ax.set_ylabel("Yaw rate (deg/s)")
ax.set_title("B   Extreme-event timing", loc="left", fontweight="bold")
ax.legend(frameon=False, loc="upper right")
ax.grid(axis="y", color="#D0D5DD", linewidth=0.7)
ax.spines[["right", "top"]].set_visible(False)

fig.text(0.01, 0.015,
         "Red markers identify discontinuities likely to drive the angular command into saturation. "
         "Histogram endpoints include values clipped beyond +/-300 deg/s for readability.",
         fontsize=8.7, color="#667085")
fig.subplots_adjust(left=0.075, right=0.985, bottom=0.16, top=0.84, wspace=0.28)
for suffix in ("png", "svg", "pdf"):
    fig.savefig(OUT / f"kat_husky_yaw_signal_quality.{suffix}", dpi=300 if suffix == "png" else None,
                bbox_inches="tight", facecolor="white")
plt.close(fig)

print({"samples": len(t), "duration": float(t[-1]), "modes": modes,
       "saturation": saturation, "yaw_extreme_count": int(np.sum(np.abs(yaw) > 180))})
