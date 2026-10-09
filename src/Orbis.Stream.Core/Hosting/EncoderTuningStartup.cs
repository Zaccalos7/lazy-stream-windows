using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbis.Stream.Core.Services;

namespace Orbis.Stream.Core.Hosting;

/// <summary>
/// Fits the default settings to the machine once the database is ready (see
/// <see cref="EncoderTuningService.TuneDefaultsAsync"/>). It runs ffmpeg a few times, so it runs
/// behind the window rather than in front of it: a live started before it is done goes out with
/// the defaults as they were, which is what they were for every live until now.
/// </summary>
public sealed class EncoderTuningStartup(EncoderTuningService tuning, ILogger<EncoderTuningStartup> logger) : IHostedService
{
    private readonly CancellationTokenSource _stopping = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await tuning.TuneDefaultsAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "The default settings could not be fitted to this machine");
            }
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        return Task.CompletedTask;
    }
}
