using Microsoft.EntityFrameworkCore;
using AcademicSentinel.Server.Data;
using AcademicSentinel.Server.Hubs;
using AcademicSentinel.Server.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// PORT BINDING (Render / DigitalOcean App Platform)
// PaaS providers inject a $PORT env var and expect the container to listen on
// 0.0.0.0:$PORT over plain HTTP — TLS is terminated at the edge.
// ---------------------------------------------------------------------------
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

// ---------------------------------------------------------------------------
// CONNECTION STRING — env-injected, with Render-style URL parsing
// Render exposes Postgres as `DATABASE_URL=postgres://user:pass@host:port/db`.
// Npgsql wants key/value form, so translate when needed. Falls back to
// appsettings.json for local development.
// ---------------------------------------------------------------------------
string? connectionString =
    Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? builder.Configuration.GetConnectionString("DefaultConnection");

if (!string.IsNullOrEmpty(connectionString) &&
    (connectionString.StartsWith("postgres://") || connectionString.StartsWith("postgresql://")))
{
    var uri = new Uri(connectionString);
    var userInfo = uri.UserInfo.Split(':', 2);
    connectionString =
        $"Host={uri.Host};Port={(uri.Port > 0 ? uri.Port : 5432)};" +
        $"Database={uri.AbsolutePath.TrimStart('/')};" +
        $"Username={Uri.UnescapeDataString(userInfo[0])};" +
        $"Password={(userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty)};" +
        // Render's managed Postgres requires SSL; trust their self-signed cert.
        "SSL Mode=Require;Trust Server Certificate=true";
}

// ---------------------------------------------------------------------------
// JWT — secrets must come from env in production, never from appsettings.json
// ---------------------------------------------------------------------------
var jwtKey = Environment.GetEnvironmentVariable("JWT_KEY")
             ?? builder.Configuration["Jwt:Key"];
var jwtIssuer = Environment.GetEnvironmentVariable("JWT_ISSUER")
                ?? builder.Configuration["Jwt:Issuer"];
var jwtAudience = Environment.GetEnvironmentVariable("JWT_AUDIENCE")
                  ?? builder.Configuration["Jwt:Audience"];

if (string.IsNullOrWhiteSpace(jwtKey))
    throw new InvalidOperationException("JWT signing key not configured (set JWT_KEY env var).");

var keyBytes = Encoding.UTF8.GetBytes(jwtKey);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(keyBytes)
        };

        // SignalR sends the JWT as a query-string param during the WebSocket
        // upgrade because browsers can't set custom headers on WS handshakes.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/monitoringHub"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

// ---------------------------------------------------------------------------
// CORS — required for browser-based frontends (and SignalR with credentials).
// Origins are read from CORS_ALLOWED_ORIGINS (comma-separated) so deployments
// can be reconfigured without a rebuild.
// ---------------------------------------------------------------------------
var corsOrigins = (Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS")
                   ?? "http://localhost:5173,http://localhost:3000")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddCors(options =>
{
    options.AddPolicy("AcademicSentinelCors", policy =>
    {
        policy.WithOrigins(corsOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              // SignalR WebSockets require credentials; AllowAnyOrigin is
              // incompatible with AllowCredentials, hence WithOrigins above.
              .AllowCredentials();
    });
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// --- SWAGGER JWT SETUP ---
builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "Enter 'Bearer' [space] and then your token. Example: 'Bearer 12345abcdef'",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
                Scheme = "oauth2",
                Name = "Bearer",
                In = ParameterLocation.Header,
            },
            new List<string>()
        }
    });
});

builder.Services.AddSignalR(options =>
{
    // Tighten transport liveness so a hard network drop surfaces through
    // OnDisconnectedAsync in ~12s instead of the 30s default — this is the
    // SignalR-level companion to the heartbeat sweeper (10s) and makes the
    // instructor's IMC reflect a student disconnect in near real time.
    // ClientTimeoutInterval must be >= 2x KeepAliveInterval (SignalR rule).
    options.KeepAliveInterval     = TimeSpan.FromSeconds(5);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(12);
});

// Single master for student-disconnect handling.  Used by both
// MonitoringHub.OnDisconnectedAsync (SignalR transport drop) and
// DisconnectSweeperService (heartbeat timeout) to eliminate duplicate
// writes, UI flicker, and JoinApprovalStatus NOT NULL violations.
// Singleton — owns a process-local 10s idempotency map; acquires its own
// AppDbContext scope per call via IServiceScopeFactory.
builder.Services.AddSingleton<AcademicSentinel.Server.Services.DisconnectService>();

// Heartbeat-driven disconnect detector. Runs every 5s, flips participants
// to Disconnected within ~15s when SAC stops pinging. See
// Services/DisconnectSweeperService.cs for the full reasoning.
builder.Services.AddHostedService<AcademicSentinel.Server.Services.DisconnectSweeperService>();

// Ghost-lock sweeper for the single-device login policy. Runs every 5
// minutes and releases User.IsLoggedIn + CurrentDeviceId when:
//   • the account is marked logged-in, AND
//   • LastLoginAt is older than 15 min (matches AuthController's own
//     stale-lock window), AND
//   • the user isn't currently alive on the hub heartbeat map.
// Without this, a force-quit BEFORE the user joined any room would
// leave the lock pinned until the next contested login attempt
// passively cleared it. See Services/GhostLockSweeperService.cs.
builder.Services.AddHostedService<AcademicSentinel.Server.Services.GhostLockSweeperService>();

// --------------------------------------------------------------------------
// PAST SESSION ARCHIVE — Trash retention.
//
// Archive:RetentionDays controls how long soft-deleted (Trashed) Past
// Session archives are kept before ArchiveCleanupService hard-deletes
// them. Allowed values are 15 or 30. Validated at startup so a typo
// in appsettings.json / env var is caught immediately rather than
// silently sweeping data on the wrong cadence.
// --------------------------------------------------------------------------
var retentionDaysRaw = Environment.GetEnvironmentVariable("Archive__RetentionDays")
                       ?? builder.Configuration["Archive:RetentionDays"];
if (string.IsNullOrWhiteSpace(retentionDaysRaw))
{
    // No setting present: appsettings.json ships with 30; treat
    // missing as "use the default" rather than failing startup.
    retentionDaysRaw = "30";
}
if (!int.TryParse(retentionDaysRaw, out var retentionDays) || (retentionDays != 15 && retentionDays != 30))
{
    throw new InvalidOperationException(
        $"Archive:RetentionDays must be 15 or 30 (got '{retentionDaysRaw}'). " +
        "Set it in appsettings.json or via the Archive__RetentionDays env var.");
}
builder.Services.AddHostedService<AcademicSentinel.Server.Services.ArchiveCleanupService>();

// ---------------------------------------------------------------------------
// IMAGE STORAGE — pick implementation based on env.
// If Cloudinary creds are present, use the cloud-backed implementation
// (survives Render's ephemeral filesystem). In Production we hard-fail
// startup if Cloudinary is not configured: Render's free tier wipes uploaded
// files on every restart, so silently falling back to local disk would just
// produce mysterious 404s on course images later.
// ---------------------------------------------------------------------------
var cloudinaryUrl = Environment.GetEnvironmentVariable("CLOUDINARY_URL");
var isCloudinaryConfigured = !string.IsNullOrWhiteSpace(cloudinaryUrl);

if (isCloudinaryConfigured)
{
    // Singleton: the Cloudinary client is thread-safe and the constructor
    // does HTTP-client setup that we don't want to repeat per request.
    // Scoping it forced re-init on every upload — and any constructor
    // exception then surfaced as an opaque 500 instead of startup failure.
    builder.Services.AddSingleton<IImageStorageService, CloudinaryImageStorageService>();
}
else if (builder.Environment.IsProduction())
{
    throw new InvalidOperationException(
        "CLOUDINARY_URL is not set. Production deployments must use Cloudinary " +
        "because the host filesystem is ephemeral. Set CLOUDINARY_URL in the " +
        "deployment environment (format: cloudinary://api_key:api_secret@cloud_name).");
}
else
{
    builder.Services.AddScoped<IImageStorageService, ImageStorageService>();
}

// ---------------------------------------------------------------------------
// MULTIPART / FORM LIMITS — explicit ceilings for image uploads. Default
// MultipartBodyLengthLimit is 128 MB which is fine, but setting it
// explicitly makes the policy auditable and protects against any PaaS
// proxy that would otherwise truncate.
// ---------------------------------------------------------------------------
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 10 * 1024 * 1024; // 10 MB
    options.ValueLengthLimit = 10 * 1024 * 1024;
});
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10 MB
});

builder.Services.AddTransient<IEmailSender, OutlookEmailSender>();

builder.Services.AddDbContext<AppDbContext>(options =>
    options
        .UseNpgsql(connectionString)
        // EF Core 10 promoted PendingModelChangesWarning from "warn" to
        // "throw at startup" by default. This repo uses hand-written
        // migrations (see the *_*000000 timestamps under /Migrations),
        // so the BuildTargetModel snapshot can drift very slightly from
        // the runtime model — annotations the scaffolder would emit
        // automatically aren't always reproduced by hand. The actual
        // schema is correct (each manual migration's Up() applies the
        // intended DDL); only the snapshot-vs-runtime comparison is
        // noisy. Demote this single warning back to a log line so
        // startup proceeds, leaving every other warning still strict.
        // Reference: https://aka.ms/efcore-docs-pending-changes
        .ConfigureWarnings(w => w.Ignore(
            Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

var app = builder.Build();

// ---------------------------------------------------------------------------
// FORWARDED HEADERS — Render/DO sit a reverse proxy in front of us, so we
// must trust X-Forwarded-Proto/-For to keep Request.Scheme correct (matters
// for SignalR negotiation and any URL generation).
// MUST run before authentication.
// ---------------------------------------------------------------------------
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    // PaaS proxies are not in our network — clear the safe-list defaults.
    KnownNetworks = { },
    KnownProxies = { }
});

// ---------------------------------------------------------------------------
// Startup diagnostics — surface which storage backend is wired up so the
// Render logs make misconfiguration obvious instead of silent.
// ---------------------------------------------------------------------------
{
    var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    startupLogger.LogInformation(
        "Image storage backend: {Backend}",
        isCloudinaryConfigured ? "Cloudinary (persistent)" : "Local disk (EPHEMERAL — files lost on restart)");
    startupLogger.LogInformation("Environment: {Env}", app.Environment.EnvironmentName);
    startupLogger.LogInformation("CORS allowed origins: {Origins}", string.Join(", ", corsOrigins));
}

// ---------------------------------------------------------------------------
// AUTOMATIC EF CORE MIGRATIONS at startup.
// Wrapped in a scope; aborts startup loudly if migration fails so the
// platform's failed-deploy signal trips correctly.
// ---------------------------------------------------------------------------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// HTTPS redirect: only in dev. The PaaS proxy already enforces HTTPS at
// the edge; doing it inside the container causes redirect loops.
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseRouting();

// CORS must be between UseRouting and UseAuthentication for SignalR to work.
app.UseCors("AcademicSentinelCors");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<MonitoringHub>("/monitoringHub");

// Lightweight health endpoint for Render/DO health checks.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();
