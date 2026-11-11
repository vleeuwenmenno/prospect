namespace Prospect.Listing;

/// <summary>
/// Drops expired entries on a timer, so a server that stops heartbeating leaves the list (and frees its address's
/// slot) even when no request arrives.
/// </summary>
public sealed class ExpirySweeper(ListingStore store, ListingOptions options, ILogger<ExpirySweeper> log) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.SweepIntervalMilliseconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var removed = store.Sweep();
                if (removed > 0)
                {
                    log.LogDebug("Expired {Count} entries", removed);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
