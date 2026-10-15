# KAT × Husky 转角实验室

网页展示实际 `Assets/KAT/KatHuskyDirectAngleController.cs` 编译运行后的离线仿真结果，没有第二套 JavaScript 控制器。

本仓库仅提交页面与服务源码，生成的 `dist/catalog.json`、`dist/traces/` 不入库。
首次克隆或控制器更新后，先执行下方 `--export-site` 命令，再启动本地服务。

目前提供 13 个场景 × 3 组滤波 × 3 组底盘响应，共 117 组 120 秒记录。支持场景切换、参数组合比较、累计角曲线、角速度曲线、时间回放和 CSV 导出。网站中的参数是离线预设，不能任意连续调整；新增参数组合请修改导出器后重新生成。

从 Unity 项目根目录运行：

```powershell
dotnet run --project SupportPackage/KatDirectAngleRegression/Tests.csproj
dotnet run --project SupportPackage/KatDirectAngleRegression/Tests.csproj -- --export-site SupportPackage/KatAngleLab/dist
node SupportPackage/KatAngleLab/serve.mjs
```

本地打开 http://127.0.0.1:8766 。重新生成后刷新页面即可。目录内 `dist` 是可独立静态托管的网站，包含对应生成时的结果；修改控制器不会自动更新已发布网站。

模型以 50 Hz 调用真实控制器，`cmd_vel` 按控制器的发布逻辑保持。底盘用一阶角速度响应、指令增益和反馈延迟模拟。网页曲线每 0.1 秒保存一次，事件按发生时刻保存。最大指令指标应读取全速采样统计，不能仅从绘图降采样推断。

短异常保持参考，长异常和无进展会丢弃目标。观察“身体要求 − 真实转角”和“控制器剩余误差”两项：目标被取消后后者可能为零，但前者仍很大。

这是控制逻辑仿真，未运行 Unity 引擎、KAT 驱动或 ROS 网络。不会连接机器人；也不能验证轮胎接地、地面滑移、静摩擦与真实传感器误差。后续可将同一套输入和验收指标接入 Gazebo/Unity 物理模型及 rosbag 回放。

`catalog.json` 记录控制器 SHA-256 与生成时间，可核对网页结果是否对应当前代码。场景中身体输入已经转到 ROS 正方向；实车仍使用 `bodyToRosYawSign=-1`，仿真设置 +1 仅用于直接输入 ROS 角度。
