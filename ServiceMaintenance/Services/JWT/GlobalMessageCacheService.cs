using ServiceMaintenance.Models;
using Microsoft.Extensions.Logging;

namespace ServiceMaintenance.Services.JWT
{
    public class GlobalMessageCacheService
    {
        private readonly JwtMessageService _messageService;
        private readonly UserService _userService;
        private readonly ILogger<GlobalMessageCacheService> _logger;

        private List<UserMessagePreview> _cachedMessages = new();
        private Dictionary<string, int> _cachedUnreadCounts = new(StringComparer.OrdinalIgnoreCase);
        private int _cachedTotalUnread = 0;
        private DateTime _lastRefresh = DateTime.MinValue;
        private readonly TimeSpan _cacheExpiration = TimeSpan.FromSeconds(30);
        private readonly SemaphoreSlim _refreshLock = new(1, 1);

        public event Func<List<UserMessagePreview>, int, Task> OnMessagesUpdated;
        public event Func<string, int, Task> OnUnreadCountChanged;
        public event Func<string, UserMessagePreview, Task> OnNewConversation;

        public GlobalMessageCacheService(
            JwtMessageService messageService,
            UserService userService,
            ILogger<GlobalMessageCacheService> logger)
        {
            _messageService = messageService;
            _userService = userService;
            _logger = logger;
        }

        public async Task<(List<UserMessagePreview> Messages, int TotalUnread)> GetMessagesAsync(
            string currentUserId,
            bool forceRefresh = false)
        {
            if (string.IsNullOrEmpty(currentUserId))
                return (new List<UserMessagePreview>(), 0);

            bool isCacheValid = !forceRefresh &&
                               (DateTime.UtcNow - _lastRefresh) < _cacheExpiration &&
                               _cachedMessages.Any();

            if (isCacheValid)
                return (_cachedMessages, _cachedTotalUnread);

            await _refreshLock.WaitAsync();
            try
            {
                if (!forceRefresh &&
                    (DateTime.UtcNow - _lastRefresh) < _cacheExpiration &&
                    _cachedMessages.Any())
                {
                    return (_cachedMessages, _cachedTotalUnread);
                }

                _logger.LogDebug("Refreshing message cache for user {UserId}", currentUserId);

                var unreadCountsResponse = await _messageService.GetUnreadCountsAsync(currentUserId);

                if (unreadCountsResponse?.Status == "Success" && unreadCountsResponse.Data != null)
                {
                    _cachedUnreadCounts = new Dictionary<string, int>(
                        unreadCountsResponse.Data,
                        StringComparer.OrdinalIgnoreCase
                    );
                    _cachedTotalUnread = _cachedUnreadCounts.Values.Sum();
                }
                else
                {
                    _cachedUnreadCounts.Clear();
                    _cachedTotalUnread = 0;
                }

                var conversationsResponse = await _messageService.GetAllConversationsAsync(currentUserId);
                var newMessages = new List<UserMessagePreview>();

                if (conversationsResponse?.Status == "Success" &&
                    conversationsResponse.Data != null &&
                    conversationsResponse.Data.Any())
                {
                    foreach (var conversation in conversationsResponse.Data)
                    {
                        try
                        {
                            var otherUser = await _userService.GetApplicationUserAsync(conversation.OtherUserId);

                            if (otherUser == null)
                                continue;

                            int unreadCount = _cachedUnreadCounts.GetValueOrDefault(conversation.OtherUserId, 0);

                            var userMsg = new UserMessagePreview
                            {
                                UserId = otherUser.Id,
                                UserName = otherUser.UserName ?? "Unknown User",
                                ProfilePicture = "/images/default-profile.png",
                                LastMessageText = GetMessagePreviewText(
                                    conversation.LastMessageText,
                                    conversation.LastMessageFileUrl,
                                    conversation.LastMessageAudioUrl
                                ),
                                LastMessageTime = conversation.LastMessageTime,
                                UnreadCount = unreadCount,
                                IsOnline = false
                            };

                            newMessages.Add(userMsg);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error processing conversation for user {UserId}", conversation.OtherUserId);
                        }
                    }

                    newMessages = newMessages
                        .OrderByDescending(u => u.UnreadCount)
                        .ThenByDescending(u => u.LastMessageTime)
                        .ToList();
                }
                else if (_cachedUnreadCounts.Any())
                {
                    foreach (var kvp in _cachedUnreadCounts.Where(x => x.Value > 0))
                    {
                        try
                        {
                            var otherUser = await _userService.GetApplicationUserAsync(kvp.Key);

                            if (otherUser != null)
                            {
                                newMessages.Add(new UserMessagePreview
                                {
                                    UserId = otherUser.Id,
                                    UserName = otherUser.UserName ?? "Unknown User",
                                    ProfilePicture = "/images/default-profile.png",
                                    LastMessageText = "New message",
                                    LastMessageTime = DateTime.UtcNow,
                                    UnreadCount = kvp.Value,
                                    IsOnline = false
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error loading user {UserId}", kvp.Key);
                        }
                    }

                    newMessages = newMessages.OrderByDescending(u => u.UnreadCount).ToList();
                }

                _cachedMessages = newMessages;
                _lastRefresh = DateTime.UtcNow;

                _logger.LogInformation("Message cache refreshed: {Count} conversations, {Unread} unread",
                    _cachedMessages.Count, _cachedTotalUnread);

                await NotifyMessagesUpdated();

                return (_cachedMessages, _cachedTotalUnread);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing message cache");
                return (_cachedMessages, _cachedTotalUnread);
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        public async Task UpdateLastMessageAsync(
            string currentUserId,
            string senderId,
            string senderName,
            string messageText,
            DateTime timestamp,
            string fileUrl = null,
            string audioUrl = null)
        {
            try
            {
                var userMsg = _cachedMessages.FirstOrDefault(u => u.UserId == senderId);

                if (userMsg != null)
                {
                    userMsg.LastMessageText = GetMessagePreviewText(messageText, fileUrl, audioUrl);
                    userMsg.LastMessageTime = timestamp;

                    if (senderId != currentUserId)
                    {
                        userMsg.UnreadCount++;
                        _cachedTotalUnread++;

                        if (_cachedUnreadCounts.ContainsKey(senderId))
                            _cachedUnreadCounts[senderId]++;
                        else
                            _cachedUnreadCounts[senderId] = 1;
                    }

                    _cachedMessages.Remove(userMsg);
                    _cachedMessages.Insert(0, userMsg);
                }
                else if (senderId != currentUserId)
                {
                    var otherUser = await _userService.GetApplicationUserAsync(senderId);

                    if (otherUser != null)
                    {
                        var newUserMsg = new UserMessagePreview
                        {
                            UserId = otherUser.Id,
                            UserName = otherUser.UserName,
                            ProfilePicture = "/images/default-profile.png",
                            LastMessageText = GetMessagePreviewText(messageText, fileUrl, audioUrl),
                            LastMessageTime = timestamp,
                            UnreadCount = 1,
                            IsOnline = false
                        };

                        _cachedMessages.Insert(0, newUserMsg);
                        _cachedTotalUnread++;
                        _cachedUnreadCounts[senderId] = 1;

                        await NotifyNewConversation(senderId, newUserMsg);
                    }
                }

                await NotifyMessagesUpdated();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating last message");
            }
        }

        public async Task UpdateUnreadCountsAsync(Dictionary<string, int> counts)
        {
            if (counts == null || !counts.Any()) return;

            _cachedUnreadCounts = new Dictionary<string, int>(counts, StringComparer.OrdinalIgnoreCase);

            foreach (var userMsg in _cachedMessages)
            {
                if (counts.TryGetValue(userMsg.UserId, out int count))
                    userMsg.UnreadCount = count;
                else if (userMsg.UnreadCount > 0)
                    userMsg.UnreadCount = 0;
            }

            _cachedTotalUnread = _cachedMessages.Sum(u => u.UnreadCount);

            await NotifyMessagesUpdated();
        }

        public async Task UpdateUserUnreadCountAsync(string userId, int count)
        {
            var userMsg = _cachedMessages.FirstOrDefault(u =>
                u.UserId.Equals(userId, StringComparison.OrdinalIgnoreCase));

            if (userMsg != null && userMsg.UnreadCount != count)
            {
                userMsg.UnreadCount = count;
                _cachedUnreadCounts[userId] = count;
                _cachedTotalUnread = _cachedMessages.Sum(u => u.UnreadCount);

                await NotifyUnreadCountChanged(userId, count);
                await NotifyMessagesUpdated();
            }
        }

        public async Task ClearUnreadForUserAsync(string userId)
        {
            await UpdateUserUnreadCountAsync(userId, 0);
        }

        public async Task RefreshAsync(string currentUserId)
        {
            await GetMessagesAsync(currentUserId, forceRefresh: true);
        }

        private string GetMessagePreviewText(string messageText, string fileUrl, string audioUrl)
        {
            if (!string.IsNullOrEmpty(audioUrl))
                return "Sent a voice message";

            if (!string.IsNullOrEmpty(fileUrl))
            {
                if (IsImageFile(fileUrl)) return "Sent a photo";
                if (IsVideoFile(fileUrl)) return "Sent a video";
                return "Sent an attachment";
            }

            return TruncateMessage(messageText, 50);
        }

        private bool IsImageFile(string fileUrl)
        {
            if (string.IsNullOrEmpty(fileUrl)) return false;
            return fileUrl.Contains("image/", StringComparison.OrdinalIgnoreCase) ||
                   fileUrl.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                   fileUrl.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                   fileUrl.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                   fileUrl.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
                   fileUrl.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsVideoFile(string fileUrl)
        {
            if (string.IsNullOrEmpty(fileUrl)) return false;
            return fileUrl.Contains("video/", StringComparison.OrdinalIgnoreCase) ||
                   fileUrl.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                   fileUrl.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ||
                   fileUrl.EndsWith(".avi", StringComparison.OrdinalIgnoreCase) ||
                   fileUrl.EndsWith(".mov", StringComparison.OrdinalIgnoreCase);
        }

        private string TruncateMessage(string message, int maxLength)
        {
            if (string.IsNullOrEmpty(message)) return "No message";
            var decoded = System.Net.WebUtility.HtmlDecode(message);
            return decoded.Length <= maxLength ? decoded : decoded.Substring(0, maxLength) + "...";
        }

        private async Task NotifyMessagesUpdated()
        {
            if (OnMessagesUpdated != null)
                await OnMessagesUpdated.Invoke(_cachedMessages, _cachedTotalUnread);
        }

        private async Task NotifyUnreadCountChanged(string userId, int count)
        {
            if (OnUnreadCountChanged != null)
                await OnUnreadCountChanged.Invoke(userId, count);
        }

        private async Task NotifyNewConversation(string userId, UserMessagePreview message)
        {
            if (OnNewConversation != null)
                await OnNewConversation.Invoke(userId, message);
        }
    }

    public class UserMessagePreview
    {
        public string UserId { get; set; }
        public string UserName { get; set; }
        public string ProfilePicture { get; set; }
        public string LastMessageText { get; set; }
        public DateTime LastMessageTime { get; set; }
        public int UnreadCount { get; set; }
        public bool IsOnline { get; set; }
    }
}