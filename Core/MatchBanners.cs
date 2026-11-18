namespace PartyScout.Core;

internal sealed record MatchBanner(Listing Listing, int Count, DateTimeOffset Started);

internal sealed class MatchBanners
{
    internal static readonly TimeSpan Duration = TimeSpan.FromSeconds(6);
    private readonly Dictionary<ulong, DateTimeOffset> seen = [];
    private HashSet<ulong> showing = [], waiting = [];
    internal MatchBanner? Current { get; private set; }

    internal void Observe(ReadOnlySpan<Match> ranked, ulong currentLeader, DateTimeOffset now, bool notify)
    {
        foreach (var id in seen.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray()) seen.Remove(id);
        if (!notify) Hide();
        if (Current is { } old && now - old.Started >= Duration) { showing.Clear(); Current = null; }
        foreach (var match in ranked)
        {
            var listing = match.Listing;
            if (listing.Expires <= now || notify && !listing.Fresh(now)) continue;
            var first = seen.TryAdd(listing.Id, listing.Expires);
            seen[listing.Id] = listing.Expires;
            if (first && notify && (currentLeader == 0 || listing.Leader != currentLeader)) waiting.Add(listing.Id);
        }
        if (seen.Count > 4096)
            foreach (var id in seen.OrderBy(pair => pair.Value).Take(seen.Count - 4096).Select(pair => pair.Key).ToArray()) seen.Remove(id);
        if (!notify) return;

        // Keep one quiet banner on screen. Arrivals during it form the next batch.
        var started = Current?.Started ?? now;
        Current = Select(showing, ranked, currentLeader, now, started);
        if (Current is null)
        {
            showing.Clear();
            (showing, waiting) = (waiting, showing);
            Current = Select(showing, ranked, currentLeader, now, now);
        }
    }

    private static MatchBanner? Select(HashSet<ulong> ids, ReadOnlySpan<Match> ranked, ulong leader, DateTimeOffset now, DateTimeOffset started)
    {
        if (ids.Count == 0) return null;
        Listing? best = null;
        var count = 0;
        foreach (var match in ranked)
        {
            var listing = match.Listing;
            if (!ids.Contains(listing.Id) || !listing.Fresh(now) || leader != 0 && listing.Leader == leader) continue;
            best ??= listing;
            count++;
        }
        return best is null ? null : new(best, count, started);
    }

    internal void Dismiss() { showing.Clear(); Current = null; }
    internal void Hide() { Dismiss(); waiting.Clear(); }
    internal void Reset() { Hide(); seen.Clear(); }
}
