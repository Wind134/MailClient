using System.Text.Json.Serialization;
using MailClient.Core;

namespace MailClient.Providers.Setup;

/// <summary>账号连接设置 JSON（枚举按字符串读写，与历史 JsonStringEnumConverter 行为一致）。</summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AccountConnection))]
[JsonSerializable(typeof(ImapServerSettings))]
[JsonSerializable(typeof(SmtpServerSettings))]
[JsonSerializable(typeof(SetupProviderKind))]
[JsonSerializable(typeof(MailSecureSocketOptions))]
public partial class AccountConnectionJsonContext : JsonSerializerContext
{
}
