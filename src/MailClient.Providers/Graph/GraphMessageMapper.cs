using System.Security.Cryptography;
using System.Text;
using MailClient.Core;
using Microsoft.Graph.Models;

namespace MailClient.Providers.Graph;

/// <summary>Graph <see cref="Message"/> → 领域同步模型。</summary>
public static class GraphMessageMapper
{
    public static readonly string[] DefaultSelect =
    [
        "id",
        "subject",
        "bodyPreview",
        "from",
        "receivedDateTime",
        "sentDateTime",
        "isRead",
        "flag",
        "hasAttachments",
        "internetMessageId",
        "isDraft",
        "parentFolderId"
    ];

    public static MailMessageSyncItem ToSyncItem(
        Guid accountId,
        string folderId,
        Message message,
        int previewMaxChars = 200)
    {
        ArgumentNullException.ThrowIfNull(message);
        var graphId = message.Id ?? throw new ArgumentException("Message.Id 为空。");

        var fromEmail = message.From?.EmailAddress;
        var fromAddress = fromEmail?.Address ?? string.Empty;
        var fromName = string.IsNullOrWhiteSpace(fromEmail?.Name) ? null : fromEmail!.Name;
        var fromDisplay = FormatFromDisplay(fromAddress, fromName);

        var preview = Truncate(message.BodyPreview ?? string.Empty, previewMaxChars);
        var received = message.ReceivedDateTime ?? message.SentDateTime ?? DateTimeOffset.UtcNow;
        var isFlagged = message.Flag?.FlagStatus == FollowupFlagStatus.Flagged;
        var localId = BuildMessageId(accountId, graphId);

        var summary = new MailMessageSummary(
            Id: localId,
            FolderId: folderId,
            Subject: message.Subject ?? string.Empty,
            From: fromDisplay,
            ReceivedAt: received,
            IsRead: message.IsRead == true,
            IsFlagged: isFlagged,
            Preview: preview);

        return new MailMessageSyncItem(
            Summary: summary,
            RemoteUid: StableUid(graphId),
            RfcMessageId: message.InternetMessageId,
            FromAddress: fromAddress,
            FromName: fromName,
            HasAttachments: message.HasAttachments == true,
            SizeBytes: 0,
            IsDraft: message.IsDraft == true,
            SentAt: message.SentDateTime);
    }

    public static string BuildMessageId(Guid accountId, string graphMessageId)
        => $"{accountId:D}:{graphMessageId}";

    public static string FormatFromDisplay(string address, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return address;
        }

        return string.IsNullOrWhiteSpace(address) ? name : $"{name} <{address}>";
    }

    public static string Truncate(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text ?? string.Empty;
        }

        return text[..maxChars];
    }

    /// <summary>将 Graph 字符串 Id 稳定映射为 long，供 MessageEntity.RemoteUid 使用。</summary>
    public static long StableUid(string graphId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(graphId));
        var value = BitConverter.ToInt64(hash, 0);
        return value == long.MinValue ? 0 : Math.Abs(value);
    }
}
