# MailClient Privacy Policy / 隐私权政策

**Effective date / 生效日期：2026-11-10**

This policy explains how the open-source desktop application **MailClient** (Windows display name may appear as 「邮件」) handles information related to you. The official web version of this policy is <https://wind134.github.io/MailClient/privacy.html>. The project homepage is <https://wind134.github.io/MailClient/>.

MailClient's use and transfer of information received from Google APIs to any other app will adhere to [Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy), including the Limited Use requirements.

---

## English

### Who we are and what the product is

MailClient is a **free, open-source Windows desktop email client**. It lets you read, search, write and send your own email from Gmail, Outlook / Microsoft 365, and IMAP accounts (for example QQ or 163) on your own PC.

The project is maintained by GitHub user [Wind134](https://github.com/Wind134). Source code is at <https://github.com/Wind134/MailClient>.

We **do not operate** a backend server that receives, stores, or analyzes your mail, and we **do not provide** a cloud sync service that would require you to upload mailbox data to us. This website is public documentation; it has no user accounts and does not ask you to sign in.

### Data the app accesses

Depending on the account type you add in the desktop app, MailClient may access the following categories of data **on your device**, in order to provide the email-client features you requested:

- **Email address and basic profile.** For a Google account, this comes from the `openid`, `email`, and `profile` scopes (the email address, and basic profile information such as the account name). The app uses this to identify which Google account you authorized and to display that account in the client.
- **Email messages.** Message bodies (HTML or plain text) that you open or that are downloaded so you can read them later offline.
- **Message headers.** Subject, From, To, Cc, Bcc, Date, Message-ID, and similar envelope fields, used to list, sort, search, and thread mail.
- **Attachments.** Files attached to messages you open, save, or attach when composing mail.
- **Folders and labels.** Mailbox folders and Gmail labels (Inbox, Sent, Drafts, Trash, user labels, and so on) so the app can list and organize your mail the way the provider does.
- **Addresses that appear in mail headers.** Names and email addresses that already appear in From / To / Cc / Bcc fields of messages you sync. MailClient does **not** request the Google People or Contacts API; it does not download your address book as a separate data source.
- **Account settings.** Local settings you choose in the app (for example display preferences, or an OAuth client ID you paste yourself).
- **OAuth tokens and credentials.** Access tokens, refresh tokens, and (for IMAP accounts) passwords or app passwords, used only to authenticate to the mail provider you configured.

MailClient does not collect advertising identifiers, analytics events, or crash reports.

### Google scopes requested

When you authorize a Gmail account, MailClient requests these default scopes (defined in the project's `GmailScopes.Default`):

| Scope | Purpose |
| --- | --- |
| `https://mail.google.com/` | Restricted mail scope. Used only to connect to Gmail **IMAP** (`imap.gmail.com:993`) and **SMTP** (`smtp.gmail.com:465`) with **XOAUTH2**, so the app can sync, display, search, organize, and send **your own** email on your device. |
| `openid` | OpenID Connect. Used so the authorization flow can authenticate the Google account and (with the ID token) confirm which account signed in. |
| `email` (`https://www.googleapis.com/auth/userinfo.email`) | Read the Google account's email address so the app can show the correct account and match it to the mailbox you added. |
| `profile` (`https://www.googleapis.com/auth/userinfo.profile`) | Read basic profile information (such as the account name) to label the account in the app. |

The app does not request other Google scopes (for example Drive, Calendar, People/Contacts, or YouTube).

### How each type of data is used

All of the data above is used **only to provide MailClient's email-client features to you on your device**:

- **Email address / basic profile:** display the account in the folder list, confirm that the authorized Google account is the one you intended, and use the address as the IMAP/SMTP username for XOAUTH2.
- **Messages, headers, attachments, folders/labels:** download and cache mail so you can read, search, flag, file, delete, reply, forward, and send mail; keep a local copy of drafts you are writing; and keep the local view in sync with the provider.
- **Addresses in headers:** show senders and recipients, fill reply/forward addressing, and search by `from:` / `to:`.
- **Account settings:** remember how you configured the app on that Windows user profile.
- **OAuth tokens and credentials:** obtain and refresh access so the app can talk to Gmail (or your other provider) without asking you to authorize every time. Tokens are not used for any other product.

Google user data is **not** used for advertising, analytics, personalization for third parties, or any purpose unrelated to providing or improving the user-facing email client.

### Storage and security

Account configuration, mail cache, drafts, and logs are stored **locally** on your Windows PC under:

`%LocalAppData%\MailClient`

This typically includes:

- `mail.db` — a local **SQLite** database of accounts and cached mail
- `settings.json` — app settings (may include OAuth client identifiers that **you** enter)
- `credentials.dat` — tokens and credentials, encrypted with Windows **DPAPI** (`CryptProtectData`, scoped to the **current Windows user**), so they usually can be decrypted only by that same Windows user
- log files and local copies of draft / outbox attachments

Network connections to mail providers and to Google's OAuth token endpoints use **HTTPS / TLS**. Optional update checks call the public GitHub Releases API over HTTPS and do not attach your email address, tokens, or mail content.

We do not operate a server that stores this data.

### Sharing

MailClient **does not share** your mail or Google user data with the developer or with third parties. Data moves **directly** between your device and the mail provider you signed in to (for example Google or Microsoft) or an IMAP/SMTP server you configured. We do **not** sell data, we do **not** use it for advertising, and **no human at MailClient reads your mail** — there is no developer backend from which anyone could do so.

The only optional network call that is not a mail-provider request is an update check to `GET https://api.github.com/repos/Wind134/MailClient/releases/latest`. That request does not include your personal data (only ordinary HTTP headers such as User-Agent). Update installers, if you choose to download them, come from GitHub / GitHubusercontent HTTPS URLs.

### Artificial intelligence, machine learning, and AI NCII

MailClient **does not use AI or machine learning on Google user data**. It does **not** train models on your mail or any other Google user data, does **not** generate images, and does **not** use Google APIs to create or distribute AI-generated non-consensual intimate imagery (**AI NCII**).

The app complies with the [Google APIs Terms of Service](https://developers.google.com/terms) and the [Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy), including **Limited Use**.

MailClient's use and transfer of information received from Google APIs to any other app will adhere to Google API Services User Data Policy, including the Limited Use requirements.

### Retention and deletion

Data stays on your device until you remove it:

- Remove the account inside MailClient to drop that account's local cache and stored tokens (the app also attempts to revoke the Google token at `https://oauth2.googleapis.com/revoke`).
- Uninstall the app. The installer **keeps** `%LocalAppData%\MailClient` unless you check the option to delete local mail data and settings, or you delete that folder yourself.
- Revoke MailClient / the corresponding OAuth client in [Google Account → Third-party apps & services](https://myaccount.google.com/permissions). After you revoke access, existing tokens will stop working.

### Children

MailClient is a general-purpose desktop email client. It is **not directed at children under 13**, and we do not knowingly collect personal information from children. Because the app does not operate a developer backend, we do not collect information from users of any age on our servers.

### Changes to this policy

If this policy changes, we will update this page and the copy in the GitHub repository, and we will change the effective date at the top. The current web address remains <https://wind134.github.io/MailClient/privacy.html>.

### Contact

For privacy questions, please use GitHub Issues:

<https://github.com/Wind134/MailClient/issues>

Do not paste tokens, passwords, or full email bodies in an Issue.

---

## 中文

### 我们是谁、产品是什么

MailClient 是一款**免费、开源的 Windows 桌面邮件客户端**（Windows 上显示名称可为「邮件」）。您可以在自己的电脑上阅读、搜索、撰写并发送来自 Gmail、Outlook / Microsoft 365 以及 IMAP 账号（例如 QQ、163）的邮件。

项目由 GitHub 用户 [Wind134](https://github.com/Wind134) 维护，源代码位于 <https://github.com/Wind134/MailClient>。

我们**不运营**用于接收、存储或分析您邮件内容的后端服务器，也**不提供**要求您把邮箱数据上传给我们的云同步服务。本网站是公开说明文档，没有用户账号，也不要求您在网站上登录。

### 应用会访问哪些数据

根据您在桌面应用中添加的账号类型，MailClient 可能在**您的设备上**访问以下类别的数据，且仅用于提供您所使用的邮件客户端功能：

- **电子邮箱地址与基本资料。** 对 Google 账号而言，这些信息来自 `openid`、`email`、`profile` 范围（邮箱地址，以及账号名称等基本资料）。应用用它们识别您授权的 Google 账号，并在客户端中显示该账号。
- **邮件正文。** 您打开或为离线阅读而下载的邮件正文（HTML 或纯文本）。
- **邮件头。** 主题、发件人、收件人、抄送、密送、日期、Message-ID 等，用于列表、排序、搜索和会话归组。
- **附件。** 您打开、另存，或在撰写邮件时添加的附件。
- **文件夹与标签。** 收件箱、已发送、草稿、已删除以及您自己的标签等，以便按服务商的结构组织邮件。
- **出现在邮件头中的地址。** 已同步邮件的发件人 / 收件人 / 抄送 / 密送字段中的姓名与邮箱。MailClient **不**申请 Google People 或 Contacts API，也不会单独下载您的通讯录。
- **账号设置。** 您在应用中选择的本地设置（例如界面偏好，或您自行填写的 OAuth 客户端 ID）。
- **OAuth 令牌与凭据。** 访问令牌、刷新令牌，以及（IMAP 账号的）密码或应用专用密码，仅用于向您配置的邮件服务商进行身份验证。

MailClient 不收集广告标识、分析事件或崩溃报告。

### 向 Google 申请的范围（Scopes）

当您授权 Gmail 账号时，MailClient 默认申请以下范围（项目中的 `GmailScopes.Default`）：

| 范围 | 用途 |
| --- | --- |
| `https://mail.google.com/` | 受限邮件范围。仅用于通过 **XOAUTH2** 连接 Gmail **IMAP**（`imap.gmail.com:993`）和 **SMTP**（`smtp.gmail.com:465`），以便在您的设备上同步、展示、搜索、整理和发送**您自己的**邮件。 |
| `openid` | OpenID Connect。用于完成授权流程、确认是哪一个 Google 账号完成了授权。 |
| `email`（`https://www.googleapis.com/auth/userinfo.email`） | 读取该 Google 账号的邮箱地址，以便在应用中显示正确账号并与您添加的邮箱对应。 |
| `profile`（`https://www.googleapis.com/auth/userinfo.profile`） | 读取基本资料（如账号名称），用于在应用中标注该账号。 |

应用不申请其他 Google 范围（例如 Drive、Calendar、People/Contacts 或 YouTube）。

### 各类数据如何使用

上述数据**仅用于在您的设备上向您提供 MailClient 的邮件客户端功能**：

- **邮箱地址 / 基本资料：** 在文件夹列表中显示账号，确认授权的 Google 账号符合预期，并将该地址用作 XOAUTH2 的 IMAP/SMTP 用户名。
- **邮件、邮件头、附件、文件夹/标签：** 下载并缓存邮件，供您阅读、搜索、标记、归档、删除、回复、转发和发送；保存您正在撰写的本地草稿；并与服务商保持同步。
- **邮件头中的地址：** 显示发件人与收件人，预填回复/转发地址，以及按 `from:` / `to:` 搜索。
- **账号设置：** 记住该 Windows 用户下的应用配置。
- **OAuth 令牌与凭据：** 获取并刷新访问权限，使应用可以连接 Gmail（或其他服务商），而不必每次都重新授权。令牌不用于其他产品。

Google 用户数据**不会**用于广告、分析、向第三方做个性化，或与提供、改进面向用户的邮件客户端无关的目的。

### 存储与安全

账号配置、邮件缓存、草稿和日志默认保存在您本机：

`%LocalAppData%\MailClient`

其中包括（但不限于）：

- `mail.db`：本地 **SQLite** 邮件与账号数据
- `settings.json`：应用设置（可含您自行填写的 OAuth ClientId / ClientSecret 等配置）
- `credentials.dat`：登录凭据与令牌；使用 Windows **DPAPI**（`CryptProtectData`，**当前 Windows 用户**范围）加密后写入，通常仅同一 Windows 用户可解密
- 同步 / 更新等日志文件，以及草稿、发件箱附件的本地副本目录

与邮件服务商以及 Google OAuth 令牌端点的网络连接使用 **HTTPS / TLS**。若开启「检查更新」，应用会请求 GitHub 公开接口，**不附带**您的邮箱地址、令牌或邮件内容。

我们不运营用于保存这些数据的服务器。

卸载时，安装程序默认**保留**上述用户数据；只有您主动勾选删除本地数据，或手动删除该目录，数据才会被清除。

### 共享

MailClient **不会**把您的邮件或 Google 用户数据分享给开发者或任何第三方。数据只在您的设备与您登录的邮件服务商（例如 Google、Microsoft）或您配置的 IMAP/SMTP 服务器之间**直接**传输。我们**不出售**数据，**不用于广告**，也**没有人**在 MailClient 一侧阅读您的邮件——本项目没有可供他人查看这些数据的开发者后端。

除连接邮件服务商之外，唯一可选的网络请求是检查更新：

`GET https://api.github.com/repos/Wind134/MailClient/releases/latest`

该请求**不附带**您的邮箱地址、令牌或其他个人数据（仅常规 HTTP 请求头，例如 User-Agent）。下载更新安装包时，仅从 GitHub / GitHubusercontent 的 HTTPS 地址获取发布资源。

### 人工智能、机器学习与 AI NCII

MailClient **不会对 Google 用户数据使用人工智能或机器学习**，**不会**用您的邮件或其他 Google 用户数据训练模型，**不会**生成图像，也**不会**使用 Google API 创建或传播人工智能生成的非自愿亲密影像（**AI NCII**）。

本应用遵守 [Google APIs Terms of Service](https://developers.google.com/terms) 与 [Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy)，包括 **Limited Use（有限使用）** 要求。

MailClient's use and transfer of information received from Google APIs to any other app will adhere to Google API Services User Data Policy, including the Limited Use requirements.

### 保留与删除

数据会留在您的设备上，直到您自行删除：

- 在 MailClient 内移除对应邮箱账号，以清除该账号的本地缓存与已存储令牌（应用也会尽量向 `https://oauth2.googleapis.com/revoke` 撤销 Google 令牌）。
- 卸载应用。除非您勾选删除本地邮件数据与设置，或手动删除 `%LocalAppData%\MailClient`，安装程序会**保留**该目录。
- 在 [Google 账号 → 第三方应用与服务的访问权限](https://myaccount.google.com/permissions) 中撤销 MailClient / 相应 OAuth 客户端的访问。撤销后，已有令牌将失效。

### 儿童

MailClient 是通用桌面邮件客户端，**并非面向 13 岁以下儿童**，我们也不会在知情情况下收集儿童的个人信息。由于应用没有开发者后端，我们不会在自己的服务器上收集任何年龄用户的信息。

### 政策变更

若本政策有更新，我们会修改本页以及 GitHub 仓库中的副本，并更新文首的生效日期。现行网址仍为 <https://wind134.github.io/MailClient/privacy.html>。

### 联系方式

如有隐私相关问题，请通过 GitHub Issues 联系：

<https://github.com/Wind134/MailClient/issues>

请勿在 Issue 中粘贴令牌、密码或完整邮件正文。
