namespace Orbis.Stream.Core.Services;

/// <summary>
/// Tells the open pages that what they show has changed, so that they ask for it again instead of
/// asking every few seconds. Every change is a counter tick, whoever caused it: the streaming
/// engine when a video starts or ends, the pages when the user starts or stops a live, the API when
/// something outside the window happens.
/// </summary>
public sealed class LiveChangeNotifier
{
    private readonly object _gate = new();
    private TaskCompletionSource _pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _version;

    /// <summary>How many changes happened so far. A page compares it to know if it is behind.</summary>
    public long Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    /// <summary>One tick: every listener wakes up, whoever caused the change.</summary>
    public void Raise()
    {
        lock (_gate)
        {
            _version++;
            _pulse.TrySetResult();
            _pulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>
    /// Waits for the next change and answers the number it will have. A listener that arrives
    /// after the change it missed is answered at once instead of waiting for the following one.
    /// </summary>
    public async Task<long> WaitAsync(long since, CancellationToken token)
    {
        while (true)
        {
            Task pulse;
            long version;

            lock (_gate)
            {
                version = _version;
                if (version != since)
                {
                    return version;
                }

                // The pulse is read inside the lock, so a change cannot slip between this test and
                // the wait below: Raise cannot replace the source until this one is read.
                pulse = _pulse.Task;
            }

            await pulse.WaitAsync(token).ConfigureAwait(false);
        }
    }
}
