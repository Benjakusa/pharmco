namespace Pharmco.Api.Services.Daraja;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Polly;
using Dapper;
using Npgsql;
using Pharmco.Core.Security;
using Pharmco.Core.Sales;
using Pharmco.Core.Data;
using Microsoft.Extensions.Logging;

/// <summary>
/// Per-tenant M-Pesa Daraja integration: OAuth, STK Push, Transaction Status.
/// Credentials are read from master.daraja_config (AES-256-GCM encrypted) and
/// cached in-memory per tenant for the OAuth token lifetime (50 min).
/// </summary>
public sealed class DarajaService : IDisposable
{
    private readonly NpgsqlConnectionFactory _factory;
    private readonly ILogger<DarajaService> _logger;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<Guid, (string Token, DateTimeOffset ExpiresAt)> _tokenCache
        = new();

    private const string CallbackUrl = "https://api.pharmco.co.ke/api/mpesa/callback";
    private const int TokenCacheEarlyExpiryMinutes = 5;

    public DarajaService(NpgsqlConnectionFactory factory, ILogger<DarajaService> logger)
    {
        _factory = factory;
        _logger = logger;

        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.safaricom.co.ke"),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    // ------------------------------------------------------------------
    // OAuth
    // ------------------------------------------------------------------

    /// <summary>
    /// Gets a cached or fresh OAuth token for the tenant. Tokens are cached
    /// for 50 minutes (Safaricom tokens expire in 1 hour; we refresh early).
    /// Uses Polly retry (3 attempts, exponential backoff) on network failures.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (_tokenCache.TryGetValue(tenantId, out var cached)
            && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(TokenCacheEarlyExpiryMinutes))
        {
            _logger.LogDebug("Using cached OAuth token for tenant {TenantId}", tenantId);
            return cached.Token;
        }

        var creds = await GetCredentialsAsync(tenantId, ct)
            ?? throw new InvalidOperationException($"No Daraja config for tenant {tenantId}");

        var request = new HttpRequestMessage(HttpMethod.Get,
            "/oauth/v1/generate?grant_type=client_credentials");
        request.Headers.Authorization = new BasicAuthenticationCredential(creds.ConsumerKey, creds.ConsumerSecret);

        var response = await ExecuteWithRetryAsync(async () =>
        {
            var msg = await _http.SendAsync(request, ct);
            msg.EnsureSuccessStatusCode();
            return msg;
        }, ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        var tokenResponse = JsonSerializer.Deserialize<OAuthTokenResponse>(json)
            ?? throw new InvalidOperationException("Failed to parse OAuth response");

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn);
        _tokenCache[tenantId] = (tokenResponse.AccessToken, expiresAt);

        _logger.LogInformation("OAuth token obtained for tenant {TenantId}, expires at {ExpiresAt}",
            tenantId, expiresAt);

        return tokenResponse.AccessToken;
    }

    // ------------------------------------------------------------------
    // STK Push
    // ------------------------------------------------------------------

    /// <summary>
    /// Initiates an STK Push to the customer's phone. Returns the CheckoutRequestID
    /// which is used to match the callback.
    /// </summary>
    public async Task<StkPushResult> PushStkAsync(
        Guid tenantId,
        string phoneNumber,
        int amountKSh,
        string invoiceNo,
        string? merchantReference = null,
        CancellationToken ct = default)
    {
        var creds = await GetCredentialsAsync(tenantId, ct)
            ?? throw new InvalidOperationException($"No Daraja config for tenant {tenantId}");

        var accessToken = await GetAccessTokenAsync(tenantId, ct);

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        var password = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{creds.Shortcode}{creds.Passkey}{timestamp}"));

        var requestBody = new StkPushRequest
        {
            BusinessShortCode = creds.Shortcode,
            Password = password,
            Timestamp = timestamp,
            TransactionType = "CustomerPayBillOnline",
            Amount = amountKSh,
            PartyA = phoneNumber,
            PartyB = creds.Shortcode,
            PhoneNumber = phoneNumber,
            CallBackURL = CallbackUrl,
            AccountReference = invoiceNo,
            TransactionDesc = merchantReference ?? $"Pharmco Invoice {invoiceNo}",
        };

        var json = JsonSerializer.Serialize(requestBody);
        _logger.LogInformation("STK Push request for tenant {TenantId}, invoice {InvoiceNo}, " +
            "phone {Phone}, amount KSh {Amount} — shortcode {Shortcode}",
            tenantId, invoiceNo, MaskPhone(phoneNumber), amountKSh, creds.Shortcode);

        var request = new HttpRequestMessage(HttpMethod.Post, "/mpesa/stkpush/v1/processrequest");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await ExecuteWithRetryAsync(async () =>
        {
            var msg = await _http.SendAsync(request, ct);
            msg.EnsureSuccessStatusCode();
            return msg;
        }, ct);

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        _logger.LogInformation("STK Push response for tenant {TenantId}, invoice {InvoiceNo}: {Response}",
            tenantId, invoiceNo, RedactSecrets(responseJson));

        var stkResponse = JsonSerializer.Deserialize<StkPushResponse>(responseJson)
            ?? throw new InvalidOperationException("Failed to parse STK Push response");

        if (stkResponse.ResponseCode != "0")
        {
            _logger.LogWarning("STK Push rejected for tenant {TenantId}, invoice {InvoiceNo}: {Message}",
                tenantId, invoiceNo, stkResponse.CustomerMessage);
            return new StkPushResult
            {
                Success = false,
                CheckoutRequestId = stkResponse.CheckoutRequestID,
                Error = stkResponse.CustomerMessage ?? "STK Push failed",
            };
        }

        return new StkPushResult
        {
            Success = true,
            CheckoutRequestId = stkResponse.CheckoutRequestID,
            MerchantRequestId = stkResponse.MerchantRequestID,
            ResponseDescription = stkResponse.ResponseDescription,
        };
    }

    // ------------------------------------------------------------------
    // Transaction Status (for offline verification)
    // ------------------------------------------------------------------

    /// <summary>
    /// Queries the Daraja Transaction Status API for a given checkout request.
    /// Used by the offline verification job.
    /// </summary>
    public async Task<TransactionStatusResult?> GetTransactionStatusAsync(
        Guid tenantId,
        string checkoutRequestId,
        CancellationToken ct = default)
    {
        var creds = await GetCredentialsAsync(tenantId, ct)
            ?? throw new InvalidOperationException($"No Daraja config for tenant {tenantId}");

        var accessToken = await GetAccessTokenAsync(tenantId, ct);

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        var password = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{creds.Shortcode}{creds.Passkey}{timestamp}"));

        var body = JsonSerializer.Serialize(new
        {
            BusinessShortCode = creds.Shortcode,
            Password = password,
            Timestamp = timestamp,
            CheckoutRequestID = checkoutRequestId,
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/mpesa/stkpushquery/v1/query");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await ExecuteWithRetryAsync(async () =>
        {
            var msg = await _http.SendAsync(request, ct);
            msg.EnsureSuccessStatusCode();
            return msg;
        }, ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        _logger.LogInformation("Transaction Status query for tenant {TenantId}, checkout {CheckoutId}",
            tenantId, checkoutRequestId);

        var statusResponse = JsonSerializer.Deserialize<TransactionStatusResponse>(json);
        if (statusResponse?.CallbackMetadata?.Item?.Values is null)
            return null;

        var values = statusResponse.CallbackMetadata.Item.Values;
        return new TransactionStatusResult
        {
            ResponseCode = statusResponse.ResponseCode,
            ResponseDescription = statusResponse.ResponseDescription,
            MpesaReceiptNumber = GetValue(values, "MpesaReceiptNumber"),
            PhoneNumber = GetValue(values, "PhoneNumber"),
            Amount = GetValue(values, "Amount"),
            TransactionDate = GetValue(values, "TransactionDate"),
        };
    }

    // ------------------------------------------------------------------
    // Credentials
    // ------------------------------------------------------------------

    public async Task<DarajaCredentials?> GetCredentialsAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<DarajaConfigRow>(
            "SELECT * FROM master.daraja_config WHERE tenant_id = @TenantId",
            new { TenantId = tenantId }, commandTimeout: 30);

        if (row is null) return null;

        var encryptionKey = Environment.GetEnvironmentVariable("Daraja__EncryptionKey")
            ?? throw new InvalidOperationException("Daraja__EncryptionKey not configured");

        var encryption = new DarajaEncryption(encryptionKey);

        return new DarajaCredentials
        {
            ConsumerKey = encryption.Decrypt(row.ConsumerKeyEnc),
            ConsumerSecret = encryption.Decrypt(row.ConsumerSecretEnc),
            Passkey = encryption.Decrypt(row.PasskeyEnc),
            Shortcode = row.Shortcode,
            ShortcodeType = row.ShortcodeType,
        };
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static string GetValue(List<TransactionStatusValue>? values, string key)
        => values?.FirstOrDefault(v => v.Key == key)?.Value ?? "";

    private static string MaskPhone(string phone)
        => phone.Length > 4 ? "****" + phone[^4..] : "****";

    /// <summary>
    /// Redacts secrets from log output.
    /// </summary>
    internal static string RedactSecrets(string json)
    {
        if (string.IsNullOrEmpty(json)) return json;
        return Regex.Replace(json,
            @"""consumer_key""\s*:\s*""[^"""]+""", @"""consumer_key""\s*:\s*""****""");
    }

    /// <summary>
    /// Executes an async operation with Polly retry (3 attempts, exponential backoff).
    /// </summary>
    private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        return await Policy
            .Handle<HttpRequestException>()
            .Or<TaskCanceledException>()
            .WaitAndRetryAsync(
                3,
                retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                (exception, timeSpan, retryCount, context) =>
                {
                    _logger.LogWarning(exception,
                        "Daraja request retry {RetryCount} after {Delay}s: {Message}",
                        retryCount, timeSpan.TotalSeconds, exception.Message);
                })
            .ExecuteAsync(operation);
    }

    public void Dispose()
    {
        _http.Dispose();
        _tokenCache.Clear();
    }

    // ------------------------------------------------------------------
    // Connection access for endpoints
    // ------------------------------------------------------------------

    public NpgsqlConnectionFactory ConnectionFactory => _factory;

    public async Task<NpgsqlConnection> GetOpenConnectionAsync(CancellationToken ct = default)
        => await _factory.OpenAsync(ct);

    // ------------------------------------------------------------------
    // Inner types
    // ------------------------------------------------------------------

    public sealed class StkPushResult
    {
        public bool Success { get; init; }
        public string? CheckoutRequestId { get; init; }
        public string? MerchantRequestId { get; init; }
        public string? ResponseDescription { get; init; }
        public string? Error { get; init; }
    }

    public sealed class TransactionStatusResult
    {
        public string ResponseCode { get; init; } = "";
        public string ResponseDescription { get; init; } = "";
        public string MpesaReceiptNumber { get; init; } = "";
        public string PhoneNumber { get; init; } = "";
        public string Amount { get; init; } = "";
        public string TransactionDate { get; init; } = "";
    }

    private sealed class DarajaConfigRow
    {
        public Guid TenantId { get; init; }
        public byte[] ConsumerKeyEnc { get; init; } = Array.Empty<byte>();
        public byte[] ConsumerSecretEnc { get; init; } = Array.Empty<byte>();
        public byte[] PasskeyEnc { get; init; } = Array.Empty<byte>();
        public string Shortcode { get; init; } = "";
        public string ShortcodeType { get; init; } = "paybill";
        public DateTimeOffset? VerifiedAt { get; init; }
    }
}

/// <summary>Basic Auth header credential for HttpClient.</summary>
file sealed class BasicAuthenticationCredential : AuthenticationHeaderValue
{
    public BasicAuthenticationCredential(string username, string password)
        : base("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")))
    { }
}
