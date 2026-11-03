# 发送与服务器回写

本文说明两件事：**发件箱投递（真正发信）** 和 **已读 / 标记 / 归档 / 删除的服务器回写**。两者都是“本地先行”：界面先改本地库，后台 worker 再把变更同步到服务器；失败不会丢数据，也不会卡住界面。

> 开发与测试约定：所有自动化测试使用 fake，不联网、不登录真实账号、不发真实邮件。演示账号（`DemoDataSeeder.IsDemoAccount`）不会产生回写任务，发送时会被判为永久失败（见下）。

## 1. 发件箱

### 1.1 状态机

```
Pending ──取到并锁定──▶ Sending ──成功──▶ Sent
   ▲                       │
   │  可重试的失败          ├─ 永久失败 ──▶ Failed（用户点“重试”回到 Pending）
   └──── 退避等待 ◀─────────┘  可重试且次数未满 → Pending（NextAttemptAt = 退避后时间）
```

- 入口：撰写窗口点“发送”→ 写入 `Outbox` 表（状态 Pending，并先生成并保存 `Message-ID`）→ 立即踢一下 worker。
- 触发时机：应用启动时、Compose 入队后、网络恢复（`NetworkAvailabilityChanged`）时，以及到点的退避重试（`OutboxWorker` 里的 `KickLoop`）。
- **崩溃安全**：启动时把卡在 `Sending` 的条目重置为 `Pending`，并标记为 `MaybeSent`（可能已经发出）。

### 1.2 重试与退避

- 最多 `RetryPolicy.MaxAttempts = 5` 次。
- 退避：`30 秒 × 4^(n-1)`，上限 1 小时（30s、2min、8min、32min、1h）。
- 追加“已发送”副本（APPEND）失败单独重试，最多 `MaxCopyAttempts = 4` 次，不会重发邮件。

### 1.3 失败分类（`SendFailureClassifier`）

| 类别 | 例子 | 处理 |
| --- | --- | --- |
| 暂时性 Transient | 网络断开、超时、连接被重置、服务器 4xx 临时错误、SMTP 421/450/451/452 | 退避重试，次数用完后转 Failed |
| 认证 Authentication | 密码 / 授权码错误、XOAUTH2 令牌失效、Graph 401 | 永久失败，提示重新登录 / 重新输入授权码 |
| 收件人被拒 RecipientRejected | SMTP 550/553 对收件人、Graph 400 无效地址 | 永久失败，指出被拒地址 |
| 邮件被拒 MessageRejected | 邮件过大、被判垃圾、Graph 附件超限 | 永久失败 |
| 配置 Configuration | 账号被删、SMTP 未配置、演示账号 | 永久失败 |

永久失败直接进入 `Failed`，不再自动重试；用户在发件箱里点“重试”可手动再来一轮（次数清零）。

### 1.4 幂等：不重复发送

- 发送前先把 `Message-ID` 存进 `Outbox` 行，重试时复用同一个 `Message-ID`。
- 若条目是崩溃恢复出来的（`MaybeSent`），先去服务器“已发送”里按 `Message-ID` 搜索：
  - 找到 → 视为已发送，不再重发；
  - 没找到，且服务器是权威的（Gmail / Graph，发送必定留副本）→ 重发；
  - 没找到，但服务器不保证留副本（QQ / 163 / 其他 IMAP）→ 不敢重发，转 `Failed`，提示“无法确认是否已发出…”，由用户决定是否重试。

### 1.5 已发送副本（`SentCopyPolicy`）

| 提供商 | 发送后是否 APPEND 到“已发送” |
| --- | --- |
| Gmail（SMTP） | 否，Gmail 自己会保存 |
| Outlook / Graph | 否，`saveToSentItems=true` |
| QQ / 163 / 126 / 其他 IMAP | 是（默认） |

- “已发送”文件夹：优先本地已知的 Sent 文件夹路径，其次服务器 SPECIAL-USE `\Sent`，最后按文件夹名称识别（`Sent`、`Sent Messages`、`已发送` 等，见 `FolderSpecialMapper`）。找不到时副本追加失败（NotFound），邮件本身仍算已发送；副本追加单独重试，最多 4 次，之后放弃并在条目上留下说明。
- 可按预设覆盖：`%LocalAppData%\MailClient\settings.json`

  ```json
  { "SentCopyOverrides": { "Qq": false, "NetEase": true, "OtherImap": true } }
  ```

  键为预设名（`Qq` / `NetEase` / `OtherImap`）。Gmail 和 Graph 不能被覆盖为 true（会产生重复邮件）。
- 追加成功后，本地“已发送”里立即能看到这封邮件。

### 1.6 撰写与 MIME（`OutgoingMimeBuilder`）

- 发件人：账号显示名 + 地址；To / Cc / Bcc 支持分号、逗号分隔，带校验，Bcc 不写进头部（SMTP 信封里仍有）。
- 正文：`text/plain` + `text/html` 两种备选；回复 / 全部回复 / 转发会在正文后附“引用的原文”（界面里只读，发送时拼接），并设置 `In-Reply-To`、`References`（目前只带父邮件的 Message-ID）。
- 阅读窗格的 回复 / 全部回复 / 转发 按钮会预填撰写窗口（主题加 `Re:` / `Fwd:`，全部回复去掉自己的地址）。
- 附件：文件选择器添加；总大小超过限制只给出警告，不阻止（由服务器最终裁决）。阈值为原始字节数：Outlook / Graph 3 MB，QQ / 163 / 126 / yeah.net 30 MB，Gmail 与其他 IMAP 18 MB（`AttachmentLimits`）。
- Graph：附件合计 ≤ 3 MB 走内联 `fileAttachment`；更大的暂未实现上传会话，会以“邮件被拒”报告（见已知缺口）。
- 草稿：撰写窗口 3 秒防抖自动保存到本地“草稿”文件夹；发送或点“丢弃”后清除。

### 1.7 界面

- 左侧出现“发件箱”节点（仅当发件箱非空时显示），列表里每行显示状态、错误文字，以及 **重试 / 删除** 操作。
- 发送成功 / 失败时底部弹出 InfoBar：失败会写明原因，以及“稍后会自动重试”还是“请在发件箱里查看并重试”。

## 2. 已读 / 标记 / 归档 / 删除的回写

### 2.1 待回写队列（`PendingOps` 表，schema 迁移）

> 迁移编号说明：原先库的 schema 版本是 1，所以本次迁移是 **V2**（需求里写的 V3 对应这里的 V2）。

操作类型：`MarkRead` / `MarkUnread` / `Flag` / `Unflag` / `Move`（归档、移到垃圾箱）/ `Delete`。

- **本地先行**：界面操作先改本地库（乐观更新），再入队，队列写成功才算完成；回写失败不回滚本地状态。
- **合并（coalescing）**：同一封邮件按字段合并，最新的覆盖旧的——已读状态一个字段、标记状态一个字段、位置（归档 / 删除 / 移动）一个字段。例如先 MarkRead 后 MarkUnread，只剩 MarkUnread；删除后之前针对它的已读 / 标记任务全部丢弃。
- 执行顺序：已读 → 标记 → 位置（位置最后，因为 Move 后 UID 会变）。
- 失败处理：
  - 暂时性失败：指数退避重试；
  - 认证 / 配置失败：1 小时后再试（`PendingOpsProcessor.AuthBackoff`）；
  - 邮件不存在（NotFound）、UIDVALIDITY 变了、服务器明确拒绝：丢弃该任务并触发 `OpDropped` 事件（界面可提示），避免卡死队列。

### 2.2 各协议的实现

| | 已读 / 标记 | 归档 | 删除 |
| --- | --- | --- | --- |
| 通用 IMAP（含 QQ/163/126） | `UID STORE +FLAGS / -FLAGS (\Seen)`、`(\Flagged)`；每次先核对 UIDVALIDITY | 优先 `MOVE`，不支持则 `COPY` + 标记 `\Deleted` + `EXPUNGE`（有 UIDPLUS 时只 expunge 该 UID） | 本地先移到垃圾箱（Move）；已在垃圾箱里（或账号没有垃圾箱）再删除则永久删除：`+FLAGS \Deleted` 后 `EXPUNGE`（有 UIDPLUS 时只 expunge 该 UID） |
| Gmail（XOAUTH2 IMAP） | 同上 | 移到 `[Gmail]/All Mail`（等价于去掉 Inbox 标签） | 移到 `[Gmail]/Trash`；在垃圾箱里再删除则永久删除 |
| Microsoft Graph | `PATCH isRead` / `flag.flagStatus` | `POST /move` 到 `archive` | `POST /move` 到 `deleteditems`；已在其中则 `DELETE` |

### 2.3 同步合并规则

同步（拉取服务器）不再覆盖本地尚未回写的变更：

- 某封邮件的某个字段（已读 / 标记 / 位置）在 `PendingOps` 里还有未完成任务 → 跳过该字段，保留本地值；
- 没有挂起任务的字段 → 以服务器为准。

### 2.4 界面

- 打开邮件 600 ms 后自动标已读（切换太快不会误标）；
- 阅读窗格工具栏：标记 / 归档 / 删除 / 未读切换；列表行右键菜单同样提供这些命令；
- 所有操作先改界面，再在后台回写。

## 3. 测试

- `OutboxProcessorTests`：状态机、重试 / 退避、永久失败分类、幂等（MaybeSent 三条分支）、Sent 副本决策；
- `SendingPureTests`：`RetryPolicy`、`SendFailureClassifier`、`SentCopyPolicy`、附件限制、`OutgoingMimeBuilder`（回复头、引用、附件）；
- `PendingOpsTests`：合并、同步合并规则、数据库层、`PendingOpsProcessor`（fake 网关）；
- `ViewModelSendingTests`：撰写 / 发件箱 / 列表操作的视图模型。
- 以上全部使用 fake 传输和网关，不联网。
- **联调测试**：本次没有编写需要真实账号的联调测试（任务要求不接触真实账号）。已有的 Gmail 联调测试仍由 `MAILCLIENT_GMAIL_CLIENT_ID` / `MAILCLIENT_GMAIL_EMAIL` 控制，未设置时自动跳过。

运行：

```powershell
dotnet test tests\MailClient.Tests\MailClient.Tests.csproj --nologo
```

## 4. 已知缺口

- 未用真实 SMTP / Graph / Gmail 服务器验证发送与回写（MOVE、UIDPLUS 的回退路径同样未联调）。
- 已同步过的 IMAP 邮件，服务器端后来变化的已读 / 标记状态暂不会拉回（同步游标是最大 UID，只拉新邮件）。
- 草稿只保存在本地；没有同步到服务器 Drafts，也还不能从界面重新打开草稿继续编辑。
- Graph 超过 3 MB 的附件未实现上传会话（会明确报“邮件被拒”并在撰写窗口预警）。
- Graph `sendMail` 无法设置 In-Reply-To，回复邮件在对方那边可能不会串成会话。
- `References` 只带父邮件的 Message-ID，不带完整链；转发不携带原邮件的附件。
- Graph 邮件刚在本地被移动后，针对它的后续任务会等到下一次同步（拿到新 id）才执行。
- 一封邮件的移动任务正在执行时又被重新入队，存在极小的竞态窗口。
- 认证失败时，同一封邮件的其他任务仍会立刻到期、各自再失败一次（只是多几次无害的失败）。
