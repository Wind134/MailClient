using System.Text;
using MailClient.Auth;

namespace MailClient.Tests;

public class MsalTokenCacheHelperTests
{
    [Fact]
    public void Roundtrip_bytes_through_InMemoryCredentialStore()
    {
        var store = new InMemoryCredentialStore();
        var helper = new MsalTokenCacheHelper(store, "msal:test");
        var payload = Encoding.UTF8.GetBytes("fake-msal-v3-cache-bytes");

        helper.Save(payload);
        var loaded = helper.Load();

        Assert.NotNull(loaded);
        Assert.Equal(payload, loaded);

        // 新 helper 实例应从 store 再读
        var helper2 = new MsalTokenCacheHelper(store, "msal:test");
        var loaded2 = helper2.Load();
        Assert.Equal(payload, loaded2);
    }

    [Fact]
    public void Encode_decode_helpers()
    {
        var raw = new byte[] { 1, 2, 3, 4, 5 };
        var b64 = MsalTokenCacheHelper.EncodeForStore(raw);
        Assert.Equal(raw, MsalTokenCacheHelper.DecodeFromStore(b64));
        Assert.Null(MsalTokenCacheHelper.DecodeFromStore(null));
    }

    [Fact]
    public void Save_null_deletes_secret()
    {
        var store = new InMemoryCredentialStore();
        var helper = new MsalTokenCacheHelper(store, "msal:del");
        helper.Save(Encoding.UTF8.GetBytes("x"));
        helper.Save(null);

        var again = new MsalTokenCacheHelper(store, "msal:del");
        Assert.Null(again.Load());
    }

    [Fact]
    public void SuggestVaultKey_contains_account()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal($"msal:graph:{id:D}", MsalTokenCacheHelper.SuggestVaultKey(id));
    }
}
