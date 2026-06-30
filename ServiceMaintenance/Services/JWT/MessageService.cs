using ServiceMaintenance.Models;
using ServiceMaintenance.Services;
using Microsoft.Extensions.Logging;

namespace ServiceMaintenance.Services.JWT
{
    public class MessageService : IMessageService
    {
        private readonly JwtMessageService _jwtMessageService;
        private readonly JwtSessionService _jwtSessionService;
        private readonly ILogger<MessageService> _logger;

        public MessageService(
            JwtMessageService jwtMessageService,
            JwtSessionService jwtSessionService,
            ILogger<MessageService> logger)
        {
            _jwtMessageService = jwtMessageService;
            _jwtSessionService = jwtSessionService;
            _logger = logger;
        }

        public async Task SaveMessageAsync(Message message)
        {
            try
            {
                _logger.LogInformation($"💾 Saving message from {message.UserName} to {message.RecipientID}");

                var messageDto = new SendMessageDto
                {
                    UserID = message.UserID,
                    RecipientID = message.RecipientID,
                    Text = message.Text,
                    FileUrl = message.FileUrl,
                    AudioUrl = message.AudioURL,
                    UserName = message.UserName,
                    ReplyToMessageId = message.ReplyToMessageId,
                    ReplyToUserName = message.ReplyToUserName,
                    ReplyToText = message.ReplyToText
                };

                var response = await _jwtMessageService.SendMessageAsync(messageDto);

                if (response?.Status != "Success")
                {
                    var errorMsg = response?.Message ?? "Unknown error";
                    _logger.LogError($"❌ Failed to save message: {errorMsg}");

                    if (errorMsg.Contains("Unauthorized") || errorMsg.Contains("401"))
                    {
                        throw new UnauthorizedAccessException("Authentication failed. Please refresh the page and try again.");
                    }

                    throw new Exception($"Failed to save message: {errorMsg}");
                }

                _logger.LogInformation("✅ Message saved successfully");
            }
            catch (UnauthorizedAccessException uaEx)
            {
                _logger.LogError(uaEx, "❌ Unauthorized error saving message");
                throw new Exception("Authentication failed. Please refresh the page and log in again.", uaEx);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error saving message");
                throw new Exception($"Failed to save message: {ex.Message}", ex);
            }
        }

        public async Task<List<Message>> GetMessagesAsync(string userId, string recipientId)
        {
            try
            {
                var response = await _jwtMessageService.GetConversationAsync(userId, recipientId);

                if (response?.Status == "Success" && response.Data != null)
                {
                    return response.Data;
                }

                _logger.LogWarning($"Failed to get messages: {response?.Message}");
                return new List<Message>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting messages");
                return new List<Message>();
            }
        }

        public async Task<IEnumerable<Message>> GetAllUserMessagesAsync(string userId)
        {
            try
            {
                _logger.LogInformation($"📋 Getting all messages for user: {userId}");

                var conversationsResponse = await _jwtMessageService.GetAllConversationsAsync(userId);

                if (conversationsResponse?.Status != "Success" || conversationsResponse.Data == null)
                {
                    _logger.LogWarning($"No conversations found for user {userId}");
                    return new List<Message>();
                }

                var allMessages = new List<Message>();

                foreach (var conversation in conversationsResponse.Data)
                {
                    try
                    {
                        var messages = await GetMessagesAsync(userId, conversation.OtherUserId);
                        if (messages != null && messages.Any())
                        {
                            allMessages.AddRange(messages);
                            _logger.LogInformation($"   ✅ Loaded {messages.Count} messages from {conversation.OtherUserName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to load messages for conversation with {conversation.OtherUserId}");
                    }
                }

                var sortedMessages = allMessages
                    .OrderByDescending(m => m.When)
                    .ToList();

                _logger.LogInformation($"✅ Total messages loaded: {sortedMessages.Count}");

                return sortedMessages;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting all messages for user {userId}");
                return new List<Message>();
            }
        }

        public async Task DeleteMessageAsync(int messageId)
        {
            await _jwtMessageService.DeleteMessageAsync(messageId);
        }

        public async Task<Message> GetLastMessageAsync(string userId, string recipientId)
        {
            try
            {
                var response = await _jwtMessageService.GetLastMessageAsync(userId, recipientId);

                if (response?.Status == "Success" && response.Data != null)
                {
                    return response.Data;
                }

                _logger.LogWarning($"Failed to get last message: {response?.Message}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting last message");
                return null;
            }
        }

        public async Task<Message> GetMessageByIdAsync(int messageId)
        {
            try
            {
                var response = await _jwtMessageService.GetMessageByIdAsync(messageId);

                if (response?.Status == "Success" && response.Data != null)
                {
                    return response.Data;
                }

                _logger.LogWarning($"Failed to get message {messageId}: {response?.Message}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting message {messageId}");
                return null;
            }
        }

        public async Task<Dictionary<string, int>> GetUnreadCountsAsync(string recipientId)
        {
            try
            {
                _logger.LogInformation($"📊 API CALL: Getting unread counts for recipientId: {recipientId}");

                var response = await _jwtMessageService.GetUnreadCountsAsync(recipientId);

                _logger.LogInformation($"📦 API Response Status: {response?.Status}");
                _logger.LogInformation($"📦 API Response Message: {response?.Message}");

                if (response?.Status == "Success" && response.Data != null)
                {
                    _logger.LogInformation($"✅ API returned {response.Data.Count} unread count entries");

                    foreach (var kvp in response.Data)
                    {
                        _logger.LogInformation($"   - SenderId: '{kvp.Key}' → Count: {kvp.Value}");
                    }

                    return new Dictionary<string, int>(response.Data, StringComparer.OrdinalIgnoreCase);
                }

                _logger.LogWarning($"⚠️ Failed to get unread counts: {response?.Message}");
                return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ EXCEPTION in GetUnreadCountsAsync");
                _logger.LogError($"   Message: {ex.Message}");
                _logger.LogError($"   Stack: {ex.StackTrace}");
                return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public async Task<int> GetUnreadCountAsync(string recipientId, string senderId)
        {
            try
            {
                _logger.LogInformation($"📊 Getting unread count: recipientId={recipientId}, senderId={senderId}");

                var response = await _jwtMessageService.GetUnreadCountAsync(recipientId, senderId);

                if (response?.Status == "Success")
                {
                    _logger.LogInformation($"✅ Unread count: {response.Count}");
                    return response.Count;
                }

                _logger.LogWarning($"⚠️ Failed to get unread count: {response?.Message}");
                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error getting unread count");
                return 0;
            }
        }

        public async Task<int> MarkMessagesReadAsync(string recipientId, string senderId)
        {
            try
            {
                _logger.LogInformation($"✉️ Marking messages as read: recipientId={recipientId}, senderId={senderId}");

                var response = await _jwtMessageService.MarkMessagesAsReadAsync(recipientId, senderId);

                if (response?.Status == "Success")
                {
                    _logger.LogInformation($"✅ Marked {response.MarkedCount} messages as read");
                    return response.MarkedCount;
                }

                _logger.LogWarning($"⚠️ Failed to mark messages as read: {response?.Message}");
                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error marking messages as read");
                return 0;
            }
        }

        // ✅ NEW: Delete all messages by ReportNo
        public async Task<int> DeleteMessagesByReportNoAsync(string reportNo)
        {
            try
            {
                _logger.LogInformation($"🗑️ Deleting all messages with ReportNo: {reportNo}");

                var response = await _jwtMessageService.DeleteMessagesByReportNoAsync(reportNo);

                if (response?.Status == "Success")
                {
                    int deletedCount = 0;

                    if (response.Data != null)
                    {
                        if (response.Data is System.Text.Json.JsonElement jsonElement)
                        {
                            if (jsonElement.TryGetProperty("deletedCount", out var countElement) ||
                                jsonElement.TryGetProperty("DeletedCount", out countElement))
                            {
                                deletedCount = countElement.GetInt32();
                            }
                        }
                        else if (response.Data.GetType().GetProperty("DeletedCount") != null)
                        {
                            deletedCount = (int)response.Data.GetType().GetProperty("DeletedCount").GetValue(response.Data);
                        }
                    }

                    _logger.LogInformation($"✅ Deleted {deletedCount} messages for ReportNo: {reportNo}");
                    return deletedCount;
                }

                _logger.LogWarning($"⚠️ Failed to delete messages by ReportNo: {response?.Message}");
                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Error deleting messages by ReportNo: {reportNo}");
                return 0;
            }
        }
    }
}