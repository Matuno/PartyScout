namespace PartyScout.Core;

public sealed class WatchedPlayer
{
    public string Name = "";
    public ushort World;
}
[Flags]
internal enum SocialKind { None = 0, Friend = 1, FreeCompany = 2, Custom = 4 }
internal sealed record SocialPerson(ulong Id, string Name, ushort World, SocialKind Kind);
internal sealed record SocialHit(Listing Listing, PreviewMember Member, SocialKind Kind, DateTimeOffset Seen);

internal static class Social
{
    internal static DateTimeOffset LastSeen(Listing listing, PartyDetail? detail, PreviewMember member)
    {
        var seen = member.Id != 0 && member.Id == listing.Leader ? listing.Seen : detail?.Collected ?? listing.Seen;
        return seen < listing.Seen ? seen : listing.Seen;
    }
    internal static bool Recent(DateTimeOffset seen, DateTimeOffset now) => now - seen < TimeSpan.FromMinutes(2);

    internal static SocialKind Match(PreviewMember member, IReadOnlyDictionary<ulong, SocialPerson> known, Settings settings)
    {
        var kind = known.TryGetValue(member.Id, out var person) ? person.Kind : SocialKind.None;
        if (!settings.WatchFriends) kind &= ~SocialKind.Friend;
        if (!settings.WatchFreeCompany) kind &= ~SocialKind.FreeCompany;
        if (member.Resolved && settings.CustomFriends.Any(p => p.World == member.WorldId && p.Name.Trim().Equals(member.Name, StringComparison.OrdinalIgnoreCase))) kind |= SocialKind.Custom;
        return kind;
    }
}

internal static class SlotRoles
{
    // PF uses one DPS colour for all three DPS roles.
    internal static int[] Colors(ulong mask) => Jobs.Rows.Where(j => (mask & Jobs.Mask(j)) != 0)
        .Select(j => Jobs.RoleOf(j) switch { Role.Tank => 0, Role.Healer => 1, Role.Melee or Role.Ranged or Role.Caster => 2, _ => -1 })
        .Where(r => r >= 0).Distinct().Order().ToArray();
}
