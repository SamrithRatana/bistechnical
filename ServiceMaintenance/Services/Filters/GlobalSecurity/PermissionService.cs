using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;

namespace ServiceMaintenance.Services.Filters.GlobalSecurity
{
    /// <summary>
    /// Service for checking user permissions from JWT claims
    /// </summary>
    public class PermissionService
    {
        private readonly AuthenticationStateProvider _authenticationStateProvider;
        private readonly ILogger<PermissionService> _logger;

        public PermissionService(
            AuthenticationStateProvider authenticationStateProvider,
            ILogger<PermissionService> logger)
        {
            _authenticationStateProvider = authenticationStateProvider;
            _logger = logger;
        }

        /// <summary>
        /// Check if user has access to a specific module
        /// </summary>
        public async Task<bool> CheckModuleAccessAsync(string moduleName)
        {
            try
            {
                var authState = await _authenticationStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                if (user?.Identity?.IsAuthenticated != true)
                {
                    _logger.LogWarning("User is not authenticated");
                    return false;
                }

                // Check for Access permission
                var requiredPermission = $"Permissions.{moduleName}.Access";

                _logger.LogInformation($"🔍 Checking permission: {requiredPermission}");

                // ⭐ FIX: Get ALL claims with type "Permission"
                var permissionClaims = user.FindAll("Permission").ToList();

                _logger.LogInformation($"📋 Found {permissionClaims.Count} permission claims");

                foreach (var claim in permissionClaims)
                {
                    _logger.LogInformation($"  - {claim.Value}");

                    // Check if this claim value contains our required permission
                    if (claim.Value.Contains(requiredPermission))
                    {
                        _logger.LogInformation($"✅ Permission found: {requiredPermission}");
                        return true;
                    }
                }

                _logger.LogWarning($"❌ Permission NOT found: {requiredPermission}");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error checking module access for {moduleName}");
                return false;
            }
        }

        /// <summary>
        /// Check multiple permissions for a module and return dictionary
        /// </summary>
        public async Task<Dictionary<string, bool>> CheckModulePermissionsAsync(string moduleName)
        {
            var permissions = new Dictionary<string, bool>();
            var permissionTypes = new[] { "View", "Create", "Edit", "Delete", "Print", "Export" };

            try
            {
                var authState = await _authenticationStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                if (user?.Identity?.IsAuthenticated != true)
                {
                    _logger.LogWarning("User is not authenticated");
                    foreach (var type in permissionTypes)
                    {
                        permissions[type] = false;
                    }
                    return permissions;
                }

                // ⭐ FIX: Get ALL permission claims once
                var permissionClaims = user.FindAll("Permission")
                    .SelectMany(c => c.Value.Split(','))
                    .Select(p => p.Trim())
                    .ToHashSet();

                _logger.LogInformation($"🔍 Checking permissions for module: {moduleName}");
                _logger.LogInformation($"📋 Total permission values found: {permissionClaims.Count}");

                // Check each permission type
                foreach (var type in permissionTypes)
                {
                    var requiredPermission = $"Permissions.{moduleName}.{type}";
                    var hasPermission = permissionClaims.Contains(requiredPermission);

                    permissions[type] = hasPermission;

                    _logger.LogInformation($"  {type}: {(hasPermission ? "✅" : "❌")} ({requiredPermission})");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error checking permissions for module {moduleName}");
                foreach (var type in permissionTypes)
                {
                    permissions[type] = false;
                }
            }

            return permissions;
        }

        /// <summary>
        /// Check if user has a specific permission
        /// </summary>
        public async Task<bool> HasPermissionAsync(string permission)
        {
            try
            {
                var authState = await _authenticationStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                if (user?.Identity?.IsAuthenticated != true)
                {
                    return false;
                }

                // ⭐ FIX: Check all Permission claims
                var permissionClaims = user.FindAll("Permission")
                    .SelectMany(c => c.Value.Split(','))
                    .Select(p => p.Trim())
                    .ToHashSet();

                return permissionClaims.Contains(permission);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error checking permission: {permission}");
                return false;
            }
        }

        /// <summary>
        /// Get all permissions for the current user
        /// </summary>
        public async Task<List<string>> GetUserPermissionsAsync()
        {
            try
            {
                var authState = await _authenticationStateProvider.GetAuthenticationStateAsync();
                var user = authState.User;

                if (user?.Identity?.IsAuthenticated != true)
                {
                    return new List<string>();
                }

                // ⭐ FIX: Parse all Permission claims
                var permissions = user.FindAll("Permission")
                    .SelectMany(c => c.Value.Split(','))
                    .Select(p => p.Trim())
                    .Distinct()
                    .ToList();

                _logger.LogInformation($"📋 User has {permissions.Count} total permissions");

                return permissions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting user permissions");
                return new List<string>();
            }
        }

        /// <summary>
        /// Check if user has any of the specified permissions
        /// </summary>
        public async Task<bool> HasAnyPermissionAsync(params string[] permissions)
        {
            var userPermissions = await GetUserPermissionsAsync();
            return permissions.Any(p => userPermissions.Contains(p));
        }

        /// <summary>
        /// Check if user has all of the specified permissions
        /// </summary>
        public async Task<bool> HasAllPermissionsAsync(params string[] permissions)
        {
            var userPermissions = await GetUserPermissionsAsync();
            return permissions.All(p => userPermissions.Contains(p));
        }
    }
}