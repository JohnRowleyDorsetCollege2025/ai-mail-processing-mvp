using Microsoft.Extensions.Options;

namespace Aura.WorkerService;

/// <summary>Settings every periodic task has. Task-specific options derive from this.</summary>
public class PeriodicTaskOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Delay between runs, as a TimeSpan string in config, e.g. "00:00:30".</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Base class for the worker's tasks: runs <see cref="RunOnceAsync"/> repeatedly, waiting <c>Interval</c> between runs.
/// Options are re-read before every run, so changes to appsettings.json (Enabled, Interval) apply without a restart.
/// A failed run is logged and the next run still happens.
/// </summary>
public abstract class PeriodicTask<TOptions>(IOptionsMonitor<TOptions> options, ILogger logger) : BackgroundService
    where TOptions : PeriodicTaskOptions
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);

    protected TOptions Options => options.CurrentValue;

    protected abstract Task RunOnceAsync(CancellationToken stoppingToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var name = GetType().Name;
        logger.LogInformation("{Task} started (enabled: {Enabled}, interval: {Interval})", name, Options.Enabled, Options.Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (Options.Enabled)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "{Task} run failed; will retry after {Interval}", name, Options.Interval);
                }
            }

            try
            {
                await Task.Delay(Options.Interval < MinimumInterval ? MinimumInterval : Options.Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("{Task} stopped", name);
    }
}
