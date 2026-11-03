# Microsoft Graph / Outlook 接入说明

本客户端通过 **MSAL.NET 公共客户端** + **Microsoft Graph** 读写 Outlook / Microsoft 365 邮箱（`ProviderId = microsoft.graph`）。

## 包版本（已钉死并缓存到 `local-nuget`）

| 包 | 版本 |
|---|---|
| Microsoft.Identity.Client | 4.92.0 |
| Microsoft.Identity.Client.Broker | 4.92.0 |
| Microsoft.Graph | 6.9.0 |
| Microsoft.Graph.Core | 4.0.1（传递依赖） |

## Entra ID 应用注册（必须）

1. 打开 [Microsoft Entra 管理中心](https://entra.microsoft.com/) → **应用注册** → **新注册**。
2. 名称自定；支持的帐户类型建议选「任何组织目录中的帐户和个人 Microsoft 帐户」（对应租户 `common`）。
3. 重定向 URI：平台选 **移动和桌面应用程序**，至少填写：
   - `http://localhost`（**必填**：本客户端默认用系统浏览器登录）
   - `ms-appx-web://microsoft.aad.brokerplugin/<你的客户端ID>`（建议补上；个别环境仍走 WAM 时需要）
   - 可选：`https://login.microsoftonline.com/common/oauth2/nativeclient`
4. 注册完成后复制 **应用程序（客户端）ID** → 填入 `MicrosoftGraphAccountConfig.ClientId`。
5. **不要**创建客户端密码 / 证书（公共客户端无 secret）。
6. **API 权限** → Microsoft Graph → **委托权限** 添加：
   - `User.Read`
   - `Mail.Read`
   - `Mail.ReadWrite`
   - `Mail.Send`
   - `offline_access`
7. 若租户启用了管理员同意，由管理员点「为 [租户] 授予管理员同意」；个人 MSA 一般用户可自行同意。
8. **身份验证**页确认「允许公共客户端流」为 **是**（设备代码 / WAM 等场景需要）。

## 在代码里配置 ClientId

```csharp
var config = new MicrosoftGraphAccountConfig(
    AccountId: Guid.NewGuid(),
    EmailAddress: "you@outlook.com", // 可先占位，登录后以 Graph /me 为准
    DisplayName: "Outlook",
    ClientId: "你的-应用-客户端-ID",
    TenantId: "common",              // 单租户可改成具体 tenant id
    RedirectUri: "http://localhost", // 可省略，默认即此
    CredentialVaultKey: null,        // 默认 msal:graph:{AccountId}
    PreferDeltaSync: true,
    UseWamBroker: false);            // 默认系统浏览器；true 时先试 WAM，失败再回退浏览器

var store = /* ICredentialStore：生产用 Credential Locker，测试用 InMemory */;
var provider = new MicrosoftGraphProvider(config, store);
await provider.ConnectAsync();
```

环境变量联调（测试项目）：

```text
MAILCLIENT_GRAPH_CLIENT_ID=<客户端ID>
MAILCLIENT_GRAPH_TENANT_ID=common
```

## 交互登录方式（默认系统浏览器）

本客户端 **默认 `UseWamBroker: false`**，交互登录走系统浏览器 + `http://localhost`，避开 WAM 在部分机器上的 `ApiContractViolation`（2156265475）和「需要 Internet」(0x800704CF) 误报。

若显式开启 WAM，仍须提供主窗口 HWND，且建议在 Entra 登记 broker 重定向 URI：
`ms-appx-web://microsoft.aad.brokerplugin/<ClientId>`。WAM 失败时会自动回退系统浏览器一次。

## WAM 父窗口句柄（仅 UseWamBroker=true）

Windows 上启用 WAM 时，交互登录**必须**提供主窗口 HWND：

- `App` 在 `MainWindow.Activate()` 后用 `WinRT.Interop.WindowNative.GetWindowHandle` 拿到句柄，经 `Func<IntPtr>` 注入 `AccountSetupService` / `AccountProviderFactory`。
- `MicrosoftGraphTokenAcquirer` 在 `PublicClientApplicationBuilder` 上挂 `WithParentActivityOrWindow(Func<IntPtr>)`，并在 `AcquireTokenInteractive` 上调用 **`WithParentActivityOrWindow(IntPtr)`（传值，不是传 Func）**。
- 交互请求构建器没有 `Func<IntPtr>` 重载；若把 Func 直接传入会命中 `object` 重载，WAM 报 `window_handle_required`。
- 从 ContentDialog / 添加账号向导发起登录时，仍应使用主窗口 HWND（不要用对话框句柄）。

## 能力与限制（相对 IMAP）

| 能力 | Graph 提供商 | Generic IMAP |
|---|---|---|
| Outlook.com / M365 登录 | OAuth（MSAL），推荐 | 可用但微软正收紧基本认证 |
| 文件夹列表 | `me/mailFolders` | LIST + SPECIAL-USE |
| 增量同步 | 优先 messages/delta（deltaLink 存 `folders.SyncCursor`） | UID 游标 |
| 发送 | `me/sendMail`（HTML + 文件附件） | SMTP |
| 已读/旗标回写 | MVP 未做 | MVP 未做 |
| QQ / 163 等 | 不支持（请用 IMAP） | 支持 |

其它限制：

- 令牌缓存经 MSAL `SerializeMsalV3` → Base64 → `ICredentialStore`，**不要**把 refresh token 明文写入 SQLite secrets 表。
- 大附件受 Graph 上传限制；MVP 用 `fileAttachment` 直传，超大附件需后续改 upload session。
- 分类（Primary/Transactions…）仍只是 UI 壳，不在本提供商实现。
- Gmail OAuth 不在本轮范围。

## 相关类型

- `MailClient.Core.MicrosoftGraphAccountConfig` / `MicrosoftGraphScopes`
- `MailClient.Auth.MicrosoftGraphTokenAcquirer` / `MsalTokenCacheHelper`
- `MailClient.Providers.MicrosoftGraphProvider`
- `MailClient.Providers.Graph.GraphSyncPipeline`


## 开源仓库里的占位符

仓库不包含真实 Microsoft 客户端 ID。请复制根目录 `settings.example.json` 到 `%LOCALAPPDATA%\MailClient\settings.json`，把 `YOUR_MSAL_CLIENT_ID` 换成你在 Entra ID 注册的「公共客户端 / 移动和桌面」应用 ID；或设置环境变量 `MAILCLIENT_GRAPH_CLIENT_ID`（可选 `MAILCLIENT_GRAPH_TENANT_ID`，默认 `common`）。切勿把填好的设置文件提交进 Git。
