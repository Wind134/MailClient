using MailClient.Core.Notifications;

namespace MailClient.Tests;

public sealed class ToastActionTests
{
    private static readonly Guid Acc = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Theory]
    [InlineData(ToastActionKind.Open)]
    [InlineData(ToastActionKind.MarkRead)]
    [InlineData(ToastActionKind.Delete)]
    public void Arguments_round_trip(ToastActionKind kind)
    {
        var action = new ToastAction(kind, Acc, "acc:INBOX:42");
        var parsed = ToastAction.TryParse(action.ToArguments());
        Assert.Equal(action, parsed);
    }

    [Fact]
    public void Summary_toast_opens_the_inbox()
    {
        var parsed = ToastAction.TryParse(new ToastAction(ToastActionKind.OpenInbox, Acc).ToArguments());
        Assert.Equal(new ToastAction(ToastActionKind.OpenInbox, Acc), parsed);
    }

    [Fact]
    public void Only_button_actions_run_in_background()
    {
        Assert.True(new ToastAction(ToastActionKind.MarkRead, Acc, "m").RunsInBackground);
        Assert.True(new ToastAction(ToastActionKind.Delete, Acc, "m").RunsInBackground);
        Assert.False(new ToastAction(ToastActionKind.Open, Acc, "m").RunsInBackground);
        Assert.False(new ToastAction(ToastActionKind.OpenInbox, Acc).RunsInBackground);
    }

    [Fact]
    public void Missing_or_unknown_action_is_ignored()
    {
        Assert.Null(ToastAction.TryParse(null));
        Assert.Null(ToastAction.TryParse(new Dictionary<string, string>()));
        Assert.Null(ToastAction.TryParse(new Dictionary<string, string> { ["action"] = "format-disk" }));
    }

    [Fact]
    public void Message_actions_without_a_message_id_fall_back_to_opening_the_inbox()
    {
        var parsed = ToastAction.TryParse(new Dictionary<string, string> { ["action"] = "delete" });
        Assert.Equal(ToastActionKind.OpenInbox, parsed!.Kind);
    }

    [Fact]
    public void Parsing_is_case_insensitive_and_tolerates_a_bad_account_id()
    {
        var parsed = ToastAction.TryParse(new Dictionary<string, string> { ["ACTION"] = "MarkRead", ["accountid"] = "nope", ["messageId"] = "x" });
        Assert.Equal(new ToastAction(ToastActionKind.MarkRead, null, "x"), parsed);
    }
}