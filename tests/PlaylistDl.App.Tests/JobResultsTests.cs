using System.Text.Json;
using PlaylistDl.App.Services;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class JobResultsTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void ParsesSuccessAndFailureEntries()
    {
        var message = Parse("""
            {
              "type": "job_completed",
              "results": [
                {"track_id": "a", "path": "C:\\music\\a.mp3", "success": true},
                {"track_id": "b", "path": null, "success": false}
              ]
            }
            """);

        var results = JobResults.Parse(message);

        Assert.Equal(2, results.Count);
        Assert.Equal(new DownloadResult("a", "C:\\music\\a.mp3", true), results[0]);
        Assert.Equal(new DownloadResult("b", null, false), results[1]);
    }

    [Fact]
    public void MissingResultsYieldsEmptyList()
    {
        Assert.Empty(JobResults.Parse(Parse("""{"type": "job_cancelled"}""")));
    }

    [Fact]
    public void MalformedEntriesAreSkipped()
    {
        var message = Parse("""
            {
              "results": [
                {"path": "no-id.mp3", "success": true},
                {"track_id": "", "success": true},
                {"track_id": "ok", "success": true}
              ]
            }
            """);

        var results = JobResults.Parse(message);

        Assert.Single(results);
        Assert.Equal("ok", results[0].TrackId);
    }

    [Fact]
    public void ParsesErrorDetailAndClass()
    {
        var message = Parse("""
            {
              "results": [
                {"track_id": "a", "path": null, "success": false,
                 "error": "Sign in to confirm", "error_class": "youtube_blocked"}
              ]
            }
            """);

        var results = JobResults.Parse(message);

        Assert.Single(results);
        Assert.Equal("Sign in to confirm", results[0].Error);
        Assert.Equal("youtube_blocked", results[0].ErrorClass);
    }

    [Fact]
    public void ParseFailureReadsJobLevelClassAndHint()
    {
        var message = Parse("""
            {"failure_class": "youtube_blocked", "failure_hint": "Add cookies."}
            """);

        var failure = JobResults.ParseFailure(message);

        Assert.Equal("youtube_blocked", failure.FailureClass);
        Assert.Equal("Add cookies.", failure.FailureHint);
    }

    [Fact]
    public void ParseFailureToleratesAbsentFields()
    {
        var failure = JobResults.ParseFailure(Parse("""{"type": "job_completed"}"""));

        Assert.Null(failure.FailureClass);
        Assert.Null(failure.FailureHint);
    }
}
