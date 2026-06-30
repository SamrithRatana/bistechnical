using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;
using ServiceMaintenance.Models;
using ServiceMaintenance.Services;
using System.Collections.Concurrent;
using System.Security.Claims;
using ServiceMaintenance.Services.JWT;
namespace ServiceMaintenance.Services.RealTimeServices // Use your namespace
{
    public class ChatHub : Hub
    {
        private static readonly ConcurrentDictionary<string, UserConnectionInfo> OnlineUsers = new();
        private static readonly ConcurrentDictionary<string, DateTime> LastSeenTimes = new();

        public class UserConnectionInfo
        {
            public string UserId { get; set; } = "";
            public string ConnectionId { get; set; } = "";
            public DateTime ConnectedAt { get; set; }
            public DateTime LastActivity { get; set; }
        }

        private readonly IMessageService _messageService;
        private readonly ILogger<ChatHub> _logger;
        private readonly GlobalUserService _userCacheService;

        public ChatHub(
            IMessageService messageService,
            ILogger<ChatHub> logger,
            GlobalUserService userCacheService)
        {
            _messageService = messageService;
            _logger = logger;
            _userCacheService = userCacheService;
        }

        private string GetUserId()
        {
            try
            {
                // ✅ Check if user is authenticated
                if (Context.User?.Identity?.IsAuthenticated != true)
                {
                    _logger.LogError("❌ [ChatHub] User is NOT authenticated");
                    _logger.LogError($"   ConnectionId: {Context.ConnectionId}");
                    _logger.LogError($"   User Identity: {Context.User?.Identity?.Name ?? "NULL"}");
                    _logger.LogError($"   Auth Type: {Context.User?.Identity?.AuthenticationType ?? "NULL"}");

                    // Log all claims for debugging
                    if (Context.User?.Claims != null)
                    {
                        _logger.LogError("   Available claims:");
                        foreach (var claim in Context.User.Claims)
                        {
                            _logger.LogError($"     - {claim.Type} = {claim.Value}");
                        }
                    }

                    throw new HubException("User is not authenticated. Please log in again.");
                }

                // ✅ Try multiple strategies to get user ID
                var strategies = new[]
                {
                ClaimTypes.NameIdentifier,
                "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier",
                "sub",
                "nameid",
                "userId",
                "uid"
            };

                foreach (var claimType in strategies)
                {
                    var userId = Context.User.FindFirst(claimType)?.Value;
                    if (!string.IsNullOrEmpty(userId))
                    {
                        _logger.LogInformation($"✅ [ChatHub] Found User ID in claim '{claimType}': {userId}");
                        return userId;
                    }
                }

                // ✅ Last resort: try Context.UserIdentifier
                if (!string.IsNullOrEmpty(Context.UserIdentifier))
                {
                    _logger.LogInformation($"✅ [ChatHub] Found User ID from Context.UserIdentifier: {Context.UserIdentifier}");
                    return Context.UserIdentifier;
                }

                // ✅ Log all available claims for debugging
                _logger.LogError("❌ [ChatHub] User ID NOT FOUND in any claim. Available claims:");
                foreach (var claim in Context.User.Claims)
                {
                    _logger.LogError($"   - Type: '{claim.Type}', Value: '{claim.Value}'");
                }

                throw new HubException("User ID not found in JWT claims. Please log in again.");
            }
            catch (HubException)
            {
                throw; // Re-throw HubException as-is
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [ChatHub] Error extracting user ID");
                throw new HubException($"Failed to get user ID: {ex.Message}");
            }
        }

        public override async Task OnConnectedAsync()
        {
            try
            {
                var userId = GetUserId();
                _logger.LogInformation($"✅ [ChatHub] User {userId} connecting from {Context.ConnectionId}");

                if (!string.IsNullOrEmpty(userId))
                {
                    var connectionInfo = new UserConnectionInfo
                    {
                        UserId = userId,
                        ConnectionId = Context.ConnectionId,
                        ConnectedAt = DateTime.UtcNow,
                        LastActivity = DateTime.UtcNow
                    };

                    OnlineUsers.AddOrUpdate(userId, connectionInfo, (key, oldValue) => connectionInfo);
                    LastSeenTimes.TryRemove(userId, out _);

                    await Clients.All.SendAsync("UserConnected", userId);

                    // Send persisted unread counts to the connecting user
                    try
                    {
                        var unread = await _messageService.GetUnreadCountsAsync(userId);
                        await Clients.Caller.SendAsync("InitialUnreadCounts", unread);
                        _logger.LogInformation($"✅ [ChatHub] Sent initial unread counts to {userId}: {unread?.Count ?? 0} conversations");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[ChatHub] GetUnreadCountsAsync failed for user {UserId}", userId);
                    }

                    _logger.LogInformation($"✅ [ChatHub] User {userId} connected successfully at {DateTime.UtcNow}");
                }
                else
                {
                    _logger.LogWarning($"⚠️ [ChatHub] Connection established but no user ID available. ConnectionId: {Context.ConnectionId}");
                }

                await base.OnConnectedAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [ChatHub] OnConnectedAsync failed");
                throw;
            }
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            try
            {
                var userId = GetUserId();

                if (exception != null)
                {
                    _logger.LogWarning($"❌ [ChatHub] User {userId} disconnecting with error: {exception.Message}");
                }
                else
                {
                    _logger.LogInformation($"❌ [ChatHub] User {userId} disconnecting normally");
                }

                if (!string.IsNullOrEmpty(userId))
                {
                    var disconnectedAt = DateTime.UtcNow;
                    OnlineUsers.TryRemove(userId, out _);
                    LastSeenTimes.AddOrUpdate(userId, disconnectedAt, (key, oldValue) => disconnectedAt);

                    await Clients.All.SendAsync("UserDisconnected", userId, disconnectedAt);

                    _logger.LogInformation($"❌ [ChatHub] User {userId} disconnected at {disconnectedAt}");
                }

                await base.OnDisconnectedAsync(exception);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [ChatHub] OnDisconnectedAsync error");
            }
        }

        public async Task SendMessage(
            string userName,
            string messageText,
            string userId,
            string recipientId,
            string timestamp,
            string fileUrl = null,
            string audioUrl = null,
            int? replyToMessageId = null,
            string replyToUserName = null,
            string replyToText = null)
        {
            try
            {
                _logger.LogInformation($"📨 [ChatHub] Message from {userName} ({userId}) to {recipientId}");
                _logger.LogDebug($"   Message: {messageText?.Substring(0, Math.Min(50, messageText?.Length ?? 0))}...");

                await UpdateUserActivity();

                var message = new Message
                {
                    UserName = userName,
                    Text = messageText,
                    When = DateTime.Parse(timestamp),
                    UserID = userId,
                    RecipientID = recipientId,
                    FileUrl = fileUrl,
                    AudioURL = audioUrl,
                    IsRead = false,
                    ReplyToMessageId = replyToMessageId,
                    ReplyToUserName = replyToUserName,
                    ReplyToText = replyToText
                };

                await _messageService.SaveMessageAsync(message);

                // Send message to all clients
                await Clients.All.SendAsync("ReceiveMessage", new object[]
                {
                userName,
                messageText,
                timestamp,
                fileUrl ?? string.Empty,
                audioUrl ?? string.Empty,
                message.Id,
                userId,
                recipientId,
                replyToMessageId,
                replyToUserName ?? string.Empty,
                replyToText ?? string.Empty
                });

                // Update last message preview
                await Clients.All.SendAsync("UpdateLastMessage", userId, recipientId, messageText, timestamp);

                // Update unread counts for the recipient
                try
                {
                    var newCount = await _messageService.GetUnreadCountAsync(recipientId, userId);
                    var allUnreadCounts = await _messageService.GetUnreadCountsAsync(recipientId);

                    await Clients.User(recipientId).SendAsync("UnreadCountUpdated", userId, newCount);
                    await Clients.User(recipientId).SendAsync("InitialUnreadCounts", allUnreadCounts);

                    _logger.LogInformation($"✅ [ChatHub] Sent unread counts to {recipientId}: {newCount} from {userId}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ChatHub] Failed to fetch unread count after SaveMessage");
                }

                _logger.LogInformation($"✅ [ChatHub] Message saved successfully. ID: {message.Id}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [ChatHub] Error in SendMessage");
                throw;
            }
        }

        public async Task MarkMessagesRead(string recipientId, string senderId)
        {
            try
            {
                _logger.LogInformation($"📖 [ChatHub] Marking messages as read: recipient={recipientId}, sender={senderId}");
                await UpdateUserActivity();

                var updated = await _messageService.MarkMessagesReadAsync(recipientId, senderId);

                if (updated > 0)
                {
                    await Clients.Caller.SendAsync("MessagesMarkedRead", senderId);
                    await Clients.User(senderId).SendAsync("MessagesReadByRecipient", recipientId, senderId);

                    var unread = await _messageService.GetUnreadCountsAsync(recipientId);
                    await Clients.Caller.SendAsync("InitialUnreadCounts", unread);

                    _logger.LogInformation($"✅ [ChatHub] Marked {updated} messages as read");
                }
                else
                {
                    _logger.LogInformation($"ℹ️ [ChatHub] No messages to mark as read");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [ChatHub] Error in MarkMessagesRead");
                throw;
            }
        }

        public async Task<Dictionary<string, int>> GetUnreadCounts(string userId)
        {
            try
            {
                _logger.LogInformation($"📊 [ChatHub] Getting unread counts for {userId}");
                await UpdateUserActivity();
                var unread = await _messageService.GetUnreadCountsAsync(userId);
                _logger.LogInformation($"✅ [ChatHub] Found {unread?.Count ?? 0} conversations with unread messages");
                return unread ?? new Dictionary<string, int>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [ChatHub] GetUnreadCounts failed for {UserId}", userId);
                return new Dictionary<string, int>();
            }
        }

        public Task<IEnumerable<string>> GetOnlineUsers()
        {
            var onlineUserIds = OnlineUsers.Keys.AsEnumerable();
            _logger.LogInformation($"👥 [ChatHub] {OnlineUsers.Count} users currently online");
            return Task.FromResult(onlineUserIds);
        }

        public Task<Dictionary<string, DateTime>> GetLastSeenData()
        {
            var lastSeen = LastSeenTimes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            _logger.LogInformation($"🕐 [ChatHub] Returning last seen data for {lastSeen.Count} users");
            return Task.FromResult(lastSeen);
        }

        public Task UpdateUserActivity()
        {
            var userId = GetUserId();

            if (!string.IsNullOrEmpty(userId) && OnlineUsers.TryGetValue(userId, out var connectionInfo))
            {
                connectionInfo.LastActivity = DateTime.UtcNow;
            }

            return Task.CompletedTask;
        }

        public async Task<IEnumerable<Message>> GetChatHistory(string userId, string recipientId)
        {
            try
            {
                _logger.LogInformation($"💬 [ChatHub] Getting chat history: {userId} <-> {recipientId}");
                await UpdateUserActivity();
                var messages = await _messageService.GetMessagesAsync(userId, recipientId);
                _logger.LogInformation($"✅ [ChatHub] Retrieved {messages?.Count() ?? 0} messages");
                return messages;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [ChatHub] Error in GetChatHistory");
                return new List<Message>();
            }
        }

        // ✅ OPTIMIZED: Delete message and notify clients
        public async Task DeleteMessage(int messageId)
        {
            try
            {
                _logger.LogInformation($"🗑️ [ChatHub] Deleting message {messageId}");

                await UpdateUserActivity();

                var message = await _messageService.GetMessageByIdAsync(messageId);
                if (message == null)
                {
                    _logger.LogWarning($"⚠️ [ChatHub] Message {messageId} not found");
                    return;
                }

                var senderId = message.UserID;
                var recipientId = message.RecipientID;

                // ✅ Delete from database first
                await _messageService.DeleteMessageAsync(messageId);
                _logger.LogInformation($"✅ [ChatHub] Message {messageId} deleted from database");

                // ✅ Notify ALL clients to remove the message from their UI
                await Clients.All.SendAsync("MessageDeleted", messageId);
                _logger.LogInformation($"✅ [ChatHub] Broadcast MessageDeleted event to all clients");

                // ✅ Update last message preview for both users
                if (!string.IsNullOrEmpty(senderId) && !string.IsNullOrEmpty(recipientId))
                {
                    var lastSenderMessage = await _messageService.GetLastMessageAsync(senderId, recipientId);
                    var lastRecipientMessage = await _messageService.GetLastMessageAsync(recipientId, senderId);

                    await Clients.All.SendAsync("UpdateLastMessage", senderId, recipientId,
                        lastSenderMessage?.Text ?? "No messages",
                        lastSenderMessage?.When.ToString("o") ?? DateTime.Now.ToString("o"));

                    await Clients.All.SendAsync("UpdateLastMessage", recipientId, senderId,
                        lastRecipientMessage?.Text ?? "No messages",
                        lastRecipientMessage?.When.ToString("o") ?? DateTime.Now.ToString("o"));
                }

                _logger.LogInformation($"✅ [ChatHub] Message {messageId} deleted successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [ChatHub] Error in DeleteMessage");
                throw;
            }
        }

        public Task<Dictionary<string, object>> GetConnectionInfo()
        {
            var connectionInfo = OnlineUsers.ToDictionary(
                kvp => kvp.Key,
                kvp => (object)new
                {
                    ConnectedAt = kvp.Value.ConnectedAt,
                    LastActivity = kvp.Value.LastActivity,
                    ConnectionId = kvp.Value.ConnectionId
                }
            );

            _logger.LogInformation($"🔌 [ChatHub] Returning connection info for {connectionInfo.Count} users");
            return Task.FromResult(connectionInfo);
        }

        public async Task SetUserOffline(string userId)
        {
            if (OnlineUsers.TryRemove(userId, out _))
            {
                var disconnectedAt = DateTime.UtcNow;
                LastSeenTimes.AddOrUpdate(userId, disconnectedAt, (key, oldValue) => disconnectedAt);
                await Clients.All.SendAsync("UserDisconnected", userId, disconnectedAt);
                _logger.LogInformation($"👋 [ChatHub] Set user {userId} offline");
            }
        }

        public static void CleanupInactiveConnections(TimeSpan inactivityThreshold)
        {
            var now = DateTime.UtcNow;
            var inactiveUsers = OnlineUsers
                .Where(kvp => now - kvp.Value.LastActivity > inactivityThreshold)
                .ToList();

            foreach (var inactiveUser in inactiveUsers)
            {
                OnlineUsers.TryRemove(inactiveUser.Key, out _);
                LastSeenTimes.AddOrUpdate(inactiveUser.Key, inactiveUser.Value.LastActivity,
                    (key, oldValue) => inactiveUser.Value.LastActivity);
            }
        }

        public async Task<List<ConversationPreview>> GetAllConversations(string userId)
        {
            try
            {
                _logger.LogInformation($"📋 [ChatHub] GetAllConversations for user: {userId}");
                await UpdateUserActivity();

                var allMessages = await _messageService.GetAllUserMessagesAsync(userId);

                if (allMessages == null || !allMessages.Any())
                {
                    _logger.LogInformation($"ℹ️ [ChatHub] No messages found for user {userId}");
                    return new List<ConversationPreview>();
                }

                var conversations = allMessages
                    .GroupBy(m => m.UserID == userId ? m.RecipientID : m.UserID)
                    .Select(g => new
                    {
                        OtherUserId = g.Key,
                        LastMessage = g.OrderByDescending(m => m.When).FirstOrDefault()
                    })
                    .Where(c => !string.IsNullOrEmpty(c.OtherUserId))
                    .ToList();

                _logger.LogInformation($"✅ [ChatHub] Found {conversations.Count} conversations");

                var userCache = await _userCacheService.GetUsersAsync();
                var result = new List<ConversationPreview>();

                foreach (var conv in conversations)
                {
                    string userName = "Unknown User";

                    if (userCache != null && userCache.TryGetValue(conv.OtherUserId, out var cachedUserName))
                    {
                        userName = cachedUserName;
                    }

                    result.Add(new ConversationPreview
                    {
                        OtherUserId = conv.OtherUserId,
                        OtherUserName = userName,
                        LastMessageText = conv.LastMessage?.Text ?? "",
                        LastMessageTime = conv.LastMessage?.When ?? DateTime.UtcNow,
                        UnreadCount = 0
                    });

                    _logger.LogDebug($"   👤 Conversation with: {userName} ({conv.OtherUserId})");
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [ChatHub] Error in GetAllConversations");
                return new List<ConversationPreview>();
            }
        }

        public async Task NotifyItemDeleted(Guid serviceId)
        {
            try
            {
                _logger.LogInformation($"🗑️ [ChatHub] Broadcasting item deletion: {serviceId}");
                await Clients.All.SendAsync("BroadcastItemDelete", serviceId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [ChatHub] Error in NotifyItemDeleted");
            }
        }

        public async Task NotifyItemUpdated(string message)
        {
            try
            {
                _logger.LogInformation($"🔄 [ChatHub] Broadcasting item update: {message}");
                await Clients.All.SendAsync("ReceiveItemUpdate", message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [ChatHub] Error in NotifyItemUpdated");
            }
        }

        public class ConversationPreview
        {
            public string OtherUserId { get; set; }
            public string OtherUserName { get; set; }
            public string LastMessageText { get; set; }
            public DateTime LastMessageTime { get; set; }
            public int UnreadCount { get; set; }
        }
    }
}