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
