# 录制清单与问题记录

建立日期：2026-10-29。两部分：①每次实车测试前要准备哪些录制；②按日期追加的问题记录，最新一条在最上面，防止遗忘。

topic 名称来自代码和现有文档，注明了依据。标"待确认"的项没有在本仓库里核实，开录前先用 `rostopic list` / `ros2 topic list` 对一遍，不要凭此表假定存在。

## 一、每次录制前的清单

### 1. 开始前（5 分钟）

- [ ] 记下本次要复现的**一个**问题，写进下面的问题记录，并写好失败判据（例如"转回正前方后车头偏离画面中心超过 X 度"）。
- [ ] 记录版本：本仓库 `git rev-parse --short HEAD` 和未提交修改；`livoscan-mywork` 同样；Jetson/Husky 上实际部署的版本另记。
- [ ] 记录配置：`headingSource`、`panoramaHeadingMode`、`compensateRobotHeading`、`videoHeadingDelaySeconds`、`alignViewOnActivation`。注意区分源码默认值、Inspector 序列化值和 Play 实例值。
- [ ] 确认磁盘空间（`df -h .`）和三台设备的时钟情况（Husky、Jetson、PC 是否同步，不确定就写"待确认"，不要假定）。
- [ ] 测试区域平整、有足够距离，操作员保留随时停车能力。

### 2. Husky 侧 ROS 1 bag

| 项目 | 说明 | 依据 |
|---|---|---|
| 推荐命令 | `rosbag record -a --duration=<时长> -O <名字>.bag`，先 `rostopic list -v > topics_before.txt`、`rosparam dump rosparams.yaml` | [ReverseDrift_2026-09-10.md](../SupportPackage/ReverseDrift_2026-09-10.md) 的录制步骤 |
| 需要给 Clearpath 支持时 | 连续 10–15 分钟、`-a`，并说明是实体 Husky A200 | 同上，9 月 9 日邮件要求 |
| 只做本地诊断的最小集合（`-a` 之外的备选） | `/odometry/filtered`、`/cmd_vel`、`/husky_velocity_controller/cmd_vel`、`/husky_velocity_controller/cmd_vel_out`、`/odom`、`/imu/data`、`/tf`、`/tf_static` | 前几项见 [KatPlayCapture.cs](../Assets/KAT/Editor/KatPlayCapture.cs) 第 267、272 行和各控制器的默认 topic；`/imu/data` 见 ReverseDrift 文档；`/tf` 系列待确认 |
| 结束后 | `rosbag info ... \| tee bag_info.txt`，`sha256sum`，确认生成的是 `.bag` 而不是残留 `.bag.active` | ReverseDrift 文档 |

### 3. Jetson / livoscan 侧（ROS 2，是否与 Husky 同一台机器待确认）

| topic | 用途 | 依据 |
|---|---|---|
| `/livoscan/heading`（`Vector3Stamped`，x/y/z = yaw/rate/bias，rad） | 当前 `JetsonGyro` 航向来源 | [ENGINEERING_WORKFLOW.md](ENGINEERING_WORKFLOW.md) §8.2 |
| `/livoscan/pose_fast`（仅 `FAST_POSE=1` 时存在） | 快位姿 | 同上 |
| `/aft_mapped_to_init`（`Odometry`） | SLAM 位姿 | 同上 |
| `/livox/imu` | 陀螺仪原始输入 | 同上 |

- 这一侧是 ROS 2，用 `ros2 bag record <topic...>`，与 Husky 的 ROS 1 bag 是**两个独立文件**，不能互相替代。
- 桥的 `/ws` 推送（`yaw`、`yaw_odom`、`heading_t`）不在 ROS bag 里。Unity 侧能录到什么见下一节。

### 4. Unity 侧（PC，自动）

| 项目 | 做法 |
|---|---|
| KAT 采集 | 点 Play 自动开始，退出 Play 自动出 ZIP，在 `KATCaptures/` 下。字段说明见 [KatPlayCapture.README.md](../Assets/KAT/Editor/KatPlayCapture.README.md) |
| 与本问题直接相关的 CSV | `control_panorama.csv`（全景模式、标定版本、头显/球体/车头朝向）、`control_view_overlay.csv`（车身框与目标箭头方向）、`control_request.csv`（方向误差和状态）、`control_odometry.csv`、`control_chassis_command.csv` |
| 打事件标记 | 菜单 `KAT > 控制测试标记`，在"开始倒车""转回前方""偏差出现"这些时刻手动打点（只做标签，不发运动指令） |
| 画面录制（可选） | 停止 Play 时勾选 `KAT > Record Game view on next Play`，只对下一次 Play 生效；有额外 GPU 和写盘开销，需要看画面叠加层时才开 |
| Console 日志 | 测试后另存。重点搜 `Panorama view aligned`、`TARGET DROPPED`、`NO TURN PROGRESS`、`Panorama heading frame changed` |
| 外部旁证 | 如果可能，用手机或相机拍一段"车头实际朝向 + 头显画面"的对照视频，用来判断偏差是画面里的还是车的 |

### 5. 录制中的操作顺序（每次尽量相同）

1. 静止、面朝身体正前方，按 Trigger 激活（打标记）。
2. 前进一段，停（打标记）。
3. 切换倒车，倒一段，停（打标记）。
4. 切回前进，**转回身体正前方**，停，等待 5 秒（打标记）。
5. 记录此刻画面里车头与正前方的偏差（估读角度或用对照视频）。
6. 全程避免中途松开 Trigger 或按 Y；如果发生了，在标记里写明。

### 6. 录完后

- [ ] 原始 bag、`KATCaptures/` ZIP、Console 日志和对照视频放在同一个按日期命名的目录，**不要覆盖或编辑原始数据**。
- [ ] 在下面的问题记录里补：实际执行的操作、环境、结果、证据位置、未验证项。
- [ ] 编译通过、离线测试通过、连通、联合功能通过、实机通过分别报告，不混写。

## 二、问题记录

格式：状态 / 现象 / 已确认 / 假设 / 下一步 / 证据位置。状态取值：待诊断、诊断中、已定位、已修复待验证、已验证、搁置。

### 2026-10-29　转回前进方向后车头不一定正好在画面中央

- **状态**：待诊断，尚无录制数据。
- **现象**（用户描述）：倒车后再转回前进方向，画面里车头位置不一定正好在车头应在的位置。偏差大小、是否随时间回正、是否随使用时间变大，均**待确认**。
- **已确认（代码依据，未经实机验证）**：
  - 倒车模式只把线速度取负（[KatHuskyDirectAngleController.cs](../Assets/KAT/KatHuskyDirectAngleController.cs) 第 595–598 行），不改变转向目标，也不翻转画面。
  - 车头在画面中的位置 = 按下 Trigger 时的头显 yaw + 之后车航向的变化量（[MediaMtxWhepReceiver.cs](../Assets/WebRTC/MediaMtxWhepReceiver.cs) 第 323–336、358–362 行）。
  - 车的转向目标 = 按下 Trigger 时的车航向 + 之后 KAT 身体转角的变化量（[KatHuskyDirectAngleController.cs](../Assets/KAT/KatHuskyDirectAngleController.cs) 第 972–981 行）。
  - 倒车模式在暂停、Y、Trigger 松开、急停、KAT 输入丢失时都会被清除（第 473–477 行），之后需要重新激活，激活时会重新对齐画面。
- **假设（按可能性，均无证据）**：
  1. 按 Trigger 对齐时头与身体不在同一方向，夹角一直保留。
  2. 车未转到位：角度容差内的残差，或制动/跟随滞后。
  3. 转向目标被丢弃（`NO TURN PROGRESS / TARGET DROPPED`，第 968 行），之后身体和车永久差一个角度。
  4. 头显 yaw 或 KAT 身体 yaw 缓慢漂移。
  5. 陀螺仪航向与相机真实转角不一致（比例或漂移），或 `videoHeadingDelaySeconds` 不准。
- **排除检查**：
  - 假设 1：看最后一次 `Panorama view aligned` 日志的头显 yaw，对比当时 KAT 身体朝向（`control_input.csv`）。
  - 假设 2：看偏差出现时刻的 `HeadingErrorDeg`（`control_request.csv`）是否非 0；停 5 秒后是否回正。
  - 假设 3：Console 日志有无 `TARGET DROPPED`。
  - 假设 4：偏差是否随使用时间单调变大；重新按 Trigger 后是否消失。
  - 假设 5：偏差是否只在转动中出现、停下后消失（延迟问题），还是累积（比例问题）。
- **下一步最小诊断**：按上面"录制中的操作顺序"录一轮，分别在"重新激活后立即测"和"长时间使用后测"各做一次。
- **验收（待定，需用户确认）**：转回前方并静止 5 秒后，画面里车头与真实车头的偏差不超过 ___ 度，连续 ___ 次。
- **不需要改**：倒车时相机镜头对着车尾，用户 10-28 重新考虑后认为可以接受，因为能看清倒车轨迹。这一点不再作为待改项。
- **证据位置**：待补。
