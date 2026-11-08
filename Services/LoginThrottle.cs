using System.Collections.Concurrent;

namespace Chandlery.Services;

/// <summary>
/// Slows down password guessing. After <see cref="MaxFailuresPerLogin"/> wrong passwords for one sign-in name within the window, that name
/// is refused for <see cref="LockFor"/> (even with the right password), and one address is refused after <see cref="MaxFailuresPerAddress"/>
/// failures across any names. Held in memory: a restart clears it, which is acceptable for a brake on guessing.
/// </summary>
public sealed class LoginThrottle(TimeProvider? clock = null)
{
    public const int MaxFailuresPerLogin = 5;
    public const int MaxFailuresPerAddress = 30;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Counter> _counters = new();

    private sealed class Counter
    {
        public readonly object Gate = new();
        public int Failures;
        public DateTimeOffset First;
        public DateTimeOffset LockedUntil;
    }

    private static string LoginKey(string login) => "u:" + (login ?? "").Trim().ToLowerInvariant();
    private static string AddressKey(string ip) => "ip:" + ip;

    public bool IsLocked(string login, string ip) => IsLocked(LoginKey(login), MaxFailuresPerLogin) | (ip != "" && IsLocked(AddressKey(ip), MaxFailuresPerAddress));

    private bool IsLocked(string key, int max)
    {
        if (!_counters.TryGetValue(key, out var c)) return false;
        lock (c.Gate) return c.LockedUntil > _clock.GetUtcNow();
    }

    public void RecordFailure(string login, string ip)
    {
        Bump(LoginKey(login), MaxFailuresPerLogin);
        if (ip != "") Bump(AddressKey(ip), MaxFailuresPerAddress);
        if (_counters.Count > 5000) Prune();
    }

    /// <summary>A correct sign-in clears the name's count (the address keeps its own, so one person cannot wash away a scan).</summary>
    public void RecordSuccess(string login) => _counters.TryRemove(LoginKey(login), out _);

    private void Bump(string key, int max)
    {
        var now = _clock.GetUtcNow();
        var c = _counters.GetOrAdd(key, _ => new Counter { First = now });
        lock (c.Gate)
        {
            if (now - c.First > Window && c.LockedUntil <= now) { c.Failures = 0; c.First = now; }
            c.Failures++;
            if (c.Failures >= max) c.LockedUntil = now + LockFor;
        }
    }

    private void Prune()
    {
        var now = _clock.GetUtcNow();
        foreach (var (key, c) in _counters)
            lock (c.Gate)
                if (c.LockedUntil <= now && now - c.First > Window) _counters.TryRemove(key, out _);
    }

    /// <summary>True for a path on this site ("/worklist"); false for an absolute address or a protocol-relative one that would leave it.</summary>
    public static bool IsLocalPath(string? url)
        => !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\')) && !url.Contains('\n') && !url.Contains('\r');
}
