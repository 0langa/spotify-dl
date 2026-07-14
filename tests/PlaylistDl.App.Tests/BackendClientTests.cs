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

    [Theory]
    [InlineData("0.8.0", "0.9.0", true)]
    [InlineData("0.9.0", "0.9.0", false)]
    [InlineData("0.10.0", "0.9.0", false)]
    [InlineData("dev", "0.9.0", false)]
    [InlineData("0.8.0", null, false)]
    public void OutdatedBackendDetectionRequiresComparableOlderVersion(
        string launchedVersion,
        string? bundledVersion,
        bool expected)
    {
        Assert.Equal(
            expected,
            BackendClient.IsBackendVersionOutdated(launchedVersion, bundledVersion));
    }
}
