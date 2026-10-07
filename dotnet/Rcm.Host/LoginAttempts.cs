using Rcm.Crm;

namespace Rcm.Host;

internal sealed class LoginAttempts(TimeProvider clock)
{
    private readonly object gate = new();
    private readonly Dictionary<string, List<Attempt>> buckets = new(StringComparer.Ordinal);
    internal sealed record Attempt(string Key, long Timestamp);

    public Attempt Reserve(string address, string principal)
    {
        lock (gate)
        {
            var now = clock.GetTimestamp();
            foreach (var key in buckets.Keys.ToArray())
            {
                buckets[key].RemoveAll(a => clock.GetElapsedTime(a.Timestamp, now) >= TimeSpan.FromMinutes(1));
                if (buckets[key].Count == 0) buckets.Remove(key);
            }
            var bucketKey = address + ":" + principal;
            if (!buckets.TryGetValue(bucketKey, out var attempts))
            {
                if (buckets.Count >= 4096) throw Limited();
                buckets[bucketKey] = attempts = [];
            }
            if (attempts.Count >= 10) throw Limited();
            var attempt = new Attempt(bucketKey, now);
            attempts.Add(attempt);
            return attempt;
        }
    }

    public void Release(Attempt attempt)
    {
        lock (gate)
        {
            if (!buckets.TryGetValue(attempt.Key, out var attempts)) return;
            var position = attempts.FindIndex(a => ReferenceEquals(a, attempt));
            if (position >= 0) attempts.RemoveAt(position);
            if (attempts.Count == 0) buckets.Remove(attempt.Key);
        }
    }

    public async Task<T> Authenticate<T>(HttpContext http, string principal, Func<Task<T>> authenticate)
    {
        var attempt = Reserve(http.Connection.RemoteIpAddress?.ToString() ?? "unknown", principal);
        try
        {
            var result = await authenticate();
            Release(attempt);
            return result;
        }
        catch (CrmFault error) when (error.Status == 401) { throw; }
        catch { Release(attempt); throw; }
    }

    private static CrmFault Limited() => new(429, "Zbyt wiele prób. Spróbuj za minutę.");
}
