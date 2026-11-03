using MailClient.Core;
using MailClient.Core.Services;
using MailClient.Core.Sync;
using MailClient.Storage;

namespace MailClient.ViewModels;

public sealed record ActionNotice(string Message, bool IsError);

/// <summary>
/// 阅读窗格命令与列表右键菜单共用的“邮件操作”：本地库立即生效（乐观更新），同时把写回服务器的变更排进队列
/// （<see cref="MailDatabase.ApplyMessageChangeAsync"/>），由 PendingOpsWorker 在后台写回；演示账号只改本地。
/// </summary>
public sealed class MessageActions
{
    private readonly MailDatabase _database;
    private readonly MailboxTreeViewModel _tree;

    private readonly IDraftStore? _drafts;

    public MessageActions(MailDatabase database, MailboxTreeViewModel tree, IDraftStore? drafts = null)
    {
        _database = database;
        _tree = tree;
        _drafts = drafts;
    }

    /// <summary>本地草稿在列表里是 id 以 "draft:" 开头的“邮件”，它不在服务器上，不能走已读 / 标记 / 归档 / 移动这些写回操作。</summary>
    public static bool IsLocalDraft(string messageId) => messageId.StartsWith(MailDatabase.DraftMessagePrefix, StringComparison.Ordinal);

    public event EventHandler<MessageMutationEventArgs>? Mutated;

    /// <summary>需要告诉用户的结果（例如“该账号没有归档文件夹”）。</summary>
    public event EventHandler<ActionNotice>? Notice;

    public async Task SetReadAsync(string messageId, bool read)
    {
        if (IsLocalDraft(messageId))
        {
            return;
        }

        if (await _database.ApplyMessageChangeAsync(messageId, read ? PendingOpKind.MarkRead : PendingOpKind.MarkUnread))
        {
            Mutated?.Invoke(this, new MessageMutationEventArgs(messageId, read ? MessageMutation.Read : MessageMutation.Unread));
        }
    }

    public async Task SetFlaggedAsync(string messageId, bool flagged)
    {
        if (IsLocalDraft(messageId))
        {
            return;
        }

        if (await _database.ApplyMessageChangeAsync(messageId, flagged ? PendingOpKind.Flag : PendingOpKind.Unflag))
        {
            Mutated?.Invoke(this, new MessageMutationEventArgs(messageId, flagged ? MessageMutation.Flagged : MessageMutation.Unflagged));
        }
    }

    public async Task<bool> ArchiveAsync(string messageId)
    {
        if (IsLocalDraft(messageId))
        {
            return false;
        }

        var message = await _database.GetMessageAsync(messageId);
        if (message is null)
        {
            return false;
        }

        var target = _tree.FindFolderId(message.AccountId, SpecialFolder.Archive);
        if (target is null)
        {
            Notice?.Invoke(this, new ActionNotice("该账号没有归档文件夹（服务器上没有“归档 / 所有邮件”），无法归档。", true));
            return false;
        }

        if (target == message.FolderId)
        {
            return false;
        }

        if (!await _database.ApplyMessageChangeAsync(messageId, PendingOpKind.Move, target))
        {
            return false;
        }

        Mutated?.Invoke(this, new MessageMutationEventArgs(messageId, MessageMutation.Archived));
        return true;
    }

    /// <summary>删除：不在已删除文件夹时移入已删除；已在其中（或账号没有已删除文件夹）则永久删除。</summary>
    public async Task<bool> DeleteAsync(string messageId)
    {
        if (IsLocalDraft(messageId))
        {
            // 本地草稿：删除草稿本身（含附件副本；若它来自服务器草稿，一并删除服务器上的那份）
            if (_drafts is null)
            {
                return false;
            }

            await _drafts.DeleteAsync(messageId[MailDatabase.DraftMessagePrefix.Length..]);
            Mutated?.Invoke(this, new MessageMutationEventArgs(messageId, MessageMutation.Deleted));
            return true;
        }

        var message = await _database.GetMessageAsync(messageId);
        if (message is null)
        {
            return false;
        }

        var trash = _tree.FindFolderId(message.AccountId, SpecialFolder.Trash);
        var ok = trash is null || trash == message.FolderId
            ? await _database.ApplyMessageChangeAsync(messageId, PendingOpKind.Delete)
            : await _database.ApplyMessageChangeAsync(messageId, PendingOpKind.Move, trash);
        if (ok)
        {
            Mutated?.Invoke(this, new MessageMutationEventArgs(messageId, MessageMutation.Deleted));
        }

        return ok;
    }
}
