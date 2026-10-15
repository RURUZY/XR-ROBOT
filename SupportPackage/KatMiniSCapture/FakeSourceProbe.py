"""Offline lifecycle fixture. No sockets, ROS, or hardware imports."""
import argparse
import json
import pathlib
import sys
import time

parser = argparse.ArgumentParser()
for name in ["master-uri", "publisher-node", "topic", "seconds", "output-prefix", "stop-file"]:
    parser.add_argument("--" + name, required=True)
args = parser.parse_args()
prefix = pathlib.Path(args.output_prefix)
prefix.with_suffix(".csv").write_text("SYNTHETIC ONLY\n")
prefix.with_suffix(".args.json").write_text(json.dumps(vars(args)))
print("SYNTHETIC STDOUT", flush=True)
print("SYNTHETIC STDERR", file=sys.stderr, flush=True)
end = time.monotonic() + 5
while not pathlib.Path(args.stop_file).exists() and time.monotonic() < end:
    time.sleep(.02)
prefix.with_suffix(".json").write_text(json.dumps({"end_reason": "stop_requested"}))
