using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orbis.Stream.Core.Data;
using Orbis.Stream.Core.Domain;
using Orbis.Stream.Core.Services;

namespace Orbis.Stream.Tests;

/// <summary>
/// The scene deck through its routes: a button is a name, an uploaded file and a key, the deck
/// refuses what it could not put on air, and a live that is not running takes no button. The files
/// are checked with ffprobe when it is installed, and taken on trust when it is not.
/// </summary>
public sealed class SceneButtonApiTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private TestHostRunner _host = null!;

    public Task InitializeAsync()
    {
        _host = TestHostRunner.Start(Tool("ffmpeg"), Tool("ffprobe"));
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private string MediaFolder => Path.Combine(_host.DataDirectory, SceneButtonService.DirectoryName);

    [Fact]
    public async Task A_button_is_a_name_an_uploaded_file_and_a_key()
    {
        var picture = TestPictures.Png(64, 36, (x, _) => x < 32 ? (255, 0, 255, 255) : (0, 0, 0, 0));
        var media = await UploadAsync("Torno subito!.png", picture);
        Assert.Equal(("IMAGE", "Torno-subito.png"), (media.Kind, media.Label));
        Assert.Matches("^Torno-subito-[0-9a-f]{10}\\.png$", media.Name);

        // The same file again is the same file, under the same name.
        Assert.Equal(media.Name, (await UploadAsync("Torno subito!.png", picture)).Name);
        Assert.Single(Directory.GetFiles(MediaFolder, "Torno-subito-*.png"));

        using (var created = await _host.Client.PostAsJsonAsync("/scene-buttons", new { label = " BRB ", mediaName = media.Name, hotkey = "Digit1" }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var button = (await created.Content.ReadFromJsonAsync<SceneButtonResponse>(Web))!;
            Assert.Equal(("BRB", "IMAGE", "Digit1", true), (button.Label, button.Kind, button.Hotkey, button.Available));
            Assert.Equal(media.Name, button.MediaName);
        }

        var clip = await UploadAsync("intro.mp4", await ClipAsync());
        Assert.Equal("VIDEO", clip.Kind);
        using (var second = await _host.Client.PostAsJsonAsync("/scene-buttons", new { label = "Intro", mediaName = clip.Name }))
        {
            Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        }

        var buttons = (await _host.Client.GetFromJsonAsync<List<SceneButtonResponse>>("/scene-buttons", Web))!;
        Assert.Equal(["BRB", "Intro"], buttons.Select(button => button.Label));
        Assert.Null(buttons[1].Hotkey);

        // The face of a button is drawn by ffmpeg, so it is there only when ffmpeg is.
        using (var still = await _host.Client.GetAsync(buttons[0].Still))
        {
            if (Tool("ffmpeg") is null)
            {
                Assert.Equal(HttpStatusCode.NoContent, still.StatusCode);
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, still.StatusCode);
                Assert.Equal("image/png", still.Content.Headers.ContentType?.MediaType);
                Assert.Contains("immutable", still.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
            }
        }

        // An edit can take the key of nobody else, and another file: the one it no longer carries goes.
        using (var edited = await _host.Client.PutAsJsonAsync($"/scene-buttons/{buttons[0].Pkid}", new { label = "Torno subito", mediaName = clip.Name, hotkey = "Ctrl+KeyB" }))
        {
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
            var button = (await edited.Content.ReadFromJsonAsync<SceneButtonResponse>(Web))!;
            Assert.Equal(("Torno subito", "VIDEO", "Ctrl+KeyB"), (button.Label, button.Kind, button.Hotkey));
        }

        Assert.False(File.Exists(Path.Combine(MediaFolder, media.Name)), "the picture no button carries any more is still there");

        // Two buttons carry the clip: deleting one keeps it, deleting the other lets it go.
        using (var deleted = await _host.Client.DeleteAsync($"/scene-buttons/{buttons[0].Pkid}"))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }

        Assert.True(File.Exists(Path.Combine(MediaFolder, clip.Name)));
        using (var deleted = await _host.Client.DeleteAsync($"/scene-buttons/{buttons[1].Pkid}"))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }

        Assert.False(File.Exists(Path.Combine(MediaFolder, clip.Name)));
        Assert.Empty((await _host.Client.GetFromJsonAsync<List<SceneButtonResponse>>("/scene-buttons", Web))!);
    }

    [Fact]
    public async Task The_deck_refuses_what_it_could_not_put_on_air()
    {
        var media = await UploadAsync("brb.png", TestPictures.Png(16, 16, (_, _) => (0, 128, 255, 255)));

        async Task<string> RefusedAsync(object body, string field)
        {
            using var response = await _host.Client.PostAsJsonAsync("/scene-buttons", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(Web))!;
            Assert.True(errors.ContainsKey(field), $"no error on {field}: {string.Join(", ", errors.Keys)}");
            return errors[field];
        }

        await RefusedAsync(new { label = "  ", mediaName = media.Name }, "label");
        await RefusedAsync(new { label = new string('x', SceneButtonService.MaxLabelLength + 1), mediaName = media.Name }, "label");
        await RefusedAsync(new { label = "BRB" }, "mediaName");
        await RefusedAsync(new { label = "BRB", mediaName = "../stream.db" }, "mediaName");
        await RefusedAsync(new { label = "BRB", mediaName = "nowhere-0123456789.png" }, "mediaName");
        foreach (var key in new[] { "Enter", "F5", "Ctrl+KeyR", "Ctrl+", "Shift+Ctrl+KeyA", "MediaPlayPause" })
        {
            await RefusedAsync(new { label = "BRB", mediaName = media.Name, hotkey = key }, "hotkey");
        }

        using (var first = await _host.Client.PostAsJsonAsync("/scene-buttons", new { label = "First", mediaName = media.Name, hotkey = "KeyQ" }))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        }

        Assert.Contains("First", await RefusedAsync(new { label = "Second", mediaName = media.Name, hotkey = "KeyQ" }, "hotkey"), StringComparison.Ordinal);

        // Six buttons fill the deck.
        for (var index = 2; index <= SceneButtonService.MaxButtons; index++)
        {
            using var created = await _host.Client.PostAsJsonAsync("/scene-buttons", new { label = $"Button {index}", mediaName = media.Name });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using (var seventh = await _host.Client.PostAsJsonAsync("/scene-buttons", new { label = "Seventh", mediaName = media.Name }))
        {
            Assert.Equal(HttpStatusCode.Conflict, seventh.StatusCode);
        }

        using (var missing = await _host.Client.PutAsJsonAsync("/scene-buttons/987654", new { label = "Ghost", mediaName = media.Name }))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        // What is not a video or a picture does not even reach the folder.
        using (var text = await PostMediaAsync("notes.txt", "not a picture"u8.ToArray()))
        {
            Assert.Equal(HttpStatusCode.BadRequest, text.StatusCode);
        }

        if (Tool("ffprobe") is not null)
        {
            using var fake = await PostMediaAsync("fake.png", "this is text with the name of a picture"u8.ToArray());
            Assert.Equal(HttpStatusCode.BadRequest, fake.StatusCode);
            Assert.DoesNotContain(Directory.GetFiles(MediaFolder), path => Path.GetFileName(path).StartsWith("fake", StringComparison.Ordinal));
        }

        // A name is all a still is asked by: nothing outside the folder is drawn.
        foreach (var name in new[] { "..%2Fstream.db", "%2E%2E%2F%2E%2E%2Fetc%2Fpasswd.png", "nowhere-0123456789.png" })
        {
            using var still = await _host.Client.GetAsync($"/scene-buttons/media/{name}/still");
            Assert.True(still.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound, $"{name}: {still.StatusCode}");
            Assert.Empty(await still.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task A_live_that_is_not_running_takes_no_button()
    {
        var media = await UploadAsync("brb.png", TestPictures.Png(16, 16, (_, _) => (255, 255, 0, 255)));
        using var created = await _host.Client.PostAsJsonAsync("/scene-buttons", new { label = "BRB", mediaName = media.Name });
        var button = (await created.Content.ReadFromJsonAsync<SceneButtonResponse>(Web))!;

        // A live that ran yesterday: its rows are there, nothing is streaming it.
        var history = _host.Services.GetRequiredService<VideoLiveHistoryRepository>().Insert(new VideoLiveHistoryEntity
        {
            FolderOfVideoToStream = "/videos/yesterday.mp4",
            LocalDateTimeStartLive = DateTime.Now.AddDays(-1),
            StreamUrl = "rtmp://live.twitch.tv/app/",
            StreamKey = "key",
            PlatformStreamName = "Twitch",
            UserName = StreamingService.CurrentUserName
        });
        var row = _host.Services.GetRequiredService<VideoRepository>().Insert(new VideoEntity
        {
            Name = "yesterday.mp4",
            VideoPath = "/videos/yesterday.mp4",
            Extension = "mp4",
            LiveStatus = LiveStatus.Ended,
            VideoLiveHistoryId = history,
            ChannelName = "scene-deck-channel"
        });

        var state = (await _host.Client.GetFromJsonAsync<LiveSceneState>($"/live/{row}/scene", Web))!;
        Assert.Equal((false, history), (state.Live, state.History!.Value));
        Assert.Null(state.Current);

        using (var play = await _host.Client.PostAsync($"/live/{row}/scene/{button.Pkid}", null))
        {
            Assert.Equal(HttpStatusCode.Conflict, play.StatusCode);
        }

        using (var resume = await _host.Client.PostAsync($"/live/{row}/scene/resume", null))
        {
            Assert.Equal(HttpStatusCode.Conflict, resume.StatusCode);
        }

        using (var nobody = await _host.Client.PostAsync($"/live/987654/scene/{button.Pkid}", null))
        {
            Assert.Equal(HttpStatusCode.NotFound, nobody.StatusCode);
        }
    }

    private async Task<SceneMediaResponse> UploadAsync(string name, byte[] bytes)
    {
        using var response = await PostMediaAsync(name, bytes);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{name} was refused: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<SceneMediaResponse>(Web))!;
    }

    /// <summary>The file as the body of the request, the way the panel sends it.</summary>
    private Task<HttpResponseMessage> PostMediaAsync(string name, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return _host.Client.PostAsync("/scene-buttons/media?name=" + Uri.EscapeDataString(name), content);
    }

    /// <summary>A short clip when ffmpeg can make one; bytes with the name of a clip when it cannot, which an unchecked deck takes.</summary>
    private async Task<byte[]> ClipAsync()
    {
        if (Tool("ffmpeg") is not { } ffmpeg)
        {
            return "no ffmpeg, no clip"u8.ToArray();
        }

        var path = Path.Combine(_host.DataDirectory, "intro-source.mp4");
        using var process = Process.Start(new ProcessStartInfo(ffmpeg, $"-y -loglevel error -f lavfi -i color=c=yellow:s=160x90:r=15 -t 1 -pix_fmt yuv420p \"{path}\"")
        {
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        return await File.ReadAllBytesAsync(path);
    }

    private static string? Tool(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
}
