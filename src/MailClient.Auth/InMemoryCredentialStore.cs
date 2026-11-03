using System.Collections.Concurrent;

namespace MailClient.Auth;

/// <summary>进程内凭据存储，供单元测试与显式注入场景使用。</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public Task SaveSecretAsync(string key, string secret, CancellationToken cancellationToken = default)
    {
        _secrets[key] = secret;
        return Task.CompletedTask;
    }

    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        _secrets.TryGetValue(key, out var value);
        return Task.FromResult<string?>(value);
    }

    public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        _secrets.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
