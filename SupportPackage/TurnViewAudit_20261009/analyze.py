import csv
import json
import math
import re
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = Path(__file__).resolve().parent
sessions = {}
current = None
latest_t = None

def values(text):
    result = {}
    for key, val in re.findall(r'\b(\w+)=([^\s|]+)', text):
        val = val.removesuffix('deg/s')
        try:
            result[key] = float(val)
        except ValueError:
            result[key] = val
    return result

for line in (OUT / 'editor-relevant.log').read_text(encoding='utf-8-sig').splitlines():
    if '[KAT Capture]' in line and '.zip' not in line:
        m = re.search(r'KAT_MiniS_\d{8}_\d{6}_\d+_\w+', line)
        if m:
            current = m.group()
            sessions.setdefault(current, {'control': [], 'pose': [], 'events': []})
            latest_t = None
    if not current:
        continue
    number, text = line.split(':', 1)
    kind = ('control' if 'KAT DIRECT ANGLE |' in text else
            'pose' if 'KAT POSE RAW |' in text else None)
    if kind:
        row = values(text)
        row['line'] = int(number)
        latest_t = row.get('t', latest_t)
        if kind == 'control':
            m = re.search(r'Turn=\[([^\]]*)\]', text)
            row['turn_status'] = m.group(1) if m else ''
        sessions[current][kind].append(row)
    elif '[KAT DIRECT ANGLE]' in text or 'Panorama' in text or 'WHEP' in text:
        sessions[current]['events'].append({'t_near': latest_t, 'line': int(number), 'text': text})

def spans(rows, predicate, gap=.35):
    groups = []
    group = []
    for row in rows:
        if predicate(row):
            if group and row['t'] - group[-1]['t'] > gap:
                groups.append(group)
                group = []
            group.append(row)
        elif group:
            groups.append(group)
            group = []
    if group:
        groups.append(group)
    return groups

def brief(row):
    keys = ['t', 'BodyRaw', 'YawRate', 'BodyTurn', 'TargetTurn', 'RobotTurn', 'Error',
            'Angular', 'AngularCommand', 'RobotYawRate', 'Linear', 'turn_status', 'line']
    return {k: row[k] for k in keys if k in row}

summary = {}
for name, data in sessions.items():
    rows = data['control']
    active = [r for r in rows if r.get('Activated') == 'True']
    opposite = spans(rows, lambda r: r.get('Activated') == 'True'
                     and abs(r.get('YawRate', 0)) > 10
                     and abs(r.get('AngularCommand', 0)) > .05
                     and r.get('YawRate', 0) * r.get('AngularCommand', 0) > 0)
    events = data['events']
    counts = Counter('realign' if 'realign=True' in e['text'] else
                     'activation' if 'Activation=' in e['text'] else
                     'kat_disconnect' if 'KAT DISCONNECTED;' in e['text'] else
                     'kat_recovery' if 'KAT input recovered' in e['text'] else
                     'body_jump_rejected' if 'Skipped body yaw jump' in e['text'] else
                     'body_resync' if 'BODY SENSOR RESYNCED' in e['text'] else
                     'target_drop' if 'Target dropped' in e['text'] or 'TARGET DROPPED' in e['text'] else
                     'odom_reanchor' if 'ODOMETRY GAP / TARGET RE-ANCHORED' in e['text'] else
                     'panorama_calibration' if 'Panorama forward calibrated' in e['text'] else
                     'other' for e in events)
    summary[name] = {
        'logged_rows': len(rows), 'active_rows': len(active),
        'max_abs_error': max((abs(r['Error']) for r in active if math.isfinite(r.get('Error', math.nan))), default=None),
        'turn_status_counts': dict(Counter(r['turn_status'] for r in active)),
        'event_counts': dict(counts),
        'opposite_live_direction_spans': [
            {'start': brief(g[0]), 'end': brief(g[-1]),
             'peak': brief(max(g, key=lambda r: abs(r['AngularCommand'])))}
            for g in opposite if g[-1]['t'] - g[0]['t'] >= .25],
        'important_events': [e for e in events if any(s in e['text'] for s in (
            'Activation=', 'Skipped body yaw jump', 'BODY SENSOR RESYNCED',
            'Target dropped', 'TARGET DROPPED', 'ODOMETRY GAP', 'Panorama forward calibrated'))],
    }
    with (OUT / (name + '-control.csv')).open('w', encoding='utf-8', newline='') as f:
        keys = sorted(set().union(*(set(r) for r in rows))) if rows else ['t']
        writer = csv.DictWriter(f, fieldnames=keys)
        writer.writeheader()
        writer.writerows(rows)

capture_summary = {}
for directory in sorted((ROOT / 'KATCaptures').glob('KAT_MiniS_20261009*')):
    if not directory.is_dir():
        continue
    with (directory / 'samples.csv').open(encoding='utf-8-sig', newline='') as f:
        rows = list(csv.DictReader(f))
    disconnects = []
    start = None
    previous = None
    jumps = []
    for r in rows:
        t = float(r['elapsed_s'])
        if r['connected'].lower() != 'true':
            if start is None:
                start = t
            previous = None
            continue
        if start is not None:
            disconnects.append([round(start, 3), round(t, 3), round(t-start, 3)])
            start = None
        x, y, z, w = (float(r['quat_'+c]) for c in 'xyzw')
        norm = x*x+y*y+z*z+w*w
        if norm == 0:
            continue
        yaw = math.degrees(math.atan2(2*(w*y+x*z), norm-2*(x*x+y*y)))
        if previous:
            dt = t - previous[0]
            step = (yaw-previous[1]+180) % 360 - 180
            if abs(step) > 45 and dt < .3:
                jumps.append({'t': round(t,3), 'dt': round(dt,4), 'step': round(step,2),
                              'before': round(previous[1],2), 'after': round(yaw,2)})
        previous = (t, yaw)
    if start is not None and rows:
        end = float(rows[-1]['elapsed_s'])
        disconnects.append([round(start,3),round(end,3),round(end-start,3)])
    capture_summary[directory.name] = {'rows': len(rows), 'disconnects': disconnects, 'jumps_over45': jumps}

(OUT / 'summary.json').write_text(json.dumps(summary, indent=2, ensure_ascii=False), encoding='utf-8')
(OUT / 'capture-summary.json').write_text(json.dumps(capture_summary, indent=2), encoding='utf-8')
for name, item in summary.items():
    print(name)
    print(json.dumps({k:v for k,v in item.items() if k not in ('important_events', 'opposite_live_direction_spans')}, ensure_ascii=False))
    print('opposite spans:', len(item['opposite_live_direction_spans']))
    for span in sorted(item['opposite_live_direction_spans'], key=lambda g:g['end']['t']-g['start']['t'], reverse=True)[:6]:
        print(json.dumps(span, ensure_ascii=False))
    if name[-6:] == 'ecbcf6':
        print('IMPORTANT EVENTS', json.dumps(item['important_events'], ensure_ascii=False))
    cs = capture_summary.get(name, {})
    print('capture: disconnect count=', len(cs.get('disconnects', [])),
          'max duration=', max((d[2] for d in cs.get('disconnects',[])), default=0),
          'jumps=', cs.get('jumps_over45', []))

name = 'KAT_MiniS_20261009_170326_316_ecbcf6'
print('\nLATEST DISCONNECTS', capture_summary[name]['disconnects'])
rows = sessions[name]['control']
for start, end in [(33.5,35.2),(55.3,60.8),(100.8,107),(142,147.6),(150.5,153.4)]:
    print('WINDOW', start, end)
    selected = [r for r in rows if start <= r['t'] <= end]
    for i, r in enumerate(selected):
        if i % 3 == 0 or i == len(selected)-1:
            print(json.dumps(brief(r), ensure_ascii=False))
directory = ROOT / 'KATCaptures' / name
with (directory/'calibration.csv').open(encoding='utf-8-sig',newline='') as f:
    calibration = list(csv.DictReader(f))
resets = []
last = None
for r in calibration:
    t = float(r['elapsed_s'])
    value = float(r['last_calibrated_time_escaped_raw'])
    if last and value < last[1] - .1:
        resets.append({'t':t,'before':last[1],'after':value})
    last = (t,value)
print('CALIBRATION TIMER DECREASES', resets)
pose = sessions[name]['pose']
print('RAW QUATERNION JUMPS', json.dumps([
    {k:r[k] for k in ['t','SensorYaw','PrevYaw','YawStep','QuaternionStep','SampleDtMs','Stamp','PrevStamp','Activated','line']}
    for r in pose if abs(r.get('YawStep',0))>45],ensure_ascii=False))
small_stalls = spans(rows, lambda r:r.get('Activated')=='True' and 2<abs(r.get('Error',0))<7.16
                    and abs(r.get('YawRate',0))<2 and abs(r.get('RobotYawRate',0))<1)
print('LONG SMALL ERROR SPANS',[(brief(g[0]),brief(g[-1])) for g in small_stalls if g[-1]['t']-g[0]['t']>1])
