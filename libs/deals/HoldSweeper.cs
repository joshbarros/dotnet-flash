using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Flash.Deals;

/// <summary>
/// Gives back the pieces of holds that were never confirmed. Every instance runs one; the claim on each hold is a
/// conditional UPDATE with SKIP LOCKED, so several sweepers never act on the same hold.
/// </summary>
public sealed class HoldSweeper(IServiceScopeFactory scopes, IOptions<DealOptions> options, ILogger<HoldSweeper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(options.Value.SweepIntervalMs, 50)));
        while (await WaitAsync(timer, stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<DealsHandler>();
                int swept;
                do
                {
                    swept = await handler.SweepAsync(DateTimeOffset.UtcNow, ct: stoppingToken);
                    if (swept > 0)
                        log.LogInformation("Expired {Count} unconfirmed holds", swept);
                }
                while (swept >= 50 && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Sweeping expired holds failed; it will be tried again");
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
