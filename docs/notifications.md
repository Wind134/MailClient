# Windows 新邮件通知

软件接入了 Windows 系统通知（Windows App SDK 的 `AppNotificationManager`），收到新邮件时会在屏幕右下角弹出通知，也会留在操作中心。

## 什么时候会弹

只有同时满足下面全部条件才会弹（逻辑集中在 `MailClient.Core.Notifications.NewMailPolicy`，有单元测试）：

- 邮件在**收件箱**里，状态**未读**，并且是**这一轮同步新发现**的（不是本来就在本地库里的）。
- **不是**新添加账号时的第一次同步（否则一次会涌出几百条）。
- 邮件日期在最近 2 天内（防止服务器补回来的老邮件刷屏）。
- 发件人**不是你自己**（本账号的地址和别名）。
- 设置里的“新邮件通知”是开着的（默认开）。
- Windows 自己没有关掉本应用的通知（系统设置 → 通知，或专注助手 / 勿扰）。

数量：一轮里最多逐封弹 3 条（发件人、主题、预览；带“标为已读”和“删除”按钮）；超过 3 封合并成一条“你有 N 封新邮件”。

## 点通知会怎样

| 操作 | 结果 |
| --- | --- |
| 点通知正文（单封） | 窗口还原并置前，切到该邮件所在文件夹并选中它 |
| 点汇总通知 | 窗口置前，打开对应账号的收件箱 |
| 点“标为已读” | 不弹窗口，走和界面里一样的待回写队列（`MessageActions` → `PendingOps`），之后同步到服务器，并清掉这条通知 |
| 点“删除” | 同上，邮件移到已删除 |

软件已关闭时点通知：Windows 会通过清单里声明的 COM 激活器重新启动软件。点正文则正常打开窗口并定位邮件；点按钮则**不显示窗口**，等回写队列发完（最多 20 秒）后自动退出。

## 设置与测试

左侧窗格底部 **“设置”** → 新邮件通知开关（保存在 `settings.json` 的 `NotificationsEnabled`）；对话框里还显示 Windows 当前是否允许本应用发通知、一个打开 Windows 通知设置的链接，以及 **“发送测试通知”** 按钮，用来确认整条链路是通的。

## 实现位置

- 策略：`src/MailClient.Core/Notifications/NewMailPolicy.cs`、`ToastAction.cs`（通知参数的编码 / 解析）
- 发现新邮件：`MailDatabase.Notifications.cs`（同步前后各取一次收件箱快照比对）、`AccountSyncService.NewMailDetected`
- 系统通知：`src/MailClient.App/NotificationService.cs`；接线在 `App.xaml.cs`（启动登记、冷启动激活、事件分发、退出注销）
- 打包版必需：`Package.appxmanifest` 里的 `windows.toastNotificationActivation` 和 `windows.comServer`（两处使用同一个 CLSID；ExeServer 的 `Executable` 必须是真实文件名 `MailClient.App.exe`，写 `$targetnametoken$` 不会被替换，登记时会报 “Caller RegistrationProcess is not the same as manifest defined COM Server”）
- 测试：`NewMailPolicyTests`、`NewMailDetectionTests`、`ToastActionTests`

## 已知限制

- 只有**软件在运行时**（包括最小化）才会收到并弹出通知；没有托盘常驻 / 后台服务，关闭窗口后不再同步，也就没有通知。
- 软件窗口在前台时同样会弹（没有“窗口聚焦就不弹”的判断）。
- 通知标题栏显示的是包名“MailClient.App”和默认图标（清单里的显示名与图标是模板值）。
- 以管理员身份运行软件时 Windows 不会显示通知。
- 未打包（免打包）运行时没有包标识，通知登记可能失败，设置里会显示原因；通知功能以 MSIX 打包版为准。

> 1.0.11.0: package DisplayName is now 邮件; Assets icons replaced.
