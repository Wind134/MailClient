using System.Security.Cryptography;
using System.Text;

namespace MailClient.Auth;

/// <summary>OAuth 2.0 PKCE（RFC 7636）与 state 生成；纯函数，便于单测。</summary>
public static class Pkce
{
    /// <summary>生成 code_verifier：32 字节随机数 → base64url（43 字符，落在规范要求的 43–128 之内）。</summary>
    public static string CreateVerifier(int byteLength = 32)
    {
        if (byteLength is < 32 or > 96)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLength), "byteLength 需在 32–96 之间（对应 43–128 个字符）。");
        }

        return Base64Url(RandomNumberGenerator.GetBytes(byteLength));
    }

    /// <summary>S256：BASE64URL(SHA256(ASCII(verifier)))。</summary>
    public static string CreateChallenge(string verifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(verifier);
        return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(24));

    public static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
