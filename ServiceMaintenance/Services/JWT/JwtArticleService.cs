// Services/JwtArticleService.cs
using Microsoft.Extensions.Logging;
using ServiceMaintenance.Models;
using System.Net.Http.Json;

namespace ServiceMaintenance.Services.JWT
{
    public class JwtArticleService
    {
        private readonly JwtHttpClientService _jwtHttpClient;
        private readonly ILogger<JwtArticleService> _logger;

        public JwtArticleService(
            JwtHttpClientService jwtHttpClient,
            ILogger<JwtArticleService> logger)
        {
            _jwtHttpClient = jwtHttpClient;
            _logger = logger;
        }

        // GET /api/Article - Get all articles
        public async Task<List<Article>> GetAllArticlesAsync()
        {
            try
            {
                var response = await _jwtHttpClient.GetAsync("api/Article");

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<ArticleResponse>();
                    return result?.Data ?? new List<Article>();
                }

                _logger.LogWarning("Failed to fetch articles");
                return new List<Article>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching articles");
                return new List<Article>();
            }
        }

        // GET /api/Article/user/{username} - Get articles for specific user
        public async Task<List<Article>> GetArticlesByUserAsync(string username)
        {
            try
            {
                var response = await _jwtHttpClient.GetAsync($"api/Article/user/{username}");

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<ArticleResponse>();
                    return result?.Data ?? new List<Article>();
                }

                return new List<Article>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching user articles");
                return new List<Article>();
            }
        }

        // GET /api/Article/unread/count - Get unread count
        public async Task<int> GetUnreadCountAsync()
        {
            try
            {
                var response = await _jwtHttpClient.GetAsync("api/Article/unread/count");

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<UnreadCountResponse>();
                    return result?.Count ?? 0;
                }

                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching unread count");
                return 0;
            }
        }

        // POST /api/Article - Create article
        public async Task<bool> CreateArticleAsync(Article article)
        {
            try
            {
                var response = await _jwtHttpClient.PostAsync(
                    "api/Article",
                    JsonContent.Create(article)
                );

                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating article");
                return false;
            }
        }

        // DELETE /api/Article/{id} - Delete article
        public async Task<bool> DeleteArticleAsync(int id)
        {
            try
            {
                var response = await _jwtHttpClient.DeleteAsync($"api/Article/{id}");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error deleting article {id}");
                return false;
            }
        }

        // PUT /api/Article/{id}/read - Mark as read
        public async Task<bool> MarkAsReadAsync(int id)
        {
            try
            {
                var response = await _jwtHttpClient.PutAsync($"api/Article/{id}/read", null);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error marking article {id} as read");
                return false;
            }
        }

        // ✅ FIXED: PUT /api/Article/visibility - Update visibility
        // Based on API docs, payload should include BOTH ReportId and IsVisible
        public async Task<bool> UpdateVisibilityAsync(int articleId, bool isVisible)
        {
            try
            {
                // ✅ Include articleId as "ReportId" in payload (check your API's expected property name)
                var payload = new
                {
                    ReportId = articleId,  // Try "ReportId" first
                    IsVisible = isVisible
                };

                _logger.LogInformation($"📝 Updating article {articleId} visibility to {isVisible}");

                var response = await _jwtHttpClient.PutAsync(
                    "api/Article/visibility",
                    JsonContent.Create(payload)
                );

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation($"✅ Article {articleId} visibility updated successfully");
                    return true;
                }

                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning($"❌ Failed to update visibility: {response.StatusCode} - {errorContent}");

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Error updating article {articleId} visibility");
                return false;
            }
        }
    }

    // Response models
    public class ArticleResponse
    {
        public string Status { get; set; }
        public List<Article> Data { get; set; }
    }

  
}