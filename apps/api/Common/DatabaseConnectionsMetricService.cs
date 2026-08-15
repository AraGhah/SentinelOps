using Npgsql;

namespace SentinelOps.Api.Common;

// Reports the Aurora cluster's total open-connection count once a minute —
// server-side (pg_stat_activity), not this task's own Npgsql pool, since
// that's the number capacity planning actually cares about: how close the
// whole fleet of ECS tasks is to Aurora's max_connections, not any single
// task's local pool. Plain ADO.NET (NpgsqlCommand), not EF Core's
// FromSqlRaw/ExecuteSqlRaw — current_database() takes no input, so there's
// nothing to parameterize, and this deliberately isn't run through
// SentinelOpsDbContext at all (a fixed monitoring query has no tenant to
// scope it to).
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
                // A transient DB hiccup here shouldn't crash the whole
                // background service — just skip this tick and try again in
                // Interval.
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
