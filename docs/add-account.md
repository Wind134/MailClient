# 添加账号向导

左侧窗格底部的 **“添加账号”** 按钮会打开向导（ContentDialog）。没有任何真实账号（演示数据除外）时，应用启动后会弹出首次使用提示，按钮也会以强调色显示。

## 流程

| 步骤 | 内容 |
| --- | --- |
| 1. 选择服务商 | Gmail、Outlook / Microsoft 365 / Hotmail、QQ 邮箱、163 / 126 网易邮箱、其他 IMAP/SMTP |
| 2. 登录信息 | 随服务商不同，见下 |
| 3. 验证连接 | 依次连接并认证 IMAP、SMTP（Outlook 为 Microsoft Graph `/me`），显示进度，失败时给出具体中文原因，可“重试” |
| 4. 完成 | 填写显示名称，可选账号颜色；保存后后台开始首次同步 |

### 软件内的设置指引

第 2 步顶部有一个 **“怎么设置”** 折叠面板（`ProviderGuide`，每种服务商一份），把需要在网页里做的事拆成带编号的步骤，带链接的步骤会直接在默认浏览器里打开对应页面：

- Gmail / Outlook 这类需要自己的 OAuth 凭据、而且尚未配置 ClientId 的账号，面板默认展开；QQ / 163 / 126 / 其他 IMAP 也默认展开，其余情况收起，随时可点开。
- 面板里还写了常见坑：Gmail 应用处于“测试”状态时授权 7 天失效、“Google 尚未验证此应用”提示是正常的、QQ 授权码只显示一次等。
- 报错信息会指向这些步骤（“请展开「怎么设置」…”），不再要求去翻文档文件。下面的 `gmail-oauth.md`、`microsoft-graph.md` 只是给开发者看的补充。

### 第 2 步：各服务商

- **Gmail**：可填邮箱作提示，点 **“用 Google 登录”** 在系统浏览器里完成授权（`GmailTokenAcquirer`）。等待时可点“取消登录”。如果没有配置 ClientId，向导会提示按“怎么设置”操作（开发者细节见 [`gmail-oauth.md`](gmail-oauth.md)），同时在“OAuth 客户端设置”里填写 ClientId（可选 ClientSecret），保存到应用设置。
- **Outlook**：**“用 Microsoft 登录”**（`MicrosoftGraphTokenAcquirer`，**默认系统浏览器**，不用 WAM；`UseWamBroker` 可选手动打开）。Entra 需登记 `http://localhost`，建议同时登记 `ms-appx-web://microsoft.aad.brokerplugin/{ClientId}`。ClientId 缺失时提示展开“怎么设置”（开发者细节见 [`microsoft-graph.md`](microsoft-graph.md)）。
- **QQ 邮箱 / 163 / 126**：邮箱地址 + **授权码**（不是登录密码），“怎么设置”面板里有分步说明和网页链接。
  - QQ：设置 → 账户 → 开启 IMAP/SMTP 服务 → 生成授权码。
  - 163/126：设置 → POP3/SMTP/IMAP → 开启并新增授权密码。
  - 服务器取自 `ImapProviderPresets`；163/126 连接时会发送 IMAP `ID` 命令，避免 “Unsafe Login”。
- **其他 IMAP/SMTP**：邮箱 + 密码。点“下一步”或“自动检测服务器”时：先按域名匹配内置预设，再尝试 Mozilla ISPDB（`https://autoconfig.thunderbird.net/v1.1/<域名>`，尽力而为，约数秒超时）。仍然没有结果时，会按 `imap.<域名>:993` / `smtp.<域名>:465` 预填并展开 **“高级设置”**，需要核对后再点一次“下一步”。高级设置里可改 IMAP/SMTP 主机、端口、安全性（SSL / STARTTLS / 无）和用户名。

## 保存位置

| 数据 | 位置 |
| --- | --- |
| 账号（`accounts` 表） | 邮件库 SQLite（`%LOCALAPPDATA%\MailClient\mail.db`，或 `MAILCLIENT_DB`） |
| 密码 / 授权码 / OAuth 令牌 | `%LOCALAPPDATA%\MailClient\credentials.dat`，使用 Windows **DPAPI**（当前用户）加密，`DpapiCredentialStore` |
| 连接设置（服务器、类型） | 同一凭据文件内的 `conn:<账号Id>` 条目 |
| ClientId / ClientSecret、账号颜色、提示是否关闭 | `%LOCALAPPDATA%\MailClient\settings.json`（**明文**；仓库提供 `settings.example.json` 作模板，不要把填好的设置提交进 Git） |

设置了 `MAILCLIENT_DB` 时，`settings.json` 和 `credentials.dat` 与该数据库放在同一目录，便于隔离测试。环境变量 `MAILCLIENT_GMAIL_CLIENT_ID` 等优先于设置文件。

## 首次同步与刷新

完成向导后会在后台同步文件夹和最近邮件，进度显示在左侧窗格底部，邮箱树随后自动刷新。顶部“刷新”按钮在存在真实账号时会对它们执行同步（`ISyncService` → `AccountSyncService`）；只有演示数据时仍是原来的演示刷新。

## 移除账号 / 清除演示数据

- 右键账号节点 → **“移除账号…”**（有确认对话框；同时删除本地邮件和保存的凭据，不影响服务器上的邮件）。节点菜单里还有“同步此账号”。
- 已有真实账号后，左侧底部出现 **“清除演示数据”**，确认后删除两个演示账号及其邮件，并不再自动重新生成。

## 常见错误提示

| 提示 | 原因 |
| --- | --- |
| 需要使用授权码 | QQ/163/126 用了登录密码；先在网页邮箱开启 IMAP/SMTP 并生成授权码 |
| 用户名或密码不正确 | 认证失败 |
| 找不到服务器 / 无法连接服务器 | DNS 解析失败 / 端口被拒绝或被防火墙拦截 |
| TLS/SSL 错误 | 安全性选项与端口不匹配，或证书无效 |
| Unsafe Login（网易） | 被服务器拒绝，请确认已开启 IMAP 并使用授权码 |

## 已知限制

- Gmail / Microsoft 登录在测试中未用真实账号验证（需要你自己的 ClientId）。
- SMTP 验证失败会阻止完成向导。
- 没有发件箱投递线程：写信保存到本地发件箱，不会真正发出。
- 首次同步跳过 Gmail “所有邮件”和自定义文件夹。
- DPAPI 凭据只能由同一 Windows 用户读取，换用户/换机器需要重新添加账号。

## 代码位置

- 向导逻辑：`src/MailClient.ViewModels/AddAccount/`（`AddAccountViewModel`，可用假的 `IAccountSetupService` 测试）
- 服务：`src/MailClient.Providers/Setup/`（`AccountSetupService`、`ProviderDetector`、`IspdbAutoconfig`、`SetupErrorClassifier`、`AccountConnectionStore`、`AccountSyncService`）
- 界面：`src/MailClient.App/Controls/AddAccountView.xaml`，对话框和菜单在 `MainWindow.xaml(.cs)`
- 测试：`tests/MailClient.Tests/AddAccountTests.cs`
