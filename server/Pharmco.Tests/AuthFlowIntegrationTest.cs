using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace Pharmco.Tests;

/// <summary>
/// LIVE-STACK integration tests. They hit a running API backed by Postgres with
/// db/master/003_refresh_tokens.sql applied (see .tmp/smoke.sql provider flow).
///
/// RUN:
///   PHARMCO_TEST_API_URL=http://localhost:8080 \
///   PHARMCO_TEST_ADMIN_USER=admin@nairobi-chemist \
///   PHARMCO_TEST_ADMIN_PASS=... \
///   PHARMCO_TEST_PHARMACY_A=PHARMCO-001 \
///   PHARMCO_TEST_PHARMACY_B=PHARMCO-002 \
///   dotnet test                                   # from server/Pharmco.Tests
///
/// Without PHARMCO_TEST_API_URL the tests are skipped (see LiveStackFactAttribute),
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

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static string Base => Required(EnvUrl);
    private static string AdminUser => Required(EnvUser);
    private static string AdminPass => Required(EnvPass);
    private static string PharmacyA => Required(EnvTenantA);
    private static string PharmacyB => Required(EnvTenantB);

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
        Assert.Equal(429, sixth.Status);                  // 6th attempt → 429
        Assert.Contains("rate_limited", sixth.Body);
    }

    [LiveStackFact]
    public void Login_InactiveUser_Returns403()
    {
        var adminToken = Login(PharmacyA, AdminUser, AdminPass);

        // Create a user, deactivate it, then log in → 403.
        var victim = "inactive." + Guid.NewGuid().ToString("N")[..8];
        var created = Post("/api/users",
            "{\"username\":\"" + victim + "\",\"password\":\"Password123\",\"role\":\"pharmacist\"}", adminToken);
        Assert.Equal(201, created.Status);
        var id = Between(created.Body, "\"id\":\"", "\"");
        Assert.Equal(200, Patch("/api/users/" + id, "{\"is_active\":false}", adminToken).Status);

        var login = Post("/api/auth/login",
            "{\"pharmacy_code\":\"" + PharmacyA + "\",\"username\":\"" + victim + "\",\"password\":\"Password123\"}", null);
        Assert.Equal(403, login.Status);
        Assert.Contains("account_disabled", login.Body);
    }

    [LiveStackFact]
    public void Refresh_RotatesToken()
    {
        var login = Post("/api/auth/login",
            "{\"pharmacy_code\":\"" + PharmacyA + "\",\"username\":\"" + AdminUser + "\",\"password\":\"" + AdminPass + "\"}", null);
        var oldRefresh = Between(login.Body, "\"refresh_token\":\"", "\"");

        var rotated = Post("/api/auth/refresh", "{\"refresh_token\":\"" + oldRefresh + "\"}", null);
        Assert.Equal(200, rotated.Status);

        // Old refresh token is now revoked → reusing it is rejected (and kills the family).
        var reuse = Post("/api/auth/refresh", "{\"refresh_token\":\"" + oldRefresh + "\"}", null);
        Assert.Equal(401, reuse.Status);
    }

    [LiveStackFact]
    public void User_CreateByAdmin_Succeeds()
    {
        var adminToken = Login(PharmacyA, AdminUser, AdminPass);
        var username = "pharm1." + Guid.NewGuid().ToString("N")[..8];

        var resp = Post("/api/users",
            "{\"username\":\"" + username + "\",\"password\":\"Password123\",\"role\":\"pharmacist\"}", adminToken);
        Assert.Equal(201, resp.Status);
        Assert.Contains("\"role\":\"pharmacist\"", resp.Body);

        // The new pharmacist can actually log in.
        var login = Post("/api/auth/login",
            "{\"pharmacy_code\":\"" + PharmacyA + "\",\"username\":\"" + username + "\",\"password\":\"Password123\"}", null);
        Assert.Equal(200, login.Status);
    }

    [LiveStackFact]
    public void User_CreateByCashier_Returns403()
    {
        var adminToken = Login(PharmacyA, AdminUser, AdminPass);
        var cashier = "cashier." + Guid.NewGuid().ToString("N")[..8];
        Post("/api/users",
            "{\"username\":\"" + cashier + "\",\"password\":\"Password123\",\"role\":\"cashier\"}", adminToken);

        var cashierToken = Login(PharmacyA, cashier, "Password123");
        var resp = Post("/api/users",
            "{\"username\":\"nope\",\"password\":\"Password123\",\"role\":\"cashier\"}", cashierToken);
        Assert.Equal(403, resp.Status);                   // role check works
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

    private static string Required(string name)
        => Environment.GetEnvironmentVariable(name)
           ?? throw new InvalidOperationException($"{name} must be set to run live-stack tests");

    private sealed record Response(int Status, string Body);

    private static string Login(string pharmacy, string user, string pass)
    {
        var resp = Post("/api/auth/login",
            "{\"pharmacy_code\":\"" + pharmacy + "\",\"username\":\"" + user + "\",\"password\":\"" + pass + "\"}", null);
        if (resp.Status != 200)
            throw new Exception("test setup login failed: " + resp.Status);
        return Between(resp.Body, "\"access_token\":\"", "\"");
    }

    private static Response Post(string path, string json, string? bearer)
        => Send(HttpMethod.Post, path, json, bearer);

    private static Response Patch(string path, string json, string? bearer)
        => Send(HttpMethod.Patch, path, json, bearer);

    private static Response Send(HttpMethod method, string path, string body, string? bearer)
    {
        using var request = new HttpRequestMessage(method, Base + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = Client.Send(request);
        var responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return new Response((int)response.StatusCode, responseBody);
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
        var pad = (4 - clean.Length % 4) % 4;
        for (var i = 0; i < pad; i++) clean += "=";
        return Encoding.UTF8.GetString(Convert.FromBase64String(clean));
    }
}
