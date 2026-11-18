namespace PartyScout.Core;

internal enum NameState { Pending, Resolved, Unavailable }
internal sealed record PreviewMember(ulong Id, byte Job, string Name = "", string World = "", string Region = "", NameState State = NameState.Pending)
{
    internal int Slot { get; init; } = -1;
    internal ushort WorldId { get; init; }
    internal bool Resolved => State == NameState.Resolved && Name.Length > 0 && World.Length > 0;
}
internal sealed record PartyDetail(ulong Id, uint Duty, string Description, PreviewMember[] Members)
{
    internal Listing? Recruitment { get; init; }
    internal DateTimeOffset Collected { get; init; }
}
internal sealed record Performance(int? Kills, double? JobBest, double? AnyBest, string? BestJob, string Status)
{
    internal double? Average { get; init; }
    internal double? LowestWipe { get; init; }
    internal double? LatestZoneAverage { get; init; }
    internal bool LatestZoneMedian { get; init; }
    internal bool LatestZoneFailed { get; init; }
    internal int Samples { get; init; }
    internal bool Failed { get; init; }
}

internal sealed class MemberData
{
    internal TomestoneCharacterInfo? Progress { get; set; }
    internal Performance[]? Parses { get; set; }
    internal string? ProgressError { get; set; }
    internal string? ParseError { get; set; }
    internal DateTimeOffset ProgressAt { get; set; }
    internal DateTimeOffset ParsesAt { get; set; }
}

internal sealed record PartyRecruitmentSnapshot(uint Duty, string Description, ulong ListingId, ulong Leader);

internal sealed class CurrentPartyContext
{
    internal PartyRecruitmentSnapshot? Recruitment { get; private set; }
    internal uint Duty { get; private set; }
    internal bool InParty { get; private set; }
    private long partyId;

    internal bool Observe(bool inParty, long id, ulong leader, Listing? candidate, bool sameParty = false)
    {
        var ended = InParty && (!inParty || !sameParty && partyId != 0 && id != 0 && partyId != id);
        if (ended && Recruitment is { } previous && candidate?.Leader == previous.Leader) candidate = null;
        if (ended || !inParty) Clear();
        if (!inParty) return ended;
        InParty = true;
        if (id != 0) partyId = id;
        if (Recruitment is null && leader != 0 && candidate is { } listing && listing.Leader == leader &&
            (Duty == 0 || listing.IsDuty && listing.Duty == Duty)) Remember(id, listing);
        return ended;
    }

    internal void Remember(long id, Listing listing)
    {
        InParty = true;
        if (id != 0) partyId = id;
        var duty = listing.IsDuty ? listing.Duty : 0;
        RememberDuty(duty);
        Recruitment = new(duty, listing.Description, listing.Id, listing.Leader);
    }

    internal void RememberDuty(uint duty) { if (Duty == 0) Duty = duty; }

    internal void Clear() { Recruitment = null; Duty = 0; InParty = false; partyId = 0; }
}

internal static class Inspection
{
    internal static IEnumerable<PartyDetail> LookupBatches(IEnumerable<PartyDetail> ordered) =>
        ordered.GroupBy(detail => detail.Duty).Select(group => group.First() with
        {
            Members = group.SelectMany(detail => detail.Members).Where(member => member.Resolved)
                .DistinctBy(member => MemberKey(member, group.Key)).ToArray(),
        });

    internal static PartyDetail? SingleMemberListing(Listing listing)
    {
        if (listing.Id == 0 || listing.Leader == 0 || listing.Parties != 1 || listing.Filled != 1 ||
            listing.Host.Length == 0 || listing.HomeWorld == 0 || listing.Total is < 1 or > 48) return null;
        var occupied = Enumerable.Range(0, listing.Total).Where(slot => listing.JobsPresent.ElementAtOrDefault(slot) != 0).Take(2).ToArray();
        if (occupied.Length != 1) return null;
        var slot = occupied[0];
        return new PartyDetail(listing.Id, listing.IsDuty ? listing.Duty : 0, listing.Description,
            [new PreviewMember(listing.Leader, listing.JobsPresent[slot], listing.Host) { Slot = slot, WorldId = listing.HomeWorld }])
            { Recruitment = listing, Collected = listing.Seen };
    }

    internal static PreviewMember[] SlottedMembers(int count, Func<int, PreviewMember?> read)
    {
        var members = new List<PreviewMember>();
        for (var slot = 0; slot < Math.Clamp(count, 0, 48); slot++)
            if (read(slot) is { } member && (member.Id != 0 || member.Name.Length > 0 && member.WorldId != 0))
                members.Add(member with { Slot = slot });
        return members.ToArray();
    }

    internal static int RosterSlots(PartyDetail detail) => Math.Clamp(detail.Recruitment?.Total ??
        Math.Max(8, detail.Members.Select(m => m.Slot + 1).DefaultIfEmpty().Max()), 1, 48);

    internal static PreviewMember?[] DisplayRoster(PreviewMember[] members, int slots) =>
        Enumerable.Range(0, slots).Select(slot => members.FirstOrDefault(m => m.Slot == slot))
            .Concat(members.Where(m => m.Slot < 0 || m.Slot >= slots)).ToArray();

    internal static (ulong Id, string Name, string World, byte Job, uint Duty) MemberKey(PreviewMember member, uint duty) =>
        (member.Id, member.Id == 0 ? member.Name.ToUpperInvariant() : "", member.Id == 0 ? member.World.ToUpperInvariant() : "", member.Job, duty);

}
