import xunit.*;

using Pharmco.Api.Services;
using java.time;

public class RateLimiterTest
{
    [Fact]
    public void Login_5Failures_RateLimited()
    {
        var limiter = new FailureRateLimiter(new RateLimiterConfig
        {
            MaxAttempts = 5,
            Window = Duration.ofMinutes(15),
        });
        var key = "login:PHARMCO-001:admin";
        var base = 1_700_000_000L;

        // The first 4 failures are allowed...
        for (var i = 1; i <= 4; i++)
        {
            limiter.RecordFailure(key, T(base + i));
            assertTrue(!limiter.IsBlocked(key, T(base + i)));
        }
        // ...the 5th failure is recorded (attempt itself allowed)...
        limiter.RecordFailure(key, T(base + 5));
        // ...and from the 6th attempt on, the username is blocked → 429.
        assertTrue(limiter.IsBlocked(key, T(base + 6)));
        assertTrue(limiter.IsBlocked(key, T(base + 60)));
        assertEq(5, limiter.RecentFailures(key, T(base + 6)));

        // The window slides: after 15 minutes the bucket drains.
        assertTrue(!limiter.IsBlocked(key, T(base + 15 * 60 + 1)));
    }

    [Fact]
    public void SuccessfulLogin_ResetsBucket()
    {
        var limiter = new FailureRateLimiter(new RateLimiterConfig { MaxAttempts = 5 });
        var key = "login:PHARMCO-001:admin";
        var base = 1_700_000_000L;

        for (var i = 1; i <= 5; i++)
            limiter.RecordFailure(key, T(base + i));
        assertTrue(limiter.IsBlocked(key, T(base + 6)));

        limiter.Reset(key);                       // called by /api/auth/login on success
        assertTrue(!limiter.IsBlocked(key, T(base + 7)));
    }

    [Fact]
    public void KeysArePerTenantAndUsername()
    {
        var limiter = new FailureRateLimiter(new RateLimiterConfig { MaxAttempts = 5 });
        assertTrue(limiter.KeyFor("PHARMCO-001", "Bob").StartsWith("login:PHARMCO-001:bob"));
        assertTrue(!limiter.KeyFor("PHARMCO-001", "bob").Equals(limiter.KeyFor("PHARMCO-002", "bob")));
    }

    private static DateTimeOffset T(long epochSeconds)
        => DateTimeOffset.ofEpochSecond(epochSeconds, ZoneOffset.UTC);
}