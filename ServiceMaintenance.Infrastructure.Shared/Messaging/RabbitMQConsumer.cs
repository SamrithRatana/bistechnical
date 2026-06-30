using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceMaintenance.Infrastructure.Shared.Caching;
using System.Text;
using System.Text.Json;

namespace ServiceMaintenance.Infrastructure.Shared.Messaging;

/// <summary>
/// Generic RabbitMQ consumer base — handles reconnect, ack/nack, graceful shutdown.
/// Updated: Smart Nack + Dead Letter Queue + MessageId-based Idempotency
/// </summary>
public abstract class RabbitMQConsumerBase : BackgroundService
{
    protected readonly IConfiguration Config;
    protected readonly ILogger Logger;
    protected readonly string QueueName;
    protected readonly IdempotencyHelper Idempotency;

    private IConnection? _connection;
    private IChannel? _channel;

    protected RabbitMQConsumerBase(
        IConfiguration config,
        ILogger logger,
        string queueName,
        IdempotencyHelper idempotency)
    {
        Config = config;
        Logger = logger;
        QueueName = queueName;
        Idempotency = idempotency;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError("❌ Consumer [{Queue}] crashed: {Message} — retrying in 10s", QueueName, ex.Message);
                await Task.Delay(10_000, stoppingToken);
            }
        }

        await CleanupAsync();
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            HostName = Config["RabbitMQ:Host"] ?? "localhost",
            Port = int.Parse(Config["RabbitMQ:Port"] ?? "5672"),
            UserName = Config["RabbitMQ:Username"] ?? "guest",
            Password = Config["RabbitMQ:Password"] ?? "guest",
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
        };

        _connection = await factory.CreateConnectionAsync(ct);
        _channel = await _connection.CreateChannelAsync(cancellationToken: ct);
        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, ct);

        // ✅ Declare main queue with Dead Letter Queue support
        // NOTE: x-delivery-limit requires x-queue-type=quorum. Removed to avoid
        // declaration conflicts on classic queues. Smart Nack logic below already
        // controls retry vs. discard behavior without needing this argument.
        await _channel.QueueDeclareAsync(
            queue: QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object>
            {
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = $"{QueueName}.dead-letter",
            },
            cancellationToken: ct);

        // ✅ Declare dead-letter queue (monitor failed messages)
        await _channel.QueueDeclareAsync(
            queue: $"{QueueName}.dead-letter",
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: ct);

        var consumer = new AsyncEventingBasicConsumer(_channel);

        consumer.ReceivedAsync += async (_, ea) =>
        {
            var body = ea.Body.ToArray();
            var message = Encoding.UTF8.GetString(body);

            try
            {
                Logger.LogInformation("📥 [{Queue}] received message ({Bytes}b)", QueueName, body.Length);
                await HandleMessageAsync(message, ct);
                await _channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
                Logger.LogDebug("✅ [{Queue}] acked {DeliveryTag}", QueueName, ea.DeliveryTag);
            }
            catch (Exception ex)
            {
                Logger.LogError("❌ [{Queue}] handler failed: {Message}", QueueName, ex.Message);

                bool isPermanentError =
                    ex is JsonException ||
                    ex is ArgumentNullException ||
                    ex is ArgumentException ||
                    ex is InvalidOperationException;

                await _channel.BasicNackAsync(
                    ea.DeliveryTag,
                    multiple: false,
                    requeue: !isPermanentError,
                    ct);

                if (isPermanentError)
                    Logger.LogWarning("⚠️ [{Queue}] Permanent error — message sent to dead-letter: {Type}", QueueName, ex.GetType().Name);
                else
                    Logger.LogWarning("⚠️ [{Queue}] Transient error — message requeued for retry", QueueName);
            }
        };

        await _channel.BasicConsumeAsync(
            queue: QueueName,
            autoAck: false,
            consumer: consumer,
            cancellationToken: ct);

        Logger.LogInformation("✅ Consumer [{Queue}] started", QueueName);
        await Task.Delay(Timeout.Infinite, ct);
    }

    protected abstract Task HandleMessageAsync(string message, CancellationToken ct);

    protected async Task InvalidateCacheAsync(AsyncServiceScope scope, params string[] prefixes)
    {
        try
        {
            var cache = scope.ServiceProvider.GetRequiredService<CacheHelper>();
            var tasks = prefixes.Select(p =>
                p == CacheKeys.DashboardStats
                    ? cache.InvalidateAsync(p)
                    : cache.InvalidateByPrefixAsync(p));
            await Task.WhenAll(tasks);
            Logger.LogDebug("🗑️ Cache invalidated: {Prefixes}", string.Join(", ", prefixes));
        }
        catch (Exception ex)
        {
            Logger.LogWarning("⚠️ Cache invalidation failed: {Message}", ex.Message);
        }
    }

    private async Task CleanupAsync()
    {
        try
        {
            if (_channel is not null) await _channel.DisposeAsync();
            if (_connection is not null) await _connection.DisposeAsync();
            Logger.LogInformation("✅ Consumer [{Queue}] stopped cleanly", QueueName);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("⚠️ Cleanup error [{Queue}]: {Message}", QueueName, ex.Message);
        }
    }
}

// ─────────────────────────────────────────────────
// 1. ReceiveItem consumer
// ─────────────────────────────────────────────────
public class ReceiveItemConsumer : RabbitMQConsumerBase
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ReceiveItemConsumer(
        IConfiguration config,
        ILogger<ReceiveItemConsumer> logger,
        IServiceScopeFactory scopeFactory,
        IdempotencyHelper idempotency)
        : base(config, logger, MessageQueueHelper.Queues.ReceiveItem, idempotency)
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task HandleMessageAsync(string message, CancellationToken ct)
    {
        var msg = JsonSerializer.Deserialize<QueueMessage>(message);
        if (msg is null) return;

        // ✅ IDEMPOTENCY CHECK — based on MessageId, not EntityId+Action
        if (await Idempotency.IsAlreadyProcessedAsync(QueueName, msg.MessageId))
        {
            Logger.LogInformation("⏭️ ReceiveItem MessageId={MessageId} — DUPLICATE, skipping", msg.MessageId);
            return;
        }

        Logger.LogInformation("📋 ReceiveItem [{Action}] ReportNo={ReportNo} EntityId={EntityId} MessageId={MessageId}",
            msg.Action, msg.ReportNo, msg.EntityId, msg.MessageId);

        await using var scope = _scopeFactory.CreateAsyncScope();

        switch (msg.Action)
        {
            case nameof(QueueAction.Create): await OnCreateAsync(scope, msg, ct); break;
            case nameof(QueueAction.Update): await OnUpdateAsync(scope, msg, ct); break;
            case nameof(QueueAction.Delete): await OnDeleteAsync(scope, msg, ct); break;
            case nameof(QueueAction.StatusChange): await OnStatusChangeAsync(scope, msg, ct); break;
            default:
                Logger.LogWarning("⚠️ ReceiveItem unknown action: {Action}", msg.Action);
                return;
        }

        await Idempotency.MarkAsProcessedAsync(QueueName, msg.MessageId);
    }

    protected virtual async Task OnCreateAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("➕ ReceiveItem Create: {ReportNo}", msg.ReportNo);
        await InvalidateCacheAsync(scope, CacheKeys.PrefixReceiveItem, CacheKeys.DashboardStats);
    }

    protected virtual async Task OnUpdateAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("✏️ ReceiveItem Update: {ReportNo}", msg.ReportNo);
        await InvalidateCacheAsync(scope, CacheKeys.PrefixReceiveItem, CacheKeys.DashboardStats);
    }

    protected virtual async Task OnDeleteAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("🗑️ ReceiveItem Delete: {EntityId}", msg.EntityId);
        await InvalidateCacheAsync(scope, CacheKeys.PrefixReceiveItem, CacheKeys.DashboardStats);
    }

    protected virtual async Task OnStatusChangeAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("🔄 ReceiveItem Status → {Status}: {ReportNo}", msg.Status, msg.ReportNo);
        await InvalidateCacheAsync(scope, CacheKeys.PrefixReceiveItem, CacheKeys.PrefixInspectItem, CacheKeys.DashboardStats);
    }
}

// ─────────────────────────────────────────────────
// 2. AwaitCustomer consumer
// ─────────────────────────────────────────────────
public class AwaitCustomerConsumer : RabbitMQConsumerBase
{
    private readonly IServiceScopeFactory _scopeFactory;

    public AwaitCustomerConsumer(
        IConfiguration config,
        ILogger<AwaitCustomerConsumer> logger,
        IServiceScopeFactory scopeFactory,
        IdempotencyHelper idempotency)
        : base(config, logger, MessageQueueHelper.Queues.AwaitCustomer, idempotency)
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task HandleMessageAsync(string message, CancellationToken ct)
    {
        var msg = JsonSerializer.Deserialize<QueueMessage>(message);
        if (msg is null) return;

        if (await Idempotency.IsAlreadyProcessedAsync(QueueName, msg.MessageId))
        {
            Logger.LogInformation("⏭️ AwaitCustomer MessageId={MessageId} — DUPLICATE, skipping", msg.MessageId);
            return;
        }

        Logger.LogInformation("📋 AwaitCustomer [{Action}] ReportNo={ReportNo} EntityId={EntityId} MessageId={MessageId}",
            msg.Action, msg.ReportNo, msg.EntityId, msg.MessageId);

        await using var scope = _scopeFactory.CreateAsyncScope();

        switch (msg.Action)
        {
            case nameof(QueueAction.StatusChange): await OnStatusChangeAsync(scope, msg, ct); break;
            case nameof(QueueAction.Create): await OnCreateAsync(scope, msg, ct); break;
            case nameof(QueueAction.Update): await OnUpdateAsync(scope, msg, ct); break;
            case nameof(QueueAction.Delete): await OnDeleteAsync(scope, msg, ct); break;
            default:
                Logger.LogWarning("⚠️ AwaitCustomer unknown action: {Action}", msg.Action);
                return;
        }

        await Idempotency.MarkAsProcessedAsync(QueueName, msg.MessageId);
    }

    protected virtual async Task OnStatusChangeAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("🔄 AwaitCustomer Status → {Status}: {ReportNo}", msg.Status, msg.ReportNo);
        await InvalidateCacheAsync(scope, CacheKeys.PrefixAwaitCustomer, CacheKeys.DashboardStats);
    }

    protected virtual async Task OnCreateAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("➕ AwaitCustomer Create: {ReportNo}", msg.ReportNo);
        await InvalidateCacheAsync(scope, CacheKeys.PrefixAwaitCustomer, CacheKeys.DashboardStats);
    }

    protected virtual async Task OnUpdateAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("✏️ AwaitCustomer Update: {ReportNo}", msg.ReportNo);
        await InvalidateCacheAsync(scope, CacheKeys.PrefixAwaitCustomer, CacheKeys.DashboardStats);
    }

    protected virtual async Task OnDeleteAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("🗑️ AwaitCustomer Delete: {EntityId}", msg.EntityId);
        await InvalidateCacheAsync(scope, CacheKeys.PrefixAwaitCustomer, CacheKeys.DashboardStats);
    }
}

// ─────────────────────────────────────────────────
// 3. AwaitSparePart consumer
// ─────────────────────────────────────────────────
public class AwaitSparePartConsumer : RabbitMQConsumerBase
{
    private readonly IServiceScopeFactory _scopeFactory;

    public AwaitSparePartConsumer(
        IConfiguration config,
        ILogger<AwaitSparePartConsumer> logger,
        IServiceScopeFactory scopeFactory,
        IdempotencyHelper idempotency)
        : base(config, logger, MessageQueueHelper.Queues.AwaitSparePart, idempotency)
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task HandleMessageAsync(string message, CancellationToken ct)
    {
        var msg = JsonSerializer.Deserialize<QueueMessage>(message);
        if (msg is null) return;

        if (await Idempotency.IsAlreadyProcessedAsync(QueueName, msg.MessageId))
        {
            Logger.LogInformation("⏭️ AwaitSparePart MessageId={MessageId} — DUPLICATE, skipping", msg.MessageId);
            return;
        }

        Logger.LogInformation("📋 AwaitSparePart [{Action}] ReportNo={ReportNo} EntityId={EntityId} MessageId={MessageId}",
            msg.Action, msg.ReportNo, msg.EntityId, msg.MessageId);

        await using var scope = _scopeFactory.CreateAsyncScope();

        switch (msg.Action)
        {
            case nameof(QueueAction.StatusChange): await OnStatusChangeAsync(scope, msg, ct); break;
            case nameof(QueueAction.Create): await OnCreateAsync(scope, msg, ct); break;
            case nameof(QueueAction.Update): await OnUpdateAsync(scope, msg, ct); break;
            case nameof(QueueAction.Delete): await OnDeleteAsync(scope, msg, ct); break;
            default:
                Logger.LogWarning("⚠️ AwaitSparePart unknown action: {Action}", msg.Action);
                return;
        }

        await Idempotency.MarkAsProcessedAsync(QueueName, msg.MessageId);
    }

    protected virtual async Task OnStatusChangeAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("🔄 AwaitSparePart Status → {Status}: {ReportNo}", msg.Status, msg.ReportNo);
        await InvalidateCacheAsync(scope, CacheKeys.PrefixAwaitSparePart, CacheKeys.DashboardStats);
    }

    protected virtual async Task OnCreateAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("➕ AwaitSparePart Create: {ReportNo}", msg.ReportNo);
        await InvalidateCacheAsync(scope, CacheKeys.PrefixAwaitSparePart, CacheKeys.DashboardStats);
    }

    protected virtual async Task OnUpdateAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("✏️ AwaitSparePart Update: {ReportNo}", msg.ReportNo);
        await InvalidateCacheAsync(scope, CacheKeys.PrefixAwaitSparePart, CacheKeys.DashboardStats);
    }

    protected virtual async Task OnDeleteAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("🗑️ AwaitSparePart Delete: {EntityId}", msg.EntityId);
        await InvalidateCacheAsync(scope, CacheKeys.PrefixAwaitSparePart, CacheKeys.DashboardStats);
    }
}

// ─────────────────────────────────────────────────
// 4. InspectItem consumer
// ─────────────────────────────────────────────────
public class InspectItemConsumer : RabbitMQConsumerBase
{
    private readonly IServiceScopeFactory _scopeFactory;

    public InspectItemConsumer(
        IConfiguration config,
        ILogger<InspectItemConsumer> logger,
        IServiceScopeFactory scopeFactory,
        IdempotencyHelper idempotency)
        : base(config, logger, MessageQueueHelper.Queues.InspectItem, idempotency)
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task HandleMessageAsync(string message, CancellationToken ct)
    {
        var msg = JsonSerializer.Deserialize<QueueMessage>(message);
        if (msg is null) return;

        if (await Idempotency.IsAlreadyProcessedAsync(QueueName, msg.MessageId))
        {
            Logger.LogInformation("⏭️ InspectItem MessageId={MessageId} — DUPLICATE, skipping", msg.MessageId);
            return;
        }

        Logger.LogInformation("📋 InspectItem [{Action}] ReportNo={ReportNo} EntityId={EntityId} MessageId={MessageId}",
            msg.Action, msg.ReportNo, msg.EntityId, msg.MessageId);

        await using var scope = _scopeFactory.CreateAsyncScope();

        switch (msg.Action)
        {
            case nameof(QueueAction.Create): await OnCreateAsync(scope, msg, ct); break;
            case nameof(QueueAction.Update): await OnUpdateAsync(scope, msg, ct); break;
            case nameof(QueueAction.Delete): await OnDeleteAsync(scope, msg, ct); break;
            case nameof(QueueAction.StatusChange): await OnStatusChangeAsync(scope, msg, ct); break;
            default:
                Logger.LogWarning("⚠️ InspectItem unknown action: {Action}", msg.Action);
                return;
        }

        await Idempotency.MarkAsProcessedAsync(QueueName, msg.MessageId);
    }

    protected virtual async Task OnCreateAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("➕ InspectItem Create: {ReportNo}", msg.ReportNo);
        await InvalidateCacheAsync(scope, CacheKeys.PrefixInspectItem, CacheKeys.PrefixReceiveItem, CacheKeys.DashboardStats);
    }

    protected virtual async Task OnUpdateAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("✏️ InspectItem Update: {ReportNo}", msg.ReportNo);
        await InvalidateCacheAsync(scope, CacheKeys.PrefixInspectItem, CacheKeys.PrefixInspectionItem, CacheKeys.DashboardStats);
    }

    protected virtual async Task OnDeleteAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("🗑️ InspectItem Delete: {EntityId}", msg.EntityId);
        await InvalidateCacheAsync(scope, CacheKeys.PrefixInspectItem, CacheKeys.PrefixInspectionItem, CacheKeys.DashboardStats);
    }

    protected virtual async Task OnStatusChangeAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("🔄 InspectItem Status → {Status}: {ReportNo}", msg.Status, msg.ReportNo);
        await InvalidateCacheAsync(scope, CacheKeys.PrefixInspectItem, CacheKeys.PrefixInspectionItem, CacheKeys.DashboardStats);
    }
}

// ─────────────────────────────────────────────────
// 5. InspectionItem consumer
// ─────────────────────────────────────────────────
public class InspectionItemConsumer : RabbitMQConsumerBase
{
    private readonly IServiceScopeFactory _scopeFactory;

    public InspectionItemConsumer(
        IConfiguration config,
        ILogger<InspectionItemConsumer> logger,
        IServiceScopeFactory scopeFactory,
        IdempotencyHelper idempotency)
        : base(config, logger, MessageQueueHelper.Queues.InspectionItem, idempotency)
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task HandleMessageAsync(string message, CancellationToken ct)
    {
        var msg = JsonSerializer.Deserialize<QueueMessage>(message);
        if (msg is null) return;

        // ✅ FIXED — ប្រើ MessageId ជំនួស EntityId+Action
        // មុននេះ Action ជានិច្ច "StatusChange" ប៉ុន្តែ Status ប្រែប្រួល (Awaiting Sparepart → Inspecting ។ល។)
        // ប្រសិនបើនៅប្រើ EntityId+Action ជា Key, StatusChange ទីពីរលើ Entity ដូចគ្នានឹងត្រូវ Skip ដោយខុស
        if (await Idempotency.IsAlreadyProcessedAsync(QueueName, msg.MessageId))
        {
            Logger.LogInformation("⏭️ InspectionItem MessageId={MessageId} — DUPLICATE, skipping", msg.MessageId);
            return;
        }

        Logger.LogInformation("📋 InspectionItem [{Action}] ReportNo={ReportNo} EntityId={EntityId} Status={Status} MessageId={MessageId}",
            msg.Action, msg.ReportNo, msg.EntityId, msg.Status, msg.MessageId);

        await using var scope = _scopeFactory.CreateAsyncScope();

        switch (msg.Action)
        {
            case nameof(QueueAction.StatusChange): await OnStatusChangeAsync(scope, msg, ct); break;
            default:
                Logger.LogWarning("⚠️ InspectionItem unknown action: {Action}", msg.Action);
                return;
        }

        await Idempotency.MarkAsProcessedAsync(QueueName, msg.MessageId);
    }

    protected virtual async Task OnStatusChangeAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("🔄 InspectionItem Status → {Status}: {ReportNo}", msg.Status, msg.ReportNo);

        var extraPrefix = msg.Status switch
        {
            "Awaiting Sparepart" => CacheKeys.PrefixAwaitSparePart,
            "Awaiting Customer Confirm" => CacheKeys.PrefixAwaitCustomer,
            "Inspecting" => CacheKeys.PrefixInspectItem,
            _ => null
        };

        if (extraPrefix is not null)
            await InvalidateCacheAsync(scope, CacheKeys.PrefixInspectionItem, extraPrefix, CacheKeys.DashboardStats);
        else
            await InvalidateCacheAsync(scope, CacheKeys.PrefixInspectionItem, CacheKeys.DashboardStats);
    }
}

// ─────────────────────────────────────────────────
// 6. RepairItem consumer
// ─────────────────────────────────────────────────
public class RepairItemConsumer : RabbitMQConsumerBase
{
    private readonly IServiceScopeFactory _scopeFactory;

    public RepairItemConsumer(
        IConfiguration config,
        ILogger<RepairItemConsumer> logger,
        IServiceScopeFactory scopeFactory,
        IdempotencyHelper idempotency)
        : base(config, logger, MessageQueueHelper.Queues.RepairItem, idempotency)
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task HandleMessageAsync(string message, CancellationToken ct)
    {
        var msg = JsonSerializer.Deserialize<QueueMessage>(message);
        if (msg is null) return;

        if (await Idempotency.IsAlreadyProcessedAsync(QueueName, msg.MessageId))
        {
            Logger.LogInformation("⏭️ RepairItem MessageId={MessageId} — DUPLICATE, skipping", msg.MessageId);
            return;
        }

        Logger.LogInformation("📋 RepairItem [{Action}] ReportNo={ReportNo} EntityId={EntityId} MessageId={MessageId}",
            msg.Action, msg.ReportNo, msg.EntityId, msg.MessageId);

        await using var scope = _scopeFactory.CreateAsyncScope();

        switch (msg.Action)
        {
            case nameof(QueueAction.StatusChange): await OnStatusChangeAsync(scope, msg, ct); break;
            case nameof(QueueAction.Create): await OnCreateAsync(scope, msg, ct); break;
            case nameof(QueueAction.Update): await OnUpdateAsync(scope, msg, ct); break;
            case nameof(QueueAction.Delete): await OnDeleteAsync(scope, msg, ct); break;
            default:
                Logger.LogWarning("⚠️ RepairItem unknown action: {Action}", msg.Action);
                return;
        }

        await Idempotency.MarkAsProcessedAsync(QueueName, msg.MessageId);
    }

    protected virtual async Task OnStatusChangeAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("🔄 RepairItem Status → {Status}: {ReportNo}", msg.Status, msg.ReportNo);
        await InvalidateCacheAsync(scope, CacheKeys.PrefixRepairItem, CacheKeys.DashboardStats);
    }

    protected virtual async Task OnCreateAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("➕ RepairItem Create: {ReportNo}", msg.ReportNo);
        await InvalidateCacheAsync(scope, CacheKeys.PrefixRepairItem, CacheKeys.DashboardStats);
    }

    protected virtual async Task OnUpdateAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("✏️ RepairItem Update: {ReportNo}", msg.ReportNo);
        await InvalidateCacheAsync(scope, CacheKeys.PrefixRepairItem, CacheKeys.DashboardStats);
    }

    protected virtual async Task OnDeleteAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("🗑️ RepairItem Delete: {EntityId}", msg.EntityId);
        await InvalidateCacheAsync(scope, CacheKeys.PrefixRepairItem, CacheKeys.DashboardStats);
    }
}

// ─────────────────────────────────────────────────
// 7. FinishedItem consumer
// ─────────────────────────────────────────────────
public class FinishedItemConsumer : RabbitMQConsumerBase
{
    private readonly IServiceScopeFactory _scopeFactory;

    public FinishedItemConsumer(
        IConfiguration config,
        ILogger<FinishedItemConsumer> logger,
        IServiceScopeFactory scopeFactory,
        IdempotencyHelper idempotency)
        : base(config, logger, MessageQueueHelper.Queues.FinishItem, idempotency)
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task HandleMessageAsync(string message, CancellationToken ct)
    {
        var msg = JsonSerializer.Deserialize<QueueMessage>(message);
        if (msg is null) return;

        if (await Idempotency.IsAlreadyProcessedAsync(QueueName, msg.MessageId))
        {
            Logger.LogInformation("⏭️ FinishedItem MessageId={MessageId} — DUPLICATE, skipping", msg.MessageId);
            return;
        }

        Logger.LogInformation("📋 FinishedItem [{Action}] ReportNo={ReportNo} EntityId={EntityId} Status={Status} MessageId={MessageId}",
            msg.Action, msg.ReportNo, msg.EntityId, msg.Status, msg.MessageId);

        await using var scope = _scopeFactory.CreateAsyncScope();

        switch (msg.Action)
        {
            case nameof(QueueAction.StatusChange): await OnStatusChangeAsync(scope, msg, ct); break;
            case nameof(QueueAction.Create): await OnCreateAsync(scope, msg, ct); break;
            case nameof(QueueAction.Update): await OnUpdateAsync(scope, msg, ct); break;
            case nameof(QueueAction.Delete): await OnDeleteAsync(scope, msg, ct); break;
            default:
                Logger.LogWarning("⚠️ FinishedItem unknown action: {Action}", msg.Action);
                return;
        }

        await Idempotency.MarkAsProcessedAsync(QueueName, msg.MessageId);
    }
    // ─────────────────────────────────────────────────
    // 9. SparePart consumer
    // ─────────────────────────────────────────────────
    public class SparePartConsumer : RabbitMQConsumerBase
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public SparePartConsumer(
            IConfiguration config,
            ILogger<SparePartConsumer> logger,
            IServiceScopeFactory scopeFactory,
            IdempotencyHelper idempotency)
            : base(config, logger, MessageQueueHelper.Queues.SparePart, idempotency)
        {
            _scopeFactory = scopeFactory;
        }

        protected override async Task HandleMessageAsync(string message, CancellationToken ct)
        {
            var msg = JsonSerializer.Deserialize<QueueMessage>(message);
            if (msg is null) return;

            if (await Idempotency.IsAlreadyProcessedAsync(QueueName, msg.MessageId))
            {
                Logger.LogInformation("⏭️ SparePart MessageId={MessageId} — DUPLICATE, skipping", msg.MessageId);
                return;
            }

            Logger.LogInformation("📋 SparePart [{Action}] EntityId={EntityId} MessageId={MessageId}",
                msg.Action, msg.EntityId, msg.MessageId);

            await using var scope = _scopeFactory.CreateAsyncScope();

            switch (msg.Action)
            {
                case nameof(QueueAction.Create): await OnCreateAsync(scope, msg, ct); break;
                case nameof(QueueAction.Update): await OnUpdateAsync(scope, msg, ct); break;
                case nameof(QueueAction.Delete): await OnDeleteAsync(scope, msg, ct); break;
                case nameof(QueueAction.StatusChange): await OnStatusChangeAsync(scope, msg, ct); break;
                default:
                    Logger.LogWarning("⚠️ SparePart unknown action: {Action}", msg.Action);
                    return;
            }

            await Idempotency.MarkAsProcessedAsync(QueueName, msg.MessageId);
        }

        protected virtual async Task OnCreateAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
        {
            Logger.LogInformation("➕ SparePart Create: {EntityId}", msg.EntityId);
            await InvalidateCacheAsync(scope, CacheKeys.PrefixSparePart, CacheKeys.DashboardStats);
        }

        protected virtual async Task OnUpdateAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
        {
            Logger.LogInformation("✏️ SparePart Update (qty/stock-out/edit): {EntityId}", msg.EntityId);
            await InvalidateCacheAsync(scope, CacheKeys.PrefixSparePart, CacheKeys.DashboardStats);
        }

        protected virtual async Task OnDeleteAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
        {
            Logger.LogInformation("🗑️ SparePart Delete: {EntityId}", msg.EntityId);
            await InvalidateCacheAsync(scope, CacheKeys.PrefixSparePart, CacheKeys.DashboardStats);
        }

        protected virtual async Task OnStatusChangeAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
        {
            // Used for manual stock-out events
            Logger.LogInformation("🔄 SparePart Status → {Status}: {EntityId}", msg.Status, msg.EntityId);
            await InvalidateCacheAsync(scope, CacheKeys.PrefixSparePart, CacheKeys.DashboardStats);
        }
    }

    // ─────────────────────────────────────────────────
    // 10. ItemModule consumer
    // ─────────────────────────────────────────────────
    public class ItemModuleConsumer : RabbitMQConsumerBase
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public ItemModuleConsumer(
            IConfiguration config,
            ILogger<ItemModuleConsumer> logger,
            IServiceScopeFactory scopeFactory,
            IdempotencyHelper idempotency)
            : base(config, logger, MessageQueueHelper.Queues.ItemModule, idempotency)
        {
            _scopeFactory = scopeFactory;
        }

        protected override async Task HandleMessageAsync(string message, CancellationToken ct)
        {
            var msg = JsonSerializer.Deserialize<QueueMessage>(message);
            if (msg is null) return;

            if (await Idempotency.IsAlreadyProcessedAsync(QueueName, msg.MessageId))
            {
                Logger.LogInformation("⏭️ ItemModule MessageId={MessageId} — DUPLICATE, skipping", msg.MessageId);
                return;
            }

            Logger.LogInformation("📋 ItemModule [{Action}] EntityId={EntityId} MessageId={MessageId}",
                msg.Action, msg.EntityId, msg.MessageId);

            await using var scope = _scopeFactory.CreateAsyncScope();

            switch (msg.Action)
            {
                case nameof(QueueAction.Create): await OnCreateAsync(scope, msg, ct); break;
                case nameof(QueueAction.Update): await OnUpdateAsync(scope, msg, ct); break;
                case nameof(QueueAction.Delete): await OnDeleteAsync(scope, msg, ct); break;
                default:
                    Logger.LogWarning("⚠️ ItemModule unknown action: {Action}", msg.Action);
                    return;
            }

            await Idempotency.MarkAsProcessedAsync(QueueName, msg.MessageId);
        }

        protected virtual async Task OnCreateAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
        {
            Logger.LogInformation("➕ ItemModule Create: {EntityId}", msg.EntityId);
            await InvalidateCacheAsync(scope, CacheKeys.PrefixItemModule, CacheKeys.DashboardStats);
        }

        protected virtual async Task OnUpdateAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
        {
            Logger.LogInformation("✏️ ItemModule Update: {EntityId}", msg.EntityId);
            await InvalidateCacheAsync(scope, CacheKeys.PrefixItemModule, CacheKeys.DashboardStats);
        }

        protected virtual async Task OnDeleteAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
        {
            Logger.LogInformation("🗑️ ItemModule Delete: {EntityId}", msg.EntityId);
            await InvalidateCacheAsync(scope, CacheKeys.PrefixItemModule, CacheKeys.DashboardStats);
        }
    }
    protected virtual async Task OnStatusChangeAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("✅ FinishedItem → Status={Status} ReportNo={ReportNo}", msg.Status, msg.ReportNo);
        await InvalidateCacheAsync(scope, CacheKeys.PrefixFinishRepair, CacheKeys.PrefixRepairItem, CacheKeys.DashboardStats);
    }

    protected virtual async Task OnCreateAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("➕ FinishedItem Create: {ReportNo}", msg.ReportNo);
        await InvalidateCacheAsync(scope, CacheKeys.PrefixFinishRepair, CacheKeys.DashboardStats);
    }

    protected virtual async Task OnUpdateAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("✏️ FinishedItem Update: {ReportNo}", msg.ReportNo);
        await InvalidateCacheAsync(scope, CacheKeys.PrefixFinishRepair, CacheKeys.DashboardStats);
    }

    protected virtual async Task OnDeleteAsync(AsyncServiceScope scope, QueueMessage msg, CancellationToken ct)
    {
        Logger.LogInformation("🗑️ FinishedItem Delete: {EntityId}", msg.EntityId);
        await InvalidateCacheAsync(scope, CacheKeys.PrefixFinishRepair, CacheKeys.DashboardStats);
    }
}

// ─────────────────────────────────────────────────
// 8. Order consumer (legacy — no idempotency logic needed, base requires param)
// ─────────────────────────────────────────────────
public class OrderConsumer : RabbitMQConsumerBase
{
    public OrderConsumer(
        IConfiguration config,
        ILogger<OrderConsumer> logger,
        IdempotencyHelper idempotency)
        : base(config, logger, MessageQueueHelper.Queues.Order, idempotency) { }

    protected override Task HandleMessageAsync(string message, CancellationToken ct)
    {
        Logger.LogInformation("✅ Order received: {Message}", message);
        return Task.CompletedTask;
    }
}