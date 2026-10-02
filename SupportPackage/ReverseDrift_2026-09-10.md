# 倒车画面左漂：复核与 Clearpath 新录包

## 已核实与尚未核实

2026-09-10 重新读取本地两份原始 ROS bag，SHA-256 与旧报告一致：

| 记录 | 时长 | /cmd_vel 与底盘 cmd_vel 最大绝对 angular.z | 轮式 odom 累计 yaw | filtered odom 累计 yaw |
| --- | --- | --- | --- | --- |
| reverse_check（9 月 7 日） | 39.860 秒 | 0 | +19.219° | +19.059° |
| reverse_debug（9 月 7 日） | 33.148 秒 | 0 | +197.892° | +198.023° |

角度按相邻样本展开累计，不会在 ±180° 处丢失转角。两包没有前进指令，不能用它们证明前进正常。旧报告还记录 cmd_vel_out 的 angular.z 为零，以及驱动已解码 travel 左右不一致：第二包左侧累计 -8.272 m，右侧 -0.879 m；两侧速度反馈却都为负。该 travel 证据位于主机已解码层，不能直接判断 MCU、解析器或编码器哪个坏了。

当前磁盘 MainScene：KatHuskyDirectController 已挂载但禁用；键盘 CmdVelPublisher 启用；全景 compensateRobotHeading=1，输入 /odometry/filtered，sign=1。运行中未保存的 Inspector 设置和头显已安装构建可能不同。

MediaMtxWhepReceiver.cs 的 OnHeadingOdometry 从姿态四元数提取 yaw；LateUpdate 把相对 yaw 直接用于旋转全景球。因此如果车身未转而 odom 转了，画面也会产生虚假旋转。不是把 rad/s 当成角度积分：yaw rate 的 Rad2Deg 转换只供诊断显示。Direct 控制器的倒车只反转 linear，angular 独立计算；它不会主动保持直线航向。

当前证据支持：倒车反馈异常 → 轮式里程计虚假转角 → 融合里程计跟随 → 全景补偿引起画面漂移。车身未明显转动来自此前人工观察，仍需外部固定机位视频佐证。旧记录中 /imu/data 没有消息/发布者，使融合缺少该独立约束，但不能据此认定实体 IMU 不存在。升级后是否仍发生，必须用新包验证。

不应在取证前用倒车冻结 yaw、角速度偏置或未经核实的里程计闭环补偿掩盖异常。本次没有修改控制器或场景。

## 在真实 Husky 的 ROS 1 终端录制

Clearpath 9 月 9 日邮件要求：软件更新后重新录制连续 10–15 分钟、使用 rosbag record -a，并明确说明问题发生在实体 Husky A200 还是仿真。旧包只有约 40 秒和 33 秒，不满足要求。更新完成情况需按实际确认，不能仅凭邮件认定所有组件已升级。

在平整、有足够距离的测试区域，由操作员控制车辆并保留随时停车能力；下面命令只采集数据，不发布运动指令。使用已能访问真实底盘 ROS master 的终端，保留实际使用的工作空间环境。

```bash
source /opt/ros/noetic/setup.bash
mkdir -p ~/husky_support
cd ~/husky_support
run_dir="reverse_$(date +%Y%m%d_%H%M%S)"
mkdir "$run_dir"
cd "$run_dir"

date -Is > system.txt
hostname >> system.txt
uname -a >> system.txt
cat /etc/os-release >> system.txt
printenv ROS_DISTRO ROS_MASTER_URI ROS_IP ROS_HOSTNAME >> system.txt
rosversion husky_base >> system.txt
dpkg-query -W 'ros-noetic-husky*' > husky_packages.txt 2>&1
apt list --upgradable > pending_updates.txt 2>&1
rosparam dump rosparams.yaml
rosnode list > nodes_before.txt
rostopic list -v > topics_before.txt
rostopic info /imu/data > imu_info.txt 2>&1
df -h .

rosbag record -a --duration=12m -O husky_all_12min.bag

rosbag info husky_all_12min.bag | tee bag_info.txt
rostopic list -v > topics_after.txt
sha256sum husky_all_12min.bag > SHA256SUMS.txt
```

apt list 使用现有软件源缓存，不是已经完成更新的证明。按实际维护流程确认更新，并补充更新日期和支持方要求的固件版本；不要为了录包盲目刷新固件。

录包终端保持打开，注意磁盘空间和 recorder 的丢消息/缓冲区警告。12 分钟是采集时长，不是连续倒车时长。若中途必须结束，Ctrl+C 等待文件正常关闭；不足 10 分钟需要重新完成一轮。确认生成 .bag 而不是残留 .bag.active，bag_info 的实际时长应在 600–900 秒。

## 12 分钟复现安排

| 时间 | 操作 |
| --- | --- |
| 0–1 分钟 | 静止，记录初始车头与画面方向 |
| 1–3 分钟 | 多次短距离直线前进，每段之间停车 |
| 3–6 分钟 | 多次短距离直线倒车，覆盖低速和原来出现问题的速度，每段停车 |
| 6–8 分钟 | 低速左转、右转与停车，提供真实转向对照 |
| 8–11 分钟 | 使用发生问题时相同的控制方式反复复现倒车左漂 |
| 11–12 分钟 | 静止，观察里程计/画面是否继续漂移 |

保持唯一有效运动指令来源，记录是键盘、KAT 还是手柄。若做不同控制器对照，先停车切换并写明时间，不要同时启用多个发布器。每次异常记录墙钟时间、车身是否真实转向、屏幕显示的 linear/angular、Odom yaw 和 Sphere Y。可将操作事件手写到 notes.txt，不必改控制代码。

同步录 Unity/头显画面和外部固定机位下的车身。WebRTC 视频若未发布成 ROS topic，record -a 不会把视频收入 bag；Unity 本地日志同样需要单独保存。固定机位最好能看到地面直线参照，用来区分真实偏航与纯显示漂移。

bag_info 中重点检查 /cmd_vel、/husky_velocity_controller/cmd_vel、/husky_velocity_controller/cmd_vel_out、/joint_states、/husky_velocity_controller/odom、/odometry/filtered、/tf、/tf_static、/status、/diagnostics、/rosout、/rosout_agg 和实际 IMU topic。名称可以因配置不同而变化；应录全部实际话题，不是只录这份列表。不存在的 topic 不会被 -a 自动补出，缺失情况要说明。

## 提交内容和结果判读

提交新 bag、bag_info、版本/参数/话题快照、复现时间表，以及对应视频和 Unity 日志。明确说明实体 Husky A200；当前项目的 Unity 是遥操作和全景显示端，这不等于机器人运行在仿真中。旧项目记录指向实体机，提交前以实际本次测试为准。

- 若输入及底盘最终指令 angular.z=0、车身未转，而轮式 odom 转：继续查轮反馈/驱动/固件，优先解释右侧累计 travel 与速度不一致。
- 若 angular.z 非零：先定位实际发布器及输入；KAT 身体 yaw 波动和键盘轴输入都需查看。
- 若车身确实转：检查真实左右轮运动、地面/轮胎差异及底盘反馈，不能只归因于显示。
- 若 odom 稳定但画面仍漂：回到全景映射、XR 朝向和视频本身的稳定处理排查。

参考：
- ROS Noetic record 参数实现：https://github.com/ros/ros_comm/blob/noetic-devel/tools/rosbag/src/record.cpp
- Clearpath ROS 1 Husky 故障诊断：https://docs.clearpathrobotics.com/docs_robots/legacy/ros1_robots/outdoor_robots/husky/troubleshooting_husky/

本次只完成本地代码/历史数据复核与录包准备；没有连接实体 Husky、核验其更新状态或生成新的实机录包。
