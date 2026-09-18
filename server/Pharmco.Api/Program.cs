using System.Text;
using Dapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Hosting;
using Pharmco.Api.Endpoints;
using Pharmco.Api.Middleware;
using Pharmco.Api.Services;
using Pharmco.Api.Services.Daraja;
using Pharmco.Core.Auth;
using Pharmco.Core.Data;
using Pharmco.Core.Tenants;

var builder = WebApplication.CreateBuilder(args);

// --- configuration ------------------------------------------------------------
var connectionString = builder.Configuration["ConnectionStrings:Master"]
    ?? throw new InvalidOperationException("ConnectionStrings__Master (or appsettings 'ConnectionStrings:Master') is required");
var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("Jwt:Secret is required");

// --- services -----------------------------------------------------------------
builder.Services.AddSingleton(new NpgsqlConnectionFactory(connectionString));
builder.Services.AddSingleton<TenantRepository>();
builder.Services.AddSingleton<AuthRepository>();

// Daraja service (singleton — owns HttpClient + token cache)
builder.Services.AddSingleton<DarajaService>();

// JWT (HS256) — the hand-rolled signer/verifier and the framework JwtBearer
// filter share the same secret, issuer and audience, so either path validates
// the same tokens. Access 15 min (env Jwt__AccessTtlMinutes), refresh 30 days
// (env Jwt__RefreshTtlDays).
var jwtConfig = new JwtConfig();
jwtConfig.Issuer = builder.Configuration["Jwt:Issuer"] ?? "pharmco";
jwtConfig.Audience = builder.Configuration["Jwt:Audience"] ?? "pharmco-client";
jwtConfig.AccessTtlSeconds = int64.Parse(builder.Configuration["Jwt:AccessTtlMinutes"] ?? "15") * 60;
jwtConfig.RefreshTtlSeconds = int64.Parse(builder.Configuration["Jwt:RefreshTtlDays"] ?? "30") * 86_400;
builder.Services.AddSingleton(new JwtService(jwtSecret, jwtConfig));

// Login failure limiter: 5 failed attempts / username / 15 min (env-tunable).
builder.Services.AddSingleton(new FailureRateLimiter(RateLimiterConfig.FromParts(
    builder.Configuration["RateLimit:MaxAttempts"],
    builder.Configuration["RateLimit:WindowMinutes"])));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "pharmco",
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "pharmco-client",
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
        };
    });
builder.Services.AddAuthorization();

// Background job for pending M-Pesa verification
builder.Services.AddHostedService<PendingVerificationJob>();

var app = builder.Build();

// --- endpoints ----------------------------------------------------------------
app.MapGet("/health", Health.Handle);

// Auth. login/refresh are deliberately public; logout + session require a token.
app.MapPost("/api/auth/login", AuthEndpoints.Login);
app.MapPost("/api/auth/refresh", AuthEndpoints.Refresh);
app.MapPost("/api/auth/logout", AuthEndpoints.Logout).RequireAuthorization();
app.MapGet("/api/auth/session", AuthEndpoints.Session).RequireAuthorization();

// Daraja M-Pesa configuration (admin only)
app.MapPost("/api/daraja/config", DarajaConfigEndpoints.SaveConfig).RequireAuthorization(Authz.IsAdmin);
app.MapPost("/api/daraja/test", DarajaConfigEndpoints.TestConfig).RequireAuthorization(Authz.IsAdmin);
app.MapGet("/api/daraja/status", DarajaConfigEndpoints.GetStatus).RequireAuthorization(Authz.IsAdmin);

// M-Pesa callback (NO auth — Safaricom calls it)
app.MapPost("/api/mpesa/callback", MpesaCallbackEndpoint.HandleCallback);

// Sync endpoints (requires auth)
app.MapPost("/api/sync/push", SyncEndpoints.PushSync).RequireAuthorization();
app.MapGet("/api/sync/pull", SyncEndpoints.PullSync).RequireAuthorization();

// License endpoints
app.MapPost("/api/license/renew-request", LicenseEndpoints.RenewRequest).RequireAuthorization(Authz.IsAdmin);

// User management — .RequireAuthorization(Authz.IsAdmin) is the route-level
// equivalent of [Authorize(Roles = "admin")]; handlers additionally re-check
// with Authz.RequireAdmin (cashier → 403, exercised by the integration tests).
app.MapGet("/api/users", UserEndpoints.List).RequireAuthorization(Authz.IsAdmin);
app.MapPost("/api/users", UserEndpoints.Create).RequireAuthorization(Authz.IsAdmin);
app.MapPatch("/api/users/{id}", UserEndpoints.Patch).RequireAuthorization(Authz.IsAdmin);
app.MapDelete("/api/users/{id}", UserEndpoints.Delete).RequireAuthorization(Authz.IsAdmin);

// --- pipeline ------------------------------------------------------------------
// Authentication first (populates context.User from the Bearer token), then the
// tenant resolver (401/403 + search_path), then endpoint execution. The tenant
// resolver must SKIP /api/auth/login + /api/auth/refresh (no bearer present:
// login binds the tenant from the body, refresh from the stored token row).
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TenantResolverMiddleware>();

// Smoke/diagnostics endpoint: resolves the request-scoped tenant connection and
// runs an UNQUALIFIED products count — proves search_path routing end-to-end.
app.MapGet("/v1/tenant/me", async (HttpContext http) =>
    {
        var current = TenantScope.Current(http);
        if (current is null)
            return Results.Json(new { error = "tenant scope not resolved" },
                statusCode: StatusCodes.Status401Unauthorized);

        var productCount = await current.Connection.ExecuteScalarAsync<int>("SELECT count(*) FROM products");
        return Results.Json(new
        {
            tenant_id = current.TenantId,
            code = current.Code,
            schema = current.SchemaName,
            license_expires_at = current.LicenseExpiresAt,
            products_in_tenant_schema = productCount,
        });
    })
    .RequireAuthorization();

app.Run();

public partial class Program { }