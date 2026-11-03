# MailClient 1.0.9.0 发布说明

**版本：** 1.0.9.0  
**日期：** 2026-10-31（Asia/Shanghai）  
**安装包：** `artifacts/msix/MailClient.App_1.0.9.0_x64_Test/MailClient.App_1.0.9.0_x64.msix`

## 原因（1.0.8 黄条 / 闪退）

1.0.8 以 **Release + PublishTrimmed=true** 打包。裁剪会把 `JsonSerializer.IsReflectionEnabledByDefault` 设为 `false`。  
Microsoft Graph（Kiota）、MSAL、以及应用里尚未走 source-gen 的 JSON（令牌、账号连接、发件箱载荷等）仍用反射序列化 → 打开需拉正文的邮件时出现：

> Reflection-based serialization has been disabled for this application…

同步路径同样失败（`sync.log` 大量同类 WARN）；严重时进程退出（闪退）。

## 本版改动

1. **关闭 PublishTrimmed**（`MailClient.App.csproj` 对所有配置 `PublishTrimmed=false`），并显式 `JsonSerializerIsReflectionEnabledByDefault=true`。Graph / MailKit / MSAL 未 trim-safe 前不再做半残裁剪包。
2. **本地 DTO 补 JsonSerializerContext 源生成**：`MailClientJsonContext`（Dictionary / List / OutgoingMailMessage）、`GmailAuthJsonContext`、`AccountConnectionJsonContext`；凭据库、发件箱、草稿引用/附件列表改走 source-gen。
3. **正文错误条**：识别 STJ 反射禁用文案并给出中文提示；过长异常截断；原有 catch + 重试保留。

## 验证

| 场景 | 期望 |
|------|------|
| Get-AppxPackage | 1.0.9.0 |
| 打开需拉取正文的邮件 | 无黄条 STJ 文案，正文显示或可重试 |
| sync.log | 不再刷 Reflection-based serialization |
| 单元测试 | 全绿 |

## 遗留

- 以后若再开裁剪，需按 Graph/Kiota 官方 trim 指引补根程序集，并扫清所有反射 JSON。
- 内嵌 cid 图、关窗后台收信等仍未做。
