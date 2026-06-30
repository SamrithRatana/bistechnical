using ServiceMaintenance.Configuration;
using ServiceMaintenance.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.Text.Json.Serialization;

namespace ServiceMaintenance.Services.BISServices
{
    public class CustomerService
    {
        private readonly HttpClient _httpClient;
        private static Dictionary<string, Customer> _customerCache = new Dictionary<string, Customer>();
        private static DateTime _cacheExpiration = DateTime.MinValue;
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

        // ✅ NEW: CustomerType cache
        private static List<CustomerTypeDto> _customerTypesCache = new List<CustomerTypeDto>();
        private static DateTime _customerTypesCacheExpiration = DateTime.MinValue;
        private static readonly TimeSpan CustomerTypesCacheDuration = TimeSpan.FromMinutes(30);

        public CustomerService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        // ✅ NEW: Helper method to build Customer API URLs
        private string GetFullUrl(string endpoint)
        {
            return ApiConfiguration.BuildCustomerApiUrl(endpoint);
        }

        #region Customer Methods (Existing)

        /// <summary>
        /// ✅ OPTIMIZED: Get paginated customers - DEFAULT TO 10 items per page
        /// </summary>
        public async Task<PagedResponse<Customer>> GetCustomersPaginatedAsync(
            int pageNumber = 1,
            int pageSize = 10,
            string searchTerm = null,
            bool? isActive = null)
        {
            try
            {
                // ✅ Build URL using centralized config
                var endpoint = $"/api/Customer?pageNumber={pageNumber}&pageSize={pageSize}";

                if (!string.IsNullOrEmpty(searchTerm))
                {
                    // ✅ IMPORTANT: URL encode for Unicode/Khmer characters
                    endpoint += $"&searchTerm={Uri.EscapeDataString(searchTerm)}";
                }

                if (isActive.HasValue)
                    endpoint += $"&isActive={isActive.Value}";

                var url = GetFullUrl(endpoint);
                Console.WriteLine($"📡 API Request: {url}");

                var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                var response = await _httpClient.GetAsync(url, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"📥 Response length: {responseContent.Length} characters");

                    // ✅ IMPORTANT: Case-insensitive JSON deserialization
                    var options = new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                    };

                    var pagedResponse = await response.Content.ReadFromJsonAsync<PagedResponse<Customer>>(options, cts.Token);

                    if (pagedResponse?.Data != null)
                    {
                        // Filter out customers with empty company names
                        var filteredData = pagedResponse.Data
                            .Where(c => !string.IsNullOrWhiteSpace(c.CompanyName))
                            .ToList();

                        Console.WriteLine($"✅ Successfully loaded {filteredData.Count} customers");

                        // Update cache
                        foreach (var customer in filteredData)
                        {
                            if (!_customerCache.ContainsKey(customer.CompanyName))
                            {
                                _customerCache[customer.CompanyName] = customer;
                            }
                        }
                        _cacheExpiration = DateTime.Now.Add(CacheDuration);

                        return new PagedResponse<Customer>(
                            filteredData,
                            pagedResponse.PageNumber,
                            pagedResponse.PageSize,
                            pagedResponse.TotalRecords
                        );
                    }
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"❌ API Error - Status: {response.StatusCode}");
                    Console.WriteLine($"❌ Error Content: {errorContent}");
                }

                return new PagedResponse<Customer>(new List<Customer>(), pageNumber, pageSize, 0);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("⏱️ Request timeout");
                return new PagedResponse<Customer>(new List<Customer>(), pageNumber, pageSize, 0);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Exception: {ex.Message}");
                return new PagedResponse<Customer>(new List<Customer>(), pageNumber, pageSize, 0);
            }
        }

        [Obsolete("Use GetCustomersPaginatedAsync for better performance")]
        public async Task<List<Customer>> GetCustomersAsync()
        {
            try
            {
                if (_customerCache.Any() && DateTime.Now < _cacheExpiration)
                {
                    Console.WriteLine("Returning customers from cache");
                    return _customerCache.Values.ToList();
                }

                Console.WriteLine("⚠️ WARNING: Loading ALL customers - consider using GetCustomersPaginatedAsync");
                Console.WriteLine("Fetching customers from API...");

                // ✅ Use centralized config
                var url = GetFullUrl("/api/customer/");

                var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
                var response = await _httpClient.GetAsync(url, cts.Token);

                Console.WriteLine($"API Response Status: {response.StatusCode}");

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Response Content Length: {responseContent?.Length ?? 0}");

                    if (string.IsNullOrWhiteSpace(responseContent))
                    {
                        Console.WriteLine("API returned empty response");
                        return _customerCache.Any() ? _customerCache.Values.ToList() : new List<Customer>();
                    }

                    var customers = await response.Content.ReadFromJsonAsync<List<Customer>>(cancellationToken: cts.Token);

                    if (customers != null && customers.Any())
                    {
                        _customerCache = customers
                            .Where(c => !string.IsNullOrWhiteSpace(c.CompanyName))
                            .ToDictionary(c => c.CompanyName, c => c, StringComparer.OrdinalIgnoreCase);

                        _cacheExpiration = DateTime.Now.Add(CacheDuration);
                        Console.WriteLine($"Loaded {customers.Count} customers from API and cached {_customerCache.Count}");

                        return customers;
                    }
                    else
                    {
                        Console.WriteLine("API returned null or empty customer list");
                        return _customerCache.Any() ? _customerCache.Values.ToList() : new List<Customer>();
                    }
                }
                else
                {
                    Console.WriteLine($"Failed to load customers. Status: {response.StatusCode}");
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Error Content: {errorContent}");

                    if (_customerCache.Any())
                    {
                        Console.WriteLine("Using expired cache due to API failure");
                        return _customerCache.Values.ToList();
                    }

                    return new List<Customer>();
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Request timeout while loading all customers");
                return _customerCache.Any() ? _customerCache.Values.ToList() : new List<Customer>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading customers: {ex.Message}");

                if (_customerCache.Any())
                {
                    Console.WriteLine("Using cache due to exception");
                    return _customerCache.Values.ToList();
                }

                return new List<Customer>();
            }
        }

        public async Task<string> GetCustomerTypeByCompanyName(string companyName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(companyName))
                {
                    Console.WriteLine("GetCustomerTypeByCompanyName: Company name is null or empty");
                    return string.Empty;
                }

                if (_customerCache.Any() && DateTime.Now < _cacheExpiration)
                {
                    if (_customerCache.TryGetValue(companyName, out var cachedCustomer))
                    {
                        Console.WriteLine($"Found customer type in cache for: {companyName}");
                        return cachedCustomer?.CustomerType ?? string.Empty;
                    }
                }

                var pagedResponse = await GetCustomersPaginatedAsync(
                    pageNumber: 1,
                    pageSize: 20,
                    searchTerm: companyName
                );

                if (pagedResponse?.Data != null)
                {
                    var customer = pagedResponse.Data.FirstOrDefault(c =>
                        c.CompanyName.Equals(companyName, StringComparison.OrdinalIgnoreCase));

                    if (customer != null)
                    {
                        Console.WriteLine($"Found customer type: {customer.CustomerType} for {companyName}");
                        return customer.CustomerType ?? string.Empty;
                    }
                }

                Console.WriteLine($"Customer not found: {companyName}");
                return string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting customer type: {ex.Message}");
                return string.Empty;
            }
        }

        public async Task<Customer> GetCustomerByCompanyName(string companyName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(companyName))
                {
                    Console.WriteLine("GetCustomerByCompanyName: Company name is null or empty");
                    return null;
                }

                if (_customerCache.Any() && DateTime.Now < _cacheExpiration)
                {
                    if (_customerCache.TryGetValue(companyName, out var cachedCustomer))
                    {
                        Console.WriteLine($"Found customer in cache: {companyName}");
                        return cachedCustomer;
                    }
                }

                var pagedResponse = await GetCustomersPaginatedAsync(
                    pageNumber: 1,
                    pageSize: 20,
                    searchTerm: companyName
                );

                if (pagedResponse?.Data != null)
                {
                    var customer = pagedResponse.Data.FirstOrDefault(c =>
                        c.CompanyName.Equals(companyName, StringComparison.OrdinalIgnoreCase));

                    if (customer != null)
                    {
                        Console.WriteLine($"Found customer: {companyName}");

                        if (!_customerCache.ContainsKey(companyName))
                        {
                            _customerCache[companyName] = customer;
                        }

                        return customer;
                    }
                }

                Console.WriteLine($"Customer not found: {companyName}");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting customer: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region CustomerType Methods (NEW)

        /// <summary>
        /// ✅ NEW: Get all customer types with caching (uses dedicated CustomerType API)
        /// Much faster than loading customers - only 78 types vs 1000+ customers!
        /// </summary>
        public async Task<List<CustomerTypeDto>> GetAllCustomerTypesAsync(bool forceRefresh = false)
        {
            try
            {
                // Check cache first
                if (!forceRefresh &&
                    _customerTypesCache.Any() &&
                    DateTime.Now < _customerTypesCacheExpiration)
                {
                    Console.WriteLine($"✅ Returning {_customerTypesCache.Count} customer types from cache");
                    return _customerTypesCache.Where(ct => ct.IsActive).ToList();
                }

                Console.WriteLine("🔄 Loading customer types from dedicated API...");

                var allTypes = new List<CustomerTypeDto>();
                int pageNumber = 1;
                int pageSize = 100;
                bool hasMore = true;

                // Load all pages (usually just 1 page for 78 records)
                while (hasMore)
                {
                    var pagedResponse = await GetCustomerTypesPaginatedAsync(pageNumber, pageSize);

                    if (pagedResponse?.Data != null && pagedResponse.Data.Any())
                    {
                        allTypes.AddRange(pagedResponse.Data);
                        hasMore = pagedResponse.HasNext;
                        pageNumber++;
                    }
                    else
                    {
                        hasMore = false;
                    }
                }

                // Update cache
                _customerTypesCache = allTypes;
                _customerTypesCacheExpiration = DateTime.Now.Add(CustomerTypesCacheDuration);

                Console.WriteLine($"✅ Loaded and cached {allTypes.Count} customer types (expires in 30 min)");

                return allTypes.Where(ct => ct.IsActive).ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error loading customer types: {ex.Message}");

                // Return cache if available, even if expired
                return _customerTypesCache.Where(ct => ct.IsActive).ToList();
            }
        }

        /// <summary>
        /// ✅ NEW: Get paginated customer types from dedicated API
        /// </summary>
        public async Task<PagedResponse<CustomerTypeDto>> GetCustomerTypesPaginatedAsync(
            int pageNumber = 1,
            int pageSize = 100,
            string searchTerm = null,
            bool? isActive = true)
        {
            try
            {
                // ✅ Use centralized config
                var endpoint = $"/api/CustomerType?pageNumber={pageNumber}&pageSize={pageSize}";

                if (!string.IsNullOrEmpty(searchTerm))
                    endpoint += $"&searchTerm={Uri.EscapeDataString(searchTerm)}";

                if (isActive.HasValue)
                    endpoint += $"&isActive={isActive.Value}";

                var url = GetFullUrl(endpoint);

                var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
                var response = await _httpClient.GetAsync(url, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    var pagedResponse = await response.Content.ReadFromJsonAsync<PagedResponse<CustomerTypeDto>>(cancellationToken: cts.Token);
                    return pagedResponse ?? new PagedResponse<CustomerTypeDto>(new List<CustomerTypeDto>(), pageNumber, pageSize, 0);
                }

                Console.WriteLine($"Failed to load customer types. Status: {response.StatusCode}");
                return new PagedResponse<CustomerTypeDto>(new List<CustomerTypeDto>(), pageNumber, pageSize, 0);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading customer types: {ex.Message}");
                return new PagedResponse<CustomerTypeDto>(new List<CustomerTypeDto>(), pageNumber, pageSize, 0);
            }
        }

        #endregion

        #region Cache Management

        /// <summary>
        /// Clear customer cache only
        /// </summary>
        public void ClearCache()
        {
            _customerCache.Clear();
            _cacheExpiration = DateTime.MinValue;
            Console.WriteLine("Customer cache cleared");
        }

        /// <summary>
        /// ✅ NEW: Clear all caches (customers + customer types)
        /// </summary>
        public void ClearAllCaches()
        {
            _customerCache.Clear();
            _cacheExpiration = DateTime.MinValue;
            _customerTypesCache.Clear();
            _customerTypesCacheExpiration = DateTime.MinValue;
            Console.WriteLine("All caches cleared (customers + customer types)");
        }

        #endregion

        #region DTOs and Models

        public class PagedResponse<T>
        {
            public int PageNumber { get; set; }
            public int PageSize { get; set; }
            public int TotalPages { get; set; }
            public int TotalRecords { get; set; }
            public IEnumerable<T> Data { get; set; }

            public bool HasPrevious => PageNumber > 1;
            public bool HasNext => PageNumber < TotalPages;

            public PagedResponse()
            {
                Data = new List<T>();
            }

            public PagedResponse(IEnumerable<T> data, int pageNumber, int pageSize, int totalRecords)
            {
                Data = data ?? new List<T>();
                PageNumber = pageNumber;
                PageSize = pageSize;
                TotalRecords = totalRecords;
                TotalPages = totalRecords > 0 ? (int)Math.Ceiling(totalRecords / (double)pageSize) : 0;
            }
        }

        /// <summary>
        /// ✅ NEW: DTO for CustomerType API response
        /// Matches the structure from https://customerapi.koompi.cloud/api/CustomerType
        /// </summary>
        public class CustomerTypeDto
        {
            [JsonPropertyName("listId")]
            public int ListId { get; set; }

            [JsonPropertyName("parentListId")]
            public int? ParentListId { get; set; }

            [JsonPropertyName("createdBy")]
            public string CreatedBy { get; set; }

            [JsonPropertyName("createdAt")]
            public DateTime? CreatedAt { get; set; }

            [JsonPropertyName("modifiedBy")]
            public string ModifiedBy { get; set; }

            [JsonPropertyName("modifiedAt")]
            public DateTime? ModifiedAt { get; set; }

            [JsonPropertyName("type")]
            public string Type { get; set; }

            [JsonPropertyName("description")]
            public string Description { get; set; }

            [JsonPropertyName("isActive")]
            public bool IsActive { get; set; }
        }

        #endregion
    }
}