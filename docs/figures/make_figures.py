"""Generate thesis / demo figures from KATCaptures + Husky logs.

Usage:  venv/Scripts/python.exe make_figures.py <repo_root> <out_dir>
Reads only; raw captures are never modified. A per-session cache (pickle) is
kept next to this script so re-runs are fast.
"""
import csv, glob, json, math, os, pickle, sys, collections, bisect
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from matplotlib.lines import Line2D

REPO, OUT = sys.argv[1], sys.argv[2]
CAP = os.path.join(REPO, 'KATCaptures')
os.makedirs(OUT, exist_ok=True)
CACHE = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'fig_cache.pkl')

# ---- palette (reference categorical order, light mode) ----
C = dict(blue='#2a78d6', orange='#eb6834', aqua='#1baf7a', yellow='#eda100',
         magenta='#e87ba4', green='#008300', violet='#6250d6', red='#e34948',
         ink='#0b0b0b', ink2='#52514e', muted='#8a8984', grid='#e4e3df', surface='#fcfcfb')
plt.rcParams.update({
    'figure.facecolor': C['surface'], 'axes.facecolor': C['surface'], 'savefig.facecolor': C['surface'],
    'axes.edgecolor': C['muted'], 'axes.labelcolor': C['ink2'], 'xtick.color': C['ink2'], 'ytick.color': C['ink2'],
    'axes.grid': True, 'grid.color': C['grid'], 'grid.linewidth': 0.8, 'axes.spines.top': False,
    'axes.spines.right': False, 'font.size': 9.5, 'axes.titlesize': 11, 'axes.titleweight': 'bold',
    'axes.titlecolor': C['ink'], 'axes.titlelocation': 'left', 'legend.frameon': False, 'lines.linewidth': 1.6,
    'font.family': 'DejaVu Sans'})


def wrap(a):
    return (np.asarray(a) + 180.0) % 360.0 - 180.0


def F(x, d=np.nan):
    try:
        return float(x)
    except (TypeError, ValueError):
        return d


def load(d, name, cols):
    p = os.path.join(d, name)
    if not os.path.exists(p):
        return None
    out = {c: [] for c in cols}
    try:
        with open(p, encoding='utf-8-sig', newline='') as f:
            for r in csv.DictReader(f):
                for c in cols:
                    out[c].append(r.get(c, ''))
    except Exception:
        return None
    return out


def arr(v):
    return np.array([F(x) for x in v], dtype=float)


def bool_arr(v):
    return np.array([x == 'True' for x in v], dtype=bool)


def quat_yaw(qy, qw):
    return np.degrees(2 * np.arctan2(qy, qw)) % 360


def runs(mask):
    """index pairs [i, j] of consecutive True runs."""
    out, i, n = [], 0, len(mask)
    while i < n:
        if mask[i]:
            j = i
            while j + 1 < n and mask[j + 1]:
                j += 1
            out.append((i, j))
            i = j + 1
        else:
            i += 1
    return out


# ======================= stage 1: per-session parse =======================
def parse_session(d):
    name = os.path.basename(d)
    s = dict(name=name, date=name[10:18], time=name[19:25])
    js = os.path.join(d, 'session.json')
    if os.path.exists(js):
        try:
            s['meta'] = json.load(open(js, encoding='utf-8-sig'))
        except Exception:
            s['meta'] = {}
    S = load(d, 'samples.csv', ['elapsed_s', 'connected', 'sdk_last_update_raw', 'quat_y', 'quat_w', 'horizontal_speed'])
    if S is None or len(S['elapsed_s']) < 50:
        return None
    t = arr(S['elapsed_s']); con = bool_arr(S['connected'])
    yaw = quat_yaw(arr(S['quat_y']), np.nan_to_num(arr(S['quat_w']), nan=1.0))
    stamp = np.array(S['sdk_last_update_raw'])
    s['duration'] = float(t[-1] - t[0])
    s['connected_s'] = float(np.sum(np.diff(t)[con[1:]])) if len(t) > 1 else 0.0
    s['speed'] = arr(S['horizontal_speed'])
    # SDK update intervals (time between stamp changes, connected only)
    ch = np.r_[True, stamp[1:] != stamp[:-1]]
    idx = np.where(ch & con)[0]
    s['upd_dt'] = np.diff(t[idx]) if len(idx) > 1 else np.array([])
    # freezes: same stamp & connected, duration > 0.2 s
    fr = []
    i = 0
    n = len(t)
    while i < n:
        j = i
        while j + 1 < n and stamp[j + 1] == stamp[i] and con[j + 1] == con[i]:
            j += 1
        if con[i] and t[j] - t[i] > 0.2:
            fr.append((t[i], t[j]))
        i = j + 1
    s['freezes'] = fr
    s['disconnects'] = [(t[a], t[min(b + 1, n - 1)]) for a, b in runs(~con) if a > 0]
    # headset yaw
    P = load(d, 'control_panorama.csv', ['elapsed_s', 'head_world_yaw_deg', 'robot_world_yaw_deg',
                                         'sphere_world_yaw_deg', 'heading_fresh', 'odom_age_s'])
    ht = hy = None
    if P and len(P['elapsed_s']) > 10:
        ht = arr(P['elapsed_s']); hy = arr(P['head_world_yaw_deg'])
        ok = np.isfinite(hy)
        ht, hy = ht[ok], hy[ok]
        if len(hy) < 10 or np.nanstd(hy) < 1e-6:
            ht = hy = None
        s['pano'] = dict(t=arr(P['elapsed_s']), head=arr(P['head_world_yaw_deg']),
                         robot=arr(P['robot_world_yaw_deg']), sphere=arr(P['sphere_world_yaw_deg']),
                         age=arr(P['odom_age_s']))
    s['has_head'] = ht is not None

    def head_at(x):
        k = min(max(bisect.bisect_left(ht, x), 0), len(ht) - 1)
        return hy[k]

    # jumps (> 45 deg single step between connected samples)
    ci = np.where(con)[0]
    jumps = []
    small = 0
    for a, b in zip(ci[:-1], ci[1:]):
        st = float(wrap(yaw[b] - yaw[a]))
        if 20 < abs(st) <= 45:
            small += 1
        if abs(st) > 45:
            # stamp-unchanged time before the jump
            k = a
            while k > 0 and con[k - 1] and stamp[k - 1] == stamp[a]:
                k -= 1
            freeze = t[a] - t[k]
            k2 = a
            while k2 > 0 and con[k2 - 1] and abs(wrap(yaw[k2] - yaw[k2 - 1])) < 0.5:
                k2 -= 1
            crossed = any(t[a] <= x0 <= t[b] for x0, _ in s['disconnects'])
            hc = float(wrap(head_at(t[b] + 0.3) - head_at(t[b] - 1.0))) if ht is not None else np.nan
            jumps.append(dict(t=float(t[b]), step=st, stamp_freeze=float(freeze), yaw_flat=float(t[a] - t[k2]),
                              dt=float(t[b] - t[a]), crossed=crossed, head=hc))
    s['jumps'] = jumps
    s['small_steps'] = small
    keep = slice(None, None, max(1, len(t) // 6000))
    s['kat'] = dict(t=t[keep], yaw=yaw[keep], con=con[keep])
    s['kat_full'] = None
    # control_request (DirectAngle real-car runs)
    R = load(d, 'control_request.csv', ['elapsed_s', 'requested_linear', 'requested_angular', 'target_yaw_deg',
                                        'error_deg', 'activated', 'status', 'heading_source', 'gyro_yaw_deg',
                                        'gyro_rate_deg_s', 'wheel_yaw_deg', 'pose_msg_age_s'])
    s['drive'] = None
    if R and len(R['elapsed_s']) > 50:
        rt = arr(R['elapsed_s'])
        s['drive'] = dict(t=rt, lin=arr(R['requested_linear']), ang=arr(R['requested_angular']),
                          target=arr(R['target_yaw_deg']), err=arr(R['error_deg']), status=R['status'],
                          src=collections.Counter(R['heading_source']).most_common(1)[0][0],
                          gyro=arr(R['gyro_yaw_deg']), gyro_rate=arr(R['gyro_rate_deg_s']),
                          wheel=arr(R['wheel_yaw_deg']), pose_age=arr(R['pose_msg_age_s']))
    s['stale'] = bool(s['drive'] and any('GYRO HEADING STALE' in x for x in s['drive']['status']))
    O = load(d, 'control_odometry.csv', ['elapsed_s', 'unity_receipt_s', 'linear_x', 'angular_z', 'quat_z', 'quat_w', 'x', 'y'])
    s['odom'] = None
    if O and len(O['elapsed_s']) > 50:
        s['odom'] = dict(t=arr(O['elapsed_s']), rx=arr(O['unity_receipt_s']), v=arr(O['linear_x']),
                         w=arr(O['angular_z']), x=arr(O['x']), y=arr(O['y']))
    M = load(d, 'control_minimap.csv', ['elapsed_s', 'slam_pose_fresh', 'slam_localized', 'slam_x', 'slam_y', 'slam_age_s'])
    s['slam'] = None
    if M and len(M['elapsed_s']) > 50:
        fresh = bool_arr(M['slam_pose_fresh'])
        x, y = arr(M['slam_x']), arr(M['slam_y'])
        ok = fresh & np.isfinite(x) & np.isfinite(y)
        if ok.sum() > 50:
            s['slam'] = dict(t=arr(M['elapsed_s'])[ok], x=x[ok], y=y[ok], age=arr(M['slam_age_s'])[ok])
    return s


def build_cache():
    old = {}
    if os.path.exists(CACHE):
        try:
            old = pickle.load(open(CACHE, 'rb'))
        except Exception:
            old = {}
    out = {}
    dirs = sorted(d for d in glob.glob(os.path.join(CAP, 'KAT_MiniS_*')) if os.path.isdir(d))
    for i, d in enumerate(dirs):
        nm = os.path.basename(d)
        mt = os.path.getmtime(os.path.join(d, 'samples.csv')) if os.path.exists(os.path.join(d, 'samples.csv')) else 0
        if nm in old and old[nm][0] == mt:
            out[nm] = old[nm]
            continue
        print(f'[{i + 1}/{len(dirs)}] parse {nm}', flush=True)
        out[nm] = (mt, parse_session(d))
    pickle.dump(out, open(CACHE, 'wb'))
    return [v[1] for k, v in sorted(out.items()) if v[1] is not None]


SESS = build_cache()
print('sessions parsed:', len(SESS))
FIGS = []


def save(fig, name, caption):
    for ext in ('png', 'pdf'):
        fig.savefig(os.path.join(OUT, f'{name}.{ext}'), dpi=200 if ext == 'png' else None, bbox_inches='tight')
    plt.close(fig)
    FIGS.append((name, caption))
    print('saved', name)


def note(fig, text, y=-0.02):
    fig.text(0.01, y, text, fontsize=7.5, color=C['muted'], ha='left', va='top', wrap=True)


def gapnan(t, *ys, gap=0.5):
    t = np.asarray(t, float); k = np.where(np.diff(t) > gap)[0] + 1
    out = [np.insert(t, k, np.nan)] + [np.insert(np.asarray(y, float), k, np.nan) for y in ys]
    return out


def ddate(s):
    return f"{s['date'][:4]}-{s['date'][4:6]}-{s['date'][6:]}"


REAL = [s for s in SESS if s['connected_s'] > 20]       # sessions with real KAT data
DRIVE = [s for s in SESS if s['drive'] is not None]
DRIVE_OK = [s for s in DRIVE if not s['stale']]

# ---------------- summary CSV ----------------
with open(os.path.join(OUT, 'session_summary.csv'), 'w', newline='') as f:
    wr = csv.writer(f)
    wr.writerow(['session', 'date', 'duration_s', 'kat_connected_s', 'sdk_update_median_ms', 'freezes_gt0.3s',
                 'freezes_ge0.95s', 'disconnects', 'jumps_gt45deg', 'has_headset', 'directangle_drive',
                 'heading_source', 'gyro_stale_session', 'active_heading_err_p50_deg', 'active_heading_err_p95_deg'])
    for s in SESS:
        dr = s['drive']
        e = np.abs(dr['err'][np.isfinite(dr['err'])]) if dr else np.array([])
        wr.writerow([s['name'], ddate(s), round(s['duration'], 1), round(s['connected_s'], 1),
                     round(float(np.median(s['upd_dt'])) * 1000, 1) if len(s['upd_dt']) else '',
                     sum(b - a > 0.3 for a, b in s['freezes']), sum(b - a >= 0.95 for a, b in s['freezes']),
                     len(s['disconnects']), len(s['jumps']), s['has_head'], dr is not None,
                     dr['src'] if dr else '', s['stale'],
                     round(float(np.percentile(e, 50)), 2) if len(e) else '',
                     round(float(np.percentile(e, 95)), 2) if len(e) else ''])

# ======================= F01 dataset overview =======================
by_date = collections.OrderedDict()
for s in REAL:
    k = ddate(s)
    by_date.setdefault(k, [0.0, 0.0, 0.0])
    if s['drive'] is not None and s['stale']:
        by_date[k][2] += s['duration'] / 60
    elif s['drive'] is not None:
        by_date[k][1] += s['duration'] / 60
    else:
        by_date[k][0] += s['duration'] / 60
dates = list(by_date)
v = np.array(list(by_date.values()))
fig, ax = plt.subplots(figsize=(12, 4.2))
x = np.arange(len(dates))
bottom = np.zeros(len(dates))
labels = ['KAT / headset / odometry only', 'DirectAngle real-car drive', 'Drive run with GYRO STALE (failed run)']
cols = [C['blue'], C['aqua'], C['orange']]
for k in range(3):
    ax.bar(x, v[:, k], bottom=bottom, color=cols[k], width=0.72, label=labels[k], edgecolor=C['surface'], linewidth=1)
    bottom += v[:, k]
ax.set_xticks(x, [d[5:] for d in dates], rotation=60, fontsize=7.5)
ax.set_ylabel('recorded minutes')
ax.set_title(f'Experiment data collected: {len(REAL)} KAT capture sessions, {sum(s["duration"] for s in REAL) / 3600:.1f} h '
             f'({dates[0]} .. {dates[-1]})')
ax.legend(loc='upper left')
save(fig, 'F01_dataset_overview', 'Recorded minutes per test day, split by session type. Sessions with < 20 s of connected KAT data are excluded.')

# ======================= F02 SDK update interval =======================
allDt = np.concatenate([s['upd_dt'] for s in REAL if len(s['upd_dt'])])
fig, ax = plt.subplots(figsize=(9, 4))
bins = np.logspace(np.log10(0.005), np.log10(3), 90)
ax.hist(np.clip(allDt, 0.005, 3), bins=bins, color=C['blue'])
ax.set_xscale('log')
ax.set_yscale('log')
med = np.median(allDt)
ax.axvline(med, color=C['ink2'], lw=1, ls='--')
ax.text(med * 1.08, ax.get_ylim()[1] * 0.3, f'median {med * 1000:.0f} ms', color=C['ink2'])
for th in (0.3, 0.95):
    ax.axvline(th, color=C['orange'], lw=1, ls=':')
    ax.text(th * 1.05, ax.get_ylim()[1] * 0.05, f'{th:g} s', color=C['orange'])
ax.set_xlabel('interval between KAT SDK lastUpdateTimePoint changes while connected (s, log)')
ax.set_ylabel('count (log)')
p99 = np.percentile(allDt, 99.9)
ax.set_title(f'KAT mini S SDK update interval, {len(allDt):,} updates in {len(REAL)} sessions')
note(fig, f'> 0.3 s: {np.sum(allDt > 0.3):,} intervals ({np.mean(allDt > 0.3) * 100:.2f} %);  >= 0.95 s: {np.sum(allDt >= 0.95):,}.  99.9th percentile {p99:.2f} s.')
save(fig, 'F02_kat_sdk_update_interval', 'Distribution of the time between KAT SDK timestamp changes. The long tail (> 0.3 s) is the sensor freeze that precedes body-yaw jumps.')

# ======================= F03 freeze duration histogram + per-session rate =======================
fz = np.array([b - a for s in REAL for a, b in s['freezes']])
fig, (ax, ax2) = plt.subplots(1, 2, figsize=(12, 4), gridspec_kw=dict(width_ratios=[1.2, 1]))
ax.hist(np.minimum(fz, 2.0), bins=90, color=C['orange'])
ax.set_xlabel('duration SDK stamp unchanged while connected (s, clipped at 2 s)')
ax.set_ylabel('count')
ax.set_title(f'{len(fz):,} SDK freezes > 0.2 s')
rate = [(ddate(s), sum(b - a > 0.3 for a, b in s['freezes']) / max(s['connected_s'] / 60, 1e-6)) for s in REAL if s['connected_s'] > 60]
rd = sorted(set(r[0] for r in rate))
ax2.scatter([rd.index(r[0]) for r in rate], [r[1] for r in rate], s=22, color=C['orange'], edgecolor=C['surface'], linewidth=1, zorder=3)
ax2.set_xticks(range(len(rd)), [d[5:] for d in rd], rotation=60, fontsize=7)
ax2.set_ylabel('freezes > 0.3 s per connected minute')
ax2.set_title('Freeze rate per session (> 60 s connected)')
fig.tight_layout()
save(fig, 'F03_kat_freeze_distribution', 'Left: duration of SDK freezes. Right: freeze rate per session over the test period; the fault is present on every test day.')

# ======================= F04 jump magnitude vs freeze before =======================
J = [dict(j, sess=s['name']) for s in REAL for j in s['jumps'] if not j['crossed']]
Jx = [j for j in J]
fig, ax = plt.subplots(figsize=(8, 5))
xs = np.array([j['yaw_flat'] for j in Jx]); ys = np.abs([j['step'] for j in Jx])
ax.scatter(np.minimum(xs, 2.5), ys, s=26, color=C['blue'], alpha=0.75, edgecolor=C['surface'], linewidth=0.8)
ax.set_xlabel('time the raw SDK body yaw stayed flat (< 0.5 deg/sample) right before the jump (s, clipped at 2.5)')
ax.set_ylabel('|single-sample body-yaw step| (deg)')
share = np.mean(xs >= 0.4) * 100 if len(xs) else 0
ax.set_xlim(0, max(1.2, min(2.6, xs.max() + 0.1)))
ax.set_title(f'Freeze-then-jump: {len(Jx)} body-yaw steps > 45 deg (not across a disconnect)')
ax.axvline(0.4, color=C['orange'], ls=':', lw=1)
ax.text(0.98, 0.97, f'{share:.1f} % of jumps follow a flat stretch >= 0.4 s', transform=ax.transAxes, ha='right', va='top', color=C['ink2'], fontsize=9)
ax.set_ylim(40, 185)
save(fig, 'F04_jump_vs_preceding_freeze', 'Every large body-yaw jump vs how long the KAT SDK timestamp was frozen right before it.')

# ======================= F05 KAT step vs headset turn =======================
Jh = [j for j in J if np.isfinite(j['head'])]
if Jh:
    fig, ax = plt.subplots(figsize=(6.6, 6.2))
    hx = np.array([j['head'] for j in Jh]); ky = np.array([j['step'] for j in Jh])
    same = np.sign(hx) == np.sign(ky)
    agree = same & (np.abs(np.abs(ky) - np.abs(hx)) <= 30)
    ax.scatter(hx[agree], ky[agree], s=30, color=C['aqua'], edgecolor=C['surface'], linewidth=0.8,
               label=f'consistent with headset (real turn): {agree.sum()}', zorder=3)
    ax.scatter(hx[~agree], ky[~agree], s=30, color=C['red'], marker='X', edgecolor=C['surface'], linewidth=0.6,
               label=f'inconsistent (sensor fault): {(~agree).sum()}', zorder=3)
    lim = 190
    ax.plot([-lim, lim], [-lim, lim], color=C['muted'], lw=1, ls='--')
    ax.fill_between([-lim, lim], [-lim - 30, lim - 30], [-lim + 30, lim + 30], color=C['grid'], alpha=0.6, lw=0)
    ax.text(100, 70, 'KAT step = headset turn\n(+/- 30 deg band)', color=C['ink2'], fontsize=8, rotation=0)
    ax.set_xlim(-lim, lim); ax.set_ylim(-lim, lim)
    ax.axhline(0, color=C['muted'], lw=0.6); ax.axvline(0, color=C['muted'], lw=0.6)
    ax.set_xlabel('headset yaw change, 1 s before to 0.3 s after jump (deg)')
    ax.set_ylabel('KAT body-yaw single-sample step (deg)')
    ax.set_title('Body-yaw jump vs headset turn')
    ax.legend(loc='upper left', fontsize=8)
    ratio = np.abs(ky[same & (np.abs(hx) > 5)]) / np.abs(hx[same & (np.abs(hx) > 5)])
    note(fig, f'{len(Jh)} jumps in sessions that logged headset yaw. Same-direction jumps: median |KAT|/|headset| = {np.median(ratio):.1f}x. '
              'Classification mirrors the BodyJumpConfirmation rule (same sign, |diff| <= 30 deg).')
    save(fig, 'F05_jump_vs_headset_turn', 'Each body-yaw jump compared with the headset turn in the same window. Points far from the diagonal are KAT faults, not real turns.')

# ======================= F06 example freeze-then-jump timeline =======================
def pick_example():
    best = None
    for s in REAL:
        if not s['has_head']:
            continue
        for j in s['jumps']:
            if j['crossed'] or not np.isfinite(j['head']):
                continue
            score = (abs(j['step']) - abs(j['head'])) * (j['stamp_freeze'] > 0.4)
            if best is None or score > best[0]:
                best = (score, s, j)
    return best


ex = pick_example()
if ex:
    _, s, j = ex
    d = os.path.join(CAP, s['name'])
    S = load(d, 'samples.csv', ['elapsed_s', 'connected', 'sdk_last_update_raw', 'quat_y', 'quat_w'])
    t = arr(S['elapsed_s']); con = bool_arr(S['connected'])
    yaw = quat_yaw(arr(S['quat_y']), arr(S['quat_w'])); stamp = np.array(S['sdk_last_update_raw'])
    a, b = j['t'] - 6, j['t'] + 4
    m = (t >= a) & (t <= b) & con
    pn = s['pano']
    pm = (pn['t'] >= a) & (pn['t'] <= b)
    yk = np.degrees(np.unwrap(np.radians(yaw[m])))
    hh = np.degrees(np.unwrap(np.radians(pn['head'][pm])))
    hh = hh - hh[0] + yk[0]
    fig, (ax, ax2) = plt.subplots(2, 1, figsize=(11, 5.8), sharex=True, gridspec_kw=dict(height_ratios=[3, 1]))
    ax.plot(t[m], yk, color=C['blue'], lw=1.6, label='KAT body yaw (SDK)')
    ax.plot(pn['t'][pm], hh, color=C['aqua'], lw=1.6, label='Headset yaw (offset-aligned reference)')
    for x0, x1 in s['freezes']:
        if x1 >= a and x0 <= b:
            ax.axvspan(x0, x1, color=C['orange'], alpha=0.18, lw=0)
            ax2.axvspan(x0, x1, color=C['orange'], alpha=0.18, lw=0)
    ax.annotate(f"jump {j['step']:+.0f} deg\nheadset {j['head']:+.0f} deg", (j['t'], yk[np.searchsorted(t[m], j['t'])] if np.searchsorted(t[m], j['t']) < len(yk) else yk[-1]),
                xytext=(18, -10), textcoords='offset points', fontsize=9, color=C['ink'],
                arrowprops=dict(arrowstyle='-', color=C['muted']))
    ax.set_ylabel('yaw (deg, unwrapped)')
    ax.legend(loc='upper left')
    ax.set_title(f"Freeze-then-jump example: {s['name']} at t = {j['t']:.1f} s")
    tt = t[(t >= a) & (t <= b)]; cc = con[(t >= a) & (t <= b)]; ss = stamp[(t >= a) & (t <= b)]
    age, base, prev = [], tt[0], None
    for ti, ci_, si in zip(tt, cc, ss):
        if si != prev:
            base, prev = ti, si
        age.append(ti - base if ci_ else np.nan)
    ax2.plot(tt, age, color=C['ink2'], lw=1.2)
    ax2.set_ylabel('SDK stamp\nage (s)')
    ax2.set_xlabel('session time (s);  orange band = SDK stamp unchanged > 0.2 s while connected')
    fig.tight_layout()
    save(fig, 'F06_freeze_then_jump_example', 'Zoom on the largest KAT fault: the SDK stops updating, the body yaw stays flat while the head turns, then jumps by far more than the head moved.')

# ======================= F07 heading tracking example =======================
def drive_score(s):
    dr = s['drive']
    return np.sum(np.abs(dr['ang']) > 0.05)


if DRIVE_OK:
    s = max(DRIVE_OK, key=drive_score)
    dr = s['drive']
    meas = dr['gyro'] if dr['src'] == 'JetsonGyro' else dr['wheel']
    tgt = dr['target']
    fig, (ax, ax2, ax3) = plt.subplots(3, 1, figsize=(12, 7.2), sharex=True, gridspec_kw=dict(height_ratios=[2.2, 1, 1]))
    T, Y1, Y2, EE, AA, LL = gapnan(dr['t'], np.degrees(np.unwrap(np.radians(np.nan_to_num(tgt)))),
                                   np.degrees(np.unwrap(np.radians(np.nan_to_num(meas)))), dr['err'], dr['ang'], dr['lin'])
    ax.plot(T, Y1, color=C['blue'], lw=1.4, label='target yaw (KAT body turn, 1:1)')
    ax.plot(T, Y2, color=C['orange'], lw=1.4, label=f"robot yaw ({dr['src']})")
    ax.set_ylabel('yaw (deg, unwrapped)')
    ax.legend(loc='upper left')
    ax.set_title(f"Heading tracking during a real-car drive: {s['name']}")
    ax2.plot(T, EE, color=C['ink2'], lw=1)
    ax2.axhline(0, color=C['muted'], lw=0.6)
    ax2.set_ylabel('heading\nerror (deg)')
    ax3.plot(T, AA, color=C['blue'], lw=1, label='requested angular (rad/s)')
    ax3.plot(T, LL, color=C['aqua'], lw=1, label='requested linear (m/s)')
    ax3.set_ylabel('command')
    ax3.legend(loc='lower left', bbox_to_anchor=(0, 1.0), ncol=2, fontsize=8)
    ax3.set_ylim(-0.75, 0.75)
    ax3.set_xlabel('session time (s); gaps = controller inactive')
    fig.tight_layout()
    save(fig, 'F07_heading_tracking_example', 'Body-turn target vs measured robot heading and the resulting /cmd_vel for the drive session with the most turning.')

# ======================= F08 heading error distribution across drive sessions =======================
if DRIVE_OK:
    rows = []
    for s in DRIVE_OK:
        dr = s['drive']
        e = np.abs(dr['err'][np.isfinite(dr['err'])])
        if len(e) > 200:
            rows.append((s, e))
    rows.sort(key=lambda r: r[0]['name'])
    fig, ax = plt.subplots(figsize=(12, 4.6))
    bp = ax.boxplot([r[1] for r in rows], whis=(5, 95), showfliers=False, patch_artist=True, widths=0.6,
                    medianprops=dict(color=C['ink'], lw=1.4))
    for p in bp['boxes']:
        p.set_facecolor(C['blue']); p.set_alpha(0.55); p.set_edgecolor(C['blue'])
    ax.set_xticks(range(1, len(rows) + 1), [f"{r[0]['date'][4:6]}-{r[0]['date'][6:]} {r[0]['time'][:4]}" for r in rows], rotation=70, fontsize=7)
    ax.set_ylabel('|heading error| while active (deg)')
    allE = np.concatenate([r[1] for r in rows])
    ax.set_title(f'Heading error per drive session ({len(rows)} sessions, GYRO STALE runs excluded)')
    note(fig, f'Box = 25-75 %, whiskers = 5-95 %. Pooled: median {np.median(allE):.1f} deg, 95th pct {np.percentile(allE, 95):.1f} deg. '
              'Error = target yaw - robot yaw as logged by the controller (includes the time the car is still catching up).', y=-0.16)
    save(fig, 'F08_heading_error_by_session', 'Distribution of the controller heading error in every valid real-car drive session.')

# ======================= F09 command vs measured yaw rate / lag =======================
def xcorr_lag(t, a, b, maxlag=2.0, dt=0.02):
    tg = np.arange(t[0], t[-1], dt)
    A = np.interp(tg, t, a); B = np.interp(tg, t, b)
    A -= A.mean(); B -= B.mean()
    if A.std() < 1e-6 or B.std() < 1e-6:
        return np.nan, np.nan
    L = int(maxlag / dt)
    cs = [np.corrcoef(A[:len(A) - k], B[k:])[0, 1] for k in range(0, L)]
    k = int(np.nanargmax(cs))
    return k * dt, cs[k]


lags, gains = [], []
scat_c, scat_m = [], []
for s in DRIVE_OK:
    dr, od = s['drive'], s['odom']
    if od is None or np.sum(np.abs(dr['ang']) > 0.05) < 100:
        continue
    o = np.argsort(od['t'])
    ot, ow = od['t'][o], od['w'][o]
    good = np.isfinite(ow)
    ot, ow = ot[good], ow[good]
    rt, ra = dr['t'], dr['ang']
    m = (rt > ot[0]) & (rt < ot[-1]) & np.isfinite(ra)
    if m.sum() < 200:
        continue
    lag, r = xcorr_lag(rt[m], ra[m], np.interp(rt[m], ot, ow))
    if np.isfinite(lag) and r > 0.3:
        lags.append((s, lag, r))
        mw = np.interp(rt[m] + lag, ot, ow)
        cm = ra[m]
        sel = np.abs(cm) > 0.05
        scat_c.append(cm[sel][::3]); scat_m.append(mw[sel][::3])
        steady = np.abs(cm) > 0.3
        if steady.sum() > 30:
            gains.append(np.median(mw[steady] / cm[steady]))
if lags:
    fig, (ax, ax2) = plt.subplots(1, 2, figsize=(12, 4.8))
    cc = np.concatenate(scat_c); mm = np.concatenate(scat_m)
    ax.hexbin(cc, mm, gridsize=45, cmap='Blues', mincnt=1, bins='log')
    lim = max(np.nanmax(np.abs(cc)), 0.2) * 1.1
    ax.plot([-lim, lim], [-lim, lim], color=C['muted'], ls='--', lw=1)
    ax.text(lim * 0.35, lim * 0.85, 'measured = commanded', color=C['ink2'], fontsize=8)
    ax.set_xlabel('requested angular z (rad/s)')
    ax.set_ylabel('measured angular z, /odometry/filtered (rad/s, lag-aligned)')
    ax.set_title('Commanded vs measured yaw rate')
    note(fig, f'{len(lags)} drive sessions. Median steady-state ratio measured/commanded (|cmd| > 0.3 rad/s) = '
              f'{np.median(gains):.2f}. Lag from cross-correlation of Unity-side command log vs Unity-received odometry (same Unity host clock).')
    ax2.hist([l[1] for l in lags], bins=np.arange(0, 2.05, 0.1), color=C['orange'], edgecolor=C['surface'])
    ax2.axvline(np.median([l[1] for l in lags]), color=C['ink2'], ls='--', lw=1)
    ax2.text(np.median([l[1] for l in lags]) + 0.3, ax2.get_ylim()[1] * 0.9, f"median {np.median([l[1] for l in lags]):.2f} s", color=C['ink2'])
    ax2.set_xlabel('command -> odometry response lag (s)')
    ax2.set_ylabel('sessions')
    ax2.set_title('Turn response lag per session')
    fig.tight_layout()
    save(fig, 'F09_turn_command_response', 'Husky yaw-rate response to the requested angular velocity, and the command-to-feedback lag per session.')

# ======================= F10 linear velocity response =======================
lc, lm = [], []
for s in DRIVE_OK:
    dr, od = s['drive'], s['odom']
    if od is None:
        continue
    o = np.argsort(od['t'])
    ot, ov = od['t'][o], od['v'][o]
    g = np.isfinite(ov)
    if g.sum() < 50:
        continue
    ot, ov = ot[g], ov[g]
    m = (dr['t'] > ot[0]) & (dr['t'] < ot[-1] - 0.5) & np.isfinite(dr['lin'])
    if m.sum() < 100:
        continue
    lc.append(dr['lin'][m][::3]); lm.append(np.interp(dr['t'][m][::3] + 0.5, ot, ov))
if lc:
    lc = np.concatenate(lc); lm = np.concatenate(lm)
    fig, ax = plt.subplots(figsize=(6.6, 5.6))
    ax.hexbin(lc, lm, gridsize=40, cmap='Blues', mincnt=1, bins='log')
    ax.plot([-0.4, 0.6], [-0.4, 0.6], color=C['muted'], ls='--', lw=1)
    ax.set_xlabel('requested linear x (m/s)')
    ax.set_ylabel('measured linear x 0.5 s later (m/s)')
    ax.set_title('Walking speed command vs Husky forward speed')
    save(fig, 'F10_linear_command_response', 'Requested forward speed (from KAT walking) vs odometry forward speed, all valid drive sessions.')

# ======================= F11 controller status time share =======================
CATS = [('Tracking / aligned', ('TRACKING ANGLE', 'ANGLE ALIGNED', 'CATCHING UP', 'BRAKING')),
        ('Body-yaw jump guard', ('BODY YAW JUMP', 'BODY HEADING GAP')),
        ('Gyro heading stale', ('GYRO HEADING STALE',)),
        ('Odometry stale / target dropped', ('ODOMETRY STALE', 'TARGET DROPPED', 'NO TURN PROGRESS'))]
CC = [C['blue'], C['orange'], C['yellow'], C['violet']]


def status_share(dr):
    t = dr['t']; dt = np.r_[np.diff(t), 0]
    dt = np.clip(dt, 0, 0.2)
    sh = np.zeros(len(CATS) + 1)
    for st, d_ in zip(dr['status'], dt):
        for k, (_, keys) in enumerate(CATS):
            if any(kk in st for kk in keys):
                # jump/stale/drop take priority over tracking words
                sh[k] += d_
                break
        else:
            sh[-1] += d_
    return sh


def cat_index(st):
    for k in (1, 2, 3, 0):
        if any(kk in st for kk in CATS[k][1]):
            return k
    return 4


rows = []
for s in sorted(DRIVE, key=lambda s: s['name']):
    dr = s['drive']; t = dr['t']; dt = np.clip(np.r_[np.diff(t), 0], 0, 0.2)
    sh = np.zeros(5)
    rev = 0.0
    for st, d_ in zip(dr['status'], dt):
        sh[cat_index(st)] += d_
        if 'REVERSE' in st:
            rev += d_
    if sh.sum() > 10:
        rows.append((s, sh / sh.sum() * 100, rev / sh.sum() * 100))
fig, ax = plt.subplots(figsize=(10, max(4, len(rows) * 0.22)))
y = np.arange(len(rows))
left = np.zeros(len(rows))
for k in range(4):
    vals = np.array([r[1][k] for r in rows])
    ax.barh(y, vals, left=left, color=CC[k], height=0.78, label=CATS[k][0], edgecolor=C['surface'], linewidth=1)
    left += vals
ax.set_yticks(y, [f"{'* ' if r[0]['stale'] else ''}{r[0]['date'][4:6]}-{r[0]['date'][6:]} {r[0]['time'][:4]}" for r in rows], fontsize=6.5)
ax.invert_yaxis()
ax.set_xlim(0, 100)
ax.set_xlabel('% of active time')
ax.set_title(f'DirectAngle controller state while active ({len(rows)} drive sessions)')
ax.legend(loc='upper center', bbox_to_anchor=(0.5, -0.06), ncol=4, fontsize=8)
note(fig, '* = session contains GYRO HEADING STALE rows (treated as a failed run and excluded from F07-F10, F12-F13).', y=-0.035)
ax.grid(axis='y', visible=False)
save(fig, 'F11_controller_status_share', 'Share of active time spent tracking vs in each protection state. Rows with yellow are the failed runs where the Jetson gyro heading went stale.')

# ======================= F12 feedback freshness =======================
intervals, ages = [], []
for s in DRIVE_OK:
    od = s['odom']
    if od is not None:
        r = np.sort(od['rx'][np.isfinite(od['rx'])])
        if len(r) > 10:
            intervals.append(np.diff(r))
    dr = s['drive']
    a = dr['pose_age'][np.isfinite(dr['pose_age'])]
    if dr['src'] == 'JetsonGyro' and len(a):
        ages.append(a)
fig, axs = plt.subplots(1, 2, figsize=(12, 4))
if intervals:
    iv = np.concatenate(intervals)
    axs[0].hist(np.clip(iv * 1000, 0, 300), bins=100, color=C['blue'])
    axs[0].set_yscale('log')
    axs[0].set_xlabel('Unity receipt interval of /odometry/filtered (ms, clipped at 300)')
    axs[0].set_ylabel('count (log)')
    axs[0].set_title(f'Odometry feedback cadence (median {np.median(iv) * 1000:.0f} ms)')
if ages:
    ag = np.concatenate(ages)
    axs[1].hist(np.clip(ag * 1000, 0, 500), bins=100, color=C['aqua'])
    axs[1].set_yscale('log')
    axs[1].set_xlabel('Jetson gyro pose message age at use (ms, clipped at 500)')
    axs[1].set_title(f'Gyro heading age (median {np.median(ag) * 1000:.0f} ms, p95 {np.percentile(ag, 95) * 1000:.0f} ms)')
note(fig, 'Receipt intervals are measured on the Unity host clock. Pose age = time since the Jetson pose was received in Unity; it does not include upstream/network delay (clocks not synchronized).')
fig.tight_layout()
save(fig, 'F12_feedback_freshness', 'Cadence of odometry feedback and age of the Jetson gyro heading when the controller used it.')

# ======================= F13 panorama compensation example =======================
cand = [s for s in DRIVE_OK if 'pano' in s and s['has_head']]
if cand:
    s = max(cand, key=drive_score)
    pn = s['pano']
    m = np.isfinite(pn['robot']) & np.isfinite(pn['sphere']) & np.isfinite(pn['head'])
    fig, ax = plt.subplots(figsize=(12, 4.2))
    un = lambda a: np.degrees(np.unwrap(np.radians(a)))
    ax.plot(pn['t'][m], un(pn['robot'][m]), color=C['orange'], label='robot heading in XR world (deg)')
    ax.plot(pn['t'][m], un(pn['sphere'][m]), color=C['blue'], label='video sphere yaw (compensation)')
    ax.plot(pn['t'][m], un(pn['head'][m]), color=C['aqua'], lw=1.1, label='headset yaw')
    ax.set_xlabel('session time (s)')
    ax.set_ylabel('yaw (deg, unwrapped)')
    ax.set_title(f"360 video heading compensation: {s['name']}")
    ax.legend(loc='lower left', bbox_to_anchor=(0, 1.06), ncol=3, fontsize=8)
    save(fig, 'F13_panorama_compensation', 'Video-sphere yaw counter-rotates with the robot heading so the panorama stays world-fixed while the operator turns their head.')

# ======================= F14 SLAM trajectories (minimap demo) =======================
tr = [s for s in SESS if s['slam'] is not None]
for s in tr:
    p = s['slam']
    s['_path'] = float(np.nansum(np.hypot(np.diff(p['x']), np.diff(p['y']))))
tr = sorted([s for s in tr if s['_path'] > 2], key=lambda s: -s['_path'])[:6]
if tr:
    n = len(tr)
    cols_ = min(3, n); rws = int(math.ceil(n / cols_))
    fig, axs = plt.subplots(rws, cols_, figsize=(4.2 * cols_, 4.0 * rws), squeeze=False)
    for ax, s in zip(axs.flat, tr):
        p = s['slam']
        ax.plot(p['x'], p['y'], color=C['blue'], lw=1.6)
        ax.plot(p['x'][0], p['y'][0], 'o', color=C['aqua'], ms=8, mec=C['surface'], mew=2)
        ax.plot(p['x'][-1], p['y'][-1], 's', color=C['orange'], ms=8, mec=C['surface'], mew=2)
        ax.set_aspect('equal', adjustable='datalim')
        ax.set_title(f"{ddate(s)} {s['time'][:2]}:{s['time'][2:4]}  path {s['_path']:.1f} m", fontsize=9)
        ax.set_xlabel('SLAM x (m)'); ax.set_ylabel('SLAM y (m)')
    for ax in list(axs.flat)[n:]:
        ax.set_visible(False)
    fig.legend(handles=[Line2D([], [], marker='o', color=C['aqua'], ls='', ms=8, label='start'),
                        Line2D([], [], marker='s', color=C['orange'], ls='', ms=8, label='end')],
               loc='upper right', ncol=2)
    fig.suptitle('Robot trajectories from livoscan SLAM (as received by the wrist minimap)', x=0.01, ha='left',
                 fontweight='bold', color=C['ink'])
    fig.tight_layout()
    save(fig, 'F14_slam_trajectories', 'Longest SLAM trajectories received by the Unity wrist minimap (fresh pose samples only).')

# ======================= F15 per-day KAT fault summary =======================
day = collections.OrderedDict()
for s in sorted(REAL, key=lambda s: s['name']):
    k = ddate(s)
    dd = day.setdefault(k, dict(min=0.0, jumps=0, fr=0))
    dd['min'] += s['connected_s'] / 60; dd['jumps'] += len(s['jumps']); dd['fr'] += sum(b - a >= 0.95 for a, b in s['freezes'])
ks = list(day)
fig, axs = plt.subplots(2, 1, figsize=(12, 5.6), sharex=True)
xx = np.arange(len(ks))
axs[0].bar(xx, [day[k]['jumps'] / max(day[k]['min'], 1e-6) * 10 for k in ks], color=C['red'], width=0.7)
axs[0].set_ylabel('jumps > 45 deg\nper 10 min')
axs[0].set_title('KAT body-orientation faults per test day (normalized by connected time)')
axs[1].bar(xx, [day[k]['fr'] / max(day[k]['min'], 1e-6) * 10 for k in ks], color=C['orange'], width=0.7)
axs[1].set_ylabel('freezes >= 0.95 s\nper 10 min')
axs[1].set_xticks(xx, [k[5:] for k in ks], rotation=60, fontsize=7.5)
fig.tight_layout()
save(fig, 'F15_kat_faults_per_day', 'KAT jump and long-freeze rates per test day.')

# ======================= F16-F17 Husky motor logs (2026-11-20) =======================
HB = os.path.join(REPO, 'HuskyBags_from_robot_20261120')


def rcsv(p):
    if not os.path.exists(p):
        return None
    with open(p, encoding='utf-8', newline='') as f:
        R = list(csv.DictReader(f))
    return R if R else None


for tag in ('kb', 'kb2', 'kb3'):
    cmd = rcsv(os.path.join(HB, f'{tag}_cmdout.csv'))
    js = rcsv(os.path.join(HB, f'{tag}_js.csv'))
    stt = rcsv(os.path.join(HB, f'{tag}_status.csv'))
    if not (cmd and js and stt):
        continue
    t0 = min(F(cmd[0]['%time']), F(js[0]['%time'])) / 1e9
    tc = np.array([F(r['%time']) / 1e9 - t0 for r in cmd])
    tj = np.array([F(r['%time']) / 1e9 - t0 for r in js])
    ts = np.array([F(r['%time']) / 1e9 - t0 for r in stt])
    fig, axs = plt.subplots(4, 1, figsize=(12, 8.4), sharex=True)
    axs[0].plot(tc, [F(r['field.twist.linear.x']) for r in cmd], color=C['blue'], label='linear x (m/s)')
    axs[0].plot(tc, [F(r['field.twist.angular.z']) for r in cmd], color=C['orange'], label='angular z (rad/s)')
    axs[0].set_ylabel('cmd_vel_out'); axs[0].legend(loc='upper left', ncol=2)
    axs[0].set_title(f'Husky A200 drive log {tag} (2026-11-20): command, wheel speed, driver current')
    vl = np.array([(F(r['field.velocity0']) + F(r['field.velocity2'])) / 2 for r in js])
    vr = np.array([(F(r['field.velocity1']) + F(r['field.velocity3'])) / 2 for r in js])
    axs[1].plot(tj, vl, color=C['blue'], label='left wheels'); axs[1].plot(tj, vr, color=C['orange'], label='right wheels')
    axs[1].set_ylabel('wheel speed\n(rad/s)'); axs[1].legend(loc='upper left', ncol=2)
    axs[2].plot(ts, [F(r['field.left_driver_current']) for r in stt], color=C['blue'], marker='o', ms=3, label='left driver')
    axs[2].plot(ts, [F(r['field.right_driver_current']) for r in stt], color=C['orange'], marker='o', ms=3, label='right driver')
    axs[2].set_ylabel('driver current\n(A)'); axs[2].legend(loc='upper left', ncol=2)
    flags = ['timeout', 'lockout', 'e_stop', 'ros_pause', 'current_limit']
    for k, fl in enumerate(flags):
        on = np.array([F(r['field.' + fl], 0) > 0 for r in stt])
        axs[3].scatter(ts[on], np.full(on.sum(), k), s=14, color=C['red'], marker='s')
        axs[3].scatter(ts[~on], np.full((~on).sum(), k), s=4, color=C['grid'], marker='s')
    axs[3].set_yticks(range(len(flags)), flags, fontsize=8)
    axs[3].set_ylim(-0.6, len(flags) - 0.4)
    axs[3].grid(axis='y', visible=False)
    axs[3].set_xlabel('time (s) from log start;  red = flag set')
    fig.tight_layout()
    save(fig, f'F16_husky_drive_log_{tag}', f'Husky {tag} log: /husky_velocity_controller/cmd_vel_out, wheel joint velocities, motor-driver currents and status flags.')

# ======================= F18 regression tests history =======================
hist = [('10-13', 74), ('10-13', 88), ('10-14', 95), ('10-15', 95), ('10-20', 78), ('10-20', 84), ('10-20', 85), ('10-22', 94), ('10-24', 94)]
fig, ax = plt.subplots(figsize=(8, 3.6))
ax.plot(range(len(hist)), [h[1] for h in hist], color=C['blue'], marker='o', ms=7, mec=C['surface'], mew=2)
for i, (d_, n_) in enumerate(hist):
    ax.text(i, n_ + 1.2, str(n_), ha='center', fontsize=8, color=C['ink2'])
ax.set_xticks(range(len(hist)), [h[0] for h in hist])
ax.set_ylim(60, 102)
ax.set_ylabel('offline regression tests passing')
ax.set_title('KAT DirectAngle offline regression suite size (all passing)')
ax.annotate('navigation feature removed', (4, 78), xytext=(4.3, 66), fontsize=8, color=C['ink2'],
            arrowprops=dict(arrowstyle='-', color=C['muted']))
note(fig, 'From docs/PROJECT_STATUS_2026-10-24.md section 5 (historical records; only the 10-24 run was re-executed then). Count drops reflect removed features, not regressions.')
save(fig, 'F18_regression_suite_history', 'Size of the offline regression suite over time, per the project status document.')

# ---------------- index ----------------
with open(os.path.join(OUT, 'README.md'), 'w', encoding='utf-8') as f:
    f.write('# Experiment figures\n\nGenerated by `make_figures.py` from `KATCaptures/` (gitignored raw data) and '
            '`HuskyBags_from_robot_20261120/`. Each figure is saved as PNG (200 dpi) and PDF. '
            'Per-session numbers are in `session_summary.csv`.\n\n')
    f.write(f'- KAT sessions parsed: {len(SESS)}; with > 20 s connected KAT data: {len(REAL)}; '
            f'DirectAngle real-car drive sessions: {len(DRIVE)} ({len(DRIVE) - len(DRIVE_OK)} with GYRO HEADING STALE, '
            'excluded from tracking/response figures as failed runs).\n\n')
    f.write('| Figure | What it shows |\n|---|---|\n')
    for n_, c_ in FIGS:
        f.write(f'| [{n_}]({n_}.png) | {c_} |\n')
print('done', len(FIGS), 'figures')
