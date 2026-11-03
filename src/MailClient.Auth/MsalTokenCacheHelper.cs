using System.Text;

namespace MailClient.Auth;

/// <summary>
/// 将 MSAL 令牌缓存（SerializeMsalV3 字节）以 Base64 存入 <see cref="ICredentialStore"/>。
/// MSAL 通知回调是同步的，因此这里用同步封装；调用方勿在 UI 线程长时间阻塞。
/// </summary>
public sealed class MsalTokenCacheHelper
{
    private readonly ICredentialStore _store;
    private readonly string _vaultKey;
    private byte[]? _memory;

    public MsalTokenCacheHelper(ICredentialStore store, string vaultKey)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (string.IsNullOrWhiteSpace(vaultKey))
        {
            throw new ArgumentException("vaultKey 不能为空。", nameof(vaultKey));
        }

        _vaultKey = vaultKey;
    }

    public string VaultKey => _vaultKey;

    /// <summary>从凭据库加载缓存字节（无则 null）。</summary>
    public byte[]? Load()
    {
        if (_memory is not null)
        {
            return _memory.Length == 0 ? null : _memory;
        }

        var packed = _store.GetSecretAsync(_vaultKey).GetAwaiter().GetResult();
        if (string.IsNullOrEmpty(packed))
        {
            _memory = Array.Empty<byte>();
            return null;
        }

        try
        {
            _memory = Convert.FromBase64String(packed);
            return _memory.Length == 0 ? null : _memory;
        }
        catch (FormatException)
        {
            _memory = Array.Empty<byte>();
            return null;
        }
    }

    /// <summary>保存缓存字节到内存与凭据库。</summary>
    public void Save(byte[]? data)
    {
        _memory = data ?? Array.Empty<byte>();
        if (_memory.Length == 0)
        {
            _store.DeleteSecretAsync(_vaultKey).GetAwaiter().GetResult();
            return;
        }

        var b64 = Convert.ToBase64String(_memory);
        _store.SaveSecretAsync(_vaultKey, b64).GetAwaiter().GetResult();
    }

    /// <summary>仅测试：往返 Base64 编解码是否与 store 一致。</summary>
    public static string EncodeForStore(byte[] data) => Convert.ToBase64String(data);

    public static byte[]? DecodeFromStore(string? packed)
    {
        if (string.IsNullOrEmpty(packed))
        {
            return null;
        }

        return Convert.FromBase64String(packed);
    }

    public static string SuggestVaultKey(Guid accountId) => $"msal:graph:{accountId:D}";
}
