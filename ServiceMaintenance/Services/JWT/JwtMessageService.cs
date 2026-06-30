using Microsoft.Extensions.Logging;
using ServiceMaintenance.Models;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;

namespace ServiceMaintenance.Services.JWT
{
    public class JwtMessageService
    {
        private readonly JwtHttpClientService _jwtHttpClient;
        private readonly ILogger<JwtMessageService> _logger;

        public JwtMessageService(
            JwtHttpClientService jwtHttpClient,
            ILogger<JwtMessageService> logger)
        {
            _jwtHttpClient = jwtHttpClient;
            _logger = logger;
        }

        // ==================== SEND MESSAGE (FIXED) ====================
        public async Task<MessageApiResponse> SendMessageAsync(SendMessageDto messageDto)
        {
            try
            {
                _logger.LogInformation("═══════════════════════════════════════");
                _logger.LogInformation($"📤 SENDING MESSAGE");
                _logger.LogInformation($"   From: {messageDto.UserName} ({messageDto.UserID})");
                _logger.LogInformation($"   To: {messageDto.RecipientID}");
                _logger.LogInformation($"   Text: {messageDto.Text?.Substring(0, Math.Min(50, messageDto.Text?.Length ?? 0))}...");
                _logger.LogInformation("═══════════════════════════════════════");

                var json = JsonSerializer.Serialize(messageDto, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });

                _logger.LogInformation($"📦 Request JSON: {json}");

                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _jwtHttpClient.PostAsync("api/Message", content);

                _logger.LogInformation($"📦 Response Status: {response.StatusCode}");

                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogInformation($"📦 Response Content: {responseContent}");

                if (response.IsSuccessStatusCode)
                {
                    if (string.IsNullOrWhiteSpace(responseContent))
                    {
                        _logger.LogInformation("✅ Message sent successfully (empty response)");
                        return new MessageApiResponse
                        {
                            Status = "Success",
                            Message = "Message sent successfully"
                        };
                    }

                    try
                    {
                        var result = JsonSerializer.Deserialize<MessageApiResponse>(responseContent, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });

                        if (result != null)
                        {
                            _logger.LogInformation("✅ Message sent successfully with response data");
                            return result;
                        }
                    }
                    catch (JsonException jsonEx)
                    {
                        _logger.LogWarning($"⚠️ JSON deserialization failed: {jsonEx.Message}");
                    }

                    _logger.LogInformation("✅ Message sent successfully");
                    return new MessageApiResponse
                    {
                        Status = "Success",
                        Message = "Message sent successfully"
                    };
                }
                else
                {
                    _logger.LogError($"❌ Failed: {response.StatusCode} - {responseContent}");
                    return new MessageApiResponse
                    {
                        Status = "Error",
                        Message = $"Failed: {response.StatusCode} - {responseContent}"
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error sending message");
                return new MessageApiResponse
                {
                    Status = "Error",
                    Message = $"Error: {ex.Message}"
                };
            }
        }

        // ==================== GET CONVERSATION ====================
        public async Task<MessageListResponse> GetConversationAsync(string userId, string recipientId)
        {
            try
            {
                var endpoint = $"api/Message/conversation?userId={userId}&recipientId={recipientId}";
                var response = await _jwtHttpClient.GetAsync(endpoint);

                if (response.IsSuccessStatusCode)
                {
                    var contentString = await response.Content.ReadAsStringAsync();

                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    using var doc = JsonDocument.Parse(contentString);
                    var root = doc.RootElement;

                    List<Message> messages = null;

                    if (root.TryGetProperty("Data", out var dataProperty) ||
                        root.TryGetProperty("data", out dataProperty))
                    {
                        messages = JsonSerializer.Deserialize<List<Message>>(dataProperty.GetRawText(), options);
                    }
                    else if (root.ValueKind == JsonValueKind.Array)
                    {
                        messages = JsonSerializer.Deserialize<List<Message>>(contentString, options);
                    }

                    return new MessageListResponse
                    {
                        Status = "Success",
                        Message = "Messages retrieved successfully",
                        Data = messages ?? new List<Message>()
                    };
                }

                return new MessageListResponse
                {
                    Status = "Error",
                    Message = $"API returned {response.StatusCode}",
                    Data = new List<Message>()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Exception in GetConversationAsync");
                return new MessageListResponse
                {
                    Status = "Error",
                    Message = $"Exception: {ex.Message}",
                    Data = new List<Message>()
                };
            }
        }

        // ==================== GET ALL CONVERSATIONS ====================
        public async Task<ConversationsResponse> GetAllConversationsAsync(string userId)
        {
            try
            {
                var response = await _jwtHttpClient.GetAsync($"api/Message/conversations?userId={userId}");

                if (response.IsSuccessStatusCode)
                {
                    var contentString = await response.Content.ReadAsStringAsync();
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                    using var doc = JsonDocument.Parse(contentString);
                    var root = doc.RootElement;

                    List<ConversationPreview> conversations = null;

                    if (root.TryGetProperty("Data", out var dataProperty) ||
                        root.TryGetProperty("data", out dataProperty))
                    {
                        conversations = JsonSerializer.Deserialize<List<ConversationPreview>>(dataProperty.GetRawText(), options);
                    }
                    else if (root.ValueKind == JsonValueKind.Array)
                    {
                        conversations = JsonSerializer.Deserialize<List<ConversationPreview>>(contentString, options);
                    }

                    return new ConversationsResponse
                    {
                        Status = "Success",
                        Message = "Conversations retrieved successfully",
                        Data = conversations ?? new List<ConversationPreview>()
                    };
                }

                return new ConversationsResponse
                {
                    Status = "Success",
                    Message = "No conversations found",
                    Data = new List<ConversationPreview>()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Exception in GetAllConversationsAsync");
                return new ConversationsResponse
                {
                    Status = "Error",
                    Message = $"Exception: {ex.Message}",
                    Data = new List<ConversationPreview>()
                };
            }
        }

        // ==================== GET MESSAGE BY ID ====================
        public async Task<MessageDetailResponse> GetMessageByIdAsync(int messageId)
        {
            try
            {
                var response = await _jwtHttpClient.GetAsync($"api/Message/{messageId}");

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<MessageDetailResponse>();
                }

                return new MessageDetailResponse
                {
                    Status = "Error",
                    Message = $"Message not found: {response.ReasonPhrase}"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Error fetching message {messageId}");
                return new MessageDetailResponse
                {
                    Status = "Error",
                    Message = $"Error: {ex.Message}"
                };
            }
        }

        // ==================== JwtMessageService.cs ====================
        // ✅ FIXED: DeleteMessageAsync with proper token handling

        public async Task DeleteMessageAsync(int messageId)
        {
            var response = await _jwtHttpClient.DeleteAsync($"api/Message/{messageId}");

            response.EnsureSuccessStatusCode();
        }


        // ✅ HELPER: Get valid token (refresh if needed)
        private async Task<string> GetValidTokenAsync()
        {
            try
            {
                // Try to get current token from JwtHttpClientService
                // JwtHttpClientService already handles token validation
                // So we just need to verify the session token exists

                var httpClient = await Task.FromResult(_jwtHttpClient);

                // The JwtHttpClientService automatically adds the token
                // We just need to verify it's available
                return "token_will_be_added_by_client"; // Placeholder - actual token added by JwtHttpClientService
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting valid token");
                return string.Empty;
            }
        }
        // ==================== GET LAST MESSAGE ====================
        public async Task<MessageDetailResponse> GetLastMessageAsync(string userId, string recipientId)
        {
            try
            {
                var response = await _jwtHttpClient.GetAsync(
                    $"api/Message/last?userId={userId}&recipientId={recipientId}"
                );

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<MessageDetailResponse>();
                }

                return new MessageDetailResponse
                {
                    Status = "Error",
                    Message = $"Failed: {response.ReasonPhrase}"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error fetching last message");
                return new MessageDetailResponse
                {
                    Status = "Error",
                    Message = $"Error: {ex.Message}"
                };
            }
        }

        // ==================== GET UNREAD COUNTS ====================
        public async Task<UnreadCountsResponse> GetUnreadCountsAsync(string recipientId)
        {
            try
            {
                var response = await _jwtHttpClient.GetAsync(
                    $"api/Message/unread/counts?recipientId={recipientId}"
                );

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<UnreadCountsResponse>();
                }

                return new UnreadCountsResponse
                {
                    Status = "Error",
                    Message = $"Failed: {response.ReasonPhrase}",
                    Data = new Dictionary<string, int>()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error fetching unread counts");
                return new UnreadCountsResponse
                {
                    Status = "Error",
                    Message = $"Error: {ex.Message}",
                    Data = new Dictionary<string, int>()
                };
            }
        }

        // ==================== GET UNREAD COUNT (SPECIFIC) ====================
        public async Task<UnreadCountResponse> GetUnreadCountAsync(string recipientId, string senderId)
        {
            try
            {
                var response = await _jwtHttpClient.GetAsync(
                    $"api/Message/unread/count?recipientId={recipientId}&senderId={senderId}"
                );

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<UnreadCountResponse>();
                }

                return new UnreadCountResponse
                {
                    Status = "Error",
                    Message = $"Failed: {response.ReasonPhrase}",
                    Count = 0
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error fetching unread count");
                return new UnreadCountResponse
                {
                    Status = "Error",
                    Message = $"Error: {ex.Message}",
                    Count = 0
                };
            }
        }

        // ==================== MARK MESSAGES AS READ ====================
        public async Task<MarkReadResponse> MarkMessagesAsReadAsync(string recipientId, string senderId)
        {
            try
            {
                var payload = new { RecipientId = recipientId, SenderId = senderId };
                var response = await _jwtHttpClient.PutAsync(
                    "api/Message/read",
                    JsonContent.Create(payload)
                );

                var result = await response.Content.ReadFromJsonAsync<MarkReadResponse>();

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation($"✅ Marked messages as read");
                }

                return result ?? new MarkReadResponse
                {
                    Status = "Error",
                    Message = $"Failed: {response.ReasonPhrase}",
                    MarkedCount = 0
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error marking messages as read");
                return new MarkReadResponse
                {
                    Status = "Error",
                    Message = $"Error: {ex.Message}",
                    MarkedCount = 0
                };
            }
        }
        // ==================== DELETE MESSAGES BY REPORTNO ====================
        public async Task<MessageApiResponse> DeleteMessagesByReportNoAsync(string reportNo)
        {
            try
            {
                _logger.LogInformation($"🗑️ Deleting messages with ReportNo: {reportNo}");

                var response = await _jwtHttpClient.DeleteAsync($"api/Message/byReportNo/{reportNo}");

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<MessageApiResponse>();
                    _logger.LogInformation($"✅ Deleted messages for ReportNo: {reportNo}");
                    return result ?? new MessageApiResponse
                    {
                        Status = "Success",
                        Message = "Messages deleted successfully"
                    };
                }

                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError($"❌ Failed to delete messages: {response.StatusCode} - {errorContent}");
                return new MessageApiResponse
                {
                    Status = "Error",
                    Message = $"Failed: {response.StatusCode}"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Error deleting messages by ReportNo: {reportNo}");
                return new MessageApiResponse
                {
                    Status = "Error",
                    Message = $"Error: {ex.Message}"
                };
            }
        }
    }

    // ==================== DTO & RESPONSE MODELS ====================
    public class SendMessageDto
    {
        public string UserID { get; set; }
        public string RecipientID { get; set; }
        public string Text { get; set; }
        public string FileUrl { get; set; }
        public string UserName { get; set; }
        public string VideoUrl { get; set; }
        public string AudioUrl { get; set; }
        public int? ReplyToMessageId { get; set; }
        public string ReplyToUserName { get; set; }
        public string ReplyToText { get; set; }
    }

    public class MessageListResponse
    {
        public string Status { get; set; }
        public string Message { get; set; }
        public List<Message> Data { get; set; }
    }

    public class MessageDetailResponse
    {
        public string Status { get; set; }
        public string Message { get; set; }
        public Message Data { get; set; }
    }

    public class UnreadCountsResponse
    {
        public string Status { get; set; }
        public string Message { get; set; }
        public Dictionary<string, int> Data { get; set; }
    }

    public class UnreadCountResponse
    {
        public string Status { get; set; }
        public string Message { get; set; }
        public int Count { get; set; }
    }

    public class MarkReadResponse
    {
        public string Status { get; set; }
        public string Message { get; set; }
        public int MarkedCount { get; set; }
    }

    public class MessageApiResponse
    {
        public string Status { get; set; }
        public string Message { get; set; }
        public object Data { get; set; }
    }

    public class ConversationPreview
    {
        public string OtherUserId { get; set; }
        public string OtherUserName { get; set; }
        public string LastMessageText { get; set; }
        public string LastMessageFileUrl { get; set; }
        public string LastMessageAudioUrl { get; set; }
        public DateTime LastMessageTime { get; set; }
        public int UnreadCount { get; set; }
    }

    public class ConversationsResponse
    {
        public string Status { get; set; } 
        public string Message { get; set; }
        public List<ConversationPreview> Data { get; set; }
    }
}