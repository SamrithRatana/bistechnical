using Microsoft.AspNetCore.SignalR;
namespace ServiceMaintenance.Services.RealTimeServices 
{
    public class UserActivityBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<UserActivityBackgroundService> _logger;
        private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(5);
        private readonly TimeSpan _inactivityThreshold = TimeSpan.FromMinutes(15);

        public UserActivityBackgroundService(
            IServiceProvider serviceProvider,
            ILogger<UserActivityBackgroundService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    CleanupInactiveConnections(); // Remove 'await' since method is now synchronous
                    await Task.Delay(_checkInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred while cleaning up inactive connections");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
            }
        }

        private void CleanupInactiveConnections() // Changed from 'async Task' to 'void'
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<ChatHub>>();

                // Clean up inactive connections
                ChatHub.CleanupInactiveConnections(_inactivityThreshold);

                _logger.LogInformation("Completed inactive connection cleanup at {Time}", DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during inactive connection cleanup");
            }
        }
    }

    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddUserActivityBackgroundService(this IServiceCollection services)
        {
            services.AddHostedService<UserActivityBackgroundService>();
            return services;
        }
    }
}