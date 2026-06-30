using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace ServiceMaintenance.Infrastructure.Shared.Messaging;

/// <summary>
/// Periodically polls every known dead-letter queue's depth and logs a
/// warning when messages are sitting there unprocessed. Without this,
/// "permanent error" messages silently accumulate in *.dead-letter with
/// no visibility (see RabbitMQConsumerBase — they're never consumed).
///
/// This does NOT reprocess dead-lettered messages automatically (that's
/// a separate decision requiring a human/replay tool); it only makes the
/// failure visible so it can be alerted on / investigated.
/// </summary>
public class DeadLetterMonitorService : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ILogger<DeadLetterMonitorService> _logger;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private static readonly string[] MonitoredQueues =
    {
        MessageQueueHelper.Queues.ReceiveItem,
        MessageQueueHelper.Queues.Order,
        MessageQueueHelper.Queues.AwaitCustomer,
        MessageQueueHelper.Queues.AwaitSparePart,
        MessageQueueHelper.Queues.InspectItem,
        MessageQueueHelper.Queues.InspectionItem,
        MessageQueueHelper.Queues.RepairItem,
        MessageQueueHelper.Queues.FinishItem,
        MessageQueueHelper.Queues.SparePart,     // NEW
         MessageQueueHelper.Queues.ItemModule,
    };

    public DeadLetterMonitorService(IConfiguration config, ILogger<DeadLetterMonitorService> logger)
    {
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give consumers time to declare their queues/DLQs first.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckDeadLettersAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ DeadLetterMonitor check failed: {Message}", ex.Message);
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CheckDeadLettersAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            HostName = _config["RabbitMQ:Host"] ?? "localhost",
            Port = int.Parse(_config["RabbitMQ:Port"] ?? "5672"),
            UserName = _config["RabbitMQ:Username"] ?? "guest",
            Password = _config["RabbitMQ:Password"] ?? "guest",
        };

        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        foreach (var queue in MonitoredQueues)
        {
            var dlqName = $"{queue}.dead-letter";
            try
            {
                // Passive declare just to read message count — does not
                // create the queue, fails harmlessly if it doesn't exist yet.
                var result = await channel.QueueDeclarePassiveAsync(dlqName, ct);

                if (result.MessageCount > 0)
                {
                    _logger.LogWarning(
                        "🚨 Dead-letter queue [{Dlq}] has {Count} unprocessed message(s) — investigate failed messages",
                        dlqName, result.MessageCount);
                }
                else
                {
                    _logger.LogDebug("✅ Dead-letter queue [{Dlq}] empty", dlqName);
                }
            }
            catch (Exception ex)
            {
                // Queue likely doesn't exist yet (consumer hasn't started) — not an error.
                _logger.LogDebug("DLQ check skipped for [{Dlq}]: {Message}", dlqName, ex.Message);
            }
        }
    }
}