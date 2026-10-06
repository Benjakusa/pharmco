using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Pharmco.Api;
using Pharmco.Api.Endpoints;
using Pharmco.Api.Middleware;
using Pharmco.Api.Services;
using Pharmco.Api.Services.Daraja;
using Pharmco.Core.Auth;
using Pharmco.Core.Data;
using Pharmco.Core.Licensing;
using Pharmco.Core.Tenants;

var builder = WebApplication.CreateBuilder(args);

// --- configuration ------------------------------------------------------------
var connectionString = builder.Configuration["ConnectionStrings:Master"]
    ?? throw new InvalidOperationException("ConnectionStrings__Master (or appsettings 'ConnectionStrings:Master') is required");
var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("Jwt:Secret is required");
if (builder.Environment.IsProduction()
    && (jwtSecret.Length < 32 || jwtSecret.StartsWith("dev-only-", StringComparison.Ordinal)))
{
    throw new InvalidOperationException("Production requires Jwt:Secret to be a non-development secret of at least 32 characters.");
}
if (builder.Environment.IsProduction())
{
    var darajaKey = builder.Configuration["Daraja:EncryptionKey"]
        ?? throw new InvalidOperationException("Production requires Daraja:EncryptionKey.");
    _ = new Pharmco.Core.Security.DarajaEncryption(darajaKey);
}

// --- JSON: the wire format is snake_case everywhere (docs/api-contract.md) ----
// Request DTO properties are PascalCase in C#; without this policy
// System.Text.Json cannot bind "pharmacy_code" → PharmacyCode (the underscore
// kills the case-insensitive match), so every snake_case request body would
// deserialize as empty and 400. Responses use the same policy, so the
// anonymous snake_case payloads endpoints hand-build stay identical.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});

// --- services -----------------------------------------------------------------
builder.Services.AddSingleton(new NpgsqlConnectionFactory(connectionString));
builder.Services.AddSingleton<TenantRepository>();
builder.Services.AddSingleton<AuthRepository>();

// Daraja service (singleton — owns HttpClient + token cache)
builder.Services.AddSingleton<DarajaService>();

// License signing/verification key (RSA-2048). The API must use the SAME keypair
// the provisioning CLI signs with (License__PrivateKeyPath), otherwise the
// /api/sync/pull license block and any license verification cannot work.
builder.Services.AddSingleton(CreateLicenseService(builder.Configuration, builder.Environment.IsProduction()));

// JWT (HS256) — the hand-rolled signer/verifier and the framework JwtBearer
// filter share the same secret, issuer and audience, so either path validates
// the same tokens. Access 15 min (env Jwt__AccessTtlMinutes), refresh 30 days
// (env Jwt__RefreshTtlDays).
var jwtConfig = new JwtConfig();
jwtConfig.Issuer = builder.Configuration["Jwt:Issuer"] ?? "pharmco";
jwtConfig.Audience = builder.Configuration["Jwt:Audience"] ?? "pharmco-client";
jwtConfig.AccessTtlSeconds = long.Parse(builder.Configuration["Jwt:AccessTtlMinutes"] ?? "15") * 60;
jwtConfig.RefreshTtlSeconds = long.Parse(builder.Configuration["Jwt:RefreshTtlDays"] ?? "30") * 86_400;
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
// The desktop client gzip-compresses sync batches to keep offline queues small.
builder.Services.AddRequestDecompression();

// Background job for pending M-Pesa verification
builder.Services.AddHostedService<PendingVerificationJob>();

var app = builder.Build();

// Serve the browser client from wwwroot at the same origin as the API. This
// keeps browser API calls same-origin and avoids a separate frontend toolchain.
app.UseDefaultFiles();
app.UseStaticFiles();

// Static endpoint classes keep an optional logger; wire it from the container so
// their diagnostics are not silently dropped.
SyncEndpoints.SetLogger(app.Services.GetRequiredService<ILogger<ApiLog>>());
LicenseEndpoints.SetLogger(app.Services.GetRequiredService<ILogger<ApiLog>>());

// --- endpoints ----------------------------------------------------------------
app.MapGet("/health", Health.Handle);

// Auth. login/refresh are deliberately public; logout + session require a token.
app.MapPost("/api/auth/login", AuthEndpoints.Login);
app.MapPost("/api/auth/refresh", AuthEndpoints.Refresh);
app.MapPost("/api/auth/logout", AuthEndpoints.Logout).RequireAuthorization();
app.MapGet("/api/auth/session", AuthEndpoints.Session).RequireAuthorization();

// Daraja M-Pesa configuration (admin only)
app.MapPost("/api/daraja/config", DarajaConfigEndpoints.SaveConfig).RequireAuthorization(policy => policy.RequireRole(Authz.RoleAdmin));
app.MapPost("/api/daraja/test", DarajaConfigEndpoints.TestConfig).RequireAuthorization(policy => policy.RequireRole(Authz.RoleAdmin));
app.MapGet("/api/daraja/status", DarajaConfigEndpoints.GetStatus).RequireAuthorization(policy => policy.RequireRole(Authz.RoleAdmin));

// M-Pesa callback (NO auth — Safaricom calls it)
app.MapPost("/api/mpesa/callback", MpesaCallbackEndpoint.HandleCallback);

// Sync endpoints (requires auth)
app.MapPost("/api/sync/push", SyncEndpoints.PushSync).RequireAuthorization();
app.MapGet("/api/sync/pull", SyncEndpoints.PullSync).RequireAuthorization();

// License endpoints
app.MapPost("/api/license/renew-request", LicenseEndpoints.RenewRequest).RequireAuthorization(policy => policy.RequireRole(Authz.RoleAdmin));

// User management — the route-level policy is the equivalent of
// [Authorize(Roles = "admin")]; handlers additionally re-check with
// Authz.RequireAdmin (cashier → 403, exercised by the integration tests).
app.MapGet("/api/users", UserEndpoints.List).RequireAuthorization(policy => policy.RequireRole(Authz.RoleAdmin));
app.MapPost("/api/users", UserEndpoints.Create).RequireAuthorization(policy => policy.RequireRole(Authz.RoleAdmin));
app.MapPatch("/api/users/{id}", UserEndpoints.Patch).RequireAuthorization(policy => policy.RequireRole(Authz.RoleAdmin));
app.MapDelete("/api/users/{id}", UserEndpoints.Delete).RequireAuthorization(policy => policy.RequireRole(Authz.RoleAdmin));

// --- pipeline ------------------------------------------------------------------
// Authentication first (populates context.User from the Bearer token), then the
// tenant resolver (401/403 + search_path), then endpoint execution. The tenant
// resolver must SKIP /api/auth/login + /api/auth/refresh (no bearer present:
// login binds the tenant from the body, refresh from the stored token row).
app.UseRequestDecompression();
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

// Loads the RSA license key from License__PrivateKeyPath (file) or
// License__PrivateKey (inline PEM). Falls back to an ephemeral key with a loud
// warning so a misconfigured deployment is obvious instead of silently unable
// to verify tenant licenses.
static LicenseService CreateLicenseService(IConfiguration configuration, bool isProduction)
{
    var path = configuration["License:PrivateKeyPath"];
    if (!string.IsNullOrWhiteSpace(path))
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"License:PrivateKeyPath not found: {path}");
        return new LicenseService(LicenseService.LoadPrivateKey(File.ReadAllText(path)));
    }

    var inline = configuration["License:PrivateKey"];
    if (!string.IsNullOrWhiteSpace(inline))
        return new LicenseService(LicenseService.LoadPrivateKey(inline));

    if (isProduction)
        throw new InvalidOperationException("Production requires License:PrivateKeyPath or License:PrivateKey; an ephemeral key cannot verify provisioned licenses.");

    Console.Error.WriteLine("WARNING: License:PrivateKeyPath is not set — using an ephemeral development RSA key.");
    return new LicenseService(RSA.Create(2048));
}

public partial class Program { }
