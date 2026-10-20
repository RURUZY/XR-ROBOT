# KAT Walk mini S 数据采集包

本工具读取当前项目 KAT Universal SDK 的普通查询接口，将 KAT 数据保存为 CSV、JSON，停止后生成 ZIP。随 Windows Unity Editor 的 Play 启停，原始 KAT 采集依赖已安装的 KAT Runtime 和项目 SDK DLL，不依赖 ROS 或 Python。v2 另外记录控制链事件；接收底盘指令/里程计需要现有 ROS 连接，此包不能替代机器人侧 ROS bag。

**完全排除 extraData：不解析、不保存、不据此生成落脚标签。** 原生返回结构必须为该区域保留空间以保持接口内存布局，但采集代码不会访问其中内容。

## 自动跟随 Unity Play

本项目已经安装 `Assets/KAT/Editor/KatPlayCapture.cs`，不需要挂组件或手动运行命令。

1. 等 Unity 脚本编译完成，确认 KAT 软件和 mini S 连接正常。
2. 点击 Play：自动建立新的采集目录，Console 出现 `[KAT Capture] 开始采集`。
3. 退出 Play：自动停止并生成 ZIP，Console 显示保存路径、样本量和错误数。
4. 从菜单 `KAT > 打开采集目录` 查看结果。

**画面录制默认关闭；CSV 控制链记录仍自动开始。** 需要额外记录画面时，在停止 Play 的状态下勾选 `KAT > Record Game view on next Play`，仅下一次 Play 会开启录制。选项开始时即被消耗，不会延续到之后的 Play 或 Play 中的脚本重载；Play 或正在切换 Play 时不能修改此选项。

默认保存到项目根目录 `KATCaptures/KAT_MiniS_日期_时间_唯一编号/`，同级生成同名 ZIP。每次 Play 单独保存，旧数据不覆盖。所有 ZIP 自带本 README。

读取发生在 Unity Editor 更新回调中，目标上限 100 Hz；受编辑器实际更新频率、接口耗时影响，实际可能更低。暂停 Play 时暂停采样；elapsed_s 仍继续计时，所以暂停期间会出现时间间隔。不补造样本，不改变 Time.timeScale 或物理更新频率。

如果在 Play 中修改脚本引发程序集重载，当前段会先打包，重载后若仍在 Play 则新开一段。关闭编辑器会尝试打包；强制结束进程、系统崩溃或断电可能无法生成 ZIP，已刷入磁盘的 CSV 仍保留。约每秒刷盘一次。

此工具只在 Windows Unity Editor 中执行，不启动场景、不发送机器人控制。控制器包含只读的输入/请求/发布观测点；你点击 Play 后，场景原有功能仍按原配置运行。原始 SDK 轮询在 Editor 暂停时停止；控制链事件在实际发生时记录，ROS 回调若仍运行也会记录，不能把编辑器暂停假定为整条链路都停了。

**100 Hz 是尝试读取上限，不是设备新数据频率。** 每次读取都保存，重复 SDK 时间戳不丢弃。设备未连接的零值不能视为真实静止测量。

## 文件内容

| 文件 | 内容 |
|---|---|
| samples.csv | 每次读取的整体移动速度、身体四元数、连接状态和时间戳 |
| device_status.csv | 同一次读取中 3 个设备状态槽的数据，用 sample_index 与 samples.csv 对应 |
| device_count.csv | 每约 1 秒查询一次的设备数量 |
| devices.csv | 每约 1 秒刷新一次的设备描述、序列号和型号标识 |
| calibration.csv | 校准计时接口的原始返回值 |
| errors.csv | 接口异常与时间；没有错误时不会生成此文件 |
| session.json | 采集起止、请求频率、实际样本量、有效连接样本数、错误数、结束原因 |
| control_input.csv | DirectAngle 本次计算真正使用的 KAT 普通输入；与独立轮询的 samples.csv 区分 |
| control_keyboard_input.csv | 键盘控制器实际读取的前进/转向轴 |
| control_request.csv | KAT 映射的平移请求、朝向辅助后的请求、发布限加速度前的转向请求、方向误差和状态 |
| control_publish.csv | KAT/键盘实际交给 ROSConnection 的线速度、角速度，包括暂停发零；不是底盘收到的确认 |
| control_chassis_command.csv | ROS 底盘 cmd_vel 输入与带时间戳的 cmd_vel_out 输出，按 topic 区分 |
| control_odometry.csv | ROS 里程计原始位姿和速度反馈，保留源时间戳；不等于独立车身测量 |
| control_configuration.csv | 起始及每秒检查到变化的控制器参数快照；实验中不要边跑边改参数 |
| control_marker.csv | 菜单 KAT → 控制测试标记 中手动添加的片段标签；不发运动指令 |
| control_panorama.csv | 约 10 Hz：全景模式、补偿参数、标定版本、里程计／球体／头显／车头世界朝向及有效性 |
| control_view_overlay.csv | 约 10 Hz：实际绘制的车身框与蓝色目标箭头方向、控制器目标与误差 |
| control_minimap.csv | 小地图显示时约 10 Hz：SLAM 朝向／新鲜度、地图旋转、箭头水平投影方向 |
| view_frames/*.jpg | 仅勾选画面录制时生成：约 5 帧/秒的 Unity Game 视图，长边最多 1280 像素，包含可见的方向叠加层 |
| view_frames.csv | 仅勾选画面录制时生成：每帧的文件名、Unity 帧号、主机时间、采集段 elapsed_s、头显／车头／目标方向 |
| view_capture.json | 仅勾选画面录制时生成：采集状态、保存帧数、繁忙跳帧数和错误；检查 no_frames / failed |
| view_frames.ffconcat | 仅勾选画面录制时生成：按实际帧间隔排列的 FFmpeg 输入清单 |
| README.md | 本说明 |

某类接口未成功返回过时，相应 CSV 可能不存在；查看 errors.csv 和 session.json。空值表示缺少前一帧、非有限数或不可计算，不用 0 代替。CSV 为带 BOM 的 UTF-8，便于 Excel 打开。

## v2 控制链记录与边界

2026-10-13 回归排查后，**Unity Game 视图**（桌面 XR 镜像）录制改为默认关闭、单次手动开启，以减少测试时新增的 GPU 复制、内存分配、JPEG 编码和写盘负担。此改动只移除默认画面采集开销；没有证明画面录制是卡顿的唯一原因，反馈源时间戳积压等问题需要单独核对控制日志。已有画面和采集包保留。

仅在停止 Play 时勾选 `KAT > Record Game view on next Play`，下一次 Play 的右下角才会显示 `VIEW REC`，帧数应持续增加。未勾选时不显示 `VIEW REC`、不生成 `view_*` 文件，这是正常行为；`session.json` 的 `viewCaptureRequested=false` 可以确认该段没有请求画面录制。勾选后仍需保持 Game 页正在渲染，停止 Play 后等打包结束。没有增加帧数时先排查 Game 页渲染和 Console，不能把空目录当作已录到画面。采集仅保存这个渲染视图，不录其他桌面应用、不录声音，也不是外部相机拍到的实体车身。

画面通过 [Unity ScreenCapture](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/ScreenCapture.CaptureScreenshotIntoRenderTexture.html) 在帧末取得，用异步 GPU 读取和后台 JPEG 写盘；同一时间最多一帧待处理，处理繁忙时跳帧。异步读取不代表没有渲染和内存开销。已有实测采集生成了 JPG，但未完成 GPU 性能、图像上下方向和 XR 镜像内容的完整验收。

停止 Play 后，在项目根目录运行 `python Tools/render_view_capture.py "KATCaptures/本次目录名"`，生成 `view.mp4`。此机已装 FFmpeg。视频从首张成功截图开始；对日志时加上 `view_frames.csv` 第一行的 `elapsed_s`。保持实际帧间隔，不用固定帧率掩盖缺帧。MP4 是事后导出，原始 JPG 与 CSV 已包含在采集 ZIP 内。

录到的画面时间是 **Unity 显示帧时间**；与控制记录共用主机时基，不能据此推算 Insta360 的曝光时刻或网络延迟。之前已结束的采集没有这些画面，无法补录。

所有 `control_*.csv` 使用同一采集段的 `elapsed_s` 和电脑 `host_utc_ns`，并保留 `unity_receipt_s`。这统一了 Unity 端的记录时基，不代表远程机器和视频采集时钟已经同步。`event_seq` 表示事件顺序，`source + input_seq` 关联一次控制器读取及它产生的请求/发布；停止事件可沿用上一输入编号，初始编号可为 0。每次重新启用组件可重置 input_seq，需要同时用 event_seq 区分段落。

`request` 每次实际计算记录，`publish` 每次实际发布记录，因此行数不必一致。`requested_linear` 是踏步映射后的前进值，`requested_angular` 尚未经过发布节流/加减速，`target_yaw_deg` 与 `error_deg` 都在 SLAM 校正后的朝向参考系中，`slam_correction_deg`/`slam_status` 是当时的校正量与状态（2026-10-20 起导航辅助相关的 `unassisted_linear`、`forward_multiplier`、`heading_mode` 三列已删除）。同日新增：`heading_source` 为实际使用的航向来源（JetsonGyro 收到陀螺仪字段之前显示 WheelPlusSlam），`gyro_yaw_deg`（外推后的 unwrap 角）、`gyro_rate_deg_s`、`wheel_yaw_deg`（轮式 unwrap 角）、`pose_msg_age_s`（heading_age_s + 本地收到后经过的时间）、`pose_t`（pose 消息的 t）、`pose_receipt_s`（Unity 收到时间），收到时间减 `pose_t` 可用来量 Wi-Fi 链路延迟（两机时钟需同步）；最终发给连接器的值看 `control_publish.csv`。`transport_available=True` 只说明存在连接器对象，不证明 TCP 在线或机器人执行成功。原生 SDK 数据中的时间戳仍是原值，不当作已验证的 UTC 或硬件采样时刻。

采集通过现有 ROSConnection 只读订阅 `/odometry/filtered`（以及场景控制器指定的 odometryTopic）、`/husky_velocity_controller/cmd_vel`（Twist）和 `/husky_velocity_controller/cmd_vel_out`（TwistStamped）。输出类型已和项目 2026-09-11 实车 bag 核对；如果现场换了名称或类型，需要相应适配。没有收到消息时留空/缺文件，绝不补零。保留机器人侧同一实验的 ROS bag，核对源时间戳、发布者以及原始到达顺序。

`session.json` 的 `unityPublishRows`、`odometryRows`、`chassisInputRows`、`chassisOutputRows` 用于检查链路完整性，`controlCaptureError` 非空表示控制链写盘失败。日志失败不会阻断运动控制，但该段不能作为完整证据。键盘底盘测试没有 KAT 有效样本是正常的，不因此否定键盘/ROS 记录；完整 KAT 测试则需要原始输入和请求。

这里只记录普通 KAT 输入，不增加 extraData 采集。额外数据对既有控制器的影响可从最终请求看到，但本包无法独立重放所有 extraData 分支。完整实车验收步骤见项目 `SupportPackage/HuskyResponseAudit/README.md`。

## 调用的接口

| 接口 | 用途与频率 |
|---|---|
| DeviceCount() | 当前枚举设备数，约每秒一次 |
| GetDevicesDesc(index) | 每个已枚举设备的描述，约每秒一次 |
| GetWalkStatus(serialNumber) | 每轮读取每个设备的状态；无枚举设备时尝试 SDK 默认设备，空序列号 |
| GetLastCalibratedTimeEscaped() | 每轮读取全局校准计时，函数名按项目源码保留 |

以上覆盖当前 KATNativeSDK.cs 的普通公开数据查询接口。ForceConnect、灯光、振动、卸载和回调注册属于控制或生命周期操作，不作为采集查询调用。设备失联通过 connected 和设备枚举记录。

## 时间字段（重要）

| 字段 | 含义 |
|---|---|
| elapsed_s | 从本次采集开始算起的单调时间，单位秒；不受电脑校时跳变影响 |
| host_utc_ns | 电脑收到这次接口结果后的 UTC Unix 时间，单位纳秒；数字单位不表示实际精度达到纳秒 |
| sdk_call_ms | GetWalkStatus 调用耗时，单位毫秒 |
| sdk_last_update_raw | SDK lastUpdateTimePoint 原值；项目未说明单位、起点和硬件采样语义，不转换成 Unix 时间 |
| sdk_stamp_changed | 与同一设备上次原始时间戳是否不同；首条为空。变化不等于落脚 |
| sdk_stamp_delta_raw | 当前原始时间戳减前一个；保留负值便于观察重置，单位未确认 |
| last_calibrated_time_escaped_raw | GetLastCalibratedTimeEscaped 原值；根据命名可能表示距上次校准经过的时间，但当前源码未明确单位和语义，不能作为已验证的秒数 |

电脑时间是接口接收时间，可能包含传输和处理延迟。各个接口顺序调用，并非同一硬件时刻的原子快照。三条设备状态与对应 samples 行使用同一时间和 sample_index，因为来自同一次 GetWalkStatus 返回。

视频时间和采集时间**不会自动同步**。需在录像中记录一个可见/可听且能在日志时间轴确定的同步事件，并记录偏移量。若确认日志 tL 秒对应视频 tV 秒，则视频时间 = 日志 elapsed_s + (tV - tL)。仅依靠“差不多同时点开始”不能提供精确落脚标注。长视频建议检查首尾同步偏差。

## samples.csv 参数

| 参数 | 含义 |
|---|---|
| sample_index | 本次采集全局递增样本编号，从 0 开始；多设备共享编号 |
| serial_number | 查询时传入的序列号；空值表示请求 SDK 默认设备 |
| device_name | SDK 返回的设备名称 |
| connected | SDK 返回的连接状态；False 时本行不应作为有效运动数据 |
| move_x / move_y / move_z | SDK moveSpeed 三个分量，是整体目标移动速度/方向输出，不是左右脚数据；保留原始量纲，未核实为 m/s |
| horizontal_speed | sqrt(move_x² + move_z²)，辅助查看水平移动输入强弱，与速度分量量纲相同；是派生值 |
| quat_x / quat_y / quat_z / quat_w | bodyRotationRaw 身体朝向四元数的原始四个分量，无量纲；不是欧拉角、不是角速度，也不是脚部姿态；不自动转换 ROS 坐标系 |

速度峰值不是已确认的落脚时刻。此包没有“左脚落地”“右脚落地”或逐步训练标签。

## device_status.csv 参数

| 参数 | 含义 |
|---|---|
| slot_index | SDK deviceDatas 数组槽位 0、1、2；当前源码未定义 mini S 上各槽位对应关系，不能擅自标为左脚/右脚/腰部 |
| btnPressed | SDK 注释为校准按钮是否按下 |
| isBatteryCharging | 是否充电 |
| batteryLevel_raw | 电量相关原始 float；源码注释 Battery Used，量程、已用/剩余定义未确认，不标成百分比 |
| firmwareVersion_raw | 固件版本的原始字节值，不推测版本字符串 |

mini S 可能不提供全部槽位的有效状态。全零不能单独证明“电池没电”或“按钮从未按下”。

## 设备描述参数

| 参数 | 含义 |
|---|---|
| device_count | 枚举的设备数，不是脚数 |
| index | 此次枚举索引，重连后可能变化；优先用序列号区分设备 |
| device | 设备名称 |
| isBusy | SDK 的忙碌标志，具体判定含义未在源码解释 |
| serialNumber | 设备序列号；分享数据包前注意这属于设备标识 |
| pid / vid | SDK 返回的产品/厂商标识，按整数原值保存 |
| deviceType | 源码定义 0=错误，1=跑步机，2=追踪器；其他值原样保留 |
| deviceSource | 设备来源代码；当前源码没有枚举解释 |

结构体中的 hidUsage 在 C# 源码中为私有字段，不作为公开数据输出。

## 有效性与后续训练

检查 session.json 的 connectedSampleRows、sampleRows 和 errorRows，再检查 connected、时间戳变化、实际速度/朝向变化。connectedSampleRows=0 表示没有任何连接有效的样本，ZIP 只用于诊断，不能当作步行训练数据。仅出现 connected=True 也不代表传感器更新频率和各字段语义已得到校验。

本包可与视频配对、辅助定位运动片段，仍需人工或经验证的其他信号生成落脚标签。本工具不会把速度峰自动认定成真实接触时刻。

## 实现与依据

数据布局直接对应本项目 Assets/KAT/SDK/KATNativeSDK.cs：Pack=1，1 字节布尔值，固定 3 槽设备状态，四元数 x/y/z/w；保留原始返回值，不修改 SDK 或场景。采集通过现有 KATNativeSDK 调用原生 DLL，与现有控制器使用相同接口。SDK DLL 仍可能因未安装 Runtime、设备离线或版本不匹配而失败；真实设备采集需实际运行验证。

## session.json 参数

| 参数 | 含义 |
|---|---|
| format | 采集格式版本 |
| startUtc / endUtc | 采集开始和结束的 UTC 时间 |
| endReason | play_stopped=退出 Play；assembly_reload=脚本重载分段；editor_quit=编辑器退出；capture_error=写入错误 |
| durationSeconds | 本段实际经过时间（含 Play 暂停时间） |
| sampleRows | 成功读取的总样本数 |
| connectedSampleRows | connected=True 的样本数 |
| errorRows | 接口异常记录数 |
| targetPollHz | 目标轮询频率上限 100 |
| actualSampleRowsPerSecond | 总样本数/总时长；多设备时为各设备行数总和，包含暂停时间 |
| extraDataRecorded | 固定 false，不记录扩展数据 |
| viewCaptureRequested | 本段是否通过单次菜单选项请求画面录制；默认 false。true 不保证成功录到帧，还需检查 view_capture.json 和 view_frames.csv |
| sampling | 采样调度说明 |
| interfaces | 本次调用的普通数据查询接口列表 |

## Optional one-shot direct source probe

Only `SupportPackage/FeedbackTransportAudit/enable_next_play.flag` enables this diagnostic. On successful launch the marker is preserved in the session as `source_probe_enable_next_play.flag`; later Play sessions do not repeat it. The hidden Python process subscribes directly to `/odometry/filtered` from `/ekf_localization`, using the existing ROSConnection IP and master port 11311, for at most 60 seconds. It never publishes motion commands. Python defaults to `D:\PYTHON312\python.exe`; `KAT_SOURCE_PROBE_PYTHON` may specify another existing absolute Python executable path.

Read `source_probe.csv`, `source_probe.json`, `source_probe.stdout.txt`, `source_probe.stderr.txt`, and `source_probe_lifecycle.txt` in the original session directory. Stop requests use a cooperative stop file and process cleanup runs in the background so diagnostics do not delay controller shutdown. The main capture ZIP excludes `source_probe*` files because they may still be written during packaging; retain the original session directory for the probe results. Assembly reload/editor exit or a forced process stop can leave the summary absent; inspect the lifecycle log and partial CSV. This is additional diagnostic evidence, not a fix for transport backlog.
