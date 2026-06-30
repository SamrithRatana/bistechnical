using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.SignalR;
using ServiceMaintenance.Models;
using ServiceMaintenance.Services.RealTimeServices;
using System.Threading.Tasks;

namespace ServiceMaintenance.Services.JWT
{
    /// <summary>
    /// ✅ JWT-based State Management Action (100% JWT API - No Database Context)
    /// Uses JWT authentication and JwtArticleService for all operations
    /// </summary>
    public class StateManagementAction
    {
        private readonly IHubContext<NotificationHub> _hubContext;
        private readonly AuthenticationStateProvider _authenticationStateProvider;
        private readonly NavigationManager _navigationManager;
        private readonly JwtArticleService _jwtArticleService;
        private readonly GlobalArticleCacheService _globalArticleCache;
        private readonly ILogger<StateManagementAction> _logger;

        public StateManagementAction(
            IHubContext<NotificationHub> hubContext,
            AuthenticationStateProvider authenticationStateProvider,
            NavigationManager navigationManager,
            JwtArticleService jwtArticleService,
            GlobalArticleCacheService globalArticleCache,
            ILogger<StateManagementAction> logger)
        {
            _hubContext = hubContext;
            _authenticationStateProvider = authenticationStateProvider;
            _navigationManager = navigationManager;
            _jwtArticleService = jwtArticleService;
            _globalArticleCache = globalArticleCache;
            _logger = logger;
        }

        public async Task SendNotificationAsync(string actionType)
        {
            try
            {
                var authState = await _authenticationStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                // ✅ Get user info from JWT claims
                var currentUserName = user.Identity?.Name ?? "Unknown User";

                // ✅ Get profile picture from JWT claims or use default
                var profilePictureClaim = user.FindFirst("profile_picture")?.Value
                    ?? user.FindFirst("picture")?.Value;
                var currentUserProfilePicture = profilePictureClaim ?? "/images/default-avatar.png";

                var currentUrl = _navigationManager.Uri;
                var moduleName = ExtractModuleNameFromUrl(currentUrl);

                var articleHeading = actionType switch
                {
                    "create" => $"Created Successfully in {moduleName}",
                    "update" => $"Updated Successfully in {moduleName}",
                    "delete" => $"Deleted Successfully in {moduleName}",
                    _ => "Action Performed"
                };

                var fixedArticleContent = "Service Checklist Management";

                // ✅ Send SignalR notification to all clients
                await _hubContext.Clients.All.SendAsync("sendToUser",
                    articleHeading,
                    fixedArticleContent,
                    currentUserName,
                    currentUserProfilePicture);

                _logger.LogInformation($"✅ Notification sent: {articleHeading} by {currentUserName}");

                // ✅ OPTIONAL: Also save to database via JWT API
                // Uncomment if you want notifications persisted
                /*
                var article = new Article
                {
                    ArticleHeading = articleHeading,
                    ArticleContent = fixedArticleContent,
                    Username = currentUserName,
                    ProfilePicture = currentUserProfilePicture,
                    Timestamp = DateTime.UtcNow,
                    IsRead = false,
                    IsActionVisible = false
                };

                await _globalArticleCache.AddArticleAsync(article);
                */
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error sending notification");
            }
        }

        public async Task SendNotificationReceiveItem(string actionType, string reportNo)
        {
            try
            {
                var authState = await _authenticationStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                var currentUserName = user.Identity?.Name ?? "Unknown User";
                var profilePictureClaim = user.FindFirst("profile_picture")?.Value
                    ?? user.FindFirst("picture")?.Value;
                var currentUserProfilePicture = profilePictureClaim ?? "/images/default-avatar.png";

                var currentUrl = _navigationManager.Uri;
                var moduleName = ExtractModuleNameFromUrl(currentUrl);

                var articleHeading = actionType switch
                {
                    "create" => $"Created Successfully in {moduleName} (Ref No: {reportNo})",
                    "update" => $"Updated Successfully in {moduleName} (Ref No: {reportNo})",
                    "delete" => $"Deleted Successfully in {moduleName} (Ref No: {reportNo})",
                    _ => $"Action in {moduleName} (Ref No: {reportNo})"
                };

                var fixedArticleContent = "Service Checklist Management";

                // ✅ Send SignalR notification
                await _hubContext.Clients.All.SendAsync("sendToUser",
                    articleHeading,
                    fixedArticleContent,
                    currentUserName,
                    currentUserProfilePicture);

                _logger.LogInformation($"✅ Notification sent: {articleHeading} by {currentUserName}");

                // ✅ Save to database via JWT API (with action visibility for "create")
                var article = new Article
                {
                    ArticleHeading = articleHeading,
                    ArticleContent = fixedArticleContent,
                    Username = currentUserName,
                    ProfilePicture = currentUserProfilePicture,
                    Timestamp = DateTime.UtcNow,
                    IsRead = false,
                    IsActionVisible = actionType == "create" // Only show actions for new items
                };

                await _globalArticleCache.AddArticleAsync(article);
                _logger.LogInformation($"✅ Article saved to database: {articleHeading}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error sending notification for receive item");
            }
        }

        public async Task SendNotificationForInspectItem(string actionType, string reportNo)
        {
            try
            {
                var authState = await _authenticationStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                var currentUserName = user.Identity?.Name ?? "Unknown User";
                var profilePictureClaim = user.FindFirst("profile_picture")?.Value
                    ?? user.FindFirst("picture")?.Value;
                var currentUserProfilePicture = profilePictureClaim ?? "/images/default-avatar.png";

                var currentUrl = _navigationManager.Uri;
                var moduleName = ExtractModuleNameofInspect(currentUrl);

                var articleHeading = actionType switch
                {
                    "create" => $"Created Successfully in {moduleName} (Ref No: {reportNo})",
                    "update" => $"Updated Successfully in {moduleName} (Ref No: {reportNo})",
                    "delete" => $"Deleted Successfully in {moduleName} (Ref No: {reportNo})",
                    _ => $"Action in {moduleName} (Ref No: {reportNo})"
                };

                var fixedArticleContent = "Service Checklist Management";

                // ✅ Send SignalR notification
                await _hubContext.Clients.All.SendAsync("sendToUser",
                    articleHeading,
                    fixedArticleContent,
                    currentUserName,
                    currentUserProfilePicture);

                _logger.LogInformation($"✅ Notification sent: {articleHeading} by {currentUserName}");

                // ✅ Save to database via JWT API
                var article = new Article
                {
                    ArticleHeading = articleHeading,
                    ArticleContent = fixedArticleContent,
                    Username = currentUserName,
                    ProfilePicture = currentUserProfilePicture,
                    Timestamp = DateTime.UtcNow,
                    IsRead = false,
                    IsActionVisible = actionType == "create"
                };

                await _globalArticleCache.AddArticleAsync(article);
                _logger.LogInformation($"✅ Article saved to database: {articleHeading}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error sending notification for inspect item");
            }
        }

        /// <summary>
        /// ✅ Update notification visibility using JWT API instead of database context
        /// </summary>
        public async Task UpdateNotificationVisibility(string reportNo, bool isVisible)
        {
            try
            {
                _logger.LogInformation($"📝 Updating notification visibility for {reportNo}: {isVisible}");

                // ✅ STEP 1: Get all articles from cache/API
                var articles = await _globalArticleCache.GetArticlesAsync(forceRefresh: true);

                // ✅ STEP 2: Find the notification by ReportNo in the ArticleHeading
                var notification = articles.FirstOrDefault(a =>
                    a.ArticleHeading.Contains($"Ref No: {reportNo}", StringComparison.OrdinalIgnoreCase));

                if (notification != null)
                {
                    _logger.LogInformation($"✅ Found notification: {notification.ArticleHeading} (ID: {notification.Id})");

                    // ✅ STEP 3: Update visibility via GlobalArticleCache (uses JWT API internally)
                    var success = await _globalArticleCache.UpdateArticleVisibilityAsync(
                        notification.Id,
                        isVisible
                    );

                    if (success)
                    {
                        _logger.LogInformation($"✅ Updated notification visibility for {reportNo}: {isVisible}");
                    }
                    else
                    {
                        _logger.LogWarning($"⚠️ Failed to update notification visibility for {reportNo}");
                    }
                }
                else
                {
                    _logger.LogWarning($"⚠️ Notification not found for report: {reportNo}");
                    _logger.LogInformation($"   Searched in {articles.Count} articles");

                    // Debug: Show what we have
                    var reportsFound = articles
                        .Where(a => a.ArticleHeading.Contains("Ref No:"))
                        .Select(a => a.ArticleHeading)
                        .ToList();

                    if (reportsFound.Any())
                    {
                        _logger.LogInformation($"   Available reports: {string.Join(", ", reportsFound)}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Error updating notification visibility for {reportNo}");
            }
        }

        /// <summary>
        /// ✅ ALTERNATIVE: Update by article ID (more reliable)
        /// </summary>
        public async Task UpdateNotificationVisibilityById(int articleId, bool isVisible)
        {
            try
            {
                _logger.LogInformation($"📝 Updating notification visibility for article {articleId}: {isVisible}");

                var success = await _globalArticleCache.UpdateArticleVisibilityAsync(articleId, isVisible);

                if (success)
                {
                    _logger.LogInformation($"✅ Updated article {articleId} visibility: {isVisible}");
                }
                else
                {
                    _logger.LogWarning($"⚠️ Failed to update article {articleId} visibility");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Error updating article {articleId} visibility");
            }
        }

        private string ExtractModuleNameFromUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                var segments = uri.Segments;
                var moduleName = segments.Length > 1 ? segments[^1].Trim('/') : "Unknown Module";
                return moduleName;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting module name from URL");
                return "Unknown Module";
            }
        }

        private string ExtractModuleNameofInspect(string url)
        {
            try
            {
                var uri = new Uri(url);
                var segments = uri.Segments;

                if (segments.Length > 1)
                {
                    return segments[1].Trim('/');
                }

                return "Unknown Module";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting inspect module name from URL");
                return "Unknown Module";
            }
        }
    }
}