namespace Pharmco.Core.Provisioning;

/// <summary>
/// Pure license-expiry math so it can be unit-tested without a database.
/// Renewal anchors on max(current expiry, now) — an expired licence still gets
/// a full term from today, never from a date in the past.
/// </summary>
public static class RenewalPolicy
{
    public static DateTimeOffset NextExpiry(DateTimeOffset? current, int days, DateTimeOffset now)
    {
        if (days < 1) throw new ArgumentOutOfRangeException(nameof(days));
        var anchor = current.HasValue && current.Value > now ? current.Value : now;
        return anchor.AddDays(days);
    }
}