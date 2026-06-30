# ============================
# BASE RUNTIME IMAGE
# ============================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8085 8087 8081 5000 6379

# Install required libraries, fonts, supervisor, and Redis server
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        libc6 libicu-dev libfontconfig1 libgdiplus libx11-6 libxrender1 libxext6 \
        fonts-dejavu-core fonts-khmeros fontconfig libfreetype6 \
        libjpeg62-turbo libpng16-16 zlib1g libxrandr2 libxinerama1 libxcursor1 \
        libxi6 libharfbuzz0b libpango-1.0-0 libxcb1 libpixman-1-0 \
        libatk-bridge2.0-0 libatk1.0-0 libgdk-pixbuf2.0-0 libglib2.0-0 libgtk-3-0 \
        ca-certificates curl gnupg lsb-release wkhtmltopdf supervisor redis-server && \
    rm -rf /var/lib/apt/lists/*

# ============================
# BUILD IMAGE
# ============================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy local NuGet packages (DevExpress)
COPY ["nuget-packages/", "/src/nuget-packages/"]

# ── Copy solution file ──────────────────────────────────
COPY ["ServiceMaintenanceApplication.sln", "./"]

# ── Copy ALL .csproj files (layer-cache friendly) ───────
COPY ["ServiceMaintenance/ServiceMaintenance.csproj",                                             "ServiceMaintenance/"]
COPY ["ServiceMaintenance.Models/ServiceMaintenance.Models.csproj",                               "ServiceMaintenance.Models/"]

# ✅ FIXED: Infrastructure.Shared was missing — causes restore to fail
COPY ["ServiceMaintenance.Infrastructure.Shared/ServiceMaintenance.Infrastructure.Shared.csproj", "ServiceMaintenance.Infrastructure.Shared/"]

COPY ["UserManagementAPI/UserManagementAPI.csproj",                                               "UserManagementAPI/"]
COPY ["BlazorApplication/EmployeeManagement.Api/EmployeeManagement.Api.csproj",                   "BlazorApplication/EmployeeManagement.Api/"]
COPY ["BlazorApplication/EmployeeManagement.Models/EmployeeManagement.Models.csproj",             "BlazorApplication/EmployeeManagement.Models/"]
COPY ["TechnicalServices/TechnicalService.API/TechnicalService.API.csproj",                       "TechnicalServices/TechnicalService.API/"]
COPY ["TechnicalServices/TechnicalService.Domain/TechnicalService.Domain.csproj",                 "TechnicalServices/TechnicalService.Domain/"]
COPY ["TechnicalServices/TechnicalService.Infrastructure/TechnicalService.Infrastructure.csproj", "TechnicalServices/TechnicalService.Infrastructure/"]

# ── Generate nuget.config for DevExpress local feed ─────
RUN echo '<?xml version="1.0" encoding="utf-8"?>' > nuget.config && \
    echo '<configuration>' >> nuget.config && \
    echo '  <packageSources>' >> nuget.config && \
    echo '    <add key="local-devexpress" value="/src/nuget-packages" />' >> nuget.config && \
    echo '    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />' >> nuget.config && \
    echo '  </packageSources>' >> nuget.config && \
    echo '  <packageSourceMapping>' >> nuget.config && \
    echo '    <packageSource key="local-devexpress">' >> nuget.config && \
    echo '      <package pattern="DevExpress.*" />' >> nuget.config && \
    echo '    </packageSource>' >> nuget.config && \
    echo '    <packageSource key="nuget.org">' >> nuget.config && \
    echo '      <package pattern="*" />' >> nuget.config && \
    echo '  </packageSource>' >> nuget.config && \
    echo '  </packageSourceMapping>' >> nuget.config && \
    echo '</configuration>' >> nuget.config

# ── Restore (layer-cached until a .csproj changes) ──────
RUN dotnet restore "ServiceMaintenance/ServiceMaintenance.csproj"               --configfile nuget.config
RUN dotnet restore "UserManagementAPI/UserManagementAPI.csproj"                 --configfile nuget.config
RUN dotnet restore "BlazorApplication/EmployeeManagement.Api/EmployeeManagement.Api.csproj" --configfile nuget.config
RUN dotnet restore "TechnicalServices/TechnicalService.API/TechnicalService.API.csproj"     --configfile nuget.config

# ── Copy all remaining source files ─────────────────────
COPY . .

# ── Build ────────────────────────────────────────────────
WORKDIR "/src/ServiceMaintenance"
RUN dotnet build "ServiceMaintenance.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/build/servicemaintenance

WORKDIR "/src/UserManagementAPI"
RUN dotnet build "UserManagementAPI.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/build/usermanagementapi

WORKDIR "/src/BlazorApplication/EmployeeManagement.Api"
RUN dotnet build "EmployeeManagement.Api.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/build/employeemanagementapi

WORKDIR "/src/TechnicalServices/TechnicalService.API"
RUN dotnet build "TechnicalService.API.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/build/technicalserviceapi

# ============================
# PUBLISH IMAGE
# ============================
FROM build AS publish
ARG BUILD_CONFIGURATION=Release

WORKDIR "/src/ServiceMaintenance"
RUN dotnet publish "ServiceMaintenance.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/publish/servicemaintenance \
    /p:UseAppHost=false \
    /p:ErrorOnDuplicatePublishOutputFiles=false

WORKDIR "/src/UserManagementAPI"
RUN dotnet publish "UserManagementAPI.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/publish/usermanagementapi \
    /p:UseAppHost=false \
    /p:ErrorOnDuplicatePublishOutputFiles=false

WORKDIR "/src/BlazorApplication/EmployeeManagement.Api"
RUN dotnet publish "EmployeeManagement.Api.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/publish/employeemanagementapi \
    /p:UseAppHost=false \
    /p:ErrorOnDuplicatePublishOutputFiles=false

WORKDIR "/src/TechnicalServices/TechnicalService.API"
RUN dotnet publish "TechnicalService.API.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/publish/technicalserviceapi \
    /p:UseAppHost=false \
    /p:ErrorOnDuplicatePublishOutputFiles=false

# ============================
# FINAL RUNTIME IMAGE
# ============================
FROM base AS final
WORKDIR /app

# ── Copy published apps ───────────────────────────────────
COPY --from=publish /app/publish/servicemaintenance    ./servicemaintenance
COPY --from=publish /app/publish/usermanagementapi     ./usermanagementapi
COPY --from=publish /app/publish/employeemanagementapi ./employeemanagementapi
COPY --from=publish /app/publish/technicalserviceapi   ./technicalserviceapi

# ── Create required directories ───────────────────────────
RUN mkdir -p \
        /app/servicemaintenance/wwwroot/images/spareparts \
        /app/servicemaintenance/wwwroot/uploads \
        /app/servicemaintenance/wwwroot/temp \
        /app/servicemaintenance/dataprotection-keys \
        /app/servicemaintenance/logs \
        /app/usermanagementapi/wwwroot/uploads/profile-pictures \
        /app/usermanagementapi/dataprotection-keys \
        /app/usermanagementapi/logs \
        /app/employeemanagementapi/logs \
        /app/technicalserviceapi/logs \
        /var/log/supervisor \
        /var/lib/redis \
        /var/log/redis && \
    chmod -R 755 \
        /app/servicemaintenance/wwwroot \
        /app/servicemaintenance/dataprotection-keys \
        /app/servicemaintenance/logs \
        /app/usermanagementapi/wwwroot \
        /app/usermanagementapi/dataprotection-keys \
        /app/usermanagementapi/logs \
        /app/employeemanagementapi/logs \
        /app/technicalserviceapi/logs \
        /var/log/supervisor && \
    chown -R redis:redis /var/lib/redis /var/log/redis

# ── Fix libgdiplus symlink + rebuild font cache ───────────
RUN ln -sf /usr/lib/x86_64-linux-gnu/libgdiplus.so /usr/lib/libgdiplus.so || true && \
    fc-cache -fv

# ── Supervisor config ─────────────────────────────────────
RUN mkdir -p /etc/supervisor/conf.d

COPY <<'EOF' /etc/supervisor/conf.d/supervisord.conf
[supervisord]
nodaemon=true
user=root
logfile=/var/log/supervisor/supervisord.log
pidfile=/var/run/supervisord.pid
loglevel=info

; ── Redis — port 6379 (LOCAL, in-container cache/broker) ──
; Runs as root at the supervisor level ONLY to fix ownership of the
; mounted named volume on every container start (named volumes mount
; as root-owned, overriding the chown done at image build time above).
; redis-server itself then takes over the process via exec.
[program:redis]
command=/bin/sh -c "chown -R redis:redis /var/lib/redis && exec redis-server --bind 127.0.0.1 --port 6379 --save 60 1 --loglevel warning"
directory=/var/lib/redis
autostart=true
autorestart=true
startsecs=5
startretries=5
priority=1
stderr_logfile=/var/log/redis.err.log
stdout_logfile=/var/log/redis.out.log
user=root

; ── ServiceMaintenance (Blazor) — port 8085 ──────────────
[program:servicemaintenance]
command=dotnet /app/servicemaintenance/ServiceMaintenance.dll
directory=/app/servicemaintenance
autostart=true
autorestart=true
startsecs=10
startretries=5
priority=10
stderr_logfile=/var/log/servicemaintenance.err.log
stdout_logfile=/var/log/servicemaintenance.out.log
environment=
    ASPNETCORE_ENVIRONMENT="Development",
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT="false",
    ASPNETCORE_URLS="http://+:8085",
    TZ="Asia/Phnom_Penh"
user=www-data

; ── UserManagementAPI — port 8087 ────────────────────────
[program:usermanagementapi]
command=dotnet /app/usermanagementapi/UserManagementAPI.dll
directory=/app/usermanagementapi
autostart=true
autorestart=true
startsecs=10
startretries=5
priority=10
stderr_logfile=/var/log/usermanagementapi.err.log
stdout_logfile=/var/log/usermanagementapi.out.log
environment=
    ASPNETCORE_ENVIRONMENT="Development",
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT="false",
    ASPNETCORE_URLS="http://+:8087",
    TZ="Asia/Phnom_Penh"
user=www-data

; ── EmployeeManagement.Api — port 8081 ───────────────────
[program:employeemanagementapi]
command=dotnet /app/employeemanagementapi/EmployeeManagement.Api.dll
directory=/app/employeemanagementapi
autostart=true
autorestart=true
startsecs=10
startretries=5
priority=10
stderr_logfile=/var/log/employeemanagementapi.err.log
stdout_logfile=/var/log/employeemanagementapi.out.log
environment=
    ASPNETCORE_ENVIRONMENT="Development",
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT="false",
    ASPNETCORE_URLS="http://+:8081",
    TZ="Asia/Phnom_Penh"
user=www-data

; ── TechnicalService.API — port 5000 ─────────────────────
[program:technicalserviceapi]
command=dotnet /app/technicalserviceapi/TechnicalService.API.dll
directory=/app/technicalserviceapi
autostart=true
autorestart=true
startsecs=10
startretries=5
priority=10
stderr_logfile=/var/log/technicalserviceapi.err.log
stdout_logfile=/var/log/technicalserviceapi.out.log
environment=
    ASPNETCORE_ENVIRONMENT="Development",
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT="false",
    ASPNETCORE_URLS="http://+:5000",
    TZ="Asia/Phnom_Penh"
user=www-data
EOF

# ── Set ownership ─────────────────────────────────────────
RUN chown -R www-data:www-data \
        /app/servicemaintenance/wwwroot \
        /app/servicemaintenance/dataprotection-keys \
        /app/servicemaintenance/logs \
        /app/usermanagementapi/wwwroot \
        /app/usermanagementapi/dataprotection-keys \
        /app/usermanagementapi/logs \
        /app/employeemanagementapi/logs \
        /app/technicalserviceapi/logs

# ── Environment ───────────────────────────────────────────
ENV ASPNETCORE_ENVIRONMENT=Development
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENV TZ=Asia/Phnom_Penh

# ── Start all services via supervisor ─────────────────────
CMD ["/usr/bin/supervisord", "-c", "/etc/supervisor/conf.d/supervisord.conf"]