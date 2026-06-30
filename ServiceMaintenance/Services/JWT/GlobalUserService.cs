using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceMaintenance.Services.JWT
{
    /// <summary>
    /// ✅ OPTIMIZED: Efficient User Service with Memory Caching
    /// Caches users for 30 minutes, supports concurrent access
    /// </summary>
    public class GlobalUserService
    {
        private readonly JwtUserManagementService _jwtUserManagementService;
        private readonly ILogger<GlobalUserService> _logger;
        private readonly IMemoryCache _cache;
        private readonly SemaphoreSlim _loadLock = new SemaphoreSlim(1, 1);

        private const string USERS_CACHE_KEY = "global_users_dict";
        private const string USERS_DETAILS_CACHE_KEY = "global_users_details";
        private const int CACHE_DURATION_MINUTES = 30;

        public GlobalUserService(
            JwtUserManagementService jwtUserManagementService,
            ILogger<GlobalUserService> logger,
            IMemoryCache cache)
        {
            _jwtUserManagementService = jwtUserManagementService ?? throw new ArgumentNullException(nameof(jwtUserManagementService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        public class UserFullInfo
        {
            public string Id { get; set; }
            public string UserName { get; set; }
            public string Email { get; set; }
            public string FullName { get; set; }
            public string FirstName { get; set; }
            public string LastName { get; set; }
            public string PhoneNumber { get; set; }
            public List<string> Roles { get; set; } = new();
            public bool EmailConfirmed { get; set; }
            public bool IsLocked { get; set; }
        }

        /// <summary>
        /// ✅ Get all users as dictionary - uses cache
        /// </summary>
        public async Task<Dictionary<string, string>> GetUsersAsync(bool forceRefresh = false)
        {
            if (!forceRefresh && _cache.TryGetValue(USERS_CACHE_KEY, out Dictionary<string, string> cached))
            {
                _logger.LogDebug($"✅ Returning {cached.Count} users from cache");
                return cached;
            }

            await _loadLock.WaitAsync();
            try
            {
                // Double-check after acquiring lock
                if (!forceRefresh && _cache.TryGetValue(USERS_CACHE_KEY, out cached))
                {
                    return cached;
                }

                _logger.LogInformation("📥 Loading users from API...");

                var allUsers = new List<UserDto>();
                int currentPage = 1;
                int pageSize = 100;
                bool hasMorePages = true;

                while (hasMorePages)
                {
                    var usersResponse = await _jwtUserManagementService.GetAllUsersAsync(
                        page: currentPage,
                        pageSize: pageSize
                    );

                    if (usersResponse?.Status == "Success" && usersResponse.Data != null && usersResponse.Data.Any())
                    {
                        allUsers.AddRange(usersResponse.Data);
                        _logger.LogDebug($"Loaded {usersResponse.Data.Count} users from page {currentPage}");
                        hasMorePages = usersResponse.Pagination?.HasNext ?? false;
                        currentPage++;
                    }
                    else
                    {
                        hasMorePages = false;
                    }
                }

                if (allUsers.Any())
                {
                    var userDictionary = allUsers.ToDictionary(
                        u => u.Id,
                        u => $"{u.FirstName} {u.LastName}".Trim()
                    );

                    // ✅ Cache for 30 minutes
                    _cache.Set(USERS_CACHE_KEY, userDictionary, TimeSpan.FromMinutes(CACHE_DURATION_MINUTES));

                    _logger.LogInformation($"✅ Loaded and cached {userDictionary.Count} users");
                    return userDictionary;
                }
                else
                {
                    _logger.LogWarning("No users loaded from API");
                    return new Dictionary<string, string>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading users from API");
                return new Dictionary<string, string>();
            }
            finally
            {
                _loadLock.Release();
            }
        }

        /// <summary>
        /// ✅ Get all users with full details - uses cache
        /// </summary>
        public async Task<Dictionary<string, UserFullInfo>> GetUsersWithDetailsAsync(bool forceRefresh = false)
        {
            if (!forceRefresh && _cache.TryGetValue(USERS_DETAILS_CACHE_KEY, out Dictionary<string, UserFullInfo> cached))
            {
                _logger.LogDebug($"✅ Returning {cached.Count} user details from cache");
                return cached;
            }

            await _loadLock.WaitAsync();
            try
            {
                // Double-check after acquiring lock
                if (!forceRefresh && _cache.TryGetValue(USERS_DETAILS_CACHE_KEY, out cached))
                {
                    return cached;
                }

                _logger.LogInformation("📥 Loading user details from API...");

                var allUsers = new List<UserDto>();
                int currentPage = 1;
                int pageSize = 100;
                bool hasMorePages = true;

                while (hasMorePages)
                {
                    var usersResponse = await _jwtUserManagementService.GetAllUsersAsync(
                        page: currentPage,
                        pageSize: pageSize
                    );

                    if (usersResponse?.Status == "Success" && usersResponse.Data != null && usersResponse.Data.Any())
                    {
                        allUsers.AddRange(usersResponse.Data);
                        hasMorePages = usersResponse.Pagination?.HasNext ?? false;
                        currentPage++;
                    }
                    else
                    {
                        hasMorePages = false;
                    }
                }

                if (allUsers.Any())
                {
                    var userDetails = allUsers.ToDictionary(
                        u => u.Id,
                        u => new UserFullInfo
                        {
                            Id = u.Id,
                            UserName = u.UserName,
                            Email = u.Email,
                            FullName = $"{u.FirstName} {u.LastName}".Trim(),
                            FirstName = u.FirstName,
                            LastName = u.LastName,
                            PhoneNumber = u.PhoneNumber ?? "N/A",
                            Roles = u.Roles ?? new List<string>(),
                            EmailConfirmed = u.EmailConfirmed,
                            IsLocked = u.IsLocked
                        }
                    );

                    // ✅ Cache for 30 minutes
                    _cache.Set(USERS_DETAILS_CACHE_KEY, userDetails, TimeSpan.FromMinutes(CACHE_DURATION_MINUTES));

                    _logger.LogInformation($"✅ Loaded and cached {userDetails.Count} user details");
                    return userDetails;
                }
                else
                {
                    _logger.LogWarning("No users loaded from API");
                    return new Dictionary<string, UserFullInfo>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading user details from API");
                return new Dictionary<string, UserFullInfo>();
            }
            finally
            {
                _loadLock.Release();
            }
        }

        /// <summary>
        /// ✅ OPTIMIZED: Get user name by ID - uses cache
        /// </summary>
        public async Task<string> GetUserNameByIdAsync(Guid? userId)
        {
            if (!userId.HasValue || userId.Value == Guid.Empty)
            {
                return "Unknown User";
            }

            try
            {
                string userIdString = userId.Value.ToString();

                // ✅ Try cache first
                var users = await GetUsersAsync(forceRefresh: false);

                if (users.TryGetValue(userIdString, out string userName))
                {
                    return userName;
                }

                // ✅ Cache miss - load individual user
                _logger.LogDebug($"Cache miss for user {userIdString}, loading from API");

                var userResponse = await _jwtUserManagementService.GetUserByIdAsync(userIdString);

                if (userResponse?.Status == "Success" && userResponse.Data != null)
                {
                    var name = $"{userResponse.Data.FirstName} {userResponse.Data.LastName}".Trim();

                    // ✅ Update cache with this user
                    users[userIdString] = name;
                    _cache.Set(USERS_CACHE_KEY, users, TimeSpan.FromMinutes(CACHE_DURATION_MINUTES));

                    return name;
                }

                _logger.LogWarning($"User not found: {userIdString}");
                return "Unknown User";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting user name for {userId}");
                return "Error";
            }
        }

        /// <summary>
        /// Synchronous version - for compatibility
        /// </summary>
        public string GetUserNameById(Guid? userId)
        {
            return GetUserNameByIdAsync(userId).GetAwaiter().GetResult();
        }

        /// <summary>
        /// ✅ Get user phone number - uses cache
        /// </summary>
        public async Task<string> GetUserPhoneNumberByIdAsync(Guid? userId, bool autoLoad = true)
        {
            if (!userId.HasValue || userId.Value == Guid.Empty)
            {
                return "N/A";
            }

            try
            {
                string userIdString = userId.Value.ToString();

                // ✅ Try cache first
                var usersDetails = await GetUsersWithDetailsAsync(forceRefresh: false);

                if (usersDetails.TryGetValue(userIdString, out var userInfo))
                {
                    return userInfo.PhoneNumber ?? "N/A";
                }

                // Cache miss - load from API
                var userResponse = await _jwtUserManagementService.GetUserByIdAsync(userIdString);

                if (userResponse?.Status == "Success" && userResponse.Data != null)
                {
                    return userResponse.Data.PhoneNumber ?? "N/A";
                }

                return "N/A";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting phone number for {userId}");
                return "Error";
            }
        }

        /// <summary>
        /// ✅ Refresh cache - loads fresh data
        /// </summary>
        public async Task RefreshUsersAsync()
        {
            _logger.LogInformation("🔄 Refreshing user cache...");
            await GetUsersAsync(forceRefresh: true);
            await GetUsersWithDetailsAsync(forceRefresh: true);
            _logger.LogInformation("✅ User cache refreshed");
        }

        /// <summary>
        /// ✅ Clear cache - forces reload on next access
        /// </summary>
        public void ClearCache()
        {
            _cache.Remove(USERS_CACHE_KEY);
            _cache.Remove(USERS_DETAILS_CACHE_KEY);
            _logger.LogInformation("🗑️ User cache cleared");
        }
    }
}