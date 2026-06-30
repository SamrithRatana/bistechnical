using Blazored.LocalStorage;
using BlazorReports.Extensions;
using DevExpress.Blazor.Reporting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OfficeOpenXml;
using Polly;
using Polly.Extensions.Http;
using ServiceMaintenance.Authentication;
using ServiceMaintenance.Infrastructure.Shared.Caching;
using ServiceMaintenance.Infrastructure.Shared.Messaging;
using ServiceMaintenance.Middleware;
using ServiceMaintenance.Services;
using ServiceMaintenance.Services.AsyncServices;
using ServiceMaintenance.Services.BISServices;
using ServiceMaintenance.Services.Filters.GlobalSecurity;
using ServiceMaintenance.Services.JWT;
using ServiceMaintenance.Services.KoompiCloudStorage;
using ServiceMaintenance.Services.RealTimeServices;
using Syncfusion.Blazor;
using Syncfusion.Licensing;
using System.Runtime.InteropServices;
using System.Text;
using UserManagementAPI.Data;
using UserManagementAPI.Models;
using static ServiceMaintenance.Infrastructure.Shared.Messaging.FinishedItemConsumer;

var builder = WebApplication.CreateBuilder(args);

ExcelPackage.License.SetNonCommercialPersonal("ServiceMaintenance App");

// ════════════════════════════════════════════════════════
// INFRASTRUCTURE — Redis + RabbitMQ
// ════════════════════════════════════════════════════════

// ✅ In-memory cache (used by GlobalUserService)
builder.Services.AddMemoryCache();

// ✅ Redis — Singleton (one ConnectionMultiplexer for the app lifetime)
builder.Services.AddSingleton<IRedisService, RedisService>();
builder.Services.AddSingleton<CacheHelper>();           // depends on IRedisService → also Singleton
builder.Services.AddSingleton<IdempotencyHelper>();     // ✅ NEW — Redis Deduplication Store (Guideline)

// ✅ RabbitMQ — Singleton connection, Singleton helper
// FIXED: was AddScoped → caused a new connection per HTTP request (expensive + connection leak)
builder.Services.AddSingleton<IRabbitMQService, RabbitMQService>();
builder.Services.AddSingleton<MessageQueueHelper>();    // depends on IRabbitMQService → also Singleton

// ✅ Background consumers — one instance each for the app lifetime
builder.Services.AddHostedService<ReceiveItemConsumer>();
builder.Services.AddHostedService<OrderConsumer>();
builder.Services.AddHostedService<AwaitCustomerConsumer>();
builder.Services.AddHostedService<AwaitSparePartConsumer>();
builder.Services.AddHostedService<InspectItemConsumer>();
builder.Services.AddHostedService<InspectionItemConsumer>(); // ✅ NEW
builder.Services.AddHostedService<RepairItemConsumer>();
builder.Services.AddHostedService<FinishedItemConsumer>();
builder.Services.AddHostedService<DeadLetterMonitorService>(); // ✅ NEW — surfaces DLQ depth instead of silent accumulation
builder.Services.AddHostedService<SparePartConsumer>();    // NEW
builder.Services.AddHostedService<ItemModuleConsumer>();
// ════════════════════════════════════════════════════════
// SERVER CONFIGURATION
// ════════════════════════════════════════════════════════
builder.WebHost.UseUrls("http://*:8085");
builder.WebHost.UseWebRoot("wwwroot");
builder.WebHost.UseStaticWebAssets();

// ════════════════════════════════════════════════════════
// JWT CONFIGURATION
// ════════════════════════════════════════════════════════
var jwtKey = builder.Configuration["JWT:Secret"]
    ?? throw new InvalidOperationException("JWT Secret not configured");
var jwtIssuer = builder.Configuration["JWT:ValidIssuer"] ?? "https://user.koompi.cloud";
var jwtAudience = builder.Configuration["JWT:ValidAudience"] ?? "https://technicalsystemservices.koompi.cloud";
var tokenValidityHours = Convert.ToDouble(builder.Configuration["JWT:TokenValidityInHours"] ?? "8");
var userMgmtConnection = builder.Configuration.GetConnectionString("UserManagementConnection")!;

// ════════════════════════════════════════════════════════
// DATABASE
// ════════════════════════════════════════════════════════
builder.Services.AddDbContext<UserManagementContext>(options =>
    options.UseSqlServer(userMgmtConnection));

builder.Services.AddIdentityCore<ApplicationUser>()
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<UserManagementContext>()
    .AddDefaultTokenProviders();

// ════════════════════════════════════════════════════════
// AUTHENTICATION
// ════════════════════════════════════════════════════════
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
{
    options.LoginPath = "/Identity/Account/Login";
    options.LogoutPath = "/Identity/Account/Logout";
    options.AccessDeniedPath = "/Identity/Account/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromHours(tokenValidityHours);
    options.SlidingExpiration = true;
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.Name = ".ServiceMaintenance.Auth";
})
.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
{
    options.SaveToken = true;
    options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtIssuer,
        ValidAudience = jwtAudience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ClockSkew = TimeSpan.FromMinutes(5),
    };

    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            if (string.IsNullOrEmpty(accessToken))
                accessToken = context.HttpContext.Session.GetString("jwtToken");

            var path = context.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(accessToken) &&
                (path.StartsWithSegments("/chatHub") ||
                 path.StartsWithSegments("/notificationHub") ||
                 path.StartsWithSegments("/itemHub") ||
                 path.StartsWithSegments("/NotificationHub")))
            {
                context.Token = accessToken;
                context.HttpContext.Items["SignalRToken"] = accessToken;
            }
            return Task.CompletedTask;
        },
        OnAuthenticationFailed = context =>
        {
            if (context.Exception is SecurityTokenExpiredException)
                context.Response.Headers.Append("Token-Expired", "true");
            return Task.CompletedTask;
        },
        OnChallenge = context =>
        {
            context.HandleResponse();
            return Task.CompletedTask;
        },
    };
});

builder.Services.AddScoped<AuthenticationStateProvider, JwtAuthenticationStateProvider>();

// ════════════════════════════════════════════════════════
// DATA PROTECTION
// ════════════════════════════════════════════════════════
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo("/app/servicemaintenance/dataprotection-keys"))
    .SetApplicationName("ServiceMaintenance")
    .SetDefaultKeyLifetime(TimeSpan.FromDays(90));

// ════════════════════════════════════════════════════════
// BLAZOR & UI
// ════════════════════════════════════════════════════════
builder.Services.AddRazorPages();
builder.Services.AddRazorComponents();
builder.Services.AddServerSideBlazor(options =>
{
    options.DetailedErrors = true;
    options.DisconnectedCircuitMaxRetained = 100;
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(5);
    options.JSInteropDefaultCallTimeout = TimeSpan.FromMinutes(2);
});
builder.Services.AddControllers();
builder.Services.AddHttpContextAccessor();

// ════════════════════════════════════════════════════════
// DEVEXPRESS
// ════════════════════════════════════════════════════════
builder.Services.AddDevExpressBlazor(options =>
{
    options.BootstrapVersion = DevExpress.Blazor.BootstrapVersion.v5;
    options.SizeMode = DevExpress.Blazor.SizeMode.Medium;
});
builder.Services.AddDevExpressServerSideBlazorReportViewer();
builder.Services.AddBlazorReports();

// ════════════════════════════════════════════════════════
// SYNCFUSION
// ════════════════════════════════════════════════════════
SyncfusionLicenseProvider.RegisterLicense("NjA1NkAzMjM2MkUzMTJFMzliSzVTQlJKN0NLVzNVOFVKSlErcVEzYW9PSkZ2dUhicHliVjkrMncxdHpRPQ==");
builder.Services.AddSyncfusionBlazor();

// ════════════════════════════════════════════════════════
// SIGNALR
// ════════════════════════════════════════════════════════
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = true;
    options.MaximumReceiveMessageSize = 1024 * 1024 * 100;
    options.ClientTimeoutInterval = TimeSpan.FromMinutes(5);
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.HandshakeTimeout = TimeSpan.FromMinutes(1);
    options.MaximumParallelInvocationsPerClient = 10;
    options.StreamBufferCapacity = 20;
});

// ════════════════════════════════════════════════════════
// LOCALIZATION + SESSION
// ════════════════════════════════════════════════════════
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.AddBlazoredLocalStorage();

builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(tokenValidityHours);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.Name = ".ServiceMaintenance.Session";
});

// ════════════════════════════════════════════════════════
// RESPONSE CACHING + COMPRESSION
// ════════════════════════════════════════════════════════
builder.Services.AddResponseCaching();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
});

// ════════════════════════════════════════════════════════
// HTTP CLIENTS
// ════════════════════════════════════════════════════════
builder.Services.AddHttpClient("JwtApi", client =>
{
    client.BaseAddress = new Uri(ServiceMaintenance.Configuration.ApiConfiguration.JwtApiBaseUrl);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.AcceptEncoding.Add(new System.Net.Http.Headers.StringWithQualityHeaderValue("gzip"));
    client.DefaultRequestHeaders.AcceptEncoding.Add(new System.Net.Http.Headers.StringWithQualityHeaderValue("deflate"));
    client.DefaultRequestHeaders.AcceptEncoding.Add(new System.Net.Http.Headers.StringWithQualityHeaderValue("br"));
})
.ConfigurePrimaryHttpMessageHandler(sp =>
{
    var env = sp.GetRequiredService<IWebHostEnvironment>();
    return new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = env.IsDevelopment()
            ? (_, _, _, _) => true : null,
        MaxConnectionsPerServer = 50,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    };
})
.AddPolicyHandler(GetRetryPolicy())
.AddPolicyHandler(Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(25)))
.SetHandlerLifetime(TimeSpan.FromMinutes(2));

// ════════════════════════════════════════════════════════
// JWT SERVICES
// ════════════════════════════════════════════════════════
ConfigureJwtServices(builder.Services);

// ════════════════════════════════════════════════════════
// APPLICATION SERVICES
// ════════════════════════════════════════════════════════
ConfigureApplicationServices(builder.Services);

// ════════════════════════════════════════════════════════
// TYPED HTTP CLIENTS
// ════════════════════════════════════════════════════════
ConfigureHttpClients(builder.Services);

// ════════════════════════════════════════════════════════
// KESTREL
// ════════════════════════════════════════════════════════
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 2L * 1024 * 1024 * 1024;
    options.Limits.MinRequestBodyDataRate = null;
    options.Limits.MinResponseDataRate = null;
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
    options.Limits.MaxConcurrentConnections = 1000;
    options.Limits.MaxConcurrentUpgradedConnections = 1000;
    options.ConfigureEndpointDefaults(listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2;
    });
});

// ════════════════════════════════════════════════════════
// FORM OPTIONS
// ════════════════════════════════════════════════════════
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 5368709120;
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
});

// ════════════════════════════════════════════════════════
// LOGGING
// ════════════════════════════════════════════════════════
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// ════════════════════════════════════════════════════════
// DEVEXPRESS DRAWING ENGINE (Linux)
// ════════════════════════════════════════════════════════
if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    DevExpress.Drawing.Settings.DrawingEngine = DevExpress.Drawing.DrawingEngine.Skia;

// ════════════════════════════════════════════════════════
// CORS
// ════════════════════════════════════════════════════════
builder.Services.AddCors(options =>
{
    options.AddPolicy("JwtCorsPolicy", policy =>
    {
        policy.WithOrigins(
                "https://technicalsystemservices.koompi.cloud",
                "https://user.koompi.cloud"
            )
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// ════════════════════════════════════════════════════════
// BUILD APP
// ════════════════════════════════════════════════════════
var app = builder.Build();

// ════════════════════════════════════════════════════════
// MIDDLEWARE PIPELINE
// ════════════════════════════════════════════════════════
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
            var exceptionFeature = context.Features.Get<IExceptionHandlerFeature>();
            if (exceptionFeature is not null)
            {
                logger.LogError(exceptionFeature.Error, "❌ Unhandled exception");
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new
                {
                    Status = "Error",
                    Message = "An internal server error occurred",
                    TraceId = System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier,
                });
            }
        });
    });
    app.UseHsts();
}
else
{
    app.UseDeveloperExceptionPage();
}

app.UseWebSockets(new Microsoft.AspNetCore.Builder.WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(120) });
app.UseHttpsRedirection();
app.UseResponseCompression();   // ✅ FIXED: must come before UseResponseCaching
app.UseResponseCaching();
app.UseStaticFiles();
app.UseRouting();
app.UseCors("JwtCorsPolicy");   // ✅ FIXED: must be after UseRouting, before UseAuth
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TokenExpiryMiddleware>();

// ════════════════════════════════════════════════════════
// SIGNALR HUBS
// ════════════════════════════════════════════════════════
var hubOptions = new Action<Microsoft.AspNetCore.Http.Connections.HttpConnectionDispatcherOptions>(options =>
{
    options.Transports = HttpTransportType.WebSockets |
                                  HttpTransportType.ServerSentEvents |
                                  HttpTransportType.LongPolling;
    options.LongPolling.PollTimeout = TimeSpan.FromMinutes(2);
    options.WebSockets.CloseTimeout = TimeSpan.FromSeconds(30);
});

app.MapHub<NotificationHub>("/NotificationHub", hubOptions);
app.MapHub<ChatHub>("/chatHub", hubOptions);
app.MapHub<ItemHub>("/itemHub", hubOptions);

// ════════════════════════════════════════════════════════
// ROUTES
// ════════════════════════════════════════════════════════
app.MapRazorPages();
app.MapControllers();
app.MapBlazorHub();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.MapFallbackToPage("/_Host");

app.Run();

// ════════════════════════════════════════════════════════
// HELPER METHODS
// ════════════════════════════════════════════════════════

static void ConfigureJwtServices(IServiceCollection services)
{
    services.AddScoped<JwtSessionService>();
    services.AddScoped<JwtHttpClientService>();
    services.AddScoped<JwtApiService>();
    services.AddScoped<JwtUserManagementService>();
    services.AddScoped<JwtMessageService>();
    services.AddScoped<JwtArticleService>();
    services.AddScoped<GlobalUserService>();
    services.AddScoped<GlobalArticleCacheService>();
    services.AddScoped<UserService>();
    services.AddScoped<IMessageService, MessageService>();
}

static void ConfigureApplicationServices(IServiceCollection services)
{
    services.AddUserActivityBackgroundService();
    services.AddScoped<MenuService>();
    services.AddHttpClient();
    services.AddScoped<RolesService>();
    services.AddScoped<PermissionService>();
    services.AddScoped<StateManagementAction>();
    services.AddScoped<SignalRService>();
    services.AddScoped<MonthlyReportService>();
    services.AddScoped<KoompiStorageService>();
    services.AddScoped<SparepartUsageService>();
    services.AddScoped<SparepartHoldService>();
    services.AddScoped<PrintPreviewService>();
}

static void ConfigureHttpClients(IServiceCollection services)
{
    var technicalUrl = ServiceMaintenance.Configuration.ApiConfiguration.TechnicalServicesBaseUrl;
    var customerUrl = ServiceMaintenance.Configuration.ApiConfiguration.CustomerApiBaseUrl;

    AddHttpClientWithPolicy<ItemService>(services, technicalUrl);
    AddHttpClientWithPolicy<RepairItemService>(services, technicalUrl);
    AddHttpClientWithPolicy<ServiceSparePart>(services, technicalUrl);
    AddHttpClientWithPolicy<ThirdPartyRepairService>(services, technicalUrl);
    AddHttpClientWithPolicy<ReceiveItemService>(services, technicalUrl);
    AddHttpClientWithPolicy<InspectingService>(services, technicalUrl);
    AddHttpClientWithPolicy<InspectItemService>(services, technicalUrl);
    AddHttpClientWithPolicy<AwaitingCustomerConfirmService>(services, technicalUrl);
    AddHttpClientWithPolicy<AwaitingSparePartService>(services, technicalUrl);
    AddHttpClientWithPolicy<UnrepairableService>(services, technicalUrl);
    AddHttpClientWithPolicy<RepairService>(services, technicalUrl);
    AddHttpClientWithPolicy<FinishItemService>(services, technicalUrl);
    AddHttpClientWithPolicy<CustomerRejectedService>(services, technicalUrl);
    AddHttpClientWithPolicy<RentalItemService>(services, technicalUrl);
    AddHttpClientWithPolicy<RentalServicesService>(services, technicalUrl);
    AddHttpClientWithPolicy<RentalItemDetailService>(services, technicalUrl);
    AddHttpClientWithPolicy<CustomerService>(services, customerUrl);
    AddHttpClientWithPolicy<SaleConfirmedService>(services, technicalUrl);
}

static void AddHttpClientWithPolicy<T>(IServiceCollection services, string baseUrl) where T : class
{
    services.AddHttpClient<T>(client =>
    {
        client.BaseAddress = new Uri(baseUrl);
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.AcceptEncoding.Add(new System.Net.Http.Headers.StringWithQualityHeaderValue("gzip"));
        client.DefaultRequestHeaders.AcceptEncoding.Add(new System.Net.Http.Headers.StringWithQualityHeaderValue("deflate"));
        client.DefaultRequestHeaders.AcceptEncoding.Add(new System.Net.Http.Headers.StringWithQualityHeaderValue("br"));
    })
    .ConfigurePrimaryHttpMessageHandler(sp =>
    {
        var env = sp.GetRequiredService<IWebHostEnvironment>();
        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = env.IsDevelopment()
                ? (_, _, _, _) => true : null,
            MaxConnectionsPerServer = 50,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            MaxResponseHeadersLength = 64,
        };
    })
    .AddPolicyHandler(GetRetryPolicy())
    .AddPolicyHandler(Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(25)))
    .SetHandlerLifetime(TimeSpan.FromMinutes(2));
}

static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
{
    return HttpPolicyExtensions
        .HandleTransientHttpError()
        .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        .WaitAndRetryAsync(
            retryCount: 2,
            sleepDurationProvider: attempt => TimeSpan.FromMilliseconds(300 * attempt),
            onRetry: (outcome, timespan, retryCount, _) =>
            {
                var reason = outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString() ?? "Unknown";
                Console.WriteLine($"⚠️ HTTP Retry {retryCount} after {timespan.TotalMilliseconds}ms — {reason}");
            });
}