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
        var cfg = new RateLimiterConfig();
        if (!string.IsNullOrEmpty(maxAttempts) && int.TryParse(maxAttempts, out var ma))
            cfg.MaxAttempts = Math.Clamp(ma, 1, 100);
        if (!string.IsNullOrEmpty(windowMinutes) && int.TryParse(windowMinutes, out var wm))
            cfg.Window = TimeSpan.FromMinutes(Math.Clamp(wm, 1, 60 * 24));
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
    // Queue<DateTimeOffset> values are accessed under per-key locks via the
    // ConcurrentDictionary's built-in partition locks on GetOrAdd + explicit
    // lock guards around dequeue mutation (Queue is not thread-safe itself).
    private readonly ConcurrentDictionary<string, object> _locks = new();
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _failures = new();

    public FailureRateLimiter(RateLimiterConfig config) => _config = config;

    public string KeyFor(string pharmacyCode, string username)
        => KeyPrefix + pharmacyCode + ":" + username.ToLowerInvariant();

    /// <summary>True when the caller is currently blocked (≥ MaxAttempts in window).</summary>
    public bool IsBlocked(string key)
        => IsBlocked(key, DateTimeOffset.UtcNow);

    public bool IsBlocked(string key, DateTimeOffset now)
    {
        if (!_failures.TryGetValue(key, out var deque))
            return false;
        lock (GetLock(key))
        {
            Prune(deque, now);
            if (deque.Count == 0)
            {
                _failures.TryRemove(key, out _);
                return false;
            }
            return deque.Count >= _config.MaxAttempts;
        }
    }

    public void RecordFailure(string key)
        => RecordFailure(key, DateTimeOffset.UtcNow);

    public void RecordFailure(string key, DateTimeOffset now)
    {
        var deque = _failures.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (GetLock(key))
        {
            Prune(deque, now);
            deque.Enqueue(now);
            // Bound the queue: if it grows beyond 4× MaxAttempts drop the oldest half
            while (deque.Count > _config.MaxAttempts * 4)
                deque.Dequeue();
        }
    }

    /// <summary>Call after a successful login so the bucket starts clean.</summary>
    public void Reset(string key)
    {
        _failures.TryRemove(key, out _);
        _locks.TryRemove(key, out _);
    }

    public int RecentFailures(string key, DateTimeOffset now)
    {
        if (!_failures.TryGetValue(key, out var deque))
            return 0;
        lock (GetLock(key))
        {
            Prune(deque, now);
            return deque.Count;
        }
    }

    private object GetLock(string key) => _locks.GetOrAdd(key, _ => new object());

    private void Prune(Queue<DateTimeOffset> deque, DateTimeOffset now)
    {
        while (deque.Count > 0 && now - deque.Peek() > _config.Window)
            deque.Dequeue();
    }
}