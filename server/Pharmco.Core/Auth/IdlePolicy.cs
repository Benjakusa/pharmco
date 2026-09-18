namespace Pharmco.Core.Auth;

/// <summary>
/// Auto-lock policy: the desktop session locks once it has been idle for
/// <c>AutoLockMinutes</c> (default 5, configurable in client settings). Pure
/// logic so the UI test (AutoLock_After5Min_RequiresPassword) is a unit test.
/// </summary>
public sealed class IdlePolicy
{
    public const int DefaultAutoLockMinutes = 5;

    /// <summary>True when <c>lastActivity</c> is older than the configured window.</summary>
    public static bool ShouldLock(DateTimeOffset lastActivity, DateTimeOffset now, int autoLockMinutes)
    {
        if (autoLockMinutes <= 0)
            return false;                       // auto-lock disabled
        return (now - lastActivity).TotalSeconds >= (long) autoLockMinutes * 60;
    }

    /// <summary>Seconds until the session auto-locks (0 ⇒ already locked).</summary>
    public static long RemainingSeconds(DateTimeOffset lastActivity, DateTimeOffset now, int autoLockMinutes)
    {
        var remaining = (long) autoLockMinutes * 60 - (long) (now - lastActivity).TotalSeconds;
        return remaining < 0 ? 0 : remaining;
    }
}