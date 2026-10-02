# KAT Walk mini S 数据采集包

本工具读取当前项目 KAT Universal SDK 的普通查询接口，将 KAT 数据保存为 CSV、JSON，停止后生成 ZIP。不是 ROS bag，不需要 ROS 或 Python。随 Windows Unity Editor 的 Play 启停，依赖已安装的 KAT Runtime 和项目 SDK DLL。

**完全排除 extraData：不解析、不保存、不据此生成落脚标签。** 原生返回结构必须为该区域保留空间以保持接口内存布局，但采集代码不会访问其中内容。

## 自动跟随 Unity Play

本项目已经安装 `Assets/KAT/Editor/KatPlayCapture.cs`，不需要挂组件或手动运行命令。

1. 等 Unity 脚本编译完成，确认 KAT 软件和 mini S 连接正常。
2. 点击 Play：自动建立新的采集目录，Console 出现 `[KAT Capture] 开始采集`。
3. 退出 Play：自动停止并生成 ZIP，Console 显示保存路径、样本量和错误数。
4. 从菜单 `KAT > 打开采集目录` 查看结果。

默认保存到项目根目录 `KATCaptures/KAT_MiniS_日期_时间_唯一编号/`，同级生成同名 ZIP。每次 Play 单独保存，旧数据不覆盖。所有 ZIP 自带本 README。

读取发生在 Unity Editor 更新回调中，目标上限 100 Hz；受编辑器实际更新频率、接口耗时影响，实际可能更低。暂停 Play 时暂停采样；elapsed_s 仍继续计时，所以暂停期间会出现时间间隔。不补造样本，不改变 Time.timeScale 或物理更新频率。

如果在 Play 中修改脚本引发程序集重载，当前段会先打包，重载后若仍在 Play 则新开一段。关闭编辑器会尝试打包；强制结束进程、系统崩溃或断电可能无法生成 ZIP，已刷入磁盘的 CSV 仍保留。约每秒刷盘一次。

此工具只在 Windows Unity Editor 中执行，不启动场景、不发送机器人控制、不改已有 KAT 控制器。你点击 Play 后，场景原有功能仍按原配置运行。

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
| README.md | 本说明 |

某类接口未成功返回过时，相应 CSV 可能不存在；查看 errors.csv 和 session.json。空值表示缺少前一帧、非有限数或不可计算，不用 0 代替。CSV 为带 BOM 的 UTF-8，便于 Excel 打开。

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
| sampling | 采样调度说明 |
| interfaces | 本次调用的普通数据查询接口列表 |
