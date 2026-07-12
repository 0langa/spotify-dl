using System.Text.Json;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class SourceCandidatesTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void ParsesRankedCandidatesWithDeltaLabels()
    {
        var response = Parse("""
            {
              "candidates": [
                {"url": "https://music.youtube.com/watch?v=a", "title": "Tune",
                 "artists": ["Artist"], "album": "Album", "result_type": "song",
                 "duration_seconds": 200, "duration_delta_seconds": 0},
                {"url": "https://www.youtube.com/watch?v=b", "title": "Tune (Live)",
                 "artists": ["Artist"], "album": null, "result_type": "video",
                 "duration_seconds": 215, "duration_delta_seconds": 15}
              ]
            }
            """);

        var candidates = SourceOverrideWindow.ParseCandidates(response);

        Assert.Equal(2, candidates.Count);
        Assert.Equal("Music", candidates[0].TypeLabel);
        Assert.Contains("exact", candidates[0].DurationLabel);
        Assert.Equal("Artist · Album", candidates[0].Subtitle);
        Assert.Equal("Video", candidates[1].TypeLabel);
        Assert.Contains("+15s", candidates[1].DurationLabel);
        Assert.Equal("Artist", candidates[1].Subtitle);
    }

    [Fact]
    public void SkipsCandidatesWithoutUrlOrTitle()
    {
        var response = Parse("""
            {"candidates": [{"title": "no url"}, {"url": "https://youtu.be/x"}]}
            """);

        Assert.Empty(SourceOverrideWindow.ParseCandidates(response));
    }

    [Fact]
    public void MissingCandidatesArrayYieldsEmptyList()
    {
        Assert.Empty(SourceOverrideWindow.ParseCandidates(Parse("""{"type":"sources_found"}""")));
    }
}
