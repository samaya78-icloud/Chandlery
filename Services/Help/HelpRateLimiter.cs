namespace Chandlery.Services;

/// <summary>
/// A sliding window per person: at most N questions in the last hour. Held in memory, so a restart clears it; that is enough for a cost and abuse guard.
/// </summary>
public class HelpRateLimiter(HelpOptions options, Func<DateTime>? clock = null)
{
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly Dictionary<Guid, Queue<DateTime>> _asked = new();
    private readonly object _lock = new();

    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>Counts one question for the person and returns true, or returns false (counting nothing) when they have used up the hour.</summary>
    public bool TryAcquire(Guid userId)
    {
        lock (_lock)
        {
            var now = _now();
            if (!_asked.TryGetValue(userId, out var times)) _asked[userId] = times = new Queue<DateTime>();
            while (times.Count > 0 && now - times.Peek() >= Window) times.Dequeue();
            if (times.Count >= options.PerUserPerHour) return false;
            times.Enqueue(now);
            return true;
        }
    }

    /// <summary>How many questions the person has left this hour.</summary>
    public int Remaining(Guid userId)
    {
        lock (_lock)
        {
            var now = _now();
            var used = _asked.TryGetValue(userId, out var times) ? times.Count(t => now - t < Window) : 0;
            return Math.Max(0, options.PerUserPerHour - used);
        }
    }
}
