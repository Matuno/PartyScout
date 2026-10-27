using System.Text.RegularExpressions;

namespace PartyScout.Core;

public enum Role { Tank, Healer, Melee, Ranged, Caster }

public sealed class SearchProfile
{
    public string Name = "Search 1";
    public bool Enabled = true;
    public HashSet<uint> Jobs = [];
    public HashSet<uint> Duties = [];
    public string Include = "", Exclude = "";
    public bool Regex;
    public bool RequireFullDutySize = true;

    public void Normalize()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "Search profile" : Name[..Math.Min(Name.Length, 80)];
        Jobs ??= []; Duties ??= [];
        Include = (Include ?? "")[..Math.Min(Include?.Length ?? 0, 2048)];
        Exclude = (Exclude ?? "")[..Math.Min(Exclude?.Length ?? 0, 2048)];
    }
}

public class Settings
{
    public List<WatchedPlayer> CustomFriends = [];
    public bool WatchFriends = true, WatchFreeCompany = true, WatchContacts = true;
    public uint CurrentPartyDuty;
    public List<SearchProfile> Profiles = [];
    // Read the original single-search fields once when migrating existing configurations.
    public HashSet<uint> Jobs = [];
    public HashSet<uint> Duties = [];
    public string Include = "", Exclude = "";
    public bool Regex;
    public int[] RoleWeights = [10, 10, 5, 5, 5];
    public bool Alerts = true, TimedJoin;
    public int JoinDelay = 30;
    public bool Toast = true, Sound = true, HideNative, ShowNativeFinder;
    public int SoundId = 6;
    public float Scale = 1, Rounding = 8, RowSpacing = 4, Opacity = .98f;
    public bool Compact = true, Debug;
    public RibbonMode? WindowMode;
    public ColourScheme Theme = ColourScheme.Grey;
    public uint ThemeSeed = 0x8B9A95;
    public ListingEmphasis Emphasis;

    public void Normalize(int? savedVersion = null)
    {
        CustomFriends ??= [];
        CustomFriends = CustomFriends.Where(p => p is not null && p.World > 0 && !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => new WatchedPlayer { Name = p.Name.Trim()[..Math.Min(p.Name.Trim().Length, 64)], World = p.World })
            .DistinctBy(p => (p.Name.ToUpperInvariant(), p.World)).Take(512).ToList();
        Profiles ??= [];
        Profiles.RemoveAll(p => p is null);
        if (Profiles.Count == 0)
            Profiles.Add(new SearchProfile { Jobs = Jobs ?? [], Duties = Duties ?? [], Include = Include, Exclude = Exclude, Regex = Regex });
        foreach (var profile in Profiles) profile.Normalize();
        Jobs = []; Duties = []; Include = Exclude = ""; Regex = false;
        if (RoleWeights is not { Length: 5 }) RoleWeights = [10, 10, 5, 5, 5];
        for (var i = 0; i < RoleWeights.Length; i++) RoleWeights[i] = Math.Clamp(RoleWeights[i], 0, 100);
        JoinDelay = Math.Clamp(JoinDelay, 10, 300); SoundId = Math.Clamp(SoundId, 1, 16);
        Scale = float.IsFinite(Scale) ? Math.Clamp(Scale, .75f, 1.5f) : 1;
        Rounding = float.IsFinite(Rounding) ? Math.Clamp(Rounding, 0, 18) : 10;
        RowSpacing = float.IsFinite(RowSpacing) ? Math.Clamp(RowSpacing, 2, 16) : 8;
        Opacity = float.IsFinite(Opacity) ? Math.Clamp(Opacity, .65f, 1) : .98f;
        WindowMode = WindowMode is { } mode && Enum.IsDefined(mode) ? mode : Compact ? RibbonMode.Collapsed : RibbonMode.Open;
        Compact = WindowMode == RibbonMode.Collapsed;
        if (!Enum.IsDefined(Theme) || (savedVersion is < 5 && Theme == ColourScheme.Teal)) Theme = ColourScheme.Grey;
        if (!Enum.IsDefined(Emphasis)) Emphasis = ListingEmphasis.Composition;
        ThemeSeed &= 0xFFFFFF;
    }
}

public static class Jobs
{
    // Party Finder's job bits differ from ClassJob row IDs after THM.
    public static readonly byte[] Rows = [1, 2, 3, 4, 5, 6, 7, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42];
    public static ulong Mask(uint row)
    {
        var index = Array.IndexOf(Rows, (byte)row);
        return row <= byte.MaxValue && index >= 0 ? 1UL << (index + 1) : 0;
    }
    public static Role? RoleOf(byte job) => job switch
    {
        1 or 3 or 19 or 21 or 32 or 37 => Role.Tank,
        6 or 24 or 28 or 33 or 40 => Role.Healer,
        2 or 4 or 20 or 22 or 29 or 30 or 34 or 39 or 41 => Role.Melee,
        5 or 23 or 31 or 38 => Role.Ranged,
        7 or 25 or 26 or 27 or 35 or 36 or 42 => Role.Caster,
        _ => null,
    };
    public static int Score(IEnumerable<byte> jobs, Settings settings)
        => jobs.Sum(j => RoleOf(j) is { } role ? settings.RoleWeights[(int)role] : 0);
    public static int? PartyScore(byte[] jobs, Settings settings)
        => jobs.Length > 0 && jobs.All(j => RoleOf(j) is not null) ? Score(jobs, settings) : null;
}

public sealed record Listing(ulong Id, ulong Leader, uint Duty, bool IsDuty, string Description,
    int Total, int Filled, byte[] JobsPresent, ulong[] Slots, bool OnePerJob, bool Private,
    int Parties, int ItemLevel, DateTimeOffset Seen, DateTimeOffset Expires)
{
    public string Host { get; init; } = "";
    public ushort HomeWorld { get; init; }
    public byte Objective { get; init; } = 1;
    public byte Conditions { get; init; } = 1;
    public byte DutySettings { get; init; }
    public byte Loot { get; init; }
    public byte Languages { get; init; }
    public bool Beginners { get; init; }
    public bool Fresh(DateTimeOffset now) => now - Seen < TimeSpan.FromSeconds(25) && Expires > now;
    public bool Accepts(uint job) => !Private && (!OnePerJob || !JobsPresent.Contains((byte)job)) &&
        Slots.Take(Total).Where((_, i) => i < JobsPresent.Length && JobsPresent[i] == 0).Any(mask => (mask & Jobs.Mask(job)) != 0);
}

public sealed record Match(Listing Listing, int Score, uint[] PlayableJobs)
{
    public string[] ProfileNames { get; init; } = [];
}

public sealed class ListingFilter
{
    private readonly Settings settings;
    private readonly IReadOnlyDictionary<uint, int> dutySizes;
    private readonly List<(SearchProfile Profile, Regex? Include, Regex? Exclude)> profiles = [];
    private readonly Dictionary<SearchProfile, string> errors = [];
    public string? Error => errors.Count == 0 ? null : string.Join("\n", errors.Select(e => $"{e.Key.Name}: {e.Value}"));

    public ListingFilter(Settings settings, IReadOnlyDictionary<uint, int>? dutySizes = null)
    {
        this.settings = settings;
        this.dutySizes = dutySizes ?? new Dictionary<uint, int>();
        foreach (var profile in settings.Profiles.Where(p => p.Enabled))
        {
            try
            {
                const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
                var include = profile.Regex && profile.Include.Length > 0 ? new Regex(profile.Include, options, TimeSpan.FromMilliseconds(30)) : null;
                var exclude = profile.Regex && profile.Exclude.Length > 0 ? new Regex(profile.Exclude, options, TimeSpan.FromMilliseconds(30)) : null;
                profiles.Add((profile, include, exclude));
            }
            catch (ArgumentException) { errors[profile] = "Invalid regular expression. This profile is paused until corrected."; }
        }
    }

    public Match? Evaluate(Listing listing, DateTimeOffset now)
    {
        if (!listing.Fresh(now) || listing.Filled >= listing.Total || listing.Private) return null;
        var jobs = new HashSet<uint>();
        var names = new List<string>();
        foreach (var (profile, include, exclude) in profiles)
        {
            if (errors.ContainsKey(profile) || profile.Duties.Count > 0 && (!listing.IsDuty || !profile.Duties.Contains(listing.Duty))) continue;
            if (profile.RequireFullDutySize && listing.IsDuty && dutySizes.TryGetValue(listing.Duty, out var size) && size > 0 && listing.Total < size) continue;
            var playable = profile.Jobs.Where(listing.Accepts).ToArray();
            if (playable.Length == 0) continue;
            try
            {
                var description = listing.Description;
                var matches = profile.Regex
                    ? (include is null || include.IsMatch(description)) && (exclude is null || !exclude.IsMatch(description))
                    : Terms(profile.Include).All(t => description.Contains(t, StringComparison.OrdinalIgnoreCase)) &&
                      !Terms(profile.Exclude).Any(t => description.Contains(t, StringComparison.OrdinalIgnoreCase));
                if (!matches) continue;
                jobs.UnionWith(playable); names.Add(profile.Name);
            }
            catch (RegexMatchTimeoutException) { errors[profile] = "Regular expression took too long. This profile is paused until corrected."; }
        }
        return jobs.Count == 0 ? null : new Match(listing, Jobs.Score(listing.JobsPresent.Take(listing.Total), settings), jobs.Order().ToArray())
            { ProfileNames = names.Distinct().ToArray() };
    }

    public bool InEnabledDuties(Listing listing) => settings.Profiles.Any(profile => profile.Enabled &&
        (profile.Duties.Count == 0 || listing.IsDuty && profile.Duties.Contains(listing.Duty)));

    // shortcut: capacities above PF's 48-slot limit are unknown; revisit if PF supports larger listings.
    public static int DutyCapacity(int membersPerParty, int parties) => membersPerParty > 0 && parties > 0 &&
        (long)membersPerParty * parties <= 48 ? membersPerParty * parties : 0;

    private static string[] Terms(string text) => text.Split([',', '\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

public sealed class AlertState
{
    private readonly Dictionary<ulong, int> dismissed = [];
    private readonly Dictionary<ulong, int> announced = [];
    public Match? Target { get; private set; }
    public DateTimeOffset? Deadline { get; private set; }

    public bool Update(IEnumerable<Match> matches, int? baseline, bool enabled, bool timed, int delay, DateTimeOffset now, uint currentJob)
    {
        var best = enabled && baseline is { } baselineScore ? matches.FirstOrDefault(m => m.Score > baselineScore && m.Listing.Fresh(now) &&
            (!dismissed.TryGetValue(m.Listing.Id, out var score) || m.Score > score)) : null;
        var changed = best?.Listing.Id != Target?.Listing.Id;
        timed = timed && best is not null && best.Listing.Parties == 1 && best.PlayableJobs.Contains(currentJob);
        if (changed) Deadline = best is not null && timed ? now.AddSeconds(delay) : null;
        if (!timed) Deadline = null;
        if (best is not null && timed && Deadline is null) Deadline = now.AddSeconds(delay);
        Target = best;
        if (best is null || announced.TryGetValue(best.Listing.Id, out var previous) && best.Score <= previous) return false;
        Remember(announced, best.Listing.Id, best.Score);
        return true;
    }

    public void Dismiss(Match? match = null)
    {
        if ((match ?? Target) is { } target)
        {
            var score = Target?.Listing.Id == target.Listing.Id ? Math.Max(target.Score, Target.Score) : target.Score;
            Remember(dismissed, target.Listing.Id, Math.Max(score, dismissed.GetValueOrDefault(target.Listing.Id, -1)));
            if (Target?.Listing.Id == target.Listing.Id) Clear();
        }
    }
    public void Clear() { Target = null; Deadline = null; }
    public void SuppressExisting(IEnumerable<Match> matches)
    {
        foreach (var match in matches)
        {
            Remember(dismissed, match.Listing.Id, match.Score);
            Remember(announced, match.Listing.Id, match.Score);
        }
        Clear();
    }
    public void Reset() { Clear(); dismissed.Clear(); announced.Clear(); }
    private static void Remember(Dictionary<ulong, int> history, ulong id, int score)
    {
        if (history.Count >= 2048 && !history.ContainsKey(id)) history.Remove(history.Keys.First());
        history[id] = score;
    }
}
