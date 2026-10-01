using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Orbis.Stream.Core.Domain;
using Xunit.Abstractions;

namespace Orbis.Stream.Tests;

/// <summary>
/// The preview of a live, against a real ffmpeg: the snapshot the page is drawn from, the file the
/// player asks for, and the change of a parameter on a live that is already running.
/// </summary>
public sealed class LivePreviewTests : IAsyncLifetime
{
    private const int TwitchVideoSettingId = 1;

    private readonly ITestOutputHelper _output;
    private TestHostRunner _host = null!;
    private string? _ffmpeg;
    private string? _ffprobe;
    private int _pkid;

    public LivePreviewTests(ITestOutputHelper output) => _output = output;

    public Task InitializeAsync()
    {
        _ffmpeg = Tool("ffmpeg");
        _ffprobe = Tool("ffprobe");
        _host = TestHostRunner.Start(_ffmpeg, _ffprobe);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    [Fact]
    public async Task Preview_AnswersTheStateOfTheLiveAndServesItsFile()
    {
        if (!await StartLiveAsync())
        {
            return;
        }

        var snapshot = await WaitForLiveAsync();

        Assert.Equal("clip.mp4", snapshot.GetProperty("videoName").GetString());
        Assert.Equal("mp4", Path.GetExtension(snapshot.GetProperty("videoPath").GetString()!).TrimStart('.'));
        Assert.True(snapshot.GetProperty("isPlayable").GetBoolean(), "an mp4 plays in the WebView");
        Assert.Equal(320, snapshot.GetProperty("source").GetProperty("width").GetInt32());
        Assert.Equal(240, snapshot.GetProperty("source").GetProperty("height").GetInt32());
        Assert.True(snapshot.GetProperty("source").GetProperty("frameRate").GetDouble() > 0);
        Assert.Equal(320, snapshot.GetProperty("output").GetProperty("width").GetInt32());
        Assert.Equal(5_000_000, snapshot.GetProperty("parameters").GetProperty("videoBitrate").GetInt32());
        Assert.Equal("libx264", snapshot.GetProperty("parameters").GetProperty("videoCodecName").GetString());
        Assert.Single(snapshot.GetProperty("running").EnumerateArray());

        // The file is served with range support, because a player that cannot ask for a range has to
        // read the whole file to seek, and a live of twenty seconds is already over by then.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/preview/live/{_pkid}/video");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1024, 2047);
        using var file = await _host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, file.StatusCode);
        Assert.Equal("video/mp4", file.Content.Headers.ContentType?.MediaType);
        var bytes = await file.Content.ReadAsByteArrayAsync();
        Assert.Equal(1024, bytes.Length);
    }

    [Fact]
    public async Task Preview_ChangesTheParametersOfARunningLive()
    {
        if (!await StartLiveAsync())
        {
            return;
        }

        // ffmpeg reports where it is every 0.2s, so a live that just started has no position yet.
        var position = await WaitForPositionAsync();
        Assert.True(position > 0, "ffmpeg is reading the file");

        using var applied = await _host.Client.PutAsJsonAsync(
            $"/preview/live/{_pkid}/parameters",
            new { videoBitrate = 3_000_000, videoWidth = 160, videoHeight = 120, frameRate = 15d });
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var answer = await applied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("success", answer.GetProperty("response").GetString());

        // The setting is what the next transcode will read, and it is written at once.
        var after = await SnapshotAsync();
        Assert.Equal(3_000_000, after.GetProperty("parameters").GetProperty("videoBitrate").GetInt32());

        // The numbers the page shows are the ones the encoder is really using, so they change only
        // when the new ffmpeg is up: until then the snapshot still answers the old resolution. And
        // the restart happens from the position the encoder reached, so the live goes on from there
        // instead of from the first frame.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var current = await SnapshotAsync();
            // The page reloads when the snapshot says nothing is running, and a moment where the two
            // ffmpeg processes overlap into "no live" would reload it on every change.
            Assert.True(current.GetProperty("isLive").GetBoolean(), "the live went missing while it restarted");

            var at = current.GetProperty("positionMilliseconds").GetInt64();
            var width = current.GetProperty("output").GetProperty("width").GetInt32();
            if (!current.GetProperty("reconfiguring").GetBoolean() && width == 160 && at >= position)
            {
                Assert.Equal(120, current.GetProperty("output").GetProperty("height").GetInt32());
                Assert.Equal(15d, current.GetProperty("output").GetProperty("frameRate").GetDouble());
                Assert.True(at > position, "the transcode carried on instead of starting over");
                return;
            }

            await Task.Delay(250);
        }

        Assert.Fail("the live did not restart with the new parameters");
    }

    [Fact]
    public async Task Preview_PutsTheResolutionBackToTheSourceWhenAskedForZero()
    {
        if (!await StartLiveAsync())
        {
            return;
        }

        await WaitForLiveAsync();

        using var scaled = await _host.Client.PutAsJsonAsync(
            $"/preview/live/{_pkid}/parameters",
            new { videoWidth = 160, videoHeight = 120 });
        Assert.Equal(HttpStatusCode.OK, scaled.StatusCode);

        // Zero is how the page asks for the resolution of the file again: an absent field means
        // "leave it as it is", so a decision cannot be undone by not repeating it.
        using var source = await _host.Client.PutAsJsonAsync(
            $"/preview/live/{_pkid}/parameters",
            new { videoWidth = 0, videoHeight = 0, frameRate = 0d });
        Assert.Equal(HttpStatusCode.OK, source.StatusCode);

        var snapshot = await SnapshotAsync();
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("parameters").GetProperty("videoWidth").ValueKind);
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("parameters").GetProperty("frameRate").ValueKind);
        Assert.Equal(320, snapshot.GetProperty("output").GetProperty("width").GetInt32());
    }

    [Fact]
    public async Task Preview_AnswersAnUnchangedRequestWithoutTouchingTheEncoder()
    {
        if (!await StartLiveAsync())
        {
            return;
        }

        await WaitForLiveAsync();

        using var same = await _host.Client.PutAsJsonAsync(
            $"/preview/live/{_pkid}/parameters",
            new { videoBitrate = 5_000_000 });
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        var answer = await same.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("nothing", answer.GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);

        var snapshot = await SnapshotAsync();
        Assert.False(snapshot.GetProperty("reconfiguring").GetBoolean());
    }

    [Fact]
    public async Task Preview_RefusesAValueThatCannotBeEncoded()
    {
        if (!await StartLiveAsync())
        {
            return;
        }

        await WaitForLiveAsync();

        using var refused = await _host.Client.PutAsJsonAsync(
            $"/preview/live/{_pkid}/parameters",
            new { videoBitrate = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var errors = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(errors.TryGetProperty("videoBitrate", out _), "the error names the field that was refused");

        // A refused change leaves the configuration alone, which is what the page repaints from.
        var snapshot = await SnapshotAsync();
        Assert.Equal(5_000_000, snapshot.GetProperty("parameters").GetProperty("videoBitrate").GetInt32());
    }

    [Fact]
    public async Task Preview_RefusesAHalfResolution()
    {
        if (!await StartLiveAsync())
        {
            return;
        }

        await WaitForLiveAsync();

        using var refused = await _host.Client.PutAsJsonAsync(
            $"/preview/live/{_pkid}/parameters",
            new { videoWidth = 160 });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task Preview_IsEmptyWithoutALiveAndRefusesToChangeAStoppedOne()
    {
        var offline = await SnapshotAsync();
        Assert.False(offline.GetProperty("isLive").GetBoolean());
        Assert.Empty(offline.GetProperty("running").EnumerateArray());

        using var missing = await _host.Client.GetAsync("/preview/live/999/video");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var nothing = await _host.Client.PutAsJsonAsync("/preview/live/999/parameters", new { videoBitrate = 1 });
        Assert.Equal(HttpStatusCode.NotFound, nothing.StatusCode);

        using var empty = await _host.Client.PutAsJsonAsync($"/preview/live/{_pkid}/parameters", new { });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task PreviewPage_IsServedWithAndWithoutALive()
    {
        using var offline = await _host.Client.GetAsync("/orbis/mainPreview");
        Assert.Equal(HttpStatusCode.OK, offline.StatusCode);
        var html = await offline.Content.ReadAsStringAsync();

        // With nothing on air the page waits for a live and leads to the wizard: the canvas is a
        // step of starting a live now, and the old address of it goes there.
        Assert.DoesNotContain("data-composer", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/orbis/mainLive?start=1\"", html, StringComparison.Ordinal);

        using var compose = await _host.NoRedirectClient.GetAsync("/orbis/mainPreview?compose=1");
        Assert.Equal("/orbis/mainLive?start=1", compose.Headers.Location?.OriginalString);

        // The list of the lives on air is on the page from the start, hidden while there is nothing
        // to pick: what fills it is the push channel, so a second live shows up without a reload.
        Assert.Contains("data-live-picker hidden", html, StringComparison.Ordinal);
        Assert.Contains("<select", html, StringComparison.Ordinal);

        if (!await StartLiveAsync())
        {
            return;
        }

        await WaitForLiveAsync();

        using var live = await _host.Client.GetAsync($"/orbis/mainPreview?live={_pkid}");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        var page = await live.Content.ReadAsStringAsync();
        Assert.Contains($"/preview/live/{_pkid}/stream", page, StringComparison.Ordinal);
        Assert.Contains("data-preview-frame", page, StringComparison.Ordinal);

        // The stage is the shape of what is on air, so a canvas that is not 16:9 is not watched
        // inside a 16:9 box.
        Assert.Contains("aspect-ratio: ", page, StringComparison.Ordinal);

        // The light picture is what is on air, pushed as motion JPEG: whole frames, one after the
        // other. One frame proves the stream opens; several prove the picture moves instead of
        // standing still, which is what the page is judged on.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/preview/live/{_pkid}/stream");
        using var stream = await _host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.StartsWith("multipart/x-mixed-replace", stream.Content.Headers.ContentType?.ToString());
        await using var body = await stream.Content.ReadAsStreamAsync();

        var stopwatch = Stopwatch.StartNew();
        var frames = await ReadFramesAsync(body, wanted: 4, TimeSpan.FromSeconds(20));
        stopwatch.Stop();

        Assert.Equal(4, frames.Count);

        // ffmpeg writes fifteen frames a second: a page that has to wait for the next one of them
        // is what makes the picture judder.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"four frames took {stopwatch.Elapsed}");
        foreach (var frame in frames)
        {
            // A whole JPEG, markers and all: a part cut short would be the picture the page shows as
            // a torn or grey rectangle.
            Assert.Equal(0xFF, frame[0]);
            Assert.Equal(0xD8, frame[1]);
            Assert.Equal(0xFF, frame[^2]);
            Assert.Equal(0xD9, frame[^1]);
        }
    }

    /// <summary>
    /// Reads whole parts of a motion JPEG stream: the headers they open with, the bytes those
    /// headers announce and the blank line that closes them. Returns what arrived before the timeout
    /// or the wanted number of frames, whichever comes first.
    /// </summary>
    private static async Task<List<byte[]>> ReadFramesAsync(System.IO.Stream body, int wanted, TimeSpan timeout)
    {
        var frames = new List<byte[]>();
        var chunk = new byte[16 * 1024];
        var pending = Array.Empty<byte>();
        using var cancel = new CancellationTokenSource(timeout);

        while (frames.Count < wanted)
        {
            var read = await body.ReadAtLeastAsync(chunk.AsMemory(0, 1), 1, throwOnEndOfStream: false, cancel.Token);
            if (read == 0)
            {
                break;
            }

            var arrived = new byte[read];
            Array.Copy(chunk, arrived, read);
            pending = pending.Length == 0 ? arrived : [.. pending, .. arrived];

            var bytes = pending.AsSpan();
            while (TakeFrame(bytes, out var frame, out var consumed))
            {
                frames.Add(frame);
                bytes = bytes[consumed..];
                if (frames.Count >= wanted)
                {
                    return frames;
                }
            }

            // Whatever is left is the beginning of a part that has not arrived whole yet.
            pending = bytes.ToArray();
        }

        return frames;
    }

    private static bool TakeFrame(ReadOnlySpan<byte> bytes, out byte[] frame, out int consumed)
    {
        frame = [];
        consumed = 0;

        var headers = bytes.IndexOf("--orbisframe\r\n"u8);
        if (headers < 0)
        {
            return false;
        }

        var from = headers + "--orbisframe\r\n".Length;
        var to = bytes[from..].IndexOf("\r\n\r\n"u8);
        if (to < 0)
        {
            return false;
        }

        var head = System.Text.Encoding.ASCII.GetString(bytes[from..(from + to)]);
        var announced = head
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.Ordinal))
            ?.Split(':')[1]
            .Trim();

        if (!int.TryParse(announced, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
        {
            return false;
        }

        var body = from + to + 4;
        if (bytes.Length < body + length + 2)
        {
            return false;
        }

        frame = bytes.Slice(body, length).ToArray();
        consumed = body + length + 2;
        return true;
    }

    [Fact]
    public async Task WithTwoLivesOnAir_ThePreviewPageOffersThemInAList()
    {
        if (!await StartLiveAsync())
        {
            return;
        }

        await WaitForLiveAsync();
        var second = await StartSecondLiveAsync();
        if (second is null)
        {
            return;
        }

        using var page = await _host.Client.GetAsync("/orbis/mainPreview");
        var html = await page.Content.ReadAsStringAsync();

        // With something to pick the list is there for the picking...
        Assert.DoesNotContain("data-live-picker hidden", html, StringComparison.Ordinal);
        Assert.Contains("<select", html, StringComparison.Ordinal);

        // ...it holds one option per live on air, and the watched one is the chosen one, which with
        // no ?live= is the live that started last. A row of buttons would put the two of them side by
        // side instead, and take more room with every live that starts.
        var list = html.IndexOf("data-live-picker", StringComparison.Ordinal);
        var options = html[list..html.IndexOf("</select>", list, StringComparison.Ordinal)];

        Assert.Equal(2, options.Split("<option").Length - 1);
        Assert.Contains($"value=\"{second}\" selected", options, StringComparison.Ordinal);
        Assert.Contains($"value=\"{_pkid}\"", options, StringComparison.Ordinal);
        Assert.DoesNotContain($"value=\"{_pkid}\" selected", options, StringComparison.Ordinal);
    }

    /// <summary>A second live, on another channel: one channel at a time is the rule.</summary>
    private async Task<int?> StartSecondLiveAsync()
    {
        var clips = Path.Combine(_host.DataDirectory, "other-clips");
        Directory.CreateDirectory(clips);

        var clip = Path.Combine(clips, "other.mp4");
        await RunAsync(_ffmpeg!, "-y -f lavfi -i testsrc=size=320x240:rate=30 -t 30 -pix_fmt yuv420p " + Quote(clip));

        using var started = await _host.Client.PostAsJsonAsync("/live/start-live", new
        {
            streamUrl = new Uri(Path.Combine(_host.DataDirectory, "output")).AbsoluteUri.TrimEnd('/'),
            streamKey = "other.flv",
            videoPath = clips,
            platformStreamName = "channel-other",
            channelName = "channel-other",
            videoSettingsRecord = new
            {
                id = TwitchVideoSettingId,
                title = "Default Twitch",
                isDefaultConfiguration = true,
                defaultPlatformConfiguration = "Twitch",
                videoCodec = 27,
                videoCodecName = "libx264",
                pixelFormat = 0,
                videoBitrate = 5_000_000,
                videoFormat = "flv",
                gopSize = 2,
                isVideoAndAudioSettingActive = true,
                audioSettingRecord = new { audioCodec = 86018, audioBitrate = 128_000 }
            }
        });
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);

        // The snapshot follows the live that started last, which is the one just asked for.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await SnapshotAsync();
            var running = snapshot.GetProperty("running").EnumerateArray().ToList();
            if (running.Count == 2 && running.Any(option => option.GetProperty("channelName").GetString() == "channel-other"))
            {
                return running.First(option => option.GetProperty("channelName").GetString() == "channel-other")
                    .GetProperty("videoPkid").GetInt32();
            }

            await Task.Delay(200);
        }

        Assert.Fail("the second live did not go on air within the time the test allows for it");
        return null;
    }

    private async Task<JsonElement> SnapshotAsync()
    {
        using var response = await _host.Client.GetAsync("/preview/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>The first sample that says a live is running: ffmpeg needs a moment to reach the file.</summary>
    private async Task<JsonElement> WaitForLiveAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await SnapshotAsync();
            if (snapshot.GetProperty("isLive").GetBoolean())
            {
                return snapshot;
            }

            await Task.Delay(200);
        }

        Assert.Fail("no live started within the time the test allows for it");
        return default;
    }

    /// <summary>The first position ffmpeg reports: zero until the first progress block, at 0.2s.</summary>
    private async Task<long> WaitForPositionAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var at = (await SnapshotAsync()).GetProperty("positionMilliseconds").GetInt64();
            if (at > 0)
            {
                return at;
            }

            await Task.Delay(100);
        }

        Assert.Fail("ffmpeg never reported a position");
        return default;
    }

    /// <summary>Creates a clip and streams it, exactly like the live page does.</summary>
    private async Task<bool> StartLiveAsync()
    {
        if (_pkid > 0)
        {
            return true;
        }

        if (string.IsNullOrEmpty(_ffmpeg) || string.IsNullOrEmpty(_ffprobe))
        {
            _output.WriteLine("ffmpeg/ffprobe are not installed: the preview test is skipped.");
            return false;
        }

        var clips = Path.Combine(_host.DataDirectory, "clips");
        var output = Path.Combine(_host.DataDirectory, "output");
        Directory.CreateDirectory(clips);
        Directory.CreateDirectory(output);

        // Long enough that the assertions land while the live is still running: ffmpeg reads it in
        // real time, so a two second clip would be over before the first sample arrives.
        var clip = Path.Combine(clips, "clip.mp4");
        await RunAsync(_ffmpeg,
            "-y -f lavfi -i testsrc=size=320x240:rate=30 -f lavfi -i sine=frequency=440:sample_rate=44100 "
            + "-t 30 -pix_fmt yuv420p -c:a aac " + Quote(clip));

        using var started = await _host.Client.PostAsJsonAsync("/live/start-live", new
        {
            streamUrl = new Uri(output).AbsoluteUri.TrimEnd('/'),
            streamKey = "stream.flv",
            videoPath = clips,
            platformStreamName = "channel-preview",
            channelName = "channel-preview",
            videoSettingsRecord = new
            {
                id = TwitchVideoSettingId,
                title = "Default Twitch",
                isDefaultConfiguration = true,
                defaultPlatformConfiguration = "Twitch",
                videoCodec = 27,
                videoCodecName = "libx264",
                pixelFormat = 0,
                videoBitrate = 5_000_000,
                videoFormat = "flv",
                gopSize = 2,
                isVideoAndAudioSettingActive = true,
                audioSettingRecord = new { audioCodec = 86018, audioBitrate = 128_000 }
            }
        });
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);

        var live = await WaitForLiveAsync();
        _pkid = live.GetProperty("videoPkid").GetInt32();
        return true;
    }

    private async Task RunAsync(string fileName, string arguments)
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

    private static string? Tool(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string Quote(string value) => $"\"{value}\"";
}

/// <summary>What the WebView can and cannot play, and how the preview serves what it can.</summary>
public sealed class VideoExtensionsTests
{
    [Theory]
    [InlineData("mp4", "video/mp4")]
    [InlineData("MP4", "video/mp4")]
    [InlineData("mov", "video/quicktime")]
    [InlineData("webm", "video/webm")]
    [InlineData("flv", "video/x-flv")]
    [InlineData("mkv", "video/x-matroska")]
    [InlineData(null, "application/octet-stream")]
    public void ContentType_AnswersWhatTheBrowserAsksFor(string? extension, string expected)
    {
        Assert.Equal(expected, VideoExtensions.ContentTypeOf(extension));
    }

    [Theory]
    [InlineData("mp4", true)]
    [InlineData("mov", true)]
    [InlineData("webm", true)]
    [InlineData("flv", false)]
    [InlineData("mkv", false)]
    public void IsBrowserPlayable_SaysWhatThePreviewCanShow(string extension, bool expected)
    {
        Assert.Equal(expected, VideoExtensions.IsBrowserPlayable(extension));
    }
}
