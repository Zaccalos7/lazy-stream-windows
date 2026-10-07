using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Http;
using Orbis.Stream.Core.Services;
using Orbis.Stream.Core.Streaming;
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
        var firstStop = repository.FindByPkid(rows[0].Pkid)!.LastTimeStampBeforeStop;
        Assert.True(firstStop > 0, "the stop did not record where to resume from");

        // A play resumes from there, and a second stop records a position in the file, past the
        // first one: ffmpeg counts from the point it was seeked to, the session adds that point back.
        streaming.StartVideo(_host.Services.GetRequiredService<VideoService>().FindVideo(rows[0].Pkid));
        Assert.True(await WaitForAsync(() => repository.FindByPkid(rows[0].Pkid)!.LiveStatus == LiveStatus.Live), "the resume never went live");
        await Task.Delay(TimeSpan.FromSeconds(2));
        streaming.StopVideoStreamingByPkid(rows[0].Pkid);
        Assert.True(await WaitForAsync(() => repository.FindByPkid(rows[0].Pkid)!.LiveStatus == LiveStatus.Stopped), "the resume was not stopped");
        var secondStop = repository.FindByPkid(rows[0].Pkid)!.LastTimeStampBeforeStop;
        Assert.True(secondStop > firstStop, $"the second stop went back in time: {secondStop} ms after {firstStop} ms");
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

    [Fact]
    public async Task EnqueueSpot_PausesCurrentVideo_PlaysSpot_AndResumesWhileRemainingLive()
    {
        var ffmpeg = Tool("ffmpeg");
        if (ffmpeg is null || Tool("ffprobe") is null)
        {
            return;
        }

        var folder = Path.Combine(_host.DataDirectory, "spot-test");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(_host.DataDirectory, "output-spot"));

        var mainVideo = Path.Combine(folder, "main.mp4");
        var spotVideo = Path.Combine(folder, "spot.mp4");

        await RunAsync(ffmpeg, $"-y -f lavfi -i testsrc=size=160x120:rate=15 -t 8 -pix_fmt yuv420p \"{mainVideo}\"");
        await RunAsync(ffmpeg, $"-y -f lavfi -i testsrc=size=160x120:rate=15 -t 2 -pix_fmt yuv420p \"{spotVideo}\"");

        var streaming = _host.Services.GetRequiredService<StreamingService>();
        var repository = _host.Services.GetRequiredService<VideoRepository>();

        streaming.StartLive(RequestFor(mainVideo));

        var page = repository.FindLivePage(null, null, 0, 10);
        var mainRow = page.Items.First(r => r.Video.VideoPath == mainVideo).Video;

        Assert.True(await WaitForAsync(() => repository.FindByPkid(mainRow.Pkid)?.LiveStatus == LiveStatus.Live));

        // Enqueue the spot
        streaming.EnqueueSpot(spotVideo, mainRow.Pkid);

        // Verify status remains Live
        var current = repository.FindByPkid(mainRow.Pkid);
        Assert.NotNull(current);
        Assert.Equal(LiveStatus.Live, current.LiveStatus);

        // Stop cleanly
        streaming.StopVideoStreamingByPkid(mainRow.Pkid);
        Assert.True(await WaitForAsync(() => repository.FindByPkid(mainRow.Pkid)?.LiveStatus == LiveStatus.Stopped));
    }

    [Fact]
    public async Task EnqueueSpot_StreamsSpotThroughAndResumesMainVideo()
    {
        var ffmpeg = Tool("ffmpeg");
        if (ffmpeg is null || Tool("ffprobe") is null)
        {
            return;
        }

        var folder = Path.Combine(_host.DataDirectory, "spot-full-test");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(_host.DataDirectory, "output"));

        var mainVideo = Path.Combine(folder, "main.mp4");
        var spotVideo = Path.Combine(folder, "spot.mp4");

        // Main video: 15 seconds, Spot: 4 seconds
        await RunAsync(ffmpeg, $"-y -f lavfi -i testsrc=size=160x120:rate=15 -t 15 -pix_fmt yuv420p \"{mainVideo}\"");
        await RunAsync(ffmpeg, $"-y -f lavfi -i testsrc=size=160x120:rate=15 -t 4 -pix_fmt yuv420p \"{spotVideo}\"");

        var streaming = _host.Services.GetRequiredService<StreamingService>();
        var repository = _host.Services.GetRequiredService<VideoRepository>();

        streaming.StartLive(RequestFor(mainVideo));

        var page = repository.FindLivePage(null, null, 0, 10);
        var mainRow = page.Items.First(r => r.Video.VideoPath == mainVideo).Video;

        Assert.True(await WaitForAsync(() => repository.FindByPkid(mainRow.Pkid)?.LiveStatus == LiveStatus.Live));

        // Enqueue the spot
        streaming.EnqueueSpot(spotVideo, mainRow.Pkid);

        // While spot is streaming, status must stay Live
        await Task.Delay(1500);
        var duringSpot = repository.FindByPkid(mainRow.Pkid);
        Assert.NotNull(duringSpot);
        Assert.Equal(LiveStatus.Live, duringSpot.LiveStatus);

        // Eventually after spot finishes and main video resumes and reaches end, status becomes Ended
        Assert.True(await WaitForAsync(() => repository.FindByPkid(mainRow.Pkid)?.LiveStatus == LiveStatus.Ended));
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

    /// <summary>
    /// A canvas is not a playlist, but the end of it is decided the same way: the live lasts as long
    /// as its longest file, and it is that length that the streaming loop watches for. A probe of
    /// zero is a live that never ends, which is what a canvas used to be measured with.
    /// </summary>
    [Fact]
    public void ACanvasLastsAsLongAsItsLongestFile()
    {
        var shortClip = Clip("C:/clips/short.mp4");
        var longClip = Clip("C:/clips/long.mp4");

        var probes = new Dictionary<string, MediaProbeResult>
        {
            [shortClip.VideoPath] = Probe(1920, 1080, 120d),
            [longClip.VideoPath] = Probe(3840, 2160, 754.5d)
        };

        Assert.Equal(
            TimeSpan.FromSeconds(754.5),
            FfmpegVideoPlaylistStreamer.LongestFileDuration([shortClip, longClip], probes));

        // The shortest one on its own is the length of the live: the canvas is one shape to fill.
        Assert.Equal(
            TimeSpan.FromSeconds(120),
            FfmpegVideoPlaylistStreamer.LongestFileDuration([shortClip], probes));

        // A capture device has no length of its own and says nothing about the end of the canvas:
        // it is the streaming loop that refuses a length altogether when the canvas has a device on
        // it, because a device produces frames for ever.
        var camera = new VideoEntity { Pkid = 9, SourceKind = SourceKind.Camera, SourceTarget = "video=Cam" };
        Assert.Equal(
            TimeSpan.FromSeconds(120),
            FfmpegVideoPlaylistStreamer.LongestFileDuration([shortClip, camera], probes));

        // A file nothing can read has no length either, and alone on the canvas it leaves nothing
        // to measure the live against.
        Assert.Null(FfmpegVideoPlaylistStreamer.LongestFileDuration(
            [Clip("C:/clips/unreadable.mp4")],
            new Dictionary<string, MediaProbeResult>()));
    }

    private static VideoEntity Clip(string path) =>
        new() { Pkid = Math.Abs(path.GetHashCode()), SourceKind = SourceKind.File, VideoPath = path, Name = path };

    private static MediaProbeResult Probe(int width, int height, double seconds) =>
        new(width, height, 25d, HasAudio: true, AudioChannels: 2, DurationSeconds: seconds);

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
