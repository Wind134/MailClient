using MailClient.Auth;
using MailClient.Core;
using MailClient.Providers;

namespace MailClient.Tests;

/// <summary>
/// 真实 Graph 联调（手动执行）。需要环境变量 MAILCLIENT_GRAPH_CLIENT_ID，
/// 可选 MAILCLIENT_GRAPH_TENANT_ID（默认 common）。未配置时用例标记为跳过。
/// </summary>
[Trait("Category", "Live")]
public class LiveGraphTests
{
    [SkippableFact]
    public async Task Connect_list_folders_when_env_set()
    {
        var clientId = LiveEnvironment.Require("MAILCLIENT_GRAPH_CLIENT_ID");
        var tenant = LiveEnvironment.Optional("MAILCLIENT_GRAPH_TENANT_ID") ?? "common";
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
