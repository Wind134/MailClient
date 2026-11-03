# exe 安装程序

从 1.0.14.0 起，发布形式由自签名 MSIX 改为 **exe 安装程序 + 绿色版 zip**。MSIX 脚本（`scripts\build-msix.ps1`）仍保留，可继续构建。

## 产物

`scripts\build-installer.ps1` 输出到 `artifacts\release\`：

- `MailClient-Setup-<版本>.exe`：单文件安装程序（约 140 MB）。
- `MailClient-<版本>-win-x64-portable.zip`：自包含绿色版。
- `SHA256SUMS.txt`：校验和。

## 构成

```text
MailClient-Setup.exe      ← installer\MailClient.Setup.Bootstrap（net472 WinForms，嵌入 payload.zip，先显示启动画面）
 └ payload.zip            ← 自包含应用 + 安装程序本体
    ├ MailClient.Setup.exe ← installer\MailClient.Setup（WinUI 3，Mica、浅/深色、动画进度；也是卸载程序）
    └ MailClient.App.exe 等
installer\MailClient.Setup.Core   ← net8.0，可测试的逻辑：参数解析、路径校验、复制/清单、快捷方式、注册表、结束进程
```

- 用 net472 做外壳，是因为它随 Windows 自带，双击即可运行，不需要先装 .NET 或 Windows App Runtime；它把 payload 解压到 `%TEMP%\MailClient-Setup-xxxx`，运行里面的 WinUI 安装程序，结束后清理。
- 安装程序与应用共用同一份自包含运行时（应用文件优先），所以只需一份。
- 安装器项目不在 `MailClient.sln` 里，由 `build-installer.ps1` 单独构建。

## 安装做了什么（每用户，无需管理员）

1. 目录：默认 `%LocalAppData%\Programs\MailClient`。安装目录会被校验：不允许盘符根目录、系统目录，以及 `%LocalAppData%\MailClient`（用户数据）本身或其上级/内部。
2. 若已安装并在运行：先关闭**安装目录里的** `MailClient.App.exe`（含托盘后台），不会碰其他位置（例如 MSIX 版）的进程。
3. 复制文件，同时写 `install-manifest.txt`（卸载只会删清单中的文件）和 `setup-files.txt`（卸载程序自身用到的文件）。
4. 开始菜单快捷方式 `邮件.lnk`，带 `System.AppUserModel.ID = MailClient.Desktop`；可选桌面快捷方式。
5. `HKCU\Software\Classes\AppUserModelId\MailClient.Desktop`（显示名「邮件」、图标）——Windows 通知需要它。
6. `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\MailClient`：名称、版本、发行者、安装位置、图标、大小、`UninstallString`。

升级即对同一目录重复以上步骤，并清理旧清单里已不存在的文件。

## 卸载

`MailClient.Setup.exe /uninstall`（「已安装的应用」里的卸载就是它）。界面同安装；选项「同时删除我的邮件数据和设置」**默认不勾选**。

- 只按清单删除文件，之后删除空目录；
- 卸载程序自身正在使用的文件（`setup-files.txt`）无法在运行中删除，改由一个自删除的临时批处理在退出后清理；
- 不勾选时 `%LocalAppData%\MailClient` 完全不动。

## 命令行参数

| 参数 | 含义 |
| --- | --- |
| `/S` 或 `/silent` | 静默安装，不显示界面（默认不启动应用）。 |
| `/D=路径` 或 `/dir=路径` | 安装目录（默认 `%LocalAppData%\Programs\MailClient`；已安装时静默升级沿用原目录）。 |
| `/nodesktop` | 不创建桌面快捷方式（默认创建）。 |
| `/run` | 安装完成后启动应用。 |
| `/uninstall` | 卸载（「已安装的应用」里的卸载命令就是它）。 |
| `/deletedata` | 卸载时同时删除 `%LocalAppData%\MailClient`（默认保留）。 |
| `/preview=welcome\|progress\|done\|error\|uninstall\|uninstall-progress\|uninstall-done` | 只预览界面，不做任何改动（开发用）。 |

## 免打包（unpackaged）应用

- 自带 .NET 与 Windows App Runtime，不需要另装运行时；体积约 140 MB。
- 通知、托盘、单实例、图标与 MSIX 版一致：通知用的 AUMID `MailClient.Desktop` 由安装器写入的开始菜单快捷方式和注册表项提供。
- 应用文件里需要有 `Assets\*.png` / `*.ico`（免打包运行时用绝对路径读取图标），所以发布时把它们作为普通文件带上。
- Windows App SDK 2.x 自包含发布缺 `Microsoft.WindowsAppRuntime.Insights.Resource.dll`，缺它 `AppNotificationManager.Register()` 会抛 `0x8007007E`、通知失效。`build-installer.ps1` 会从 NuGet 缓存里 Runtime 包的框架 MSIX 中把它取出放进应用目录。用 `dotnet build` 直接运行免打包版时没有这一步，通知在那种开发场景下不可用，请用安装包或脚本产物验证通知。
- 免打包时不要调用 `AppNotificationManager.Unregister()`。
- 数据仍在 `%LocalAppData%\MailClient`，与 MSIX 版相同，**不用迁移**。
- **与 MSIX 版同时安装**：互不覆盖。但单实例键 `MailClient.Main` 在 MSIX 进程与免打包进程之间**不共享**，两者可同时运行并同时读写同一份数据（同步/发件箱可能重复处理）。欢迎页在检测到 MSIX 版时会提示；建议确认 exe 版可用后卸载 MSIX 版。

## 签名与 SmartScreen

目前没有代码签名证书，所以首次运行会出现 SmartScreen：「更多信息 → 仍要运行」。拿到证书后，只需在 `build-installer.ps1` 末尾对 Setup exe 与 `MailClient.App.exe` 调 `signtool`，即可消除警告（EV 证书立刻消除，普通证书需积累信誉）。

## 发布流程（CI）

`.github/workflows/release.yml`：推送 `v*` 标签 → Windows runner 上还原、跑单元测试（排除 `Live*`）、运行 `build-installer.ps1 -SkipTests`、用 `docs/release-<版本>*.md` 作为发布说明，创建 GitHub Release 并附上 `artifacts/release` 里的文件。也可手动触发（只构建并上传产物，不发布）。

## 应用内检查更新（GitHub Releases）

应用会从 GitHub Releases 检查新版本（见 [updates.md](updates.md)）。**仓库地址只在一个地方定义**：`src/MailClient.Core/Updates/UpdateConfig.cs` 里的

```csharp
public const string Repository = "Wind134/MailClient";   // owner/repo
```

换成别的仓库（例如转移所有者或改名后）只需改这一行，重新构建即可；检查地址和「查看发布页」链接都由它派生。发布时必须保证 Release 里带有 `MailClient-Setup-<版本>.exe` 和 `SHA256SUMS.txt`，并且标签形如 `v1.2.3`（CI 的 `Verify update metadata` 一步会检查）。
