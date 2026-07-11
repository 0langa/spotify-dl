using System.Net;
using System.Net.Http;
using System.Text;
using PlaylistDl.App.Services;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("2.0.0-beta.1", 2, 0, 0)]
    public void ParsesReleaseTags(string tag, int major, int minor, int build)
    {
        Assert.Equal(new Version(major, minor, build), UpdateService.ParseVersion(tag));
    }

    [Fact]
    public async Task ReturnsNewerPublishedRelease()
    {
        using var client = new HttpClient(new JsonHandler(
            """{"tag_name":"v1.3.0","html_url":"https://github.com/0langa/spotify-dl/releases/tag/v1.3.0"}"""));
        var service = new UpdateService(client);

        var result = await service.CheckAsync(new Version(1, 2, 0));

        Assert.NotNull(result);
        Assert.Equal(new Version(1, 3, 0), result.Version);
        Assert.Equal("v1.3.0", result.Tag);
    }

    [Fact]
    public async Task ReturnsNullWhenCurrentVersionIsLatest()
    {
        using var client = new HttpClient(new JsonHandler(
            """{"tag_name":"v1.2.0","html_url":"https://github.com/0langa/spotify-dl/releases/tag/v1.2.0"}"""));

        Assert.Null(await new UpdateService(client).CheckAsync(new Version(1, 2, 0)));
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("api.github.com", request.RequestUri?.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
