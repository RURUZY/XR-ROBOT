# XR 小地图需求（交接文档，2026-10-09）

给谁看：在 XR-ROBOT Unity 工程里写代码的人（用户本人，或在那台电脑上运行的 Claude Code）。
这份文档是需求的唯一来源。Jetson 上的 livoscan 会话和 Unity 这边的会话不共享对话记录，有疑问先看这里，
文档里没写的，问用户再定。

## 1. 背景
- 系统：Clearpath Husky + Livox Avia 雷达 + ZED2i，Jetson AGX Orin 上跑 FAST-LIVO2 SLAM 和语义建图（RayFronts + 物体层）。
- 操作：用户戴 Quest 3，站在 KAT VR 上走动来遥控 Husky，主画面是 Insta360 X5 传来的 360° 全景视频（场景里的 `Insta360 360 Sphere`）。
- 目标（论文：基于 XR 的移动机器人遥操作与三维环境重建）：操作者在看全景视频的同时，
  通过一个**小地图**知道机器人在建好的地图里的位置，以及周围有哪些物体。

## 2. 已经完成的（2026-10-09 验证）
- Jetson 上的服务 `scripts/xr_bridge_server.py`（livoscan-mywork 仓库），地址 `http://192.168.2.22:8770`。
- Unity 客户端已导入 `Assets/Livoscan/`：`LivoscanWorldClient.cs`、`LivoscanVertexColor.shader`，
  材质 `LivoscanPoints`（Livoscan/VertexColor）、`LivoscanBoxes`（Standard，Fade）。
- 在 Scene 视图里测通：1:1 大小的房间点云（约 32 万点）、59 个物体框和标签、机器人标记（回放模式下沿录制轨迹移动）。

## 3. 接口（`livoscan.xr.v0`，详见 livoscan-mywork 的 `docs/xr-interface.md`）
- `GET /info`、`GET /world_model.json`、`GET /map/points.bin`（"LVXP" + uint32 版本 + uint32 N + N×(3×float32 + 4×uint8)，小端）。
- `GET /ws` WebSocket（JSON 文本帧）：
  - `{"type":"objects","seq":..,"objects":[{"id","label","center":[x,y,z],"size":[sx,sy,sz],"yaw","confidence","observations"}]}`：连接时发一次，之后每 1 秒发一次全量；
  - `{"type":"pose","t":..,"position":[x,y,z],"yaw":..,"orientation_xyzw":[..]}`：约 10 Hz，是雷达 IMU 的位姿（离地约 0.56 m），不是 Husky 底盘中心。
- 坐标系 `livoscan_map`：米，z 朝上，地面 z = 0，x 是建图起点时雷达的朝向。
  换算到 Unity（与 `OdometrySubscriber.cs` 一致）：`unity = (-y, z, x)`，`unity_yaw_deg = -yaw_rad * Rad2Deg`；
  框的大小在 Unity 里是 `(size[1], size[2], size[0])`。
- 服务有两种模式：回放（位姿按录好的轨迹循环）和实时（`--ros`，位姿来自正在运行的 SLAM）。
  **实时模式下，地图和物体仍然来自离线结果**，边开车边长出地图（实时点云推送）还没做。
- 每次实时 SLAM 启动都会以当时的位置为原点，所以实时位姿和离线地图会有偏移，在对齐功能做好之前，这是已知限制。

## 4. 本次要做：方案 A，挂在左手手柄上的小地图（必须）
1. **位置**：小地图挂在左手手柄上，像看手表一样，抬手可见，放下不挡视线。用 XR Interaction Toolkit 2.4.3 的左手控制器作为父物体。
2. **大小**：整个房间缩小到大约 0.3–0.5 m 见方，比例做成 Inspector 参数（默认约 0.03）。
3. **朝向**：默认**跟着车转**（机器人车头永远朝向手腕的"前方"），保留一个开关可以改成固定朝北。
   做法：每帧用最新 pose 的 `yaw` 反向旋转地图，并让地图以机器人位置为中心。
4. **内容**：点云（可降采样，比如每隔 N 个点取一个，用于头显性能）、物体框和类别名、机器人标记（带车头方向的箭头或三角）。
5. **开关**：左手柄的一个按键（例如 X 键）切换显示或隐藏；默认显示。
6. **显示在全景视频前面**：全景球（`Insta360InsideSphere.shader`）是 Queue Geometry、ZWrite On。
   小地图的材质要参考 `RobotFootprintOverlay.shader`：Queue Transparent、ZTest Always、ZWrite Off，否则会被视频球挡住。
7. **文字**：头显里不要用 OnGUI（不支持双眼立体渲染）。标签用 TextMesh/TextMeshPro 或 World Space Canvas
   （参考 `KatStatusHud.cs`），并让它朝向头显相机。
8. **不能影响现有功能**：`/cmd_vel` 控制（`HuskyController.cs`/`CmdVelPublisher`、KAT 控制器）、ROS-TCP 连接、
   Insta360 视频、`HuskyBodyReferenceOverlay` 都保持原样。小地图只通过 HTTP 和 WebSocket 读取 Jetson 的数据，不发任何东西。
9. **断线**：Jetson 服务没开或断开时不能报错刷屏，显示一行"未连接"即可，并自动重连（现有客户端每 2 秒重连一次）。

建议在现有 `LivoscanWorldClient` 的基础上做：把"接收数据"和"显示"分开，1:1 地图和小地图共用同一份数据。

## 5. 需要顺便修的问题
- **标签文字是反的**：现在的 `LateUpdate` 让标签朝向 `Camera.main`，在 Scene 视图里看是镜像。
  在头显里要确认方向正确；如果是反的，就改成 `LookRotation(cam.forward)` 这一类写法。
- **点云颜色偏淡**：工程如果用的是线性色彩空间，顶点颜色需要从 gamma 转到线性
  （在 `LivoscanVertexColor.shader` 里对 `v.color.rgb` 用 `GammaToLinearSpace`，并用 `UNITY_COLORSPACE_GAMMA` 判断）。
- **Quest 上点只有 1 像素**：小地图里的点可能太稀，必要时改成小方块或增大点的尺寸（要用 instancing 或 geometry 方式实现）。

## 6. 以后再做（这次不做，只要不妨碍以后扩展即可）
- **方案 B：全景视频里的方向标签**：在 360° 画面里，物体所在的方向上浮一个标签，比如"chair 3.2 m"。
  需要先标定 Insta360 相对雷达的旋转（3 个角度），还要补偿视频延迟。
- **方案 C：3D 框精确叠加在视频上**：需要完整外参（6 个参数）加延迟补偿。
- **实时点云推送**：由 Jetson 端开发，到时会扩展 WebSocket 消息类型，版本号也会升级。

## 7. 验收
1. 在编辑器里点 Play（Jetson 回放模式）：左手柄上出现小地图，能看到房间、物体框和移动的机器人；按 X 能显示和隐藏；
   全景视频照常播放，不会挡住小地图；`/cmd_vel` 控制照常工作。
2. 在 Quest 3 头显里：文字是正的、能看清；帧率没有明显下降；抬手能看，放下不挡视线。
3. Jetson 实时模式（KAT VR 彩排时）：机器人标记跟着真车移动和转向；跟车转模式下，车头始终朝手腕前方。

## 8. 还需要用户决定
- 小地图默认用跟车转（推荐）还是固定朝北？
- 显示开关用哪个键？（默认 X）
- 头显是 PC 串流（Link/Air Link）还是 Quest 独立运行？这会影响点云能显示多少点。
