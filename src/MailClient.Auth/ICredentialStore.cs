namespace MailClient.Auth;

public interface ICredentialStore
{
    Task SaveSecretAsync(string key, string secret, CancellationToken cancellationToken = default);
    Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default);
    Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default);
}
