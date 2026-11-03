using System.Net;
using MailClient.Core;
using MailClient.Core.Classification;

namespace MailClient.Storage.Demo;

/// <summary>
/// 仅供单元测试 / 显式 seed：写入两个固定 Guid 的演示账号与约 35 封虚构中文邮件（地址均为 example.com）。正式包启动不会播种，并会清掉这些演示账号。
/// 分类由 <see cref="MailCategoryClassifier"/> 规则得出（同时也演示该分类器）。
/// </summary>
public static class DemoDataSeeder
{
    public static readonly Guid OutlookAccountId = new("0d6f1c5e-3b1a-4c55-9a4e-0a1b2c3d4e01");
    public static readonly Guid QqAccountId = new("0d6f1c5e-3b1a-4c55-9a4e-0a1b2c3d4e02");

    public const string OutlookAddress = "demo.outlook@example.com";
    public const string QqAddress = "demo.qq@example.com";

    private sealed record Demo(
        Guid Account,
        SpecialFolder Folder,
        string Name,
        string Address,
        string Subject,
        string[] Paragraphs,
        int MinutesAgo,
        bool Unread = false,
        bool Flagged = false,
        string[]? Attachments = null,
        string? To = null);

    public static bool IsDemoAccount(Guid accountId)
        => accountId == OutlookAccountId || accountId == QqAccountId;

    /// <summary>删除两个演示账号（及其全部演示邮件）。返回删除的账号数。</summary>
    public static async Task<int> ClearDemoDataAsync(MailDatabase db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var accounts = await db.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        var removed = 0;
        foreach (var account in accounts.Where(a => IsDemoAccount(a.Id)))
        {
            await db.DeleteAccountAsync(account.Id, cancellationToken).ConfigureAwait(false);
            removed++;
        }

        return removed;
    }

    public static string FolderId(Guid accountId, SpecialFolder folder)
        => $"{accountId:N}:{folder.ToString().ToLowerInvariant()}";

    /// <summary>仅当没有任何账号时写入；返回是否执行了播种。</summary>
    public static async Task<bool> SeedIfEmptyAsync(MailDatabase db, DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        if ((await db.ListAccountsAsync(cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            return false;
        }

        var clock = now ?? DateTimeOffset.Now;
        var created = clock.AddDays(-30);

        await db.UpsertAccountAsync(new AccountEntity(OutlookAccountId, "Outlook", OutlookAddress, "microsoft.graph",
            AuthKind.OAuth, created, clock, clock, true), cancellationToken).ConfigureAwait(false);
        await db.UpsertAccountAsync(new AccountEntity(QqAccountId, "QQ 邮箱", QqAddress, "imap.generic",
            AuthKind.AppPassword, created.AddMinutes(1), clock, clock, true), cancellationToken).ConfigureAwait(false);

        foreach (var account in new[] { OutlookAccountId, QqAccountId })
        {
            var order = 0;
            foreach (var (special, name) in new (SpecialFolder, string)[]
                     {
                         (SpecialFolder.Inbox, "收件箱"),
                         (SpecialFolder.Drafts, "草稿"),
                         (SpecialFolder.Sent, "已发送"),
                         (SpecialFolder.Archive, "归档"),
                         (SpecialFolder.Junk, "垃圾邮件"),
                         (SpecialFolder.Trash, "已删除邮件")
                     })
            {
                await db.UpsertFolderAsync(new FolderEntity(FolderId(account, special), account, name, special,
                    name, 0, 0, order++, null, null), cancellationToken).ConfigureAwait(false);
            }
        }

        var counter = 0;
        foreach (var demo in Data)
        {
            counter++;
            var id = $"demo-{counter:D3}";
            var received = clock.AddMinutes(-demo.MinutesAgo);
            var text = string.Join("\n\n", demo.Paragraphs);
            var html = string.Concat(demo.Paragraphs.Select(p => $"<p>{WebUtility.HtmlEncode(p).Replace("\n", "<br>")}</p>"));
            var preview = text.Replace("\n", " ").Trim();
            if (preview.Length > 110)
            {
                preview = preview[..110] + "…";
            }

            var category = demo.Folder is SpecialFolder.Inbox
                ? MailCategoryClassifier.Classify(demo.Address, demo.Name, demo.Subject, preview)
                : MailCategory.Primary;

            var accountAddress = demo.Account == OutlookAccountId ? OutlookAddress : QqAddress;
            var isOutgoing = demo.Folder is SpecialFolder.Sent or SpecialFolder.Drafts;
            var fromAddress = isOutgoing ? accountAddress : demo.Address;
            var fromName = isOutgoing ? "我" : demo.Name;
            var toAddress = isOutgoing ? demo.To ?? demo.Address : accountAddress;
            var toName = isOutgoing ? demo.Name : null;
            var hasAttachments = demo.Attachments is { Length: > 0 };

            await db.UpsertMessageAsync(new MessageEntity(
                id, demo.Account, FolderId(demo.Account, demo.Folder), null, $"<{id}@demo.local>", id,
                demo.Subject, fromAddress, fromName, preview, text, html, received, received,
                !demo.Unread, demo.Flagged, demo.Folder == SpecialFolder.Drafts, hasAttachments,
                category, 2048 + text.Length * 3), cancellationToken).ConfigureAwait(false);

            await db.UpsertRecipientAsync(new RecipientEntity(0, id, RecipientKind.To, toAddress, toName),
                cancellationToken).ConfigureAwait(false);

            if (demo.Attachments is not null)
            {
                var n = 0;
                foreach (var file in demo.Attachments)
                {
                    n++;
                    await db.UpsertAttachmentAsync(new AttachmentEntity($"{id}-a{n}", id, file, GuessContentType(file),
                        120_000 + n * 83_000 + file.Length * 1000, null, false, null), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        await db.RecalculateFolderCountsAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static string GuessContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".pdf" => "application/pdf",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        _ => "application/octet-stream"
    };

    private static readonly Demo[] Data =
    [
        // ───────── Outlook · 收件箱 ─────────
        new(OutlookAccountId, SpecialFolder.Inbox, "Linda Mao", "linda.mao@example.com", "项目进度更新与下周计划",
            [
                "Hi 团队，",
                "希望大家一切顺利。以下是本周项目进度更新以及下周的主要计划。",
                "本周成果：\n• 完成了用户登录模块的设计和开发\n• 完成了 API 接口的单元测试，覆盖率达到 85%\n• 与设计团队同步了最终的 UI 规范",
                "下周计划：\n• 进行系统集成测试\n• 准备上线前的数据迁移方案\n• 与客户召开中期评审会议",
                "感谢大家的努力。如有任何问题，请随时在群里或邮件中讨论。",
                "Best,\nLinda Mao"
            ], 125, Unread: true, Flagged: true, Attachments: ["项目周报_第43周.xlsx"]),
        new(OutlookAccountId, SpecialFolder.Inbox, "张伟", "zhang.wei@example.com", "周五评审会议时间调整",
            ["同学你好，", "原定周五下午 3 点的设计评审调整到下午 4 点，会议室不变（B 座 6 楼）。", "请提前把你负责的部分过一遍，有问题我们现场讨论。", "张伟"],
            48, Unread: true),
        new(OutlookAccountId, SpecialFolder.Inbox, "王芳", "wang.fang@example.com", "合同修改稿请确认",
            ["我，", "附件是第三版合同修改稿，主要改动在第 5 条付款节点和第 9 条违约责任，已用修订模式标出。", "麻烦你本周内确认，我这边好安排法务最后审核。", "王芳"],
            190, Unread: true, Flagged: true, Attachments: ["合同_v3.docx", "修订对照表.pdf"]),
        new(OutlookAccountId, SpecialFolder.Inbox, "妈妈", "family@example.com", "这周末回家吃饭吗",
            ["儿子，", "这周末你爸生日，我们打算在家里做一桌，你能回来吗？", "提前跟我说一声，我好去买菜。", "妈"],
            410, Unread: false),
        new(OutlookAccountId, SpecialFolder.Inbox, "陈思远", "chen.siyuan@example.com", "上次聊的那个开源项目",
            ["同学，", "上次你提到想给 Wino Mail 提 PR，我这边整理了几个适合入门的 issue，链接发给你了。", "有空我们可以语音过一下。"],
            1700),
        new(OutlookAccountId, SpecialFolder.Inbox, "京东", "order@jd.com", "您的订单 JD-83291234 已发货",
            ["尊敬的客户，您的订单 JD-83291234 已从上海仓库发出，预计 11 月 2 日送达。", "商品：机械键盘 K8 Pro × 1\n配送：京东快递\n运单号：JDVA0012345678"],
            75, Unread: true),
        new(OutlookAccountId, SpecialFolder.Inbox, "Amazon.cn", "shipment-tracking@amazon.cn", "您的 Amazon.cn 订单已发货",
            ["您好，您订购的《深入理解计算机系统》（第 3 版）已发货。", "预计送达：11 月 3 日\n承运商：顺丰速运"],
            300),
        new(OutlookAccountId, SpecialFolder.Inbox, "招商银行信用卡", "creditcard@cmbchina.com", "您的 10 月电子账单已出",
            ["尊敬的李先生，您的 10 月信用卡电子账单已生成。", "本期应还金额：¥3,284.50\n最后还款日：11 月 18 日", "请登录招商银行 App 查看明细。"],
            1500, Attachments: ["电子账单_2026-10.pdf"]),
        new(OutlookAccountId, SpecialFolder.Inbox, "12306", "12306@rails.com.cn", "【12306】订单 E123456789 购票成功",
            ["您已成功购买 11 月 7 日 G13 次列车车票。", "北京南 08:00 → 上海虹桥 12:28\n二等座 05 车 12F\n票价：¥553.00"],
            2900),
        new(OutlookAccountId, SpecialFolder.Inbox, "支付宝", "service@mail.alipay.com", "付款成功 ¥128.00",
            ["您已成功向「盒马鲜生」付款 ¥128.00。", "付款方式：花呗\n交易号：2026103122001412345678"],
            720, Unread: true),
        new(OutlookAccountId, SpecialFolder.Inbox, "滴滴出行", "receipt@didiglobal.com", "您的行程电子发票已开具",
            ["您 10 月 29 日的行程发票已开具，金额 ¥46.20，请查收附件。"],
            3600, Attachments: ["电子发票_滴滴_46.20.pdf"]),
        new(OutlookAccountId, SpecialFolder.Inbox, "GitHub", "notifications@github.com", "[mailclient/app] PR #42：重构三栏布局",
            ["chenzihao 在 mailclient/app 中提交了一个 Pull Request：", "#42 重构三栏布局，改用 NavigationView + 自定义分隔条\n+ 482 − 117", "请求你 review 这次改动。"],
            35, Unread: true),
        new(OutlookAccountId, SpecialFolder.Inbox, "Slack", "feedback@slack.com", "团队每日摘要：你错过了 12 条消息",
            ["#frontend 频道有 7 条新消息，#design 频道有 5 条新消息。", "点击查看完整对话。"],
            260),
        new(OutlookAccountId, SpecialFolder.Inbox, "Notion", "team@makenotion.com", "Notion 本月更新：全新数据库视图",
            ["本月我们发布了日历视图的重复事件、数据库分组折叠，以及更快的搜索。", "立即体验。"],
            1900),
        new(OutlookAccountId, SpecialFolder.Inbox, "Microsoft 帐户团队", "account-security-noreply@accountprotection.microsoft.com", "新登录活动提醒",
            ["我们检测到你的 Microsoft 帐户在新设备上登录。", "设备：Windows 11\n位置：上海，中国\n时间：今天 09:12", "如果是你本人操作，无需处理。"],
            200, Unread: true),
        new(OutlookAccountId, SpecialFolder.Inbox, "少数派", "newsletter@sspai.com", "少数派周刊：本周值得读的 10 篇文章",
            ["本周精选：WinUI 3 到底值不值得学、一个人做独立开发的第 100 天、效率工具年度盘点……"],
            4300),
        new(OutlookAccountId, SpecialFolder.Inbox, "天猫", "promo@tmall.com", "双11 狂欢：全场满 300 减 50，限时抢购",
            ["双11 预售已开启！跨店满 300 减 50，会员额外 95 折。", "活动截止 11 月 11 日 23:59。如不想接收此类邮件，请点击退订。"],
            95, Unread: true),
        new(OutlookAccountId, SpecialFolder.Inbox, "星巴克", "marketing@starbucks.com.cn", "【限时】本周五买一送一优惠券已到账",
            ["亲爱的会员，一张「任意大杯买一送一」优惠券已发放到您的账户。", "有效期：本周五至周日"],
            610),
        new(OutlookAccountId, SpecialFolder.Inbox, "优衣库", "news@uniqlo.cn", "秋冬新品 5 折起，会员专享折扣",
            ["摇粒绒外套、轻型羽绒服、HEATTECH 全线折扣，会员专享额外 9 折。"],
            2200),
        new(OutlookAccountId, SpecialFolder.Inbox, "携程旅行", "promo@ctrip.com", "周末特价机票低至 3 折",
            ["上海 → 成都 ¥286 起，上海 → 厦门 ¥198 起，先到先得。"],
            5200),

        // ───────── QQ 邮箱 · 收件箱 ─────────
        new(QqAccountId, SpecialFolder.Inbox, "王磊", "friend.a@example.com", "周六同学聚会定在哪儿？",
            ["同学，", "这周六的聚会我们想定在三里屯那家火锅店，晚上 6 点，你能来的话回我一下，我好订位。", "人数大概 12 个。"],
            55, Unread: true, Flagged: true),
        new(QqAccountId, SpecialFolder.Inbox, "赵敏", "friend.b@example.com", "国庆旅行的照片",
            ["照片我整理好了，一共 36 张，挑了几张最好看的发给你。", "后面还有视频，晚点再传。"],
            520, Unread: true, Attachments: ["IMG_2031.jpg", "IMG_2048.jpg"]),
        new(QqAccountId, SpecialFolder.Inbox, "星河科技 HR", "hr@xinghe-tech.com", "面试邀请：前端开发工程师",
            ["你好，", "感谢你投递我们的前端开发工程师岗位。我们希望邀请你参加面试：", "时间：11 月 5 日（周四）14:00\n形式：线上（腾讯会议）\n时长：约 60 分钟", "请回复确认是否方便。"],
            860, Unread: true),
        new(QqAccountId, SpecialFolder.Inbox, "QQ 邮箱团队", "10000@qq.com", "QQ 邮箱安全提醒：新设备登录",
            ["您的 QQ 邮箱于今天 08:45 在新设备上登录，如非本人操作，请及时修改密码。"],
            360),
        new(QqAccountId, SpecialFolder.Inbox, "腾讯云", "noreply@cloud.tencent.com", "您的云服务器即将到期",
            ["您的云服务器 ins-8k2x1 将于 11 月 12 日到期，请及时续费以免影响业务。"],
            1100, Unread: true),
        new(QqAccountId, SpecialFolder.Inbox, "美团外卖", "noreply@meituan.com", "您的外卖订单已送达，评价赢红包",
            ["您在「老乡鸡」下的订单已送达，祝您用餐愉快。"],
            2400),
        new(QqAccountId, SpecialFolder.Inbox, "网易严选", "promo@you.163.com", "秋季大促，领券立减 ¥50",
            ["全场家居满 299 减 50，部分爆款低至 5 折。"],
            3000),
        new(QqAccountId, SpecialFolder.Inbox, "掘金", "newsletter@juejin.cn", "掘金本周热榜：WinUI 3 实战与 .NET 9 新特性",
            ["本周热榜：1. WinUI 3 邮件客户端实战  2. .NET 9 性能改进盘点  3. SQLite FTS5 中文检索方案"],
            6100),
        new(QqAccountId, SpecialFolder.Inbox, "爸爸", "relative@example.com", "周末带上你的笔记本",
            ["家里路由器又出问题了，你回来时带上笔记本帮我看看。"],
            9000),

        // ───────── 其它文件夹 ─────────
        new(OutlookAccountId, SpecialFolder.Sent, "Linda Mao", "linda.mao@example.com", "回复：项目进度更新与下周计划",
            ["Linda，", "收到，周三前我把集成测试清单补上。", "— Demo"], 70, To: "linda.mao@example.com"),
        new(OutlookAccountId, SpecialFolder.Sent, "张伟", "zhang.wei@example.com", "设计评审材料",
            ["张伟你好，", "评审用的原型图和交互说明已放到共享盘，请查阅。"], 1300, To: "zhang.wei@example.com"),
        new(OutlookAccountId, SpecialFolder.Drafts, "陈思远", "chen.siyuan@example.com", "关于开源项目的几个想法",
            ["思远，", "我想先从 issue #88 开始，大概需要……"], 20, To: "chen.siyuan@example.com"),
        new(OutlookAccountId, SpecialFolder.Archive, "Linda Mao", "linda.mao@example.com", "Q3 项目复盘纪要",
            ["各位，Q3 复盘会议纪要整理如下：目标达成率 92%，主要风险集中在数据迁移。"], 21000),
        new(OutlookAccountId, SpecialFolder.Junk, "幸运抽奖中心", "winner@lucky-draw.top", "恭喜您获得 iPhone 抽奖资格",
            ["点击链接领取您的奖品，仅限今日。"], 3800),
        new(OutlookAccountId, SpecialFolder.Trash, "某培训机构", "edu@train-now.cn", "最后一天！报名立减 1000",
            ["课程报名即将截止。"], 14000),
        new(QqAccountId, SpecialFolder.Sent, "王磊", "friend.a@example.com", "回复：周末聚会",
            ["可以，我六点前到。"], 3000, To: "friend.a@example.com")
    ];
}
