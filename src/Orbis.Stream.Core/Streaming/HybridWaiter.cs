using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Orbis.Stream.Core.Streaming;

/// <summary>
/// The pacing of the Java frame loop, as it was written there:
/// <code>
/// long differenceNanos = targetTimeNanos - currentTimeNanos;
/// if (differenceNanos > TWO_MILLISECONDS) LockSupport.parkNanos(differenceNanos - ONE_MILLISECONDS);
/// while (System.nanoTime() &lt; targetTimeNanos) Thread.onSpinWait();
/// </code>
/// The thread sleeps through the difference less one millisecond and spins through the last one,
/// so it is already awake when the frame is due instead of waking up after it.
/// <para>What .NET changes is the sleep. <c>parkNanos</c> wakes about when it is told to on Linux;
/// a sleep on Windows lasts up to a tick of the system timer, 15.6 ms. So on Windows the park is a
/// high resolution waitable timer (Windows 10 1803 and later), which wakes within a fraction of a
/// millisecond and changes nothing for the rest of the system; where there is none, a plain sleep
/// with the system timer at one millisecond. Either way the sleeper is measured once, and how late
/// it wakes is taken off the park, so the spin really starts a millisecond before the frame.</para>
/// </summary>
public sealed class HybridWaiter : IDisposable
{
    /// <summary>TWO_MILLISECONDS of Java: below it the wait is all spin.</summary>
    private static readonly long TwoMilliseconds = Stopwatch.Frequency / 500;

    /// <summary>ONE_MILLISECONDS of Java: the stretch that is always spun.</summary>
    private static readonly long OneMillisecond = Stopwatch.Frequency / 1000;

    private readonly ISleeper _sleeper;

    /// <summary>How much later than asked the sleeper wakes, in stopwatch ticks.</summary>
    private readonly long _overshoot;

    private HybridWaiter(ISleeper sleeper, long overshoot)
    {
        _sleeper = sleeper;
        _overshoot = Math.Max(0, overshoot);
    }

    /// <summary>How late a park was measured to wake, for the log.</summary>
    public TimeSpan SleepOvershoot => Stopwatch.GetElapsedTime(0, _overshoot);

    /// <summary>Which sleeper parks the thread: the high resolution timer or a plain sleep.</summary>
    public string SleeperName => _sleeper.Name;

    public static long Now => Stopwatch.GetTimestamp();

    /// <summary>Stopwatch ticks of a duration.</summary>
    public static long Ticks(TimeSpan duration) => (long)(duration.TotalSeconds * Stopwatch.Frequency);

    /// <summary>
    /// A waiter for the calling thread: the most precise sleeper this machine has, measured with a
    /// few one millisecond parks. The worst of how late they woke is the margin from then on.
    /// </summary>
    public static HybridWaiter Create()
    {
        ISleeper sleeper = HighResolutionTimer.TryCreate() is { } timer ? timer : new PlainSleeper();

        long worst = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var before = Stopwatch.GetTimestamp();
            sleeper.Sleep(OneMillisecond, CancellationToken.None);
            worst = Math.Max(worst, Stopwatch.GetTimestamp() - before - OneMillisecond);
        }

        return new HybridWaiter(sleeper, worst);
    }

    /// <summary>Returns when the stopwatch reaches <paramref name="target"/>, or at once if it is past.</summary>
    public void WaitUntil(long target, CancellationToken cancellationToken)
    {
        var difference = target - Stopwatch.GetTimestamp();
        if (difference <= 0)
        {
            return;
        }

        if (difference > TwoMilliseconds)
        {
            var park = difference - OneMillisecond - _overshoot;
            if (park > 0)
            {
                _sleeper.Sleep(park, cancellationToken);
            }
        }

        // Thread.onSpinWait(): SpinOnce with -1 spins and yields the core, and never turns into
        // the Sleep(1) that would oversleep the very millisecond this loop is here for.
        var spinner = new SpinWait();
        while (Stopwatch.GetTimestamp() < target)
        {
            spinner.SpinOnce(sleep1Threshold: -1);
        }
    }

    public void Dispose() => _sleeper.Dispose();

    private interface ISleeper : IDisposable
    {
        string Name { get; }

        void Sleep(long ticks, CancellationToken cancellationToken);
    }

    /// <summary>
    /// A waitable timer created with CREATE_WAITABLE_TIMER_HIGH_RESOLUTION: due times in 100 ns
    /// units, honoured to a fraction of a millisecond whatever the system timer is set to. It is a
    /// wait handle like any other, so a cancellation wakes it as well.
    /// </summary>
    private sealed class HighResolutionTimer : ISleeper
    {
        private const uint CreateWaitableTimerHighResolution = 0x00000002;
        private const uint TimerAllAccess = 0x1F0003;

        private readonly TimerHandle _timer;
        private CancellationToken _lastToken;
        private WaitHandle[]? _handles;

        private HighResolutionTimer(TimerHandle timer) => _timer = timer;

        public string Name => "high resolution timer";

        public static HighResolutionTimer? TryCreate()
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            try
            {
                var handle = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    return null;
                }

                return new HighResolutionTimer(new TimerHandle(handle));
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
        }

        public void Sleep(long ticks, CancellationToken cancellationToken)
        {
            // Negative is relative, in 100 ns units.
            var due = -(long)(ticks * (10_000_000d / Stopwatch.Frequency));
            if (due >= 0 || !SetWaitableTimer(_timer.SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                return;
            }

            if (!cancellationToken.CanBeCanceled)
            {
                _timer.WaitOne();
                return;
            }

            if (_handles is null || _lastToken != cancellationToken)
            {
                _handles = [_timer, cancellationToken.WaitHandle];
                _lastToken = cancellationToken;
            }

            if (WaitHandle.WaitAny(_handles) == 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        public void Dispose() => _timer.Dispose();

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(
            SafeWaitHandle timer,
            ref long dueTime,
            int period,
            IntPtr completionRoutine,
            IntPtr completionArgument,
            [MarshalAs(UnmanagedType.Bool)] bool resume);

        private sealed class TimerHandle : WaitHandle
        {
            public TimerHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
        }
    }

    /// <summary>
    /// A sleep in whole milliseconds. On Linux that is nanosleep and wakes on time; on a Windows
    /// without the high resolution timer the system timer is raised to one millisecond for as long
    /// as the waiter lives, which is what every streaming application does.
    /// </summary>
    private sealed class PlainSleeper : ISleeper
    {
        private readonly TimerResolution _resolution = TimerResolution.Raise();

        public string Name => "sleep";

        public void Sleep(long ticks, CancellationToken cancellationToken)
        {
            var milliseconds = (int)(ticks * 1000 / Stopwatch.Frequency);
            if (milliseconds < 1)
            {
                return;
            }

            if (cancellationToken.CanBeCanceled)
            {
                if (cancellationToken.WaitHandle.WaitOne(milliseconds))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            else
            {
                Thread.Sleep(milliseconds);
            }
        }

        public void Dispose() => _resolution.Dispose();
    }
}

/// <summary>
/// Asks Windows for a one millisecond system timer while a live is paced, and gives it back after.
/// Only the fallback sleeper needs it; elsewhere the timer is already fine and this does nothing.
/// </summary>
public sealed class TimerResolution : IDisposable
{
    private readonly bool _raised;

    private TimerResolution(bool raised) => _raised = raised;

    public static TimerResolution Raise()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new TimerResolution(false);
        }

        try
        {
            return new TimerResolution(TimeBeginPeriod(1) == 0);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return new TimerResolution(false);
        }
    }

    public void Dispose()
    {
        if (_raised)
        {
            TimeEndPeriod(1);
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);
}
