namespace PartyScout.Core;

// Stable ordering prevents a refreshed top result from starving the remaining parties.
internal sealed class CollectionQueue
{
    private readonly Dictionary<ulong, DateTimeOffset> attempted = [];
    internal ulong NextPrioritized(IEnumerable<Listing> matches, IEnumerable<Listing> contacts,
        IReadOnlyDictionary<ulong, PartyDetail> collected, DateTimeOffset now, CollectionQueue contactQueue, bool allowContacts)
    {
        var ranked = matches as IReadOnlyList<Listing> ?? matches.ToArray();
        var id = Next(ranked, collected, now);
        if (id != 0 || !allowContacts) return id;
        var matchingIds = ranked.Select(listing => listing.Id).ToHashSet();
        return contactQueue.Next(contacts.Where(listing => !matchingIds.Contains(listing.Id)), collected, now);
    }

    internal ulong Next(IEnumerable<Listing> listings, IReadOnlyDictionary<ulong, PartyDetail> collected, DateTimeOffset now)
    {
        foreach (var id in attempted.Where(pair => now - pair.Value >= TimeSpan.FromMinutes(10)).Select(pair => pair.Key).ToArray()) attempted.Remove(id);
        var oldest = DateTimeOffset.MaxValue;
        ulong next = 0;
        foreach (var listing in listings.Where(l => l.Expires > now).DistinctBy(l => l.Id))
        {
            if (!CanRequest(listing, collected, now)) continue;
            var visited = attempted.GetValueOrDefault(listing.Id, DateTimeOffset.MinValue);
            if (collected.TryGetValue(listing.Id, out var detail) && detail.Collected > visited) visited = detail.Collected;
            if (next != 0 && visited >= oldest) continue;
            oldest = visited;
            next = listing.Id;
        }
        if (next == 0) return 0;
        if (attempted.Count >= 1000 && !attempted.ContainsKey(next)) attempted.Remove(attempted.MinBy(pair => pair.Value).Key);
        attempted[next] = now;
        return next;
    }
    internal bool CanRequest(Listing listing, IReadOnlyDictionary<ulong, PartyDetail> collected, DateTimeOffset now) =>
        listing.Expires > now && (!collected.TryGetValue(listing.Id, out var detail) || !IsFresh(listing, detail, now)) &&
        (!attempted.TryGetValue(listing.Id, out var last) || now - last >= TimeSpan.FromSeconds(15));
    internal void Clear() => attempted.Clear();
    internal static bool IsFresh(Listing listing, PartyDetail? detail, DateTimeOffset now) =>
        detail is not null && now - detail.Collected < TimeSpan.FromSeconds(60) && Compatible(listing, detail);
    internal static bool Compatible(Listing listing, PartyDetail detail) => detail.Recruitment is { } previous &&
        previous.Leader == listing.Leader && previous.Duty == listing.Duty && previous.Total == listing.Total && previous.Filled == listing.Filled &&
        previous.JobsPresent.Take(previous.Total).SequenceEqual(listing.JobsPresent.Take(listing.Total)) &&
        previous.Slots.Take(previous.Total).SequenceEqual(listing.Slots.Take(listing.Total));
}
