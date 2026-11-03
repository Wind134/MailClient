# 正文懒加载与附件

## 问题

同步阶段只保存约 200 字的 **Preview**（列表摘要）。1.0.4 及以前阅读窗格用 `BodyText ?? Preview` 显示，用户会看到「内容被截断」的错觉。

## 做法（schema V5 / 包 1.0.5.0）

1. **`messages.BodyFetched`**：`1` 表示已向服务器取过完整正文（即使正文为空也不再重复拉）。
2. **`attachments.RemoteId`**：Graph 附件 Id；IMAP 可空，下载时按文件名在 MIME 里匹配。
3. **打开邮件时**：
   - 已缓存 → 直接显示 `BodyHtml`（WebView2，脚本禁用）或 `BodyText`。
   - 未缓存 → 显示「正在加载完整正文…」，通过 `MessageBodyService` 按账号类型拉取：
     - IMAP / Gmail XOAUTH2：`GetMessageAsync` + `MimeMessageMapper.ExtractContent`（multipart/alternative 优先 HTML）。
     - Graph：`GET /me/messages/{id}?$select=body,uniqueBody,hasAttachments`，并列出附件元数据。
   - 写入 `SaveMessageBodyAsync` 后刷新阅读窗格；失败显示 InfoBar +「重试」，并临时展示 Preview（标明不是全文）。
4. **同步保缓存**：`UpsertMessage` 仅在 `excluded.BodyFetched=1` 时覆盖 `BodyText` / `BodyHtml`；IMAP 同步若已 `GetMessageAsync` 会顺带 `PersistFetchedBodiesAsync`。
5. **附件**：阅读窗格列出非内联附件；点击弹出另存为，走 IMAP MIME 或 Graph `ContentBytes` / `/$value`。

## 入口

| 层 | 类型 |
| --- | --- |
| Core | `IMessageBodyLoader` / `MessageBodyContent` / `MessageAttachmentInfo` |
| Storage | `SchemaV5`、`MailDatabase.SaveMessageBodyAsync` |
| Providers | `MessageBodyService`、`GenericImapProvider.FetchBodyAsync`、`MicrosoftGraphProvider.FetchBodyAsync` |
| ViewModels | `ReadingPaneViewModel`（加载态 / 错误 / 重试 / 附件下载事件） |

## 已知缺口

- 未打开过的邮件正文仍不在 FTS 索引里（搜不到全文）；打开并缓存后需另议是否重建 FTS。
- 后台预取未读正文默认关闭，避免打爆服务器。
- Graph 发信附件合计 > 3 MB 的上传会话仍未实现（与本次下载无关）。
- 内联图片 CID 替换未做，HTML 里的 `cid:` 图可能裂开。

## 列表预览（1.0.8）

- `HtmlPreview` / `MimeMessageMapper.BuildPreview`：优先纯文本；HTML 先剥 `style`/`script`/`head` 再剥标签；`LooksLikeCss` 丢弃残留样式文本。
- 存量脏 Preview：schema V6 + 启动时 `RepairBadPreviewsAsync`（有 `BodyHtml` 时重算；不改阅读窗 HTML）。

