namespace PartyScout.Core;

// Stable ordering prevents a refreshed top result from starving the remaining parties.
internal sealed class CollectionQueue
{
    private readonly Dictionary<ulong, DateTimeOffset> attempted = [];
    internal ulong Next(IEnumerable<Listing> listings, IReadOnlyDictionary<ulong, PartyDetail> collected, DateTimeOffset now)
    {
        var wanted = listings.Where(l => l.Expires > now).DistinctBy(l => l.Id).ToArray();
        foreach (var id in attempted.Where(pair => now - pair.Value >= TimeSpan.FromMinutes(10)).Select(pair => pair.Key).ToArray()) attempted.Remove(id);
        DateTimeOffset LastVisit(Listing listing)
        {
            var last = attempted.GetValueOrDefault(listing.Id, DateTimeOffset.MinValue);
            return collected.TryGetValue(listing.Id, out var data) && data.Collected > last ? data.Collected : last;
        }
        foreach (var l in wanted.OrderBy(LastVisit))
        {
            var upToDate = collected.TryGetValue(l.Id, out var detail) && IsFresh(l, detail, now);
            if (!upToDate && (!attempted.TryGetValue(l.Id, out var last) || now - last >= TimeSpan.FromSeconds(15)))
            {
                if (attempted.Count >= 1000 && !attempted.ContainsKey(l.Id)) attempted.Remove(attempted.MinBy(pair => pair.Value).Key);
                attempted[l.Id] = now; return l.Id;
            }
        }
        return 0;
    }
    internal void Clear() => attempted.Clear();
    internal static bool IsFresh(Listing listing, PartyDetail? detail, DateTimeOffset now) =>
        detail is not null && now - detail.Collected < TimeSpan.FromSeconds(60) && Compatible(listing, detail);
    internal static bool Compatible(Listing listing, PartyDetail detail) => detail.Recruitment is { } previous &&
        previous.Leader == listing.Leader && previous.Duty == listing.Duty && previous.Total == listing.Total && previous.Filled == listing.Filled &&
        previous.JobsPresent.Take(previous.Total).SequenceEqual(listing.JobsPresent.Take(listing.Total)) &&
        previous.Slots.Take(previous.Total).SequenceEqual(listing.Slots.Take(listing.Total));
}
