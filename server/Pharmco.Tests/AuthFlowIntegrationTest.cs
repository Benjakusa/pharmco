import xunit.*;

using java.net.URI;
using java.net.http;
using java.nio.charset;
using java.time;

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
/// Without PHARMCO_TEST_API_URL the tests are skipped (assumption failure),
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

    private string Base => System.getenv(EnvUrl)!;
    private string AdminUser => System.getenv(EnvUser)!;
    private string AdminPass => System.getenv(EnvPass)!;
    private string PharmacyA => System.getenv(EnvTenantA)!;
    private string PharmacyB => System.getenv(EnvTenantB)!;

    [Fact]
    public void Login_ValidCredentials_ReturnsTokens()
    {
        RequiresLiveStack();
        var resp = Post("/api/auth/login", "{\"pharmacy_code\":\"" + PharmacyA + "\",\"username\":\"" + AdminUser + "\",\"password\":\"" + AdminPass + "\"}", null);

        assertEq(200, resp.Status);
        assertTrue(resp.Body.Contains("\"access_token\":\""));
        assertTrue(resp.Body.Contains("\"refresh_token\":\""));
        assertTrue(resp.Body.Contains("\"token_type\":\"Bearer\""));

        // Decode the JWT payload midway: confirm tenant + admin role claims.
        var token = Between(resp.Body, "\"access_token\":\"", "\"");
        var payload = DecodePayload(token);
        assertTrue(payload.Contains("\"tenant_id\":"));
        assertTrue(payload.Contains("\"tenant_code\":\"" + PharmacyA + "\""));
        assertTrue(payload.Contains("\"role\":\"admin\""));
        assertTrue(payload.Contains("\"exp\":"));
    }

    [Fact]
    public void Login_WrongPassword_Returns401()
    {
        RequiresLiveStack();
        var resp = Post("/api/auth/login", "{\"pharmacy_code\":\"" + PharmacyA + "\",\"username\":\"" + AdminUser + "\",\"password\":\"definitely-wrong\"}", null);
        assertEq(401, resp.Status);
    }

    [Fact]
    public void Login_5Failures_RateLimited()
    {
        RequiresLiveStack();
        var limiterUser = "ratelimit." + java.util.UUID.randomUUID().ToString().Substring(0, 8);
        var body = "{\"pharmacy_code\":\"" + PharmacyA + "\",\"username\":\"" + limiterUser + "\",\"password\":\"wrong\"}";

        for (var i = 1; i <= 5; i++)
            assertEq(401, Post("/api/auth/login", body, null).Status);

        var sixth = Post("/api/auth/login", body, null);
        assertEq(429, sixth.Status);                      // 6th attempt → 429
        assertTrue(sixth.Body.Contains("rate_limited"));
    }

    [Fact]
    public void Login_InactiveUser_Returns403()
    {
        RequiresLiveStack();
        var adminToken = Login(PharmacyA, AdminUser, AdminPass);

        // Create a user, deactivate it, then log in → 403.
        var victim = "inactive." + java.util.UUID.randomUUID().ToString().Substring(0, 8);
        var created = Post("/api/users",
            "{\"username\":\"" + victim + "\",\"password\":\"Password123\",\"role\":\"pharmacist\"}", adminToken);
        assertEq(201, created.Status);
        var id = Between(created.Body, "\"id\":\"", "\"");
        assertEq(200, Patch("/api/users/" + id, "{\"is_active\":false}", adminToken).Status);

        var login = Post("/api/auth/login",
            "{\"pharmacy_code\":\"" + PharmacyA + "\",\"username\":\"" + victim + "\",\"password\":\"Password123\"}", null);
        assertEq(403, login.Status);
        assertTrue(login.Body.Contains("account_disabled"));
    }

    [Fact]
    public void Refresh_RotatesToken()
    {
        RequiresLiveStack();
        var login = Post("/api/auth/login",
            "{\"pharmacy_code\":\"" + PharmacyA + "\",\"username\":\"" + AdminUser + "\",\"password\":\"" + AdminPass + "\"}", null);
        var oldRefresh = Between(login.Body, "\"refresh_token\":\"", "\"");

        var rotated = Post("/api/auth/refresh", "{\"refresh_token\":\"" + oldRefresh + "\"}", null);
        assertEq(200, rotated.Status);

        // Old refresh token is now revoked → reusing it is rejected (and kills the family).
        var reuse = Post("/api/auth/refresh", "{\"refresh_token\":\"" + oldRefresh + "\"}", null);
        assertEq(401, reuse.Status);
    }

    [Fact]
    public void User_CreateByAdmin_Succeeds()
    {
        RequiresLiveStack();
        var adminToken = Login(PharmacyA, AdminUser, AdminPass);
        var username = "pharm1." + java.util.UUID.randomUUID().ToString().Substring(0, 8);

        var resp = Post("/api/users",
            "{\"username\":\"" + username + "\",\"password\":\"Password123\",\"role\":\"pharmacist\"}", adminToken);
        assertEq(201, resp.Status);
        assertTrue(resp.Body.Contains("\"role\":\"pharmacist\""));

        // The new pharmacist can actually log in.
        var login = Post("/api/auth/login",
            "{\"pharmacy_code\":\"" + PharmacyA + "\",\"username\":\"" + username + "\",\"password\":\"Password123\"}", null);
        assertEq(200, login.Status);
    }

    [Fact]
    public void User_CreateByCashier_Returns403()
    {
        RequiresLiveStack();
        var adminToken = Login(PharmacyA, AdminUser, AdminPass);
        var cashier = "cashier." + java.util.UUID.randomUUID().ToString().Substring(0, 8);
        Post("/api/users",
            "{\"username\":\"" + cashier + "\",\"password\":\"Password123\",\"role\":\"cashier\"}", adminToken);

        var cashierToken = Login(PharmacyA, cashier, "Password123");
        var resp = Post("/api/users",
            "{\"username\":\"nope\",\"password\":\"Password123\",\"role\":\"cashier\"}", cashierToken);
        assertEq(403, resp.Status);                       // role check works
    }

    [Fact]
    public void CrossTenantLogin_WrongPharmacyCode_Fails()
    {
        RequiresLiveStack();
        var adminToken = Login(PharmacyA, AdminUser, AdminPass);
        var cashier = "cross." + java.util.UUID.randomUUID().ToString().Substring(0, 8);
        Post("/api/users",
            "{\"username\":\"" + cashier + "\",\"password\":\"Password123\",\"role\":\"cashier\"}", adminToken);

        // Cashier of A tries Pharmacy B's code → tenant lookup fails → 401.
        var resp = Post("/api/auth/login",
            "{\"pharmacy_code\":\"" + PharmacyB + "\",\"username\":\"" + cashier + "\",\"password\":\"Password123\"}", null);
        assertEq(401, resp.Status);
    }

    // --- harness ----------------------------------------------------------------

    private void RequiresLiveStack()
        => Assumptions.assumeNotNull(System.getenv(EnvUrl),
            "set PHARMCO_TEST_API_URL to run live integration tests");

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

    private Response Patch(string path, string json, string? bearer)
        => Send("PATCH", path, "application/json", json, bearer);

    private Response Send(string method, string path, string contentType, string body, string? bearer)
    {
        var builder = HttpRequest.newBuilder(URI.Create(Base + path))
            .header("accept", "application/json");
        if (bearer is not null)
            builder.header("authorization", "Bearer " + bearer);
        builder.header("content-type", contentType);
        builder.method(method, HttpRequest.BodyPublishers.ofString(body));

        var client = java.net.http.HttpClient.newBuilder()
            .connectTimeout(Duration.ofSeconds(10))
            .build();
        var resp = client.sendAsync(builder.build()).Join();   // block (JDK CompletableFuture) — tests are synchronous
        return new Response { Status = resp.statusCode(), Body = resp.body().string() };
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start) + start.Length;
        var to = text.IndexOf(end, from);
        return text.Substring(from, to - from);
    }

    private static string DecodePayload(string jwt)
    {
        var payload = jwt.Split('.')[1];
        var clean = payload.Replace('-', '+').Replace('_', '/');
        var pad = (4 - clean.Length % 4) % 4;
        for (var i = 0; i < pad; i++) clean += "=";
        return new string(java.util.Base64.getDecoder().decode(clean), Charset.UTF_8);
    }
}