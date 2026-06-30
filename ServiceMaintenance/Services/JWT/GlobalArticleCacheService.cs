using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using ServiceMaintenance.Models;

namespace ServiceMaintenance.Services.JWT
{
    /// <summary>
    /// ✅ Global service for caching and managing articles/notifications
    /// - Prevents duplicate API calls from Header and Bottom Navigation
    /// - Thread-safe per-session caching
    /// - Real-time updates via events
    /// - Automatic cleanup of expired sessions
    /// </summary>
    public class GlobalArticleCacheService : IDisposable
    {
        private readonly JwtArticleService _jwtArticleService;
        private readonly JwtSessionService _jwtSessionService;
        private readonly ILogger<GlobalArticleCacheService> _logger;
        private readonly IHttpContextAccessor _httpContextAccessor;

        // Thread-safe dictionaries for per-session caching
        private readonly ConcurrentDictionary<string, List<Article>> _articlesBySession;
        private readonly ConcurrentDictionary<string, int> _unreadCountBySession;
        private readonly ConcurrentDictionary<string, DateTime> _lastRefreshTimeBySession;
        private readonly ConcurrentDictionary<string, DateTime> _lastAccessTimeBySession;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _loadingSemaphoresBySession;

        // Configuration
        private readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(5); // Shorter for real-time feel
        private readonly TimeSpan _sessionTimeout = TimeSpan.FromHours(2);
        private readonly Timer _cleanupTimer;
        private bool _disposed = false;

        // ✅ Events for real-time updates
        public event Func<List<Article>, int, Task> OnArticlesUpdated;
        public event Func<Article, Task> OnArticleAdded;
        public event Func<int, Task> OnArticleDeleted;
        public event Func<int, Task> OnArticleRead;

        public GlobalArticleCacheService(
            JwtArticleService jwtArticleService,
            JwtSessionService jwtSessionService,
            ILogger<GlobalArticleCacheService> logger,
            IHttpContextAccessor httpContextAccessor)
        {
            _jwtArticleService = jwtArticleService ?? throw new ArgumentNullException(nameof(jwtArticleService));
            _jwtSessionService = jwtSessionService ?? throw new ArgumentNullException(nameof(jwtSessionService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));

            _articlesBySession = new ConcurrentDictionary<string, List<Article>>();
            _unreadCountBySession = new ConcurrentDictionary<string, int>();
            _lastRefreshTimeBySession = new ConcurrentDictionary<string, DateTime>();
            _lastAccessTimeBySession = new ConcurrentDictionary<string, DateTime>();
            _loadingSemaphoresBySession = new ConcurrentDictionary<string, SemaphoreSlim>();

            _cleanupTimer = new Timer(CleanupExpiredSessions, null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));

            _logger.LogInformation("✅ GlobalArticleCacheService initialized");
        }

        #region Session Management

        private string GetSessionId()
        {
            try
            {
                var sessionId = _httpContextAccessor.HttpContext?.Session?.Id;

                if (string.IsNullOrEmpty(sessionId))
                {
                    _logger.LogWarning("⚠️ No session ID available, using fallback");
                    return "default";
                }

                _lastAccessTimeBySession[sessionId] = DateTime.Now;
                return sessionId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error getting session ID");
                return "default";
            }
        }

        private SemaphoreSlim GetSessionSemaphore(string sessionId)
        {
            return _loadingSemaphoresBySession.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        }

        private void CleanupExpiredSessions(object state)
        {
            try
            {
                var now = DateTime.Now;
                var expiredSessions = _lastAccessTimeBySession
                    .Where(kvp => now - kvp.Value > _sessionTimeout)
                    .Select(kvp => kvp.Key)
                    .ToList();

                if (!expiredSessions.Any())
                {
                    return;
                }

                _logger.LogInformation($"🧹 Cleaning up {expiredSessions.Count} expired article sessions");

                foreach (var sessionId in expiredSessions)
                {
                    RemoveSessionData(sessionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error during article session cleanup");
            }
        }

        private void RemoveSessionData(string sessionId)
        {
            _articlesBySession.TryRemove(sessionId, out _);
            _unreadCountBySession.TryRemove(sessionId, out _);
            _lastRefreshTimeBySession.TryRemove(sessionId, out _);
            _lastAccessTimeBySession.TryRemove(sessionId, out _);

            if (_loadingSemaphoresBySession.TryRemove(sessionId, out var semaphore))
            {
                semaphore?.Dispose();
            }
        }

        #endregion

        #region Cache Accessors

        private List<Article> GetArticlesCache()
        {
            var sessionId = GetSessionId();
            return _articlesBySession.GetOrAdd(sessionId, _ => new List<Article>());
        }

        private int GetUnreadCountCache()
        {
            var sessionId = GetSessionId();
            return _unreadCountBySession.GetOrAdd(sessionId, _ => 0);
        }

        private DateTime GetLastRefreshTime()
        {
            var sessionId = GetSessionId();
            return _lastRefreshTimeBySession.GetOrAdd(sessionId, _ => DateTime.MinValue);
        }

        private void SetLastRefreshTime(DateTime time)
        {
            var sessionId = GetSessionId();
            _lastRefreshTimeBySession[sessionId] = time;
        }

        #endregion

        #region Public Methods - Get Articles

        /// <summary>
        /// Get all articles with optional filtering by username
        /// </summary>
        public async Task<List<Article>> GetArticlesAsync(string excludeUsername = null, bool forceRefresh = false)
        {
            var sessionId = GetSessionId();
            var semaphore = GetSessionSemaphore(sessionId);

            await semaphore.WaitAsync();
            try
            {
                if (_jwtSessionService.IsTokenExpired())
                {
                    _logger.LogWarning($"⚠️ JWT token expired for session {sessionId}");
                    return new List<Article>();
                }

                var articlesCache = GetArticlesCache();
                var lastRefreshTime = GetLastRefreshTime();
                bool cacheExpired = DateTime.Now - lastRefreshTime > _cacheExpiration;

                if (!forceRefresh && !cacheExpired && articlesCache.Any())
                {
                    _logger.LogDebug($"✅ Using cached articles ({articlesCache.Count} articles) for session {sessionId}");

                    // ✅ Filter by username if needed
                    if (!string.IsNullOrEmpty(excludeUsername))
                    {
                        var filtered = articlesCache
                            .Where(a => !string.Equals(a.Username, excludeUsername, StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(a => a.Timestamp)
                            .ToList();

                        _logger.LogDebug($"📊 Filtered to {filtered.Count} articles (excluding {excludeUsername})");
                        return filtered;
                    }

                    return articlesCache.OrderByDescending(a => a.Timestamp).ToList();
                }

                _logger.LogInformation($"📥 Loading articles from JWT API for session {sessionId}...");

                var articles = await _jwtArticleService.GetAllArticlesAsync();

                if (articles != null && articles.Any())
                {
                    _articlesBySession[sessionId] = articles;
                    SetLastRefreshTime(DateTime.Now);

                    // ✅ Update unread count (total, not filtered)
                    var unreadCount = articles.Count(a => !a.IsRead);
                    _unreadCountBySession[sessionId] = unreadCount;

                    _logger.LogInformation($"✅ Loaded {articles.Count} articles for session {sessionId}");

                    // Notify subscribers with FULL list
                    await NotifyArticlesUpdated(articles, unreadCount);

                    // ✅ Filter if needed for return
                    if (!string.IsNullOrEmpty(excludeUsername))
                    {
                        var filtered = articles
                            .Where(a => !string.Equals(a.Username, excludeUsername, StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(a => a.Timestamp)
                            .ToList();

                        _logger.LogInformation($"📊 Returning {filtered.Count} articles (excluding {excludeUsername})");
                        return filtered;
                    }

                    return articles.OrderByDescending(a => a.Timestamp).ToList();
                }
                else
                {
                    _logger.LogWarning($"❌ No articles returned for session {sessionId}");
                    return GetArticlesCache();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Error loading articles for session {sessionId}");
                return GetArticlesCache();
            }
            finally
            {
                semaphore.Release();
            }
        }


        /// <summary>
        /// Get unread count from cache or API
        /// </summary>
        public async Task<int> GetUnreadCountAsync(bool forceRefresh = false)
        {
            var sessionId = GetSessionId();

            if (_jwtSessionService.IsTokenExpired())
            {
                return 0;
            }

            var lastRefreshTime = GetLastRefreshTime();
            bool cacheExpired = DateTime.Now - lastRefreshTime > _cacheExpiration;

            if (!forceRefresh && !cacheExpired)
            {
                return GetUnreadCountCache();
            }

            try
            {
                var unreadCount = await _jwtArticleService.GetUnreadCountAsync();
                _unreadCountBySession[sessionId] = unreadCount;
                return unreadCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching unread count");
                return GetUnreadCountCache();
            }
        }

        #endregion

        #region Public Methods - Modify Articles

        /// <summary>
        /// Add new article to cache and API
        /// </summary>
        public async Task<bool> AddArticleAsync(Article article)
        {
            try
            {
                var success = await _jwtArticleService.CreateArticleAsync(article);

                if (success)
                {
                    var sessionId = GetSessionId();
                    var articlesCache = GetArticlesCache();

                    articlesCache.Insert(0, article);
                    _articlesBySession[sessionId] = articlesCache;

                    // Update unread count
                    var unreadCount = articlesCache.Count(a => !a.IsRead);
                    _unreadCountBySession[sessionId] = unreadCount;

                    _logger.LogInformation($"✅ Article added to cache for session {sessionId}");

                    // Notify subscribers
                    await NotifyArticleAdded(article);
                    await NotifyArticlesUpdated(articlesCache, unreadCount);

                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding article");
                return false;
            }
        }
        public async Task<bool> UpdateArticleVisibilityAsync(int articleId, bool isVisible)
        {
            try
            {
                _logger.LogInformation($"📝 Updating article {articleId} visibility to {isVisible}");

                var success = await _jwtArticleService.UpdateVisibilityAsync(articleId, isVisible);

                if (success)
                {
                    var sessionId = GetSessionId();
                    var articlesCache = GetArticlesCache();

                    var article = articlesCache.FirstOrDefault(a => a.Id == articleId);
                    if (article != null)
                    {
                        article.IsActionVisible = isVisible;
                        _articlesBySession[sessionId] = articlesCache;

                        // Update unread count (unchanged)
                        var unreadCount = articlesCache.Count(a => !a.IsRead);

                        _logger.LogInformation($"✅ Article {articleId} visibility updated to {isVisible} in cache");

                        // Notify subscribers
                        await NotifyArticlesUpdated(articlesCache, unreadCount);
                    }
                    else
                    {
                        _logger.LogWarning($"⚠️ Article {articleId} not found in cache");
                    }

                    return true;
                }
                else
                {
                    _logger.LogWarning($"❌ Failed to update article {articleId} visibility via API");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Error updating article {articleId} visibility");
                return false;
            }
        }
        /// <summary>
        /// Delete article from cache and API
        /// </summary>
        public async Task<bool> DeleteArticleAsync(int articleId)
        {
            try
            {
                var success = await _jwtArticleService.DeleteArticleAsync(articleId);

                if (success)
                {
                    var sessionId = GetSessionId();
                    var articlesCache = GetArticlesCache();

                    var article = articlesCache.FirstOrDefault(a => a.Id == articleId);
                    if (article != null)
                    {
                        articlesCache.Remove(article);
                        _articlesBySession[sessionId] = articlesCache;

                        // Update unread count
                        var unreadCount = articlesCache.Count(a => !a.IsRead);
                        _unreadCountBySession[sessionId] = unreadCount;

                        _logger.LogInformation($"✅ Article {articleId} deleted from cache");

                        // Notify subscribers
                        await NotifyArticleDeleted(articleId);
                        await NotifyArticlesUpdated(articlesCache, unreadCount);
                    }

                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error deleting article {articleId}");
                return false;
            }
        }

        /// <summary>
        /// Mark article as read in cache and API
        /// </summary>
        public async Task<bool> MarkAsReadAsync(int articleId)
        {
            try
            {
                var success = await _jwtArticleService.MarkAsReadAsync(articleId);

                if (success)
                {
                    var sessionId = GetSessionId();
                    var articlesCache = GetArticlesCache();

                    var article = articlesCache.FirstOrDefault(a => a.Id == articleId);
                    if (article != null)
                    {
                        article.IsRead = true;

                        // Update unread count
                        var unreadCount = articlesCache.Count(a => !a.IsRead);
                        _unreadCountBySession[sessionId] = unreadCount;

                        _logger.LogInformation($"✅ Article {articleId} marked as read");

                        // Notify subscribers
                        await NotifyArticleRead(articleId);
                        await NotifyArticlesUpdated(articlesCache, unreadCount);
                    }

                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error marking article {articleId} as read");
                return false;
            }
        }

        /// <summary>
        /// Clear all articles from cache and API
        /// </summary>
        public async Task<bool> ClearAllArticlesAsync()
        {
            try
            {
                var sessionId = GetSessionId();
                var articlesCache = GetArticlesCache();

                // Delete all from API
                var deleteSuccesses = new List<bool>();
                foreach (var article in articlesCache.ToList())
                {
                    var success = await _jwtArticleService.DeleteArticleAsync(article.Id);
                    deleteSuccesses.Add(success);
                }

                if (deleteSuccesses.All(s => s))
                {
                    articlesCache.Clear();
                    _articlesBySession[sessionId] = articlesCache;
                    _unreadCountBySession[sessionId] = 0;

                    _logger.LogInformation("✅ All articles cleared");

                    // Notify subscribers
                    await NotifyArticlesUpdated(articlesCache, 0);

                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing all articles");
                return false;
            }
        }

        #endregion

        #region Event Notifications

        private async Task NotifyArticlesUpdated(List<Article> articles, int unreadCount)
        {
            if (OnArticlesUpdated != null)
            {
                try
                {
                    await OnArticlesUpdated.Invoke(articles, unreadCount);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error notifying articles updated");
                }
            }
        }

        private async Task NotifyArticleAdded(Article article)
        {
            if (OnArticleAdded != null)
            {
                try
                {
                    await OnArticleAdded.Invoke(article);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error notifying article added");
                }
            }
        }

        private async Task NotifyArticleDeleted(int articleId)
        {
            if (OnArticleDeleted != null)
            {
                try
                {
                    await OnArticleDeleted.Invoke(articleId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error notifying article deleted");
                }
            }
        }

        private async Task NotifyArticleRead(int articleId)
        {
            if (OnArticleRead != null)
            {
                try
                {
                    await OnArticleRead.Invoke(articleId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error notifying article read");
                }
            }
        }

        #endregion

        #region Cache Management

        public void ClearCache()
        {
            var sessionId = GetSessionId();
            _logger.LogInformation($"🧹 Clearing article cache for session {sessionId}");
            RemoveSessionData(sessionId);
        }

        public void ClearAllCaches()
        {
            _logger.LogInformation("🧹 Clearing ALL article session caches");

            foreach (var kvp in _loadingSemaphoresBySession)
            {
                kvp.Value?.Dispose();
            }

            _articlesBySession.Clear();
            _unreadCountBySession.Clear();
            _lastRefreshTimeBySession.Clear();
            _lastAccessTimeBySession.Clear();
            _loadingSemaphoresBySession.Clear();

            _logger.LogInformation("✅ All article session caches cleared");
        }

        public async Task RefreshArticlesAsync()
        {
            try
            {
                if (_jwtSessionService.IsTokenExpired())
                {
                    return;
                }

                await GetArticlesAsync(forceRefresh: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in RefreshArticlesAsync");
            }
        }

        #endregion

        #region Statistics

        public CacheStatistics GetCacheStatistics()
        {
            return new CacheStatistics
            {
                ActiveSessions = _articlesBySession.Count,
                TotalCachedArticles = _articlesBySession.Values.Sum(cache => cache.Count),
                TotalUnreadArticles = _articlesBySession.Values.Sum(cache => cache.Count(a => !a.IsRead)),
                OldestCacheAge = _lastRefreshTimeBySession.Values.Any()
                    ? DateTime.Now - _lastRefreshTimeBySession.Values.Min()
                    : TimeSpan.Zero
            };
        }

        public class CacheStatistics
        {
            public int ActiveSessions { get; set; }
            public int TotalCachedArticles { get; set; }
            public int TotalUnreadArticles { get; set; }
            public TimeSpan OldestCacheAge { get; set; }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                try
                {
                    _logger.LogInformation("🧹 Disposing GlobalArticleCacheService");

                    _cleanupTimer?.Dispose();

                    foreach (var kvp in _loadingSemaphoresBySession)
                    {
                        try
                        {
                            kvp.Value?.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Error disposing semaphore for session {kvp.Key}");
                        }
                    }

                    ClearAllCaches();

                    _logger.LogInformation("✅ GlobalArticleCacheService disposed successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during GlobalArticleCacheService disposal");
                }
            }

            _disposed = true;
        }

        #endregion
    }
}