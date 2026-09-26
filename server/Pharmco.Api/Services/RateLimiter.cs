namespace Pharmco.Api.Services;

using System.Collections.Concurrent;

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
    public TimeSpan Window = TimeSpan.FromMinutes(15);

    public static RateLimiterConfig FromParts(string? maxAttempts, string? windowMinutes)
    {
        var config = new RateLimiterConfig();
        if (int.TryParse(maxAttempts, out var attempts))
            config.MaxAttempts = Math.Clamp(attempts, 1, 100);
        if (int.TryParse(windowMinutes, out var minutes))
            config.Window = TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 60 * 24));
        return config;
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
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTimeOffset>> _failures = new();

    public FailureRateLimiter(RateLimiterConfig config) => _config = config;

    public string KeyFor(string pharmacyCode, string username)
        => KeyPrefix + pharmacyCode + ":" + username.ToLowerInvariant();

    /// <summary>True when the caller is currently blocked (≥ MaxAttempts in window).</summary>
    public bool IsBlocked(string key) => IsBlocked(key, DateTimeOffset.UtcNow);

    public bool IsBlocked(string key, DateTimeOffset now)
    {
        if (!_failures.TryGetValue(key, out var failures))
            return false;

        Prune(failures, now);
        if (failures.IsEmpty)
        {
            _failures.TryRemove(key, out _);
            return false;
        }
        return failures.Count >= _config.MaxAttempts;
    }

    public void RecordFailure(string key) => RecordFailure(key, DateTimeOffset.UtcNow);

    public void RecordFailure(string key, DateTimeOffset now)
    {
        var failures = _failures.GetOrAdd(key, _ => new ConcurrentQueue<DateTimeOffset>());
        Prune(failures, now);
        failures.Enqueue(now);

        // Bound the bucket: keep the newest MaxAttempts entries once it grows.
        while (failures.Count > _config.MaxAttempts * 4 && failures.TryDequeue(out _))
        {
        }
    }

    /// <summary>Call after a successful login so the bucket starts clean.</summary>
    public void Reset(string key) => _failures.TryRemove(key, out _);

    public int RecentFailures(string key, DateTimeOffset now)
    {
        if (!_failures.TryGetValue(key, out var failures))
            return 0;
        Prune(failures, now);
        return failures.Count;
    }

    // Drop attempts that fell out of the sliding window. Callers serialise per
    // key only implicitly, which is fine: pruning is monotonic and idempotent.
    private void Prune(ConcurrentQueue<DateTimeOffset> failures, DateTimeOffset now)
    {
        var threshold = now - _config.Window;
        while (failures.TryPeek(out var oldest) && oldest <= threshold)
            failures.TryDequeue(out _);
    }
}
