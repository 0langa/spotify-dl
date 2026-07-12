using PlaylistDl.App.Services;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class BackendClientTests
{
    [Fact]
    public void ConfiguredBackendOverridesEnvironment()
    {
        Assert.Equal(
            @"C:\chosen\playlistdl-backend.exe",
            BackendClient.SelectBackendOverride(
                @" C:\chosen\playlistdl-backend.exe ",
                @"C:\environment\playlistdl-backend.exe"));
    }

    [Fact]
    public void EnvironmentBackendRemainsCompatibleFallback()
    {
        Assert.Equal(
            @"C:\environment\playlistdl-backend.exe",
            BackendClient.SelectBackendOverride(null, @" C:\environment\playlistdl-backend.exe "));
    }

    [Fact]
    public void EmptyOverridesUseBundledBackend()
    {
        Assert.Null(BackendClient.SelectBackendOverride(" ", null));
    }
}
