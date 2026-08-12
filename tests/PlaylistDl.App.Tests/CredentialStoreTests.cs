using PlaylistDl.App.Services;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class CredentialStoreTests : IDisposable
{
    private readonly string _target = $"PlaylistDL-Tests:{Guid.NewGuid():N}";

    private CredentialStore Store => new(_target);

    [Fact]
    public void SecretsRoundTripThroughTheWindowsVault()
    {
        var stored = new SpotifyCredentials("client-id-value", "client-secret-value");

        Assert.True(Store.Save(stored));
        var loaded = Store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(stored.ClientId, loaded.ClientId);
        Assert.Equal(stored.ClientSecret, loaded.ClientSecret);
    }

    [Fact]
    public void NothingStoredLoadsAsNull()
    {
        Assert.Null(Store.Load());
    }

    [Fact]
    public void SavingAgainReplacesTheStoredPair()
    {
        Store.Save(new SpotifyCredentials("first", "one"));
        Store.Save(new SpotifyCredentials("second", "two"));

        var loaded = Store.Load();

        Assert.Equal("second", loaded!.ClientId);
        Assert.Equal("two", loaded.ClientSecret);
    }

    [Fact]
    public void DeleteRemovesThePairAndIsSafeToRepeat()
    {
        Store.Save(new SpotifyCredentials("client", "secret"));

        Assert.True(Store.Delete());
        Assert.Null(Store.Load());
        // Deleting what is not there is the same successful outcome for the caller.
        Assert.True(Store.Delete());
    }

    [Fact]
    public void UnicodeSecretsSurviveIntact()
    {
        var stored = new SpotifyCredentials("clientid", "sécret-with-ünïcode-ø");

        Store.Save(stored);

        Assert.Equal(stored.ClientSecret, Store.Load()!.ClientSecret);
    }

    public void Dispose() => Store.Delete();
}
