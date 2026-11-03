using System.Text.Json;
using MailClient.Auth;
using MailClient.Core;

namespace MailClient.Providers.Setup;

/// <summary>账号的服务器连接设置（不含密码；整体存在凭据库里，数据库 schema 不变）。</summary>
public sealed record AccountConnection(
    SetupProviderKind Kind,
    string UserName,
    ImapServerSettings? Imap,
    SmtpServerSettings? Smtp);

/// <summary>
/// 每个账号在 <see cref="ICredentialStore"/> 里的条目：
/// <c>conn:{id}</c> = 连接设置 JSON；<c>imap:{id}</c> = “用户名\n密码/授权码”（仅 IMAP 类）；
/// OAuth 令牌由各自的 acquirer 管理（<c>oauth:gmail:{id}</c> / <c>msal:graph:{id}</c>）。
/// </summary>
public sealed class AccountConnectionStore
{
    private readonly ICredentialStore _store;

    public AccountConnectionStore(ICredentialStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public static string ConnectionKey(Guid id) => $"conn:{id:D}";

    public static string ImapSecretKey(Guid id) => $"imap:{id:D}";

    public static string GmailTokenKey(Guid id) => GmailEndpoints.SuggestVaultKey(id);

    public static string GraphTokenKey(Guid id) => MsalTokenCacheHelper.SuggestVaultKey(id);

    public async Task SaveAsync(Guid id, AccountConnection connection, string? password, CancellationToken ct = default)
    {
        await _store.SaveSecretAsync(ConnectionKey(id), JsonSerializer.Serialize(connection, AccountConnectionJsonContext.Default.AccountConnection), ct).ConfigureAwait(false);
        if (password is not null)
        {
            await _store.SaveSecretAsync(ImapSecretKey(id), connection.UserName + "\n" + password, ct).ConfigureAwait(false);
        }
    }

    public async Task<AccountConnection?> LoadAsync(Guid id, CancellationToken ct = default)
    {
        var json = await _store.GetSecretAsync(ConnectionKey(id), ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, AccountConnectionJsonContext.Default.AccountConnection);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>删除该账号在凭据库里的全部条目（连接设置、IMAP 密码、Gmail / Graph 令牌）。</summary>
    public async Task DeleteAllAsync(Guid id, CancellationToken ct = default)
    {
        foreach (var key in new[] { ConnectionKey(id), ImapSecretKey(id), GmailTokenKey(id), GraphTokenKey(id) })
        {
            await _store.DeleteSecretAsync(key, ct).ConfigureAwait(false);
        }
    }
}
