using System.Text;
using MailClient.Core;

namespace MailClient.Auth;

/// <summary>构造 Google OAuth 2.0 授权请求 URL（已安装应用 + PKCE）。</summary>
public static class GoogleAuthorizationUrl
{
    public static Uri Build(
        string clientId,
        string redirectUri,
        IEnumerable<string> scopes,
        string state,
        string codeChallenge,
        string? loginHint = null,
        bool forceConsent = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeChallenge);

        var scope = string.Join(' ', scopes);
        if (string.IsNullOrWhiteSpace(scope))
        {
            throw new ArgumentException("至少需要一个 scope。", nameof(scopes));
        }

        var pairs = new List<(string, string)>
        {
            ("client_id", clientId),
            ("redirect_uri", redirectUri),
            ("response_type", "code"),
            ("scope", scope),
            ("state", state),
            ("code_challenge", codeChallenge),
            ("code_challenge_method", "S256"),
            // offline 才会发 refresh token；consent 保证重复授权时也重新发放。
            ("access_type", "offline"),
            ("include_granted_scopes", "true")
        };

        if (forceConsent)
        {
            pairs.Add(("prompt", "consent"));
        }

        if (!string.IsNullOrWhiteSpace(loginHint))
        {
            pairs.Add(("login_hint", loginHint));
        }

        var sb = new StringBuilder(GmailEndpoints.AuthorizationEndpoint).Append('?');
        for (var i = 0; i < pairs.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('&');
            }

            sb.Append(Uri.EscapeDataString(pairs[i].Item1)).Append('=').Append(Uri.EscapeDataString(pairs[i].Item2));
        }

        return new Uri(sb.ToString());
    }
}
