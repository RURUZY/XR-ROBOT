# KAT Husky 闭环控制 —— 工作记录

记录到：2026-08-14。这份文件是给"下次打开项目忘了做到哪"的自己看的，不是最终文档，随时改。

## 这个目录在做什么

人踩在 KAT Walk 跑步机上走路/转身，读取 KAT SDK 的姿态数据，换算成 `/cmd_vel`（`geometry_msgs/Twist`）发给 ROS 那边的 Husky 机器人。**2026-08-14 之前**的几版都是让机器人再用 `/odometry/filtered` 的实际朝向做闭环反馈；**2026-08-14 新增的 `KatHuskyDirectController.cs`** 改成了完全开环，见下面新的一节。

## 关键文件

| 文件 | 状态 | 说明 |
|---|---|---|
| [`KatHuskyDirectController.cs`](KatHuskyDirectController.cs) | **2026-08-14 新建，未接入场景** | 开环双通道版本：前进只看踏步/滑动强度，转弯只看身体转动**速率**（不再积分成目标航向），两个通道互不读对方的值，也不读里程计。里程计仅做 GUI/日志参考。详见下面"2026-08-14"整节。 |
| [`KatHuskySettleAnchorController.cs`](KatHuskySettleAnchorController.cs) | **场景当前实际挂载的版本** | 2026-08-10 新建。用 `/odometry/filtered` 做闭环航向、加了"settle anchoring"防漂移机制、转弯时会给前进降速、走路时会给转弯打折。2026-08-14 排查"做不到前进+转弯"问题时，判断这几处耦合就是根因，因此另开了 `KatHuskyDirectController.cs`，这个文件本身没有被动过。 |
| [`KatHuskyClosedLoopController.cs`](KatHuskyClosedLoopController.cs) | 保留作参考基线，**没有被使用** | 2026-08-05 定型后没再改过。 |
| [`Simulation/KatHuskySimTestRig.cs`](Simulation/KatHuskySimTestRig.cs) | 未涉及本次改动 | 同样是 2026-08-05 定型。 |

场景里的挂载：[`Assets/MainScene.unity:2163`](../MainScene.unity#L2163)，组件其实是**键盘**的 `CmdVelPublisher`（[`HuskyController.cs`](../HuskyController.cs)），不是任何一个 KAT 控制器。场景里目前**没有**挂载任何 KAT 控制器脚本（`KatHuskyDirectController`/`KatHuskySettleAnchorController`/`KatHuskyClosedLoopController` 的 guid 都搜过，MainScene 和所有 prefab 里都没引用）。要测 KAT 控制，需要手动把 `KatHuskyDirectController` 挂到机器人物体上，并且**禁用**掉键盘的 `CmdVelPublisher`，避免两个脚本同时发 `/cmd_vel` 互相覆盖。

⚠️ **`.git` 是空仓库**（没有任何 commit），没法用 git log/diff 找历史版本，只能靠文件内容和文件系统时间戳（mtime/birth time）互相佐证。建议尽快 `git add -A && git commit` 一次把当前状态存下来，不然下次再出现"这版本什么时候改的"这种问题还是没法查。

## 当前控制逻辑（2026-08-10 版本）

两套独立的子系统，只有一处单向耦合：

**转弯量（angular.z）** —— 只看 KAT 体感的朝向四元数，跟走路快慢无关：
- [`TryGetHorizontalBodyYaw`](KatHuskySettleAnchorController.cs#L496-L519) 从四元数取水平 yaw
- [`UpdateBodyHeading`](KatHuskySettleAnchorController.cs#L330-L409) 累积成目标朝向 `desiredRobotYawDeg`，跟机器人实际朝向 `robotYawDeg` 比较得到 `headingErrorDeg`
- [`CalculateAngular`](KatHuskySettleAnchorController.cs#L546-L572)：`angular = 转身角速度前馈 + 朝向误差反馈`，做加速度限幅

**前进量（linear.x）** —— 用 `data.moveSpeed` 的 x/z 合成幅值，不是单纯 `.z`：
```csharp
lastRawMoveSpeed = data.moveSpeed;
lastRawForward = new Vector2(lastRawMoveSpeed.x, lastRawMoveSpeed.z).magnitude;
```
（[KatHuskySettleAnchorController.cs:224-236](KatHuskySettleAnchorController.cs#L224-L236)）原因：转身时踩踏力量会从 `.z` 分流到 `.x`，只读 `.z` 会把"边走边转"误判成"没在走"。改成合成幅值后，转身不会让前进速度无端归零。

**唯一耦合点**：`linear *= CalculateTurningSpeedMultiplier()`（[L302-304](KatHuskySettleAnchorController.cs#L302-L304)）—— 转得越猛前进速度打折越多；走路中最低打 5 折，站定不走最低可以打到 0 折（原地大转身会被限到接近停住，边走边转不会被停死）。转弯**不会**反过来影响前进方向本身。

## 今天处理的问题：settle-anchor 打断"边走边转"

**现象**（用户报告）：8月5日那版可以边走边转，8月10日这版不行。

**排查**：`.git` 是空的查不了历史，靠文件 mtime/birth time 和全项目 grep "settle" 确认：settle-anchor 这套逻辑是 2026-08-10 才第一次出现的（[`KatHuskySettleAnchorController.cs`](KatHuskySettleAnchorController.cs) 的 birth time 是当天 16:41），8月5日定型的 `KatHuskyClosedLoopController.cs` 里完全没有。

**根因**：[`UpdateSettleAnchor`](KatHuskySettleAnchorController.cs#L424-L460) 原本只看"转身速率 < 3°/s 且朝向误差已收敛"，走路中步态起伏经常会让瞬时转身速率短暂跌破阈值，导致转弯做到一半就被当成"已经转完"而清零目标朝向，把连续的转弯切成一段段。

**修法**：给 settle 触发条件加了"必须真正停下脚步（`!isWalking`，用现成的 `walkDeadZone` 判断）"这道门，走路中（不管走直线还是边走边转）settle 永远不触发；只有真正站定时才会在转身速率<阈值、朝向误差已收敛的条件下，0.3秒后重新校准归零。

**还没做的**：⚠️ 没法在这个环境里接硬件实测，逻辑上应该跟 8 月 5 日那版一致了，但**需要实机走一遍边走边转确认**。另外这个改动的副作用是：原本 settle-anchor 想解决的"长时间连续走路直线时的漂移"，现在只有在用户真正停下来时才会被修正——如果以后发现"走了很久的直线也会漂"，需要回来重新考虑一个更精细的判据（比如区分"步态摆动"和"真的在转"，而不是简单地整段走路期间完全禁用）。

## 2026-08-14：新增开环版 `KatHuskyDirectController.cs`

**起因**：用户反馈"做不到前进+转弯同时控制"。排查发现 `KatHuskySettleAnchorController.cs` 里有好几层耦合叠在一起——`walkingBodyYawAccumulationScale` 走路时给转弯打折、`CalculateTurningSpeedMultiplier` 转弯时给前进打折、闭环航向依赖 `/odometry/filtered` 新鲜度（过期直接整体停车）——单独看都合理，叠在一起会互相拖累，和键盘 `CmdVelPublisher`（[HuskyController.cs](../HuskyController.cs)）那种"两个独立通道、按下就响、松开归零"的直接手感完全不是一回事。

**架构决定**：不修旧文件，另开 `KatHuskyDirectController.cs`。核心原则——`forwardIntent`/`turnIntent` 两个通道各自独立算，互不读对方的值、互不读走路/转弯状态、都不读里程计：

- **forward**：踏步/滑动合成强度（`sqrt(moveSpeed.x²+z²)`，沿用 settle-anchor 版本已验证过的修正）→ 死区 → attack/release 低通 → 响应曲线。
- **turn**：不再积分成"目标航向"跟里程计做位置闭环，改成纯速率控制——身体转动**速率**（不是角度）直接映射到 `angular.z`，类比"按住方向键转多久=转多少"，而不是"转身多少度=机器人转多少度"。闭环航向精确复现这件事被有意舍弃掉了，换来响应即时、不拖尾；机器人本身的轮速闭环（编码器→电机 PID）不受影响，那一层跟这次改动无关，一直都在。
- 里程计仍然订阅，但只喂 GUI/日志，不进 `linear`/`angular` 的计算，所以里程计过期/丢失不会再让整车停摆。

详细设计对话记录在这次会话里，代码本身的注释（尤其是类头注释）已经把"为什么"写清楚了，直接看 [KatHuskyDirectController.cs](KatHuskyDirectController.cs) 顶部。

**实测发现的问题（用实机 Console/Editor.log 数据核对，不是纸面推导）**：

1. **前进+转弯同时非零，架构改动确认有效**——日志能看到 `Linear`/`Angular` 同时非零且各自独立波动。
2. **KAT 原始身体朝向按整度量化**：`BodyRaw` 抓了几百帧全是整数，直接对原始角度求导会把每次量化跳变放大成瞬时尖峰。修法：加了 `bodyYawFilterTime`，先对角度本身低通、再对平滑后的角度求导（[UpdateTurnRate](KatHuskyDirectController.cs#L267-L295)），不是先求导再滤波。
3. **⚠️ 未修复：`MiniSExtraData.cs` 疑似有 marshaling bug。** 实测 602 帧里 `isMoving`/`isLeftGround`/`isRightGround`/`motionType`/`skatingSpeed`/`lFootSpeed`/`rFootSpeed` 等**全部字段、无一例外**读数为 0/false，即使同时段确实在真实走路转弯。对比同目录 `WalkC2ExtraData.cs`，它的 `bool` 字段都写了 `[MarshalAs(UnmanagedType.U1)]`，而 `MiniSExtraData.cs` 的 `extraInfo` 结构体**一个 `MarshalAs` 都没写**，bool 会被 C# 按 4 字节 Win32 BOOL 解析而不是 native 的 1 字节，导致结构体从第二个字段起全部错位读取——这能完整解释"全字段恒为 0"这个现象。**已经提出修复方案（照抄 `WalkC2ExtraData.cs` 的写法加 `MarshalAs`），但改动被用户暂停/搁置，`MiniSExtraData.cs` 目前还是原样，没有生效。** 下次要处理"踏步识别不灵敏"这个问题时，先看这个 bug 修没修，不要again从头排查。
4. 顺带发现一次测试里 `reverseModeEnabled` 全程是开着的（`Linear` 全程为负、`RawFwd` 却是正的，数值反推和 `maxReverseSpeed` 上限吻合）——不是代码 bug，是倒退模式锁存开关被误触发，评估手感前记得先看屏幕左上角状态文字有没有写 "REVERSE"。
5. **场景里还没接这个新控制器**（见上面"关键文件"表格）——下次真正上机测试前记得先手动挂 `KatHuskyDirectController`、关掉键盘的 `CmdVelPublisher`。

## 待办 / 下次接着看

- [ ] `KatHuskyDirectController.cs` 挂到场景里实机测试（forward-only / turn-only / 边走边转 / 松开立即停 四种场景分别验证）
- [ ] 决定 `MiniSExtraData.cs` 的 `MarshalAs` 修复要不要应用；应用后重新采集一次数据，看 `isLeftGround`/`motionType`/`skatingSpeed` 是否还是恒零——如果修复后仍然全零，才能真正确认是硬件/固件不支持，而不是解析问题
- [ ] 标定 `turnRateForMaxOutput`（当前 90°/s 是拍的默认值，没有实测校准）
- [ ] 实机测试确认（`KatHuskySettleAnchorController.cs` 相关）边走边转已恢复，且站定时的 settle-anchor 防漂移仍然有效
- [ ] 长时间直线行走是否还会漂移（如果会，settle 的"必须停下"门槛可能要放宽）
- [ ] `Assets/KAT/README.md`（这份文件）保持更新，尤其是每次调过参数或改过控制逻辑之后
- [ ] 考虑把项目纳入正常的 git 版本管理（当前 `.git` 是空的），至少定期 commit，避免再靠文件时间戳猜历史
- [x] 踏步信号已接入前进逻辑：Walk Mini S 使用 `MiniSExtraData.isMoving`；Walk C2（SDK 设备名含 `Coord2`）使用其正确的 `WalkC2ExtraData` 布局，以 `motionType` 的 MICROACTION/MOVE 或双脚水平速度检测踏步。检测后生成可配置的合成前进输入。场景默认 `enableStepInPlaceDrive=true`、`stepInPlaceInput=0.35`、`stepSignalHoldTime=0.15s`、`stepFootSpeedThreshold=0.05`；原有死区、速度曲线、转向降速、反向开关和安全停止仍然有效。
- [x] 实机日志确认 `KATVR Pro Mini(S)` 的 `extraData` 在当前 Runtime/SDK 组合下持续全零，因此增加控制器层的 `moveSpeed` 脉冲踏步回退：超过 `stepPulseThreshold=0.08` 的短促速度视为一步，并保持前进意图 `stepPulseHoldTime=0.60s`；连续左右踏步会不断刷新，停止踏步后自动超时停车。边走边转时最低前进比例提高到 0.75。
