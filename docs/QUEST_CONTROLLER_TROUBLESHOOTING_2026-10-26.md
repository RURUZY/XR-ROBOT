# Quest 摘下休眠、SteamVR Home 与手柄掉线记录

日期：2026-10-26（UTC）。接续 [Air Link 配对故障记录](QUEST_AIR_LINK_TROUBLESHOOTING.md)。本记录区分三个现象，不将它们统一归因为证书或网络问题。

## 当前结论

手柄曾短暂连接后同时反复掉线，Quest 自带主界面也无法稳定使用。头显日志显示运动样本 `IMU corrupt`、跟踪状态在 `POSITION`/`ORIENTATION`/`NONE` 之间反复变化，随后连接断开；PC Meta 与 SteamVR 均同步看到掉线。这里的错误标签不能直接解释为传感器硬件损坏，也不是手柄从未配对。

撤销临时模拟佩戴后，用户曾反馈一只恢复，但随后两只再次掉线；对异常手柄取出电池再装回也未解决。正常重启头显后，用户确认两只在 Quest 自带主界面稳定正常；新采集没有再出现 `IMU corrupt`，两只均有 `CONNECTED_ACTIVE tracking: POSITION`。这支持头显端输入/追踪运行状态异常的判断，但不能确定最初触发原因。

截至本节首次记录：用户报告 Quest 主界面恢复；新日志支持跟踪恢复。后续核对发现，重启后 Air Link 也已自动或由用户重新进入，因此本次不能视作严格隔离电脑端的完整测试。Air Link 内持续操作及正常自动休眠时间调整仍等待用户验证。没有重置手柄配对或恢复出厂设置。

## 1. 摘下约 15 秒后休眠：已确认的独立原因

实际读取到：

| 层级 | 本次有效状态 | 解释 |
|---|---|---|
| Windows | 当前电源方案的交流/直流 `STANDBYIDLE=0` | 电脑自动睡眠已关闭 |
| Oculus Debug Tool | `Bypass Proximity Sensor Check=On` | 检查时已经开启，本轮未修改此项 |
| Android 通用电源 | `screen_off_timeout=86400000`，`mSleepTimeoutSetting=-1` | 不能据此声称 Quest 自身休眠已关闭 |
| Quest `vrpowermanager` | `AutoSleepTime: 15000 ms`、`isAutosleepDisabled: false` | 头显的佩戴休眠仍为 15 秒 |

头显原始日志时间 20:43:50.456 出现 `PROX_OFF`，并由 `HEADSET_MOUNTED` 转为 `HEADSET_UNMOUNTED`；20:44:05.470 调用 `goToSleep()`，20:44:05.695 进入 `STANDBY`。前面 20:40:48.709 → 20:41:03.721 也有同样约 15 秒的过程。原始头显日志按当时本地时间保留，不与 PC 时间差计算延迟。

### 临时保持唤醒的实施与撤销

根据 [Meta 开发文档](https://developers.meta.com/vr/documentation/unity/meta-xr-operator/quest/)，曾执行：

```text
adb -s <本次 Quest 序列号> shell am broadcast -a com.oculus.vrpowermanager.prox_close
```

执行后确认 `Virtual proximity state: CLOSE`、`MountWakeLock count: 1`、`State: HEADSET_MOUNTED`。这确实改变了头显运行状态，但当时的摘下后无线持续连接验收被后续 SteamVR Home/手柄问题打断，不能记为已通过。

在手柄诊断中，于 PC 03:33:32 UTC 附近执行并验证撤销：

```text
adb -s <本次 Quest 序列号> shell am broadcast -a com.oculus.vrpowermanager.automation_disable
```

结果为 `Virtual proximity state: DISABLED`、`MountWakeLock count: 0`，恢复真实佩戴检测。之后的头显重启未重新开启模拟佩戴。仅撤销不足以解决全部手柄异常；因此既不能确认该设置是根因，也不能把它作为已验证可靠的长期方案。

后续采用头显正常“设置 → 电源 → 自动休眠”的较长时间设置（本轮建议 15 分钟或界面提供的较长值），以减轻短暂摘下导致的断线。该选项在不同版本中可能位于“系统”或“常规”内。修改后的实际值及摘下验收应另记，不能把建议值写成已经生效。[Meta 自动休眠说明](https://developers.meta.com/vr/resources/vrc-quest-functional-2/)

## 2. SteamVR Home 占据画面

- 当时运行 `steamtours.exe`；SteamVR 界面明确显示正在运行 SteamVR Home。`vrstartup.txt` 原始时间 20:46:32.325 记录 `Attempting to start home app openvr.tool.steamvr_environments`。
- 系统活动 OpenXR 路径仍为 `D:\Meta Horizon\Support\oculus-runtime\oculus_openxr_64.json`。看到 SteamVR Home 不代表活动 OpenXR 已被切到 SteamVR。
- KAT 的 SteamVR/Oculus 处理程序均在运行，但没有充分证据认定是哪一个程序触发了所有 SteamVR 重启。不能将其写成已确认根因。
- 曾向 SteamVR 窗口发送正常退出操作，随后 `steamtours` 退出；后续 SteamVR 进程再次出现，因此不能把一次退出当作永久解决。
- 03:37:22 UTC，备份 `D:\Sofeware\steam\steam\config\steamvr.vrsettings` 后，明确设置 `steamvr.enableHomeApp=false` 并读回验证。原用户配置未覆盖该字段，安装目录 `resources\settings\default.vrsettings` 第 71 行默认是 `true`。未禁用整个 SteamVR、未修改 KAT 配置或 Unity XR 配置。

备份：`%LOCALAPPDATA%\Temp\QuestControllerRecheck_20261026\steamvr-before-disable-home-20261026T033722Z.vrsettings`。需要恢复时优先只恢复该字段至原来的默认行为；不要用整份旧备份覆盖后来其他设置。

## 3. 手柄掉线的证据与操作

### 同次证据

| 来源 | 关键观测 | 边界 |
|---|---|---|
| 用户反馈 | Quest 主界面也异常；按键/移动后短暂出现，几秒后消失 | 并非只在 SteamVR 中缺少控制器模型 |
| PC `Service_2026-10-25_20.37.47.txt` | 左手 21:29:50 接入、21:29:52 掉线；21:29:58 再接入、21:30:04 掉线。右手 21:31:56 接入、21:32:04 掉线 | 原始 PC 时间为 UTC−06:00；连接时两侧曾报告 100%，掉线后的 `-2` 不应当作负电量或实测耗尽 |
| Steam `logs\vrserver.txt` | 同期 Oculus 驱动报告左右控制器 connected/disconnected | 与 Meta 层一致，不能仅通过重置 SteamVR 按键绑定解决这个证据所示问题 |
| 头显 `SyncBossHAL` / `ControllerManagement` | 两侧均出现 `IMU corrupt`、tracking 状态反复丢失及 `handleDeviceDisconnected` | 尚不能区分传输、固件或运行状态的具体触发；未认定硬件损坏 |
| 撤销模拟佩戴后的采集 | 原始 21:33 分钟内左手仍有 41 条 `IMU corrupt` | 这是对应保存文件中的行数，不是完整系统错误率；撤销后并未立即完全恢复 |
| 正常重启后的采集 | `ImuCorruptLines=0`；左右均有 `CONNECTED_ACTIVE tracking: POSITION` | 仅针对本次保存窗口，不代表永久无故障 |

日志也出现过 `update-required` 状态文本和固件版本解析警告。这不足以证明有正在失败的固件升级，本轮未据此执行升级或刷写。

### 实际处理顺序

1. 保存 PC、SteamVR 和头显日志，确认用户在 Quest 主界面也复现。
2. 撤销本轮新增的临时模拟佩戴，并验证真实佩戴检测已恢复。用户先反馈一只恢复，随后两只再次掉线。
3. 请用户对仍异常的手柄取出电池至少 30 秒再装回，用户反馈两只仍掉线。
4. 保存重启前日志；03:35:25.021 UTC 对已确认的 Quest 3 执行普通 `adb reboot`，未删除配对或恢复出厂设置。
5. 要求先不进入 Air Link/SteamVR，在 Quest 主界面连续操作两只手柄约 1 分钟。用户确认两只稳定正常；03:36:46 UTC 保存重启后的头显日志，没有发现新的 `IMU corrupt`。
6. 请求用户调整正常自动休眠时间、拔掉诊断 USB，再进入 Air Link 操作两只手柄约 1 分钟。该步骤的结果在取得证据后追加。

03:40 UTC 补充核对：头显重启后局域网地址从 `192.168.2.17` 变为 `192.168.2.29`。PC 原始日志 21:35:54.681（第 44466 行）已启动 streaming，会话为无线模式（第 44523 行），21:35:55.255 有 `firstPresent`（第 44691 行）；复核时至新地址的 TCP 连接仍建立。初查使用旧地址筛选曾漏掉新连接，后已更正。既然这段串流在用户反馈主界面测试之前已开始，不能宣称完成了严格的“未进入 Air Link”隔离实验。用户反馈和头显新采集仍支持当时手柄恢复，但与电脑端的因果隔离证据不足。

03:39 UTC 读回 `steamvr.enableHomeApp=false`，且 `steamtours.exe` 没有运行。

### 原始证据位置

`%LOCALAPPDATA%\Temp\QuestControllerRecheck_20261026\` 中保存：

- `pc-controller-events.txt`、`steam-controller-events.txt`：电脑端掉线摘录。
- `headset-controller-before.txt`、`headset-controller-after-restore.txt`：恢复真实佩戴检测前后的头显日志。
- `restore-proximity-result.txt`、`vrpower-before-restore.txt`、`vrpower-after-restore.txt`：撤销操作及读回状态。
- `headset-before-reboot.txt`、`vrpower-before-reboot.txt`、`reboot-request.json`：正常重启前证据。
- `headset-after-reboot.txt`、`vrpower-after-reboot.txt`：重启后验证。

更早的头显休眠证据位于 `%LOCALAPPDATA%\Temp\QuestKeepAwake_20261026\`，包括 `headset-power-before.txt`、`vrpower-before.txt`、`enable-result.txt`、`vrpower-after-enable.txt`。必要结论已摘录在本文件，避免只依赖临时目录。

## 验收与未确认项

| 检查 | 状态 |
|---|---|
| Quest 主界面两只手柄约 1 分钟操作 | 用户确认通过；新采集支持跟踪恢复 |
| 模拟佩戴撤销 | 已读回 `DISABLED`、唤醒锁计数为 0 |
| SteamVR Home 自动启动配置 | 已设为 false 并读回；无线串流恢复后 Home 进程没有运行 |
| Air Link 无线串流/首帧 | 新地址 `192.168.2.29` 上已确认；不是手柄持续稳定的替代证据 |
| Air Link 内手柄持续操作 | 待用户复测；本地主界面测试未严格隔离电脑连接 |
| 正常休眠时间调长、摘下后保持连接 | 待用户设置及实测 |
| Unity Play、KAT/机器人联合功能 | 本轮未验收 |
| 手柄异常最初触发原因 | 未确认；没有永久修复或硬件损坏结论 |

Unity 代码、场景、KAT 配置、网络和防火墙均未在本轮更改。下次先保存掉线同次日志；若 Quest 主界面正常而 PC VR 异常，再检查 Link/SteamVR 路径，避免直接重置手柄配对。
