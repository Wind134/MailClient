# MailClient SQLite 表结构（MVP）

本地优先：列表与阅读先读 SQLite；同步服务把远端增量写入库；发送走 Outbox。

实现位置：`src/MailClient.Storage`（`Microsoft.Data.Sqlite` **8.0.11** + 原始 SQL 迁移 `SchemaV1`）。

## ER 概览

```text
accounts 1──* folders
accounts 1──* messages
accounts 1──1 credentials_meta
accounts 1──* outbox
folders  1──* messages
messages 1──* recipients
messages 1──* attachments
messages 1──1 messages_fts   (FTS5，外置 content + 触发器维护)
```

## 表一览

| 表 | 作用 |
|---|---|
| `schema_version` | 迁移版本（当前 `6`） |
| `accounts` | 账号：Gmail / Microsoft / 通用 IMAP / QQ·163 |
| `folders` | 文件夹 + `Special` + `SyncCursor` / `UidValidity` |
| `messages` | 邮件正文与元数据（Body 可空；V5 起 `BodyFetched` 标记完整正文是否已缓存） |
| `attachments` | 附件元数据（V5 起 `RemoteId` 供 Graph 下载） |
| `recipients` | To / Cc / Bcc |
| `attachments` | 附件元数据与本地缓存路径 |
| `outbox` | 发送队列（Pending → Sending → Sent / Failed） |
| `credentials_meta` | 仅存 Windows 凭据库的 `VaultKey`，**从不存密钥** |
| `messages_fts` | FTS5：Subject / FromName / FromAddress / Preview / BodyText |

## 字段要点

### accounts
- `ProviderId`：如 `gmail` / `microsoft` / `imap` / `qq` / `netease`
- `AuthKind`：`OAuth` | `AppPassword` | `Password`

### folders
- `Special`：`Inbox` `Sent` `Drafts` `Trash` `Flagged` `Outbox` `Custom`
- `SyncCursor`：IMAP UIDNEXT / Graph delta / Gmail HistoryId 等不透明字符串
- `UidValidity`：IMAP UIDVALIDITY；失效时需全量重建该文件夹

### messages
- `RemoteUid`：文件夹内唯一（有条件唯一索引）
- `Category`：可选 `Primary` / `Transactions` / `Updates` / `Promotions`（UI 壳；规则二期）
- `BodyText` / `BodyHtml`：可空；列表只依赖 `Preview`

### outbox
- `PayloadJson`：序列化后的待发信（收件人、主题、正文、附件路径）
- 断网可重试；成功后再写入 Sent（由同步/发送服务负责）

## 同步约定（MVP）

1. 列表 / 阅读：**只读本地库**
2. 后台按账号拉取 → `UpsertFolder` / `UpsertMessages`
3. 发送：`EnqueueOutbox` → 后台投递 → 更新 `Status`
4. 搜索：`SearchMessagesAsync` → FTS5 `MATCH`（按空白分词 AND）

## C# 模型

- 精简 UI/协议记录：`MailAccount` / `MailFolder` / `MailMessageSummary` / `OutgoingMailMessage`（保持兼容）
- 持久化实体：`AccountEntity` / `FolderEntity` / `MessageEntity` / `RecipientEntity` / `AttachmentEntity` / `OutboxEntity` / `CredentialsMetaEntity`

## 迁移

`MailDatabase.InitializeAsync()` 执行 `Migrations/SchemaV1.Sql`（含 FTS 触发器）。后续版本在 `schema_version` 上递增并追加脚本即可。

- **V6**（1.0.8）：标记位迁移；启动时 `RepairBadPreviewsAsync` 把被 CSS 污染的 `Preview`（及被污染的 `BodyText`）按 `BodyHtml` 重算。不改 `BodyHtml`。
- **V7**（1.0.12）：`messages.BodyAccessedAt`（正文最后打开 / 拉取时间，UTC ISO 文本，可空）+ 部分索引 `ix_messages_body_cache`（仅 `BodyFetched = 1`）；已缓存的正文回填成迁移时刻。新库同时启用 `auto_vacuum = INCREMENTAL`，老库在第一次存储维护时用一次 VACUUM 转换。详见 [storage.md](storage.md)。
- 列表预览算法见 `MailClient.Core.HtmlPreview`（剥 `style`/`script`/`head`、丢弃 CSS 残留）。

## 搜索说明

- 拉丁文查询：FTS5 `MATCH`（空白分词 + AND，词加引号）
- **含中日韩字符的查询**：改走 `LIKE %关键词%`（Subject / From / Preview / BodyText），因为默认 `unicode61` 常把连续汉字当成单一 token，子串 MATCH 不可靠
- MVP 不做拼音 / 同义词；后续可评估 trigram 或自定义分词

