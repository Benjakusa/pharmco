using Xunit;
using Pharmco.Core.Auth;

namespace Pharmco.Tests;

/// <summary>
/// Auto-lock policy tests. The WPF SessionManager keeps a timer that polls
/// IdlePolicy.ShouldLock; after AutoLockMinutes idle it flips Session.Locked
/// and the UI requires the password — Unlock re-verifies it (online or via the
/// cached bcrypt hash + OfflineGate). Policy logic is exercised here headlessly.
/// </summary>
public class SessionLockTest
{
    [Fact]
    public void AutoLock_After5Min_RequiresPassword()
    {
        var lastActivity = T(1_000L);

        // Not idle yet at 4:59 min...
        Assert.False(IdlePolicy.ShouldLock(lastActivity, T(1_000L + 4 * 60 + 59), 5));
        // ...locked at exactly 5:00 min idle...
        Assert.True(IdlePolicy.ShouldLock(lastActivity, T(1_000L + 5 * 60), 5));
        // ...and the session cannot be unlocked without the password.
        var cachedHash = PasswordService.Hash("secret123");
        var facts = new CachedCredentialFacts
        {
            Username = "cashier@nairobi-chemist",
            PasswordHash = cachedHash,
            LastVerifiedAt = T(1_000L - 86_400),
        };
        var now = T(1_000L + 5 * 60);
        Assert.False(OfflineGate.Evaluate(facts, "wrong-password", now).Allowed);   // unlock fails
        Assert.True(OfflineGate.Evaluate(facts, "secret123", now).Allowed);         // unlock succeeds
    }

    [Fact]
    public void AutoLock_Disabled_WhenMinutesZero()
    {
        var lastActivity = T(0L);
        Assert.False(IdlePolicy.ShouldLock(lastActivity, T(99_999L), 0));
    }

    [Fact]
    public void IdleClock_RequiresConfiguredWindow()
    {
        var lastActivity = T(1_000L);
        Assert.True(IdlePolicy.ShouldLock(lastActivity, T(1_000L + 31 * 60), 30));
        Assert.False(IdlePolicy.ShouldLock(lastActivity, T(1_000L + 29 * 60), 30));
    }

    private static DateTimeOffset T(long epochSeconds)
        => DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
}
