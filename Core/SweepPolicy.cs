namespace PartyScout.Core;

internal static class SweepPolicy
{
    internal static HashSet<ulong> PageListingIds(ReadOnlySpan<ulong> ids, int count) =>
        ids[..Math.Clamp(count, 0, ids.Length)].ToArray().Where(id => id != 0).ToHashSet();

    internal static bool OnPage(Listing listing, IReadOnlySet<ulong> visible, DateTimeOffset now) =>
        visible.Contains(listing.Id) && listing.Fresh(now);

    internal static bool ShowContact(Listing listing, DateTimeOffset now) =>
        listing.Expires > now && now - listing.Seen < TimeSpan.FromMinutes(2);

    internal static bool WaitForDetails(bool pending, DateTimeOffset now, DateTimeOffset pageDeadline) =>
        pending && now < pageDeadline;

    internal static string PageKey(string page) => page.Split('-')[0].Trim();

    internal static bool NextPageReady(string previous, string current, int received, int expected) =>
        current.Length > 0 && current != previous && received >= expected;

    internal static bool IsOwnedError(uint id, bool listingPending, bool platePending, bool manualInteraction) =>
        !manualInteraction && (id == 974 && listingPending || id is >= 5855 and <= 5860 && platePending);

    internal static bool PlateInteractionBlocks(bool manualDetail, bool finderInteracting, bool plateOpen) =>
        plateOpen || finderInteracting && !manualDetail;

    internal static bool OwnedPlatePending(long requested, long owner, bool verified, DateTimeOffset now, DateTimeOffset until) =>
        verified && requested != 0 && requested == owner && now <= until;

    internal static bool IsLateBackground(ulong id, ulong owned, bool recentlyRequested, bool switchTarget, bool manuallyViewed) =>
        id != 0 && owned == 0 && recentlyRequested && !switchTarget && !manuallyViewed;
}
