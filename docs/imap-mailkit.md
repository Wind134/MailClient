# Generic IMAP / SMTP（MailKit）说明

## 已实现

- 使用 **MailKit 4.18.2** + **MimeKit 4.18.2** 连接 IMAP（SSL / STARTTLS）与 SMTP
- 账号密码或**授权码**（`AuthKind.Password` / `AppPassword` 路径）
- 列举文件夹，并按 **SPECIAL-USE** + 常见中英文名称映射到 Inbox / Sent / Drafts / Trash / Flagged
- 按 UID 拉取文件夹邮件摘要（信封、标志、体积、预览文本）
- SMTP 发送（Html + 本地附件路径）
- `ImapSyncPipeline`：把文件夹与邮件写入 SQLite（`MailDatabase`）
- 凭据：`ICredentialStore`（vault key），格式见下；测试可用构造函数直接传入 `ImapAccountSecrets`

## 尚未实现（后续）

- Gmail / Microsoft **XOAUTH2**（本阶段仍可用应用专用密码走 IMAP，若服务商允许）
- IDLE / 推送、标志回写（已读/星标同步到服务器）、完整正文按需下载缓存
- 附件二进制落盘、会话串线算法、UIDVALIDITY 失效后整夹重建
- 首次同步目前默认只拉每个文件夹**最近 N 封**（`SyncBatchSize`，默认 50）

## 配置示例

### QQ 邮箱

1. 网页邮箱开启 IMAP/SMTP，生成**授权码**（不是登录密码）
2. 预设：`ImapProviderPresets.QqMail()`  
   - IMAP `imap.qq.com:993` SSL  
   - SMTP `smtp.qq.com:465` SSL  

### 网易 163 / 126

1. 开启 IMAP，生成授权码  
2. `ImapProviderPresets.NetEase163()` / `NetEase126()`  
   - `imap.163.com` / `imap.126.com` :993  
   - SMTP 同域 :465  

### 通用

```csharp
var config = new GenericImapAccountConfig(
    AccountId: accountId,
    EmailAddress: "you@example.com",
    DisplayName: "你",
    Imap: new ImapServerSettings("imap.example.com", 993, MailSecureSocketOptions.SslOnConnect),
    Smtp: new SmtpServerSettings("smtp.example.com", 587, MailSecureSocketOptions.StartTls),
    CredentialVaultKey: "mail:you@example.com");

await credentialStore.SaveSecretAsync(
    "mail:you@example.com",
    "you@example.com\n你的授权码或密码");

await using var provider = new GenericImapProvider(config, credentialStore);
await provider.ConnectAsync();
```

凭据字符串格式（任选其一）：

1. `用户名\n密码`（推荐）  
2. `用户名:密码`（第一个冒号分割）  
3. 仅密码 → 用户名回落为 `EmailAddress`

## 落库

```csharp
var pipeline = new ImapSyncPipeline(database);
await pipeline.SyncFoldersAsync(provider);
await pipeline.SyncFolderMessagesAsync(provider, "INBOX", maxCount: 50);
```

本地文件夹 Id：`{accountId}:{remoteFullName}`。`folders.SyncCursor` 存最大 UID，便于下次增量。

## 联调测试

设置环境变量后运行测试项目中的 `LiveImapTests`：

- `MAILCLIENT_IMAP_HOST`
- `MAILCLIENT_IMAP_PORT`（默认 993）
- `MAILCLIENT_IMAP_USER`
- `MAILCLIENT_IMAP_PASSWORD`
- 可选 `MAILCLIENT_IMAP_SSL=ssl|starttls|none`
- 可选 `MAILCLIENT_SMTP_HOST` / `MAILCLIENT_SMTP_PORT`

未设置时该用例直接跳过（通过）。
