namespace Orbis.Stream.Tests;

using Orbis.Stream.Core.Configuration;

/// <summary>
/// Resolution rules of the runtime configuration, the equivalent of the Java
/// <c>StreamApplication</c> bootstrap and of the <c>application.yaml</c> properties.
/// </summary>
public sealed class RuntimeOptionsTests
{
    private static readonly string[] NoArguments = [];

    [Fact]
    public void DataDirectory_PrefersTheCommandLineOverTheEnvironment()
    {
        using var directory = new TemporaryDirectory();

        var options = Resolve(
            [$"--data-dir={directory.Path}"],
            new Dictionary<string, string?> { [OrbisRuntimeOptions.DataDirectoryEnvironmentVariable] = "/from/environment" });

        Assert.Equal(directory.Path, options.DataDirectory);
        Assert.Equal(Path.Combine(directory.Path, "stream.db"), options.DatabasePath);
        Assert.Equal(Path.Combine(directory.Path, "logs"), options.LogDirectory);
    }

    [Fact]
    public void DataDirectory_FallsBackToTheWorkingDirectory()
    {
        var options = Resolve(NoArguments, new Dictionary<string, string?>());

        Assert.Equal(Directory.GetCurrentDirectory(), options.DataDirectory);
    }

    [Fact]
    public void Port_PrefersTheCommandLineThenTheEnvironmentThenTheDefault()
    {
        Assert.Equal(
            1300,
            Resolve([$"--port=1300"], new Dictionary<string, string?> { [OrbisRuntimeOptions.PortEnvironmentVariable] = "1400" }).Port);

        Assert.Equal(
            1400,
            Resolve(NoArguments, new Dictionary<string, string?> { [OrbisRuntimeOptions.PortEnvironmentVariable] = "1400" }).Port);

        Assert.Equal(OrbisRuntimeOptions.DefaultPort, Resolve(NoArguments, new Dictionary<string, string?>()).Port);
    }

    [Fact]
    public void Port_IgnoresValuesOutsideTheValidRange()
    {
        var environment = new Dictionary<string, string?> { [OrbisRuntimeOptions.PortEnvironmentVariable] = "70000" };

        Assert.Equal(OrbisRuntimeOptions.DefaultPort, Resolve(NoArguments, environment).Port);
    }

    [Fact]
    public void ApplicationUrlAndEntryPoint_FollowThePort()
    {
        var options = Resolve([$"--port=1234"], new Dictionary<string, string?>());

        Assert.Equal("http://localhost:1234", options.ApplicationUrl);
        Assert.Equal("http://localhost:1234/orbis/mainMenu", options.ApplicationEntryPoint);
    }

    [Fact]
    public void EmbeddedBrowser_CanBeDisabledFromBothTheCommandLineAndTheEnvironment()
    {
        Assert.False(Resolve(["--no-browser"], new Dictionary<string, string?>()).EmbeddedBrowser);
        Assert.False(
            Resolve(
                NoArguments,
                new Dictionary<string, string?> { [OrbisRuntimeOptions.EmbeddedBrowserEnvironmentVariable] = "false" }).EmbeddedBrowser);

        Assert.True(Resolve(NoArguments, new Dictionary<string, string?>()).EmbeddedBrowser);
    }

    [Fact]
    public void WebRoot_PrefersTheCommandLine()
    {
        using var directory = new TemporaryDirectory();

        var options = Resolve(
            [$"--web-root={directory.Path}"],
            new Dictionary<string, string?> { ["LAZY_STREAM_WEB_ROOT"] = "/from/environment" });

        Assert.Equal(directory.Path, options.WebRootPath);
    }

    [Fact]
    public void WebRoot_FallsBackToTheWwwrootBesideTheExecutable()
    {
        // The test host has no web root of its own: create one next to the assembly
        // exactly like the publish does for the application.
        var beside = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var created = !Directory.Exists(beside);
        if (created)
        {
            Directory.CreateDirectory(beside);
        }

        try
        {
            var options = Resolve(NoArguments, new Dictionary<string, string?>());

            Assert.Equal(beside, options.WebRootPath);
        }
        finally
        {
            if (created)
            {
                Directory.Delete(beside, recursive: true);
            }
        }
    }

    [Fact]
    public void Ffmpeg_PrefersTheEnvironmentVariable()
    {
        var options = Resolve(
            NoArguments,
            new Dictionary<string, string?>
            {
                [OrbisRuntimeOptions.FfmpegEnvironmentVariable] = "/opt/ffmpeg/bin/ffmpeg",
                [OrbisRuntimeOptions.FfprobeEnvironmentVariable] = "/opt/ffmpeg/bin/ffprobe"
            });

        Assert.Equal("/opt/ffmpeg/bin/ffmpeg", options.FfmpegPath);
        Assert.Equal("/opt/ffmpeg/bin/ffprobe", options.FfprobePath);
    }

    [Fact]
    public void Ffprobe_FallsBackToTheSiblingOfFfmpeg()
    {
        using var directory = new TemporaryDirectory();
        var ffmpeg = Path.Combine(directory.Path, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        var ffprobe = Path.Combine(directory.Path, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        File.WriteAllText(ffmpeg, string.Empty);
        File.WriteAllText(ffprobe, string.Empty);

        var options = Resolve(
            NoArguments,
            new Dictionary<string, string?> { [OrbisRuntimeOptions.FfmpegEnvironmentVariable] = ffmpeg });

        Assert.Equal(ffprobe, options.FfprobePath);
    }

    [Fact]
    public void Ffmpeg_IsTakenFromTheSubdirectoryUsedByTheInstaller()
    {
        // The installer deploys the shared FFmpeg build in a ffmpeg subdirectory.
        var directory = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        var created = !Directory.Exists(directory);
        if (created)
        {
            Directory.CreateDirectory(directory);
        }

        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var ffmpeg = Path.Combine(directory, name);
        File.WriteAllText(ffmpeg, string.Empty);

        try
        {
            var options = Resolve(NoArguments, new Dictionary<string, string?>());

            Assert.Equal(ffmpeg, options.FfmpegPath);
            Assert.Equal("ffprobe", options.FfprobePath);
        }
        finally
        {
            File.Delete(ffmpeg);
            if (created)
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Ffmpeg_WithoutConfigurationIsResolvedFromThePath()
    {
        var options = Resolve(NoArguments, new Dictionary<string, string?>());

        Assert.Equal("ffmpeg", options.FfmpegPath);
        Assert.Equal("ffprobe", options.FfprobePath);
    }

    private static OrbisRuntimeOptions Resolve(string[] args, Dictionary<string, string?> environment) =>
        OrbisRuntimeOptions.Resolve(args, environment);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"orbis-options-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary directory must never fail a test.
            }
        }
    }
}
