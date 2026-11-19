# syntax=docker/dockerfile:1.7
# ---- Build stage ---------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj first so `dotnet restore` can be cached when only source changes.
COPY ["AcademicSentinel.Server.csproj", "./"]
RUN dotnet restore "./AcademicSentinel.Server.csproj"

# Copy the rest and publish
COPY . .
RUN dotnet publish "AcademicSentinel.Server.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ---- Runtime stage -------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# DigitalOcean configurations
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=1

EXPOSE 8080

# Copy the built app and assign ownership to Microsoft's built-in 'app' user
COPY --from=build --chown=app:app /app/publish .

# Switch to the secure built-in user
USER app

ENTRYPOINT ["dotnet", "AcademicSentinel.Server.dll"]
