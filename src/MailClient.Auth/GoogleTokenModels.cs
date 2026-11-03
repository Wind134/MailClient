using System.Text;
using System.Text.Json;

namespace MailClient.Auth;

/// <summary>Google OAuth 错误（token 端点 error / error_description，或回调里的 error）。</summary>
public sealed class GoogleOAuthException : Exception
{
    public GoogleOAuthException(string error, string? description)
        : base(string.IsNullOrWhiteSpace(description) ? $"Google OAuth 错误：{error}" : $"Google OAuth 错误：{error}（{description}）")
    {
        Error = error;
        ErrorDescription = description;
    }

    public string Error { get; }
    public string? ErrorDescription { get; }

    /// <summary>refresh token 已失效 / 被撤销 / 过期（需要重新交互登录）。</summary>
    public bool IsInvalidGrant => string.Equals(Error, "invalid_grant", StringComparison.OrdinalIgnoreCase);
}

/// <summary>本地没有可用令牌且不允许交互登录，或 refresh token 已失效。</summary>
public sealed class GmailAuthRequiredException : Exception
{
    public GmailAuthRequiredException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>持久化到 <see cref="ICredentialStore"/> 的令牌集合（JSON）。</summary>
public sealed record GmailTokenSet(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresOn,
    string? Email,
    string? Scope);

/// <summary>token 端点响应（解析后）。RefreshToken 仅在授权码换取时返回；刷新时通常为 null。</summary>
public sealed record GoogleTokenResponse(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresOn,
    string? IdToken,
    string? Scope)
{
    public static GoogleTokenResponse Parse(string json, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        {
            var desc = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            throw new GoogleOAuthException(err.ValueKind == JsonValueKind.String ? err.GetString()! : err.ToString(), desc);
        }

        var access = Str(root, "access_token")
            ?? throw new GoogleOAuthException("invalid_response", "响应中缺少 access_token。");
        var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) ? seconds : 3600;
        return new GoogleTokenResponse(
            access,
            Str(root, "refresh_token"),
            now.AddSeconds(expiresIn),
            Str(root, "id_token"),
            Str(root, "scope"));
    }

    private static string? Str(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public static class GoogleIdToken
{
    /// <summary>
    /// 读取 id_token 载荷里的 email。仅用于“得知登录了哪个邮箱”：
    /// id_token 是直接经 TLS 从 Google token 端点取得的，所以这里不再验签。
    /// </summary>
    public static string? TryReadEmail(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return null;
        }

        var parts = idToken.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return doc.RootElement.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String
                ? email.GetString()
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>令牌在凭据库中的读写（与 <see cref="MsalTokenCacheHelper"/> 同一思路，但异步且为 JSON）。</summary>
public sealed class GmailTokenStore
{
    private readonly ICredentialStore _store;

    public GmailTokenStore(ICredentialStore store, string vaultKey)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultKey);
        VaultKey = vaultKey;
    }

    public string VaultKey { get; }

    public async Task<GmailTokenSet?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var raw = await _store.GetSecretAsync(VaultKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            var set = JsonSerializer.Deserialize(raw, GmailAuthJsonContext.Default.GmailTokenSet);
            return set is { RefreshToken.Length: > 0 } ? set : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task SaveAsync(GmailTokenSet tokens, CancellationToken cancellationToken = default)
        => _store.SaveSecretAsync(VaultKey, JsonSerializer.Serialize(tokens, GmailAuthJsonContext.Default.GmailTokenSet), cancellationToken);

    public Task DeleteAsync(CancellationToken cancellationToken = default)
        => _store.DeleteSecretAsync(VaultKey, cancellationToken);
}
