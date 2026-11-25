# 项目现状与工作交接（2026-10-24）

这份文档汇总到 2026-10-24 为止已经完成的工作、做过哪些测试、当前工作区的状态，以及接手或继续实验前需要知道的事。细节以各专项文档为准，入口见 [文档索引](README.md)。

阅读时请注意证据层级（定义见 [工程流程 §6.1](ENGINEERING_WORKFLOW.md)）：**离线回归通过 ≠ Unity 编译通过 ≠ 实车通过**。下文每条结论都标明了它属于哪一层，以及是本次（10-24）重新执行的，还是引用历史记录。

---

## 1 项目是什么

操作者站在 KAT Walk mini S 跑步机上踏步、转身，Unity 把动作换成 `/cmd_vel` 发给远端的 Clearpath Husky A200；车上的 Insta360 X5 拼接 360° 画面推流到 Meta Quest 3；Jetson 上的 `livoscan-mywork` 提供 SLAM 位姿、雷达陀螺仪航向、点云和物体，用于转向修正和手腕小地图。

```
KAT mini S ──SDK──> DirectAngle 控制器 ──/cmd_vel (ROS-TCP)──> Husky 底盘
     ▲                    ▲   ▲                                   │
  头显朝向(新)            │   └── /odometry/filtered (50 Hz) ─────┘
                          └────── Jetson livoscan 桥 /ws：pose、yaw_odom、yaw_rate、heading_t
X5 ─SDK拼接─> H.264/RTSP ─> MediaMTX ─WHEP─> Unity 视频球（按车头航向补偿）
livoscan /map/points.bin + /ws objects ─> 手腕小地图
```

接口字段、单位、坐标和时间语义的完整说明见 [工程流程 §8.2–8.3](ENGINEERING_WORKFLOW.md)。

## 2 当前版本基线

| 项 | 值 | 依据 |
|---|---|---|
| 分支 | `feature/husky-navigation-panorama-20261015` | `git status` |
| 最新提交 | `3046a42` 物体名改为显示 display_label | `git log` |
| Unity | 2022.3.4f1 | `ProjectSettings/ProjectVersion.txt` |
| 离线测试框架 | .NET 10（只是测试工程目标框架，不是 Unity 运行时） | `Tests.csproj` |
| 对端 | `livoscan-mywork`，10-21 核对基线 `e8ffe96`；Jetson 实际部署版本**未核对** | 工程流程 §8 |

### 2.1 尚未提交的改动

| 文件 | 内容 |
|---|---|
| `Assets/KAT/BodyJumpConfirmation.cs`（新） | 头显确认 KAT 身体角跳变，见 §4 |
| `Assets/KAT/KatHuskyDirectAngleController.cs` | 接入头显确认；激活结束时自动退出倒车 |
| `Assets/WebRTC/KatStatusHud.cs` | 倒车时 HUD 第一行红字 `!! REVERSE ON !!` |
| `SupportPackage/KatDirectAngleRegression/BodyJumpTests.cs`（新）及 `Program.cs`、`Stubs.cs`、`Tests.csproj` | 9 项新回归，含 10-21 实车数据回放 |
| `Assets/MainScene.unity` | 你在 Unity 里保存的场景状态，见 §3 |
| `Assets/KAT/DirectAngle-测试说明.md`、`README.md` | 10-22 记录、流程入口 |
| `AGENTS.md`、`docs/ENGINEERING_WORKFLOW.md`（新） | 10-21 建立的协作流程 |
| `docs/README.md`、本文件（新） | 10-24 文档整理 |

这些改动还没有 commit，也没有推送。

## 3 当前场景配置（工作区里的 `MainScene.unity`）

2026-10-24 从磁盘上的场景文件读出。它是**保存值**，不等于 Play 时的运行状态。

| 组件 | 状态 |
|---|---|
| `KatHuskyDirectAngleController`（DirectAngle） | **启用**，唯一的 `/cmd_vel` 发布者 |
| `KatStatusHud` | 启用 |
| `HuskyController`（键盘 `CmdVelPublisher`） | **禁用** |
| `KatHuskyDirectController`（旧开环版） | 已挂载，禁用 |
| `MediaMtxWhepReceiver`、`HuskyBodyReferenceOverlay` | 已挂载 |
| `LivoscanDataSource`、`LivoscanWorldClient`、`LivoscanWristMinimap` | 已挂载 |

- `headingSource: 2`，即 `JetsonGyro`。场景里已经写入这个枚举，旧的 `useSlamHeadingCorrection` 字段也已清掉。
- 10-22 新增的四个参数（`confirmBodyJumpWithHeadset` 等）**还没有写进场景**，运行时用源码默认值：开启、回看 1 s、窗口 0.3 s、容差 30°。下次在 Inspector 里保存场景时才会写入。
- `reverseModeLatch: 1`：按一下 B 切换倒车。
- 前进 / 转向上限保存值为 0.5 m/s、1 rad/s；视频地址 `192.168.2.36:8889`，livoscan `192.168.2.22:8770`，ROS `192.168.2.36:10000`（详见工程流程 §8.2）。

> 工程流程 §8.2 记录的是 10-21 的提交基线（DirectAngle 禁用、键盘启用、`headingSource` 未保存），和现在的工作区不同，以本节为准。根据之前的经验，场景文件请在 Unity Inspector 里修改并保存，不要直接改磁盘上的 `.unity` 文件，否则会被编辑器覆盖。

## 4 最近一轮工作：头显确认 KAT 跳变、暂停退出倒车（10-22，未提交）

### 4.1 问题和证据

10-21 实车采集 `KATCaptures/KAT_MiniS_20261021_213032_203_8c2aa0` 中有 7 次身体角跳变，全部被跳变保护丢掉了。用 `control_panorama.csv` 的头显朝向 `head_world_yaw_deg` 对照：

- **137.2 s 是真转身**：头 1 s 内转了 +101°，KAT 读数滞后，然后一步跳 +90° 追上来。这一步被丢掉，所以车没跟着转。
- **218.5 s（+123°）、223.3 s（−131°）是传感器故障**：KAT 卡住约 0.7 s 后跳出错误读数，期间头部变化不到 10°。49、52、65、66 s 的跳变也是故障。
- 同一次实验中误按了 B，倒车模式在之后持续了一分多钟，没有被注意到。

### 4.2 做法

- **头显确认**（`BodyJumpConfirmation.cs`）。身体角跳变后的 0.3 s 内，比较"跳变前 1 s 到现在"身体和头显各转了多少。方向相同、相差不超过 30° 就当作真转身接受，否则照旧丢弃。跳变量可以 ±360° 去匹配头显，所以超过 180° 的转身不会丢角度。
- 什么时候不会确认：只转头（没有身体跳变，根本不会走到这段逻辑）；没有 `Camera.main`；头显样本超过 0.1 s 没有更新；头显历史不够 1 s；头显转动方向相反或幅度差太多。这些情况都和以前一样丢弃跳变。
- 头显和身体样本都用 `Time.realtimeSinceStartup`，与控制器的 `now` 同一时基。
- **倒车**：只要激活状态结束（按 Y、扳机关闭、急停、KAT 输入丢失），就自动退出 REVERSE。倒车时 HUD 第一行显示红字 `!! REVERSE ON !!`，并提示退出方式。

### 4.3 已知局限（未验证）

- 确认次数 `ConfirmedBodyJumps` 只在控制器属性和 Debug 日志里，**没有写进采集 CSV**。分析采集数据时只能间接判断：看 `control_request.csv` 中目标角的突变，以及 `status` 是否出现 `BODY YAW JUMP`。
- 头显 yaw 取的是 XR 世界系。如果在跳变窗口内恰好发生头显重定位（recenter），头显 yaw 也会突变，理论上可能误确认一次。这种情况还没有测试过。

### 4.4 测试结果

| 层级 | 结果 | 何时 |
|---|---|---|
| 离线回归 | **94 项通过**，含 9 项新增；10-21 回放中只有 137 s 被确认，49/52/65/66/218/223 s 都被拒绝 | **10-24 本次重跑** |
| Unity 编译 | 10-22 记录为 Unity 2022.3.4 编译通过 | 历史记录，本次未重跑 |
| 实车 | **未验收** | — |

实车验收标准：快速转身 90°、180° 各 3 次，车都应跟上；只转头时车不转；按 Y 或关扳机后 HUD 上的 REVERSE 红字消失，再激活时为前进。

### 4.5 10-22 已有一次实车采集，尚未分析

`KATCaptures/KAT_MiniS_20261022_170941_472_f8fc17`，305 s，有底盘指令和里程计。我在 10-24 只读查看了几项：

- 配置快照里 `confirmBodyJumpWithHeadset=true`、`headingSource=2`；`control_request.csv` 全程 `heading_source=JetsonGyro`，`slam_status=SLAM NOT USED (GYRO)`。
- `status` 中 `BODY YAW JUMP (TURN OFF)` 有 482 行（其中 193 行在倒车中），`BODY HEADING GAP` 有 290 行，带 `REVERSE` 的状态共 1297 行。

以上只是计数，**不能**据此说明头显确认是否起作用。下一步是对这次采集做与 10-21 相同的逐次跳变对照。

## 5 工作时间线

按专项文档整理，越往下越新。"当时验证"指当时记录的层级。

| 时间 | 工作 | 当时验证 | 文档 |
|---|---|---|---|
| 08-10~08-15 | KAT 闭环 / settle-anchor / 开环 `KatHuskyDirectController`；World Space HUD | 部分真机观察 | [KAT 工作记录](../Assets/KAT/README.md) |
| 08-14~08-15 | Insta360 X5 → Husky Docker 拼接 → H.264/RTSP → MediaMTX → WHEP → Quest 3 | Quest 3 真机看到画面 | [联调记录](../Insta360-联调记录-2026-08-14.md)、[部署说明](../Deployment/HuskyInsta360/部署说明-相机采集与360拼接.md) |
| 09-10~09-11 | 倒车画面左漂：轮编码器左右不一致导致虚假偏航 | ROS bag 分析 | [倒车漂移](../SupportPackage/ReverseDrift_2026-09-10.md)、[新包分析](../SupportPackage/NewBag_Findings_2026-09-11.md) |
| 09-13~09-15 | DirectAngle 第二轮：去掉转向锁死、滤波、踏步脉冲回退、提速；实测车只转到命令值的 0.41~0.47 倍 | 离线回归 + 部分实测 | [DirectAngle 测试说明](../Assets/KAT/DirectAngle-测试说明.md)、[大转弯追赶](../SupportPackage/KatDirectAngleRegression/TurnAudit-结果.md) |
| 10-10 | 连续导航 CurrentDirection、速度辅助 | 67 项离线 | 同上（**当晚已回退，10-20 已删除**） |
| 10-13 | 退回累计转角；`RobotFeedbackFreshness` 检测反馈积压（曾积压到约 23 s）；1:1 前馈 | 74→88 项离线、Unity 编译 | 同上、[反馈传输审计](../SupportPackage/FeedbackTransportAudit/findings_20261013.md) |
| 10-14 | SLAM 航向校正轮式里程计；全景改用校正朝向；扳机自动对齐车头 | 94→95 项离线、Unity 编译 | DirectAngle 测试说明 |
| 10-15 | 整理成当前分支；Livoscan 手腕小地图；采集与诊断工具 | 95 项离线 + Python 23 项 | [README](../README.md) 10-15 节 |
| 10-20 | 删除未使用的导航功能（`cf9b622`）；JetsonGyro 航向来源（`21b3ceb`）；按 `heading_t` 去重（`fa4eea4`） | 78→84→85 项离线、Unity 编译 | DirectAngle 测试说明 |
| 10-20 之后 | 物体名改显示 `display_label`（`3046a42`） | — | 提交信息 |
| 10-21 | 建立协作流程 `AGENTS.md`、`docs/ENGINEERING_WORKFLOW.md`，核对双方源码基线 | 只读审阅 | [工程流程](ENGINEERING_WORKFLOW.md) |
| 10-22 | 头显确认跳变、暂停退出倒车（未提交） | 94 项离线、Unity 编译 | §4 |
| 10-24 | 文档整理：本文件、[文档索引](README.md)、几处过期说明加更正注 | 本次重跑全部离线检查 | §6 |

回归项数会随功能删减而减少（例如 10-20 删除导航测试后从 95 降到 78），项数变化本身不代表退步。

## 6 本次（10-24）实际执行的检查

环境：本机 Windows 11，Git Bash，.NET 10，Python 3.12。从仓库根目录执行。

| 命令 | 结果 |
|---|---|
| `dotnet run --no-restore --project SupportPackage/KatDirectAngleRegression/Tests.csproj` | 94 tests passed，退出码 0；10-21 回放未跳过 |
| `dotnet run --no-restore --project SupportPackage/KatMiniSCapture/Tests.csproj` | Lifecycle checks passed（仅合成数据；输出在已忽略的 `test-output/`） |
| `python SupportPackage/FeedbackTransportAudit/test_compare_feedback_paths.py` | 8 项 OK |
| `python SupportPackage/FeedbackTransportAudit/test_ros_feedback_probe.py` | 9 项 OK |
| `python Tools/test_ros_source_probe.py` | 6 项 OK |
| 只读检查 `MainScene.unity`、`docker-compose.yml`、10-22 采集 | 结果写在 §3、§4.5、§7 |

**本次没有执行**：Unity 编辑器编译 / Play、Quest 头显、ROS 或 Jetson 联调、机器人运动、车载 C++ 构建。

## 7 你需要知道的事

### 7.1 运行前

1. **航向来源是 JetsonGyro**，要求 Jetson 上以 `FAST_POSE=1` 运行 `xr_bridge.sh live`，且场景里的 `LivoscanDataSource` 已启用。如果收不到陀螺仪字段，控制器自动按 `WheelPlusSlam` 运行；一旦收到过陀螺仪字段，就不再切回轮子。陀螺仪数据超过 0.2 s 没有更新时停止转向，前进不受影响。
2. **里程计 `/odometry/filtered` 仍然必须新鲜**，它同时用来确认 `/cmd_vel` 链路正常；反馈过期或积压超过 0.5 s 时停车，恢复后需要松开再按扳机，不会自动继续。
3. **按扳机激活时要面朝正前方**：每次激活都会把头显当前朝向定为车头方向。
4. **倒车没有后方避障**。B 键切换倒车，HUD 有红字提示；暂停后会自动退出倒车。
5. 场景参数请在 Inspector 里修改并保存。改源码默认值不会覆盖场景里已经保存的值。

### 7.2 有分歧或过期的说明

本仓库内的几处已在原文加了"后续更正"注（原文保留）；对端仓库的文档没有改动。

| 说法 | 实际情况 |
|---|---|
| DirectAngle 10-14 傍晚：FlowState 默认关闭（`INSTA360_FLOWSTATE`） | 当前 `docker-compose.yml` 是 **FlowState 开启**，并用 `INSTA360_GYRO_ZERO_AXES: "z"` 屏蔽 z 轴陀螺仪（注释说实时拼接关闭 FlowState 会崩溃）。与 README 10-15 节一致 |
| README 10-15 节："当前方向追踪模式、可选的朝向减速辅助" | 10-20 已删除（`cf9b622`） |
| README 10-15 节："95 项离线仿真回归通过" | 当时的数字；当前是 94 项 |
| 工程流程 §8.2 的场景状态 | 是 10-21 提交基线，当前工作区见本文 §3 |
| `SupportPackage/KatMiniSCapture/README.md` | 旧副本；采集包实际复制的是 `Assets/KAT/Editor/KatPlayCapture.README.md` |
| 对端 `GYRO_HEADING_HANDOFF_20261020.md` 第 55 行写 `(t, yaw_odom)` 去重 | 已改为 `heading_t`，以对端 `xr-interface.md` 和 `SYNC.md` 为准 |
| Insta360 部署 README 仍把推流写成"下一阶段" | H.264/RTSP/WHEP 已经实现（联调记录待办第 1 条） |

### 7.3 尚未解决的问题

按对后续实验的影响大致排序：

1. **10-22 新功能没有实车验收**（标准见 §4.4），10-22 采集尚未分析（§4.5）。
2. **JetsonGyro 模式没有实车验收**（10-20 记录写"尚未实车测试"）。10-22 采集是第一次全程使用 gyro 的实验，可以一并分析。
3. **里程计偏航比例待定**：09-13 发现同一段转弯，轮编码器反推约 ±180°，而里程计只报 ±85~89°。需要在地面画 90°/180° 标记，用外部机位拍摄实测；Unity 端判断不了。gyro 模式下控制不再依赖里程计偏航，但全景补偿是否还依赖它，需要按当前代码再核对。
4. **全景画面与车身的视觉对齐**：10-13、10-14 的记录都写着需要实车确认；FlowState 改为"开启 + 屏蔽 z 轴"之后也没有验收记录。
5. **视频块状破损、视频时间戳**：没有逐帧采集时间戳，`Video Heading Delay Seconds` 只是手动近似。
6. **避障**：没有做。倒车完全靠操作者注意。
7. **输入校验不完整**：Unity 的 pose 解析没有全面检查有限值；桥端没有检查时间单调性（工程流程 §8.4）。
8. **手腕小地图**：Quest 实机验收和三项用户决定（默认跟车转还是朝北、显示开关键、PC 串流还是独立运行）仍未完成（[需求文档](../Assets/Livoscan/XR_MINIMAP_REQUIREMENTS.md) §7–8）。
9. 遗留代码：`Insta360WebRtcPeer.cs` 旧信令地址 `192.168.1.100`，可能已废弃；`CmdVelPublisher.cs` 是空占位；`Assets/KAT/` 下有多个不在场景里的旧控制器。暂不清理，需要你决定。

### 7.4 数据和提交

- `KATCaptures/`、`.bag`、诊断视频、审计输出、`test-output/` 都已加入忽略规则，只保存在本机。换电脑时要手动拷贝；10-21 回放测试在没有该采集目录时会跳过（输出 `SKIP`），不算失败。
- 提交前建议：在 Unity 里打开场景、确认 Inspector 值并保存一次（把新参数写入场景），再把 §2.1 的改动分成"功能+测试"和"文档"两个提交。

## 8 建议的下一步

1. 在 Unity 里打开项目，确认编译没有报错，在 Inspector 中检查 DirectAngle 新参数并保存场景。
2. 按 §4.4 的标准做一次短的实车测试（快转 90°/180° 各 3 次、只转头、Y 暂停后退出倒车），开启采集。
3. 对 10-22 和新采集逐次对照身体跳变与头显朝向，确认被接受和被拒绝的跳变各自正确。
4. 通过后提交；然后再处理 §7.3 的第 3、4 项（实测转角比例、全景对齐）。
