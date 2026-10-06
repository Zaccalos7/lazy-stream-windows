using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Orbis.Stream.Core.Logging;

/// <summary>
/// The log of the application on disk, one file a day (<c>orbis-yyyy-MM-dd.log</c>) in the logs
/// directory of the data directory. The application is a window with no console, so this file is
/// the only place an Information line - how a live started, how it finished, how late it got -
/// can be read back; the Windows event log only takes the warnings.
/// <para>Whoever logs never waits for the disk. A line is formatted where it is logged and queued,
/// and one task writes the queue out: the sending thread of the relay logs while a frame is due,
/// and a write that stalls on a scanner holding the file would make the live late. A queue that is
/// full drops the line instead of blocking, and the file says how many were dropped.</para>
/// <para>A file older than <see cref="DefaultRetentionDays"/> days is deleted when the log is
/// opened and each time it moves on to a new day, so the directory never holds more than that.</para>
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const int DefaultRetentionDays = 3;

    private const string FilePrefix = "orbis-";
    private const string FileExtension = ".log";
    private const string DayFormat = "yyyy-MM-dd";
    private const int QueueCapacity = 10_000;

    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly TimeProvider _time;
    private readonly Channel<Entry> _queue;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly Task _writer;
    private long _dropped;

    public FileLoggerProvider(string directory, int retentionDays = DefaultRetentionDays, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(retentionDays, 1);

        _directory = directory;
        _retentionDays = retentionDays;
        _time = time ?? TimeProvider.System;
        _queue = Channel.CreateBounded<Entry>(
            new BoundedChannelOptions(QueueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropWrite
            },
            _ => Interlocked.Increment(ref _dropped));
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>The file the lines of <paramref name="day"/> go to.</summary>
    public string PathOf(DateOnly day) =>
        Path.Combine(_directory, FilePrefix + day.ToString(DayFormat, CultureInfo.InvariantCulture) + FileExtension);

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    /// <summary>Writes out what is queued, waiting a couple of seconds at most, and closes the file.</summary>
    public void Dispose()
    {
        _queue.Writer.TryComplete();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }

    private void Enqueue(LogLevel level, string category, string message, Exception? exception) =>
        _queue.Writer.TryWrite(new Entry(_time.GetLocalNow(), level, category, message, exception));

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        var day = DateOnly.MinValue;
        try
        {
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                try
                {
                    while (_queue.Reader.TryRead(out var entry))
                    {
                        var entryDay = DateOnly.FromDateTime(entry.Time.DateTime);
                        if (writer is null || entryDay != day)
                        {
                            writer?.Dispose();
                            writer = null;
                            day = entryDay;
                            DeleteExpired(day);
                            writer = Open(day);
                        }

                        Write(writer, entry);
                    }

                    var dropped = Interlocked.Exchange(ref _dropped, 0);
                    if (dropped > 0 && writer is not null)
                    {
                        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"{_time.GetLocalNow():yyyy-MM-dd HH:mm:ss.fff} WARN  {typeof(FileLoggerProvider).FullName}: {dropped} lines dropped, the log could not keep up"));
                    }

                    writer?.Flush();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The disk said no (full, the file taken away): the lines of this round are
                    // lost, and the file is opened again with the next ones.
                    writer?.Dispose();
                    writer = null;
                }
            }
        }
        finally
        {
            writer?.Dispose();
        }
    }

    /// <summary>
    /// Appended to, never truncated: two runs of the same day share a file. Anyone may read it, or
    /// delete it, while it is open, so the file can be looked at while the application runs.
    /// </summary>
    private StreamWriter Open(DateOnly day)
    {
        Directory.CreateDirectory(_directory);
        var stream = new FileStream(
            PathOf(day), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>The files of <see cref="_retentionDays"/> days ago and before: today is the first of the days kept.</summary>
    private void DeleteExpired(DateOnly today)
    {
        var oldestKept = today.AddDays(1 - _retentionDays);
        try
        {
            foreach (var path in Directory.EnumerateFiles(_directory, FilePrefix + "*" + FileExtension))
            {
                var name = Path.GetFileNameWithoutExtension(path)[FilePrefix.Length..];
                if (DateOnly.TryParseExact(name, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fileDay)
                    && fileDay < oldestKept)
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A file another process holds without FileShare.Delete stays until the next day.
        }
    }

    private static void Write(StreamWriter writer, Entry entry)
    {
        writer.Write(entry.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        writer.Write(' ');
        writer.Write(LevelOf(entry.Level));
        writer.Write(' ');
        writer.Write(entry.Category);
        writer.Write(": ");
        writer.WriteLine(entry.Message);
        if (entry.Exception is not null)
        {
            writer.WriteLine(entry.Exception.ToString());
        }
    }

    private static string LevelOf(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRCE ",
        LogLevel.Debug => "DBUG ",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "FAIL ",
        LogLevel.Critical => "CRIT ",
        _ => "     "
    };

    private readonly record struct Entry(
        DateTimeOffset Time, LogLevel Level, string Category, string Message, Exception? Exception);

    /// <summary>The levels are filtered by the logging rules before a line gets here.</summary>
    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.Enqueue(logLevel, category, formatter(state, exception), exception);
        }
    }
}
