namespace Chandlery.Services;

/// <summary>
/// Keeps the market exchange rates fresh: shortly after the app starts and then every few hours it checks whether today's rates have
/// been fetched and, if not, fetches them. A failed fetch is logged and tried again later; the last known rates keep being used.
/// Turn it off with "Fx:AutoRefresh": false.
/// </summary>
public class FxRefreshWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<FxRefreshWorker> logger) : BackgroundService
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Fx:AutoRefresh", true)) return;

        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var fx = scope.ServiceProvider.GetRequiredService<FxService>();
                var (latest, _) = await fx.GetMarketStatusAsync();
                if (latest is null || latest.Value.Date < DateTime.UtcNow.Date)
                {
                    var result = await fx.RefreshMarketAsync(stoppingToken);
                    logger.LogInformation("Exchange rates refreshed: {Saved} saved for {Date:yyyy-MM-dd}", result.Saved, result.Date);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not refresh exchange rates; the last known rates stay in use.");
            }

            try { await Task.Delay(CheckEvery, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
