namespace Pharmco.Core.Sales;

/// <summary>
/// DTOs for Daraja API requests/responses.
/// </summary>

/// <summary>Request body for POST /api/daraja/config (admin sets credentials).</summary>
public sealed record DarajaConfigRequest
{
    public string ConsumerKey = "";
    public string ConsumerSecret = "";
    public string Passkey = "";
    public string Shortcode = "";
    public string ShortcodeType = "paybill";  // 'paybill' | 'till'
}

/// <summary>Response from GET /api/daraja/status.</summary>
public sealed record DarajaStatusResponse
{
    public bool Configured { get; init; }
    public DateTimeOffset? VerifiedAt { get; init; }
    public string ShortcodeMasked { get; init; } = "";
    public string ShortcodeType { get; init; } = "";
}

/// <summary>Decrypted Daraja credentials for a tenant (NOT stored, used in-memory).</summary>
public sealed record DarajaCredentials
{
    public string ConsumerKey { get; init; } = "";
    public string ConsumerSecret { get; init; } = "";
    public string Passkey { get; init; } = "";
    public string Shortcode { get; init; } = "";
    public string ShortcodeType { get; init; } = "paybill";
}

/// <summary>Safaricom OAuth token response.</summary>
public sealed record OAuthTokenResponse
{
    public string AccessToken { get; init; } = "";
    public string TokenType { get; init; } = "";
    public int ExpiresIn { get; init; }
}

/// <summary>STK Push request to Safaricom.</summary>
public sealed record StkPushRequest
{
    public string BusinessShortCode { get; init; } = "";
    public string Password { get; init; } = "";
    public string Timestamp { get; init; } = "";
    public string TransactionType { get; init; } = "CustomerPayBillOnline";
    public int Amount { get; init; }
    public string PartyA { get; init; } = "";   // customer phone
    public string PartyB { get; init; } = "";   // shortcode
    public string PhoneNumber { get; init; } = "";
    public string CallBackURL { get; init; } = "";
    public string AccountReference { get; init; } = "";  // invoice_no
    public string TransactionDesc { get; init; } = "";
}

/// <summary>Safaricom STK Push response.</summary>
public sealed record StkPushResponse
{
    public string MerchantRequestID { get; init; } = "";
    public string CheckoutRequestID { get; init; } = "";
    public string ResponseCode { get; init; } = "";
    public string ResponseDescription { get; init; } = "";
    public string CustomerMessage { get; init; } = "";
}

/// <summary>STK Push callback from Safaricom.</summary>
public sealed record StkPushCallback
{
    public string ResultCode { get; init; } = "";
    public string ResultDesc { get; init; } = "";
    public string MerchantRequestID { get; init; } = "";
    public string CheckoutRequestID { get; init; } = "";
    public StkPushCallbackCallbackItem? CallbackMetadata { get; init; }
}

public sealed record StkPushCallbackCallbackItem
{
    public StkPushCallbackItem? Item { get; init; }
}

public sealed record StkPushCallbackItem
{
    public List<StkPushCallbackValue>? Values { get; init; }
}

public sealed record StkPushCallbackValue
{
    public string Key { get; init; } = "";
    public string Value { get; init; } = "";
}

/// <summary>Response from Transaction Status API.</summary>
public sealed record TransactionStatusResponse
{
    public string RequestID { get; init; } = "";
    public string ResponseCode { get; init; } = "";
    public string ResponseDescription { get; init; } = "";
    public TransactionStatusCallbackMetadata? CallbackMetadata { get; init; }
}

public sealed record TransactionStatusCallbackMetadata
{
    public TransactionStatusItem? Item { get; init; }
}

public sealed record TransactionStatusItem
{
    public List<TransactionStatusValue>? Values { get; init; }
}

public sealed record TransactionStatusValue
{
    public string Key { get; init; } = "";
    public string Value { get; init; } = "";
}
