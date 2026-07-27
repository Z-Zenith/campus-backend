using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Hubs;
using BackendApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers(options => options.Filters.Add<SessionActiveFilter>())
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi(options =>
{
    // The global JsonStringEnumConverter registered on the MVC pipeline above (line ~20)
    // is NOT visible to the OpenAPI document generator's own JsonSerializerOptions, so it
    // emits every enum as `type: integer`. The live API actually serializes enums as their
    // PascalCase member-name strings (JsonStringEnumConverter default), so the generated
    // snapshot must reflect that or a generated client would break on every enum.
    // Rewrite each enum schema to `type: string` with the member names as `enum` values.
    options.AddSchemaTransformer((schema, context, cancellationToken) =>
    {
        var clrType = context.JsonTypeInfo.Type;
        if (clrType.IsEnum)
        {
            schema.Type = Microsoft.OpenApi.JsonSchemaType.String;
            schema.Format = null; // drop the stale "int32" format from the integer schema
            schema.Enum = Enum.GetNames(clrType)
                .Select(name => (JsonNode)JsonValue.Create(name))
                .ToList();
        }
        return Task.CompletedTask;
    });
});

var connectionString = builder.Configuration.GetConnectionString("Campus")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Campus configuration.");

// Same fail-fast shape as Jwt:Key below (#137): the committed appsettings.json value is a
// dev-only placeholder (campus_dev on localhost). Unlike Jwt:Key, a missing override here
// wasn't previously distinguishable from a deliberately-configured dev DB — if
// ASPNETCORE_ENVIRONMENT=Production is set without also overriding ConnectionStrings__Campus,
// the app would otherwise start up fine and silently point at the dev database.
const string DevConnectionStringPlaceholder =
    "Host=localhost;Port=5432;Database=campus;Username=campus;Password=campus_dev";
if (!builder.Environment.IsDevelopment() && connectionString == DevConnectionStringPlaceholder)
{
    throw new InvalidOperationException(
        "ConnectionStrings:Campus is still the committed dev-only placeholder from appsettings.json. " +
        "Set the ConnectionStrings__Campus environment variable before starting in a non-Development environment.");
}

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString, npgsqlOptions =>
{
    npgsqlOptions
        .MapEnum<AccountType>()
        .MapEnum<AssignmentType>()
        .MapEnum<AttendanceStatus>()
        .MapEnum<DocType>()
        .MapEnum<FeeStatus>()
        .MapEnum<GroupType>()
        .MapEnum<NotificationType>()
        .MapEnum<ScopeKind>()
        .MapEnum<WhitelistRequestStatus>();
}));

// #131: TOTP secrets are encrypted at rest via Data Protection (see TotpService). Keys must
// be persisted outside the container's ephemeral filesystem or every restart/redeploy would
// make every existing encrypted TotpSecret unreadable, locking every user out of login.
// DOTNET_RUNNING_IN_CONTAINER is set by the official .NET base images, so this defaults to
// the volume-mounted /keys path in Docker (see docker-compose.yml) and to a local folder
// next to the build output otherwise (bare `dotnet run`, tests, etc.).
var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"]
    ?? (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"
        ? "/keys"
        : Path.Combine(AppContext.BaseDirectory, "keys"));
builder.Services.AddDataProtection()
    .SetApplicationName("Campus.BackendApi")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));

builder.Services.AddScoped<IPasswordHasher, BcryptPasswordHasher>();
builder.Services.AddScoped<ITotpService, TotpService>();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<WardAccessFilter>();
builder.Services.AddScoped<ISessionActivityService, SessionActivityService>();
builder.Services.AddScoped<SessionActiveFilter>();
builder.Services.AddScoped<IPermissionService, PermissionService>();
// #134: per-roll-number login lockout must be shared across requests, not per-scope/request.
builder.Services.AddSingleton<ParentLoginLockoutService>();
builder.Services.AddScoped<ICollegeScopeService, CollegeScopeService>();
// Notification Router (shared) — see Services/INotificationRouter.cs.
builder.Services.AddScoped<INotificationRouter, NotificationRouter>();
builder.Services.AddSignalR();
// SDA-13
builder.Services.AddHostedService<NoLoginAlertHostedService>();
// AWA-05
builder.Services.AddHostedService<FeeReminderHostedService>();

// SDA-25: AI Services (Track-2-owned) receives usage telemetry for suspicious-behaviour
// analysis. Defaults to the docker-compose service name/port if not overridden.
builder.Services.AddHttpClient("AiServices", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["AiServices:BaseUrl"] ?? "http://ai-services:8000");
    client.Timeout = TimeSpan.FromSeconds(10);
});

// AIS-03/04/07: self-hosted AI Services container (services/ai-services, FastAPI).
// Same default as the "AiServices" named client above so both fall back consistently
// when AiServices:BaseUrl isn't set.
builder.Services.AddHttpClient<IAiServicesClient, AiServicesClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["AiServices:BaseUrl"] ?? "http://ai-services:8000");
});

// SEK-01: code execution — each Run shells out to `docker run` for a throwaway
// per-submission sandbox (see DockerCodeRunner's doc comment for why this replaced
// Judge0/isolate). No HttpClient needed, this doesn't talk HTTP.
builder.Services.AddSingleton<ICodeRunner, DockerCodeRunner>();

// SEK-01: integrated terminal — persistent per-session sandbox container, reaped on an
// idle timer (see TerminalSessionService's doc comment for scope). Singleton: the
// in-memory session dictionary must be shared across requests, same reasoning as
// ParentLoginLockoutService above.
builder.Services.AddSingleton<TerminalSessionService>();
builder.Services.AddHostedService<TerminalSessionReaperHostedService>();

// AIS-02: Copyleaks — external, credentialed (Copyleaks:Email/ApiKey/WebhookSecret).
// No default base URL fallback: an empty ApiKey/Email already fails closed inside
// CopyleaksClient via ExternalServiceNotConfiguredException, so there's no safe
// "local dev" default the way AiServices:BaseUrl has one.
builder.Services.AddHttpClient<ICopyleaksClient, CopyleaksClient>(client =>
{
    var baseUrl = builder.Configuration["Copyleaks:BaseUrl"] ?? "https://api.copyleaks.com";
    client.BaseAddress = new Uri(baseUrl);
});

var jwtSection = builder.Configuration.GetSection("Jwt");
var jwtKey = jwtSection["Key"];
if (string.IsNullOrWhiteSpace(jwtKey))
{
    throw new InvalidOperationException(
        builder.Environment.IsDevelopment()
            ? "Missing Jwt:Key configuration. Set it in appsettings.Development.json (untracked/local) or the JWT__Key environment variable."
            : "Missing Jwt:Key configuration. Set the JWT__Key environment variable before starting in a non-Development environment.");
}
// Same fail-fast shape as the connection-string dev-placeholder guard above (#137): the
// committed appsettings.Development.json ships a well-known dev-only signing key. A build
// deployed with ASPNETCORE_ENVIRONMENT unset/mis-set could otherwise start up signing and
// validating tokens with this public value, letting anyone forge a valid JWT for any user.
const string DevJwtKeyPlaceholder = "dev-only-signing-key-change-me-before-any-real-deployment-32b";
if (!builder.Environment.IsDevelopment() && jwtKey == DevJwtKeyPlaceholder)
{
    throw new InvalidOperationException(
        "Jwt:Key is still the committed dev-only placeholder from appsettings.Development.json. " +
        "Set the JWT__Key environment variable to a real secret before starting in a non-Development environment.");
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidateAudience = true,
            ValidAudience = jwtSection["Audience"],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        // SignalR's browser/websocket clients can't set an Authorization header on the
        // handshake, so the Notification Router's hub accepts the JWT via query string
        // instead — only for requests actually hitting the hub path.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/notifications"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization();

// PRT-01 / SDA-02 / TWA-03 login endpoints authenticate with weak or guessable credentials
// (roll number + DOB for parents; no lockout otherwise) — rate limit by caller IP so a
// script can't brute-force the DOB/password space. Applied via
// [EnableRateLimiting(RateLimiterPolicies.Auth)] on each login action rather than globally,
// so it doesn't throttle normal authenticated traffic.
builder.Services.AddRateLimiter(RateLimiterPolicies.ConfigureAuth);

// #141: RateLimiterPolicies partitions on httpContext.Connection.RemoteIpAddress, which is
// only the real client IP when backend-api receives connections directly — true today (no
// reverse proxy in this repo's compose setup). ForwardedHeadersMiddleware is only registered
// (and only trusts X-Forwarded-For/-Proto) when TrustedProxies is explicitly configured, so
// this is a no-op — and doesn't change today's behavior at all — until someone deliberately
// puts a known proxy in front and configures it here. Trusting those headers from an
// unconfigured/unknown source would let any client spoof its own rate-limiter partition key.
var trustedProxies = builder.Configuration.GetSection("TrustedProxies").Get<string[]>() ?? [];
if (trustedProxies.Length > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownProxies.Clear();
        foreach (var proxy in trustedProxies)
        {
            if (IPAddress.TryParse(proxy, out var ip))
            {
                options.KnownProxies.Add(ip);
            }
        }
    });
}

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (trustedProxies.Length > 0)
{
    app.UseForwardedHeaders();
}

// #141: no HSTS/security headers existed at all. HSTS only applies outside Development so a
// local dev cert/http workflow isn't broken. This is a JSON API with no HTML to render, so
// the CSP is intentionally the strictest "deny everything" policy rather than a page-specific
// one.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("Content-Security-Policy", "default-src 'none'");
    await next();
});

app.UseHttpsRedirection();

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
// Notification Router (shared) — real-time transport, see Hubs/NotificationsHub.cs.
app.MapHub<NotificationsHub>("/hubs/notifications");

app.Run();
