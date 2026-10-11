namespace Pharmco.Client.Services;

using System.Net.Http;
using System.Text;
using System.Text.Json;
using Pharmco.Client.Models;

/// <summary>
/// HTTP client for the Pharmco API auth surface. Thin on purpose.
/// Endpoints: POST /api/auth/login · POST /api/auth/refresh ·
/// POST /api/auth/logout · GET /api/auth/session.
/// </summary>
public sealed class AuthService : IDisposable
{
    private readonly string _baseUrl;
    private readonly HttpClient _http;

    public AuthService(string baseUrl)
    {
        _baseUrl = TrimTrailingSlash(baseUrl);
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(25),
        };
    }

    // --- login -------------------------------------------------------------------

    public async Task<LoginResponse> LoginAsync(string pharmacyCode, string username, string password)
    {
        var body = Json.Obj(
            "pharmacy_code", pharmacyCode,
            "username", username,
            "password", password);
        var response = await SendAsync("POST", "/api/auth/login", body, null);
        if ((int) response.StatusCode != 200)
            throw await FailureAsync(response);
        return ParseLogin(await ReadBodyAsync(response));
    }

    public async Task<LoginResponse> RefreshAsync(string refreshToken)
    {
        var response = await SendAsync("POST", "/api/auth/refresh", Json.Obj("refresh_token", refreshToken), null);
        if ((int) response.StatusCode != 200)
            throw await FailureAsync(response);
        return ParseLogin(await ReadBodyAsync(response));
    }

    /// <summary>Best-effort: revokes the refresh session server-side. Throws AuthError on failure.</summary>
    public async Task LogoutAsync(string accessToken, string? refreshToken)
    {
        var body = string.IsNullOrEmpty(refreshToken)
            ? null
            : Json.Obj("refresh_token", refreshToken);
        var response = await SendAsync("POST", "/api/auth/logout", body, accessToken);
        var status = (int) response.StatusCode;
        if (status != 204 && status != 401)
            throw await FailureAsync(response);
    }

    /// <summary>GET /api/auth/session — true when the access token is still valid.</summary>
    public async Task<bool> ValidateOnlineAsync(string accessToken)
    {
        var response = await SendAsync("GET", "/api/auth/session", null, accessToken);
        return (int) response.StatusCode == 200;
    }

    // --- internals ----------------------------------------------------------------

    private async Task<HttpResponseMessage> SendAsync(string method, string path, JsonValue? body, string? accessToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), _baseUrl + path);
        request.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrEmpty(accessToken))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null)
        {
            request.Content = new StringContent(Json.Stringify(body), Encoding.UTF8, "application/json");
        }
        return await _http.SendAsync(request);
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response)
        => await response.Content.ReadAsStringAsync();

    private static LoginResponse ParseLogin(string json)
    {
        var root = (JsonObject) Json.Parse(json);
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

    private static async Task<AuthError> FailureAsync(HttpResponseMessage response)
    {
        var status = (int) response.StatusCode;
        string code = "http_" + status;
        string message = "request failed with status " + status;
        try
        {
            var root = (JsonObject) Json.Parse(await ReadBodyAsync(response));
            var error = root.GetObject("error");
            if (error is not null)
            {
                code = error.GetString("code") ?? code;
                message = error.GetString("message") ?? message;
            }
        }
        catch (JsonException) { /* non-JSON error body — keep defaults */ }
        catch (Json.ParseException) { /* non-JSON error body — keep defaults */ }
        return new AuthError(code, message);
    }

    public void Dispose() => _http.Dispose();

    private static string TrimTrailingSlash(string url)
    {
        var trimmed = url;
        while (trimmed.EndsWith('/') && trimmed.Length > 1)
            trimmed = trimmed.Substring(0, trimmed.Length - 1);
        return trimmed;
    }
}
