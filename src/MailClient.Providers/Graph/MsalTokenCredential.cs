using Azure.Core;
using MailClient.Auth;

namespace MailClient.Providers.Graph;

/// <summary>把 <see cref="MicrosoftGraphTokenAcquirer"/> 适配为 Azure.Core TokenCredential 供 Graph SDK 使用。</summary>
internal sealed class MsalTokenCredential : TokenCredential
{
    private readonly MicrosoftGraphTokenAcquirer _acquirer;
    private readonly bool _allowInteractive;

    public MsalTokenCredential(MicrosoftGraphTokenAcquirer acquirer, bool allowInteractive)
    {
        _acquirer = acquirer;
        _allowInteractive = allowInteractive;
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        var result = await _acquirer.AcquireAsync(_allowInteractive, cancellationToken).ConfigureAwait(false);
        return new AccessToken(result.AccessToken, result.ExpiresOn);
    }
}
