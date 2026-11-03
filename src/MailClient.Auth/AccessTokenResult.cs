namespace MailClient.Auth;

public sealed record AccessTokenResult(
    string AccessToken,
    DateTimeOffset ExpiresOn,
    string? AccountUsername,
    string? HomeAccountId);
