# 1.0.13.0

## 关闭窗口后保留在后台

- 点主窗口关闭默认**不退出**：藏到通知区域（托盘），同步、IDLE、发件箱写回和 Windows 通知继续工作。
- 托盘图标「邮件」：左键打开；右键「打开 / 同步全部 / 退出」。
- 设置 → **常规** →「关闭主窗口时保留在后台」（默认开；关掉则关闭=退出）。
- 单实例：后台运行时再次启动会唤起现有窗口。
- 点新邮件通知会还原窗口（若进程仍在）。
- 首次关到托盘有一条一次性提示。

实现与限制见 [tray-background.md](tray-background.md)。

## 测试 / 文档

- 新增：`CloseToTrayPolicy` 与设置往返测试。
- 文档：`docs/tray-background.md`、本文。

Package: artifacts/msix/MailClient.App_1.0.13.0_x64_Test/MailClient.App_1.0.13.0_x64.msix
