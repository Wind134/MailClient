using MailClient.Auth;
using Microsoft.Identity.Client;

namespace MailClient.Tests;

public class MicrosoftGraphTokenAcquirerTests
{
    [Fact]
    public void ResolveParentHwnd_returns_provider_value()
    {
        var expected = new IntPtr(0x12345);
        var actual = MicrosoftGraphTokenAcquirer.ResolveParentHwnd(() => expected, requireForBroker: true);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ResolveParentHwnd_throws_when_broker_requires_handle_but_zero()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => MicrosoftGraphTokenAcquirer.ResolveParentHwnd(() => IntPtr.Zero, requireForBroker: true));
        Assert.Contains("window_handle_required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveParentHwnd_throws_when_broker_requires_handle_but_null_provider()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => MicrosoftGraphTokenAcquirer.ResolveParentHwnd(null, requireForBroker: true));
        Assert.Contains("window_handle_required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveParentHwnd_allows_zero_when_broker_not_required()
    {
        Assert.Equal(IntPtr.Zero, MicrosoftGraphTokenAcquirer.ResolveParentHwnd(null, requireForBroker: false));
        Assert.Equal(IntPtr.Zero, MicrosoftGraphTokenAcquirer.ResolveParentHwnd(() => IntPtr.Zero, requireForBroker: false));
    }

    [Fact]
    public void Interactive_builder_has_IntPtr_overload_not_Func()
    {
        // 回归：AcquireTokenInteractiveParameterBuilder 不能接 Func<IntPtr>，
        // 误传 Func 会命中 object 重载并把委托当成父窗口 → window_handle_required。
        var interactive = typeof(Microsoft.Identity.Client.AcquireTokenInteractiveParameterBuilder);
        var methods = interactive.GetMethods()
            .Where(m => m.Name == "WithParentActivityOrWindow")
            .Select(m => m.GetParameters().Single().ParameterType)
            .ToArray();

        Assert.Contains(typeof(IntPtr), methods);
        Assert.Contains(typeof(object), methods);
        Assert.DoesNotContain(typeof(Func<IntPtr>), methods);

        var pca = typeof(Microsoft.Identity.Client.PublicClientApplicationBuilder);
        var pcaMethods = pca.GetMethods()
            .Where(m => m.Name == "WithParentActivityOrWindow")
            .Select(m => m.GetParameters().Single().ParameterType)
            .ToArray();
        Assert.Contains(typeof(Func<IntPtr>), pcaMethods);
    }

    [Fact]
    public void UseWamBroker_defaults_to_false()
    {
        var cfg = new MailClient.Core.MicrosoftGraphAccountConfig(
            Guid.NewGuid(), "a@b.com", "A", "client-id");
        Assert.False(cfg.UseWamBroker);
    }

    [Fact]
    public void BrokerRedirectUri_includes_client_id()
    {
        var uri = MicrosoftGraphTokenAcquirer.BrokerRedirectUri("abcd-1234");
        Assert.Equal("ms-appx-web://microsoft.aad.brokerplugin/abcd-1234", uri);
    }

    [Theory]
    [InlineData("WAM Error\n\n2156265475")]
    [InlineData("ApiContractViolation")]
    [InlineData("broker failed")]
    [InlineData("执行此操作需要 Internet")]
    [InlineData("0x800704CF network")]
    public void IsWamBrokerFailure_detects_known_signals(string message)
    {
        var ex = new MsalClientException("wam_error", message);
        Assert.True(MicrosoftGraphTokenAcquirer.IsWamBrokerFailure(ex));
        Assert.True(MicrosoftGraphTokenAcquirer.ShouldRetryWithoutBroker(ex, brokerWasEnabled: true));
        Assert.False(MicrosoftGraphTokenAcquirer.ShouldRetryWithoutBroker(ex, brokerWasEnabled: false));
    }

    [Fact]
    public void IsWamBrokerFailure_false_for_ordinary_cancel()
    {
        var ex = new MsalClientException("authentication_canceled", "user canceled");
        Assert.False(MicrosoftGraphTokenAcquirer.IsWamBrokerFailure(ex));
    }

    [Fact]
    public void FormatInteractiveFailure_includes_redirect_hint_and_client_id()
    {
        var ex = new MsalClientException("wam_provider_error_2156265475", "WAM Error\n\n2156265475");
        var msg = MicrosoftGraphTokenAcquirer.FormatInteractiveFailure(ex, "11111111-2222-3333-4444-555555555555");
        Assert.Contains("ms-appx-web://microsoft.aad.brokerplugin/11111111-2222-3333-4444-555555555555", msg);
        Assert.Contains("11111111-2222-3333-4444-555555555555", msg);
        Assert.Contains("身份验证", msg);
        Assert.Contains("移动和桌面", msg);
    }

    [Fact]
    public async Task AcquireWithBrokerFallback_retries_without_broker_on_WAM_failure()
    {
        var calls = new List<bool>();
        var result = await MicrosoftGraphTokenAcquirer.AcquireWithBrokerFallbackAsync(
            preferBroker: true,
            async (useBroker, ct) =>
            {
                calls.Add(useBroker);
                await Task.Yield();
                if (useBroker)
                {
                    throw new MsalClientException(
                        "wam_provider_error_2156265475",
                        "WAM Error\n\n2156265475");
                }

                return "ok-browser";
            });

        Assert.Equal("ok-browser", result);
        Assert.Equal(new[] { true, false }, calls);
    }

    [Fact]
    public async Task AcquireWithBrokerFallback_does_not_retry_when_broker_disabled()
    {
        var calls = 0;
        var ex = await Assert.ThrowsAsync<MsalClientException>(async () =>
            await MicrosoftGraphTokenAcquirer.AcquireWithBrokerFallbackAsync<string>(
                preferBroker: false,
                async (useBroker, ct) =>
                {
                    calls++;
                    Assert.False(useBroker);
                    await Task.Yield();
                    throw new MsalClientException("wam_provider_error_2156265475", "WAM Error\n\n2156265475");
                }));

        Assert.Equal(1, calls);
        Assert.Contains("2156265475", ex.Message);
    }
}
