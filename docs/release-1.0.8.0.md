# MailClient 1.0.8.0 发布说明

**版本：** 1.0.8.0  
**日期：** 2026-10-31（Asia/Shanghai）  
**安装包：** `artifacts/msix/MailClient.App_1.0.8.0_x64_Test/MailClient.App_1.0.8.0_x64.msix`

## 本版要点

1. **列表预览去 CSS**  
   入库/修复后预览为纯文本；库内 `cssish` 样例计数为 0。中栏行预览不再露出 `{` / `font-family` / `<style>` 等原始样式碎片。

2. **去掉演示账号种子**  
   升级后不再注入 Outlook/QQ 演示邮箱；按固定种子 Guid 清理旧演示数据。侧栏仅保留真实账号（当前机：平的Gmail、平的Outlook）。

3. **账号重命名**  
   账号节点右键菜单增加「重命名…」→ ContentDialog → 写入数据库 `DisplayName` 并刷新树。

4. **分类标签仅 Gmail**  
   - Gmail（`gmail.oauth`）：收件箱等显示「主要 / 交易 / 更新 / 促销」，分类来自服务器映射（`GmailCategoryMapper`）。  
   - Outlook / QQ / 163 / 通用 IMAP：`ShowCategoryBar=false`，不跑 `MailCategoryClassifier` 驱动 UI；列表**不过滤** Category，展示该文件夹全部邮件。  
   - 「所有收件箱」亦不显示分类条。

5. **中栏列表空白修复**  
   Release + `PublishTrimmed` 下，对 `ObservableCollection<MessageRowViewModel>` 做 `ItemsSource=` / x:Bind 会在 CsWinRT `set_ItemsSource` 抛 NRE，导致同批标题绑定中断、列表 children=0。  
   **处理：** 中栏改为 `Items.Clear/Add` + `CollectionChanged` 同步；标题/副标题代码同步；`SelectionChanged` 写回 `SelectedMessage` 以打开阅读窗格。

## 验证摘要（开发机）

| 场景 | 结果 |
|------|------|
| 所有收件箱 | 标题「所有收件箱」、无分类条、列表有行（约 120 封 / 未读角标 65） |
| Gmail 收件箱 | `showsCat=True`，四分类可见；按 Primary 过滤有数据 |
| Outlook 收件箱 | `showsCat=False`，无分类条；约 60 封不过滤 |
| 点击列表行 | 阅读窗格出现回复/转发等，正文加载 |
| 演示 QQ/Outlook 种子 | 侧栏无演示账号 |
| 预览 CSS | DB 抽样无样式碎片 |
| 单元测试 | **632 通过 / 0 失败** |

截图目录：`artifacts/shots-108/`  
- `all-inboxes-no-cats.png`  
- `gmail-inbox-cats.png`  
- `outlook-inbox-no-cats.png`  
- `reading-after-click.png`

## 已知问题（未纳入本版）

- **同步路径 STJ + PublishTrimmed：** `sync.log` 仍有部分 JSON 反序列化裁剪警告；设置等已用 source-gen 的仍可能有其他类型未进 context。不影响本版列表/分类/重命名/预览修复的本地展示；后续需补 `JsonSerializerContext` 或收紧 trim 根。

## 安装提示

同版本覆盖：先 `Remove-AppxPackage` 再 `Add-AppxPackage`（脚本 `scripts/rebuild-reinstall-108.ps1` 已包含）。不要求 Developer Mode；证书需已导入本机 TrustedPeople（一次性）。
