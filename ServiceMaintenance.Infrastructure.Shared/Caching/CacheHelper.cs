using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ServiceMaintenance.Infrastructure.Shared.Caching;

public class CacheHelper
{
    private readonly IRedisService _redis;
    private readonly ILogger<CacheHelper> _logger;

    public CacheHelper(IRedisService redis, ILogger<CacheHelper> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    // ✅ Get from cache or fetch fresh — with timing log
    public async Task<T> GetOrSetAsync<T>(
        string key,
        Func<Task<T>> fetchFunc,
        TimeSpan? expiry = null) where T : class
    {
        try
        {
            var cached = await _redis.GetAsync(key);
            if (cached is not null)
            {
                _logger.LogDebug("✅ Cache HIT: {Key}", key);
                return JsonSerializer.Deserialize<T>(cached)!;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("⚠️ Cache read failed [{Key}]: {Message}", key, ex.Message);
        }

        _logger.LogDebug("📥 Cache MISS: {Key} — fetching...", key);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var data = await fetchFunc();
        sw.Stop();

        _logger.LogDebug("✅ Fetched [{Key}] in {Ms}ms", key, sw.ElapsedMilliseconds);

        if (data is not null)
        {
            try
            {
                await _redis.SetAsync(
                    key,
                    JsonSerializer.Serialize(data),
                    expiry ?? TimeSpan.FromMinutes(5)
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ Cache write failed [{Key}]: {Message}", key, ex.Message);
            }
        }

        return data;
    }

    // ✅ Invalidate exact key
    public async Task InvalidateAsync(string key)
    {
        try
        {
            await _redis.DeleteAsync(key);
            _logger.LogDebug("🗑️ Invalidated: {Key}", key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("⚠️ Invalidate failed [{Key}]: {Message}", key, ex.Message);
        }
    }

    // ✅ Invalidate multiple exact keys (parallel)
    public async Task InvalidateManyAsync(params string[] keys)
    {
        var tasks = keys.Select(InvalidateAsync);
        await Task.WhenAll(tasks);
    }

    // ✅ FIXED — actually deletes keys by prefix pattern
    public async Task InvalidateByPrefixAsync(string prefix)
    {
        try
        {
            await _redis.DeleteByPrefixAsync(prefix);
            _logger.LogDebug("🗑️ Invalidated prefix: {Prefix}*", prefix);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("⚠️ Prefix invalidate failed [{Prefix}]: {Message}", prefix, ex.Message);
        }
    }

    public async Task<bool> ExistsAsync(string key)
    {
        try { return await _redis.ExistsAsync(key); }
        catch { return false; }
    }
}