using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Logging;

namespace Orbis.Stream.Tests;

/// <summary>The log on disk: one file a day, and nothing older than the days it keeps.</summary>
public sealed class FileLoggerTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 6);

    private readonly string _directory = Directory.CreateTempSubdirectory("orbis-log-").FullName;
    private readonly ManualTime _time = new(Today.ToDateTime(new TimeOnly(21, 55, 7, 784)));

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void ALineCarriesItsTimeLevelCategoryAndException()
    {
        using (var provider = new FileLoggerProvider(_directory, time: _time))
        {
            var logger = provider.CreateLogger("Orbis.Stream.Core.Streaming.FlvPacedRelay");
            logger.LogInformation("Relay to {Platform} finished", "Twitch");
            logger.LogError(new InvalidOperationException("boom"), "The relay failed");
        }

        var lines = File.ReadAllText(Path.Combine(_directory, "orbis-2026-10-06.log"));

        Assert.Contains("2026-10-06 21:55:07.784 INFO  Orbis.Stream.Core.Streaming.FlvPacedRelay: Relay to Twitch finished", lines, StringComparison.Ordinal);
        Assert.Contains("FAIL  Orbis.Stream.Core.Streaming.FlvPacedRelay: The relay failed", lines, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: boom", lines, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFilesOfThreeDaysAgoAndBeforeAreDeleted()
    {
        foreach (var name in new[] { "orbis-2026-10-01.log", "orbis-2026-10-03.log", "orbis-2026-10-04.log", "orbis-2026-10-05.log" })
        {
            File.WriteAllText(Path.Combine(_directory, name), "old");
        }

        // Not ours, or not a day: left where they are.
        File.WriteAllText(Path.Combine(_directory, "twitch.log"), "java");
        File.WriteAllText(Path.Combine(_directory, "orbis-notes.log"), "notes");

        using (var provider = new FileLoggerProvider(_directory, time: _time))
        {
            provider.CreateLogger("test").LogWarning("opened");
        }

        Assert.Equal(
            ["orbis-2026-10-04.log", "orbis-2026-10-05.log", "orbis-2026-10-06.log", "orbis-notes.log", "twitch.log"],
            Directory.GetFiles(_directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ANewDayIsANewFileAndTheOldestOneGoes()
    {
        using (var provider = new FileLoggerProvider(_directory, time: _time))
        {
            var logger = provider.CreateLogger("test");
            logger.LogInformation("first day");

            _time.Now = _time.Now.AddDays(1);
            logger.LogInformation("second day");

            // Three days after the first one, the first one is past what is kept.
            _time.Now = _time.Now.AddDays(2);
            logger.LogInformation("fourth day");
        }

        Assert.False(File.Exists(Path.Combine(_directory, "orbis-2026-10-06.log")));
        Assert.Contains("second day", File.ReadAllText(Path.Combine(_directory, "orbis-2026-10-07.log")), StringComparison.Ordinal);
        Assert.Contains("fourth day", File.ReadAllText(Path.Combine(_directory, "orbis-2026-10-09.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void TheFileCanBeReadWhileTheLogIsOpen()
    {
        using var provider = new FileLoggerProvider(_directory, time: _time);
        provider.CreateLogger("test").LogWarning("still running");

        var path = Path.Combine(_directory, "orbis-2026-10-06.log");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var text = string.Empty;
        while (!text.Contains("still running", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
            if (File.Exists(path))
            {
                using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                text = reader.ReadToEnd();
            }
        }

        Assert.Contains("still running", text, StringComparison.Ordinal);
    }

    /// <summary>A clock that is where the test puts it, in a zone with no daylight saving to cross.</summary>
    private sealed class ManualTime(DateTime local) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(local, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
