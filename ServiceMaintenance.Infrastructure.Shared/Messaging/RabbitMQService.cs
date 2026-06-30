using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using System.Text;

namespace ServiceMaintenance.Infrastructure.Shared.Messaging;

public interface IRabbitMQService
{
    Task PublishAsync(string queueName, string message);
    Task<bool> IsHealthyAsync();
}

/// <summary>
/// Singleton RabbitMQ publisher with:
/// - Lazy connection (created on first publish, not at startup)
/// - Auto-reconnect on failure
/// - Per-publish channel (lightweight in RabbitMQ v7)
/// - Retry up to 2 times before giving up
/// - Health check support
/// </summary>
public class RabbitMQService : IRabbitMQService, IAsyncDisposable
{
    private readonly IConfiguration _config;
    private readonly ILogger<RabbitMQService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private IConnection? _connection;
    private ConnectionFactory? _factory;
    private bool _disposed;

    public RabbitMQService(IConfiguration config, ILogger<RabbitMQService> logger)
    {
        _config = config;
        _logger = logger;
    }

    // ✅ Lazy factory — built once, reused
    private ConnectionFactory GetFactory() => _factory ??= new ConnectionFactory
    {
        HostName = _config["RabbitMQ:Host"] ?? "localhost",
        Port = int.Parse(_config["RabbitMQ:Port"] ?? "5672"),
        UserName = _config["RabbitMQ:Username"] ?? "guest",
        Password = _config["RabbitMQ:Password"] ?? "guest",
        AutomaticRecoveryEnabled = true,
        NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
        RequestedHeartbeat = TimeSpan.FromSeconds(30),
    };

    // ✅ Singleton connection — thread-safe, lazy, re-created if dropped
    private async Task<IConnection> GetConnectionAsync(CancellationToken ct = default)
    {
        if (_connection?.IsOpen == true)
            return _connection;

        await _lock.WaitAsync(ct);
        try
        {
            if (_connection?.IsOpen == true)
                return _connection;

            if (_connection is not null)
            {
                try { await _connection.DisposeAsync(); } catch { /* ignore */ }
                _connection = null;
            }

            _logger.LogInformation("📡 Creating new RabbitMQ connection...");
            _connection = await GetFactory().CreateConnectionAsync(ct);
            _logger.LogInformation("✅ RabbitMQ connected to {Host}:{Port}",
                GetFactory().HostName, GetFactory().Port);
            return _connection;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ✅ Publish — reuse singleton connection, new lightweight channel per call
    public async Task PublishAsync(string queueName, string message)
    {
        if (_disposed)
        {
            _logger.LogWarning("⚠️ Publish skipped — service is disposed");
            return;
        }

        const int maxRetries = 2;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                var connection = await GetConnectionAsync();

                await using var channel = await connection.CreateChannelAsync();

                await channel.QueueDeclareAsync(
                    queue: queueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null);

                var body = Encoding.UTF8.GetBytes(message);

                var props = new BasicProperties
                {
                    DeliveryMode = DeliveryModes.Persistent,   // survive broker restart
                    ContentType = "application/json",
                    Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                };

                await channel.BasicPublishAsync(
                    exchange: string.Empty,
                    routingKey: queueName,
                    mandatory: false,
                    basicProperties: props,
                    body: body);

                _logger.LogDebug("📨 Published to [{Queue}] ({Bytes}b)", queueName, body.Length);
                return; // ✅ success
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                _logger.LogWarning(
                    "⚠️ Publish attempt {Attempt} failed [{Queue}]: {Msg} — retrying...",
                    attempt + 1, queueName, ex.Message);

                _connection = null; // force reconnect
                await Task.Delay(TimeSpan.FromMilliseconds(300 * (attempt + 1)));
            }
            catch (Exception ex)
            {
                // Final failure — log but never throw (RabbitMQ must not break main flow)
                _logger.LogError(
                    "❌ Publish permanently failed [{Queue}]: {Msg}",
                    queueName, ex.Message);
            }
        }
    }

    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var conn = await GetConnectionAsync(cts.Token);
            return conn.IsOpen;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _lock.Dispose();

        if (_connection is not null)
        {
            try { await _connection.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning("⚠️ Dispose error: {Msg}", ex.Message); }
        }
    }
}