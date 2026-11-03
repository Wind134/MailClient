using Microsoft.Data.Sqlite;
using MailClient.Core;

namespace MailClient.Storage;

public sealed partial class MailDatabase
{
    /// <summary>写入已拉取的正文与附件元数据，并标记 BodyFetched=1；顺带用正文重算 Preview（去掉 CSS 污染）。</summary>
    public Task SaveMessageBodyAsync(
        string messageId,
        MessageBodyContent content,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(async (connection, ct) =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            var preview = HtmlPreview.BuildPreviewFromParts(content.BodyText, content.BodyHtml);

            await using (var command = Cmd(connection, transaction, """
                UPDATE messages SET
                    BodyText = $text,
                    BodyHtml = $html,
                    BodyFetched = 1,
                    BodyAccessedAt = $accessedAt,
                    Preview = CASE WHEN length($preview) > 0 THEN $preview ELSE Preview END,
                    HasAttachments = CASE WHEN $hasAtt = 1 THEN 1 ELSE HasAttachments END
                WHERE Id = $id;
                """))
            {
                command.Parameters.AddParam("$id", messageId);
                command.Parameters.AddParam("$text", content.BodyText);
                command.Parameters.AddParam("$html", content.BodyHtml);
                command.Parameters.AddParam("$preview", preview);
                command.Parameters.AddParam("$accessedAt", SqliteHelpers.Fmt(DateTimeOffset.UtcNow));
                var hasNonInline = content.Attachments.Any(a => !a.IsInline);
                command.Parameters.AddParam("$hasAtt", hasNonInline ? 1 : 0);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // 保留已下载到本地的 LocalPath：按 FileName 合并
            Dictionary<string, string?> existingPaths;
            await using (var q = Cmd(connection, transaction, "SELECT FileName, LocalPath FROM attachments WHERE MessageId = $id;"))
            {
                q.Parameters.AddParam("$id", messageId);
                existingPaths = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                await using var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    existingPaths[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetString(1);
                }
            }

            await NonQueryAsync(connection, transaction, "DELETE FROM attachments WHERE MessageId = $id;", ("$id", messageId), ct)
                .ConfigureAwait(false);

            var index = 0;
            foreach (var a in content.Attachments)
            {
                index++;
                existingPaths.TryGetValue(a.FileName, out var localPath);
                var id = $"{messageId}:att:{index}";
                await using var ins = Cmd(connection, transaction, """
                    INSERT INTO attachments (Id, MessageId, FileName, ContentType, SizeBytes, ContentId, IsInline, LocalPath, RemoteId)
                    VALUES ($id, $messageId, $fileName, $contentType, $size, $contentId, $isInline, $localPath, $remoteId);
                    """);
                ins.Parameters.AddParam("$id", id);
                ins.Parameters.AddParam("$messageId", messageId);
                ins.Parameters.AddParam("$fileName", a.FileName);
                ins.Parameters.AddParam("$contentType", a.ContentType);
                ins.Parameters.AddParam("$size", a.SizeBytes);
                ins.Parameters.AddParam("$contentId", a.ContentId);
                ins.Parameters.AddParam("$isInline", a.IsInline ? 1 : 0);
                ins.Parameters.AddParam("$localPath", localPath);
                ins.Parameters.AddParam("$remoteId", a.RemoteId);
                await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>
    /// 重算看起来像 CSS 的 Preview。优先用 BodyHtml；BodyText 若也被污染则忽略。
    /// 不修改 BodyHtml。返回更新行数。
    /// </summary>
    public Task<int> RepairBadPreviewsAsync(int maxChars = 200, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var select = connection.CreateCommand();
            select.CommandText = """
                SELECT Id, Preview, BodyText, BodyHtml FROM messages
                WHERE Preview IS NOT NULL AND length(Preview) > 0 AND (
                    instr(Preview, '{') > 0
                    OR instr(lower(Preview), 'mso-') > 0
                    OR instr(lower(Preview), '#outlook') > 0
                    OR instr(lower(Preview), 'border-collapse') > 0
                    OR instr(lower(Preview), '-webkit-text-size') > 0
                    OR instr(lower(Preview), 'body.generic') > 0
                    OR instr(lower(Preview), 'body, table') > 0
                    OR instr(lower(Preview), 'table td') > 0
                    OR Preview LIKE '-- %'
                );
                """;

            var updates = new List<(string Id, string Preview, string? BodyText)>();
            await using (var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var id = reader.GetString(0);
                    var preview = reader.IsDBNull(1) ? null : reader.GetString(1);
                    var bodyText = reader.IsDBNull(2) ? null : reader.GetString(2);
                    var bodyHtml = reader.IsDBNull(3) ? null : reader.GetString(3);
                    if (!HtmlPreview.LooksLikeCss(preview))
                    {
                        continue;
                    }

                    string? plain = bodyText;
                    if (HtmlPreview.LooksLikeCss(plain))
                    {
                        plain = null;
                    }

                    var next = HtmlPreview.BuildPreviewFromParts(plain, bodyHtml, maxChars);
                    // 无可用正文时：清空 CSS 污染预览，避免列表继续显示样式表碎片
                    if (string.IsNullOrWhiteSpace(next))
                    {
                        next = string.Empty;
                    }
                    else if (string.Equals(next, preview, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // BodyText 也被 CSS 污染时顺带清掉（有 BodyHtml 可读）
                    string? fixedText = null;
                    if (bodyText is not null && HtmlPreview.LooksLikeCss(bodyText) && !string.IsNullOrWhiteSpace(bodyHtml))
                    {
                        fixedText = HtmlPreview.HtmlToPreview(bodyHtml, maxChars: 8_000);
                    }

                    updates.Add((id, next, fixedText));
                }
            }

            if (updates.Count == 0)
            {
                return 0;
            }

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            foreach (var (id, preview, fixedText) in updates)
            {
                if (fixedText is not null)
                {
                    await using var cmd = Cmd(connection, transaction, """
                        UPDATE messages SET Preview = $preview, BodyText = $text WHERE Id = $id;
                        """);
                    cmd.Parameters.AddParam("$preview", preview);
                    cmd.Parameters.AddParam("$text", fixedText);
                    cmd.Parameters.AddParam("$id", id);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    await using var cmd = Cmd(connection, transaction, """
                        UPDATE messages SET Preview = $preview WHERE Id = $id;
                        """);
                    cmd.Parameters.AddParam("$preview", preview);
                    cmd.Parameters.AddParam("$id", id);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return updates.Count;
        }, cancellationToken);

    public Task<AttachmentEntity?> GetAttachmentAsync(string attachmentId, CancellationToken cancellationToken = default)
        => QueryAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, MessageId, FileName, ContentType, SizeBytes, ContentId, IsInline, LocalPath, RemoteId
                FROM attachments WHERE Id = $id;
                """;
            command.Parameters.AddParam("$id", attachmentId);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            return new AttachmentEntity(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6) != 0,
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.FieldCount > 8 && !reader.IsDBNull(8) ? reader.GetString(8) : null);
        }, cancellationToken);

    public Task SetAttachmentLocalPathAsync(string attachmentId, string localPath, CancellationToken cancellationToken = default)
        => ExecuteAsync(async (connection, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE attachments SET LocalPath = $p WHERE Id = $id;";
            command.Parameters.AddParam("$p", localPath);
            command.Parameters.AddParam("$id", attachmentId);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, cancellationToken);
}
