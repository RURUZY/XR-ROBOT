"""Convert a completed KAT Game-view capture to MP4, preserving measured frame intervals.

Usage: python Tools/render_view_capture.py KATCaptures/KAT_MiniS_...
Only reads existing frames. Never starts Unity, cameras, ROS or robot control.
"""
import argparse
import csv
import math
from pathlib import Path
import shutil
import subprocess


def render(capture: Path, overwrite: bool = False) -> Path:
    capture = capture.resolve()
    with (capture / "view_frames.csv").open(encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))
    if not rows:
        raise ValueError("No saved Game-view frames. Keep Unity's Game view rendering during Play.")
    times, files = [], []
    for row in rows:
        stamp = float(row["elapsed_s"])
        if not math.isfinite(stamp) or (times and stamp <= times[-1]):
            raise ValueError("Frame times must be finite and strictly increasing.")
        file = (capture / row["file"]).resolve()
        if not file.is_relative_to(capture / "view_frames") or not file.is_file():
            raise ValueError("Missing frame or frame outside view_frames: " + str(file))
        relative = file.relative_to(capture).as_posix()
        if "'" in relative or "\n" in relative or "\r" in relative:
            raise ValueError("Unsupported frame filename.")
        times.append(stamp)
        files.append(relative)
    output = capture / "view.mp4"
    if output.exists() and not overwrite:
        raise FileExistsError("view.mp4 already exists; use --overwrite to replace it.")
    ffmpeg = shutil.which("ffmpeg")
    if not ffmpeg:
        raise FileNotFoundError("ffmpeg is not on PATH; original JPEGs and CSV are preserved.")
    manifest = capture / "view_frames.ffconcat"
    with manifest.open("w", encoding="utf-8", newline="\n") as stream:
        stream.write("ffconcat version 1.0\n")
        for i, file in enumerate(files):
            duration = times[i + 1] - times[i] if i + 1 < len(times) else 0.2
            stream.write(f"file '{file}'\nduration {duration:.9f}\n")
        stream.write(f"file '{files[-1]}'\n")
    subprocess.run([
        ffmpeg, "-hide_banner", "-loglevel", "error", "-y" if overwrite else "-n",
        "-f", "concat", "-safe", "0", "-i", manifest.name, "-fps_mode", "vfr",
        "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-c:v", "libx264", "-crf", "23",
        "-pix_fmt", "yuv420p", "-movflags", "+faststart", output.name,
    ], cwd=capture, check=True)
    print(f"{output}\nFrames: {len(rows)}; first elapsed_s: {times[0]:.6f}. MP4 time zero is this first frame.")
    return output


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--overwrite", action="store_true")
    args = parser.parse_args()
    render(args.capture, args.overwrite)
