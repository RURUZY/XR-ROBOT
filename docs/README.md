# 文档索引

整理日期：2026-10-26。本页列出仓库里的全部说明文档，并标明哪些是当前有效的说明，哪些是历史记录。

文件没有移动位置。`Assets/` 下的 `.md` 带有 Unity `.meta`，采集工具也按固定路径复制 README，移动这些文件会导致链接和工具路径失效。

状态说明：**现行**表示描述当前代码或流程；**日志**表示按日期追加的工作记录，最新一节在最上面，旧节可能已经被后续改动推翻；**历史**表示只反映当时的状态。

## 先读这些

| 文档 | 状态 | 内容 |
|---|---|---|
| [项目现状与工作交接（2026-10-24）](PROJECT_STATUS_2026-10-24.md) | 现行 | 已做的工作、测试结果、当前配置、待办和注意事项 |
| [../README.md](../README.md) | 现行 | 项目简介、第三方 SDK 放置位置、Git LFS、10-15 分支说明 |
| [../AGENTS.md](../AGENTS.md) | 现行 | 协作规则摘要 |
| [工程协作与跨项目对接流程](ENGINEERING_WORKFLOW.md) | 现行（§8 基线为 10-21） | 诊断、实施、验证分层；与 `livoscan-mywork` 的接口、坐标和时间字段 |
| [录制清单与问题记录](RECORDING_CHECKLIST_AND_ISSUE_LOG.md) | 现行 + 日志（10-29 起） | 每次实车测试前要录哪些 bag 和 Unity 数据；按日期追加的未解决问题 |

## KAT 控制

| 文档 | 状态 | 内容 |
|---|---|---|
| [DirectAngle 测试说明](../Assets/KAT/DirectAngle-测试说明.md) | 日志（当前控制器的主记录） | DirectAngle 每轮改动、参数、回归和现场检查方法 |
| [KAT 工作记录](../Assets/KAT/README.md) | 历史（08-10~09-13） | 早期闭环/开环控制器和待办；场景挂载状态以现状文档为准 |
| [大转弯追赶测试](../SupportPackage/KatDirectAngleRegression/TurnAudit-结果.md) | 历史（09-14） | 离线大转弯追赶结果 |
| [转角实验室](../SupportPackage/KatAngleLab/README.md) | 现行 | 网页仿真；数据需要先用 `--export-site` 生成 |

## 采集与诊断

| 文档 | 状态 | 内容 |
|---|---|---|
| [Quest Air Link 连接故障记录](QUEST_AIR_LINK_TROUBLESHOOTING.md) | 日志（10-25～10-26） | 首次证书失配、USB 会话交接阻塞；复发时证书未变，重新配对后恢复，触发原因待确认 |
| [Quest 休眠、SteamVR Home 与手柄掉线](QUEST_CONTROLLER_TROUBLESHOOTING_2026-10-26.md) | 日志（10-26） | 15 秒摘下休眠、临时模拟佩戴已撤销、Home 自动启动关闭；手柄重启恢复与验收边界 |
| [KAT Play 采集说明](../Assets/KAT/Editor/KatPlayCapture.README.md) | 现行（采集包实际复制的版本） | `KATCaptures/` 中各 CSV 的字段和时间定义 |
| [KatMiniSCapture/README.md](../SupportPackage/KatMiniSCapture/README.md) | 旧副本 | 缺少 v2 控制链字段，以上一行为准 |
| [反馈传输审计](../SupportPackage/FeedbackTransportAudit/README.md) | 现行 | 只读反馈探针、双路径对比 |
| [反馈传输审计结论 10-13](../SupportPackage/FeedbackTransportAudit/findings_20261013.md) | 历史 | 反馈积压排查结果 |
| [Husky 底盘响应验收](../SupportPackage/HuskyResponseAudit/README.md) | 现行 | 实车分层验收步骤 |
| [SLAM 漂移分析](../Tools/slam_drift/README.md) | 现行 | 闭环首尾漂移工具 |
| [倒车画面左漂](../SupportPackage/ReverseDrift_2026-09-10.md) | 历史（09-10） | 轮编码器导致的虚假偏航；录包方法 |
| [新录包分析](../SupportPackage/NewBag_Findings_2026-09-11.md) | 历史（09-11） | 对应的 bag 分析 |

`SupportPackage/` 下的 `*Audit_2026xxxx/`、`TurnDiagnostics/`、`UnityRecovery_*` 是当时的原始证据和备份，保留不动。

## 全景视频与部署

| 文档 | 状态 | 内容 |
|---|---|---|
| [HuskyInsta360 README](../Deployment/HuskyInsta360/README.md) | 现行（简要） | 准备 SDK、在 Husky 上构建、测试相机 |
| [部署说明：相机采集与 360 拼接](../Deployment/HuskyInsta360/部署说明-相机采集与360拼接.md) | 历史（08-15） | 硬件信息和部署步骤；"下一阶段"中的推流部分其实已经完成 |
| [Insta360 联调记录](../Insta360-联调记录-2026-08-14.md) | 历史（08-14~08-15） | 整条链路首次联调 |

当前相机参数以 `Deployment/HuskyInsta360/docker-compose.yml` 为准：FlowState 开启，`INSTA360_GYRO_ZERO_AXES: "z"`，2880×1440、30 fps。

## Livoscan 小地图

| 文档 | 状态 | 内容 |
|---|---|---|
| [XR 小地图需求](../Assets/Livoscan/XR_MINIMAP_REQUIREMENTS.md) | 历史需求（10-09） | 手腕小地图需求和验收条目；验收尚未完成 |
| 对端 `livoscan-mywork/docs/xr-interface.md`、`docs/xr-unity/SYNC.md` | 对端现行 | `livoscan.xr.v0` 协议、`heading_t` 等字段；不在本仓库 |

## 不在索引里的文件

`KATCaptures/*/README.md` 是每次采集自动复制的说明副本，不在 git 中，不需要单独维护。

## 维护约定

- 改了控制逻辑：在 DirectAngle 测试说明最上面追加一节，写明日期、证据、改动、回归项数，以及是否做过实车验证。
- 某条旧说明被推翻时，在原处加一行"后续更正"并指向新位置，不删除原文。
- 每发现一个新问题，或一个问题有了新证据：在"录制清单与问题记录"第二部分最上面追加或更新一节；录制清单有变化（新 topic、新 CSV）时同步改第一部分。
- 阶段性交接时，新建一份 `PROJECT_STATUS_日期.md`，并更新本页"先读这些"。
