using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;

namespace ServiceMaintenance.Services.RealTimeServices
{
    [AllowAnonymous] // Change to [Authorize] if needed
    public class ItemHub : Hub
    {
        private readonly ILogger<ItemHub> _logger;

        public ItemHub(ILogger<ItemHub> logger)
        {
            _logger = logger;
        }

        public override async Task OnConnectedAsync()
        {
            _logger.LogInformation($"✅ [ItemHub] Client connected: {Context.ConnectionId}");
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            _logger.LogInformation($"❌ [ItemHub] Client disconnected: {Context.ConnectionId}. Exception: {exception?.Message}");
            await base.OnDisconnectedAsync(exception);
        }

        public async Task BroadcastItemUpdate(string message)
        {
            _logger.LogInformation($"📢 [ItemHub] Broadcasting item update: {message}");
            await Clients.All.SendAsync("ReceiveItemUpdate", message);
        }

        public async Task BroadcastItemDelete(Guid itemId)
        {
            _logger.LogInformation($"🗑️ [ItemHub] Broadcasting item delete: {itemId}");
            await Clients.All.SendAsync("BroadcastItemDelete", itemId);
        }
    }
}