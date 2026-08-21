using Npgsql;

namespace SentinelOps.Api.Common;

// Reports the Aurora cluster's total open-connection count once a minute via
// pg_stat_activity, not this task's own pool: capacity planning cares about the
// whole fleet vs max_connections. Bypasses SentinelOpsDbContext since this query
// has no tenant to scope it to.
public class DatabaseConnectionsMetricService(NpgsqlDataSource dataSource) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(stoppingToken);
                await using var command = new NpgsqlCommand(
                    "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database()", connection);
                var count = (long)(await command.ExecuteScalarAsync(stoppingToken) ?? 0L);
                MetricsEmitter.Emit("DatabaseConnections", count, "Count");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Skip this tick on a transient DB hiccup rather than crashing the service.
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
