# Quest Air Link 连接故障记录

更新日期：2026-10-26（UTC）。用于复发时接续诊断；后续案例在本文件追加，保留既有证据。遵循 [工程协作流程](ENGINEERING_WORKFLOW.md)。

后续补充：已单独确认头显自身的 15 秒摘下休眠；临时模拟佩戴已撤销。SteamVR Home 自动启动与手柄反复掉线另见 [10-26 头显休眠及手柄记录](QUEST_CONTROLLER_TROUBLESHOOTING_2026-10-26.md)，不要将这些现象套用为配对证书错误。

## 2026-10-26 复发：灰按钮，电脑证书未变，重新配对后恢复

**结果：本次再次恢复无线 VR 画面。新的失败原因没有获得足够证据确认，不能直接沿用前次“证书指纹不匹配”的结论。** 用户报告未插 USB、能看到 `LAPTOP-EAOIKFF1`，按钮灰色。检查时电脑运行时已于 02:37:47 UTC 重启，监听正常；本轮诊断没有再次重启或结束电脑运行时。

### 事实与证据

本节时间统一使用 2026-10-26 UTC。原始 PC 文件名和行内时间仍为主机 UTC−06:00：`Service_2026-10-25_20.37.47.txt`，位于 `%LOCALAPPDATA%\Oculus\`。行号对应本次采集版本。

| 时间（UTC）/项目 | 实际观测 | 依据与判断边界 |
|---|---|---|
| 02:37:47.807 | 运行时版本仍为 `208.0.0.84.535`，PID `36296` | 日志第 1 行；这是本轮读取前的启动，不是本轮实施的修复 |
| 02:37:49.302 | `Validating Cert and Key retrieved from store...` | 第 346 行；未看到本次启动重新生成证书 |
| 失败时及 02:41:16.847 复核 | `cert.pem` DER SHA-256/Base64 均为 `uUcXV4hgJ3MyGYsDIZ64dWslgPcRNhFT1MWniY7Pnrc=`；修改时间仍为 `2026-10-25T21:21:14.3487471Z` | 与前次成功基线相同；不能用“电脑这次又换了证书”解释复发，但这并不直接证明头显保存的身份正确 |
| 初查 | PID `36296` 监听 `1855/1856/1883/1886/1887`；未发现新无线请求或会话交接阻塞 | 端口状态与本次 PC 日志；先定位在发现/可用性/配对状态范围，暂未确定具体断点 |
| 02:37:49.986 | V3 mDNS 查询报 `Status: 123`、`Error Code: 1460`、`mDNS discovery is stalled` | 第 915–916 行；这是候选线索，不是已确认根因。后续查询返回 `status=0`，V2 服务注册成功，最终也能连通 |
| 02:40:26.422–27.718 | TLS 握手完成、无线 basic 邀请被接受、`Valid pairing value received` | 第 2051、2062、2112 行；刷新后请求已到电脑，不支持此时仍完全网络不可达 |
| 02:40:31.391–34.012 | `PC pairing status: accepted` → `PAIRING_ACK` → basic `Connected` | 第 2236、2246、2251 行 |
| 02:40:36.116–36.664 | streaming `Connected`、`New session started in wireless mode: 1`、`InVRMode set to true` | 第 2722、2880、3072 行 |
| 02:40:45.159 | `firstPresent`：`Send out first displayed frame` | 第 3708 行；用户明确确认已进入电脑 VR 画面 |
| 恢复后复核 | `192.168.2.4:1855` 与 `192.168.2.17` 的 TCP 为 `ESTABLISHED`；ADB 列表为空 | 确认无线会话；本轮没有取得头显失败日志，不能断言出现了新的 `pairing_invalid_cert` |

### 实际操作与结果

1. 对照前次记录读取 PC 日志、进程/监听、网络路由及公开证书摘要，并只读查看 Meta 界面。
2. 请用户保持 USB 拔出，将头显“使用 Air Link”关闭再打开，退出 Link 面板后重新进入并选择电脑。用户先反馈仍灰色，随后日志出现新的配对码确认和无线串流成功；不能把“第一次反馈仍灰”省略成一次操作立即解决。
3. 原本请求 USB 采集头显日志，在发现新的配对成功后撤回该步骤。最终 ADB 无设备，未执行头显配置修改。用户确认已进入电脑 VR 画面。
4. 电脑证书、防火墙、网卡配置、Unity 代码和场景均未由本轮诊断更改。只新增临时诊断文本，并更新本记录及索引。

本次可确认的是**重新配对后恢复**。灰按钮是否来自头显配对/发现状态未刷新、短暂 mDNS 异常或其他原因仍待确认。当前 Radmin 网卡和路由已存在，与前次“旧状态文件记录禁用”不同，但没有因果验证，本轮未禁用 VPN。不要把此差异直接写成故障原因。

下一次最小诊断仍是：在修改配对状态之前，保存同次头显 `xrspd`/Link 面板状态和 PC 日志，核对实际解析到的地址、端口、可达状态、保存的配对状态及认证错误；电脑证书同时与成功基线比较。本次未进行 3 次连续重连或 15 分钟持续使用验收，未验证 Unity/机器人联合功能。

诊断文件位于 `%LOCALAPPDATA%\Temp\QuestAirLinkRecheck_20261026\`：`before-recovery.json`、`pc-before-recovery.txt`、`pc-recovery-evidence.txt`、`after-recovery.json`。前两者是初查结果的事后保存，文件名不代表采集时间一定早于实际恢复；事件先后以原始日志为准。关键证据已经摘录在本节，避免只依赖临时文件。

## 2026-10-25 案例（历史）

### 当次结论

**已确认的直接原因是 Air Link 配对证书指纹不一致，头显拒绝建立连接。** 头显仍显示已配对，但基本连接没有建立，所以能看到电脑，启动按钮却不可用。当天两次采集都出现 `Certificate fingerprints do not match` 和 `pairing_invalid_cert (34)`；不能再把这次现象笼统记成“网络不好”或“重启即可”。

**最初是什么操作使证书或保存的配对身份失配，尚未确认。** 电脑当天确实生成过新证书，但第一次证书错误早于这次生成，不能用它解释全部故障，也不能据此断言每次重启都会换证书。用户反馈发生过多次；目前日志能直接证明的是本日两次相同认证错误，不能追认所有历史失败都是同一触发原因。

排查期间还出现第二个阻塞：为读取头显日志接入 USB 后，切换 Air Link 使 USB 会话占用连接；电脑在 USB→无线交接时等待旧会话结束，卡住后连应用内重启也未完成。这是诊断期间新增的问题，不能解释最初未接 USB 时的失败。

最终通过重新配对、拔掉诊断 USB、重启卡住的运行时并确认新配对码恢复。用户确认已进入电脑 VR 画面；电脑日志也有无线串流启动和首帧记录。**本次连接恢复通过；复发诱因及长期稳定性仍未验证。**

### 当次环境与链路

| 项目 | 本次观测 |
|---|---|
| 设备与方式 | Quest 3；无线 Air Link；USB 仅在排查中接入采集日志，最终验收时已拔出 |
| 电脑 | Windows，`LAPTOP-EAOIKFF1`；RTX 4060 Laptop GPU |
| Meta 运行时 | `208.0.0.84.535` / Stable Runtime `1.208.0`；安装根目录 `D:\Meta Horizon` |
| 头显系统 | OS build `52433670048800520` |
| 网络 | 电脑 `LABWORK-6G`、`192.168.2.4/24`；头显 `LABWORK-5G`、`192.168.2.17/24`；这是当次地址，不是固定配置 |
| 配对端口 | 重启前 TCP `3718`，重启后 `9495`；服务动态分配，不应固定写成防火墙诊断依据 |
| OpenXR | 当次活动运行时为 `D:\Meta Horizon\Support\oculus-runtime\oculus_openxr_64.json` |
| 仓库基线 | HEAD `3046a42`，已有多处未提交修改；本次故障发生在 Meta 配对/会话层，未修改 Unity 功能 |

本次观测链路：头显发现 `_oculusal_sp_v2._tcp.local` 服务 → 解析到电脑地址 → TCP/TLS → 校验配对身份 → basic 会话 → streaming 会话 → VR 画面。依据为下文 E1、E2 的头显 `xrspd`/`PCLinkService` 日志，以及 E4、E5 的 PC `Pairing`、`SessionStateMachine`、`ActiveSessionControl` 日志。

看到电脑只证明发现阶段成功；`PAIRED` 只表示保存着配对状态。本次 `PAIRED` 与 `BasicSessionConnected: false` 同时存在，不能把前者当成已连通。错误发生在画面串流和 Unity 应用启动之前。

### 证据与时间线

下面保留的是原始日志中的时间和关键字段，省略无关遥测标识。PC 当日时区偏移为 UTC−06:00，其日志时间加 6 小时为 UTC；头显时间按原文保留，未独立验证双端时钟同步，不用它们的差值计算网络延迟。PC 服务会清理旧日志，因此将必要摘录保存于本文件，不只依赖临时目录。

| 编号 | 原始时间（2026-10-25） | 观测及原文摘录 | 来源定位 |
|---|---|---|---|
| E1 | 头显 15:11:15.464 | `Certificate fingerprints do not match, returning`；`REASON_PAIRING_ERROR_INVALID_CERT`；随后 `prepareInviteRequest failed for 'basic': pairing_invalid_cert (34)` | 旧 `xrspd-log.txt` 第 501、511、527 行 |
| E2a | 头显 16:05:20.296–.297 | `Already Paired with:LZY::LAPTOP-EAOIKFF1`；`mStatus=PAIRED`，`BasicSessionConnected: false` | `headset-repair-attempt.txt` 第 37、39 行 |
| E2b | 头显 16:05:39.537–.541 | 再次 `Certificate fingerprints do not match, returning`；再次 `pairing_invalid_cert (34)` | 同文件第 1316、1345 行；这是本轮新采集，不是仅引用 E1 |
| E2c | 头显 16:05:41.685–43.303 | `Pairing state init: Unpaired, pairedServiceName: []` → `Pair Enabled?true` → `PAIR_ACTION` | 同文件第 1561、1613–1615 行；旧配对被清除后可重新配对 |
| E3 | PC 15:21:14.352（21:21:14.352 UTC） | `GetAirLinkPairingPcCertAndKey`：`Successfully generated PC Certificate after 0 attempts` | `Service_2026-10-25_15.21.12.txt` 第 347 行 |
| E4a | PC 16:05:49.864 | `guardSingleSessionInvite() - rejected: hasActiveConnection=1, inviteInProgress=0` | `Service_2026-10-25_15.57.43.txt` 第 2663 行 |
| E4b | PC 16:05:49.864 | `Same-device transport handoff`、`old_detector=2 new_detector=0`、`synchronously terminating old session`；随后 `waitForActiveSessionDestroyed() - waiting...` | 同文件第 2664、2673 行；结合接线操作，定位 USB→无线交接阻塞 |
| E4c | PC 16:07:47.301 | 应用内重启后收到 `setShutdownStarted()`，但运行时未完成退出 | 同文件第 3284 行；随后现场核查仍为旧进程 |
| E5a | PC 16:08:48.397–53.449 | `PC pairing status: accepted` → `PAIRING_ACK` → basic `Connected` | `Service_2026-10-25_16.08.31.txt` 第 1999、2020、2025 行 |
| E5b | PC 16:08:55.732–56.125 | streaming `Connected`；`Session did start ... sessionType streaming`；`New session started in wireless mode: 1`；`InVRMode set to true` | 同文件第 2466、2565、2620、2798 行 |
| E5c | PC 16:09:06.787 | `firstPresent`：`Send out first displayed frame` | 同文件第 3520 行；用户随后确认进入电脑 VR 画面 |

#### 证书变化的证据边界

- E1 附近收到的证书指纹为 `SunNCrv6y1h8nNcr/FHZ/Fd0bnb3WmOgTy0VfVJYdWQ=`（旧 `xrspd-log.txt` 第 175 行）。E2b 前收到的指纹为 `uUcXV4hgJ3MyGYsDIZ64dWslgPcRNhFT1MWniY7Pnrc=`（新采集第 996 行）。两次提供的证书不同。
- 恢复后只读检查 `%LOCALAPPDATA%\Oculus\Airlink\cert.pem`：公钥证书 DER 的 SHA-256/Base64 为 `uUcXV4hgJ3MyGYsDIZ64dWslgPcRNhFT1MWniY7Pnrc=`，与 E2b 前收到的相同；文件 `LastWriteTimeUtc=2026-10-25T21:21:14.3487471Z`，与 E3 生成事件吻合。这里只保存公开证书摘要，未复制私钥。
- 因此，本轮恢复没有依靠再次替换这个证书；重新建立配对关系是有证据支持的恢复措施。15:21 的证书生成可能使更早保存的配对失效，但不足以解释 15:11 已出现的错误。触发生成的具体操作、初次失配的原因及 Meta 内部持久化行为仍待确认。

#### 已做的排除检查

| 检查 | 实际结果与边界 |
|---|---|
| 服务与发现 | `OVRService` 运行，头显解析到正确电脑地址；服务运行不能证明会话状态机正常 |
| Wi-Fi 与地址 | 当次两端在 `192.168.2.0/24`，头显信号约 −19 dBm、链路速率 1200 Mbps；未做长期丢包/拥塞测试 |
| 防火墙 | 检查到 Meta 相关规则允许相应程序、覆盖各配置文件，未发现适用的显式 Meta 阻止规则；不等于所有网络问题均已排除 |
| 认证错误 | 两次已走到明确的证书指纹拒绝，证据优先于泛泛的网络、显卡或 Unity 猜测 |
| 较早的 VPN 排查 | 旧目录有 Radmin 测试脚本；状态文件记载 21:23:29 UTC 为 `disabled_user_requested`、`autoRestore=false`。这只证明当时记录，不能认定 VPN 是根因。本次整理时网卡状态查询被 Windows 拒绝，未重新切换网卡 |

### 实际恢复操作

1. 通过 USB 和用户允许的 USB 调试读取头显日志；捕获 E2。用户在头显关闭、再打开“使用 Air Link”，日志随后进入 `UNPAIRED`，重新发起配对。
2. 发现 E4 的有线会话占用与交接等待后，保存日志并拔掉 USB。拔线后电脑当时的配对监听仍未恢复，不能仅凭拔线认定已解决。
3. 尝试 `Restart-Service OVRService -Force`，因 Windows 服务控制权限不足失败；该尝试不计作成功重启。
4. 使用 Meta 应用“设置 → 通用 → 重启 Meta Horizon Link”。服务收到关闭请求，但旧进程仍卡住。
5. 核验当时 PID `29576` 确为 `D:\Meta Horizon\Support\oculus-runtime\OVRServer_x64.exe` 后结束该卡住进程；现有服务自动启动新 PID `17908`，恢复监听。**这些 PID 仅为历史证据，复发时必须重新读取，不能照抄。**
6. 保持 USB 拔出，重新打开头显 Air Link，选择 `LAPTOP-EAOIKFF1`，在两端核对并确认新配对码，再点“启动”。得到 E5 和用户画面确认。

本轮 16 时复查与恢复未改 Unity 代码/场景、驱动、防火墙或网络配置；较早的 VPN 排查另见上表，不把“本轮未改”扩大成“当天从未改过”。本次文档整理仅新增本记录并更新文档索引。

### 再次发生时的最小诊断与处理顺序

先记录一次失败发生时间、按钮状态、是否接 USB，保存当次 PC 日志；读取公开证书摘要及文件修改时间，与上述成功后的基线比较。先留证据，再操作，避免多次重启覆盖最有价值的上下文。

| 新证据 | 下一步最小操作 | 成功或转向判据 |
|---|---|---|
| 新头显日志出现 `pairing_invalid_cert` / 指纹不匹配 | 重新建立该电脑的 Air Link 配对，核对新配对码；若用 USB 采集，保存后拔线再做无线验收 | 看到 `PAIRING_ACK` 和 basic `Connected`；仍错误则对比本次证书与保存状态，不能只重复重启 |
| `hasActiveConnection=1`，随后 `waitForActiveSessionDestroyed` 不返回 | 退出已有 Link、拔掉 USB，观察监听/会话是否恢复；仍卡住才重启 Meta 运行时 | 新进程正常监听并接受无线邀请；常规重启卡住时，重新核验进程名称、路径和 PID 后再处理该进程 |
| 证书摘要相对成功基线改变 | 对照相邻 PC 启动日志的 `GetAirLinkPairingPcCertAndKey`、更新时间及实际修复/安装操作 | 找到发生变化的具体时段与动作；修改时间本身不证明是谁更换了证书 |
| 证书没变但再次认证失败 | 保存头显 `xrspd` 当前错误和配对状态，核对实际连接的主机身份 | 继续查头显保存状态或配对同步，不能宣布电脑证书重新生成 |
| 没有上述错误，或已配对但无画面 | 沿发现 → TCP/TLS → 配对 → streaming → 首帧定位新的断点 | 将其记录为新证据分支，不强行套用本次原因 |

头显采集使用当时可用的 ADB，读取 `logcat -b all -d -v threadtime`；只读默认 logcat 缓冲区可能漏掉本次原生 `xrspd` 事件。ADB 路径、设备授权及连接状态需当次核对。接入诊断 USB 会改变 Link 可用传输，采集与最终无线验收应分开记录。

#### 验收状态

| 层级 | 判据 | 本次结果 |
|---|---|---|
| 无线配对 | 两端确认新配对码，basic `Connected` | 通过 |
| 无线画面 | streaming `Connected`、无线模式、首帧，用户实际看到电脑 VR | 通过；复查时无线 TCP 为 `ESTABLISHED` |
| 连续重连 | USB 拔出，退出后再连 3 次，每次无需重建配对即可进入 VR | 待执行；任一次认证失败、持续灰按钮或无法进入 VR 判失败 |
| 持续使用 | 无线连续使用 15 分钟，无连接中断或持续黑屏 | 待执行；这是后续建议验收窗口，不是已完成测试 |
| Unity/机器人联合功能 | 项目 Play、视频/位姿/控制及实机功能分别验收 | 本次未测试；Air Link 成功不替代这些结果 |

### 原始证据位置

以下路径是采集当时的位置，临时文件及 Meta 日志可能被系统清理；本文件中的关键摘录用于长期保留结论依据。

| 内容 | 本机路径 |
|---|---|
| E1 旧头显原生日志 | `%LOCALAPPDATA%\Temp\questairlinkdiagnostics_20261025\xrspd-log.txt` |
| E2 新头显采集 | `%LOCALAPPDATA%\Temp\QuestAirLinkRecheck_20261025\headset-repair-attempt.txt` |
| E3 PC 证书生成 | `%LOCALAPPDATA%\Oculus\Service_2026-10-25_15.21.12.txt` |
| E4 PC 会话阻塞 | `%LOCALAPPDATA%\Oculus\Service_2026-10-25_15.57.43.txt` |
| E5 PC 恢复 | `%LOCALAPPDATA%\Oculus\Service_2026-10-25_16.08.31.txt` |
| 本轮摘录与结果 | `%LOCALAPPDATA%\Temp\QuestAirLinkRecheck_20261025\pc-recovery-evidence.txt`、`pc-after-usb-unplug.txt`、`result.txt` |
| 较早 VPN 排查状态 | `%LOCALAPPDATA%\Temp\questairlinkdiagnostics_20261025\radmin-test-status.json` |

复发时追加：发生日期和两端版本、失败前操作、接线状态、原始日志时间、当前证书摘要、确切错误、采取的操作及分层验收结果。保留失败前证据；不删除证书/私钥来试错，也不把本次成功当成已经找到长期复发诱因。
