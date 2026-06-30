using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;
using ServiceMaintenance.Models;
using ServiceMaintenance.Services.JWT;
using System.Security.Claims;

namespace ServiceMaintenance.Shared
{
    public partial class IbottomNavMenu : IAsyncDisposable
    {
        private ElementReference drawerElement;
        private bool showSettingsDrawer;
        private List<Article> articles = new List<Article>();
        private int notificationCount = 0;
        private bool showNotificationDropdown = false;
        private HubConnection hubConnection;
        private string notificationDropdownClass = "";
        private List<UserMessagePreview> userMessages = new List<UserMessagePreview>();
        private int totalUnreadMessages = 0;
        private string messagesOverlayClass = "";
        private HubConnection chatHubConnection;
        private string drawerClass = "closed";

        [Inject] private NavigationManager NavigationManager { get; set; }
        [Inject] private UserService UserService { get; set; }
        [Inject] private GlobalArticleCacheService GlobalArticleCache { get; set; }
        [Inject] private JwtMessageService JwtMessageService { get; set; }
        [Inject] private IJSRuntime JSRuntime { get; set; }
        [Inject] private JwtHttpClientService JwtHttpClient { get; set; }

        private UserDto currentUser;
        private string currentUserId;
        private string userName;
        private string profilePictureUrl;

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

        public class ProfileResponse
        {
            public string Status { get; set; }
            public string Message { get; set; }
            public ProfileData Data { get; set; }
        }

        public class ProfileData
        {
            public string Id { get; set; }
            public string UserName { get; set; }
            public string Email { get; set; }
            public string FirstName { get; set; }
            public string LastName { get; set; }
            public string PhoneNumber { get; set; }
            public string ProfilePictureUrl { get; set; }
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                await JSRuntime.InvokeVoidAsync("addClickDrawer", drawerElement);
            }
        }

        protected override async Task OnInitializedAsync()
        {
            try
            {
                Console.WriteLine("═══════════════════════════════════════");
                Console.WriteLine("🔧 BOTTOM NAV INITIALIZATION");
                Console.WriteLine("═══════════════════════════════════════");

                var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
                var userClaims = authState.User;

                if (!userClaims.Identity.IsAuthenticated)
                {
                    Console.WriteLine("⚠️ User not authenticated");
                    return;
                }

                currentUserId = userClaims.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                Console.WriteLine($"✅ User authenticated: {currentUserId}");

                if (string.IsNullOrEmpty(currentUserId))
                {
                    Console.WriteLine("❌ No user ID found");
                    return;
                }

                // ✅ STEP 1: Load user details
                currentUser = await UserService.GetApplicationUserAsync(currentUserId);
                if (currentUser != null)
                {
                    userName = $"{currentUser.FirstName} {currentUser.LastName}".Trim();
                    if (string.IsNullOrEmpty(userName))
                    {
                        userName = currentUser.UserName ?? "Unknown User";
                    }
                    Console.WriteLine($"✅ User loaded: {userName}");
                }

                // ✅ STEP 2: Load profile picture
                await LoadProfilePictureAsync();

                // ✅ STEP 3: Load notifications
                articles = await GlobalArticleCache.GetArticlesAsync(excludeUsername: userName);
                notificationCount = articles.Count(a => !a.IsRead && a.Username != userName);
                Console.WriteLine($"✅ Loaded {articles.Count} articles, {notificationCount} unread");

                // Subscribe to real-time updates
                GlobalArticleCache.OnArticlesUpdated += HandleArticlesUpdated;
                GlobalArticleCache.OnArticleAdded += HandleArticleAdded;

                // ✅ STEP 4: Load messages with unread counts
                await LoadUserMessagesWithUnreadCounts();

                // ✅ STEP 5: Setup SignalR
                await InitializeSignalRConnections();

                Console.WriteLine("═══════════════════════════════════════");
                Console.WriteLine($"✅ BOTTOM NAV INITIALIZATION COMPLETE");
                Console.WriteLine($"   User: {userName}");
                Console.WriteLine($"   Profile Picture: {(string.IsNullOrEmpty(profilePictureUrl) ? "Not set" : "Loaded")}");
                Console.WriteLine($"   Envelope Badge: {totalUnreadMessages}");
                Console.WriteLine($"   Bell Badge: {notificationCount}");
                Console.WriteLine("═══════════════════════════════════════");

                StateHasChanged();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error in OnInitializedAsync: {ex.Message}");
                Console.WriteLine($"   Stack: {ex.StackTrace}");
            }
        }

        private async Task LoadUserMessagesWithUnreadCounts()
        {
            try
            {
                if (string.IsNullOrEmpty(currentUserId))
                {
                    Console.WriteLine("⚠️ No current user ID available");
                    totalUnreadMessages = 0;
                    userMessages = new List<UserMessagePreview>();
                    StateHasChanged();
                    return;
                }

                Console.WriteLine("═══════════════════════════════════════");
                Console.WriteLine($"📥 LOADING BOTTOM NAV MESSAGES");
                Console.WriteLine($"   Current User ID: {currentUserId}");
                Console.WriteLine("═══════════════════════════════════════");

                // ✅ STEP 1: Get unread counts FIRST
                var unreadCountsResponse = await JwtMessageService.GetUnreadCountsAsync(currentUserId);

                Console.WriteLine($"📊 Unread Counts API Response:");
                Console.WriteLine($"   Status: {unreadCountsResponse?.Status}");
                Console.WriteLine($"   Data Count: {unreadCountsResponse?.Data?.Count ?? 0}");

                // ✅ Initialize empty dictionary
                var unreadCountsBySenderId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                if (unreadCountsResponse?.Status == "Success" && unreadCountsResponse.Data != null)
                {
                    unreadCountsBySenderId = new Dictionary<string, int>(
                        unreadCountsResponse.Data,
                        StringComparer.OrdinalIgnoreCase
                    );

                    Console.WriteLine($"✅ Loaded unread counts from {unreadCountsBySenderId.Count} users:");
                    foreach (var kvp in unreadCountsBySenderId)
                    {
                        Console.WriteLine($"   - '{kvp.Key}' → {kvp.Value} unread");
                    }
                }
                else
                {
                    Console.WriteLine("⚠️ No unread counts returned from API");
                }

                // ✅ STEP 2: Calculate total IMMEDIATELY
                totalUnreadMessages = unreadCountsBySenderId.Values.Sum();
                Console.WriteLine($"📊 Total unread messages: {totalUnreadMessages}");

                // ✅ Update UI NOW
                await InvokeAsync(StateHasChanged);

                // ✅ STEP 3: Load conversations
                var conversationsResponse = await JwtMessageService.GetAllConversationsAsync(currentUserId);

                Console.WriteLine($"📦 Conversations API Response:");
                Console.WriteLine($"   Status: {conversationsResponse?.Status}");
                Console.WriteLine($"   Conversations Count: {conversationsResponse?.Data?.Count ?? 0}");

                userMessages = new List<UserMessagePreview>();

                if (conversationsResponse?.Status == "Success" && conversationsResponse.Data != null && conversationsResponse.Data.Any())
                {
                    Console.WriteLine($"✅ Processing {conversationsResponse.Data.Count} conversations");

                    foreach (var conversation in conversationsResponse.Data)
                    {
                        try
                        {
                            var otherUser = await UserService.GetApplicationUserAsync(conversation.OtherUserId);

                            if (otherUser == null)
                            {
                                Console.WriteLine($"   ⚠️ User not found: {conversation.OtherUserId}");
                                continue;
                            }

                            int unreadCount = unreadCountsBySenderId.GetValueOrDefault(conversation.OtherUserId, 0);

                            var lastMessageText = GetMessagePreviewText(
                                conversation.LastMessageText,
                                conversation.LastMessageFileUrl,
                                conversation.LastMessageAudioUrl
                            );

                            var userMsg = new UserMessagePreview
                            {
                                UserId = otherUser.Id,
                                UserName = otherUser.UserName ?? "Unknown User",
                                ProfilePicture = "/images/default-profile.png",
                                LastMessageText = lastMessageText,
                                LastMessageTime = conversation.LastMessageTime,
                                UnreadCount = unreadCount,
                                IsOnline = false
                            };

                            userMessages.Add(userMsg);

                            Console.WriteLine($"   ✅ {userMsg.UserName}: {unreadCount} unread");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"   ❌ Error processing conversation: {ex.Message}");
                        }
                    }

                    userMessages = userMessages
                        .OrderByDescending(u => u.UnreadCount)
                        .ThenByDescending(u => u.LastMessageTime)
                        .ToList();
                }
                else if (unreadCountsBySenderId.Any())
                {
                    // ✅ FALLBACK: Create entries from unread counts
                    Console.WriteLine($"⚠️ No conversations, creating from {unreadCountsBySenderId.Count} unread counts");

                    foreach (var kvp in unreadCountsBySenderId.Where(x => x.Value > 0))
                    {
                        try
                        {
                            var otherUser = await UserService.GetApplicationUserAsync(kvp.Key);

                            if (otherUser != null)
                            {
                                var userMsg = new UserMessagePreview
                                {
                                    UserId = otherUser.Id,
                                    UserName = otherUser.UserName ?? "Unknown User",
                                    ProfilePicture = "/images/default-profile.png",
                                    LastMessageText = "New message",
                                    LastMessageTime = DateTime.UtcNow,
                                    UnreadCount = kvp.Value,
                                    IsOnline = false
                                };

                                userMessages.Add(userMsg);
                                Console.WriteLine($"   ✅ Added {userMsg.UserName}: {kvp.Value} unread");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"   ❌ Error loading user {kvp.Key}: {ex.Message}");
                        }
                    }

                    userMessages = userMessages.OrderByDescending(u => u.UnreadCount).ToList();
                }
                else
                {
                    Console.WriteLine("ℹ️ No conversations and no unread counts");
                }

                // ✅ VERIFICATION
                int calculatedTotal = userMessages.Sum(u => u.UnreadCount);

                if (calculatedTotal != totalUnreadMessages)
                {
                    Console.WriteLine($"⚠️ MISMATCH:");
                    Console.WriteLine($"   API total: {totalUnreadMessages}");
                    Console.WriteLine($"   Calculated: {calculatedTotal}");
                }
                else
                {
                    Console.WriteLine($"✅ Totals match: {totalUnreadMessages}");
                }

                Console.WriteLine("═══════════════════════════════════════");
                Console.WriteLine($"✅ BOTTOM NAV MESSAGES LOADED");
                Console.WriteLine($"   Conversations: {userMessages.Count}");
                Console.WriteLine($"   Total unread: {totalUnreadMessages}");
                Console.WriteLine("═══════════════════════════════════════");

                await InvokeAsync(StateHasChanged);
            }
            catch (Exception ex)
            {
                Console.WriteLine("═══════════════════════════════════════");
                Console.WriteLine($"❌ ERROR IN LoadUserMessagesWithUnreadCounts");
                Console.WriteLine($"   Message: {ex.Message}");
                Console.WriteLine($"   Stack: {ex.StackTrace}");
                Console.WriteLine("═══════════════════════════════════════");

                totalUnreadMessages = 0;
                userMessages = new List<UserMessagePreview>();
                await InvokeAsync(StateHasChanged);
            }
        }

        private async Task LoadProfilePictureAsync()
        {
            try
            {
                Console.WriteLine($"📸 Loading profile picture for user: {currentUserId}");

                var response = await JwtHttpClient.GetAsync("api/Auth/profile");

                if (response.IsSuccessStatusCode)
                {
                    var profileResponse = await response.Content.ReadFromJsonAsync<ProfileResponse>();

                    if (profileResponse?.Status == "Success" && profileResponse.Data != null)
                    {
                        profilePictureUrl = profileResponse.Data.ProfilePictureUrl;

                        if (!string.IsNullOrEmpty(profilePictureUrl))
                        {
                            Console.WriteLine($"✅ Profile picture loaded: {profilePictureUrl}");
                        }
                        else
                        {
                            Console.WriteLine($"ℹ️ No profile picture set");
                            profilePictureUrl = "/images/avatar.jpg";
                        }

                        StateHasChanged();
                    }
                }
                else
                {
                    Console.WriteLine($"⚠️ Failed to load profile: {response.StatusCode}");
                    profilePictureUrl = "/images/avatar.jpg";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error loading profile picture: {ex.Message}");
                profilePictureUrl = "/images/avatar.jpg";
            }
        }

        private async Task InitializeSignalRConnections()
        {
            try
            {
                Console.WriteLine("🔌 Setting up SignalR connections");

                // ✅ Notification Hub
                hubConnection = new HubConnectionBuilder()
                    .WithUrl(NavigationManager.ToAbsoluteUri("/NotificationHub"))
                    .WithAutomaticReconnect()
                    .Build();

                hubConnection.On<string, string, string, string>("sendToUser",
                    async (heading, content, username, base64ProfilePicture) =>
                    {
                        if (username != userName)
                        {
                            var newArticle = new Article
                            {
                                ArticleHeading = heading,
                                ArticleContent = content,
                                Username = username,
                                ProfilePicture = base64ProfilePicture,
                                Timestamp = DateTime.UtcNow,
                                IsRead = false
                            };

                            await GlobalArticleCache.AddArticleAsync(newArticle);
                        }
                    });

                // ✅ Chat Hub
                chatHubConnection = new HubConnectionBuilder()
                    .WithUrl(NavigationManager.ToAbsoluteUri("/chatHub"))
                    .WithAutomaticReconnect()
                    .ConfigureLogging(logging =>
                    {
                        logging.SetMinimumLevel(LogLevel.Warning);
                    })
                    .Build();

                chatHubConnection.On("ReceiveMessage", async (object[] args) =>
                {
                    try
                    {
                        string GetStringValue(object obj)
                        {
                            if (obj == null) return string.Empty;
                            if (obj is System.Text.Json.JsonElement jsonElement)
                            {
                                return jsonElement.ValueKind == System.Text.Json.JsonValueKind.Null
                                    ? string.Empty
                                    : jsonElement.GetString() ?? string.Empty;
                            }
                            return obj.ToString() ?? string.Empty;
                        }

                        var senderUserName = GetStringValue(args[0]);
                        var messageText = GetStringValue(args[1]);
                        var timestamp = GetStringValue(args[2]);
                        var fileUrl = GetStringValue(args[3]);
                        var audioUrl = GetStringValue(args[4]);
                        var senderId = GetStringValue(args[6]);
                        var recipientId = GetStringValue(args[7]);

                        // Only update if message is TO current user FROM someone else
                        if (recipientId == currentUserId && senderId != currentUserId)
                        {
                            await UpdateLastMessage(senderId, senderUserName, messageText, timestamp, fileUrl, audioUrl);
                        }

                        await InvokeAsync(StateHasChanged);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Error processing ReceiveMessage: {ex.Message}");
                    }
                });

                chatHubConnection.On<Dictionary<string, int>>("InitialUnreadCounts", counts =>
                {
                    UpdateUnreadCounts(counts);
                });

                chatHubConnection.On<string, int>("UnreadCountUpdated", (senderId, count) =>
                {
                    UpdateUserUnreadCount(senderId, count);
                });

                chatHubConnection.On<string>("MessagesMarkedRead", senderId =>
                {
                    UpdateUserUnreadCount(senderId, 0);
                });

                await chatHubConnection.StartAsync();
                await hubConnection.StartAsync();

                Console.WriteLine("✅ SignalR connections initialized");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ SignalR connection error: {ex.Message}");
            }
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

        private string GetMessagePreviewText(string messageText, string fileUrl, string audioUrl)
        {
            if (!string.IsNullOrEmpty(audioUrl))
            {
                return "🎤 Sent a voice message";
            }

            if (!string.IsNullOrEmpty(fileUrl))
            {
                if (IsImageFile(fileUrl))
                {
                    return "📷 Sent a photo";
                }
                else if (IsVideoFile(fileUrl))
                {
                    return "🎥 Sent a video";
                }
                else
                {
                    return "📎 Sent an attachment";
                }
            }

            return TruncateMessage(messageText, 50);
        }

        private string TruncateMessage(string message, int maxLength)
        {
            if (string.IsNullOrEmpty(message)) return "No message";

            var decoded = System.Net.WebUtility.HtmlDecode(message);
            return decoded.Length <= maxLength ? decoded : decoded.Substring(0, maxLength) + "...";
        }

        private void ToggleMessagesOverlay()
        {
            messagesOverlayClass = messagesOverlayClass == "show-fullscreen" ? "" : "show-fullscreen";
            InvokeAsync(StateHasChanged);
        }

        private void NavigateToChat(string userId)
        {
            try
            {
                Console.WriteLine($"🔗 Navigating to chat with user: {userId}");

                var userMsg = userMessages.FirstOrDefault(u => u.UserId == userId);

                if (userMsg != null && userMsg.UnreadCount > 0)
                {
                    int previousUnreadCount = userMsg.UnreadCount;

                    Console.WriteLine($"📨 Clearing {previousUnreadCount} unread messages");
                    userMsg.UnreadCount = 0;
                    totalUnreadMessages -= previousUnreadCount;
                    StateHasChanged();

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var response = await JwtMessageService.MarkMessagesAsReadAsync(currentUserId, userId);

                            if (response?.Status == "Success")
                            {
                                Console.WriteLine($"✅ Marked {response.MarkedCount} messages as read");

                                if (chatHubConnection?.State == HubConnectionState.Connected)
                                {
                                    await chatHubConnection.InvokeAsync("MarkMessagesAsRead", currentUserId, userId);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"❌ Background mark-as-read failed: {ex.Message}");
                        }
                    });
                }

                messagesOverlayClass = "";
                NavigationManager.NavigateTo($"/chat?userId={userId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error in NavigateToChat: {ex.Message}");
                messagesOverlayClass = "";
                NavigationManager.NavigateTo($"/chat?userId={userId}");
            }
        }

        private async Task UpdateLastMessage(string userId, string userName, string messageText, string timestamp, string fileUrl = null, string audioUrl = null)
        {
            try
            {
                Console.WriteLine($"📨 Updating message from {userName} ({userId})");

                var userMsg = userMessages.FirstOrDefault(u => u.UserId == userId);

                if (userMsg != null)
                {
                    var displayText = GetMessagePreviewText(messageText, fileUrl, audioUrl);
                    userMsg.LastMessageText = displayText;
                    userMsg.LastMessageTime = DateTime.Parse(timestamp);

                    // Only increment if message is FROM other user
                    if (userId != currentUserId)
                    {
                        userMsg.UnreadCount++;
                        totalUnreadMessages++;
                        Console.WriteLine($"   ✅ Updated: {userMsg.UserName} now has {userMsg.UnreadCount} unread");
                    }

                    userMessages.Remove(userMsg);
                    userMessages.Insert(0, userMsg);
                }
                else if (userId != currentUserId)
                {
                    var otherUser = await UserService.GetApplicationUserAsync(userId);
                    if (otherUser != null)
                    {
                        var displayText = GetMessagePreviewText(messageText, fileUrl, audioUrl);

                        var newUserMsg = new UserMessagePreview
                        {
                            UserId = otherUser.Id,
                            UserName = otherUser.UserName,
                            ProfilePicture = "/images/default-profile.png",
                            LastMessageText = displayText,
                            LastMessageTime = DateTime.Parse(timestamp),
                            UnreadCount = 1,
                            IsOnline = false
                        };

                        userMessages.Insert(0, newUserMsg);
                        totalUnreadMessages++;
                    }
                }

                Console.WriteLine($"📊 Total unread: {totalUnreadMessages}");
                await InvokeAsync(StateHasChanged);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error updating message: {ex.Message}");
            }
        }

        private void UpdateUnreadCounts(Dictionary<string, int> counts)
        {
            if (counts == null || !counts.Any()) return;

            foreach (var userMsg in userMessages)
            {
                if (counts.TryGetValue(userMsg.UserId, out int count))
                {
                    if (userMsg.UnreadCount != count)
                    {
                        userMsg.UnreadCount = count;
                    }
                }
                else if (userMsg.UnreadCount > 0)
                {
                    userMsg.UnreadCount = 0;
                }
            }

            int previousTotal = totalUnreadMessages;
            totalUnreadMessages = userMessages.Sum(u => u.UnreadCount);

            if (previousTotal != totalUnreadMessages)
            {
                Console.WriteLine($"📊 Total unread: {previousTotal} → {totalUnreadMessages}");
            }

            InvokeAsync(StateHasChanged);
        }

        private void UpdateUserUnreadCount(string userId, int count)
        {
            var userMsg = userMessages.FirstOrDefault(u =>
                u.UserId.Equals(userId, StringComparison.OrdinalIgnoreCase));

            if (userMsg != null && userMsg.UnreadCount != count)
            {
                userMsg.UnreadCount = count;

                int previousTotal = totalUnreadMessages;
                totalUnreadMessages = userMessages.Sum(u => u.UnreadCount);

                Console.WriteLine($"📊 Total: {previousTotal} → {totalUnreadMessages}");
                InvokeAsync(StateHasChanged);
            }
        }

        private async Task ClearNotifications()
        {
            try
            {
                await GlobalArticleCache.ClearAllArticlesAsync();
                articles.Clear();
                notificationCount = 0;
                await InvokeAsync(StateHasChanged);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error clearing notifications: {ex.Message}");
            }
        }

        private string GetRelativeTime(DateTime timestamp)
        {
            DateTime now = DateTime.UtcNow;
            DateTime utcTimestamp = timestamp.Kind == DateTimeKind.Utc
                ? timestamp
                : timestamp.ToUniversalTime();

            var timeDiff = now - utcTimestamp;

            if (timeDiff.TotalSeconds < 0) return "just now";
            if (timeDiff.TotalSeconds < 60) return $"{(int)timeDiff.TotalSeconds}s ago";
            if (timeDiff.TotalMinutes < 60) return $"{(int)timeDiff.TotalMinutes}m ago";
            if (timeDiff.TotalHours < 24) return $"{(int)timeDiff.TotalHours}h ago";
            if (timeDiff.TotalDays < 7) return $"{(int)timeDiff.TotalDays}d ago";
            if (timeDiff.TotalDays < 30) return $"{(int)(timeDiff.TotalDays / 7)}w ago";

            return utcTimestamp.ToLocalTime().ToString("MMM d, yyyy");
        }

        private async Task MarkAsRead(Article notification)
        {
            if (notification.IsRead) return;

            notification.IsRead = true;
            await GlobalArticleCache.MarkAsReadAsync(notification.Id);
            notificationCount = articles.Count(a => !a.IsRead);
            await InvokeAsync(StateHasChanged);
        }

        private void NavigateToHome()
        {
            NavigationManager.NavigateTo("/");
            CloseDrawer();
        }

        private void ToggleNotificationDropdown()
        {
            notificationDropdownClass = notificationDropdownClass == "show-fullscreen" ? "" : "show-fullscreen";
            InvokeAsync(StateHasChanged);
        }

        private void ToggleSettingsDrawer()
        {
            if (!showSettingsDrawer)
            {
                showSettingsDrawer = true;
                drawerClass = "open";
            }
            else
            {
                showSettingsDrawer = false;
                drawerClass = "closed";
            }

            StateHasChanged();
        }

        private void CloseDrawer()
        {
            drawerClass = "closed";
            showSettingsDrawer = false;
            StateHasChanged();
        }

        private void dr1()
        {
            NavigationManager.NavigateTo("/some-setting");
            CloseDrawer();
        }

        private void dr2()
        {
            NavigationManager.NavigateTo("/another-setting");
            CloseDrawer();
        }

        private void dr3()
        {
            NavigationManager.NavigateTo("/another-setting");
            CloseDrawer();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (GlobalArticleCache != null)
                {
                    GlobalArticleCache.OnArticlesUpdated -= HandleArticlesUpdated;
                    GlobalArticleCache.OnArticleAdded -= HandleArticleAdded;
                }

                if (chatHubConnection != null)
                {
                    await chatHubConnection.DisposeAsync();
                    Console.WriteLine("✅ Chat hub disposed");
                }

                if (hubConnection != null)
                {
                    await hubConnection.DisposeAsync();
                    Console.WriteLine("✅ Notification hub disposed");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error disposing: {ex.Message}");
            }
        }

        private async Task HandleArticlesUpdated(List<Article> updatedArticles, int unreadCount)
        {
            articles = updatedArticles.Where(a => a.Username != userName)
                                      .OrderByDescending(a => a.Timestamp)
                                      .ToList();
            notificationCount = unreadCount;
            await InvokeAsync(StateHasChanged);
        }

        private async Task HandleArticleAdded(Article newArticle)
        {
            if (newArticle.Username != userName)
            {
                await InvokeAsync(StateHasChanged);
            }
        }
    }
}