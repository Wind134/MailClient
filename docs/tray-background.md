# 托盘后台运行

## 行为

- 设置 → **常规** →「关闭主窗口时保留在后台」（**默认开**）。
- **开**：点主窗口关闭（X）→ 取消关闭、`AppWindow.Hide()`，进程继续跑；同步 / IDLE / 发件箱 / 待写回 / 存储维护 / Windows 通知都还在。
- **关**：点关闭 → 和以前一样真正退出。
- 通知区域图标（托盘）：标题「邮件」，左键 / 双击 → 打开并前置主窗口；右键菜单 → **打开** / **同步全部** / **退出**。
- 「退出」会关掉写信窗口、移除托盘图标、停掉后台服务并退出进程。
- 首次关到托盘时弹一次提示通知（之后用 `TrayHintShown` 记住，不再打扰）。

## 单实例

- `Program.Main` 里用 `AppInstance.FindOrRegisterForKey("MailClient.Main")`。
- 软件已在托盘后台时再从开始菜单启动：激活重定向到已有进程并 `BringToFront`，不会开第二个进程。
- 点新邮件通知唤起时：若进程还在，走现有 `NotificationInvoked` / 重定向激活，并还原窗口。

## 实现说明（打包 MSIX）

Windows App SDK **没有**一等托盘 API。本实现用：

- `AppWindow.Closing` 取消关闭 + `Hide`
- Win32 `Shell_NotifyIcon` + 消息窗口收回调 + `TrackPopupMenu` 右键菜单
- 图标：`Assets/AppIcon.ico`（包内路径）

限制：

- 托盘图标依赖 Shell 通知区域；部分企业策略会隐藏通知区域图标，需要用户在「任务栏设置」里允许「邮件」显示。
- 远程桌面 / 自动化截图时托盘气泡不一定能可靠捕获。
- 开机启动见下一节。不需要管理员权限。

## 相关设置字段

`settings.json`：

- `KeepRunningInBackground`（bool，默认 true）
- `LaunchAtStartup`（bool，默认 false）
- `TrayHintShown`（bool）

## 开机启动

- 设置 → **常规** → **启动** →「开机时自动启动」（**默认关**）。
- **开**：登录 Windows 后静默启动，不弹出主窗口。托盘图标仍在，同步 / IDLE / 发件箱 / 通知继续。点托盘图标或再从开始菜单打开，才会显示窗口。
- 免打包（exe 安装版 / 绿色版）：写当前用户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\MailClient`，命令是 `"<exe>" --silent`。卸载时安装程序会删掉这项。
- MSIX：用包清单里的 `StartupTask`（`MailClientStartup`，同样带 `--silent`）。若用户在「设置 → 应用 → 启动」里关过，应用不能自行再打开，设置页会说明并给出跳转。
- 任务管理器把启动项标成禁用后，这里的开关显示为关；再打开会清掉禁用标记。
- 已经在跑时又收到一次 `--silent` / StartupTask 激活：不把窗口拽到前台。
