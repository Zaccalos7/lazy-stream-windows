using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Http;
using Orbis.Stream.Core.Services;
using Xunit.Abstractions;

namespace Orbis.Stream.Tests;

public sealed class NaturalFileNameComparerTests
{
    [Fact]
    public void OrdersNumbersByValueAndIgnoresCase()
    {
        var ordered = new[] { "Episode 10.mp4", "episode 2.mp4", "Episode 1.mp4", "Episode 02b.mp4", "Bonus.mkv" }
            .Order(NaturalFileNameComparer.Instance)
            .ToList();

        Assert.Equal(["Bonus.mkv", "Episode 1.mp4", "episode 2.mp4", "Episode 02b.mp4", "Episode 10.mp4"], ordered);
    }
}

/// <summary>
/// The playlist wizard: a folder only, its videos only, in Explorer order, and a stop that holds
/// for the whole playlist. The streaming half needs ffmpeg and is skipped without it.
/// </summary>
public sealed class PlaylistTests : IAsyncLifetime
{
    private TestHostRunner _host = null!;
    private readonly ITestOutputHelper _output;

    public PlaylistTests(ITestOutputHelper output) => _output = output;

    public Task InitializeAsync()
    {
        _host = TestHostRunner.Start(Tool("ffmpeg"), Tool("ffprobe"));
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    [Fact]
    public void StartPlaylist_RefusesAFileAndAMissingFolder()
    {
        var streaming = _host.Services.GetRequiredService<StreamingService>();
        var file = Path.Combine(_host.DataDirectory, "single.mp4");
        File.WriteAllText(file, "not really a video");

        var notAFolder = Assert.Throws<NotFoundCustomException>(() => streaming.StartPlaylist(RequestFor(file)));
        Assert.Equal("playlist.not.a.folder", notAFolder.MessageCode);

        var missing = Assert.Throws<NotFoundCustomException>(
            () => streaming.StartPlaylist(RequestFor(Path.Combine(_host.DataDirectory, "nowhere"))));
        Assert.Equal("folder.not.found", missing.MessageCode);
    }

    [Fact]
    public async Task StartPlaylist_StreamsOnlyTheVideosInOrder_AndAStopHoldsForTheWholePlaylist()
    {
        var ffmpeg = Tool("ffmpeg");
        if (ffmpeg is null || Tool("ffprobe") is null)
        {
            _output.WriteLine("ffmpeg/ffprobe are not installed: the playlist streaming test is skipped.");
            return;
        }

        var folder = Path.Combine(_host.DataDirectory, "playlist");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(_host.DataDirectory, "output"));
        // The first one is long enough to be stopped halfway, the last one short enough to reach the end.
        foreach (var (name, seconds) in new[] { ("clip 10.mp4", 3), ("clip 2.mp4", 20) })
        {
            await RunAsync(ffmpeg, $"-y -f lavfi -i testsrc=size=160x120:rate=15 -t {seconds} -pix_fmt yuv420p \"{Path.Combine(folder, name)}\"");
        }

        File.WriteAllText(Path.Combine(folder, "notes.txt"), "not a video");
        File.WriteAllText(Path.Combine(folder, "cover.jpg"), "not a video either");

        var streaming = _host.Services.GetRequiredService<StreamingService>();
        var repository = _host.Services.GetRequiredService<VideoRepository>();
        streaming.StartPlaylist(RequestFor(folder));

        var historyPkid = repository
            .FindPaged(new Dictionary<string, string> { ["channelName"] = "playlist-channel" }, PageRequest.Default(10, "pkid", false))
            .Items.Select(row => row.VideoLiveHistoryId).Distinct().Single()!.Value;
        var rows = repository.FindByLiveHistoryId(historyPkid);
        Assert.Equal(["clip 2.mp4", "clip 10.mp4"], rows.Select(row => row.Name));

        // The live page shows the playlist as one row, standing on the video it got to.
        LiveRowEntity PlaylistRow() => Assert.Single(repository.FindLivePage(null, "playlist-channel", 0, 10).Items);
        Assert.Equal((rows[0].Pkid, 1, 2), (PlaylistRow().Video.Pkid, PlaylistRow().Position, PlaylistRow().Total));

        Assert.True(await WaitForAsync(() => repository.FindByPkid(rows[0].Pkid)!.LiveStatus == LiveStatus.Live), "the first video never went live");
        // ffmpeg reports its position about once a second: a stop before the first report has nothing to record.
        await Task.Delay(TimeSpan.FromSeconds(3));
        streaming.StopVideoStreamingByPkid(rows[0].Pkid);
        var wasStopped = await WaitForAsync(() => repository.FindByPkid(rows[0].Pkid)!.LiveStatus == LiveStatus.Stopped);
        Assert.True(wasStopped, "the first video was not stopped: " + string.Join(" | ", repository.FindByLiveHistoryId(historyPkid).Select(row => $"{row.Name} {row.LiveStatus} {row.LastTimeStampBeforeStop} {row.Message}")));

        // Before the fix the loop moved on to the next file as soon as the first one was stopped.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(LiveStatus.Offline, repository.FindByPkid(rows[1].Pkid)!.LiveStatus);
        Assert.True(repository.FindByPkid(rows[0].Pkid)!.LastTimeStampBeforeStop > 0, "the stop did not record where to resume from");
        Assert.Equal((rows[0].Pkid, LiveStatus.Stopped), (PlaylistRow().Video.Pkid, PlaylistRow().Video.LiveStatus));

        var details = _host.Services.GetRequiredService<VideoService>().GetPlaylist(historyPkid);
        Assert.NotNull(details);
        Assert.Equal(2, details.Videos.Count);
        Assert.Equal(rows[0].Pkid, details.CurrentPkid);

        // Rendered: one row on the page, and both videos in the details dialog and in its live redraw.
        var page = await _host.Client.GetStringAsync("/orbis/mainLive?channelName=playlist-channel");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "<tr data-row"));
        Assert.DoesNotContain("clip 10.mp4", page);

        var withDetails = await _host.Client.GetStringAsync($"/orbis/mainLive?details={historyPkid}");
        Assert.Contains("id=\"playlist-details\"", withDetails);
        Assert.Contains("clip 10.mp4", withDetails);

        var redraw = await _host.Client.GetStringAsync($"/orbis/mainLive?handler=DetailsRows&details={historyPkid}");
        Assert.Contains("is-current", redraw);
        Assert.Contains("clip 2.mp4", redraw);
        Assert.Contains("clip 10.mp4", redraw);

        // Start again from the last video: the first counts as streamed, the last goes on air, and
        // the playlist reads LIVE while it is, then ENDED once the last video reached its end.
        Assert.Equal(LiveStatus.Stopped, PlaylistRow().Status);
        streaming.PrepareStartFrom(rows[1].Pkid);
        streaming.StartVideo(_host.Services.GetRequiredService<VideoService>().FindVideo(rows[1].Pkid));
        Assert.True(await WaitForAsync(() => PlaylistRow().Status == LiveStatus.Live), "the playlist did not go live from the chosen video");
        Assert.Equal((rows[1].Pkid, 2), (PlaylistRow().Video.Pkid, PlaylistRow().Position));
        Assert.Equal(LiveStatus.Ended, repository.FindByPkid(rows[0].Pkid)!.LiveStatus);

        Assert.True(await WaitForAsync(() => PlaylistRow().Status == LiveStatus.Ended), "the playlist did not end after its last video");
        Assert.Single(repository.FindLivePage(LiveStatus.Ended, "playlist-channel", 0, 10).Items);

        // A restart forgets the statuses of the pass before, or the playlist would still read ENDED.
        streaming.RestartFromBeginning(historyPkid);
        Assert.All(repository.FindByLiveHistoryId(historyPkid), row => Assert.Equal(LiveStatus.Offline, row.LiveStatus));
        Assert.Equal((rows[0].Pkid, LiveStatus.Offline), (PlaylistRow().Video.Pkid, PlaylistRow().Status));
    }

    private StartLiveRequest RequestFor(string folder) => new(
        new Uri(Path.Combine(_host.DataDirectory, "output")).AbsoluteUri,
        "stream.flv",
        folder,
        "playlist-platform",
        "playlist-channel",
        JsonSerializer.Deserialize<VideoSettingsRequest>(
            """
            {
              "title": "Test", "videoCodec": 27, "videoCodecName": "libx264", "pixelFormat": 0,
              "videoBitrate": 500000, "videoFormat": "flv", "gopSize": 2, "isVideoAndAudioSettingActive": true,
              "audioSettingRecord": { "audioCodec": 86018, "audioBitrate": 64000 }
            }
            """,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }

    private static async Task RunAsync(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException($"Unable to start {fileName}");

        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"{fileName} {arguments} failed: {error}");
    }

    private static string? Tool(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
}
