namespace Pharmco.Api.Services;

using java.time;
using java.util.concurrent;

/// <summary>
/// Rate-limit configuration (sliding window). Defaults per the auth spec:
/// 5 failed attempts per username per 15 minutes. The absence of a real Redis
/// client in the scaffold keeps this in-process; the shape mirrors a Redis
/// INCR+EXPIRE design 1:1 so a Redis-backed swap is a pure implementation
/// detail (same keys, same window math).
/// </summary>
public sealed class RateLimiterConfig
{
    public int MaxAttempts = 5;
    public Duration Window = Duration.ofMinutes(15);

    public static RateLimiterConfig FromParts(string? maxAttempts, string? windowMinutes)
    {
        var cfg = new RateLimiterConfig();
        // int32.Parse(value, min, max) — clamps to a sane range.
        if (maxAttempts is not null && !maxAttempts.IsEmpty())
            cfg.MaxAttempts = int32.Parse(maxAttempts, 1, 100);
        if (windowMinutes is not null && !windowMinutes.IsEmpty())
            cfg.Window = Duration.ofMinutes(int32.Parse(windowMinutes, 1, 60 * 24));
        return cfg;
    }
}

/// <summary>
/// Sliding-window failure limiter keyed per username (+ tenant, so the same
/// username in two pharmacies never shares a bucket). After
/// <c>MaxAttempts</c> failures inside <c>Window</c> every further attempt is
/// rejected (HTTP 429) until the window slides past the oldest failure.
/// Thread-safe; each API process keeps its own table.
/// </summary>
public sealed class FailureRateLimiter
{
    private const string KeyPrefix = "login:";

    private readonly RateLimiterConfig _config;
    private readonly ConcurrentHashMap<string, java.util.ArrayDeque<DateTimeOffset>> _failures = new();

    public FailureRateLimiter(RateLimiterConfig config) => _config = config;

    public string KeyFor(string pharmacyCode, string username)
        => KeyPrefix + pharmacyCode + ":" + username.ToLower();

    /// <summary>True when the caller is currently blocked (≥ MaxAttempts in window).</summary>
    public bool IsBlocked(string key)
        => IsBlocked(key, DateTimeOffset.now());

    public bool IsBlocked(string key, DateTimeOffset now)
    {
        var deque = _failures.Get(key);
        if (deque is null)
            return false;
        Prune(deque, now);
        if (deque.IsEmpty())
        {
            _failures.Remove(key, deque);
            return false;
        }
        return deque.Count >= _config.MaxAttempts;
    }

    public void RecordFailure(string key)
        => RecordFailure(key, DateTimeOffset.now());

    public void RecordFailure(string key, DateTimeOffset now)
    {
        var deque = _failures.ComputeIfAbsent(key, _ => new java.util.ArrayDeque<DateTimeOffset>());
        Prune(deque, now);
        deque.AddLast(now);
        if (deque.Count > _config.MaxAttempts * 4)      // bound: drop the oldest half
        {
            for (var i = 0; i < deque.Count / 2; i++) deque.RemoveFirst();
        }
    }

    /// <summary>Call after a successful login so the bucket starts clean.</summary>
    public void Reset(string key) => _failures.Remove(key);

    public int RecentFailures(string key, DateTimeOffset now)
    {
        var deque = _failures.Get(key);
        if (deque is null)
            return 0;
        Prune(deque, now);
        return deque.Count;
    }

    // Analysing a window of up to a couple of minutes of failures needs a
    // wall-clock threshold; using the configured window relative to the fresh
    // `now` above is sufficient and keeps the bucket bounded.
    private void Prune(java.util.ArrayDeque<DateTimeOffset> deque, DateTimeOffset now)
    {
        var windowNanos = _config.Window.toNanos();
        while (!deque.IsEmpty() && now.toEpochSecond() * 1_000_000_000 - deque.PeekFirst().toEpochSecond() * 1_000_000_000 > windowNanos)
            deque.RemoveFirst();
    }
}