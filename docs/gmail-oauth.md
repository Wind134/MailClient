# Gmail OAuth（Google OAuth 2.0 · 已安装应用）

MailClient 通过 **Google OAuth 2.0「已安装应用」流程**（回环重定向 + PKCE + 系统浏览器）登录 Gmail，
然后用 **XOAUTH2** 连接 `imap.gmail.com:993` / `smtp.gmail.com:465`。**不需要任何新的 NuGet 包**：
授权流程是手写的（`TcpListener` 回环监听 + `HttpClient` + `System.Text.Json`），可完全离线还原。

## 代码结构

| 层 | 类型 | 作用 |
| --- | --- | --- |
| Core | `GmailAccountConfig` | ClientId、可选 ClientSecret、scope、同步批量等配置；`GmailScopes` / `GmailEndpoints` 常量；`IOAuthTokenSource` 接口 |
| Auth | `Pkce` | code_verifier（32 字节随机 → base64url，43 字符）/ S256 challenge / state |
| Auth | `GoogleAuthorizationUrl` | 生成授权 URL（`access_type=offline`、`prompt=consent`、`code_challenge_method=S256`、`login_hint`） |
| Auth | `LoopbackAuthorizationListener` | 绑定 `127.0.0.1:随机端口`，校验 state，向浏览器返回中文“登录成功”页，默认 5 分钟超时 |
| Auth | `GmailTokenAcquirer` | 取令牌：缓存有效 → 直接用；距过期 < 2 分钟 → refresh token 静默刷新；否则拉起浏览器交互登录 |
| Auth | `GmailTokenStore` | 把令牌 JSON 存进 `ICredentialStore`（键 `oauth:gmail:{AccountId}`），与 MSAL 缓存同一模式 |
| Providers | `GmailProvider`（`gmail.oauth`） | 复用 `GenericImapProvider` + `ImapSyncPipeline`；IMAP / SMTP 均用 `SaslMechanismOAuth2` |
| Providers | `GmailCategoryMapper` | Gmail 标签页（category）→ 应用的 4 个分类 |

### 令牌生命周期

1. 首次：`AcquireAsync(allowInteractive: true)` → 浏览器登录 → 授权码换 `access_token` + `refresh_token`，写入凭据库。
2. 之后每次 `ConnectAsync` / `SendAsync` 都向 `IOAuthTokenSource` 要最新令牌（**不会弹浏览器**）：
   访问令牌（约 1 小时）临近过期自动刷新；刷新响应不带 `refresh_token` 时**沿用旧的**，若 Google 轮换了新的则采用新的。
3. `invalid_grant`（用户撤销授权、改密码，或 Testing 模式下 refresh token 满 7 天过期）→ 清除本地令牌，
   后台调用抛出 `GmailAuthRequiredException`，由 UI 提示“重新登录”，再调用 `AcquireAsync(true)`。
4. 退出：`SignOutAsync()` 先尽力向 `oauth2.googleapis.com/revoke` 撤销，再删除本地令牌。
5. 登录的 Google 账号与配置的邮箱不一致时拒绝并**不保存**令牌。

### 用法示例

```csharp
var config = new GmailAccountConfig(
    AccountId: Guid.NewGuid(),
    EmailAddress: "you@gmail.com",
    DisplayName: "你的名字",
    ClientId: "xxxx.apps.googleusercontent.com",
    ClientSecret: null);                      // Desktop 客户端若要求，可填；见下文

ICredentialStore store = /* 应用的凭据库实现 */;
using var acquirer = new GmailTokenAcquirer(config, store);
await acquirer.AcquireAsync(allowInteractive: true);   // 首次会打开浏览器

await using var gmail = new GmailProvider(config, acquirer);
var pipeline = new ImapSyncPipeline(database);
await pipeline.SyncFoldersAsync(gmail.Imap);
await pipeline.SyncFolderMessagesAsync(gmail.Imap, "INBOX");
```

## Google Cloud Console 配置步骤

> 以下界面名称以 Google 当前控制台为准（“Google Auth Platform”），不同时期文案可能略有差异。

1. **创建项目**：打开 <https://console.cloud.google.com/> → 顶部项目选择器 → **新建项目**，随便起名（如 `MailClient`）。
2. **启用 Gmail API（可选）**：**API 和服务 → 库 → Gmail API → 启用**。
   仅用 IMAP/SMTP 时**并不是必须**，XOAUTH2 只校验 scope；启用它便于以后改用 Gmail REST API。
3. **配置 OAuth 同意屏幕**（Google Auth Platform → 品牌塑造 / 目标对象 / 数据访问）：
   - 用户类型选 **外部（External）**；填写应用名称、用户支持邮箱、开发者联系邮箱。
   - **发布状态保持“测试（Testing）”**。
   - **目标对象 → 测试用户 → Add users**：加入你要登录的 Gmail 地址（最多 100 个）。**不在名单里的账号会得到 `access_denied`**。
   - **数据访问 → 添加或移除范围**：添加 `https://mail.google.com/`（受限范围）以及 `openid`、`.../auth/userinfo.email`、`.../auth/userinfo.profile`。
4. **创建 OAuth 客户端**：**客户端（Clients）→ 创建客户端 → 应用类型选「桌面应用（Desktop app）」** → 创建。
   记下 **客户端 ID**（`xxxx.apps.googleusercontent.com`）和**客户端密钥**。
   - 桌面应用客户端**允许任意 `http://127.0.0.1:端口` 回环重定向**，所以无需登记重定向 URI。
   - 桌面应用的“客户端密钥”按 Google 的说法**并不是真正的机密**；是否必须随 token 请求发送取决于客户端，
     如果换码时报 `client_secret is missing`，就把它填进 `ClientSecret`。
5. 不要把客户端 ID / 密钥写进仓库。建议：
   - 复制仓库根目录的 `settings.example.json` 到 `%LOCALAPPDATA%\MailClient\settings.json`，把 `YOUR_GMAIL_CLIENT_ID` / `YOUR_GMAIL_CLIENT_SECRET` 换成你的值；或
   - 使用环境变量 `MAILCLIENT_GMAIL_CLIENT_ID` / `MAILCLIENT_GMAIL_CLIENT_SECRET`（优先于设置文件）。

### Testing 模式的限制

- 只有“测试用户”能登录；授权时会看到“Google 尚未验证此应用”的警告页（点 **高级 → 继续**）。
- **refresh token 7 天后过期**（`invalid_grant`），需要重新登录。这是 Testing 模式的限制，自用 / 开发阶段可接受。
- 个人使用想长期免重登：把发布状态改为“正式版（In production）”但**不提交验证**——仍会显示未验证警告、
  新用户数上限 100，不过 refresh token 不再 7 天过期。（受限范围是否仍会限制，请以 Google 当前政策为准。）

## 对外公开发布的要求（重要）

`https://mail.google.com/` 是 Google 的**受限（Restricted）范围**。若要让任意 Google 用户使用本应用：

1. 完成 **OAuth 应用验证（OAuth App Verification）**：隐私政策页面、应用主页、域名所有权验证、范围用途说明、演示视频。
2. 因为涉及受限范围且数据会被应用存储 / 处理，还需通过 **CASA（Cloud Application Security Assessment）** 第三方安全评估，
   通常每年复评一次，**有费用与周期**。
3. 仅在本机处理数据、不经过任何开发者服务器的桌面客户端，Google 对 CASA 的要求以其当前政策为准，请提前咨询。

在此之前，本功能只适合：自用、开发、或最多 100 个测试用户的小范围内测。
替代方案：让用户自己在 Google 账号里开启两步验证并生成**应用专用密码**，走通用 IMAP（`ImapProviderPresets.GmailImap()`）。

## 如何填写配置

| 配置项 | 来源 | 说明 |
| --- | --- | --- |
| `ClientId` | 控制台“桌面应用”客户端 | 必填 |
| `ClientSecret` | 同上 | 可选；仅当 Google 报缺少 secret 时填写 |
| `EmailAddress` | 要登录的 Gmail | 作为 `login_hint`，并校验登录账号一致；留空则不校验 |
| `Scopes` | 默认 `mail.google.com` + `openid email profile` | 一般不用改 |
| `CredentialVaultKey` | 默认 `oauth:gmail:{AccountId}` | 令牌在凭据库里的键 |
| `UseGmailCategories` | 默认 `true` | 用 Gmail 标签页给邮件分类 |
| `SyncBatchSize` | 默认 50 | 每次同步的最新邮件数 |
| `LoginTimeoutSeconds` | 默认 300 | 等待浏览器登录的时间 |

## Gmail 文件夹映射

依据 IMAP SPECIAL-USE 属性（`FolderSpecialMapper`，同时覆盖中文 / 常见文件夹名）：

| Gmail 文件夹 | 属性 | 应用内 |
| --- | --- | --- |
| `INBOX` | `\Inbox` | 收件箱 |
| `[Gmail]/Sent Mail` | `\Sent` | 已发送 |
| `[Gmail]/Drafts` | `\Drafts` | 草稿 |
| `[Gmail]/All Mail` | `\All` | **归档** |
| `[Gmail]/Spam` | `\Junk` | **垃圾邮件** |
| `[Gmail]/Trash` | `\Trash` | 已删除 |
| `[Gmail]/Starred` | `\Flagged` | 已标记 |
| `[Gmail]/Important` | `\Important` | 保持为自定义文件夹（不当作“已标记”） |

同一次修复也让其它 IMAP 的 `\Archive` / `\Junk` 以及 Microsoft Graph 的 `archive` / `junkemail` 正确映射为归档 / 垃圾邮件。

## 分类（主要 / 交易 / 更新 / 促销）

- 收件箱与“所有邮件”里，服务器支持 `X-GM-EXT-1` 时，每批邮件做 5 次 `X-GM-RAW` 搜索
  （`category:promotions|purchases|updates|social|forums`，限定在本批 UID 内）：
  促销→促销；purchases→交易；updates / social / forums→**更新**；都不命中→主要。
- 任何失败（不支持、网络错误）都**回落到本地规则分类器**（`MailCategoryClassifier`）。
- 顺带修复：此前 IMAP / Graph 同步入库的 `Category` 是 `NULL`，而界面分类标签页按 `Category = x` 过滤，
  所以真实账号的邮件在四个标签页都看不到。现在入库时总会写入分类，且查询把 `NULL`（旧数据）按“主要”处理。

## 测试

- 单元测试（无需网络）：PKCE（含 RFC 7636 向量）、授权 URL、token 响应解析、刷新策略与 2 分钟 skew、refresh token 保留 / 轮换、
  `invalid_grant`、令牌存储、回环监听 + 假浏览器的完整交互流程（含 state 校验、拒绝授权、账号不一致）、文件夹映射、Gmail 分类映射。
- 真实联调（手动，默认跳过）：`LiveGmailTests`。

```powershell
$env:MAILCLIENT_GMAIL_CLIENT_ID     = "xxxx.apps.googleusercontent.com"
$env:MAILCLIENT_GMAIL_EMAIL         = "you@gmail.com"
$env:MAILCLIENT_GMAIL_CLIENT_SECRET = "..."          # 可选
# $env:MAILCLIENT_GMAIL_REFRESH_TOKEN = "..."        # 可选：已有 refresh token 时跳过浏览器
dotnet test tests\MailClient.Tests --filter LiveGmailTests
```

## 已知限制

- 目前**没有 UI** 入口添加 Gmail 账号 / 触发登录（需要 UI 调用 `GmailTokenAcquirer.AcquireAsync(true)` 并把账号写入库）。
- id_token 只解码取 email、不验签（它由 TLS 直接从 Google token 端点取得）。
- “所有邮件”（归档）包含收件箱邮件的副本，同时同步两者时“未读”类汇总可能重复计数。
- 分类检测每批多 5 次搜索；邮件很多时同步会稍慢，可把 `UseGmailCategories` 设为 `false`。
- 出站队列的实际投递 worker 仍未接线（`GmailProvider.SendAsync` 可直接发送）。
