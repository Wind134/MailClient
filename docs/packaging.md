# 打包、运行与发布

## 1. 现象与根因

**现象**：打包版（默认的单项目 MSIX）直接运行 `src\MailClient.App\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\MailClient.App.exe` 时，进程在 `Main` 之前崩溃；免打包版（`-p:MailClientUnpackaged=true`）正常。

**根因**：该 exe 是 MSIX 打包应用，需要**包标识（package identity）**才能激活 Windows App SDK 的 WinRT 类。直接从 `bin\` 双击启动时进程没有包标识，而 Windows App SDK 的自动初始化器（`Microsoft.WindowsAppSDK.Foundation` 的 `DeploymentManagerAutoInitializer`）在模块初始化阶段就会激活 `DeploymentInitializeOptions`，激活失败即抛 `REGDB_E_CLASSNOTREG`。不是运行时没装，也不是我们代码的问题。

**证据**（开发机，2026-10-31 复现）：

- 事件查看器 · Application · `.NET Runtime` 1026：

  ```
  System.TypeInitializationException: The type initializer for '<Module>' threw an exception.
   ---> System.Runtime.InteropServices.COMException (0x80040154): REGDB_E_CLASSNOTREG
     at WinRT.ActivationFactory.Get(String typeName)
     at Microsoft.Windows.ApplicationModel.WindowsAppRuntime.DeploymentInitializeOptions..ctor()
     at ...DeploymentManagerCS.AutoInitialize.get_Options()   (DeploymentManagerAutoInitializer.cs:44)
  ```

  类名 = `Microsoft.Windows.ApplicationModel.WindowsAppRuntime.DeploymentInitializeOptions`；`Application Error` 1000 里 `Faulting 包全名` 为空，退出码 `0xE0434352`（CLR 未处理异常），即**进程没有包标识**。
- 本机运行时是齐的：`Get-AppxPackage Microsoft.WindowsAppRuntime.2` 有 2.2.0 / 2.3.1 / 2.4.0 / 2.5.1 / 2.6.4（x64），清单要求 `Microsoft.WindowsAppRuntime.2 >= 2.3.1.0`，满足。同一套代码以「免打包 + 框架依赖」方式（`-p:MailClientUnpackaged=true -p:MailClientFrameworkDependent=true`）运行时，进程加载的是 `C:\Program Files\WindowsApps\Microsoft.WindowsAppRuntime.2_2.6.4.0_x64__8wekyb3d8bbwe\Microsoft.UI.Xaml.dll`，主窗口和演示数据正常——说明缺的只是包标识。
- 生成的 `AppxManifest.xml` 里没有我们自己的 WinRT / COM 进程内类；唯一的 `windows.activatableClass.inProcessServer` 是 WebView2 的 `CoreWebView2EnvironmentOptions`（由 SDK 自动生成，正常）。`WindowsPackageType` 默认（MSIX）、`EnableMsixTooling=true`，免打包分支才设 `WindowsPackageType=None`，配置本身没有错。
- 正确启动方式 = 先注册包再按 AUMID 启动。开发机上 `Add-AppxPackage -Register …\AppxManifest.xml` 返回 `0x80073CFF`（要安装此应用程序，需要有 Windows 开发者许可证或启用旁加载的系统）：`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock` 下没有 `AllowDevelopmentWithoutDevLicense`，即**开发人员模式未开启**，且当前 shell 非管理员。

## 2. 开发流程

| 目的 | 命令 | 需要 |
| --- | --- | --- |
| 日常调试 / 截图 | `scripts\run-unpackaged.ps1`（`-DbPath C:\tmp\demo.db` 用独立演示库） | 无 |
| 验证本机 Windows App Runtime | `scripts\run-unpackaged.ps1 -FrameworkDependent` | 已装 `Microsoft.WindowsAppRuntime.2 >= 2.3.1` |
| 带包标识的开发运行 | `scripts\run-packaged.ps1` | **开发人员模式**（设置 → 系统 → 开发者选项） |
| 生成可分发的 `.msix` | `scripts\build-msix.ps1` | 无（安装时才需要信任证书） |
| 移除开发注册 | `scripts\run-packaged.ps1 -Unregister` | |

`run-packaged.ps1` 做的事：只读检查开发人员模式（关闭则给出说明后以退出码 2 结束，**不改任何系统设置**）→ 构建 → 读取生成的 `AppxManifest.xml`，核对框架依赖已安装（缺则提示，加 `-InstallRuntime` 才会从 NuGet 缓存的 `microsoft.windowsappsdk.runtime\*\tools\MSIX\win10-x64` 安装匹配版本）→ `Add-AppxPackage -Register` → `explorer.exe shell:AppsFolder\<PackageFamilyName>!App` 启动。

注意：

- 在同一份 `obj` / `bin` 里切换「打包 / 免打包 / 框架依赖」会残留旧的 `runtimeconfig.json`、`AppxManifest.xml`。脚本在 `obj\.build-mode` 记录上次模式，变化时自动清空 `src\MailClient.App\obj` 与 `bin`；用 VS 或手工 `dotnet build` 切换模式时请自己删这两个目录。
- 打包运行时，`%LocalAppData%\MailClient` 的写入会被 MSIX 虚拟化到 `%LocalAppData%\Packages\<PFN>\LocalCache\Local\MailClient`，所以打包版与免打包版用的是**两份**本地库 / `sync.log`。`MAILCLIENT_DB` 对 AUMID 启动的进程无效（环境变量不会从脚本传给 explorer）。
- 在 VS 里 F5 选 “MailClient.App (Package)” 同样会走注册 + AUMID 启动，同样需要开发人员模式。

## 3. 没有开发人员模式时

`scripts\build-msix.ps1` 会：

1. 生成测试证书（Subject = 清单里的 `Identity/@Publisher`，现为 `CN=AppPublisher`），导出 `dev.pfx` / `dev.cer` 到 `%LocalAppData%\MailClient\devcert`，并立即从证书库删除（不信任、不安装任何东西）；
2. 构建 MSIX 并用 `signtool` 签名，产物在 `artifacts\msix\MailClient.App_<版本>_<架构>_<配置>_Test\`，同目录 `Dependencies\x64\Microsoft.WindowsAppRuntime.2.msix` 是框架依赖包；
3. 打印安装步骤。Windows 会拒绝不受信任的发布者（`signtool verify /pa` 报 “terminated in a root certificate which is not trusted”），需要**管理员**把 `dev.cer` 导入 `LocalMachine\TrustedPeople` 一次，再 `Add-AppxPackage`。这改动的是机器级信任库，所以脚本不自动执行。

## 4. 正式发布需要什么

- **签名证书**：旁加载分发需要受信任的代码签名证书（企业内部 CA 或公共 CA），`Package.appxmanifest` 的 `Identity/@Publisher` 必须与证书 Subject 完全一致，并把 `Identity/@Name`、`Version`、`PublisherDisplayName` 换成正式值（现在是模板默认值）。`build-msix.ps1 -PfxPath … -PfxPassword …` 可直接用正式证书签名。上架 Microsoft Store 则不用自己签名，由 Store 重新签名，Identity 用 Partner Center 里保留的值。
- **运行时依赖**（二选一）：
  1. *框架依赖*（现状）：包声明依赖 `Microsoft.WindowsAppRuntime.2 >= 2.3.1`。Store 安装会自动拉取；旁加载时用户必须先装框架包（随 `.msix` 一起发 `Dependencies\<arch>\Microsoft.WindowsAppRuntime.2.msix`，或让 `.appinstaller` 声明依赖），否则安装直接失败。
  2. *自包含*：`WindowsAppSDKSelfContained=true`（免打包构建已这样用），运行时随应用一起发，不依赖系统框架包；体积更大，需自己负责安全更新。
- **免打包分发**：没有包标识就没有 MSIX 的安装 / 更新 / 卸载、AppData 隔离，也不能用依赖包标识的 API（如部分通知、后台任务）。如果走 exe + 安装程序，用自包含或在启动时通过 Bootstrapper 加载已安装的框架包。
- **Store 与旁加载**：Store 省去证书、框架依赖分发和更新；旁加载需要自己处理证书信任（企业可通过组策略 / Intune 下发）、`.appinstaller` 自动更新和 Windows 的“允许旁加载”策略。

## 5. 已知缺口

- 本机开发人员模式关闭且无管理员权限，所以「注册 + AUMID 启动」这一步没有在 开发机上实际跑通（脚本在开发人员模式关闭时按设计退出，注册失败 `0x80073CFF` 已复现）；MSIX 构建、签名（签名有效，仅根证书不受信任）和框架依赖核对已验证。
- 直接运行打包版 exe 的崩溃发生在模块初始化阶段（早于 `Main`），无法在应用里弹出友好提示，只能靠文档和脚本避免。

## 6. PublishTrimmed 涓?System.Text.Json锛?.0.9锛?
1.0.8 Release 鍖呭紑鍚簡 `PublishTrimmed`锛屽鑷?`JsonSerializer` 榛樿绂佺敤鍙嶅皠锛孏raph(Kiota) 涓庨儴鍒嗘湰鍦?JSON 鍦ㄥ悓姝?鎷夋鏂囨椂鎶涢敊銆? 
鑷?**1.0.9** 璧?`MailClient.App.csproj` 鍥哄畾 `PublishTrimmed=false`锛屽苟淇濈暀鏈湴 DTO 鐨?`JsonSerializerContext`銆傞噸鏂板惎鐢ㄨ鍓墠璇峰厛璁?Graph/MSAL/MailKit 璺緞 trim-safe銆?
