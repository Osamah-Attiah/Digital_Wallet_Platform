using DigitalWallet.Caching;
using Npgsql;

namespace DigitalWallet.Api.Foundation;

public static class DependencyHealth
{
    public static async Task<IResult> CheckAsync(IConfiguration config, RedisRuntime redis, CacheOptions options, CancellationToken ct)
    {
        bool postgres;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await using var connection = new NpgsqlConnection(config["Platform:PostgreSql:ConnectionString"]); await connection.OpenAsync(timeout.Token);
            await using var command = new NpgsqlCommand("SELECT 1", connection); await command.ExecuteScalarAsync(timeout.Token); postgres = true;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { postgres = false; }
        var available = options.Enabled && await redis.ProbeAsync(ct);
        return Results.Json(new { status = !postgres ? "unavailable" : options.Enabled && !available ? "degraded" : "healthy",
            postgres = postgres ? "available" : "unavailable", redis = !options.Enabled ? "disabled" : available ? "available" : "unavailable", financialReady = postgres }, statusCode: postgres ? 200 : 503);
    }
}
