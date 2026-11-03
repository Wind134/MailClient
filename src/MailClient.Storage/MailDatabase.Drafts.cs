using System.Text.Json;
using MailClient.Core;
using MailClient.Core.Services;
using Microsoft.Data.Sqlite;

namespace MailClient.Storage;

/// <summary>草稿的完整状态（drafts 表）；列表里的那一行仍是 messages 里 id 为 "draft:{id}" 的邮件。</summary>
public sealed partial class MailDatabase
{
    public const string DraftMessagePrefix = "draft:";

    /// <summary>本地草稿被保存或删除（可能在后台线程触发）；UI 据此刷新草稿列表与计数。</summary>
    public event EventHandler? DraftsChanged;

    /// <summary>一个事务里写入草稿状态行 + 列表行 + 收件人 + 附件，并刷新文件夹计数。</summary>
    public async Task SaveDraftAsync(
        DraftContent draft,
        MessageEntity message,
        IEnumerable<RecipientEntity> recipients,
        IEnumerable<AttachmentEntity> attachments,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var recipientList = recipients.ToList();
        var attachmentList = attachments.ToList();
        await ExecuteAsync(async (connection, ct) =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            await using (var command = Cmd(connection, transaction, """
                INSERT INTO drafts (Id, AccountId, Mode, ToText, CcText, BccText, Subject, Body, InReplyTo, ReferencesJson,
                                    QuotedHtml, QuotedText, AttachmentsJson, SourceMessageId, CreatedAt, UpdatedAt)
                VALUES ($id, $acct, $mode, $to, $cc, $bcc, $subject, $body, $irt, $refs, $qh, $qt, $att, $src, $now, $now)
                ON CONFLICT(Id) DO UPDATE SET
                    AccountId = excluded.AccountId, Mode = excluded.Mode, ToText = excluded.ToText, CcText = excluded.CcText,
                    BccText = excluded.BccText, Subject = excluded.Subject, Body = excluded.Body, InReplyTo = excluded.InReplyTo,
                    ReferencesJson = excluded.ReferencesJson, QuotedHtml = excluded.QuotedHtml, QuotedText = excluded.QuotedText,
                    AttachmentsJson = excluded.AttachmentsJson, SourceMessageId = excluded.SourceMessageId,
                    UpdatedAt = excluded.UpdatedAt;
                """))
            {
                command.Parameters.AddParam("$id", draft.Id);
                command.Parameters.AddParam("$acct", draft.AccountId.ToString("D"));
                command.Parameters.AddParam("$mode", draft.Mode);
                command.Parameters.AddParam("$to", draft.To ?? string.Empty);
                command.Parameters.AddParam("$cc", draft.Cc ?? string.Empty);
                command.Parameters.AddParam("$bcc", draft.Bcc ?? string.Empty);
                command.Parameters.AddParam("$subject", draft.Subject ?? string.Empty);
                command.Parameters.AddParam("$body", draft.Body ?? string.Empty);
                command.Parameters.AddParam("$irt", draft.InReplyTo);
                command.Parameters.AddParam("$refs", draft.References is { Count: > 0 } ? JsonSerializer.Serialize(draft.References.ToList(), MailClientJsonContext.Default.ListString) : null);
                command.Parameters.AddParam("$qh", draft.QuotedHtml);
                command.Parameters.AddParam("$qt", draft.QuotedText);
                command.Parameters.AddParam("$att", draft.Attachments is { Count: > 0 } ? JsonSerializer.Serialize(draft.Attachments.ToList(), MailClientJsonContext.Default.ListString) : null);
                command.Parameters.AddParam("$src", draft.SourceMessageId);
                command.Parameters.AddParam("$now", SqliteHelpers.Fmt(now));
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await UpsertMessageCoreAsync(connection, transaction, message, ct).ConfigureAwait(false);

            await NonQueryAsync(connection, transaction, "DELETE FROM recipients WHERE MessageId = $id;", ("$id", message.Id), ct).ConfigureAwait(false);
            foreach (var r in recipientList)
            {
                await NonQueryAsync(connection, transaction,
                    "INSERT INTO recipients (MessageId, Kind, Address, Name) VALUES ($m, $k, $a, $n);",
                    ("$m", message.Id), ("$k", SqliteHelpers.EnumName(r.Kind)), ("$a", r.Address), ("$n", r.Name), ct).ConfigureAwait(false);
            }

            await NonQueryAsync(connection, transaction, "DELETE FROM attachments WHERE MessageId = $id;", ("$id", message.Id), ct).ConfigureAwait(false);
            foreach (var a in attachmentList)
            {
                await NonQueryAsync(connection, transaction,
                    "INSERT INTO attachments (Id, MessageId, FileName, ContentType, SizeBytes, ContentId, IsInline, LocalPath) VALUES ($id, $m, $f, $c, $s, NULL, 0, $p);",
                    ("$id", a.Id), ("$m", message.Id), ("$f", a.FileName), ("$c", a.ContentType), ("$s", a.SizeBytes), ("$p", a.LocalPath), ct).ConfigureAwait(false);
            }

            await RecalculateFolderCountsCoreAsync(connection, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        DraftsChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<DraftContent?> GetDraftAsync(string draftId, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = DraftSelectSql + " WHERE Id = $id;";
            command.Parameters.AddParam("$id", draftId);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadDraft(reader) : null;
        }, cancellationToken);

    public Task<IReadOnlyList<string>> ListDraftIdsAsync(CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id FROM drafts;";
            var list = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(reader.GetString(0));
            }

            return (IReadOnlyList<string>)list;
        }, cancellationToken);

    /// <summary>由某封服务器草稿重新打开而来的本地草稿 Id（没有则 null）。</summary>
    public Task<string?> FindDraftIdBySourceAsync(string sourceMessageId, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id FROM drafts WHERE SourceMessageId = $s ORDER BY UpdatedAt DESC LIMIT 1;";
            command.Parameters.AddParam("$s", sourceMessageId);
            return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }, cancellationToken);

    /// <summary>删除草稿状态行与列表行（附件行随邮件级联删除）。返回它的来源服务器草稿 Id（若有）。</summary>
    public async Task<string?> DeleteDraftAsync(string draftId, CancellationToken cancellationToken = default)
    {
        string? source = null;
        await ExecuteAsync(async (connection, ct) =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (var read = Cmd(connection, transaction, "SELECT SourceMessageId FROM drafts WHERE Id = $id;"))
            {
                read.Parameters.AddParam("$id", draftId);
                source = await read.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            }

            await NonQueryAsync(connection, transaction, "DELETE FROM drafts WHERE Id = $id;", ("$id", draftId), ct).ConfigureAwait(false);
            await NonQueryAsync(connection, transaction, "DELETE FROM messages WHERE Id = $id;", ("$id", DraftMessagePrefix + draftId), ct).ConfigureAwait(false);
            await RecalculateFolderCountsCoreAsync(connection, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        DraftsChanged?.Invoke(this, EventArgs.Empty);
        return source;
    }

    private const string DraftSelectSql =
        "SELECT Id, AccountId, Mode, ToText, CcText, BccText, Subject, Body, InReplyTo, ReferencesJson, QuotedHtml, QuotedText, " +
        "AttachmentsJson, SourceMessageId, UpdatedAt FROM drafts";

    private static DraftContent ReadDraft(SqliteDataReader r)
    {
        static string? S(SqliteDataReader rd, int i) => rd.IsDBNull(i) ? null : rd.GetString(i);
        var refs = S(r, 9);
        var atts = S(r, 12);
        return new DraftContent(
            r.GetString(0),
            Guid.Parse(r.GetString(1)),
            r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
            Mode: r.GetString(2),
            InReplyTo: S(r, 8),
            References: refs is null ? null : JsonSerializer.Deserialize(refs, MailClientJsonContext.Default.ListString),
            QuotedHtml: S(r, 10),
            QuotedText: S(r, 11),
            Attachments: atts is null ? null : JsonSerializer.Deserialize(atts, MailClientJsonContext.Default.ListString),
            SourceMessageId: S(r, 13),
            UpdatedAt: SqliteHelpers.ParseDate(S(r, 14)));
    }
}
