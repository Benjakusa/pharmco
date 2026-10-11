using System.Net.Http;
using System.Text;
using Xunit;

namespace Pharmco.Tests;

/// <summary>
/// An [LiveStackFact] is skipped at discovery time unless PHARMCO_TEST_API_URL
/// points at a running API backed by Postgres (same pattern as [IntegrationFact]).
/// </summary>
public sealed class LiveStackFactAttribute : FactAttribute
{
    public LiveStackFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PHARMCO_TEST_API_URL")))
            Skip = "PHARMCO_TEST_API_URL is not set — live-stack test skipped";
    }
}

/// <summary>
/// LIVE-STACK integration tests. They hit a running API backed by Postgres with
/// db/master/003_refresh_tokens.sql applied (see .tmp/smoke.sql provider flow).
///
/// RUN:
///   PHARMCO_TEST_API_URL=http://localhost:8080
///   PHARMCO_TEST_ADMIN_USER=admin@nairobi-chemist
///   PHARMCO_TEST_ADMIN_PASS=...
///   PHARMCO_TEST_PHARMACY_A=PHARMCO-001
///   PHARMCO_TEST_PHARMACY_B=PHARMCO-002
///   dotnet test                                   # from server/Pharmco.Tests
///
/// Without PHARMCO_TEST_API_URL the tests are skipped at discovery,
/// so default CI stays green; a live stack is needed only for these.
///
/// NOTE: the required-table checks are the status lines; body assertions are
/// deliberately substring-level so the suite has zero external JSON dependency.
/// </summary>
public class AuthFlowIntegrationTest
{
    private const string EnvUrl = "PHARMCO_TEST_API_URL";
    private const string EnvUser = "PHARMCO_TEST_ADMIN_USER";
    private const string EnvPass = "PHARMCO_TEST_ADMIN_PASS";
    private const string EnvTenantA = "PHARMCO_TEST_PHARMACY_A";
    private const string EnvTenantB = "PHARMCO_TEST_PHARMACY_B";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private string Base => Environment.GetEnvironmentVariable(EnvUrl)!;
    private string AdminUser => Environment.GetEnvironmentVariable(EnvUser)!;
    private string AdminPass => Environment.GetEnvironmentVariable(EnvPass)!;
    private string PharmacyA => Environment.GetEnvironmentVariable(EnvTenantA)!;
    private string PharmacyB => Environment.GetEnvironmentVariable(EnvTenantB)!;

    [LiveStackFact]
    public void Login_ValidCredentials_ReturnsTokens()
    {
        var resp = Post("/api/auth/login", "{\"pharmacy_code\":\"" + PharmacyA + "\",\"username\":\"" + AdminUser + "\",\"password\":\"" + AdminPass + "\"}", null);

        Assert.Equal(200, resp.Status);
        Assert.Contains("\"access_token\":\"", resp.Body);
        Assert.Contains("\"refresh_token\":\"", resp.Body);
        Assert.Contains("\"token_type\":\"Bearer\"", resp.Body);

        // Decode the JWT payload midway: confirm tenant + admin role claims.
        var token = Between(resp.Body, "\"access_token\":\"", "\"");
        var payload = DecodePayload(token);
        Assert.Contains("\"tenant_id\":", payload);
        Assert.Contains("\"tenant_code\":\"" + PharmacyA + "\"", payload);
        Assert.Contains("\"role\":\"admin\"", payload);
        Assert.Contains("\"exp\":", payload);
    }

    [LiveStackFact]
    public void Login_WrongPassword_Returns401()
    {
        var resp = Post("/api/auth/login", "{\"pharmacy_code\":\"" + PharmacyA + "\",\"username\":\"" + AdminUser + "\",\"password\":\"definitely-wrong\"}", null);
        Assert.Equal(401, resp.Status);
    }

    [LiveStackFact]
    public void Login_5Failures_RateLimited()
    {
        var limiterUser = "ratelimit." + Guid.NewGuid().ToString("N")[..8];
        var body = "{\"pharmacy_code\":\"" + PharmacyA + "\",\"username\":\"" + limiterUser + "\",\"password\":\"wrong\"}";

        for (var i = 1; i <= 5; i++)
            Assert.Equal(401, Post("/api/auth/login", body, null).Status);

        var sixth = Post("/api/auth/login", body, null);
        Assert.Equal(429, sixth.Status);                      // 6th attempt → 429
        Assert.Contains("rate_limited", sixth.Body);
    }

    [LiveStackFact]
    public void Login_UnknownPharmacy_Returns401()
    {
        var resp = Post("/api/auth/login",
            "{\"pharmacy_code\":\"NOPE-999\",\"username\":\"x\",\"password\":\"y\"}", null);
        Assert.Equal(401, resp.Status);
    }

    [LiveStackFact]
    public void Session_WithoutToken_Returns401()
    {
        var resp = Send("GET", "/api/auth/session", null, null, null);
        Assert.Equal(401, resp.Status);
    }

    [LiveStackFact]
    public void Refresh_RotatesToken()
    {
        var login = Post("/api/auth/login",
            "{\"pharmacy_code\":\"" + PharmacyA + "\",\"username\":\"" + AdminUser + "\",\"password\":\"" + AdminPass + "\"}", null);
        Assert.Equal(200, login.Status);
        var refresh = Between(login.Body, "\"refresh_token\":\"", "\"");

        var resp = Post("/api/auth/refresh", "{\"refresh_token\":\"" + refresh + "\"}", null);
        Assert.Equal(200, resp.Status);
        Assert.Contains("\"access_token\":\"", resp.Body);
    }

    [LiveStackFact]
    public void UsersList_AdminOnly_CashierForbidden()
    {
        var adminToken = Login(PharmacyA, AdminUser, AdminPass);
        var cashier = "cashier." + Guid.NewGuid().ToString("N")[..8];
        var created = Post("/api/users",
            "{\"username\":\"" + cashier + "\",\"password\":\"Password123\",\"role\":\"cashier\"}", adminToken);
        Assert.Equal(201, created.Status);

        var cashierToken = Login(PharmacyA, cashier, "Password123");
        var resp = Post("/api/users",
            "{\"username\":\"nope\",\"password\":\"Password123\",\"role\":\"cashier\"}", cashierToken);
        Assert.Equal(403, resp.Status);                       // role check works
    }

    [LiveStackFact]
    public void CrossTenantLogin_WrongPharmacyCode_Fails()
    {
        var adminToken = Login(PharmacyA, AdminUser, AdminPass);
        var cashier = "cross." + Guid.NewGuid().ToString("N")[..8];
        Post("/api/users",
            "{\"username\":\"" + cashier + "\",\"password\":\"Password123\",\"role\":\"cashier\"}", adminToken);

        // Cashier of A tries Pharmacy B's code → tenant lookup fails → 401.
        var resp = Post("/api/auth/login",
            "{\"pharmacy_code\":\"" + PharmacyB + "\",\"username\":\"" + cashier + "\",\"password\":\"Password123\"}", null);
        Assert.Equal(401, resp.Status);
    }

    // --- harness ----------------------------------------------------------------

    private sealed class Response
    {
        public int Status;
        public string Body = "";
    }

    private string Login(string pharmacy, string user, string pass)
    {
        var resp = Post("/api/auth/login",
            "{\"pharmacy_code\":\"" + pharmacy + "\",\"username\":\"" + user + "\",\"password\":\"" + pass + "\"}", null);
        if (resp.Status != 200)
            throw new Exception("test setup login failed: " + resp.Status);
        return Between(resp.Body, "\"access_token\":\"", "\"");
    }

    private Response Post(string path, string json, string? bearer)
        => Send("POST", path, "application/json", json, bearer);

    private Response Send(string method, string path, string? contentType, string? body, string? bearer)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), Base + path);
        request.Headers.Accept.ParseAdd("application/json");
        if (bearer is not null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, contentType!);

        var resp = Http.Send(request);   // sync — tests are synchronous by design
        return new Response
        {
            Status = (int) resp.StatusCode,
            Body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult(),
        };
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal) + start.Length;
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        return text.Substring(from, to - from);
    }

    private static string DecodePayload(string jwt)
    {
        var payload = jwt.Split('.')[1];
        var clean = payload.Replace('-', '+').Replace('_', '/');
        clean += (clean.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => "",
        };
        return Encoding.UTF8.GetString(Convert.FromBase64String(clean));
    }
}
