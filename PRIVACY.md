# 隐私权政策 / Privacy Policy

**生效日期 / Effective date：2026-11-09**

本文说明开源桌面应用 **MailClient（「邮件」）** 如何处理与您相关的信息。本政策同时可作为 Google OAuth 同意屏幕所需的隐私权政策网址内容。

网页版：<https://wind134.github.io/MailClient/privacy.html>（项目首页：<https://wind134.github.io/MailClient/>）。

---

## 中文

### 我们是谁、产品是什么

MailClient 是一款在您自己的 Windows 电脑上运行的**本地桌面邮件客户端**。项目由 GitHub 用户 [Wind134](https://github.com/Wind134) 维护，源代码位于 <https://github.com/Wind134/MailClient>。

我们**不运营**用于接收、存储或分析您邮件内容的后端服务器，也**不提供**要求您把邮箱数据上传给我们的云同步服务。

### 数据存储在哪里

账号配置、邮件缓存、草稿、日志等默认保存在您本机：

`%LocalAppData%\MailClient`

其中包括（但不限于）：

- `mail.db`：本地 SQLite 邮件与账号数据
- `settings.json`：应用设置（可含您自行填写的 OAuth ClientId / ClientSecret 等配置）
- `credentials.dat`：登录凭据与令牌；使用 Windows **DPAPI**（`CryptProtectData`，**当前 Windows 用户**范围）加密后写入，通常仅同一 Windows 用户可解密
- 同步 / 更新等日志文件，以及草稿、发件箱附件的本地副本目录

卸载时，安装程序默认**保留**上述用户数据；只有您主动勾选删除本地数据，或手动删除该目录，数据才会被清除。

### 我们收集什么

**我们不收集、不上传、不出售您的个人数据。**

本应用**没有**内置遥测、分析、广告标识或崩溃上报服务；不会把您的邮件内容、通讯录或账号信息发送给开发者。

### 应用会连接哪些服务

应用只会为完成您主动配置的邮件功能，**直接**连接您登录的邮件服务商，例如：

- Google（Gmail OAuth / IMAP / SMTP）
- Microsoft（Microsoft Graph / Outlook）
- 您配置的其他 IMAP/SMTP 服务器（如 QQ、163/126 等）

此外，若开启「检查更新」，应用会请求 GitHub 公开接口：

`GET https://api.github.com/repos/Wind134/MailClient/releases/latest`

该请求**不附带**您的邮箱地址、令牌或其他个人数据（仅常规 HTTP 请求头，例如 User-Agent）。下载更新安装包时，仅从 GitHub / GitHubusercontent 的 HTTPS 地址获取发布资源。

### Google 用户数据的使用（Limited Use）

若您使用 Google 账号登录，MailClient 仅将 Google 用户数据用于**在您的设备上收发与管理您自己的邮件**（例如通过 OAuth 取得令牌后连接 Gmail IMAP/SMTP、同步与展示邮件、发送您撰写的邮件）。

我们遵守 [Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy)，包括 **Limited Use（有限使用）** 要求：不会将 Google 用户数据用于广告，不会出售，不会用于与提供或改进面向用户的邮件客户端功能无关的目的，也不会在未经您授权的情况下向无关第三方披露。

Google 相关令牌保存在本机 `credentials.dat`（DPAPI 加密），不会上传到开发者服务器（本项目也没有此类服务器）。

### 您如何控制与撤销

- 在 [Google 账号 → 第三方应用与服务的访问权限](https://myaccount.google.com/permissions) 中撤销 MailClient / 相应 OAuth 客户端的访问
- 在应用内移除对应邮箱账号
- 卸载应用；如需清除本地缓存与凭据，请在卸载时勾选删除本地数据，或手动删除 `%LocalAppData%\MailClient`

### 联系方式

如有隐私相关问题，请通过 GitHub Issues 联系：

<https://github.com/Wind134/MailClient/issues>

请勿在 Issue 中粘贴令牌、密码或完整邮件正文。

---

## English (summary)

**Effective date: 2026-11-09**

MailClient is a **local Windows desktop email client**. Mail, account settings, and tokens stay on your device under `%LocalAppData%\MailClient`. Tokens in `credentials.dat` are protected with Windows **DPAPI** (`CryptProtectData`, current Windows user). We do **not** operate a backend that collects your mail, and the app includes **no telemetry/analytics**.

The app connects **directly** to the mail providers you sign in to (Google, Microsoft, or your IMAP/SMTP servers). Optional update checks call the public GitHub Releases API (`api.github.com`) and do **not** send your personal data.

Google user data is used only to send, receive, and manage **your own** email on your device, in accordance with the [Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy), including **Limited Use**.

You may revoke access in your Google Account permissions, remove the account in the app, uninstall, and delete local data. Contact: <https://github.com/Wind134/MailClient/issues>.
