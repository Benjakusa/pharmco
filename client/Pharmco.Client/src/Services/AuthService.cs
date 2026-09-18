namespace Pharmco.Client.Services;

using java.net.URI;
using java.net.http;
using java.time;
using Pharmco.Client.Models;

/// <summary>
/// HTTP client for the Pharmco API auth surface. Thin on purpose — the
/// successor's <c>java.net.http</c> client plus the tiny Json helper.
/// Endpoints: POST /api/auth/login · POST /api/auth/refresh ·
/// POST /api/auth/logout · GET /api/auth/session.
/// </summary>
public sealed class AuthService
{
    private readonly string _baseUrl;
    private readonly HttpClient _http;

    public AuthService(string baseUrl)
    {
        _baseUrl = TrimTrailingSlash(baseUrl);
        _http = java.net.http.HttpClient.newBuilder()
            .connectTimeout(Duration.ofSeconds(10))
            .requestTimeout(Duration.ofSeconds(25))
            .followRedirects(HttpClient.Redirect.NEVER)
            .build();
    }

    // --- login -------------------------------------------------------------------

    public async Task<LoginResponse> LoginAsync(string pharmacyCode, string username, string password)
    {
        var body = Json.Obj(
            "pharmacy_code", pharmacyCode,
            "username", username,
            "password", password);
        var response = await SendAsync("POST", "/api/auth/login", body, null);
        if (response.StatusCode() != 200)
            throw Failure(response);
        return ParseLogin(response);
    }

    public async Task<LoginResponse> RefreshAsync(string refreshToken)
    {
        var response = await SendAsync("POST", "/api/auth/refresh", Json.Obj("refresh_token", refreshToken), null);
        if (response.StatusCode() != 200)
            throw Failure(response);
        return ParseLogin(response);
    }

    /// <summary>Best-effort: revokes the refresh session server-side. Throws AuthError on failure.</summary>
    public async Task LogoutAsync(string accessToken, string? refreshToken)
    {
        var body = refreshToken is null || refreshToken.IsEmpty()
            ? null
            : Json.Obj("refresh_token", refreshToken);
        var response = await SendAsync("POST", "/api/auth/logout", body, accessToken);
        if (response.StatusCode() != 204 && response.StatusCode() != 401)
            throw Failure(response);
    }

    /// <summary>GET /api/auth/session — true when the access token is still valid.</summary>
    public async Task<bool> ValidateOnlineAsync(string accessToken)
    {
        var response = await SendAsync("GET", "/api/auth/session", null, accessToken);
        return response.StatusCode() == 200;
    }

    // --- internals ----------------------------------------------------------------

    private async Task<HttpResponse> SendAsync(string method, string path, JsonObject? body, string? accessToken)
    {
        var builder = HttpRequest.newBuilder(URI.Create(_baseUrl + path));
        builder.header("accept", "application/json");
        if (accessToken is not null && !accessToken.IsEmpty())
            builder.header("authorization", "Bearer " + accessToken);
        if (body is not null)
        {
            builder.header("content-type", "application/json");
            builder.method(method, HttpRequest.BodyPublishers.ofString(Json.Stringify(body)));
        }
        else
        {
            builder.method(method, HttpRequest.BodyPublishers.noBody());
        }
        return await _http.sendAsync(builder.build());
    }

    private LoginResponse ParseLogin(HttpResponse response) throws AuthError
    {
        var root = (JsonObject) Json.Parse(ReadBody(response));
        var userObj = root.GetObject("user") ?? new JsonObject();
        return new LoginResponse
        {
            AccessToken = root.GetString("access_token") ?? "",
            RefreshToken = root.GetString("refresh_token") ?? "",
            TokenType = root.GetString("token_type") ?? "Bearer",
            ExpiresIn = (int) root.GetLong("expires_in", 0),
            User = new UserProfile
            {
                Id = userObj.GetString("id") ?? "",
                Role = userObj.GetString("role") ?? "",
                TenantCode = userObj.GetString("tenant_code") ?? "",
            },
        };
    }

    private AuthError Failure(HttpResponse response)
    {
        string code = "http_" + response.StatusCode();
        string message = "request failed with status " + response.StatusCode();
        try
        {
            var root = (JsonObject) Json.Parse(ReadBody(response));
            var error = root.GetObject("error");
            if (error is not null)
            {
                code = error.GetString("code") ?? code;
                message = error.GetString("message") ?? message;
            }
        }
        catch (Throwable) { /* non-JSON error body — keep defaults */ }
        return new AuthError(code, message);
    }

    private static string ReadBody(HttpResponse response) => response.Body().String();

    private static string TrimTrailingSlash(string url)
    {
        var trimmed = url;
        while (trimmed.EndsWith("/") && trimmed.Length > 1)
            trimmed = trimmed.Substring(0, trimmed.Length - 1);
        return trimmed;
    }
}