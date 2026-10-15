# slam_drift — 闭环首尾漂移

从录好的包里取出每个位姿话题（FAST-LIVO2 里程计、`/odometry/filtered` 等）的起点和终点，
算出各自认为车最后停在起点的哪里；再和卷尺实测值比较，得到漂移。

## 安装（Windows 这台电脑上已装好）

```bash
python -m venv Tools/slam_drift/.venv
Tools/slam_drift/.venv/Scripts/python -m pip install -r Tools/slam_drift/requirements.txt
```

在 Jetson 上运行也可以：`pip3 install rosbags numpy matplotlib`。

## 用法

```bash
Tools/slam_drift/.venv/Scripts/python Tools/slam_drift/slam_drift.py 包名.bag --measured 前 左 偏航
```

- `--measured 12 -5 3`：车最终停在起点标记**前方 12 cm、右侧 5 cm**（左为正），车头**向左偏 3°**（逆时针为正）。
  不加这个参数时，只输出各话题自己的首尾位移。
- `--window 3`：起点和终点各取 3 秒做平均，这段时间车必须静止。
- `--topic /aft_mapped_to_init`：只分析指定话题，可以重复使用；默认分析所有位姿话题。
- 轨迹图默认保存为 `包名_trajectory.png`，可以用 `--out` 指定路径。

## 输出各列的含义

| 列 | 含义 |
|---|---|
| fwd / left / dz / dyaw | 该话题认为的终点相对起点的位移，以起点时的车头方向为前方 |
| ERR cm | 水平漂移：该话题的位移和实测位移之间的距离 |
| ERR % | 漂移占总里程的百分比 |
| ERR yaw | 航向漂移 |

注：SLAM 位姿是雷达 IMU 的位置，不是底盘中心。如果终点车头偏了 θ，会额外多出约 0.2 m × θ(rad) 的误差，偏 5° 时约 2 cm。
