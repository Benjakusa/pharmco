using Xunit;
using Pharmco.Api.Services;

namespace Pharmco.Tests;

public class RateLimiterTest
{
    [Fact]
    public void Login_5Failures_RateLimited()
    {
        var limiter = new FailureRateLimiter(new RateLimiterConfig
        {
            MaxAttempts = 5,
            Window = TimeSpan.FromMinutes(15),
        });
        var key = "login:PHARMCO-001:admin";
        var @base = 1_700_000_000L;

        // The first 4 failures are allowed...
        for (var i = 1; i <= 4; i++)
        {
            limiter.RecordFailure(key, T(@base + i));
            Assert.False(limiter.IsBlocked(key, T(@base + i)));
        }
        // ...the 5th failure is recorded (attempt itself allowed)...
        limiter.RecordFailure(key, T(@base + 5));
        // ...and from the 6th attempt on, the username is blocked → 429.
        Assert.True(limiter.IsBlocked(key, T(@base + 6)));
        Assert.True(limiter.IsBlocked(key, T(@base + 60)));
        Assert.Equal(5, limiter.RecentFailures(key, T(@base + 6)));

        // The window slides: after 15 minutes the bucket drains.
        Assert.False(limiter.IsBlocked(key, T(@base + 15 * 60 + 1)));
    }

    [Fact]
    public void SuccessfulLogin_ResetsBucket()
    {
        var limiter = new FailureRateLimiter(new RateLimiterConfig { MaxAttempts = 5 });
        var key = "login:PHARMCO-001:admin";
        var @base = 1_700_000_000L;

        for (var i = 1; i <= 5; i++)
            limiter.RecordFailure(key, T(@base + i));
        Assert.True(limiter.IsBlocked(key, T(@base + 6)));

        limiter.Reset(key);                       // called by /api/auth/login on success
        Assert.False(limiter.IsBlocked(key, T(@base + 7)));
    }

    [Fact]
    public void KeysArePerTenantAndUsername()
    {
        var limiter = new FailureRateLimiter(new RateLimiterConfig { MaxAttempts = 5 });
        Assert.StartsWith("login:PHARMCO-001:bob", limiter.KeyFor("PHARMCO-001", "Bob"));
        Assert.NotEqual(limiter.KeyFor("PHARMCO-001", "bob"), limiter.KeyFor("PHARMCO-002", "bob"));
    }

    private static DateTimeOffset T(long epochSeconds)
        => DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
}
