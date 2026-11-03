using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MailClient.Core;

namespace MailClient.Auth;

/// <summary>
/// 持久化凭据库（Windows DPAPI · 当前 Windows 用户范围）。
/// 所有条目存放在一个 JSON 文件里：key → Base64(CryptProtectData(secret))。
/// 不用 Credential Manager / PasswordVault 的原因：单条凭据上限约 2.5 KB，而 MSAL 令牌缓存、
/// Google 令牌 JSON 动辄几十 KB。文件只能由同一 Windows 用户登录会话解密，换机器 / 换用户即失效。
/// 默认路径：%LocalAppData%\MailClient\credentials.dat。
/// </summary>
public sealed class DpapiCredentialStore : ICredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MailClient.CredentialStore.v1");

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string>? _cache;

    public DpapiCredentialStore(string? path = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DpapiCredentialStore 仅支持 Windows。");
        }

        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MailClient",
        "credentials.dat");

    public string FilePath => _path;

    public async Task SaveSecretAsync(string key, string secret, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(secret);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var map = await LoadAsync(cancellationToken).ConfigureAwait(false);
            map[key] = Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(secret)));
            await PersistAsync(map, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var map = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!map.TryGetValue(key, out var packed))
            {
                return null;
            }

            try
            {
                return Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(packed)));
            }
            catch (Exception ex) when (ex is Win32Exception or FormatException)
            {
                // 其他用户 / 其他机器写入的条目：视为不存在，让上层重新登录。
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var map = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (map.Remove(key))
            {
                await PersistAsync(map, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken ct)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (File.Exists(_path))
        {
            try
            {
                await using var stream = File.OpenRead(_path);
                var loaded = await JsonSerializer.DeserializeAsync(stream, MailClientJsonContext.Default.DictionaryStringString, cancellationToken: ct)
                    .ConfigureAwait(false);
                if (loaded is not null)
                {
                    foreach (var (k, v) in loaded)
                    {
                        map[k] = v;
                    }
                }
            }
            catch (JsonException)
            {
                // 文件损坏：当作空库（旧文件留在原处，下次保存时被覆盖）。
            }
        }

        _cache = map;
        return map;
    }

    private async Task PersistAsync(Dictionary<string, string> map, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = _path + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, map, MailClientJsonContext.Default.DictionaryStringString, cancellationToken: ct).ConfigureAwait(false);
        }

        File.Move(tmp, _path, overwrite: true);
    }

    // ───────── DPAPI P/Invoke ─────────

    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static byte[] Protect(byte[] plain) => Transform(plain, protect: true);

    private static byte[] Unprotect(byte[] cipher) => Transform(cipher, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var outBlob = default(DataBlob);
        try
        {
            var inBlob = new DataBlob { cbData = input.Length, pbData = inHandle.AddrOfPinnedObject() };
            var entBlob = new DataBlob { cbData = Entropy.Length, pbData = entHandle.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref inBlob, "MailClient", ref entBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob);
            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            if (outBlob.pbData != IntPtr.Zero)
            {
                LocalFree(outBlob.pbData);
            }

            inHandle.Free();
            entHandle.Free();
        }
    }
}
