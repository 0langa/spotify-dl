using System.Text;
using System.Text.Json;
using PlaylistDl.App.Services;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class BackendClientTests : IDisposable
{
    private readonly string _logDirectory = Path.Combine(
        Path.GetTempPath(), $"playlistdl-client-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_logDirectory))
        {
            Directory.Delete(_logDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task AnErrorAnsweringARequestReachesOnlyThatRequest()
    {
        // The main window treats a broadcast error during a download as the job stopping,
        // so a failed source search or diagnosis must not be broadcast while one runs.
        var client = new BackendClient(runLog: new RunLog(_logDirectory));
        var search = client.ExpectReply("search-1");
        var broadcast = new List<string?>();
        client.EventReceived += (_, message) => broadcast.Add(
            $"{message.GetProperty("type").GetString()}:{message.GetProperty("request_id").GetString()}");
        var lines =
            """{"type":"error","request_id":"search-1","message":"search failed"}""" + "\n" +
            """{"type":"error","request_id":"start-1","message":"worker stopped"}""" + "\n" +
            """{"type":"pong","request_id":"ping-1"}""" + "\n";
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(lines)));

        await client.ReadEventsCoreAsync(reader, CancellationToken.None);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => search);
        Assert.Equal("search failed", failure.Message);
        Assert.Equal(["error:start-1", "pong:ping-1"], broadcast);
    }

    [Fact]
    public async Task AReplyIsStillBroadcastAfterAnsweringItsRequest()
    {
        var client = new BackendClient(runLog: new RunLog(_logDirectory));
        var resolve = client.ExpectReply("resolve-1");
        var broadcast = new List<string?>();
        client.EventReceived += (_, message) => broadcast.Add(message.GetProperty("type").GetString());
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(
            """{"type":"playlist_resolved","request_id":"resolve-1","playlist":{}}""" + "\n")));

        await client.ReadEventsCoreAsync(reader, CancellationToken.None);

        Assert.Equal(JsonValueKind.Object, (await resolve).GetProperty("playlist").ValueKind);
        Assert.Equal(["playlist_resolved"], broadcast);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    public void BackendProtocolMustMatchClient(int? protocol, bool expected)
    {
        Assert.Equal(expected, BackendClient.IsSupportedProtocol(protocol));
    }

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
