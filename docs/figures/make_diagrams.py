"""Non-data thesis diagrams (architecture, controller pipeline, jump confirmation,
operator states, panorama compensation, timeline, coordinate conventions).
Every label is taken from the repository code/docs as of 2026-11-26.
Usage: venv/Scripts/python.exe make_diagrams.py <out_dir>
"""
import os, sys, math
import datetime as dt
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from matplotlib.patches import FancyBboxPatch, FancyArrowPatch, Circle, Wedge, Polygon

OUT = sys.argv[1]
os.makedirs(OUT, exist_ok=True)
C = dict(blue='#2a78d6', orange='#eb6834', aqua='#1baf7a', yellow='#eda100', violet='#6250d6', red='#e34948',
         ink='#0b0b0b', ink2='#52514e', muted='#8a8984', line='#c9c8c2', surface='#fcfcfb')
TINT = dict(blue='#e3eefb', orange='#fde9e0', aqua='#e0f5ed', yellow='#fdf1d6', violet='#e8e5fa', red='#fbe3e3', grey='#efeeea')
plt.rcParams.update({'font.family': 'DejaVu Sans', 'figure.facecolor': C['surface'], 'savefig.facecolor': C['surface']})


def canvas(w, h, title):
    fig = plt.figure(figsize=(w, h))
    ax = fig.add_axes([0, 0, 1, 1])
    ax.set_xlim(0, w); ax.set_ylim(0, h); ax.axis('off')
    ax.text(0.35, h - 0.45, title, fontsize=15, fontweight='bold', color=C['ink'], va='top')
    return fig, ax


def box(ax, x, y, w, h, text, hue='blue', fs=9.5, bold_first=True, fill=None, align='center'):
    ax.add_patch(FancyBboxPatch((x, y), w, h, boxstyle='round,pad=0.02,rounding_size=0.12',
                                fc=fill or TINT[hue], ec=C[hue] if hue in C else C['line'], lw=1.4))
    lines = text.split('\n')
    tx = x + w / 2 if align == 'center' else x + 0.15
    if bold_first and len(lines) > 1:
        ax.text(tx, y + h - 0.18, lines[0], ha=align, va='top', fontsize=fs + 0.5, fontweight='bold', color=C['ink'])
        ax.text(tx, y + h - 0.48, '\n'.join(lines[1:]), ha=align, va='top', fontsize=fs - 0.8, color=C['ink2'], linespacing=1.35)
    else:
        ax.text(x + w / 2, y + h / 2, text, ha='center', va='center', fontsize=fs,
                fontweight='bold' if bold_first else 'normal', color=C['ink'], linespacing=1.3)


def group(ax, x, y, w, h, label):
    ax.add_patch(FancyBboxPatch((x, y), w, h, boxstyle='round,pad=0.02,rounding_size=0.2',
                                fc='none', ec=C['line'], lw=1.2, ls=(0, (4, 3))))
    ax.text(x + 0.15, y + h - 0.12, label, fontsize=10, fontweight='bold', color=C['ink2'], va='top')


def arrow(ax, p, q, label=None, color=None, lw=1.6, rad=0.0, fs=8, lpos=0.5, loff=(0, 0.12)):
    color = color or C['ink2']
    ax.add_patch(FancyArrowPatch(p, q, arrowstyle='-|>', mutation_scale=13, lw=lw, color=color,
                                 connectionstyle=f'arc3,rad={rad}', shrinkA=2, shrinkB=2))
    if label:
        mx = p[0] + (q[0] - p[0]) * lpos + loff[0]
        my = p[1] + (q[1] - p[1]) * lpos + loff[1]
        ax.text(mx, my, label, fontsize=fs, color=C['ink'], ha='center', va='bottom',
                bbox=dict(fc=C['surface'], ec='none', pad=0.6, alpha=0.9), linespacing=1.25)


def diamond(ax, cx, cy, w, h, text, hue='yellow', fs=8.8):
    ax.add_patch(Polygon([(cx, cy + h / 2), (cx + w / 2, cy), (cx, cy - h / 2), (cx - w / 2, cy)],
                         fc=TINT[hue], ec=C[hue], lw=1.4))
    ax.text(cx, cy, text, ha='center', va='center', fontsize=fs, color=C['ink'], linespacing=1.25)


def foot(ax, text, y=0.3):
    ax.text(0.35, y, text, fontsize=8, color=C['muted'])


def save(fig, name):
    for ext in ('png', 'pdf', 'svg'):
        fig.savefig(os.path.join(OUT, f'{name}.{ext}'), dpi=200 if ext == 'png' else None)
    plt.close(fig)
    print('saved', name)


# ======================= D01 system architecture =======================
fig, ax = canvas(17, 9.6, 'XR-ROBOT system architecture: KAT walking + Quest 3 teleoperation of a Husky A200')
group(ax, 0.3, 0.9, 3.4, 7.9, 'Operator station')
box(ax, 0.75, 6.3, 2.7, 1.5, 'KAT Walk mini S\nbody yaw (quaternion)\nmoveSpeed / step pulses\nKATNativeSDK, USB', 'orange')
box(ax, 0.75, 3.7, 2.7, 1.6, 'Meta Quest 3\nheadset pose, controllers\nTrigger / A / B / Y\nLink / Air Link to PC', 'violet')
box(ax, 0.75, 1.3, 2.7, 1.3, 'Operator\nwalks, turns body,\nlooks around', 'grey', fill=TINT['grey'])
group(ax, 4.3, 0.9, 4.9, 7.9, 'Unity PC  (Unity 2022.3.4f1)')
box(ax, 4.9, 6.2, 4.0, 1.7, 'DirectAngle controller\nbody turn -> 1:1 heading target\nwalk -> forward speed\njump guard + headset confirm', 'blue')
box(ax, 4.9, 4.1, 4.0, 1.5, 'MediaMtxWhepReceiver\nWebRTC 360 video sphere\nheading compensation (0.4 s)', 'blue')
box(ax, 4.9, 2.2, 4.0, 1.4, 'LivoscanDataSource\npose, gyro heading, objects,\npoint map -> wrist minimap', 'blue')
box(ax, 4.9, 1.1, 1.9, 0.85, 'Status HUD', 'grey', fill=TINT['grey'], fs=8.5)
box(ax, 7.0, 1.1, 1.9, 0.85, 'Capture CSV', 'grey', fill=TINT['grey'], fs=8.5)
group(ax, 11.0, 4.6, 5.7, 4.2, 'Husky A200  (192.168.2.36, ROS 1)')
box(ax, 11.3, 6.5, 2.5, 1.3, 'ROS-TCP\nendpoint :10000', 'aqua')
box(ax, 14.0, 6.5, 2.4, 1.3, 'Velocity controller\nEKF: wheels + IMU', 'aqua')
box(ax, 11.3, 4.9, 2.5, 1.3, 'MediaMTX\nWHEP :8889', 'aqua')
box(ax, 14.0, 4.9, 2.4, 1.3, 'Insta360 X5\nstitch -> H.264 RTSP\n2880x1440, 30 fps', 'aqua')
group(ax, 11.0, 0.9, 5.7, 3.3, 'Jetson Orin  (192.168.2.22, ROS 2)')
box(ax, 11.3, 1.3, 2.5, 2.2, 'xr_bridge_server\n:8770\n/ws: pose, yaw_odom,\nheading_t, objects\n/map/points.bin', 'yellow')
box(ax, 14.0, 1.3, 2.4, 2.2, 'livoscan SLAM\nLivox lidar + IMU\nimu_pose_node\n/livoscan/heading', 'yellow')
ax.plot([0.5, 0.5], [1.95, 7.05], color=C['muted'], lw=1.4)
ax.plot([0.5, 0.75], [1.95, 1.95], color=C['muted'], lw=1.4)
arrow(ax, (0.5, 7.05), (0.75, 7.05), color=C['muted'])
arrow(ax, (2.1, 2.6), (2.1, 3.7), color=C['muted'])
arrow(ax, (3.45, 7.05), (4.9, 7.05), 'SDK poll', fs=7.5)
arrow(ax, (3.45, 4.9), (4.9, 6.4), 'head yaw', fs=7.5, lpos=0.55, loff=(-0.2, 0.05))
arrow(ax, (4.9, 4.6), (3.45, 4.3), 'view + HUD', fs=7.5, loff=(0, -0.42))
arrow(ax, (8.9, 7.45), (11.3, 7.45), '/cmd_vel Twist, 20 Hz', fs=7.5)
arrow(ax, (11.3, 6.8), (8.9, 6.8), '/odometry/filtered, 50 Hz', fs=7.5, loff=(0, -0.38))
arrow(ax, (13.8, 7.15), (14.0, 7.15)); arrow(ax, (14.0, 5.55), (13.8, 5.55)); arrow(ax, (14.0, 2.4), (13.8, 2.4))
arrow(ax, (11.3, 5.3), (8.9, 4.85), 'WebRTC video (WHEP)', fs=7.5, loff=(0, -0.4))
arrow(ax, (11.3, 2.6), (8.9, 2.9), 'WebSocket + HTTP', fs=7.5, loff=(0, 0.05))
ax.plot([4.9, 4.55, 4.55], [2.6, 2.6, 6.55], color=C['yellow'], lw=2)
arrow(ax, (4.55, 6.55), (4.9, 6.55), color=C['yellow'], lw=2)
ax.text(4.42, 3.3, 'gyro heading', rotation=90, fontsize=7.5, color=C['ink'], ha='center', va='bottom',
        bbox=dict(fc=C['surface'], ec='none', pad=0.5))
foot(ax, 'Addresses and ports are the saved scene/prefab values. /cmd_vel nominal 20 Hz; odometry 50 Hz; KAT polled ~72 rows/s (10-22 capture). '
     'Video settings from camera_stitch_test.cpp; received frame rate not measured.', 0.35)
save(fig, 'D01_system_architecture')

# ======================= D02 controller pipeline =======================
fig, ax = canvas(17, 8.6, 'DirectAngle controller: one FixedUpdate tick (0.02 s), publish /cmd_vel at 20 Hz')
y1, y2 = 5.6, 2.3
box(ax, 0.4, 3.6, 2.4, 2.2, 'KAT SDK\nGetWalkStatus()\nconnected?\nmoveSpeed\nbodyRotationRaw', 'orange')
box(ax, 3.5, y1, 2.7, 1.5, 'Step signal\nmoveSpeed pulse hold 0.55 s\nstep-in-place 0.35', 'blue')
box(ax, 6.8, y1, 2.7, 1.5, 'Speed curve\ndead zone 0.02, exponent 0.6\nmin 0.08 m/s', 'blue')
box(ax, 10.1, y1, 2.6, 1.5, 'Reverse (B)?\nsign flip, max reverse\n0.3 m/s (scene 0.35)', 'blue')
box(ax, 3.5, y2, 2.7, 1.7, 'Raw body yaw\nstep > 45 deg ?\n-> jump guard\n(+ headset confirm, D03)', 'red')
box(ax, 6.8, y2, 2.7, 1.7, 'Accepted body turn\nlow-pass 0.06 s\naccumulate (unwrapped)\n= 1:1 heading target', 'blue')
box(ax, 10.1, y2, 2.6, 1.7, 'Heading error\ntarget - robot yaw', 'blue')
box(ax, 10.1, 4.35, 2.6, 0.9, 'Robot yaw: Jetson gyro\n(or Husky odometry)', 'aqua', bold_first=False, fs=8.5)
box(ax, 13.3, y2, 3.3, 1.7, 'Turn command\nfeed-forward (body rate)\n+ P feedback w/ braking\nclamp to max angular', 'blue')
box(ax, 13.3, 5.6, 3.3, 1.5, 'Publish /cmd_vel\nlinear.x, angular.z\n20 Hz + motion edges', 'aqua')
box(ax, 13.3, 0.4, 3.3, 1.3, 'Safety gates\nodometry > 0.5 s old -> stop all\ngyro > 0.2 s old -> turn = 0', 'yellow')
box(ax, 3.5, 0.4, 6.0, 1.3, 'Activation\nTrigger starts (latch), Y pauses, A = E-stop,\nKAT input lost > grace -> pause, needs Trigger', 'violet')
arrow(ax, (2.8, 5.2), (3.5, 6.3)); arrow(ax, (2.8, 4.2), (3.5, 3.2))
arrow(ax, (6.2, 6.35), (6.8, 6.35)); arrow(ax, (9.5, 6.35), (10.1, 6.35)); arrow(ax, (12.7, 6.35), (13.3, 6.35), 'linear')
arrow(ax, (6.2, 3.15), (6.8, 3.15)); arrow(ax, (9.5, 3.15), (10.1, 3.15)); arrow(ax, (12.7, 3.15), (13.3, 3.15))
arrow(ax, (14.95, 4.0), (14.95, 5.6), 'angular', loff=(0.45, -0.1))
arrow(ax, (14.95, 1.7), (14.95, 2.3), color=C['yellow'])
arrow(ax, (11.4, 4.35), (11.4, 4.0), color=C['aqua'])
ax.text(3.5, 7.45, 'Forward channel (walking)', fontsize=10, fontweight='bold', color=C['ink2'])
ax.text(3.5, 4.2, 'Turn channel (body orientation)', fontsize=10, fontweight='bold', color=C['ink2'])
foot(ax, 'Numbers are source defaults in KatHuskyDirectAngleController.cs unless noted; the saved scene may override speeds '
     '(e.g. max forward 0.5 m/s in the 11-23 capture configuration).', 0.05)
save(fig, 'D02_controller_pipeline')

# ======================= D03 jump confirmation flowchart =======================
fig, ax = canvas(13, 12, 'Body-yaw jump handling with headset confirmation (BodyJumpConfirmation.cs)')
cx = 4.2
box(ax, cx - 1.9, 10.0, 3.8, 0.9, 'New KAT body-yaw sample', 'orange')
diamond(ax, cx, 9.05, 4.6, 1.3, '|raw step| > limit?\nlimit = max(45 deg, 720 deg/s x dt)')
box(ax, 8.0, 8.6, 4.4, 0.9, 'Normal sample: accept, update target', 'aqua')
diamond(ax, cx, 7.35, 4.6, 1.3, 'Headset sample fresh (< 0.1 s)\nand >= 1 s of history?')
diamond(ax, cx, 5.6, 4.6, 1.3, 'Within 0.3 s of the\njump start?')
box(ax, cx - 2.3, 3.55, 4.6, 1.15, 'Compare turns over [t_jump - 1 s, now]:\nbody turn (step +/-360 to best match)\nvs headset turn', 'blue', bold_first=False, fs=9)
diamond(ax, cx, 2.3, 4.6, 1.3, 'same sign and\n|body - head| <= 30 deg?')
box(ax, cx - 2.2, 0.55, 4.4, 0.8, 'Real fast turn: accept the step', 'aqua')
box(ax, 8.0, 5.0, 4.4, 1.7, 'Sensor glitch: drop the sample\nstatus BODY YAW JUMP (TURN OFF)\nturn target kept; forward continues\nno valid yaw for 1 s -> HEADING GAP,\nrebase after 0.3 s stable (+/-3 deg)', 'red')
arrow(ax, (cx, 10.0), (cx, 9.7))
arrow(ax, (cx + 2.3, 9.05), (8.0, 9.05), 'no', lpos=0.3)
arrow(ax, (cx, 8.4), (cx, 8.0), 'yes', loff=(0.3, -0.15))
arrow(ax, (cx + 2.3, 7.35), (8.0, 6.4), 'no', lpos=0.35)
arrow(ax, (cx, 6.7), (cx, 6.25), 'yes', loff=(0.3, -0.15))
arrow(ax, (cx + 2.3, 5.6), (8.0, 5.6), 'no', lpos=0.35)
arrow(ax, (cx, 4.95), (cx, 4.7)); arrow(ax, (cx, 3.55), (cx, 2.95))
arrow(ax, (cx, 1.65), (cx, 1.35), 'yes', loff=(0.3, -0.15))
ax.plot([cx + 2.3, 10.2], [2.3, 2.3], color=C['red'], lw=1.6)
arrow(ax, (10.2, 2.3), (10.2, 5.0), color=C['red'])
ax.text(8.0, 2.4, 'no', fontsize=8, color=C['ink'])
foot(ax, 'Turning only the head never reaches this logic (no body jump). Measured: 283 of 289 logged jumps disagree with the headset (F05).', 0.15)
save(fig, 'D03_jump_confirmation_flow')

# ======================= D04 operator state machine =======================
fig, ax = canvas(14, 7.8, 'Operator control states (right controller: Trigger, A, B; left: Y)')
box(ax, 0.5, 3.0, 3.3, 1.7, 'STANDBY / PAUSED\nPRESS TRIGGER TO START\nzero /cmd_vel;\nreverse flag kept', 'grey', fill=TINT['grey'])
box(ax, 5.5, 4.9, 3.0, 1.5, 'ACTIVE (forward)\nwalk = drive,\nturn = steer 1:1', 'aqua')
box(ax, 5.5, 1.1, 3.0, 1.5, 'ACTIVE / REVERSE\nwalk = drive back\nHUD: !! REVERSE ON !!', 'orange')
box(ax, 10.5, 3.0, 3.1, 1.7, 'EMERGENCY STOP\nA toggles,\nclears reverse', 'red')
arrow(ax, (3.8, 4.45), (5.5, 5.95), 'Trigger', fs=8, lpos=0.45, loff=(-0.35, 0.05))
arrow(ax, (5.5, 5.25), (3.8, 4.1), 'Y / Trigger off /\nKAT input lost', fs=8, lpos=0.55, loff=(0.5, -0.8))
arrow(ax, (3.8, 3.25), (5.5, 1.75), 'Trigger\n(reverse flag set)', fs=8, lpos=0.45, loff=(-0.6, -0.8))
arrow(ax, (5.5, 2.4), (3.8, 3.6), 'pause', fs=8, lpos=0.55, loff=(0.35, 0.02))
arrow(ax, (6.6, 4.9), (6.6, 2.6), 'B', fs=9, loff=(-0.25, -0.1))
arrow(ax, (7.4, 2.6), (7.4, 4.9), 'B', fs=9, loff=(0.25, -0.1))
arrow(ax, (8.5, 5.6), (10.5, 4.4), 'A', fs=9)
arrow(ax, (8.5, 1.9), (10.5, 3.3), 'A', fs=9)
ax.plot([12.05, 12.05, 2.15, 2.15], [3.0, 0.6, 0.6, 2.6], color=C['red'], lw=1.6)
arrow(ax, (2.15, 2.6), (2.15, 3.0), color=C['red'])
ax.text(9.3, 0.68, 'A again -> standby (reverse cleared)', fontsize=8, ha='center', va='bottom', color=C['ink'],
        bbox=dict(fc=C['surface'], ec='none', pad=0.5))
foot(ax, 'Reverse is kept across pauses since 11-02 (clearing it made B look unresponsive after KAT dropouts); only E-stop clears it. '
     'Pause and standby are the same controller state.', 0.15)
save(fig, 'D04_operator_state_machine')

# ======================= D05 panorama heading compensation =======================
fig, ax = canvas(15, 7.4, '360 video heading compensation (MediaMtxWhepReceiver, RobotRelative mode)')


def topview(cx, cy, robot_deg, title):
    ax.add_patch(Circle((cx, cy), 1.9, fc=TINT['blue'], ec=C['blue'], lw=1.4))
    ax.text(cx + 1.2, cy - 1.9, 'video sphere', fontsize=8, color=C['blue'])
    lx, ly = cx + 1.9 * math.cos(math.radians(130)), cy + 1.9 * math.sin(math.radians(130))
    ax.plot(lx, ly, marker='*', ms=17, color=C['aqua'], mec=C['surface'])
    ax.text(lx - 0.2, ly + 0.2, 'landmark', fontsize=8, color=C['ink'], ha='right')
    r = math.radians(90 + robot_deg)
    ax.add_patch(Wedge((cx, cy), 0.9, robot_deg + 90 - 22, robot_deg + 90 + 22, fc=C['orange'], ec='none', alpha=0.9))
    ax.plot([cx, cx + 1.9 * math.cos(r)], [cy, cy + 1.9 * math.sin(r)], color=C['orange'], lw=1.6, ls=(0, (4, 3)))
    ax.text(cx + 2.35 * math.cos(r), cy + 2.3 * math.sin(r), 'camera /\nrobot front', ha='center', va='center', fontsize=8, color=C['ink'])
    ax.annotate('', (cx - 2.4, cy + 1.6), (cx - 2.4, cy + 0.9), arrowprops=dict(arrowstyle='-|>', color=C['ink2']))
    ax.text(cx - 2.4, cy + 1.7, 'XR world\nforward', fontsize=7.5, color=C['ink2'], ha='center', va='bottom')
    ax.text(cx, cy - 2.4, title, ha='center', va='top', fontsize=9.5, fontweight='bold', color=C['ink'])


topview(3.1, 3.7, 0, 'At Trigger: robot front = headset yaw')
topview(8.1, 3.7, -60, 'Robot turned 60 deg right: sphere turned\nwith it, landmark stays in place')
box(ax, 10.9, 3.4, 3.8, 2.6, 'Sphere yaw\nhead yaw at Trigger\n + (robot yaw(t - d)\n    - robot yaw at Trigger)\n\nd = videoHeadingDelaySeconds (0.4 s)\nrobot yaw = gyro-corrected heading', 'blue', align='left', fs=9)
box(ax, 10.9, 1.3, 3.8, 1.6, 'Why the delay d\nthe video shows the scene ~d late;\nrotating the sphere early makes\nthe world appear to swim', 'grey', fill=TINT['grey'])
foot(ax, 'Top views, not to scale. The camera turns with the robot, so the sphere turns by the same heading change and the scene stays world-fixed. '
     'The 0.4 s delay is a manual setting, not a per-frame timestamp.')
save(fig, 'D05_panorama_compensation')

# ======================= D06 project timeline =======================
items = [
    ('KAT closed/open-loop controllers, World-space HUD', '08-10', '08-15', 'blue'),
    ('Insta360 X5 -> RTSP -> MediaMTX -> WHEP -> Quest 3', '08-14', '08-15', 'aqua'),
    ('Reverse drift: wheel-encoder yaw mismatch (bag analysis)', '09-10', '09-11', 'yellow'),
    ('DirectAngle round 2 (filters, step pulses, speed)', '09-13', '09-15', 'blue'),
    ('Accumulated turn, feedback freshness, 1:1 feed-forward', '10-10', '10-13', 'blue'),
    ('SLAM heading correction, panorama uses corrected yaw', '10-14', '10-15', 'aqua'),
    ('Livoscan wrist minimap, capture & diagnostics tools', '10-15', '10-15', 'violet'),
    ('JetsonGyro heading source, heading_t de-duplication', '10-20', '10-20', 'aqua'),
    ('Engineering workflow + headset jump confirmation', '10-21', '10-24', 'blue'),
    ('Real-car test campaign (KATCaptures, 46 drive runs)', '10-13', '11-23', 'orange'),
    ('KAT freeze-then-jump analysis, Husky fault logs', '11-17', '11-23', 'red'),
    ('Figures for thesis', '11-26', '11-26', 'yellow'),
]
d0 = dt.date(2026, 8, 5)
day = lambda s: (dt.date(2026, int(s[:2]), int(s[3:])) - d0).days
fig, ax = plt.subplots(figsize=(14, 6.4))
for i, (lab, a, b, hue) in enumerate(items):
    ax.barh(i, max(day(b) - day(a) + 1, 1.5), left=day(a), color=C[hue], height=0.6)
    ax.text(day(a) - 1.2, i, lab, ha='right', va='center', fontsize=8.6, color=C['ink'])
ax.set_yticks([]); ax.invert_yaxis()
ticks = ['08-10', '09-01', '09-15', '10-01', '10-15', '11-01', '11-15', '11-26']
ax.set_xticks([day(t) for t in ticks], ticks)
ax.set_xlim(-62, day('11-30'))
for sp in ('top', 'right', 'left'):
    ax.spines[sp].set_visible(False)
ax.grid(axis='x', color='#e4e3df'); ax.set_axisbelow(True)
ax.set_title('Project timeline (2026), from docs/PROJECT_STATUS and commit history', loc='left', fontweight='bold', fontsize=13)
fig.tight_layout()
save(fig, 'D06_project_timeline')

# ======================= D07 coordinate conventions =======================
fig, ax = canvas(15, 6.6, 'Frames, yaw signs and unit conversions between subsystems')


def axes2d(cx, cy, xlabel, left, ylabel, title, ccw, note, hue):
    ax.add_patch(FancyBboxPatch((cx - 1.6, cy - 2.4), 3.2, 4.5, boxstyle='round,pad=0.02,rounding_size=0.15',
                                fc=TINT[hue], ec=C[hue], lw=1.3))
    ax.text(cx, cy + 1.75, title, ha='center', va='center', fontsize=10.5, fontweight='bold', color=C['ink'])
    oy = cy - 0.5
    ax.annotate('', (cx, cy + 1.1), (cx, oy), arrowprops=dict(arrowstyle='-|>', color=C['ink'], lw=1.6))
    sx = -1 if left else 1
    ax.annotate('', (cx + sx * 1.15, oy), (cx, oy), arrowprops=dict(arrowstyle='-|>', color=C['ink'], lw=1.6))
    ax.text(cx + 0.1, cy + 1.15, ylabel, fontsize=8.5, va='bottom', color=C['ink'])
    ax.text(cx + sx * 1.15, oy - 0.15, xlabel, fontsize=8.5, ha='center', va='top', color=C['ink'])
    a0, a1 = (100, 160) if ccw else (80, 20)   # from forward toward the positive-yaw side
    p = (cx + 0.8 * math.cos(math.radians(a0)), oy + 0.8 * math.sin(math.radians(a0)))
    q = (cx + 0.8 * math.cos(math.radians(a1)), oy + 0.8 * math.sin(math.radians(a1)))
    ax.add_patch(FancyArrowPatch(p, q, connectionstyle=f'arc3,rad={0.3 if ccw else -0.3}', arrowstyle='-|>',
                                 mutation_scale=12, color=C[hue], lw=1.8))
    ax.text(cx + (-0.95 if ccw else 0.95), cy + 0.55, 'yaw +\n' + ('CCW' if ccw else 'CW'), fontsize=8.5, ha='center',
            color=C[hue], fontweight='bold')
    ax.text(cx, cy - 1.25, note, ha='center', va='top', fontsize=8, color=C['ink2'], linespacing=1.3)


axes2d(2.1, 3.3, 'y left', True, 'x forward', 'ROS / livoscan', True, 'z up; metres, rad\nframes odom, livoscan_map\nyaw_odom: gyro, CCW +', 'aqua')
axes2d(5.7, 3.3, 'x right', False, 'z forward', 'Unity / XR world', False, 'left-handed, y up\np_unity = (-y, z, x)\nyaw_unity = -yaw_ros (deg)', 'blue')
axes2d(9.3, 3.3, 'right', False, 'body forward', 'KAT body yaw', False, 'from bodyRotationRaw quat\n(Unity convention)\nbodyToRosYawSign = -1', 'orange')
axes2d(12.9, 3.3, 'right', False, 'gaze', 'Quest 3 headset', False, 'Camera.main eulerAngles.y\nsame XR world frame;\nused for jump confirmation', 'violet')
foot(ax, 'Top views. The axis swap only aligns conventions; it does not give the lidar-IMU -> chassis extrinsic, nor align the map and Husky odom origins.')
save(fig, 'D07_coordinate_conventions')
print('done')
