# XR-ROBOT

Unity XR 项目：用户站在 KAT Walk 跑步机上走路/转身，控制远端的 Clearpath Husky A200
机器人（`/cmd_vel`），同时通过挂在 Husky 上的 Insta360 X5 相机把机器人周围的实时
360° 画面传回 Meta Quest 3。

## 目录速览

| 位置 | 内容 |
|---|---|
| [`Assets/KAT/`](Assets/KAT/) | KAT 跑步机 → Husky `/cmd_vel` 的几版控制器，工作记录见 [`Assets/KAT/README.md`](Assets/KAT/README.md) |
| [`Assets/WebRTC/`](Assets/WebRTC/) | Unity 端接收 Insta360 360° 视频流（WHEP） |
| [`Deployment/HuskyInsta360/`](Deployment/HuskyInsta360/) | Husky 车载电脑上跑的 Docker：相机采集、360° 拼接、H.264 编码、mediamtx 推流 |
| [`Native/Insta360UnityBridge/`](Native/Insta360UnityBridge/) | Windows 端 C++ 桥接层源码，编译产物是 `Assets/Plugins/x86_64/Insta360UnityBridge.dll` |
| [`Insta360-联调记录-2026-08-14.md`](Insta360-联调记录-2026-08-14.md) | Insta360 → Husky → Quest 3 联调工作记录 |

## 第三方 SDK / Vendor SDKs

以下二进制**没有**提交进本仓库（体积大、属于厂商发行物、不是我们写的源码），需要自
己从官方渠道拿到后放到对应路径。对应的 `.meta` 文件保留在 git 里，所以文件放回去
之后 Unity 会直接沿用正确的平台导入设置，不需要重新配置。

### Insta360 Camera SDK + Media SDK（Windows，Unity 编辑器/Standalone 用）

放到 `Assets/Plugins/x86_64/`：

- `CameraSDK.dll`、`MediaSDK.dll`（Insta360 官方 SDK；本项目开发时用的版本号见
  [`Native/Insta360UnityBridge/build_bridge.bat`](Native/Insta360UnityBridge/build_bridge.bat)：
  `CameraSDK-20250812_192505-2.1.1-win64`、`MediaSDK-3.1.3-20260128-win64`）
- 上面两个 SDK 依赖的 OpenCV 4.7.0（含 CUDA 模块：`cudaarithm`/`cudafilters`/
  `cudaimgproc`/`cudawarping`/`cudafeatures2d`/`cudalegacy` 等）、NVIDIA
  cuBLAS/cuFFT/NPP 运行时、`exiv2.dll`、`tbb12.dll`、`msvcp140.dll`/
  `vcruntime140*.dll`、以及一批 `api-ms-win-crt-*.dll`（Windows 通用 C 运行时）—— 这些都是
  Insta360 SDK 自带的依赖，随 SDK 一起分发，不需要单独找。
- `Insta360UnityBridge.dll` 不是厂商文件，是我们自己的桥接代码，上面两项 SDK 就位
  后本地编译得到：
  ```powershell
  .\Native\Insta360UnityBridge\build_bridge.bat
  ```
  会自动从仓库根目录下的 `Insta360SDK_Source/`（同样不进 git，需要自己放好解压后的
  SDK）读取头文件/库，编译输出到 `Assets/Plugins/x86_64/Insta360UnityBridge.dll`。

### Insta360 Camera SDK + Media SDK（Linux，Husky 车载电脑用）

放到仓库根目录 `LinuxSDK_Source/`（`CameraSDK-2.1.1-Linux.tar.gz`、
`libMediaSDK-dev-3.1.1.0-amd64.tar.xz`），然后用
[`Deployment/HuskyInsta360/prepare_sdk.ps1`](Deployment/HuskyInsta360/prepare_sdk.ps1)
打包进 Docker 构建上下文。详见
[`Deployment/HuskyInsta360/README.md`](Deployment/HuskyInsta360/README.md)。

### KAT VR Native SDK

放到 `Assets/KAT/SDK/Plugin/`：
- `Win64/KATSDKWarpper.dll`、`Win64/WalkerBase.dll`、`Win64/WalkerBase_2B.dll`
- `Android/NexusClient-release.aar`

从 KAT VR 官方开发者渠道获取，具体接口见 [`Assets/KAT/SDK/KATNativeSDK.cs`](Assets/KAT/SDK/KATNativeSDK.cs)。

## Git LFS

原生插件二进制（`*.dll`/`*.so`/`*.lib`/`*.a`）默认通过 Git LFS 追踪（见
`.gitattributes`），但按上面这节的说明，实际会提交进仓库的原生二进制目前几乎没有
——真正体积大的那批已经被排除在版本控制之外了。clone 本仓库后需要先执行一次
`git lfs install`（多数客户端装了 Git LFS 后会自动配置好 hook）。

## 2026-10-15 分支更新说明

本次将当前工作区的代码、配置、测试和说明整理到
`feature/husky-navigation-panorama-20261015` 分支。主要变更：

- **KAT / Husky 控制**：增加当前方向追踪模式、可选的朝向减速辅助、反馈积压与源时间戳重置检测，以及故障后的显式重新激活逻辑；加入 SLAM 航向修正，抑制轮式里程计转向漂移。具体行为和测试方法见 [DirectAngle 测试说明](Assets/KAT/DirectAngle-测试说明.md)。
- **全景画面与朝向**：统一全景视频、车身框、目标箭头和手腕地图的航向换算；增加航向历史插值、视频延迟补偿、激活对齐与标定支持。同步保存当前 `MainScene` 配置。
- **Livoscan 小地图**：新增 HTTP / WebSocket 数据源、点云与物体显示、手腕小地图、材质和着色器；接口背景见 [XR 小地图需求](Assets/Livoscan/XR_MINIMAP_REQUIREMENTS.md)。该文件是历史需求记录，验收条目不代表本次已完成实机验收。
- **采集与诊断**：记录控制输入、请求、实际发布、底盘反馈及视图状态；增加反馈源探测、传输路径比较、底盘响应审计、视图回放和 SLAM 漂移分析工具。使用方法见 [采集器说明](Assets/KAT/Editor/KatPlayCapture.README.md)、[反馈传输审计](SupportPackage/FeedbackTransportAudit/README.md)、[底盘响应验收](SupportPackage/HuskyResponseAudit/README.md) 和 [漂移分析](Tools/slam_drift/README.md)。
- **Insta360 部署**：拼接和推流分辨率更新为 2880 × 1440、30 fps；保持 FlowState 开启，通过 `INSTA360_GYRO_ZERO_AXES=z` 配置屏蔽陀螺仪 z 轴输入，并增加轴向诊断输出。

### 本次验证

在本机使用 .NET 10 和 Python 3.12 执行：

| 检查 | 结果 |
|---|---|
| `dotnet run --no-restore --project SupportPackage/KatDirectAngleRegression/Tests.csproj` | 95 项离线仿真回归通过 |
| `dotnet run --no-restore --project SupportPackage/KatMiniSCapture/Tests.csproj` | 采集与源探测生命周期检查通过；有 2 条测试替身未使用事件警告 |
| `python SupportPackage/FeedbackTransportAudit/test_compare_feedback_paths.py` | 8 项通过 |
| `python SupportPackage/FeedbackTransportAudit/test_ros_feedback_probe.py` | 9 项通过 |
| `python Tools/test_ros_source_probe.py` | 6 项通过 |

本机已有还原缓存，因此 C# 检查使用 `--no-restore`；首次克隆时先运行
`dotnet restore` 并指定相应项目，或直接运行不带 `--no-restore` 的命令。
上述结果覆盖离线逻辑、合成采集数据与本机回环通信；本次未运行 Unity / Quest
实机验收，也未编译部署车载 C++ 服务或验证真实机器人运动。

### 提交范围与本地数据

本次不上传 `KATCaptures/`、ROS `.bag`、原始诊断视频、采集日志、审计输出、
Unity 恢复备份及编译产物；文件仍保留在本地，新增忽略规则防止以后误提交。
既有历史提交中的文件不在此次清理范围内。部分诊断脚本需要自行提供原始采集输入。

[转角实验室](SupportPackage/KatAngleLab/README.md) 保留页面与服务源码；
`dist/catalog.json` 和 `dist/traces/` 是生成的仿真数据，不纳入此次提交。
首次运行前按该目录 README 的 `--export-site` 命令生成数据，以确保结果对应当前控制器。
