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
    internal uint SelectedDuty { get; set; }
    internal bool InParty { get; private set; }
    private long partyId;
    internal uint Duty => SelectedDuty != 0 ? SelectedDuty : Recruitment?.Duty ?? 0;

    internal bool Observe(bool inParty, long id, ulong leader, Listing? candidate)
    {
        var ended = InParty && (!inParty || partyId != 0 && id != 0 && partyId != id);
        if (ended && Recruitment is { } previous && candidate?.Leader == previous.Leader) candidate = null;
        if (ended || !inParty) Clear();
        if (!inParty) return ended;
        InParty = true;
        if (id != 0) partyId = id;
        if (Recruitment is null && leader != 0 && candidate?.Leader == leader) Remember(id, candidate);
        return ended;
    }

    internal void Remember(long id, Listing listing)
    {
        InParty = true;
        if (id != 0) partyId = id;
        Recruitment = new(listing.IsDuty ? listing.Duty : 0, listing.Description, listing.Id, listing.Leader);
    }

    internal void Clear() { Recruitment = null; SelectedDuty = 0; InParty = false; partyId = 0; }
}

internal static class Inspection
{
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

    internal static bool SameParty(PartyDetail? a, PartyDetail b) => a is not null && a.Id == b.Id && a.Duty == b.Duty &&
        a.Members.Select(m => (m.Id, m.Job, m.Slot)).SequenceEqual(b.Members.Select(m => (m.Id, m.Job, m.Slot)));
}
