using ServiceMaintenance.Models;
using ServiceMaintenance.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace ServiceMaintenance.Services.BISServices
{
    // ✅ DateTime converter — prevents UTC auto-conversion (+7 shift)
    public class UnspecifiedDateTimeConverter : System.Text.Json.Serialization.JsonConverter<DateTime>
    {
        public override DateTime Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var dateString = reader.GetString();
            var dt = DateTime.Parse(dateString, null, System.Globalization.DateTimeStyles.RoundtripKind);
            return DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
        }

        public override void Write(System.Text.Json.Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString("yyyy-MM-ddTHH:mm:ss"));
    }

    public class NullableUnspecifiedDateTimeConverter : System.Text.Json.Serialization.JsonConverter<DateTime?>
    {
        public override DateTime? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == System.Text.Json.JsonTokenType.Null) return null;
            var dateString = reader.GetString();
            if (string.IsNullOrEmpty(dateString)) return null;
            var dt = DateTime.Parse(dateString, null, System.Globalization.DateTimeStyles.RoundtripKind);
            return DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
        }

        public override void Write(System.Text.Json.Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
        {
            if (value.HasValue) writer.WriteStringValue(value.Value.ToString("yyyy-MM-ddTHH:mm:ss"));
            else writer.WriteNullValue();
        }
    }

    public class RepairItemService
    {
        private readonly HttpClient _httpClient;

        // ✅ Shared options with no UTC conversion
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters =
            {
                new UnspecifiedDateTimeConverter(),
                new NullableUnspecifiedDateTimeConverter()
            }
        };

        public RepairItemService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<IEnumerable<ServiceType>> GetServiceTypesAsync()
        {
            var fullUrl = ApiConfiguration.BuildTechnicalServicesUrl("/api/technicalservices/servicetypes");
            return await _httpClient.GetFromJsonAsync<IEnumerable<ServiceType>>(fullUrl);
        }

        public async Task<IEnumerable<ServicePriority>> GetServicePrioritiesAsync()
        {
            var fullUrl = ApiConfiguration.BuildTechnicalServicesUrl("/api/technicalservices/servicepriorities");
            return await _httpClient.GetFromJsonAsync<IEnumerable<ServicePriority>>(fullUrl);
        }

        public async Task<IEnumerable<ServiceStatus>> GetServiceStatusesAsync()
        {
            var fullUrl = ApiConfiguration.BuildTechnicalServicesUrl("/api/technicalservices/servicestatuses");
            return await _httpClient.GetFromJsonAsync<IEnumerable<ServiceStatus>>(fullUrl);
        }

        public async Task<IEnumerable<Repairs>> GetRepairsAsync()
        {
            var fullUrl = ApiConfiguration.BuildTechnicalServicesUrl("/api/items");
            return await _httpClient.GetFromJsonAsync<IEnumerable<Repairs>>(fullUrl);
        }

        // ✅ Paginated method
        public async Task<PaginatedResult<RepairServices>> GetRepairServicesPagedAsync(int pageNumber = 1, int pageSize = 10)
        {
            try
            {
                var fullUrl = ApiConfiguration.BuildTechnicalServicesUrl($"/api/technicalservices?pageNumber={pageNumber}&pageSize={pageSize}");
                Console.WriteLine($"Fetching: {fullUrl}");

                var response = await _httpClient.GetAsync(fullUrl);
                response.EnsureSuccessStatusCode();

                var responseContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Response preview: {responseContent.Substring(0, Math.Min(200, responseContent.Length))}...");

                PaginatedApiResponse repairServices = null;
                List<RepairServices> items = null;
                int totalCount = 0;

                try
                {
                    if (responseContent.Contains("\"items\"") && responseContent.Contains("\"totalCount\""))
                    {
                        repairServices = JsonSerializer.Deserialize<PaginatedApiResponse>(responseContent, _jsonOptions);
                        items = repairServices?.Items;
                        totalCount = repairServices?.TotalCount ?? 0;
                        Console.WriteLine($"Parsed as PaginatedApiResponse. Items: {items?.Count}, Total: {totalCount}");
                    }
                    else
                    {
                        items = JsonSerializer.Deserialize<List<RepairServices>>(responseContent, _jsonOptions);
                        totalCount = items?.Count ?? 0;
                        Console.WriteLine($"Parsed as List<RepairServices>. Items: {items?.Count}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Deserialization error: {ex.Message}");
                    throw;
                }

                // ✅ inspectDate only — stored as UTC in DB, needs +7 adjustment
                if (items != null)
                {
                    foreach (var service in items)
                        AdjustServiceDates(service);
                }

                return new PaginatedResult<RepairServices>
                {
                    Items = items ?? new List<RepairServices>(),
                    TotalCount = totalCount,
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    TotalPages = totalCount > 0 ? (int)Math.Ceiling(totalCount / (double)pageSize) : 0
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in GetRepairServicesPagedAsync: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                throw;
            }
        }
        public async Task MoveBackToInspectingAsync(Guid serviceId)
        {
            var url = ApiConfiguration.BuildTechnicalServicesUrl(
                $"/api/technicalservices/{serviceId}/status");

            var response = await _httpClient.PutAsJsonAsync(url, new { statusId = 10 });

            if (!response.IsSuccessStatusCode)
            {
                var msg = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(msg);
            }
        }

        // ✅ SearchRepairServicesAsync
        public async Task<PaginatedResult<RepairServices>> SearchRepairServicesAsync(
            int pageNumber = 1,
            int pageSize = 10,
            string searchTerm = null,
            string serialNumber = null,
            string status = null,
            string serviceType = null,
            string serviceLocation = null,
            DateTime? fromDate = null,
            DateTime? toDate = null,
            bool? hasContract = null,
            string dateFilter = null,
            string statusFilter = null,
            string sortBy = null,
            bool sortDescending = true,
            bool useProcessDateFiltering = false,
            string[] statusesForProcessFiltering = null,
            string[] excludedStatuses = null,
            Guid[] userIds = null,
            string[] userFilterStatuses = null,
            bool forceServiceDateOnly = false)
        {
            try
            {
                var queryParams = new List<string>
                {
                    $"pageNumber={pageNumber}",
                    $"pageSize={pageSize}",
                    "api-version=1.0"
                };

                if (!string.IsNullOrEmpty(searchTerm))
                    queryParams.Add($"searchTerm={Uri.EscapeDataString(searchTerm)}");

                if (!string.IsNullOrEmpty(serialNumber))
                    queryParams.Add($"serialNumber={Uri.EscapeDataString(serialNumber)}");

                if (!string.IsNullOrEmpty(status))
                    queryParams.Add($"status={Uri.EscapeDataString(status)}");

                if (!string.IsNullOrEmpty(serviceType))
                    queryParams.Add($"serviceType={Uri.EscapeDataString(serviceType)}");

                if (!string.IsNullOrEmpty(serviceLocation))
                    queryParams.Add($"serviceLocation={Uri.EscapeDataString(serviceLocation)}");

                if (fromDate.HasValue)
                    queryParams.Add($"fromDate={fromDate.Value:yyyy-MM-dd}");

                if (toDate.HasValue)
                    queryParams.Add($"toDate={toDate.Value:yyyy-MM-dd}");

                if (hasContract.HasValue)
                    queryParams.Add($"hasContract={hasContract.Value}");

                if (!string.IsNullOrEmpty(dateFilter))
                    queryParams.Add($"dateFilter={dateFilter}");

                if (!string.IsNullOrEmpty(statusFilter))
                    queryParams.Add($"statusFilter={statusFilter}");

                if (!string.IsNullOrEmpty(sortBy))
                    queryParams.Add($"sortBy={sortBy}");

                queryParams.Add($"sortDescending={sortDescending}");

                if (useProcessDateFiltering)
                {
                    queryParams.Add($"useProcessDateFiltering={useProcessDateFiltering}");

                    if (statusesForProcessFiltering != null && statusesForProcessFiltering.Any())
                    {
                        foreach (var statusItem in statusesForProcessFiltering)
                            queryParams.Add($"statusesForProcessFiltering={Uri.EscapeDataString(statusItem)}");
                    }
                }

                if (excludedStatuses != null && excludedStatuses.Any())
                {
                    foreach (var excludedStatus in excludedStatuses)
                        queryParams.Add($"excludedStatuses={Uri.EscapeDataString(excludedStatus)}");
                }

                if (userIds != null && userIds.Any())
                {
                    foreach (var userId in userIds)
                        queryParams.Add($"userIds={userId}");
                }

                if (userFilterStatuses != null && userFilterStatuses.Any())
                {
                    foreach (var userStatus in userFilterStatuses)
                        queryParams.Add($"userFilterStatuses={Uri.EscapeDataString(userStatus)}");
                }

                if (forceServiceDateOnly)
                    queryParams.Add($"forceServiceDateOnly=true");

                var queryString = $"?{string.Join("&", queryParams)}";
                var fullUrl = ApiConfiguration.BuildTechnicalServicesUrl($"/api/technicalservices/search{queryString}");

                Console.WriteLine($"Searching services: {fullUrl}");

                var response = await _httpClient.GetAsync(fullUrl);
                response.EnsureSuccessStatusCode();

                var responseContent = await response.Content.ReadAsStringAsync();

                PaginatedApiResponse repairServices = null;

                if (responseContent.Contains("\"items\"") && responseContent.Contains("\"totalCount\""))
                {
                    // ✅ Use _jsonOptions — no UTC shift
                    repairServices = JsonSerializer.Deserialize<PaginatedApiResponse>(responseContent, _jsonOptions);
                }
                else
                {
                    var items = JsonSerializer.Deserialize<List<RepairServices>>(responseContent, _jsonOptions);
                    repairServices = new PaginatedApiResponse
                    {
                        Items = items,
                        TotalCount = items?.Count ?? 0
                    };
                }

                // ✅ inspectDate only — stored as UTC in DB, needs +7 adjustment
                if (repairServices?.Items != null)
                {
                    foreach (var service in repairServices.Items)
                        AdjustServiceDates(service);
                }

                return new PaginatedResult<RepairServices>
                {
                    Items = repairServices?.Items ?? new List<RepairServices>(),
                    TotalCount = repairServices?.TotalCount ?? 0,
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    TotalPages = repairServices != null && repairServices.TotalCount > 0
                        ? (int)Math.Ceiling(repairServices.TotalCount / (double)pageSize)
                        : 0
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in SearchRepairServicesAsync: {ex.Message}");
                throw;
            }
        }

        // ✅ ONLY inspectDate needs +7 — it is stored as pure UTC in DB.
        // All other dates are saved as DateTime.UtcNow.AddHours(7) server-side,
        // so they are already Cambodia time and need no adjustment.
        private void AdjustServiceDates(RepairServices service)
        {
            if (service.inspectDate.HasValue)
                service.inspectDate = service.inspectDate.Value.AddHours(7);
        }

        public async Task<RepairServices> GetRepairServiceByIdAsync(Guid id)
        {
            var fullUrl = ApiConfiguration.BuildTechnicalServicesUrl($"/api/technicalservices/{id}");
            var response = await _httpClient.GetAsync(fullUrl);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var service = JsonSerializer.Deserialize<RepairServices>(content, _jsonOptions);
            if (service != null) AdjustServiceDates(service);
            return service;
        }

        public async Task CreateRepairServiceAsync(RepairServices repairService)
        {
            var fullUrl = ApiConfiguration.BuildTechnicalServicesUrl("/api/technicalservices");
            var response = await _httpClient.PostAsJsonAsync(fullUrl, repairService);
            response.EnsureSuccessStatusCode();
        }

        public async Task UpdateRepairServiceAsync(RepairServices repairService)
        {
            var fullUrl = ApiConfiguration.BuildTechnicalServicesUrl("/api/technicalservices");

            try
            {
                Console.WriteLine("=== UPDATE API CALL ===");
                Console.WriteLine($"URL: {fullUrl}");

                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                };

                var json = JsonSerializer.Serialize(repairService, options);
                Console.WriteLine($"Payload:\n{json}");

                var response = await _httpClient.PutAsJsonAsync(fullUrl, repairService, options);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"API Error Response: {errorContent}");
                    throw new Exception($"API returned {response.StatusCode}: {errorContent}");
                }

                response.EnsureSuccessStatusCode();
                Console.WriteLine("Update successful!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in UpdateRepairServiceAsync: {ex.Message}");
                throw;
            }
        }

        public async Task DeleteRepairServiceAsync(Guid id)
        {
            var fullUrl = ApiConfiguration.BuildTechnicalServicesUrl($"/api/technicalservices/{id}");
            var response = await _httpClient.DeleteAsync(fullUrl);
            response.EnsureSuccessStatusCode();
        }

        public async Task<string> GetLatestReportNoAsync()
        {
            try
            {
                var result = await SearchRepairServicesAsync(
                    pageNumber: 1,
                    pageSize: 1,
                    sortBy: "reportNo",
                    sortDescending: true
                );

                if (result.Items != null && result.Items.Any())
                {
                    var latestReportNo = result.Items.First().reportNo;
                    Console.WriteLine($"✅ Latest report number: {latestReportNo}");
                    return latestReportNo;
                }

                Console.WriteLine("⚠️ No existing reports found");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error in GetLatestReportNoAsync: {ex.Message}");
                throw;
            }
        }
    }

    public class PaginatedResult<T>
    {
        public List<T> Items { get; set; }
        public int TotalCount { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public int TotalPages { get; set; }
        public int TotalUsedQuantity { get; set; }
        public int TotalServiceUsedQuantity { get; set; }
        public int TotalManualUsedQuantity { get; set; }
        public bool HasPreviousPage => PageNumber > 1;
        public bool HasNextPage => PageNumber < TotalPages;
    }

    public class PaginatedApiResponse
    {
        public List<RepairServices> Items { get; set; }
        public int TotalCount { get; set; }
    }
}
