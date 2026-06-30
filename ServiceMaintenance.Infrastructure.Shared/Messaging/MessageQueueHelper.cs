using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ServiceMaintenance.Infrastructure.Shared.Messaging;

// ─────────────────────────────────────────────────
// Queue message model
// ─────────────────────────────────────────────────
public class QueueMessage
{
    public Guid MessageId { get; set; } = Guid.NewGuid();   // ✅ NEW — Unique per publish, used for Idempotency
    public string Action { get; set; } = string.Empty;
    public string Queue { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public string? ReportNo { get; set; }
    public string? Status { get; set; }
    public Guid? UserId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public Dictionary<string, object>? Metadata { get; set; }
}

// ─────────────────────────────────────────────────
// Queue action constants
// ─────────────────────────────────────────────────
public static class QueueAction
{
    public const string Create = nameof(Create);
    public const string Update = nameof(Update);
    public const string Delete = nameof(Delete);
    public const string StatusChange = nameof(StatusChange);
}

// ─────────────────────────────────────────────────
// MessageQueueHelper — high-level publish helpers
// ─────────────────────────────────────────────────
public class MessageQueueHelper
{
    private readonly IRabbitMQService _mq;
    private readonly ILogger<MessageQueueHelper> _logger;

    public MessageQueueHelper(IRabbitMQService mq, ILogger<MessageQueueHelper> logger)
    {
        _mq = mq;
        _logger = logger;
    }

    public static class Queues
    {
        public const string ReceiveItem = "receive-item";
        public const string Order = "order";
        public const string AwaitCustomer = "await-customer";
        public const string AwaitSparePart = "await-spare-part";
        public const string InspectItem = "inspect-item";
        public const string InspectionItem = "inspection-item";
        public const string RepairItem = "repair-item";
        public const string FinishItem = "finish-item";
        public const string SparePart = "spare-part";      // NEW
        public const string ItemModule = "item-module";
    }

    public async Task PublishAsync(
        string queue,
        QueueMessage message,
        CancellationToken ct = default)
    {
        try
        {
            message.Queue = queue;
            var json = JsonSerializer.Serialize(message);
            await _mq.PublishAsync(queue, json);
            _logger.LogInformation(
                "📤 [{Queue}] {Action} ReportNo={ReportNo} EntityId={EntityId} MessageId={MessageId}",
                queue, message.Action, message.ReportNo, message.EntityId, message.MessageId);
        }
        catch (Exception ex)
        {
            _logger.LogError("❌ Failed to publish to [{Queue}]: {Message}", queue, ex.Message);
            throw;
        }
    }

    public Task PublishCreateAsync(
        string queue,
        Guid entityId,
        string? reportNo = null,
        Guid? userId = null,
        Dictionary<string, object>? metadata = null,
        CancellationToken ct = default)
        => PublishAsync(queue, new QueueMessage
        {
            Action = QueueAction.Create,
            EntityId = entityId,
            ReportNo = reportNo,
            UserId = userId,
            Metadata = metadata,
        }, ct);

    public Task PublishUpdateAsync(
        string queue,
        Guid entityId,
        string? reportNo = null,
        Guid? userId = null,
        Dictionary<string, object>? metadata = null,
        CancellationToken ct = default)
        => PublishAsync(queue, new QueueMessage
        {
            Action = QueueAction.Update,
            EntityId = entityId,
            ReportNo = reportNo,
            UserId = userId,
            Metadata = metadata,
        }, ct);

    public Task PublishDeleteAsync(
        string queue,
        Guid entityId,
        string? reportNo = null,
        Guid? userId = null,
        CancellationToken ct = default)
        => PublishAsync(queue, new QueueMessage
        {
            Action = QueueAction.Delete,
            EntityId = entityId,
            ReportNo = reportNo,
            UserId = userId,
        }, ct);

    public Task PublishStatusChangeAsync(
        string queue,
        string context,
        Guid entityId,
        string newStatus,
        string? reportNo = null,
        Guid? userId = null,
        CancellationToken ct = default)
        => PublishAsync(queue, new QueueMessage
        {
            Action = QueueAction.StatusChange,
            EntityId = entityId,
            ReportNo = reportNo,
            Status = newStatus,
            UserId = userId,
            Metadata = new Dictionary<string, object>
            {
                ["context"] = context,
            },
        }, ct);
}