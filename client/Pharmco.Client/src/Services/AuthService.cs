namespace Pharmco.Client.Services;

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Pharmco.Client.Models;

/// <summary>
/// HTTP client for the Pharmco API auth surface. Thin on purpose — the BCL
/// <see cref="HttpClient"/> plus <see cref="System.Text.Json"/>.
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
        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AllowAutoRedirect = false,          // never follow redirects on auth calls
        })
        {
            Timeout = TimeSpan.FromSeconds(25), // whole-request budget
        };
    }

    // --- login -------------------------------------------------------------------

    public async Task<LoginResponse> LoginAsync(string pharmacyCode, string username, string password)
    {
        var body = JsonSerializer.Serialize(new
        {
            pharmacy_code = pharmacyCode,
            username,
            password,
        });
        var (status, text) = await SendAsync(HttpMethod.Post, "/api/auth/login", body, null);
        if (status != 200)
            throw Failure(status, text);
        return ParseLogin(text);
    }

    public async Task<LoginResponse> RefreshAsync(string refreshToken)
    {
        var body = JsonSerializer.Serialize(new { refresh_token = refreshToken });
        var (status, text) = await SendAsync(HttpMethod.Post, "/api/auth/refresh", body, null);
        if (status != 200)
            throw Failure(status, text);
        return ParseLogin(text);
    }

    /// <summary>Best-effort: revokes the refresh session server-side. Throws AuthError on failure.</summary>
    public async Task LogoutAsync(string accessToken, string? refreshToken)
    {
        var body = string.IsNullOrEmpty(refreshToken)
            ? null
            : JsonSerializer.Serialize(new { refresh_token = refreshToken });
        var (status, _) = await SendAsync(HttpMethod.Post, "/api/auth/logout", body, accessToken);
        if (status != 204 && status != 401)
            throw Failure(status, "");
    }

    /// <summary>GET /api/auth/session — true when the access token is still valid.</summary>
    public async Task<bool> ValidateOnlineAsync(string accessToken)
    {
        var (status, _) = await SendAsync(HttpMethod.Get, "/api/auth/session", null, accessToken);
        return status == 200;
    }

    // --- internals ----------------------------------------------------------------

    private async Task<(int Status, string Body)> SendAsync(HttpMethod method, string path, string? body, string? accessToken)
    {
        using var request = new HttpRequestMessage(method, _baseUrl + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(accessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text);
    }

    private static LoginResponse ParseLogin(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var user = root.TryGetProperty("user", out var userElement) ? userElement : default;

        return new LoginResponse
        {
            AccessToken = GetString(root, "access_token"),
            RefreshToken = GetString(root, "refresh_token"),
            TokenType = GetString(root, "token_type"),
            ExpiresIn = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
                ? seconds
                : 0,
            User = new UserProfile
            {
                Id = GetString(user, "id"),
                Role = GetString(user, "role"),
                TenantCode = GetString(user, "tenant_code"),
            },
        };
    }

    private static AuthError Failure(int status, string body)
    {
        var code = "http_" + status;
        var message = "request failed with status " + status;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var errorCode = GetString(error, "code");
                var errorMessage = GetString(error, "message");
                if (errorCode.Length > 0) code = errorCode;
                if (errorMessage.Length > 0) message = errorMessage;
            }
        }
        catch (JsonException) { /* non-JSON error body — keep defaults */ }
        return new AuthError(code, message);
    }

    /// <summary>Reads a string property off an object element ("" when absent or not a string).</summary>
    private static string GetString(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string TrimTrailingSlash(string url)
    {
        var trimmed = url;
        while (trimmed.EndsWith("/") && trimmed.Length > 1)
            trimmed = trimmed[..^1];
        return trimmed;
    }
}
