using MailClient.Auth;
using MailClient.Core;
using MailClient.Providers;

namespace MailClient.Tests;

/// <summary>
/// 真实 Graph 联调：设置环境变量 MAILCLIENT_GRAPH_CLIENT_ID 后手动跑。
/// 可选 MAILCLIENT_GRAPH_TENANT_ID（默认 common）。
/// </summary>
public class LiveGraphTests
{
    private static bool LiveEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MAILCLIENT_GRAPH_CLIENT_ID"));

    [Fact]
    public async Task Connect_list_folders_when_env_set()
    {
        if (!LiveEnabled)
        {
            return; // skip quietly without live credentials
        }

        var clientId = Environment.GetEnvironmentVariable("MAILCLIENT_GRAPH_CLIENT_ID")!;
        var tenant = Environment.GetEnvironmentVariable("MAILCLIENT_GRAPH_TENANT_ID") ?? "common";
        var config = new MicrosoftGraphAccountConfig(
            AccountId: Guid.NewGuid(),
            EmailAddress: "live@example.com",
            DisplayName: "Live",
            ClientId: clientId,
            TenantId: tenant,
            UseWamBroker: true);

        var store = new InMemoryCredentialStore();
        await using var provider = new MicrosoftGraphProvider(config, store);
        await provider.ConnectAsync();
        var folders = await provider.GetFoldersAsync();
        Assert.NotEmpty(folders);
        await provider.DisconnectAsync();
    }
}
