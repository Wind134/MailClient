# 1.0.12.0

## 存储不再无限膨胀

- **正文缓存上限**：保留时间（默认 90 天，距上次打开 / 下载）+ 大小上限（默认 200 MB），超出时先清最久没打开的正文；列表行、草稿、发件箱、本地发出的副本、有待写回操作的邮件都不动；被清理的正文再次打开时自动重新下载。
- **schema V7**：`messages.BodyAccessedAt`（最后访问时间）+ 部分索引；已有缓存回填成升级时刻。
- **auto_vacuum = INCREMENTAL**：新库直接启用；老库第一次维护时 VACUUM 一次转换。之后用 `incremental_vacuum` 把空闲页还给磁盘；每 60 天一次完整 VACUUM。
- **后台维护**：启动后 20 秒、之后每 6 小时（清缓存 → 整理数据库 → 日志轮换），全部在后台线程。
- **日志封顶**：`list-debug.log` / `sync.log` / `startup.log` 各约 1 MB，保留 2 份旧文件。
- **设置 → 存储**：显示数据库 / 正文缓存 / 日志占用；保留时间与大小上限；「清理缓存」（带确认）和「压缩数据库」。

## 打开邮件中的链接

- 设置 → 链接 →「打开邮件中的链接」：**系统默认浏览器**（默认）或 **软件内置浏览器**。
- 内置浏览器是一个独立的 WebView2 窗口：标题栏显示页面标题与网址，工具栏有后退 / 前进 / 刷新、网址栏和「在系统浏览器中打开」；窗口内 `target=_blank` / `window.open` 留在同一窗口；只允许 http/https。
- 阅读窗格拦截 `NavigationStarting` 和 `NewWindowRequested`：http/https 按设置路由；`mailto:` 打开写信窗口（解析 to/cc/bcc/subject/body）；`javascript:` / `file:` / `data:` / `ms-*:` 等其他协议一律拦截并在状态栏提示。邮件正文仍然不运行脚本。

## 测试 / 文档

- 新增测试：缓存清理策略、存储选项、日志轮换、链接路由与 mailto 解析、V7 迁移、清理保护规则、搜索索引与 VACUUM、auto_vacuum 转换、维护服务。
- 文档：[storage.md](storage.md)、[sqlite-schema.md](sqlite-schema.md)（V7）。

Package: artifacts/msix/MailClient.App_1.0.12.0_x64_Test/MailClient.App_1.0.12.0_x64.msix
