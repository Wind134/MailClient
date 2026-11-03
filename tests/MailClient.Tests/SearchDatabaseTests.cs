using System.Diagnostics;
using MailClient.Core;
using MailClient.Core.Search;
using MailClient.Storage;
using MailClient.Storage.Demo;
using MailClient.Storage.Search;
using MailClient.ViewModels;
using Xunit.Abstractions;

namespace MailClient.Tests;

public class SearchDatabaseTests
{
    private static async Task<IReadOnlyList<MessageEntity>> SearchAsync(MailDatabase db, string text, IReadOnlyList<string>? folders = null,
        Guid? account = null, bool excludeTrashJunk = false)
        => await db.QueryMessagesAsync(new MessageListFilter(folders, null, false, false, text, 1000, account, excludeTrashJunk));

    [Fact]
    public async Task Schema_Is_V4_And_Trigram_Flag_Matches_Table()
    {
        await using var tmp = new TempDb();
        await tmp.Database.InitializeAsync();
        Assert.Equal(7, await tmp.Database.GetSchemaVersionAsync());
        Assert.False(string.IsNullOrEmpty(await tmp.Database.GetSqliteVersionAsync()));
        // 打包的 SQLite >= 3.34 时应启用 trigram
        var version = Version.Parse(await tmp.Database.GetSqliteVersionAsync());
        Assert.Equal(version >= new Version(3, 34), tmp.Database.SupportsTrigram);
    }

    [Fact]
    public async Task Cjk_Three_Chars_Or_More_Matches_Substrings_In_Body()
    {
        await using var tmp = await TempDb.CreateSeededAsync();
        var hits = await SearchAsync(tmp.Database, "数据迁移方案");
        Assert.Contains(hits, m => m.Subject == "项目进度更新与下周计划");
        var strategy = SearchRouter.Route(new SearchTerm(SearchField.Any, "数据迁移方案"), tmp.Database.SupportsTrigram);
        Assert.Equal(tmp.Database.SupportsTrigram ? MatchStrategy.Trigram : MatchStrategy.Like, strategy);
    }

    [Theory]
    [InlineData("迁移", "项目进度更新与下周计划")]   // 2 字：LIKE，只在正文里
    [InlineData("迁", "项目进度更新与下周计划")]     // 1 字
    [InlineData("合同", "合同修改稿请确认")]
    [InlineData("Wino", "上次聊的那个开源项目")]      // 拉丁词，正文里
    [InlineData("wi", "上次聊的那个开源项目")]        // 1-2 位拉丁：FTS 词前缀
    [InlineData("FTS5", "掘金本周热榜：WinUI 3 实战与 .NET 9 新特性")]
    public async Task Terms_Find_Expected_Message(string query, string expectedSubject)
    {
        await using var tmp = await TempDb.CreateSeededAsync();
        var hits = await SearchAsync(tmp.Database, query);
        Assert.Contains(hits, m => m.Subject == expectedSubject);
    }

    [Fact]
    public async Task Multiple_Terms_Are_ANDed()
    {
        await using var tmp = await TempDb.CreateSeededAsync();
        var both = await SearchAsync(tmp.Database, "合同 修订");
        Assert.Contains(both, m => m.Subject == "合同修改稿请确认");
        var none = await SearchAsync(tmp.Database, "合同 抽奖");
        Assert.Empty(none);
    }

    [Fact]
    public async Task Phrase_Matches_Contiguous_Text()
    {
        await using var tmp = await TempDb.CreateSeededAsync();
        Assert.NotEmpty(await SearchAsync(tmp.Database, "\"项目进度更新\""));
        Assert.Empty(await SearchAsync(tmp.Database, "\"项目更新进度\""));
    }

    [Fact]
    public async Task Like_Wildcards_Are_Literal()
    {
        await using var tmp = await TempDb.CreateSeededAsync();
        Assert.Empty(await SearchAsync(tmp.Database, "订%"));
        Assert.Empty(await SearchAsync(tmp.Database, "订_"));
        Assert.NotEmpty(await SearchAsync(tmp.Database, "订单"));
    }

    [Fact]
    public async Task Operators_From_Subject_To_Flags()
    {
        await using var tmp = await TempDb.CreateSeededAsync();
        var db = tmp.Database;

        var from = await SearchAsync(db, "from:linda");
        Assert.NotEmpty(from);
        Assert.All(from, m => Assert.Contains("linda", m.FromAddress + m.FromName, StringComparison.OrdinalIgnoreCase));

        var fromCn = await SearchAsync(db, "from:张伟");
        Assert.NotEmpty(fromCn);
        Assert.All(fromCn, m => Assert.Equal("张伟", m.FromName));

        var subject = await SearchAsync(db, "subject:订单");
        Assert.NotEmpty(subject);
        Assert.All(subject, m => Assert.Contains("订单", m.Subject));
        // 正文里有“订单”但主题没有的邮件不应出现
        var any = await SearchAsync(db, "订单");
        Assert.True(any.Count >= subject.Count);

        var attach = await SearchAsync(db, "has:attachment");
        Assert.NotEmpty(attach);
        Assert.All(attach, m => Assert.True(m.HasAttachments));

        var unread = await SearchAsync(db, "is:unread");
        Assert.NotEmpty(unread);
        Assert.All(unread, m => Assert.False(m.IsRead));

        var flagged = await SearchAsync(db, "is:flagged has:attachment");
        Assert.NotEmpty(flagged);
        Assert.All(flagged, m => Assert.True(m.IsFlagged && m.HasAttachments));

        var combined = await SearchAsync(db, "from:linda has:attachment 项目");
        Assert.Contains(combined, m => m.Subject == "项目进度更新与下周计划");

        var to = await SearchAsync(db, "to:zhang.wei");
        Assert.Contains(to, m => m.Subject == "设计评审材料");
    }

    [Fact]
    public async Task Date_Operators_Partition_The_Mailbox()
    {
        await using var tmp = await TempDb.CreateSeededAsync();
        var db = tmp.Database;
        var all = await SearchAsync(db, null!);
        var after = await SearchAsync(db, "after:2026-10-31");
        var before = await SearchAsync(db, "before:2026-10-31");
        Assert.NotEmpty(after);
        Assert.NotEmpty(before);
        Assert.Equal(all.Count, after.Count + before.Count);
        var midnight = SearchSqlBuilder.StartOfDay(new DateOnly(2026, 10, 31), TimeZoneInfo.Local);
        Assert.All(after, m => Assert.True(m.ReceivedAt >= midnight));
        Assert.All(before, m => Assert.True(m.ReceivedAt < midnight));
        var window = await SearchAsync(db, "after:2026-10-01 before:2026-10-31 订单");
        Assert.All(window, m => Assert.True(m.ReceivedAt < midnight));
    }

    [Fact]
    public async Task Scope_Current_Folder_Account_And_All()
    {
        await using var tmp = await TempDb.CreateSeededAsync();
        var db = tmp.Database;
        var outlookInbox = DemoDataSeeder.FolderId(DemoDataSeeder.OutlookAccountId, SpecialFolder.Inbox);

        var folder = await SearchAsync(db, "周末", [outlookInbox]);
        var account = await SearchAsync(db, "周末", null, DemoDataSeeder.OutlookAccountId, true);
        var everything = await SearchAsync(db, "周末", null, null, true);

        Assert.NotEmpty(folder);
        Assert.All(folder, m => Assert.Equal(outlookInbox, m.FolderId));
        Assert.All(account, m => Assert.Equal(DemoDataSeeder.OutlookAccountId, m.AccountId));
        Assert.DoesNotContain(account, m => m.Subject.Contains("笔记本"));
        Assert.Contains(everything, m => m.Subject == "周末带上你的笔记本");        // QQ
        Assert.Contains(everything, m => m.Subject == "这周末回家吃饭吗");          // Outlook
        Assert.True(folder.Count <= account.Count && account.Count < everything.Count);
    }

    [Fact]
    public async Task Cross_Folder_Search_Can_Exclude_Trash_And_Junk()
    {
        await using var tmp = await TempDb.CreateSeededAsync();
        var db = tmp.Database;
        Assert.Contains(await SearchAsync(db, "报名"), m => m.Subject.StartsWith("最后一天"));
        Assert.DoesNotContain(await SearchAsync(db, "报名", excludeTrashJunk: true), m => m.Subject.StartsWith("最后一天"));
        Assert.Contains(await SearchAsync(db, "抽奖"), m => m.Subject.Contains("抽奖"));
        Assert.Empty(await SearchAsync(db, "抽奖", excludeTrashJunk: true));
    }

    [Fact]
    public async Task Index_Follows_Updates_And_Deletes()
    {
        await using var f = await SendFixture.CreateAsync();
        var m = f.Message("INBOX", 1) with { Subject = "原始主题 alpha", BodyText = "正文内容很长很长" };
        await f.Db.UpsertMessagesAsync([m]);
        Assert.Single(await SearchAsync(f.Db, "原始主题"));
        Assert.Single(await SearchAsync(f.Db, "alpha"));

        await f.Db.UpsertMessagesAsync([m with { Subject = "全新标题 beta" }]);
        Assert.Empty(await SearchAsync(f.Db, "原始主题"));
        Assert.Empty(await SearchAsync(f.Db, "alpha"));
        Assert.Single(await SearchAsync(f.Db, "全新标题"));
        Assert.Single(await SearchAsync(f.Db, "beta"));

        // 只改已读状态不应破坏索引
        await f.Db.UpsertMessagesAsync([m with { Subject = "全新标题 beta", IsRead = true }]);
        Assert.Single(await SearchAsync(f.Db, "全新标题"));
        Assert.Single(await SearchAsync(f.Db, "is:read 全新标题"));
    }

    [Fact]
    public async Task Search_Matches_Message_With_Null_Body_By_Preview()
    {
        await using var f = await SendFixture.CreateAsync();
        await f.Db.UpsertMessagesAsync([f.Message("INBOX", 1) with { Preview = "这里只有预览文字的内容", BodyText = null, BodyHtml = null }]);
        Assert.Single(await SearchAsync(f.Db, "预览文字"));
        Assert.Single(await SearchAsync(f.Db, "预览"));
    }

    [Fact]
    public async Task Invalid_Fts_Characters_Do_Not_Throw()
    {
        await using var tmp = await TempDb.CreateSeededAsync();
        foreach (var q in new[] { "\"", "a\"b", "NEAR(", "AND", "*", "-", "(项目", "{x}", "合同\"" })
        {
            await SearchAsync(tmp.Database, q);
        }
    }

    // ───────────── 视图模型层：范围 → 过滤条件 ─────────────

    private static MailboxNodeViewModel Node(MailboxNodeKind kind, SpecialFolder? special = null, Guid? account = null)
        => new(kind, "n", "g") { Special = special, AccountId = account };

    [Fact]
    public async Task BuildFilter_Maps_Scopes()
    {
        await using var tmp = await TempDb.CreateSeededAsync();
        var list = new MessageListViewModel(tmp.Database);
        var acct = Guid.NewGuid();
        var inbox = Node(MailboxNodeKind.Folder, SpecialFolder.Inbox, acct);
        IReadOnlyList<string> ids = ["a:INBOX"];

        var none = list.BuildFilter(inbox, ids, "  ", SearchScope.AllMailboxes, null);
        Assert.Equal(ids, none.FolderIds);
        Assert.Null(none.SearchText);

        var folder = list.BuildFilter(inbox, ids, "合同", SearchScope.CurrentFolder, null);
        Assert.Equal(ids, folder.FolderIds);
        Assert.Equal("合同", folder.SearchText);
        Assert.False(folder.ExcludeTrashAndJunk);

        var account = list.BuildFilter(inbox, ids, "合同", SearchScope.CurrentAccount, null);
        Assert.Null(account.FolderIds);
        Assert.Equal(acct, account.AccountId);
        Assert.True(account.ExcludeTrashAndJunk);

        var all = list.BuildFilter(inbox, ids, "合同", SearchScope.AllMailboxes, MailCategory.Updates);
        Assert.Null(all.FolderIds);
        Assert.Null(all.AccountId);
        Assert.Null(all.Category);
        Assert.True(all.ExcludeTrashAndJunk);

        var trash = Node(MailboxNodeKind.Folder, SpecialFolder.Trash, acct);
        Assert.False(list.BuildFilter(trash, ids, "合同", SearchScope.AllMailboxes, null).ExcludeTrashAndJunk);

        // 账号节点 / 无账号的收藏夹节点：当前账号退化为全部
        var fav = Node(MailboxNodeKind.AllInboxes);
        Assert.Null(list.BuildFilter(fav, ids, "合同", SearchScope.CurrentAccount, null).AccountId);
    }
}

public class SearchPerformanceTests
{
    private readonly ITestOutputHelper _output;

    public SearchPerformanceTests(ITestOutputHelper output) => _output = output;

    private static readonly string[] CnPhrases =
    [
        "项目进度更新", "合同修改稿", "数据迁移方案", "季度财务报告", "周五评审会议", "设计规范文档", "客户需求变更", "系统集成测试",
        "上线前检查清单", "性能优化建议", "用户登录模块", "接口单元测试", "发票报销流程", "年度绩效考核", "团队建设活动", "服务器到期续费",
        "订单已发货通知", "付款成功凭证", "旅行照片整理", "同学聚会安排", "开源项目贡献", "面试邀请安排", "知识库整理", "代码评审意见"
    ];

    private static readonly string[] EnWords =
    [
        "invoice", "report", "meeting", "schedule", "project", "update", "review", "deployment", "customer", "contract", "payment", "shipment",
        "newsletter", "security", "account", "weekly", "summary", "proposal", "feedback", "release", "migration", "database", "roadmap", "budget"
    ];

    private static readonly string[] Names = ["alice", "bob", "carol", "david", "linda", "zhang.wei", "wang.fang", "chen.siyuan"];

    private static string Sentence(Random r)
    {
        var parts = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            parts.Add(r.Next(3) == 0 ? EnWords[r.Next(EnWords.Length)] : CnPhrases[r.Next(CnPhrases.Length)]);
        }

        return string.Join(r.Next(2) == 0 ? "，" : " ", parts) + "。";
    }

    private static async Task<(SendFixture F, List<MessageEntity> All)> Seed20kAsync(int count)
    {
        var f = await SendFixture.CreateAsync();
        var r = new Random(20260131);
        var folders = new[] { "INBOX", "Sent", "Archive", "Trash" };
        var list = new List<MessageEntity>(count);
        for (var i = 1; i <= count; i++)
        {
            var folder = folders[i % folders.Length];
            var name = Names[r.Next(Names.Length)];
            var body = string.Concat(Enumerable.Range(0, 4).Select(_ => Sentence(r)));
            list.Add(f.Message(folder, i) with
            {
                Subject = Sentence(r)[..Math.Min(24, 20)],
                FromAddress = $"{name}@example.com",
                FromName = name,
                Preview = body[..Math.Min(body.Length, 200)],
                BodyText = body,
                BodyHtml = null,
                ReceivedAt = SendFixture.T0.AddMinutes(-i * 7),
                IsRead = i % 3 != 0,
                IsFlagged = i % 17 == 0,
                HasAttachments = i % 5 == 0
            });
        }

        // 一封带稀有词的邮件：测“扫全表才能确认只有 1 条命中”的最坏情况
        list[12344] = list[12344] with { BodyText = list[12344].BodyText + " 独特标记词汇甲乙丙 uniq7788token" };
        for (var i = 0; i < list.Count; i += 2000)
        {
            await f.Db.UpsertMessagesAsync(list.Skip(i).Take(2000).ToList());
        }

        return (F: f, All: list);
    }

    [Fact]
    public async Task Search_On_20k_Messages_Is_Fast()
    {
        const int count = 20_000;
        var sw = Stopwatch.StartNew();
        var (f, all) = await Seed20kAsync(count);
        await using var _ = f;
        _output.WriteLine($"seed {count} messages: {sw.ElapsedMilliseconds} ms");

        var db = f.Db;
        _output.WriteLine($"sqlite_version={await db.GetSqliteVersionAsync()} trigram={db.SupportsTrigram}");

        var cases = new (string Label, string Query, Guid? Account, IReadOnlyList<string>? Folders)[]
        {
            ("CJK 6字 (trigram)", "数据迁移方案", null, null),
            ("CJK 3字 (trigram)", "合同修", null, null),
            ("CJK 2字 (LIKE)", "迁移", null, null),
            ("CJK 1字 (LIKE)", "迁", null, null),
            ("Latin 7字 (trigram)", "invoice", null, null),
            ("Latin 2字 (FTS prefix)", "re", null, null),
            ("2 词 AND", "合同修改稿 invoice", null, null),
            ("from:", "from:linda", null, null),
            ("from: + has:attachment + 词", "from:linda has:attachment 报告", null, null),
            ("is:unread is:flagged", "is:unread is:flagged", null, null),
            ("after:/before: + 词", "after:2026-09-01 before:2026-10-15 合同", null, null),
            ("subject: 词", "subject:评审", null, null),
            ("范围=当前文件夹", "数据迁移方案", null, [f.FolderId("INBOX")]),
            ("范围=当前账号", "数据迁移方案", f.AccountId, null),
            ("稀有 CJK 3字+ (trigram,1 命中)", "标记词汇", null, null),
            ("稀有 CJK 2字 (LIKE 全表扫,1 命中)", "甲乙", null, null),
            ("稀有 CJK 1字 (LIKE 全表扫,1 命中)", "丙", null, null),
            ("稀有 Latin (trigram,1 命中)", "uniq7788token", null, null),
            ("无命中", "完全不存在的词语", null, null)
        };

        foreach (var (label, query, account, folders) in cases)
        {
            var filter = new MessageListFilter(folders, null, false, false, query, 200, account, account is not null);
            await db.QueryMessagesAsync(filter); // 预热
            var times = new List<double>();
            IReadOnlyList<MessageEntity> rows = [];
            for (var i = 0; i < 5; i++)
            {
                var t = Stopwatch.StartNew();
                rows = await db.QueryMessagesAsync(filter);
                times.Add(t.Elapsed.TotalMilliseconds);
            }

            times.Sort();
            _output.WriteLine($"{label,-32} q=\"{query}\"  hits={rows.Count,4}  median={times[2],7:F1} ms  max={times[^1],7:F1} ms");
            Assert.True(times[2] < 1500, $"{label} 中位耗时 {times[2]:F0} ms 过慢");
        }

        Assert.Single(await db.QueryMessagesAsync(new MessageListFilter(null, null, false, false, "甲乙", 200)));
        Assert.Single(await db.QueryMessagesAsync(new MessageListFilter(null, null, false, false, "uniq7788", 200)));

        // 正确性抽查：结果数与内存里的子串匹配一致（上限 200）
        var expectedCjk = all.Count(m => string.Join("\u0001", m.Subject, m.FromName, m.FromAddress, m.Preview, m.BodyText).Contains("数据迁移方案", StringComparison.OrdinalIgnoreCase));
        var got = await db.QueryMessagesAsync(new MessageListFilter(null, null, false, false, "数据迁移方案", 100_000));
        Assert.Equal(expectedCjk, got.Count);
        var expectedLike = all.Count(m => string.Join("\u0001", m.Subject, m.FromName, m.FromAddress, m.Preview, m.BodyText).Contains("迁移", StringComparison.OrdinalIgnoreCase));
        var gotLike = await db.QueryMessagesAsync(new MessageListFilter(null, null, false, false, "迁移", 100_000));
        Assert.Equal(expectedLike, gotLike.Count);
    }
}
