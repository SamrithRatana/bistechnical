using ServiceMaintenance.Models;
using ServiceMaintenance.Configuration;
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace ServiceMaintenance.Services.BISServices
{
    public class ReceiveItemService
    {
        private readonly HttpClient _httpClient;
        private const string ApiUrl = "/api/receiveitem";

        public ReceiveItemService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task CreateReceiveItemAsync(ReceiveItemRequest request)
        {
            var fullUrl = ApiConfiguration.BuildTechnicalServicesUrl(ApiUrl);
            var response = await _httpClient.PostAsJsonAsync(fullUrl, request);

            if (!response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"Failed to create receive item. Status: {response.StatusCode}, Response: {responseContent}");
            }
        }

        public async Task UpdateReceiveItemAsync(ReceiveItemRequest request)
        {
            var fullUrl = ApiConfiguration.BuildTechnicalServicesUrl(ApiUrl);
            var response = await _httpClient.PutAsJsonAsync(fullUrl, request);

            if (!response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"Failed to update receive item. Status: {response.StatusCode}, Response: {responseContent}");
            }
        }

        // ✅ OPTIMIZED: Added timeout configuration for faster failure detection
        public async Task DeleteReceiveItemAsync(Guid serviceId)
        {
            var fullUrl = ApiConfiguration.BuildTechnicalServicesUrl($"{ApiUrl}/{serviceId}?api-version=1.0");

            // ✅ Set timeout to prevent hanging
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            try
            {
                var response = await _httpClient.DeleteAsync(fullUrl, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    throw new HttpRequestException($"Failed to delete receive item. Status: {response.StatusCode}, Response: {responseContent}");
                }
            }
            catch (TaskCanceledException)
            {
                throw new HttpRequestException("Delete operation timed out after 10 seconds");
            }
        }
    }
}