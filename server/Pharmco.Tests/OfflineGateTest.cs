using Xunit;
using Pharmco.Core.Auth;

namespace Pharmco.Tests;

/// <summary>
/// Offline-login policy tests (the WPF SessionManager delegates its offline
/// decision to Pharmco.Core.OfflineGate, so these run headless here).
/// </summary>
public class OfflineGateTest
{
    private static readonly string Password = "correct-horse-battery";

    [Fact]
    public void OfflineLogin_ValidCache_Succeeds()
    {
        var now = T(1_700_000_000L);
        var verdict = OfflineGate.Evaluate(Fact(now, offsetSeconds: -2L * 86_400), Password, now);

        Assert.True(verdict.Allowed);
        Assert.Equal("none", verdict.Reason);
        Assert.False(verdict.Stale);
    }

    [Fact]
    public void OfflineLogin_StaleCache_Fails()
    {
        var now = T(1_700_000_000L);
        var verdict = OfflineGate.Evaluate(Fact(now, offsetSeconds: -10L * 86_400), Password, now);

        Assert.False(verdict.Allowed);
        Assert.True(verdict.Stale);                   // last_verified_at older than 7 days
        Assert.Equal("cache_stale", verdict.Reason);  // → require online login
    }

    [Fact]
    public void OfflineLogin_WrongPassword_Fails()
    {
        var now = T(1_700_000_000L);
        var verdict = OfflineGate.Evaluate(Fact(now, offsetSeconds: -86_400), "wrong-password", now);

        Assert.False(verdict.Allowed);
        Assert.Equal("invalid_password", verdict.Reason);
    }

    [Fact]
    public void OfflineLogin_NoCache_Fails()
    {
        var verdict = OfflineGate.Evaluate(null, Password, T(1_700_000_000L));

        Assert.False(verdict.Allowed);
        Assert.True(verdict.CacheMissing);
    }

    // --- fixtures -----------------------------------------------------------------

    private static CachedCredentialFacts Fact(DateTimeOffset now, long offsetSeconds)
        => new()
        {
            Username = "admin@nairobi-chemist",
            PasswordHash = PasswordService.Hash(Password),   // client-side second hash, cost 12
            LastVerifiedAt = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() + offsetSeconds),
        };

    private static DateTimeOffset T(long epochSeconds)
        => DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
}
