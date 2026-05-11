# syntax=docker/dockerfile:1.7
# ---------------------------------------------------------------------------
# Multi-stage build for AcademicSentinel.Server (.NET 10, ASP.NET Core).
# Optimized for Render free tier and DigitalOcean App Platform:
#   - layer cache via separate restore step
#   - non-root runtime user (using Microsoft's built-in 'app' user)
#   - trimmed runtime image
#   - listens on $PORT (Render injects this) -- handled in Program.cs
# ---------------------------------------------------------------------------

# ---- Build stage ---------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj first so `dotnet restore` can be cached when only source changes.
COPY ["AcademicSentinel.Server/AcademicSentinel.Server.csproj", "AcademicSentinel.Server/"]
RUN dotnet restore "AcademicSentinel.Server/AcademicSentinel.Server.csproj"

# Copy the rest and publish a self-contained framework-dependent build.
COPY AcademicSentinel.Server/ AcademicSentinel.Server/
WORKDIR /src/AcademicSentinel.Server
RUN dotnet publish "AcademicSentinel.Server.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ---- Runtime stage -------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Render/DO inject $PORT; ASPNETCORE_URLS is overridden in Program.cs but we
# also set a sane default so `docker run` works locally.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=1

# Run as the built-in non-root user for .NET 8+ images.
# This prevents the Exit Code 4 crash caused by UID collisions.
USER app

EXPOSE 8080

# Copy the published app and give ownership to the 'app' user
COPY --from=build --chown=app:app /app/publish .

ENTRYPOINT ["dotnet", "AcademicSentinel.Server.dll"]