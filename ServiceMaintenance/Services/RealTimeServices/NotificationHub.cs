using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;
using ServiceMaintenance.Services;
using ServiceMaintenance.Models;
using System.Threading.Tasks;
using ServiceMaintenance.Services.JWT;

namespace ServiceMaintenance.Services.RealTimeServices
{
    [AllowAnonymous] // Change to [Authorize] if you need authentication
    public class NotificationHub : Hub
    {
        private readonly GlobalArticleCacheService _articleCache;
        private readonly ILogger<NotificationHub> _logger;

        // ✅ No DbContext needed - using JWT API services instead
        public NotificationHub(
            GlobalArticleCacheService articleCache,
            ILogger<NotificationHub> logger)
        {
            _articleCache = articleCache;
            _logger = logger;
        }

        public override async Task OnConnectedAsync()
        {
            _logger.LogInformation($"✅ Client connected: {Context.ConnectionId}");
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            _logger.LogInformation($"❌ Client disconnected: {Context.ConnectionId}. Exception: {exception?.Message}");
            await base.OnDisconnectedAsync(exception);
        }

        /// <summary>
        /// Send notification to all connected clients
        /// Uses GlobalArticleCacheService which calls JWT API
        /// </summary>
        public async Task SendNotification(string heading, string content, string username, byte[] profilePicture)
        {
            try
            {
                _logger.LogInformation($"📨 SendNotification called by {username}: {heading}");

                var base64ProfilePicture = profilePicture != null ? Convert.ToBase64String(profilePicture) : null;

                var notification = new Article
                {
                    ArticleHeading = heading,
                    ArticleContent = content,
                    Username = username,  // ✅ This should be the UserName (not display name)
                    ProfilePicture = base64ProfilePicture,
                    Timestamp = DateTime.UtcNow,
                    IsRead = false
                };

                // ✅ Save to JWT API via GlobalArticleCacheService
                var success = await _articleCache.AddArticleAsync(notification);

                if (success)
                {
                    _logger.LogInformation($"💾 Notification saved to JWT API via cache service");
                    _logger.LogInformation($"   Creator: {username}");
                    _logger.LogInformation($"   Heading: {heading}");

                    // ✅ Broadcast to all connected clients
                    // Each client will filter based on their own username
                    await Clients.All.SendAsync("sendToUser", heading, content, username, base64ProfilePicture);

                    _logger.LogInformation($"📤 Notification broadcasted to all clients");
                }
                else
                {
                    _logger.LogError("❌ Failed to save notification to JWT API");
                    throw new Exception("Failed to save notification");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error in SendNotification: {ex.Message}");
                _logger.LogError($"Stack trace: {ex.StackTrace}");
                throw;
            }
        }
        /// <summary>
        /// Send notification to specific user
        /// </summary>
        public async Task SendNotificationToUser(string userId, string heading, string content, string username, byte[] profilePicture)
        {
            try
            {
                _logger.LogInformation($"📨 SendNotificationToUser called for user {userId}");

                var base64ProfilePicture = profilePicture != null ? Convert.ToBase64String(profilePicture) : null;

                var notification = new Article
                {
                    ArticleHeading = heading,
                    ArticleContent = content,
                    Username = username,
                    ProfilePicture = base64ProfilePicture,
                    Timestamp = DateTime.UtcNow,
                    IsRead = false
                };

                // ✅ Save to JWT API via GlobalArticleCacheService
                var success = await _articleCache.AddArticleAsync(notification);

                if (success)
                {
                    _logger.LogInformation($"💾 Notification saved to JWT API");

                    // Send to specific user
                    await Clients.User(userId).SendAsync("sendToUser", heading, content, username, base64ProfilePicture);

                    _logger.LogInformation($"📤 Notification sent to user {userId}");
                }
                else
                {
                    _logger.LogError($"❌ Failed to save notification for user {userId}");
                    throw new Exception("Failed to save notification");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error in SendNotificationToUser: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Mark notification as read
        /// </summary>
        public async Task MarkNotificationAsRead(int articleId)
        {
            try
            {
                _logger.LogInformation($"✅ Marking notification {articleId} as read");

                var success = await _articleCache.MarkAsReadAsync(articleId);

                if (success)
                {
                    // Notify all clients about the update
                    await Clients.All.SendAsync("notificationRead", articleId);
                    _logger.LogInformation($"✅ Notification {articleId} marked as read");
                }
                else
                {
                    _logger.LogError($"❌ Failed to mark notification {articleId} as read");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error marking notification as read: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Delete notification
        /// </summary>
        public async Task DeleteNotification(int articleId)
        {
            try
            {
                _logger.LogInformation($"🗑️ Deleting notification {articleId}");

                var success = await _articleCache.DeleteArticleAsync(articleId);

                if (success)
                {
                    // Notify all clients about the deletion
                    await Clients.All.SendAsync("notificationDeleted", articleId);
                    _logger.LogInformation($"✅ Notification {articleId} deleted");
                }
                else
                {
                    _logger.LogError($"❌ Failed to delete notification {articleId}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error deleting notification: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Get unread notification count
        /// </summary>
        public async Task<int> GetUnreadCount()
        {
            try
            {
                var count = await _articleCache.GetUnreadCountAsync();
                _logger.LogInformation($"📊 Unread count: {count}");
                return count;
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error getting unread count: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Get connection info for debugging
        /// </summary>
        public string GetConnectionId()
        {
            return Context.ConnectionId;
        }
    }
}