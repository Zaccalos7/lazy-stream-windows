using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Http;
using Orbis.Stream.Core.Services;
using Xunit.Abstractions;

namespace Orbis.Stream.Tests;

/// <summary>
/// The overlay library and the overlays of a layout, through the running application: the files
/// come in as uploads and go out by name only, a layout keeps them whole, and a live started from
/// it lays them over its sources with their alpha. The live is checked on the pixels it wrote.
/// </summary>
public sealed class OverlayLiveTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private TestHostRunner _host = null!;
    private readonly ITestOutputHelper _output;

    public OverlayLiveTests(ITestOutputHelper output) => _output = output;

    public Task InitializeAsync()
    {
        _host = TestHostRunner.Start(Tool("ffmpeg"), Tool("ffprobe"));
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private bool HasFfmpeg()
    {
        if (Tool("ffmpeg") is not null && Tool("ffprobe") is not null)
        {
            return true;
        }

        _output.WriteLine("ffmpeg/ffprobe are not installed: the overlay test is skipped.");
        return false;
    }

    [Fact]
    public async Task TheLibraryTakesAPictureOnceAndAnswersWithItByNameOnly()
    {
        if (!HasFfmpeg())
        {
            return;
        }

        var png = Png(64, 32, (x, _) => x < 32 ? (255, 0, 0, 255) : (0, 0, 0, 0));
        var first = await UploadAsync("Logo canale.png", png);
        var again = await UploadAsync("Logo canale.png", png);

        // The same picture twice is one file: its name is its content.
        Assert.Equal(first.Name, again.Name);
        Assert.Equal("Logo-canale.png", first.Label);
        Assert.False(first.Video);
        var listed = Assert.Single(await _host.Client.GetFromJsonAsync<OverlayEntry[]>("/scene/overlays", Web) ?? []);
        Assert.Equal(first.Path, listed.Path);
        Assert.StartsWith(Path.Combine(_host.DataDirectory, OverlayLibrary.DirectoryName), first.Path, StringComparison.Ordinal);

        using var picture = await _host.Client.GetAsync(first.Url);
        Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
        Assert.Equal("image/png", picture.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png, await picture.Content.ReadAsByteArrayAsync());

        // Nothing but a file of the library is ever answered.
        foreach (var name in new[] { "..%2Fstream.db", "stream.db", "%2E%2E%2F%2E%2E%2Fpasswd.png" })
        {
            using var outside = await _host.Client.GetAsync("/scene/overlays/" + name);
            Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
        }
    }

    [Fact]
    public async Task TheLibraryRefusesWhatIsNotAPicture()
    {
        if (!HasFfmpeg())
        {
            return;
        }

        // The wrong kind of file, and a file that only has the name of a picture.
        foreach (var (name, bytes) in new[] { ("notes.txt", "hello"u8.ToArray()), ("fake.png", "not a picture"u8.ToArray()) })
        {
            using var response = await PostAsync(name, bytes);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(Web);
            Assert.Contains(name, errors!["file"], StringComparison.Ordinal);
        }

        Assert.Empty(await _host.Client.GetFromJsonAsync<OverlayEntry[]>("/scene/overlays", Web) ?? []);
        Assert.Empty(Directory.GetFiles(Path.Combine(_host.DataDirectory, OverlayLibrary.DirectoryName)));
    }

    [Fact]
    public async Task ALayoutKeepsItsOverlaysWholeAndTheLibraryKeepsWhatALayoutUses()
    {
        if (!HasFfmpeg())
        {
            return;
        }

        var frame = await UploadAsync("frame.png", Png(32, 18, (_, _) => (255, 255, 255, 128)));
        var spare = await UploadAsync("spare.png", Png(16, 16, (_, _) => (0, 255, 0, 255)));
        var scenes = _host.Services.GetRequiredService<SceneService>();

        // A layout of a slot and an overlay: the overlay comes back as itself, the slot as a slot.
        var (_, pkid) = scenes.Save(new SceneRequest(
            null, "Dressed", null, 1920, 1080,
            [
                new SceneItemRequest(SourceKind.Camera, "video=Cam", "Webcam", 1376, 600, 480, 270, true),
                new SceneItemRequest(SourceKind.Overlay, frame.Path, "Frame", 0, 0, 1920, 1080, true)
            ],
            IsLayout: true));
        var saved = scenes.GetOne(pkid);
        Assert.Collection(
            saved.Items!,
            slot => Assert.Equal((SourceKind.File, string.Empty), (slot!.SourceKind, slot.SourceTarget)),
            overlay =>
            {
                Assert.Equal((SourceKind.Overlay, frame.Path, "Frame"), (overlay!.SourceKind, overlay.SourceTarget, overlay.Label));
                Assert.False(overlay.AudioEnabled);
            });

        // A layout of overlays alone is a layout: a "starting soon" screen has no slot to fill.
        scenes.Save(new SceneRequest(null, "Starting soon", null, 1920, 1080,
            [new SceneItemRequest(SourceKind.Overlay, spare.Path, null, 0, 0, 1920, 1080, false)], IsLayout: true));

        // Anything but a file of the library is refused, whoever sends it.
        var outside = Path.Combine(_host.DataDirectory, "outside.png");
        await File.WriteAllBytesAsync(outside, Png(8, 8, (_, _) => (0, 0, 0, 255)));
        var refused = Assert.Throws<NotFoundCustomException>(() => scenes.Save(new SceneRequest(
            null, "Elsewhere", null, 1920, 1080,
            [new SceneItemRequest(SourceKind.Overlay, outside, "Elsewhere", 0, 0, 1920, 1080, false)], IsLayout: true)));
        Assert.Equal("scene.overlay.unknown", refused.MessageCode);

        // One logo in two corners is one file laid twice, which a scene takes.
        scenes.Save(new SceneRequest(null, "Two logos", null, 1920, 1080,
            [
                new SceneItemRequest(SourceKind.Overlay, spare.Path, null, 0, 0, 100, 100, false),
                new SceneItemRequest(SourceKind.Overlay, spare.Path, null, 1820, 980, 100, 100, false)
            ]));

        // What a layout uses stays in the library; what nothing uses goes.
        using var kept = await _host.Client.DeleteAsync(frame.Url);
        Assert.Equal(HttpStatusCode.Conflict, kept.StatusCode);
        Assert.True(File.Exists(frame.Path));

        scenes.Delete(pkid);
        using var deleted = await _host.Client.DeleteAsync(frame.Url);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.False(File.Exists(frame.Path));
    }

    [Fact]
    public async Task ALiveLaysItsOverlaysOverItsSourcesWithTheirAlpha()
    {
        if (!HasFfmpeg())
        {
            return;
        }

        var ffmpeg = Tool("ffmpeg")!;
        var folder = Path.Combine(_host.DataDirectory, "sources");
        var output = Path.Combine(_host.DataDirectory, "output");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(output);

        // A green video in the top left quarter, over a blue backdrop picture, under a frame that is
        // clear but for a red square on the video and a half white bar across the bottom.
        var video = Path.Combine(folder, "green.mp4");
        await RunAsync(ffmpeg, $"-y -f lavfi -i color=c=0x00ff00:s=320x180:r=15 -t 30 -pix_fmt yuv420p \"{video}\"");
        var backdrop = await UploadAsync("backdrop.png", Png(640, 360, (_, _) => (0, 0, 255, 255)));
        var frame = await UploadAsync("frame.png", Png(640, 360, (x, y) =>
            x is >= 40 and < 120 && y is >= 40 and < 120 ? (255, 0, 0, 255)
            : y is >= 300 and < 340 ? (255, 255, 255, 128)
            : (0, 0, 0, 0)));

        // An animated GIF loops; a WebM with alpha, when this ffmpeg can make one, keeps its alpha.
        var gif = Path.Combine(folder, "anim.gif");
        await RunAsync(ffmpeg, $"-y -f lavfi -i testsrc2=s=160x90:r=10 -t 1 \"{gif}\"");
        var animation = await UploadAsync("anim.gif", await File.ReadAllBytesAsync(gif));
        SceneItemRequest? webm = null;
        if (await SupportsAsync(ffmpeg, "libvpx-vp9"))
        {
            var clip = Path.Combine(folder, "square.webm");
            await RunAsync(ffmpeg,
                "-y -f lavfi -i color=c=white:s=160x90:r=10 -t 1 -vf \"format=yuva420p,geq=lum='lum(X,Y)':cb='cb(X,Y)':cr='cr(X,Y)':a='if(between(X,60,99)*between(Y,25,64),255,0)'\" "
                + $"-c:v libvpx-vp9 -pix_fmt yuva420p -auto-alt-ref 0 \"{clip}\"");
            var square = await UploadAsync("square.webm", await File.ReadAllBytesAsync(clip));
            webm = new SceneItemRequest(SourceKind.Overlay, square.Path, "Square", 400, 200, 160, 90, false);
        }

        List<SceneItemRequest> items =
        [
            new(SourceKind.Overlay, backdrop.Path, "Backdrop", 0, 0, 640, 360, false),
            new(SourceKind.File, video, "Video", 0, 0, 320, 180, false),
            new(SourceKind.Overlay, frame.Path, "Frame", 0, 0, 640, 360, false),
            new(SourceKind.Overlay, animation.Path, "Animation", 40, 200, 160, 90, false)
        ];
        if (webm is not null)
        {
            items.Add(webm);
        }

        var (_, scenePkid) = _host.Services.GetRequiredService<SceneService>().Save(new SceneRequest(null, "Dressed live", null, 640, 360, items));
        var streaming = _host.Services.GetRequiredService<StreamingService>();
        streaming.StartSceneLive(new StartSceneLiveRequest(
            scenePkid, new Uri(output).AbsoluteUri, "dressed.flv", "overlay-platform", "overlay-channel", Setting()));

        // The live stands on its video, not on the backdrop under it.
        LiveRow Row() => Assert.Single(_host.Services.GetRequiredService<VideoService>()
            .GetLivePage(null, "overlay-channel", new PageRequest(0, 10, [])).Content);
        Assert.True(await WaitForAsync(() => Row().Status == LiveStatus.Live), "the dressed canvas never went live: " + Row().Video.Message);
        Assert.Equal("Video", Row().Video.Name);
        var rows = _host.Services.GetRequiredService<VideoRepository>().FindByLiveHistoryId(Row().Video.VideoLiveHistory!.Pkid!.Value);
        Assert.All(rows.Where(row => row.SourceKind == SourceKind.Overlay), row => Assert.False(row.AudioEnabled));

        await Task.Delay(TimeSpan.FromSeconds(4));
        streaming.StopVideoStreamingByPkid(Row().Video.Pkid!.Value);
        Assert.True(await WaitForAsync(() => Row().Status != LiveStatus.Live), "the dressed canvas did not stop");

        // Two seconds in: the still has long been decoded, and is still there.
        var pixels = await FrameAsync(ffmpeg, Path.Combine(output, "dressed.flv"), 2, 640, 360);
        (int R, int G, int B) At(int x, int y) => (pixels[(y * 640 + x) * 3], pixels[(y * 640 + x) * 3 + 1], pixels[(y * 640 + x) * 3 + 2]);

        var red = At(80, 80);
        Assert.True(red is { R: > 200, G: < 70, B: < 70 }, $"the red square of the frame is {red}");
        var green = At(200, 100);
        Assert.True(green is { G: > 180, R: < 70, B: < 70 }, $"the video under the clear part of the frame is {green}");
        var blue = At(480, 100);
        Assert.True(blue is { B: > 180, R: < 70, G: < 70 }, $"the backdrop is {blue}");
        var bar = At(300, 320);
        Assert.True(bar is { R: > 90 and < 170, G: > 90 and < 170, B: > 200 }, $"the half white bar over the backdrop is {bar}");

        if (webm is not null)
        {
            // Opaque in its middle, clear around it: through the native decoder it would be black.
            var square = At(480, 245);
            Assert.True(square is { R: > 200, G: > 200, B: > 200 }, $"the opaque middle of the WebM is {square}");
            var around = At(410, 210);
            Assert.True(around is { B: > 180, R: < 70, G: < 70 }, $"the clear part of the WebM is {around}");
        }
    }

    private async Task<OverlayEntry> UploadAsync(string name, byte[] bytes)
    {
        using var response = await PostAsync(name, bytes);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{name} was refused: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<OverlayEntry>(Web))!;
    }

    private Task<HttpResponseMessage> PostAsync(string name, byte[] bytes)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var form = new MultipartFormDataContent { { file, "file", name } };
        return _host.Client.PostAsync("/scene/overlays", form);
    }

    /// <summary>A PNG with alpha, written by hand: the test decides every pixel of it.</summary>
    private static byte[] Png(int width, int height, Func<int, int, (int R, int G, int B, int A)> pixel)
    {
        var raw = new byte[height * (width * 4 + 1)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (width * 4 + 1);
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                var at = row + 1 + x * 4;
                (raw[at], raw[at + 1], raw[at + 2], raw[at + 3]) = ((byte)r, (byte)g, (byte)b, (byte)a);
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        (header[8], header[9]) = (8, 6);

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(System.IO.Stream png, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.Write(typed);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typed));
        png.Write(number);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }

    /// <summary>One frame of what the live wrote, as RGB bytes.</summary>
    private static async Task<byte[]> FrameAsync(string ffmpeg, string path, double seconds, int width, int height)
    {
        using var process = Process.Start(new ProcessStartInfo(ffmpeg)
        {
            ArgumentList = { "-hide_banner", "-loglevel", "error", "-ss", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", path, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;

        using var frame = new MemoryStream();
        var errors = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.BaseStream.CopyToAsync(frame);
        await process.WaitForExitAsync();
        Assert.True(frame.Length == width * height * 3, $"no frame at {seconds}s of {path}: {await errors}");
        return frame.ToArray();
    }

    private static async Task<bool> SupportsAsync(string ffmpeg, string encoder)
    {
        using var process = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -encoders")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false
        })!;
        var list = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return list.Contains(" " + encoder + " ", StringComparison.Ordinal);
    }

    private static VideoSettingsRequest Setting() => JsonSerializer.Deserialize<VideoSettingsRequest>(
        """
        {
          "title": "Test", "videoCodec": 27, "videoCodecName": "libx264", "pixelFormat": 0,
          "videoBitrate": 2000000, "videoFormat": "flv", "gopSize": 2, "isVideoAndAudioSettingActive": true,
          "audioSettingRecord": { "audioCodec": 86018, "audioBitrate": 64000 }
        }
        """,
        Web)!;

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
