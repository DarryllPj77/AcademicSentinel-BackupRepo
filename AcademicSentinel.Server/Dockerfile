# syntax=docker/dockerfile:1.7
# ---------------------------------------------------------------------------
# Multi-stage build for AcademicSentinel.Server (.NET 10, ASP.NET Core).
# Optimized for Render free tier and DigitalOcean App Platform:
#   - layer cache via separate restore step
#   - non-root runtime user
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

# Run as a non-root user (security baseline + matches DO App Platform policy).
RUN useradd --uid 1000 --user-group --no-create-home --shell /usr/sbin/nologin appuser
USER appuser

EXPOSE 8080

COPY --from=build --chown=appuser:appuser /app/publish .

ENTRYPOINT ["dotnet", "AcademicSentinel.Server.dll"]
