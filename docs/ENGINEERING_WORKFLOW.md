# 工程协作与跨项目对接流程

版本：1.0；生效日期：2026-10-21。适用项目：XR-ROBOT Unity 工程和 `livoscan-mywork`。用于功能开发、故障排查、接口变更和联合验收。

默认顺序是：明确一个问题 → 只读建立事实 → 做最小诊断 → 确认原因和方案 → 在授权范围内实施 → 分层验证 → 留下交接记录。每一步都保留依据；无法确认的内容写明待确认。本流程不把历史文档中的结果当作当前版本的运行结果。

## 1 任务范围与实施边界

每个任务写一个可观察的目标和一个可判定的完成条件。多个症状先选一个最小复现，其他问题记录为后续项，不混在一次修复中。

| 任务状态 | 可以做什么 | 转入下一步的条件 |
|---|---|---|
| 只读分析 | 读代码、配置、历史记录和已有日志，建立调用链；执行范围内的非修改检查 | 明确问题、证据和下一项诊断 |
| 诊断 | 运行约定检查，记录原始结果；需要启动服务、改配置或产生运动的检查单独说明影响 | 足以支持或排除原因；证据不足时继续最小诊断 |
| 方案待实施 | 列出具体文件、改动、兼容策略、验收与回退方式 | 用户已授权这个具体方案或当前实施任务已明确覆盖它 |
| 实施 | 只改当前问题必要的代码、配置、测试和说明 | 改动完成且有对应验证证据 |
| 验收与交接 | 按层报告通过、失败、未执行及阻塞条件 | 达到本任务验收标准；缺少实机证据则保持相应验收未完成 |

“先不要修改代码”优先于模板中的“按方案实现”。整理流程文档本身不授权改功能、场景或部署。已确认且授权的同一方案无需反复询问；新证据使方案失效、需要扩大范围或新增外部影响时，说明差异后再决定。

## 2 任务输入

优先从仓库、配置和日志补齐信息；只向用户询问无法获取且影响下一步的内容。

| 项目 | 必填内容 |
|---|---|
| 目标 | 一个功能或一个问题；明确本次不涉及的范围 |
| 环境 | 主机角色、操作系统、Unity/ROS 版本、Python/C++ 工具链、设备及连接方式；未知项保留未知 |
| 版本 | 双方仓库、分支、提交号、相关未提交修改；部署中的实际版本另记 |
| 文件 | 入口代码、场景/Prefab、启动脚本、配置文件及适用说明 |
| 输入与输出 | topic/URL、消息类型、字段、频率、单位、坐标系、时间戳、生产者和消费者 |
| 现象 | 复现步骤、实际与预期结果、出现条件、完整错误上下文、发生时间和最近修改 |
| 验收 | 输入条件、测量方法、阈值、观察时长或重复次数、失败判据 |

仅有“[现象]”“[日志]”等占位符时，没有足够信息给出针对某个故障的概率排序或修复。可以先形成结构图和诊断计划。

## 3 只读分析的固定输出

### 3.1 数据流与调用关系

沿着“输入源 → 采集/接收 → 解析与有效性检查 → 坐标/单位/时间处理 → 控制或显示 → 实际输出 → 反馈”追踪。每条关键边写出文件、类/函数、行号和配置依据，并标明是直接调用、事件回调、共享状态、ROS topic、HTTP 还是 WebSocket。

读取代码同时检查实际场景组件是否启用、Inspector 序列化值、启动参数、环境变量及运行时保存值。分别列出源码默认、配置覆盖和本次运行实测，不用其中一项代替另一项。行号随版本变化，记录提交号和函数名供重新定位。

### 3.2 事实与不确定性

| 分类 | 写法 | 不能据此声称 |
|---|---|---|
| 代码或配置事实 | 文件、函数/字段、行号、提交号，以及直接可读出的行为 | 已在目标设备执行成功 |
| 运行观测 | 同次日志、参数、时间段、采集端和测量方式 | 自动证明物理运动、单向延迟或根因 |
| 历史记录 | 日期、当时版本和场景 | 现在仍使用相同实现或已验收 |
| 假设 | 现有支持证据、反证方法、仍缺少的观察 | 已确定原因 |
| 缺失信息 | 影响哪个判断、从哪里取得、下一项最小操作 | 缺失值等于零或系统正常 |

文档、源码和运行结果不一致时保留差异，查明版本和生效配置，不擅自挑一个当作全部事实。

### 3.3 按证据排序的原因与最小诊断

使用下表，每次先执行信息价值最高、影响最小的一项检查，得到结果后更新排序。没有支持证据的原因写“候选”，不写“最可能”。

| 优先级与候选原因 | 支持证据 | 能排除它的检查 | 下一项最小操作 | 结果如何改变判断 |
|---|---|---|---|---|
| 待本次填写 | 文件或日志片段 | 输入、方法、预期与失败判据 | 一条具体读取或检查 | 保留、降级、排除或追加证据 |

尚无症状时可按以下顺序定位断点，但这不是本项目已经确认的根因排序：版本和配置是否生效 → 源端是否产出有效消息 → 传输与解析 → 时间和坐标语义 → 消费端处理 → 实际执行或显示。诊断尽量只改变一个变量，保存修改前的基线。

### 3.4 最小修改方案

说明要解决的原因、准备修改的文件/函数、复用的接口与依赖、输入异常和恢复策略、对另一项目的影响、验收方法和回退步骤。暂时没有证据支持修复时，方案只写下一项诊断，不先试着调增益、改正负号或放宽过期阈值。

## 4 坐标 单位 时间与数据有效性

### 4.1 接口记录必须写清的语义

| 类别 | 需要明确的内容 |
|---|---|
| 坐标 | frame 名称、原点、轴方向、左右手系、父子关系、机器人底盘/雷达 IMU/相机/头显的具体参考点 |
| 变换 | 写成 `T_目标←来源`，齐次坐标下定义 `p_目标 = T_目标←来源 · p_来源`；串联为 `T_A←C = T_A←B · T_B←C`；反向使用逆变换，不能只交换符号 |
| 姿态 | 四元数顺序和归一化约定、yaw 所绕轴及正方向、弧度/度、范围和是否展开累计、角速度所处坐标系 |
| 单位 | 长度 m/cm、线速度 m/s、角度 rad/deg、角速度 rad/s 或 deg/s；界面缩放与物理量换算分开 |
| 时间 | 采样时间、ROS header、桥接接收、Unity 接收、显示时刻分别记录；单位、起点、时钟域、仿真时间和重置规则 |
| 时效 | 更新周期、接收间隔、源数据年龄、积压估计分别定义；阈值从配置/合同取得，并记录实测分布 |

坐标轴重排不等于地图与里程计已对齐；雷达位姿也不等于底盘中心位姿。不同起点、外参、回环/重定位和标定版本必须另行核对。姿态变换须包含基变换，不能把位置换轴公式机械套在四元数分量上。

跨设备时钟未同步或未测偏移与误差时，不能用“本机 UTC − 远端时间戳”宣称单向网络延迟。服务端报告的年龄加本地接收后经过时间，也不自动包含全部上游积压、传输与采集延迟。手动视频延迟参数不能代替逐帧曝光时间戳。

### 4.2 输入异常检查与预期行为

以下是每个相关任务应明确并验证的要求，不表示所有现有模块均已实现。

| 情况 | 诊断与方案要求 | 验收必须观察 |
|---|---|---|
| 启动后无输入 | 区分未连接、已连接无样本、解析失败；不把默认零值当作有效测量 | 状态正确；没有凭空生成的有效数据或非预期输出 |
| 输入过期或积压 | 每条通道按自己的来源与时间判断，不用另一通道活跃掩盖它 | 达到约定阈值后的停止/降级/提示行为及发生时间 |
| 缺字段或异常数据 | 检查必填字段、数组长度、类型、NaN/Inf、量程、四元数、长度/版本；记录拒绝原因 | 无崩溃、无无限日志；异常不污染后续有效状态 |
| 重复 乱序 时间回退 | 核对去重键和时钟域；定义重新建参考或新时间段的条件 | 不错误刷新新鲜度，不产生无依据的姿态跳变 |
| 断线与重连 | 定义缓存处置、重连节奏、重新对齐及是否需重新激活 | 恢复条件清晰，不补执行旧的控制输入 |
| 版本不兼容 | 检查字段、类型和语义；记录旧版本回退或明确拒绝策略 | 不把缺失字段默认为有效零值；状态能解释当前分支 |

控制输出、显示缓存、采集记录可采用不同的退化策略，但必须写明。发送零速度只是发送端行为，不能直接声称远端已收到或机器人已停稳。

## 5 XR 与 livoscan 跨项目对接

### 5.1 双方基线与接口约定

当前配套目录为 `New Unity Project` 与 `livoscan-mywork`。两边会话不共享隐含上下文，任何实施与交接都要带上可保存的任务记录。先读 Unity 消费端和 livoscan 生产端，再核对部署中的实际版本。

每个接口建立以下记录，不凭一个示例报文推出全部协议：

| 项目 | 记录内容 |
|---|---|
| 身份 | 接口名称/版本、生产者/消费者、双方提交号、实际部署版本 |
| 传输 | URL/topic、端口、请求/订阅方式、消息类型、编码、二进制字节序和长度 |
| 字段 | 名称、类型、必选/可选、缺失语义、范围、单位、frame、时间来源；有效原始样例及异常样例 |
| 更新 | 源采样与发送频率、重复发送规则、去重键、是否全量/增量、丢失/乱序处理 |
| 生命周期 | 首条数据前、过期、断线、重连、SLAM 重启/重定位及服务重启的行为 |
| 兼容 | 新旧两端组合、能力/版本判断、回退条件、升级次序与回退版本 |
| 验收 | 检查命令或人工步骤、数据集、同步方法、测量窗口、通过阈值、失败判据、证据路径 |

已存在的对接材料包括 livoscan 的 `docs/xr-interface.md`、`docs/xr-unity/GYRO_HEADING_HANDOFF_20261020.md`，以及本仓库的 [小地图历史需求](../Assets/Livoscan/XR_MINIMAP_REQUIREMENTS.md) 和 [DirectAngle 工作记录](../Assets/KAT/DirectAngle-测试说明.md)。专项行为以双方当前代码、有效配置及测量核对，不把历史需求覆盖到新实现上。

继续使用 livoscan 的 `docs/xr-unity/SYNC.md` 记录双方进度，最新更正与旧交接说明一起核对。只读任务不执行其中历史写法所要求的自动 `git pull/commit/push`；这些操作需属于当前授权范围。接口版本规则有冲突时，在该次方案中明确采用的规则与兼容性，不能悄悄改变字段含义。

### 5.2 一次对接变更的顺序

1. 固定一个目标和双方版本，保存当前可复现的输入与输出。
2. 核对接口约定，明确改动归属生产端、消费端或双方；列出受影响的控制、显示和诊断链路。
3. 提出最小兼容方案与明确验收。若字段含义变化，即使字段名不变也属于协议变更。
4. 在已授权范围内实施；先用离线样例验证解析和语义，再验证本机或回环传输。
5. 核对真实服务端及客户端版本，按兼容方案确定升级次序，不默认“服务端先升级”一定兼容。
6. 联调记录源端输出、Unity 接收与最终消费者；同时检查无首条数据、停更、异常数据、断线、恢复和旧版本组合。
7. 涉及真实运动或视觉对齐时，在约定设备条件下独立测量；只看到里程计/叠加框变化不能验证自身准确性。
8. 双方保存结果及未完成项。若需回退，按兼容组合恢复代码、配置和标定，记录回退后的检查。

联调使用同一个任务编号和测试批次；两边记录对应的开始/结束时间、输入样例、版本和证据位置。编号只关联实验，不自动提供时钟同步。

### 5.3 跨项目交接模板

```text
任务编号 / 单一目标：
XR 仓库分支、提交、相关本地改动：
livoscan 仓库分支、提交、相关本地改动：
双方实际部署版本 / 启动命令 / 生效配置：
接口约定版本 / 修改前后原始样例：
字段、单位、坐标系、变换方向、时间语义的变化：
已确认原因及双方文件/函数/行号证据：
本端改动：
对端所需动作及授权/完成状态：
新旧版本兼容、部署次序和回退步骤：
共同测试输入、测量窗口、阈值与失败判据：
本次实际检查、结果、双方日志位置：
尚未验证 / 阻塞条件 / 下一项最小操作：
```

## 6 验证层级与现有工具

### 6.1 验证结论分层报告

| 层级 | 能支持的结论 | 不能代替 |
|---|---|---|
| 静态审阅 | 接口、依赖、调用和配置的源码事实 | 运行成功 |
| 编译 | 指定目标和配置能够构建 | 逻辑正确、设备功能通过 |
| 离线回归或回放 | 给定输入和模型中的逻辑符合预期 | 实际网络、原生 SDK、传感器与物理运动 |
| 传输联调 | 指定版本端点能够交换并解析数据 | 时间/坐标语义正确、设备行为正确 |
| 联合功能 | 从生产者到消费者满足约定输入输出 | 尚未独立测量的物理精度或真实视频对齐 |
| 实机验收 | 本次设备、场地与条件下达到可测标准 | 其他版本、场地、负载下自动成立 |

每项检查记录：输入与前提、操作、预期、失败判据、实际结果、证据位置。缺少阈值的关键项先标待定；“没有明显问题”“看着正常”不能替代精度或时延指标。

### 6.2 本仓库已有检查入口

从仓库根目录执行，按本次改动选择相关检查，不为文档改动运行全部设备或功能测试。下面是命令入口，不是本次已执行结果。

| 范围 | 入口 | 结果边界 |
|---|---|---|
| 控制、航向与反馈新鲜度 | `dotnet run --project SupportPackage/KatDirectAngleRegression/Tests.csproj` | .NET 10 离线模型；覆盖哪些分支以当前测试源码为准 |
| 采集生命周期 | `dotnet run --project SupportPackage/KatMiniSCapture/Tests.csproj` | 使用测试替身，不能证明原生设备采集正常 |
| ROS 反馈探针 | `python SupportPackage/FeedbackTransportAudit/test_ros_feedback_probe.py` | 本机回环/协议检查 |
| 双路径反馈比较 | `python SupportPackage/FeedbackTransportAudit/test_compare_feedback_paths.py` | 合成或离线数据比较逻辑 |
| ROS 源探针 | `python Tools/test_ros_source_probe.py` | 离线/本机检查，范围以测试源码为准 |
| 真实采集完整性 | `python SupportPackage/HuskyResponseAudit/audit_capture.py "KATCaptures/实际目录" --require-chassis` | 有记录不等于运动准确；缺文件不能补零 |

有还原缓存时可给 `dotnet run` 加 `--no-restore`；失败时记录原因，不把依赖不可用当成功。真实反馈探测及路径比较按 [反馈传输审计](../SupportPackage/FeedbackTransportAudit/README.md) 执行；源端与 Unity 的记录必须满足工具要求的时钟和时间戳匹配前提。底盘响应按 [实机验收方法](../SupportPackage/HuskyResponseAudit/README.md)；采集字段按 [采集器说明](../Assets/KAT/Editor/KatPlayCapture.README.md) 核对。

## 7 可直接使用的任务模板

### 7.1 只读分析与故障诊断

```text
请遵循 docs/ENGINEERING_WORKFLOW.md，先阅读代码和配置，不修改代码、场景或运行配置。
本次单一目标/问题：
环境与设备：
相关仓库、版本和文件：
输入与输出（topic/类型/频率/单位/坐标系/时间戳）：
复现步骤：
当前现象及完整日志：
最近修改：
预期与实际结果：

请输出：
1. 当前数据流及调用关系，标出文件、函数和行号依据。
2. 已确认事实、尚未确认的假设、缺少的信息。
3. 按证据排序的可能原因；每项给支持证据、能排除它的检查和下一项最小诊断。
4. 得到检查结果后给出针对原因的最小修改方案和可测验收标准。
不得假设不存在的接口；无法确认的内容明确标注。
```

### 7.2 按已确认方案实施

```text
请按已确认方案实施，每次只处理一个明确问题。
方案/任务编号：
已确认原因及证据：
授权修改的文件与行为：
对端接口约定与兼容要求：
验收输入、环境、阈值、观察时长或重复次数：

优先复用现有接口和依赖。写清坐标系、单位、时间戳和变换方向。
覆盖无输入、过期、异常数据以及断线恢复；给验证方法、预期与失败判据。
完成后说明改动、实际检查及未验证项。编译通过与功能通过分别报告。
若证据推翻方案或需要扩大修改范围，先说明原因与修订方案。
```

### 7.3 完成报告

```text
本次解决的单一问题：
已确认原因与证据：
修改文件/函数及行为变化：
接口、配置、坐标、单位、时间语义影响：
实际检查（环境、输入、预期、失败判据、结果、证据路径）：
未执行或失败的检查及原因：
已达到的验证层级 / 尚未完成的验收：
对端动作与当前部署状态：
回退方式 / 下一步：
```

## 8 当前项目核对基线

以下为 2026-10-21 的只读源码基线；本节提供后续阅读入口，不是本轮设备验收或具体故障诊断。Unity 基线提交 `fa4eea4`，livoscan 基线提交 `e8ffe96`；开始新任务时重新核对相关代码和生效配置。

### 8.1 环境与证据边界

- Unity 编辑器版本为 `2022.3.4f1`，依据 [ProjectVersion.txt](../ProjectSettings/ProjectVersion.txt) 第 1 行。
- 包清单包含 WebRTC `3.0.0-pre.8` 和 XR Interaction Toolkit `2.4.3`；ROS-TCP-Connector 锁定提交为 `c27f00c6cf750d2d0564349b3039d19aa3925e7c`。依据 [manifest.json](../Packages/manifest.json) 和 [packages-lock.json](../Packages/packages-lock.json) 第 43–47 行。
- 本次读取环境是 Windows/PowerShell；不能据此推定机器人和 Jetson 的实际系统、ROS、Python/C++ 运行版本。双方部署版本和设备在线状态另行核对。
- `.NET 10` 是两个离线测试工程的目标框架，不是 Unity 运行时版本。

### 8.2 当前调用链与接口

下面的路径均已存在。Unity 文件链接相对本仓库；livoscan 链接相对本机的相邻配套仓库，换机器时按实际检出位置定位同名文件。

| 链路 | 代码与配置事实 | 依据 |
|---|---|---|
| 键盘 → 机器人命令 | `CmdVelPublisher.Start → Update → ROSConnection.Publish`；`/cmd_vel`，`geometry_msgs/Twist`，`linear.x` 为 m/s，`angular.z` 为 rad/s；按 Unity 渲染帧发布。Twist 无 header、frame_id 或源时间戳，实际底盘采用的 frame 仍需核对 | [HuskyController.cs](../Assets/HuskyController.cs) 第 21、30、43–56 行 |
| KAT → 机器人命令 | `GetWalkStatus → DirectAngle.FixedUpdate → PublishIfDue → Publish`；同为 `/cmd_vel`；名义 20 Hz，运动边沿可提前发布；Fixed Timestep 配置 0.02 s。SDK 原始速度量纲、时间戳时基未从定义中确认 | [DirectAngle](../Assets/KAT/KatHuskyDirectAngleController.cs) 第 343、464、1269、1292 行；[SDK](../Assets/KAT/SDK/KATNativeSDK.cs) 第 92、139 行；[TimeManager](../ProjectSettings/TimeManager.asset) 第 6 行 |
| 机器人反馈 → 控制和显示 | `/odometry/filtered`、`nav_msgs/Odometry` 分别被控制器与视频接收器订阅。使用 ROS header 与本地单调接收时间检查停更/新增积压；新增积压不能测出第一条消息之前已有的恒定延迟 | [DirectAngle](../Assets/KAT/KatHuskyDirectAngleController.cs) 第 354 行；[RobotFeedbackFreshness](../Assets/WebRTC/RobotFeedbackFreshness.cs) 第 3、27 行；[视频接收器](../Assets/WebRTC/MediaMtxWhepReceiver.cs) 第 174、185 行 |
| livoscan 位姿 → 桥 → Unity | ROS 2 `Odometry` 默认 `/aft_mapped_to_init` → `ros_poses → World.set_pose/current_pose → /ws`；`FAST_POSE=1` 启动快位姿节点并改订阅 `/livoscan/pose_fast`。Unity `ReceiveLoop → Update → ApplyPose` 更新共享数据，再供地图显示和航向控制读取 | 对端 [xr_bridge_server.py](../../livoscan-mywork/scripts/xr_bridge_server.py) 第 100、119、160、252 行、[xr_bridge.sh](../../livoscan-mywork/scripts/xr_bridge.sh) 第 52–60 行；[LivoscanDataSource](../Assets/Livoscan/LivoscanDataSource.cs) 第 146、194、219 行 |
| IMU → 独立航向 → Unity 控制 | `/livox/imu` 与 SLAM 输入到快位姿节点；`/livoscan/heading` 为 `Vector3Stamped`，x/y/z 分别存 yaw/rate/bias，单位 rad、rad/s、rad/s。桥把 yaw/rate/stamp 附到 pose 消息。Unity 转为 deg、deg/s，经 `GyroHeadingTracker` 提供控制航向 | 对端 [imu_pose_node.py](../../livoscan-mywork/scripts/imu_pose_node.py) 第 31–59 行、[桥](../../livoscan-mywork/scripts/xr_bridge_server.py) 第 109–117、172 行；[LivoscanDataSource](../Assets/Livoscan/LivoscanDataSource.cs) 第 231 行；[GyroHeadingTracker](../Assets/KAT/GyroHeadingTracker.cs) 第 79、115 行 |
| 地图与物体 → Unity 显示 | Unity GET `/map/points.bin`，并从 `/ws` 收 objects/pose；`LivoscanWorldClient` 和腕部小地图共享数据源。桥还提供 `/info`、`/world_model.json`，当前 Unity 数据源未调用这两个端点。协议 `livoscan.xr.v0`；桥默认 pose 10 Hz、objects 每 1 s 全量发送，未实测 | 对端 [桥](../../livoscan-mywork/scripts/xr_bridge_server.py) 第 32、270–272 行；[LivoscanDataSource](../Assets/Livoscan/LivoscanDataSource.cs) 第 109、194 行；[WorldClient](../Assets/Livoscan/LivoscanWorldClient.cs) 第 94 行；[腕部小地图](../Assets/Livoscan/LivoscanWristMinimap.cs) 第 97、126 行 |
| X5 → MediaMTX → Unity 全景 | 相机 SDK → 拼接 RGBA → GStreamer H.264 → RTSP → MediaMTX → WHEP → 视频球材质。C++ 配置 2880×1440、30 fps；这不是本轮测得的接收帧率 | [相机程序](../Deployment/HuskyInsta360/src/camera_stitch_test.cpp) 第 54、285、323、335 行；[compose](../Deployment/HuskyInsta360/docker-compose.yml)；[视频接收器](../Assets/WebRTC/MediaMtxWhepReceiver.cs) 第 508、568、619 行 |

> 2026-10-24 补充：下面两段描述的是提交基线中的场景。工作区当前的 `MainScene.unity`（尚未提交）已启用 DirectAngle 和 HUD、禁用键盘发布器，并保存 `headingSource: 2`（JetsonGyro）。详见 [项目现状 §3](PROJECT_STATUS_2026-10-24.md)。

**实际保存的场景状态。** [MainScene](../Assets/MainScene.unity) 第 2563–2570 行启用键盘发布器，线/角速度系数为 0.5 m/s、1 rad/s；第 2012、2089 行禁用两个 KAT 控制器。DirectAngle 保存的前进/转向上限也是 0.5 m/s、1 rad/s（第 2098、2130 行），但组件当前在磁盘场景中禁用。第 1463–1474 行视频 WHEP 地址为 `http://192.168.2.36:8889/insta360/whep`，`compensateRobotHeading=0`，延迟参数 0.4 s；第 1949 行 livoscan 地址为 `http://192.168.2.22:8770`。这些都不是 Play 实例或设备在线状态。ROS 连接资源保存 `192.168.2.36:10000`，见 [连接 Prefab](../Assets/Resources/ROSConnectionPrefab.prefab) 第 47–49 行。

**航向来源。** DirectAngle 源码默认 `JetsonGyro`，尚未首次收到 gyro 字段时用 `WheelPlusSlam`；收到后若 gyro 过期，不改用另一参考系。该组件场景块尚未保存新的 `headingSource` 枚举，仍留有旧 `useSlamHeadingCorrection` 字段，不能凭旧字段判断运行来源。依据 [DirectAngle](../Assets/KAT/KatHuskyDirectAngleController.cs) 第 131、199 行及 [MainScene](../Assets/MainScene.unity) 第 2089–2156 行。`Assets/CmdVelPublisher.cs` 是空占位类，实际键盘发布器位于 `HuskyController.cs`；独立的 [OdometrySubscriber](../Assets/OdometrySubscriber.cs) 默认订阅 `/odom`，不能把它等同于 DirectAngle 的反馈入口。

### 8.3 双方坐标与时间字段

| 字段或变换 | 当前实现语义 | 依据 |
|---|---|---|
| 地图位姿 | `frame=livoscan_map`，`body=livox_imu`；单位米，z 向上；位置参考点是雷达 IMU。齐次坐标下桥应用 `p_level = T_level_from_map × T_map_odom × p_odom`，其中 `T_map_odom` 将本次 odom 映射到先验地图 | 对端 [桥](../../livoscan-mywork/scripts/xr_bridge_server.py) 第 50、85、119–130 行 |
| Unity 显示换轴 | `p_unity=(-y,z,x)`；地图 yaw 从 rad 转为 Unity 的 `-yaw×Rad2Deg`。只解决轴与角度约定，不提供 IMU→底盘外参 | [LivoscanDataSource](../Assets/Livoscan/LivoscanDataSource.cs) 第 6–8、59、63、225 行 |
| `yaw` 与 `yaw_odom` | `yaw` 是地图变换后的朝向；`yaw_odom` 是会话内 gyro 积分航向，逆时针为正，不施加地图重定位变换。静止校准完成后、首次运动前收到的第一条 SLAM 才设置起点；否则从 0 开始，不能保证与地图或 Husky odom 原点一致 | 对端 [桥](../../livoscan-mywork/scripts/xr_bridge_server.py) 第 109–128 行、[gyro_heading.py](../../livoscan-mywork/scripts/gyro_heading.py) 第 63–95 行 |
| `t` | 所选 Odometry 的 ROS header 时间，单位秒，桥发送时保留 3 位小数；快位姿模式源自 IMU stamp，标准模式源自 SLAM pose stamp | 对端 [桥](../../livoscan-mywork/scripts/xr_bridge_server.py) 第 127、168 行、[imu_pose_node.py](../../livoscan-mywork/scripts/imu_pose_node.py) 第 51–59 行 |
| `heading_t` | heading 的 ROS header 时间，源自 IMU stamp，单位秒，保留 3 位小数。Unity 用它去重；旧服务无该字段时逐消息视为新输入。`TryGetGyroHeading` 的 `sourceTime` 当前保存的是 `pose.t`，不能当成 `heading_t` | 对端 [桥](../../livoscan-mywork/scripts/xr_bridge_server.py) 第 112、172 行；[LivoscanDataSource](../Assets/Livoscan/LivoscanDataSource.cs) 第 231–239 行 |
| `age_s` | 桥的单调时钟距上次 `set_pose` 的时间；快位姿模式下不是距原始 SLAM 更新的时间。Unity `PoseAgeSeconds` 在此基础上加本地收到后经过的时间 | 对端 [桥](../../livoscan-mywork/scripts/xr_bridge_server.py) 第 108、130 行；[LivoscanDataSource](../Assets/Livoscan/LivoscanDataSource.cs) 第 55–57 行 |
| `heading_age_s` | 桥的单调时钟距上次收到 heading 的时间，与 pose 年龄独立。Unity 默认分别要求它和本地接收间隔不超过 0.2 s；不是把两者之和与 0.2 s 比较。外推使用二者之和，最大 0.4 s | 对端 [桥](../../livoscan-mywork/scripts/xr_bridge_server.py) 第 112、117 行；[GyroHeadingTracker](../Assets/KAT/GyroHeadingTracker.cs) 第 51、104–119 行 |

源端时间戳继承自 ROS 消息，实际时钟同步、仿真时间、传感器时基和运行时回退需要另查。上述年龄字段从桥接收时刻计起，不是传感器曝光/采样到 Unity 的完整延迟。视频程序的 GStreamer PTS 按帧递增，拼接回调时间戳未传入发布接口，不能与 pose 时间戳直接做逐帧同步；依据 [相机程序](../Deployment/HuskyInsta360/src/camera_stitch_test.cpp) 第 80、125、330 行。

### 8.4 已知处理边界与待确认项

以下是本次阅读发现的检查重点，不是针对某个已复现故障的根因结论，也不包含本轮修复：

- **无首条 pose。** 桥即使已有 heading，只要尚无第一条 pose 就不发 pose 消息；首条之后可重复发旧 pose 并独立更新 heading。依据对端 [桥](../../livoscan-mywork/scripts/xr_bridge_server.py) 第 104–112、252 行。首次启动、SLAM 中断和 heading 单独停更必须分开测试。
- **不同消费者的失效策略不同。** DirectAngle 的新鲜里程计门限可使两路指令归零；gyro 过期停止转向，前进仍受其他门限约束。键盘发布器没有同样的反馈过期门限，不能把 KAT 保护描述为所有控制源共有。依据 [DirectAngle](../Assets/KAT/KatHuskyDirectAngleController.cs) 第 1292–1298 行和 [HuskyController](../Assets/HuskyController.cs) 第 30–61 行。
- **解析与协议校验并非完整。** Unity pose 路径检查 position 长度，但没有全面检查 position/yaw 的有限性；点云头的版本被读取但未校验。桥 `set_pose/set_heading` 未显式核查 finite、输入 frame 或源时间单调性。依据 [LivoscanDataSource](../Assets/Livoscan/LivoscanDataSource.cs) 第 124、219 行及对端 [桥](../../livoscan-mywork/scripts/xr_bridge_server.py) 第 115–130、166–172 行。异常样例验证仍待具体任务执行。
- **旧对接说明有已被更正的内容。** 对端 [handoff](../../livoscan-mywork/docs/xr-unity/GYRO_HEADING_HANDOFF_20261020.md) 第 55 行仍写 `(t,yaw_odom)` 去重；新 [接口说明](../../livoscan-mywork/docs/xr-interface.md) 第 55–57 行和 [SYNC](../../livoscan-mywork/docs/xr-unity/SYNC.md) 的 2026-10-20 晚 3、晚 2 条目（本轮更新后第 29–40、51–61 行）已更正为 `heading_t`。历史限速也有差异，使用上面的场景证据再核对运行实例，不据旧记录改参数。
- **启动选项与字段能力分开判断。** `FAST_POSE=1` 控制 wrapper 启动节点并切换 pose topic；桥 ROS 模式本身始终订阅 heading。字段出现还取决于是否收到 heading。依据对端 [xr_bridge.sh](../../livoscan-mywork/scripts/xr_bridge.sh) 第 52–60 行和 [桥](../../livoscan-mywork/scripts/xr_bridge_server.py) 第 109、172 行。
- **部署仍待确认。** 两端本地代码已经核对；Jetson/Husky 实际运行版本、真实频率、外参、时间同步、有效控制源、当前 Play 配置、Quest 显示与实车动作均未在本轮验证。没有具体故障复现及同次日志，暂不提出功能修复。

### 8.5 本轮已做与未做的检查

本轮整理流程，读取双方相关代码、配置、历史说明和 Git 基线；核查文档链接、模板覆盖和变更范围。没有运行 Unity、ROS/设备联调、机器人运动、编译或功能回归，因此没有新增功能验证通过的结论。

## 9 流程维护

主流程保存在本文件，两个仓库的 `AGENTS.md` 保存执行摘要和入口。流程修改方同步维护两端摘要及版本/日期；无法同步时在交接记录中标明待同步。接口变更时同步更新对应接口说明与任务交接记录；源码事实有变化时更新第 8 节或重新建立任务基线，不覆盖旧实验数据。

流程只规定怎样取得并报告证据，不替代专项技术协议或已明确的用户指令。新会话先读项目规则；跨机器工作时将本流程及本次交接记录一并带到对端，不能依靠另一会话自动记住讨论内容。
