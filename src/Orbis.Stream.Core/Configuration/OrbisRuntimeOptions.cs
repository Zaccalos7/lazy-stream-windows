namespace Orbis.Stream.Core.Configuration;

/// <summary>
/// Runtime configuration of the application. Mirrors the resolution rules that
/// <c>com.orbis.stream.StreamApplication</c> applied on the Java side:
/// data directory from property/env, then the writable working directory, then
/// <c>~/.orbis-stream</c>.
/// </summary>
public sealed class OrbisRuntimeOptions
{
    public const int DefaultPort = 1200;
    public const string DataDirectoryEnvironmentVariable = "LAZY_STREAM_DATA_DIR";
    public const string DataDirectoryCommandLinePrefix = "--data-dir=";
    public const string PortEnvironmentVariable = "LAZY_STREAM_PORT";
    public const string PortCommandLinePrefix = "--port=";
    public const string EmbeddedBrowserEnvironmentVariable = "LAZY_STREAM_EMBEDDED_BROWSER";
    public const string WebRootCommandLinePrefix = "--web-root=";
    public const string FfmpegEnvironmentVariable = "FFMPEG_PATH";
    public const string FfprobeEnvironmentVariable = "FFPROBE_PATH";

    private const string DatabaseFileName = "stream.db";
    private const string LogDirectoryName = "logs";
    private const string LogFileName = "twitch.log";
    private const string ImagesDirectoryName = "images";

    private OrbisRuntimeOptions(
        string dataDirectory,
        string imagesDirectory,
        int port,
        bool embeddedBrowser,
        string ffmpegPath,
        string ffprobePath,
        string webRootPath)
    {
        DataDirectory = dataDirectory;
        ImagesDirectory = imagesDirectory;
        DatabasePath = Path.Combine(dataDirectory, DatabaseFileName);
        LogDirectory = Path.Combine(dataDirectory, LogDirectoryName);
        LogFilePath = Path.Combine(LogDirectory, LogFileName);
        Port = port;
        EmbeddedBrowser = embeddedBrowser;
        FfmpegPath = ffmpegPath;
        FfprobePath = ffprobePath;
        WebRootPath = webRootPath;
    }

    public string DataDirectory { get; }

    public string DatabasePath { get; }

    public string LogDirectory { get; }

    public string LogFilePath { get; }

    public string ImagesDirectory { get; }

    public int Port { get; }

    public bool EmbeddedBrowser { get; }

    public string FfmpegPath { get; }

    public string FfprobePath { get; }

    public string WebRootPath { get; }

    public string ApplicationUrl => $"http://localhost:{Port}";

    public string ApplicationEntryPoint => $"http://localhost:{Port}/orbis/mainMenu";

    public static OrbisRuntimeOptions Resolve(string[] args, IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);

        var dataDirectory = ResolveDataDirectory(args, environment);
        var imagesDirectory = ResolveImagesDirectory(dataDirectory);
        var port = ResolvePort(args, environment);
        var embeddedBrowser = ResolveEmbeddedBrowser(args, environment);
        var ffmpegPath = ResolveFfmpeg(environment);
        var ffprobePath = ResolveFfprobe(environment, ffmpegPath);
        var webRootPath = ResolveWebRoot(args, environment);

        return new OrbisRuntimeOptions(
            dataDirectory,
            imagesDirectory,
            port,
            embeddedBrowser,
            ffmpegPath,
            ffprobePath,
            webRootPath);
    }

    /// <summary>Creates the writable directories exactly like the Java bootstrap did.</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    private static string ResolveDataDirectory(string[] args, IDictionary<string, string?> environment)
    {
        var fromCommandLine = ReadOption(args, DataDirectoryCommandLinePrefix);
        if (!string.IsNullOrWhiteSpace(fromCommandLine))
        {
            return Path.GetFullPath(fromCommandLine);
        }

        var fromEnvironment = Read(environment, DataDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        var workingDirectory = CurrentDirectory();
        if (Path.GetDirectoryName(workingDirectory) is not null && IsWritable(workingDirectory))
        {
            return workingDirectory;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".orbis-stream");
    }

    private static string ResolveImagesDirectory(string dataDirectory)
    {
        var workingDirectory = CurrentDirectory();
        return IsWritable(workingDirectory)
            ? Path.Combine(workingDirectory, ImagesDirectoryName)
            : Path.Combine(dataDirectory, ImagesDirectoryName);
    }

    private static int ResolvePort(string[] args, IDictionary<string, string?> environment)
    {
        var fromCommandLine = ReadOption(args, PortCommandLinePrefix);
        if (int.TryParse(fromCommandLine, out var commandLinePort) && commandLinePort is > 0 and < 65536)
        {
            return commandLinePort;
        }

        var fromEnvironment = Read(environment, PortEnvironmentVariable);
        if (int.TryParse(fromEnvironment, out var environmentPort) && environmentPort is > 0 and < 65536)
        {
            return environmentPort;
        }

        return DefaultPort;
    }

    private static bool ResolveEmbeddedBrowser(string[] args, IDictionary<string, string?> environment)
    {
        if (args.Any(argument => argument.Equals("--no-browser", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (args.Any(argument => argument.Equals("--browser", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var fromEnvironment = Read(environment, EmbeddedBrowserEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return !bool.TryParse(fromEnvironment, out var parsed) || parsed;
        }

        return true;
    }

    private static string ResolveFfmpeg(IDictionary<string, string?> environment)
    {
        var fromEnvironment = Read(environment, FfmpegEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        // A portable install, or the installer payload, may ship the binaries next to
        // the executable or in the ffmpeg subdirectory: prefer them over whatever PATH
        // resolves to.
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, name),
                     Path.Combine(AppContext.BaseDirectory, "ffmpeg", name)
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "ffmpeg";
    }

    private static string ResolveFfprobe(IDictionary<string, string?> environment, string ffmpegPath)
    {
        var fromEnvironment = Read(environment, FfprobeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        if (Path.IsPathRooted(ffmpegPath))
        {
            var sibling = Path.Combine(
                Path.GetDirectoryName(ffmpegPath) ?? string.Empty,
                OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            if (File.Exists(sibling))
            {
                return sibling;
            }
        }

        return "ffprobe";
    }

    private static string ResolveWebRoot(string[] args, IDictionary<string, string?> environment)
    {
        var fromCommandLine = ReadOption(args, WebRootCommandLinePrefix);
        if (!string.IsNullOrWhiteSpace(fromCommandLine))
        {
            return Path.GetFullPath(fromCommandLine);
        }

        var fromEnvironment = Read(environment, "LAZY_STREAM_WEB_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        // Beside the executable (the publish layout), then the project folder when the
        // application is started from the build output of the IDE.
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "wwwroot"),
            Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")
        };

        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(Directory.Exists) ?? Path.GetFullPath(candidates[0]);
    }

    private static string? ReadOption(string[] args, string prefix)
    {
        foreach (var argument in args)
        {
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return argument[prefix.Length..];
            }
        }

        return null;
    }

    private static string? Read(IDictionary<string, string?> environment, string key)
    {
        return environment.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    }

    /// <summary>
    /// Absolute working directory, the equivalent of <c>new File("").getAbsolutePath()</c> of the
    /// Java bootstrap: <see cref="Path.GetFullPath(string)"/> rejects an empty path.
    /// </summary>
    private static string CurrentDirectory() => Path.GetFullPath(Directory.GetCurrentDirectory());

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".orbis-write-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
