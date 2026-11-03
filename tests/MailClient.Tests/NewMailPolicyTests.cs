using MailClient.Core;
using MailClient.Core.Notifications;

namespace MailClient.Tests;

public sealed class NewMailPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.FromHours(8));
    private static readonly Guid Acc = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly IReadOnlySet<string> NoKnown = new HashSet<string>();
    private static readonly IReadOnlySet<string> Own = NewMailPolicy.NormalizeAddresses(new[] { "Me@Example.com" });

    private static NewMailCandidate Mail(
        string id = "m1", string from = "alice@example.org", string? name = "Alice", string subject = "你好", string preview = "预览",
        bool read = false, bool draft = false, SpecialFolder folder = SpecialFolder.Inbox, DateTimeOffset? at = null)
        => new(id, Acc, "账号", folder, from, name, subject, preview, read, draft, at ?? Now.AddMinutes(-3));

    private static IReadOnlyList<NewMailCandidate> Select(IEnumerable<NewMailCandidate> mails, bool initial = false, bool enabled = true, IReadOnlySet<string>? known = null)
        => NewMailPolicy.Select(mails, known ?? NoKnown, initial, enabled, Own, Now);

    [Fact]
    public void New_unread_inbox_mail_is_notified()
        => Assert.Single(Select(new[] { Mail() }));

    [Fact]
    public void Initial_sync_never_notifies()
        => Assert.Empty(Select(new[] { Mail(), Mail("m2") }, initial: true));

    [Fact]
    public void Disabled_setting_never_notifies()
        => Assert.Empty(Select(new[] { Mail() }, enabled: false));

    [Fact]
    public void Already_read_mail_is_skipped()
        => Assert.Empty(Select(new[] { Mail(read: true) }));

    [Fact]
    public void Drafts_are_skipped()
        => Assert.Empty(Select(new[] { Mail(draft: true) }));

    [Theory]
    [InlineData(SpecialFolder.Sent)]
    [InlineData(SpecialFolder.Trash)]
    [InlineData(SpecialFolder.Junk)]
    [InlineData(SpecialFolder.Drafts)]
    public void Only_inbox_mail_is_notified(SpecialFolder folder)
        => Assert.Empty(Select(new[] { Mail(folder: folder) }));

    [Fact]
    public void Messages_the_user_sent_are_skipped_case_insensitively()
        => Assert.Empty(Select(new[] { Mail(from: " ME@example.COM ") }));

    [Fact]
    public void Known_messages_are_skipped()
        => Assert.Empty(Select(new[] { Mail("old") }, known: new HashSet<string> { "old" }));

    [Fact]
    public void Very_old_mail_is_skipped_but_recent_mail_is_not()
    {
        var result = Select(new[] { Mail("old", at: Now.AddDays(-3)), Mail("fresh", at: Now.AddDays(-1)) });
        Assert.Equal(new[] { "fresh" }, result.Select(m => m.MessageId));
    }

    [Fact]
    public void Selection_is_newest_first()
    {
        var result = Select(new[] { Mail("a", at: Now.AddMinutes(-30)), Mail("b", at: Now.AddMinutes(-1)), Mail("c", at: Now.AddMinutes(-10)) });
        Assert.Equal(new[] { "b", "c", "a" }, result.Select(m => m.MessageId));
    }

    [Fact]
    public void Few_messages_get_individual_toasts()
    {
        var plan = NewMailPolicy.Plan(Select(new[] { Mail("a"), Mail("b"), Mail("c") }));
        Assert.Null(plan.Summary);
        Assert.Equal(3, plan.Individual.Count);
        Assert.Equal(3, plan.ToastCount);
    }

    [Fact]
    public void Many_messages_collapse_into_one_summary()
    {
        var mails = Enumerable.Range(0, 7).Select(i => Mail($"m{i}", from: $"s{i}@x.org", name: $"发件人{i}")).ToList();
        var plan = NewMailPolicy.Plan(Select(mails));
        Assert.Empty(plan.Individual);
        Assert.NotNull(plan.Summary);
        Assert.Equal("你有 7 封新邮件", plan.Summary!.Title);
        Assert.Contains("等 7 位发件人", plan.Summary.Body);
        Assert.Equal(Acc, plan.Summary.AccountId);
        Assert.Equal(1, plan.ToastCount);
    }

    [Fact]
    public void Summary_from_several_accounts_has_no_account_id()
    {
        var other = Guid.NewGuid();
        var mails = Enumerable.Range(0, 4).Select(i => Mail($"m{i}") with { AccountId = i % 2 == 0 ? Acc : other }).ToList();
        Assert.Null(NewMailPolicy.Plan(mails).Summary!.AccountId);
    }

    [Fact]
    public void Empty_selection_gives_empty_plan()
        => Assert.Equal(0, NewMailPolicy.Plan(Array.Empty<NewMailCandidate>()).ToastCount);

    [Fact]
    public void Display_helpers_fall_back_sensibly()
    {
        var m = Mail(from: "bob@x.org", name: null, subject: "  ", preview: "第一行\r\n\r\n第二行   第三行");
        Assert.Equal("bob@x.org", m.SenderDisplay);
        Assert.Equal("（无主题）", m.SubjectDisplay);
        Assert.Equal("第一行 第二行 第三行", m.PreviewDisplay);
    }

    [Fact]
    public void Shorten_truncates_with_ellipsis_and_keeps_surrogate_pairs()
    {
        var text = new string('a', 9) + "😀😀😀";
        var s = NewMailPolicy.Shorten(text, 10);
        Assert.EndsWith("…", s);
        Assert.DoesNotContain('\uFFFD', s);
        Assert.True(s.Length <= 11);
        Assert.Equal(string.Empty, NewMailPolicy.Shorten(null, 10));
    }

    [Fact]
    public void Notifications_are_enabled_by_default_and_round_trip_through_json()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        var store = new AppSettingsStore(path);
        Assert.True(store.Snapshot().NotificationsEnabled);
        store.Update(s => s.NotificationsEnabled = false);
        Assert.False(new AppSettingsStore(path).Snapshot().NotificationsEnabled);
    }
}