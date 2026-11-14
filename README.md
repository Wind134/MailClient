# MailClient（WinUI 3 骨架）

基于已确认方案搭建的解决方案骨架。

## 结构

- `src/MailClient.App` — WinUI 3 三栏邮件主界面（见 [`docs/ui-shell.md`](docs/ui-shell.md)）
- `src/MailClient.ViewModels` — MVVM（CommunityToolkit.Mvvm）
- `src/MailClient.Core` — 领域模型与服务接口
- `src/MailClient.Providers` — 邮箱适配器（Generic IMAP + Microsoft Graph）
- `src/MailClient.Storage` — SQLite
- `src/MailClient.Auth` — 凭据 / MSAL OAuth
- `tests/MailClient.Tests` — xUnit

## 打开方式

用 **Visual Studio 2022** 打开桌面上的 `MailClient` 文件夹中的 `MailClient.sln`（或 `.slnx`）。

## 说明

- Windows App SDK 已钉到本机已缓存的 **2.3.1**，避免还原时拉最新失败。
- 首次运行建议在 VS 中 F5；需开启「开发人员模式」。**不要直接双击 / 运行打包版 `bin\...\MailClient.App.exe`**（没有包标识，会以 `REGDB_E_CLASSNOTREG` 崩溃），请用 `scripts\run-packaged.ps1` 或 `scripts\run-unpackaged.ps1`，原因与流程见 [docs/packaging.md](docs/packaging.md)。
- SQLite、Generic IMAP（MailKit）与 Microsoft Graph / Outlook（MSAL）已接入；Gmail OAuth（回环 + PKCE，XOAUTH2）已接入。

## 后台 / 托盘

关闭主窗口默认保留在通知区域后台运行，见 [`docs/tray-background.md`](docs/tray-background.md)。

## 本地库

SQLite 表结构与数据访问见 [`docs/sqlite-schema.md`](docs/sqlite-schema.md)。

- 包：`Microsoft.Data.Sqlite` **8.0.11**（本地 `local-nuget` 目录已缓存相关 nupkg，网络不稳时可离线还原）
- 入口：`MailClient.Storage.MailDatabase`

## IMAP / SMTP（MailKit）

通用 IMAP 已接入 **MailKit 4.18.2** / **MimeKit 4.18.2**，详见 [`docs/imap-mailkit.md`](docs/imap-mailkit.md)。

- 入口：`MailClient.Providers.GenericImapProvider`
- 落库：`MailClient.Providers.Imap.ImapSyncPipeline`
- 预设：`ImapProviderPresets`（QQ / 163 / 126 / Gmail IMAP / Outlook IMAP）
- 离线包：`local-nuget` 已缓存 MailKit 相关 nupkg

## Microsoft Graph / Outlook

Outlook / Microsoft 365 已接入 **MSAL 4.92.0** + **Microsoft Graph 6.9.0**，详见 [`docs/microsoft-graph.md`](docs/microsoft-graph.md)。

- 入口：`MailClient.Providers.MicrosoftGraphProvider`（`ProviderId = microsoft.graph`）
- 落库：`MailClient.Providers.Graph.GraphSyncPipeline`
- 需自行在 Entra ID 注册公共客户端并填写 `ClientId`
- 离线包：`local-nuget` 已缓存 Graph / MSAL 相关 nupkg

## 主界面

三栏布局（账户/文件夹 · 邮件列表 · 阅读窗格）+ 新邮件窗口，详见 [`docs/ui-shell.md`](docs/ui-shell.md)。首次启动库为空时写入中文演示数据；数据库路径可用 `MAILCLIENT_DB` 覆盖。

## Gmail（OAuth 2.0）

Gmail 通过 Google OAuth 2.0 已安装应用流程（回环重定向 + PKCE + 系统浏览器）登录，再以 XOAUTH2 连接 IMAP/SMTP，无新增 NuGet 包。Google Cloud Console 配置、受限范围验证（CASA）说明与配置填写见 [`docs/gmail-oauth.md`](docs/gmail-oauth.md)。

- Core：`GmailAccountConfig`；Auth：`GmailTokenAcquirer` / `Pkce` / `LoopbackAuthorizationListener`；Providers：`GmailProvider`（`gmail.oauth`）
- 联调测试需设置 `MAILCLIENT_GMAIL_CLIENT_ID` / `MAILCLIENT_GMAIL_EMAIL`（未设置时自动跳过）；切勿提交真实 ClientId/ClientSecret

## 隐私

本应用为本地桌面客户端，不收集、不上报用户数据。

- 项目首页：<https://wind134.github.io/MailClient/>
- 隐私权政策（网页）：<https://wind134.github.io/MailClient/privacy.html>
- 仓库内 Markdown 原文：[`PRIVACY.md`](PRIVACY.md)

## 添加账号

左侧窗格的“添加账号”向导支持 Gmail、Outlook、QQ、163/126 和其他 IMAP/SMTP；密码保存在 DPAPI 加密的凭据文件里，添加后自动开始同步。详见 [docs/add-account.md](docs/add-account.md)。

## 发送与回写

- **真正发信**：后台发件箱 worker（启动时 / 撰写入队时 / 网络恢复时触发），状态 Pending → Sending → Sent / Failed，指数退避重试（最多 5 次）、区分永久 / 暂时失败、崩溃后可恢复、以 Message-ID 保证不重复发送。IMAP 类账号（QQ / 163 / 126 / 其他）发送后自动追加到“已发送”，Gmail 与 Outlook 由服务器保存。
- **撰写**：回复 / 全部回复 / 转发预填，HTML + 纯文本，附件（有大小警告），草稿本地自动保存。左侧“发件箱”仅在非空时出现，可重试 / 删除。
- **服务器回写**：已读 / 标记 / 归档 / 删除先改本地，再由待回写队列同步到 IMAP、Gmail、Graph；同步不会覆盖尚未回写的本地变更。
- 设计说明、失败分类、已知缺口见 [docs/sending-and-sync.md](docs/sending-and-sync.md)。数据库迁移为 V2（`Outbox` / `PendingOps`）。

## 草稿与搜索

- **草稿**：每 3 秒自动保存到本地（附件复制到应用自己的 `drafts\` 目录，原文件移动不影响），在各账号“草稿”和收藏夹统一“草稿”里与服务器草稿合并显示、标“本地草稿”；双击 / Enter / “继续编辑”重新打开并还原所有字段与回复上下文，同一草稿只开一个窗口；发送后删除，丢弃需确认，关闭窗口保留。
- **搜索**：中栏搜索框（`Ctrl+F`，250 ms 防抖，范围：当前文件夹 / 当前账号 / 全部邮箱），支持 `from:` `to:` `subject:` `has:attachment` `is:unread` `is:flagged` `before:` `after:` 与引号短语；≥3 字符走 FTS5 trigram，1–2 个汉字走 LIKE，高亮命中词，`Esc` 清空，带最近搜索建议。2 万封邮件上所有查询 < 40 ms。数据库迁移为 V3。
- 已知缺口（服务器草稿回写、未打开过的正文不可搜、服务器端搜索）与细节见 [docs/drafts-and-search.md](docs/drafts-and-search.md)。

## 双向同步

- **把服务器上的变化拉回来**：别处（网页、手机）改了已读 / 星标、移动、删除，本地也会跟着变。IMAP 按服务器能力选择 QRESYNC / CONDSTORE（`CHANGEDSINCE`）/ 窗口对账（最新 2000 封 ∪ 最近 30 天，无 CONDSTORE 时），Gmail 用 `X-GM-MSGID` 去重，Graph 用每文件夹 delta（含 `@removed`、410 完整重同步）。
- **冲突规则**：本地操作写回成功前不被覆盖，之后服务器为准；写回后不会被陈旧快照翻回去。UIDVALIDITY 变化会清空该文件夹并重新同步（丢弃指向旧 UID 的待写回操作并写日志）。
- **触发**：收件箱 IMAP IDLE（断线指数退避重连、29 分钟续期）+ 每 5 分钟轮询（可在 `settings.json` 里调 `SyncPollMinutes` / `SyncWindowMessages` / `SyncWindowDays` / `SyncIdleEnabled`），离线 / 挂起暂停，恢复后补同步；刷新按钮强制一轮增量同步；界面原地更新，不丢选中项和滚动位置。日志在 `%LocalAppData%\MailClient\sync.log`。
- 策略表、冲突规则、已知缺口见 [docs/two-way-sync.md](docs/two-way-sync.md)。数据库迁移为 V4（`folders` 增加 `UidNext` / `HighestModSeq` / `LastReconcileAt`，`messages` 增加 Gmail 标识与本地改动时间）。

## 软件内指引与系统通知

- **设置指引**：添加账号向导第 2 步有“怎么设置”分步面板（Gmail / Outlook / QQ / 163 / 其他 IMAP），带可点击的网页链接；报错也直接指向这些步骤，不用再翻文档。见 [docs/add-account.md](docs/add-account.md)。
- **Windows 通知**：收到新的未读收件箱邮件时弹系统通知（少量逐封、大量合并），点击打开邮件，通知上可“标为已读 / 删除”；设置里可开关并发送测试通知。仅在软件运行时有效，细节与限制见 [docs/notifications.md](docs/notifications.md)。

## 正文与附件

- 打开邮件时按需从 IMAP / Graph 拉取**完整正文**（优先 HTML，WebView2 安全渲染；纯文本回退），写入本地缓存（`BodyFetched`），再次打开秒开。
- 同步不会冲掉已缓存正文；IMAP 同步若已下载整封 MIME 会顺带落库。
- 附件在阅读窗格列出，点击可另存到本地。
- 细节与缺口见 [docs/body-and-attachments.md](docs/body-and-attachments.md)。数据库迁移为 **V5**。

## 下载与安装（exe）

从 GitHub 仓库的 **Releases** 页面下载最新版本的两个文件之一：

| 文件 | 用途 |
| --- | --- |
| `MailClient-Setup-<版本>.exe` | **推荐。** 图形安装程序（简体中文，Fluent 风格，随系统浅色/深色）。 |
| `MailClient-<版本>-win-x64-portable.zip` | 绿色版：解压后双击 `MailClient.App.exe` 即可，不写注册表、不建快捷方式。 |

### 安装步骤

1. 双击 `MailClient-Setup-<版本>.exe`。
2. 如果 Windows 弹出蓝色的 **「Windows 已保护你的电脑」**（SmartScreen）：这是因为安装包目前**没有代码签名证书**，并不代表有问题。点 **「更多信息」→「仍要运行」** 即可。部分杀毒软件也可能对未签名安装包误报，可选择信任/放行。
3. 欢迎页可以看到安装位置（默认 `%LocalAppData%\Programs\MailClient`，可点「更改」）并选择是否创建桌面快捷方式，点 **「安装」**。
4. 看着进度动画走完，勾选「立即运行」后点 **「完成」**。

- **只为当前用户安装，不需要管理员权限**，不会弹 UAC。
- 会创建开始菜单快捷方式「邮件」（通知需要它）、可选桌面快捷方式，并在「设置 → 应用 → 已安装的应用」里登记，版本号、卸载入口都在那里。
- **升级**：直接运行新版安装程序即可覆盖安装。若「邮件」正在运行（含托盘后台），安装程序会先把它关闭；账号、邮件数据和设置**不受影响**。
- 体积约 140 MB：这是自包含发布，已带 .NET 和 Windows App Runtime，不需要另外安装运行时。仅支持 64 位 Windows 10 1809 及以上 / Windows 11。

### 卸载

「设置 → 应用 → 已安装的应用 → 邮件 → 卸载」，或运行安装目录里的 `MailClient.Setup.exe /uninstall`。卸载界面风格与安装一致，其中有一个**默认不勾选**的选项「同时删除我的邮件数据和设置」：不勾选时 `%LocalAppData%\MailClient`（本地邮件缓存、账号设置、登录信息）原样保留，重装后可直接接着用。

### 静默安装 / 命令行

```text
MailClient-Setup-1.0.14.exe /S                  静默安装（默认路径）
MailClient-Setup-1.0.14.exe /S /D=D:\Apps\Mail  指定安装目录（也可 /dir=...）
MailClient-Setup-1.0.14.exe /S /nodesktop       不创建桌面快捷方式
MailClient-Setup-1.0.14.exe /S /run             安装后启动
MailClient.Setup.exe /uninstall [/S] [/deletedata]
```

### 自己构建

```powershell
.\scripts\build-installer.ps1            # 单元测试 → 自包含发布 → 安装程序 + 绿色版 zip
.\scripts\build-installer.ps1 -SkipTests # 跳过测试，更快
```

产物在 `artifacts\release\`（含 `SHA256SUMS.txt`）。推送 `v*` 标签时 `.github/workflows/release.yml` 会自动构建并创建带这两个文件的 GitHub Release。详见 [docs/installer.md](docs/installer.md)。

## 检查更新

设置 → 常规 → 「更新」：开关「自动检查更新」（默认开），按钮「检查更新」显示当前版本和结果。启动约 30 秒后检查一次，之后每 6 小时一次，数据来自 GitHub Releases 的公开接口（不需要登录）。发现新版本会弹出对话框（版本号 + 可滚动的更新说明），可「立即更新 / 稍后提醒 / 跳过此版本」；exe 安装版会下载安装包、校验 SHA256 后静默覆盖安装并自动重启，MSIX 版和绿色版则打开发布页。检查失败（离线、限流、仓库还没有 Release）不会打扰你。详见 [docs/updates.md](docs/updates.md)；仓库地址的配置位置见 [docs/installer.md](docs/installer.md)。

## 运行与打包

- `scripts\run-unpackaged.ps1`：免打包构建并运行（无需开发人员模式；`-FrameworkDependent` 改用本机已安装的 Windows App Runtime；`-DbPath` 指定演示用数据库）。
- `scripts\run-packaged.ps1`：构建 MSIX → `Add-AppxPackage -Register` → 按 AUMID 启动（需开启开发人员模式；脚本只读检查，不会改系统设置；`-InstallRuntime`、`-Unregister` 见脚本头部说明）。
- `scripts\build-msix.ps1`：生成已签名的旁加载 `.msix`（测试证书只导出文件，不安装、不信任）。
- 根因、证据、发布所需条件（证书、运行时依赖、Store 与旁加载）见 [docs/packaging.md](docs/packaging.md)。
