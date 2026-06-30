using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Microsoft.Extensions.Configuration;
namespace ServiceMaintenance.Infrastructure.Shared.Caching;
public interface IRedisService
{
    Task SetAsync(string key, string value, TimeSpan? expiry = null);
    Task<string?> GetAsync(string key);
    Task DeleteAsync(string key);
    Task DeleteByPrefixAsync(string prefix);
    Task<bool> ExistsAsync(string key);
    Task<bool> IsHealthyAsync();
}
public class RedisService : IRedisService, IDisposable
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly IDatabase _db;
    private readonly ILogger<RedisService> _logger;
    public RedisService(IConfiguration config, ILogger<RedisService> logger)
    {
        _logger = logger;
        var connectionString = config["Redis:ConnectionString"] ?? "localhost:6379";
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        options.ConnectRetry = 3;
        options.ReconnectRetryPolicy = new LinearRetry(2000);
        _multiplexer = ConnectionMultiplexer.Connect(options);
        _db = _multiplexer.GetDatabase();
        _logger.LogInformation("✅ Redis connected: {ConnectionString}", connectionString);
    }
    public async Task SetAsync(string key, string value, TimeSpan? expiry = null)
    {
        if (expiry.HasValue)
            await _db.StringSetAsync(key, value, expiry.Value);
        else
            await _db.StringSetAsync(key, value);
    }
    public async Task<string?> GetAsync(string key)
    {
        var value = await _db.StringGetAsync(key);
        return value.HasValue ? value.ToString() : null;
    }
    public async Task DeleteAsync(string key)
        => await _db.KeyDeleteAsync(key);

    // ✅ FIXED — bounded SCAN (pageSize) + batched deletes instead of one
    // giant KeyDeleteAsync(keys[]) call, so this never blocks Redis on a
    // large keyspace and never builds an unbounded array in memory.
    public async Task DeleteByPrefixAsync(string prefix)
    {
        try
        {
            var server = _multiplexer.GetServer(_multiplexer.GetEndPoints().First());
            var pattern = $"{prefix}*";

            const int scanPageSize = 250;
            const int deleteBatchSize = 500;

            var batch = new List<RedisKey>(deleteBatchSize);
            int totalDeleted = 0;

            await foreach (var key in server.KeysAsync(pattern: pattern, pageSize: scanPageSize))
            {
                batch.Add(key);
                if (batch.Count >= deleteBatchSize)
                {
                    totalDeleted += (int)await _db.KeyDeleteAsync(batch.ToArray());
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
                totalDeleted += (int)await _db.KeyDeleteAsync(batch.ToArray());

            _logger.LogDebug("🗑️ Deleted {Count} keys with prefix: {Prefix}", totalDeleted, prefix);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("⚠️ Prefix delete failed [{Prefix}]: {Message}", prefix, ex.Message);
        }
    }
    public async Task<bool> ExistsAsync(string key)
        => await _db.KeyExistsAsync(key);
    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            await _db.PingAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }
    public void Dispose()
        => _multiplexer.Dispose();
}