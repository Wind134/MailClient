# 双向同步（把服务器上的变化拉回本地）

之前的同步只会“按最大 UID 往后拉新邮件”，已经同步过的邮件在别处（网页、手机、别的客户端）被标已读 / 加星标 / 移动 / 删除，本地永远不知道。现在三类服务器都做了真正的双向同步：**已读与星标状态、移动、删除**。

> 说明：没有连接任何真实账号做过验证。所有同步逻辑通过“假服务器”（`FakeImapSource` / `FakeGraphSource`，无网络）单测；服务器能力（CONDSTORE / QRESYNC / IDLE）在运行时探测，QQ / 163 / 126 的真实能力需要用真实账号确认。

## 1. 每种服务器用什么策略

策略由**运行时探测到的服务器能力**加**本地已存的同步状态**决定，不按服务商名字硬编码。

| 服务器 | 策略（按优先级） |
|---|---|
| **Gmail**（IMAP + OAuth） | CONDSTORE（`FETCH … CHANGEDSINCE`）取标志变化；删除 / 移走靠“窗口内 UID 差集”；`X-GM-MSGID` / `X-GM-THRID` 去重；收件箱 IDLE。Gmail 据我所知支持 CONDSTORE 但**不支持** QRESYNC，所以不会用 QRESYNC（探测到才用）。 |
| **QQ / 163 / 126 / 其他 IMAP** | 运行时看 `CAPABILITY`：有 QRESYNC → QRESYNC；有 CONDSTORE → CHANGEDSINCE；都没有 → **窗口对账**（`FETCH minUid:* (UID FLAGS)` + 差集，最新 2000 封 ∪ 最近 30 天，5 分钟节流）。IDLE 同样运行时探测，不支持就只轮询。 |
| **Microsoft Graph**（Outlook / Hotmail） | 每个文件夹 `messages/delta`，保存 `@odata.deltaLink`；处理 `@removed`（删除 / 移走）与 isRead / flag 更新；410 `syncStateNotFound` → 丢 token 完整重同步并清理服务器上已不存在的邮件；文件夹列表每次同步也会清理已删除的文件夹。只轮询。 |

### IMAP 细节

- **每文件夹状态**（`folders` 表，迁移 V4）：`UidValidity`、`UidNext`、`HighestModSeq`、`LastReconcileAt`，`SyncCursor` 仍是“已拉到的最大 UID”。
- **QRESYNC**：连接时 `EnableQuickResyncAsync`；`SELECT (QRESYNC (uidvalidity modseq known-uids))`，服务器直接给出标志变化与 `VANISHED`（MailKit 的 `MessageFlagsChanged` / `MessagesVanished` 事件）。QRESYNC 失败会记日志并降级到 CONDSTORE。
- **CONDSTORE**：`HIGHESTMODSEQ` 变了才取 `CHANGEDSINCE`；`UIDNEXT` 变了才取新邮件；modseq 变化时顺带做一次窗口内 `UID SEARCH` 做差集找删除。有些服务器 EXPUNGE 不让 modseq 前进，所以每隔“对账间隔”（默认 5 分钟）或手动刷新时也会做一次差集。
- **窗口对账（无 CONDSTORE）**：只看窗口内（最新 N 封 ∪ 最近 D 天，按 UID 取“后缀”，一个 `minUid:*` 区间就够）。先在内存里对比，只写有差异的行。窗口外的老邮件不检查，所以窗口外的变化可能不同步（见“已知缺口”）。
- **保护**：服务器返回空列表但 `EXISTS > 0` 时，认为是异常响应，跳过本轮删除检测，避免清空本地。
- **UIDVALIDITY 变化**：服务器重建了邮箱，旧 UID 全部作废 → 清空该文件夹本地来自服务器的邮件，重新同步。指向旧 UID 的待写回操作被**丢弃并写日志**（否则会误操作新邮件）。
- **V4 之前的文件夹**（有游标、没有 modseq / UIDVALIDITY）：第一次同步走一次窗口对账，之后切换到 CONDSTORE / QRESYNC。
- **移动**：服务器上的移动在 IMAP 里等于“A 文件夹删除 + B 文件夹新增”，本地也是这样表现（未按 Message-ID 合并，见缺口）。我们自己发起的移动写回成功后，本地行已经在目标文件夹，不会被源文件夹的差集误删。

### Gmail 去重

Gmail 里同一封邮件会同时出现在“收件箱”和“所有邮件”（以及各个标签文件夹）。同步时记录每封邮件的 `X-GM-MSGID`（`messages.GmMsgId`）与 `X-GM-THRID`，同一账号里相同 `GmMsgId` 只保留一份：

> 有未写回操作的 > 已删除 > 垃圾邮件 > 草稿 > 收件箱 > 已发送 > 归档 > 其他

这样“所有邮件”里的副本不会产生重复行，也不会重复计入未读数。在网页上“归档”（去掉 Inbox 标签）会表现为收件箱里那封消失。

### Graph 细节

- 首次同步只要最近 90 天（`receivedDateTime ge`），之后只用 deltaLink。
- delta 通常直接返回完整邮件对象，已读 / 星标变化在里面；只带部分字段的更新也能处理。有变化就报“已变化”，界面随之刷新。
- Graph 的移动会换邮件 Id：源文件夹 `@removed`，目标文件夹是“新邮件”。

## 2. 冲突规则

一个原则：**本地操作在写回成功之前不被服务器覆盖；之后服务器为准。** 按字段（已读 / 星标 / 位置）分别判断。

某字段保持本地值，当且仅当：

1. 这封邮件上该字段有**待写回操作**（`pending_ops`），**或**
2. 该字段的**本地改动时间**（`ReadChangedAt` / `FlagChangedAt` / `LocationChangedAt`）**晚于这次服务器快照的取得时间** `snapshotAt`。

- `snapshotAt` 在**发出服务器请求之前**取，所以“请求在飞行中本地又改了”不会被这次结果覆盖。
- 本地改动时记录时间；写回确认（ack）时刷新一次。因此刚写回成功后，一个更早发出的陈旧快照不会把状态翻回去；之后新取得的快照才会生效（比如别处又改了）。
- 否则**服务器胜出**。
- 待写回的“删除”：直接跳过（邮件在本地已不存在）。
- 服务器上邮件被删除 / 移走：本地行与它的待写回操作一并删除（对已经不存在的邮件写回只会失败）。
- 本地刚移走、尚待写回（`LocalState=2`）的邮件不会被源文件夹的删除检测误删。

## 3. 触发方式与设置

- **IMAP IDLE**：每个账号对收件箱保持一个 IDLE 会话（MailKit `IdleAsync`），有变化立即只同步该账号的收件箱；每 29 分钟重新 IDLE；断开后指数退避重连（5 秒起，上限 15 分钟）；服务器不支持 IDLE 则停下，由轮询兜底。
- **定期轮询**：默认每 5 分钟同步所有账号的所有文件夹（其余文件夹、不支持 IDLE 的账号、Graph 都靠它）。Graph 没有适合桌面程序的推送（webhook 需要公网地址），只轮询。
- **暂停 / 恢复**：离线或系统挂起时暂停（不空转、不报错），网络恢复 / 唤醒后立刻补一轮。
- **刷新按钮**：立即做一轮强制增量同步（跳过对账节流，对账一次窗口）。
- **界面**：后台同步有变化后静默刷新——未读数、列表行、阅读窗格星标原地更新，不丢选中项和滚动位置；正在看的邮件被服务器删除时，选中清空。
- **设置**（`%LocalAppData%\MailClient\settings.json`，缺省用默认值）：

| 键 | 默认 | 含义 |
|---|---|---|
| `SyncPollMinutes` | 5（1–120） | 轮询间隔 |
| `SyncWindowMessages` | 2000（50–50000） | 无 CONDSTORE 的对账窗口：最新 N 封 |
| `SyncWindowDays` | 30（1–3650） | 对账窗口：最近 D 天（与上一项取并集） |
| `SyncIdleEnabled` | true | 是否使用 IDLE |

- **日志**：`%LocalAppData%\MailClient\sync.log`（与 `mail.db` 同目录，超过 1 MB 自动清空）记录 UIDVALIDITY 重置、丢弃的待写回操作、QRESYNC 降级、IDLE 重连、每轮有变化的文件夹。

## 4. 已知缺口

- 没有按 Message-ID 合并“移动”：服务器上的移动在本地是“旧文件夹删除 + 新文件夹新增”，期间本地标识会变（未读 / 星标状态跟随服务器，不丢）。
- Graph 的分类（categories）、Gmail 的标签（`X-GM-LABELS`）变化不同步；Gmail 的“归档”在本地没有对应文件夹，只表现为从收件箱消失。
- Gmail 自己发给自己的邮件，“已发送”里的那一份可能因 `X-GM-MSGID` 相同被合并掉。
- V4 之前同步下来的邮件没有 `GmMsgId`，需要重新拉取才能参与去重。
- 无 CONDSTORE 的服务器：窗口外（超过 2000 封且超过 30 天）的老邮件的变化不检查。
- Graph 没有推送，变化最长晚一个轮询间隔出现；Graph 首次同步限 90 天。
- QQ / 163 / 126 的 CONDSTORE / QRESYNC / IDLE 支持以运行时探测为准，**未用真实账号验证**。
- 不会因为服务器上对邮件打了别的标志（草稿、已回复、已转发）而改变本地显示，只同步已读与星标。

## 5. 代码位置

| 内容 | 位置 |
|---|---|
| 纯逻辑（冲突合并、退避、调度器、选项） | `MailClient.Core\Sync\` |
| IMAP 引擎 / 管线 / 差集函数 | `MailClient.Providers\Imap\ImapFolderSyncEngine.cs`、`ImapSyncModel.cs`、`ImapSyncPipeline.cs` |
| Graph delta | `MailClient.Providers\Graph\GraphSyncPipeline.cs`、`GraphDeltaModel.cs` |
| IDLE / 轮询协调 | `MailClient.Providers\Sync\`（`ImapIdleSupervisor`、`SyncCoordinator`、`FileSyncLog`） |
| 存储（V4 迁移、标志合并、删除、Gmail 去重） | `MailClient.Storage\Migrations\SchemaV4.cs`、`MailDatabase.Sync.cs` |
| 界面原地刷新 | `MessageListViewModel.RefreshInPlaceAsync`、`MailboxTreeViewModel.RefreshAfterSyncAsync`、`ReadingPaneViewModel.RefreshStateAsync` |
| 测试 | `tests\MailClient.Tests\ImapTwoWaySyncTests.cs`、`GraphDeltaTests.cs`、`SyncSchedulingTests.cs`、`LiveRefreshTests.cs` |
