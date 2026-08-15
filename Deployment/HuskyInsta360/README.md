# Husky A200 Insta360 X5 test

This first-stage container only verifies:

`X5 -> Linux Camera SDK -> Media SDK RealTimeStitcher -> frame counter`

It does not modify or join the Clearpath OutdoorNav Docker Compose project.

## Prepare on the Windows development computer

Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\Deployment\HuskyInsta360\prepare_sdk.ps1
```

Copy the complete `Deployment/HuskyInsta360` directory to the Husky computer.

## Build on Husky

```bash
cd ~/HuskyInsta360
docker compose build
```

## Camera test

Connect X5 to the Husky USB controller and select Android control mode, then:

```bash
docker compose run --rm insta360-test
```

A successful test prints the camera model, serial number, firmware, output
size, and stitched frames per second. Press `Ctrl+C` to stop.

The initial test uses `privileged: true` solely to eliminate USB permission
variables. Replace it with a scoped `/dev/bus/usb` mapping and udev rule after
the camera's USB VID/PID is known.
