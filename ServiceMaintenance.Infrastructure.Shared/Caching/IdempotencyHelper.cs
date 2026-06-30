using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace ServiceMaintenance.Infrastructure.Shared.Caching
{
    public class IdempotencyHelper
    {
        private readonly IRedisService _redis;
        private readonly ILogger<IdempotencyHelper> _logger;
        private static readonly TimeSpan DeduplicationExpiry = TimeSpan.FromHours(24);

        public IdempotencyHelper(IRedisService redis, ILogger<IdempotencyHelper> logger)
        {
            _redis = redis;
            _logger = logger;
        }

        /// <summary>
        /// ពិនិត្យថា message នេះ (តាម MessageId) processed រួចហើយឬនៅ
        /// true = processed រួចហើយ → Skip
        /// false = ថ្មី → Process it
        /// </summary>
        public async Task<bool> IsAlreadyProcessedAsync(string queue, Guid messageId)
        {
            string key = BuildKey(queue, messageId);
            try
            {
                bool exists = await _redis.ExistsAsync(key);
                if (exists)
                    _logger.LogDebug("⏭️ Duplicate skipped: [{Queue}] MessageId={MessageId}", queue, messageId);
                return exists;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ Idempotency check failed [{Queue}]: {Message} — allowing", queue, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Mark message ថា processed រួចហើយ — Call AFTER successful processing
        /// </summary>
        public async Task MarkAsProcessedAsync(string queue, Guid messageId)
        {
            string key = BuildKey(queue, messageId);
            try
            {
                await _redis.SetAsync(key, "1", DeduplicationExpiry);
                _logger.LogDebug("✅ Marked processed: [{Queue}] MessageId={MessageId}", queue, messageId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ Failed to mark processed [{Queue}]: {Message}", queue, ex.Message);
            }
        }

        // Key: "idempotent:{queue}:{messageId}"
        private static string BuildKey(string queue, Guid messageId)
            => $"idempotent:{queue}:{messageId}";
    }
}