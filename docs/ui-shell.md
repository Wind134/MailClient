# 主界面（三栏 UI）

纯 WinUI 3 / Fluent：标准 `TitleBar`（`ExtendsContentIntoTitleBar`）+ Mica，无自绘窗口按钮。

## 布局

- **左栏**：`NavigationView`（代码按 `MailboxTreeViewModel` 生成）。顶部「新邮件」，其后是收藏夹（所有收件箱 / 已标记 / 未读），再按账户分组：收件箱、草稿、已发送、归档、垃圾邮件、已删除邮件。`PaneDisplayMode=Auto`，窗口窄于 760 时折叠，可用汉堡按钮手动收起。
- **中栏**：标题 + 刷新、`AutoSuggestBox` 搜索、`SelectorBar` 分类（主要/交易/更新/促销）、虚拟化 `ListView`（发件人、主题、预览、时间、未读点、附件/标记图标）。搜索时忽略分类过滤。
- **右栏**：`CommandBar`（回复/全部回复/转发/归档/删除/标记）、`PersonPicture` + 发件人/收件人/日期、附件条、正文。HTML 用 `WebView2`（禁用脚本，拦截页内跳转并交给系统浏览器），无 HTML 时回退为纯文本。
- **分隔条**：自定义 `ColumnSplitter`（WinUI 3 无 GridSplitter）。窄窗口（< 1100）列表/阅读栏二选一，阅读时出现「返回列表」。

## 分层

| 层 | 内容 |
| --- | --- |
| Core | `MailCategoryClassifier`（规则分类：发件人域名/关键词 → 主要/交易/更新/促销）、`SpecialFolder` 新增 Archive / Junk |
| Storage | `MailDatabase.Ui.cs`（UI 查询）、`DemoDataSeeder`（仅测试 / 显式 seed；正式包启动不种子并清理演示账号）、`OutboxSendService` |
| ViewModels | `ShellViewModel`、`MailboxTreeViewModel`、`MessageListViewModel`、`ReadingPaneViewModel`、`ComposeViewModel`（CommunityToolkit.Mvvm 8.4.0） |
| App | `MainWindow`、`ComposeWindow`、`Converters`、`Controls/ColumnSplitter` |

## 写信

「新邮件」打开 `ComposeWindow`：收件人/抄送/主题/正文，发送调用 `ISendService`（`OutboxSendService` 入 outbox，状态 Pending）。回复/全部回复/转发预填同一窗口。

## 账号菜单

账号节点右键 / 更多：`重命名…`、`同步此账号`、`移除账号…`。重命名打开 ContentDialog，空名称回退到邮箱本地部分；写入 `accounts.DisplayName` 后刷新树标题与头像字母。

## 运行

- 数据库：`%LocalAppData%\MailClient\mail.db`，可用环境变量 `MAILCLIENT_DB` 覆盖。
- **正式包**（1.0.8+）：`App` 以 `seedDemoData: false` 初始化；启动时清除固定 Guid 的演示账号（Outlook / QQ 邮箱种子）及其文件夹与邮件，**不删用户自己加的账号**。
- 无打包运行：`dotnet build src\MailClient.App -p:Platform=x64 -p:MailClientUnpackaged=true`，再直接运行 `bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\MailClient.App.exe`。

## 已知缺口

- outbox 暂无投递 worker；刷新只重新读取本地库，不触发真实同步。
- 草稿保存、`.resw` 本地化未做。
- 真实 IMAP/Graph 账户的 Archive/Junk 仍被 `FolderSpecialMapper` 映射为 Custom。
- 回复全部的"我的地址"为启发式推断；WebView2 背景按渲染时主题选择，不随主题实时切换。
