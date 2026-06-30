#nullable enable
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace ServiceMaintenance.Services.RealTimeServices // Use your namespace
{
    public class SignalRService : IAsyncDisposable
    {
        private HubConnection? _hubConnection;
        private readonly ILogger<SignalRService> _logger;
        private readonly NavigationManager _navigationManager;

        public event Func<string, string, string, string, Task>? OnNotificationReceived;
        public event Func<string, string, string, string, string, int, Task>? OnMessageReceived;

        public bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

        public SignalRService(ILogger<SignalRService> logger, NavigationManager navigationManager)
        {
            _logger = logger;
            _navigationManager = navigationManager;
        }

        public async Task InitializeAsync(string hubUrl)
        {
            if (_hubConnection != null)
            {
                _logger.LogWarning("Hub connection already initialized");
                return;
            }

            try
            {
                _hubConnection = new HubConnectionBuilder()
                    .WithUrl(_navigationManager.ToAbsoluteUri(hubUrl), options =>
                    {
                        options.Transports = HttpTransportType.LongPolling |
                                           HttpTransportType.ServerSentEvents |
                                           HttpTransportType.WebSockets;
                        options.SkipNegotiation = false;
                    })
                    .WithAutomaticReconnect(new[]
                    {
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(2),
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromSeconds(10)
                    })
                    .ConfigureLogging(logging =>
                    {
                        logging.SetMinimumLevel(LogLevel.Information);
                    })
                    .Build();

                // Setup event handlers
                _hubConnection.Closed += OnConnectionClosed;
                _hubConnection.Reconnecting += OnReconnecting;
                _hubConnection.Reconnected += OnReconnected;

                // Register message handlers
                _hubConnection.On<string, string, string, string>("sendToUser",
                    async (heading, content, username, profilePicture) =>
                    {
                        _logger.LogInformation($"📨 Notification received from {username}");
                        if (OnNotificationReceived != null)
                            await OnNotificationReceived.Invoke(heading, content, username, profilePicture);
                    });

                _hubConnection.On<string, string, string, string, string, int>("ReceiveMessage",
                    async (user, message, timestamp, fileUrl, audioUrl, messageId) =>
                    {
                        _logger.LogInformation($"💬 Message received from {user}");
                        if (OnMessageReceived != null)
                            await OnMessageReceived.Invoke(user, message, timestamp, fileUrl, audioUrl, messageId);
                    });

                await _hubConnection.StartAsync();
                _logger.LogInformation($"✅ Connected to {hubUrl}. Connection ID: {_hubConnection.ConnectionId}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Failed to connect to {hubUrl}: {ex.Message}");
                throw;
            }
        }

        private async Task OnConnectionClosed(Exception? error)
        {
            _logger.LogWarning($"❌ Connection closed: {error?.Message}");
            await Task.Delay(Random.Shared.Next(0, 5) * 1000);

            try
            {
                if (_hubConnection != null)
                    await _hubConnection.StartAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to reconnect: {ex.Message}");
            }
        }

        private Task OnReconnecting(Exception? error)
        {
            _logger.LogWarning($"🔄 Reconnecting: {error?.Message}");
            return Task.CompletedTask;
        }

        private Task OnReconnected(string? connectionId)
        {
            _logger.LogInformation($"✅ Reconnected. Connection ID: {connectionId}");
            return Task.CompletedTask;
        }

        public async Task SendNotificationAsync(string heading, string content, string username, byte[] profilePicture)
        {
            if (_hubConnection?.State != HubConnectionState.Connected)
            {
                _logger.LogWarning("Cannot send notification - not connected");
                return;
            }

            try
            {
                await _hubConnection.SendAsync("SendNotification", heading, content, username, profilePicture);
                _logger.LogInformation($"📤 Notification sent: {heading}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Failed to send notification: {ex.Message}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_hubConnection != null)
            {
                await _hubConnection.DisposeAsync();
                _hubConnection = null;
            }
        }
    }
}