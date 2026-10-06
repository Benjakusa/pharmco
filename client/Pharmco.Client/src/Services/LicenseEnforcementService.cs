using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Pharmco.Client.Services;

/// <summary>
/// Enforces license tiers with escalating restrictions:
/// >30 days: normal
/// 30-15 days: yellow banner
/// 14-8 days: daily modal on login
/// 7-1 days: red banner on every screen
/// 0 to -14: grace period, warning on every sale
/// -14 to -30: read-only (sales blocked, reports work)
/// &lt; -30: hard lock (app won't launch past login)
/// </summary>
public sealed class LicenseEnforcementService : IHostedService
{
    private readonly LocalDatabase _localDb;
    private readonly LicenseValidator _validator;
    private readonly ILogger<LicenseEnforcementService> _logger;

    // Cached license state
    private LicenseEnforcementState _state = new();

    public LicenseEnforcementState State => _state;
    public event Action<LicenseEnforcementState>? StateChanged;

    public LicenseEnforcementService(
        LocalDatabase localDb,
        LicenseValidator validator,
        ILogger<LicenseEnforcementService> logger)
    {
        _localDb = localDb;
        _validator = validator;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Load cached license on start
        LoadCachedLicenseAsync().ConfigureAwait(false);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Loads and validates the cached license from local database.
    /// </summary>
    public async Task<LicenseEnforcementState> LoadCachedLicenseAsync()
    {
        var cached = await _localDb.GetMetaAsync("license_cache", default);
        if (string.IsNullOrEmpty(cached))
        {
            _logger.LogWarning("No cached license found");
            return CreateEmptyState();
        }

        try
        {
            var licenseData = JsonSerializer.Deserialize<CachedLicense>(cached);
            if (licenseData == null)
            {
                return CreateEmptyState();
            }

            // Verify signature
            var result = _validator.Verify(licenseData.Token);
            if (!result.IsValid)
            {
                _logger.LogWarning("License signature invalid: {Error}", result.Error);
                return CreateUnauthorizedState(result.Error!);
            }

            // Check for clock tampering
            var lastSyncAtStr = await _localDb.GetMetaAsync("last_sync_at", default);
            if (!string.IsNullOrEmpty(lastSyncAtStr)
                && _validator.DetectClockTamper(DateTimeOffset.Parse(lastSyncAtStr)))
            {
                _logger.LogWarning("Clock tamper detected — rejecting license");
                return CreateUnauthorizedState("System clock appears tampered");
            }

            _state = new LicenseEnforcementState
            {
                IsValid = true,
                IsExpired = result.IsExpired,
                DaysToExpiry = result.DaysToExpiry,
                TenantId = result.Claims!.TenantId,
                TenantCode = result.Claims.Code,
                MaxUsers = result.Claims.MaxUsers,
                MaxTerminals = result.Claims.MaxTerminals,
                Features = result.Claims.Features,
                ExpiresAt = result.Claims.ExpiresAt,
                Tier = GetTier(result.DaysToExpiry)
            };

            _logger.LogInformation("License loaded: tier={Tier}, days_left={Days}",
                _state.Tier, _state.DaysToExpiry);

            return _state;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load cached license");
            return CreateUnauthorizedState(ex.Message);
        }
    }

    /// <summary>
    /// Updates the license from a new token (e.g., after sync pulls new license).
    /// </summary>
    public async Task UpdateLicenseAsync(string licenseToken)
    {
        var result = _validator.Verify(licenseToken);
        if (!result.IsValid)
        {
            _logger.LogWarning("New license verification failed: {Error}", result.Error);
            return;
        }

        // Cache the license
        var cache = new CachedLicense
        {
            Token = licenseToken,
            CachedAt = DateTimeOffset.UtcNow
        };
        await _localDb.SetMetaAsync("license_cache",
            JsonSerializer.Serialize(cache), default);

        // Update state
        var timeDiff = result.Claims!.ExpiresAt - DateTimeOffset.UtcNow;
        _state = new LicenseEnforcementState
        {
            IsValid = true,
            IsExpired = result.IsExpired,
            DaysToExpiry = (int)timeDiff.TotalDays,
            TenantId = result.Claims.TenantId,
            TenantCode = result.Claims.Code,
            MaxUsers = result.Claims.MaxUsers,
            MaxTerminals = result.Claims.MaxTerminals,
            Features = result.Claims.Features,
            ExpiresAt = result.Claims.ExpiresAt,
            Tier = GetTier((int)timeDiff.TotalDays)
        };

        _logger.LogInformation("License updated: tier={Tier}, expires={Expires}",
            _state.Tier, _state.ExpiresAt);

        StateChanged?.Invoke(_state);
    }

    /// <summary>
    /// Checks if a sale is allowed based on current license state.
    /// </summary>
    public bool IsSaleAllowed()
    {
        // Hard lock: can't make sales
        if (_state.Tier <= LicenseTier.HardLock)
            return false;

        // Read-only: can't make sales
        if (_state.Tier <= LicenseTier.ReadOnly)
            return false;

        // Grace period: warn but allow
        if (_state.Tier == LicenseTier.GracePeriod)
        {
            _logger.LogWarning("Sale allowed in grace period — license expired");
            return true;
        }

        return true;
    }

    /// <summary>
    /// Gets the warning message for the current tier.
    /// </summary>
    public string GetWarningMessage() => _state.GetWarningMessage();

    /// <summary>
    /// Gets renewal info for display.
    /// </summary>
    public RenewalInfo GetRenewalInfo()
    {
        return new RenewalInfo
        {
            PaybillNumber = "500000",
            AccountReference = "PHARMCO-REHISTRY",
            Amount = 5000, // KSh 5,000
            Phone = "+254700000000",
            TenantCode = _state.TenantCode
        };
    }

    // ------------------------------------------------------------------
    // Private Helpers
    // ------------------------------------------------------------------

    private static LicenseTier GetTier(int daysToExpiry)
    {
        if (daysToExpiry > 30) return LicenseTier.Normal;
        if (daysToExpiry > 15) return LicenseTier.WarningYellow;
        if (daysToExpiry > 7) return LicenseTier.DailyModal;
        if (daysToExpiry > 0) return LicenseTier.WarningRed;
        if (daysToExpiry >= -14) return LicenseTier.GracePeriod;
        if (daysToExpiry >= -30) return LicenseTier.ReadOnly;
        return LicenseTier.HardLock;
    }

    private static LicenseEnforcementState CreateEmptyState()
        => new() { IsValid = false, Error = "No license cached" };

    private static LicenseEnforcementState CreateUnauthorizedState(string error)
        => new() { IsValid = false, Error = error, Tier = LicenseTier.HardLock };

    // ------------------------------------------------------------------
    // Inner Types
    // ------------------------------------------------------------------

    public enum LicenseTier
    {
        Normal = 0,
        WarningYellow = 1,
        DailyModal = 2,
        WarningRed = 3,
        GracePeriod = 4,
        ReadOnly = 5,
        HardLock = 6
    }

    public sealed class CachedLicense
    {
        public string Token { get; init; } = "";
        public DateTimeOffset CachedAt { get; init; }
    }
}

public sealed class LicenseEnforcementState
{
    public bool IsValid { get; init; }
    public string? Error { get; init; }
    public bool IsExpired { get; init; }
    public int DaysToExpiry { get; init; }
    public string TenantId { get; init; } = "";
    public string TenantCode { get; init; } = "";
    public int MaxUsers { get; init; }
    public int MaxTerminals { get; init; }
    public List<string> Features { get; init; } = new();
    public DateTimeOffset ExpiresAt { get; init; }
    public LicenseEnforcementService.LicenseTier Tier { get; init; }

    /// <summary>UI copy for the current tier (banner text, expiry nag).</summary>
    public string GetWarningMessage() => Tier switch
    {
        LicenseEnforcementService.LicenseTier.Normal => "",
        LicenseEnforcementService.LicenseTier.WarningYellow =>
            $"License renews in {DaysToExpiry} days. Contact Pharmco to renew.",
        LicenseEnforcementService.LicenseTier.WarningRed =>
            $"License expires in {DaysToExpiry} days! Renew now.",
        LicenseEnforcementService.LicenseTier.DailyModal =>
            $"Your license expires in {DaysToExpiry} days. Please renew.",
        LicenseEnforcementService.LicenseTier.GracePeriod =>
            "License expired! Contact Pharmco Paybill 500000 to renew.",
        LicenseEnforcementService.LicenseTier.ReadOnly =>
            "License expired beyond grace period. Sales are disabled.",
        LicenseEnforcementService.LicenseTier.HardLock =>
            "License expired. Contact Pharmco immediately.",
        _ => "",
    };
}

public sealed record RenewalInfo
{
    public string PaybillNumber { get; init; } = "";
    public string AccountReference { get; init; } = "";
    public int Amount { get; init; }
    public string Phone { get; init; } = "";
    public string TenantCode { get; init; } = "";
}
