using System.Text.RegularExpressions;

namespace PartyScout.Core;

public enum Role { Tank, Healer, Melee, Ranged, Caster }

[Flags]
public enum PartyObjectives : byte { Any = 0, Unspecified = 1, Completion = 2, Practice = 4, Loot = 8 }

[Flags]
public enum ClearRequirements : byte { Any = 0, Unrestricted = 1, Cleared = 2, Incomplete = 4 }

public sealed class SearchProfile
{
    public string Name = "Search 1";
    public bool Enabled = true;
    public HashSet<uint> Jobs = [];
    public HashSet<uint> PreferredJobs = [];
    public int PreferredFillAllowance;
    public uint Duty;
    public Guid GroupId;
    // Read on migration from profiles with multiple duties.
    public HashSet<uint>? Duties;
    public string Include = "", Exclude = "";
    public bool Regex;
    public bool RequireFullDutySize = true;
    public PartyObjectives Objectives;
    public ClearRequirements ClearRequirements;

    public void Normalize()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "Search profile" : Name[..Math.Min(Name.Length, 80)];
        Jobs ??= [];
        PreferredJobs ??= []; PreferredJobs.IntersectWith(Jobs);
        PreferredFillAllowance = Math.Clamp(PreferredFillAllowance, 0, 2);
        Objectives &= PartyObjectives.Unspecified | PartyObjectives.Completion | PartyObjectives.Practice | PartyObjectives.Loot;
        ClearRequirements &= Core.ClearRequirements.Unrestricted | Core.ClearRequirements.Cleared | Core.ClearRequirements.Incomplete;
        Include = (Include ?? "")[..Math.Min(Include?.Length ?? 0, 2048)];
        Exclude = (Exclude ?? "")[..Math.Min(Exclude?.Length ?? 0, 2048)];
    }
    public void ToggleJob(uint job)
    {
        if (!Jobs.Remove(job)) Jobs.Add(job);
        else PreferredJobs.Remove(job);
    }
    public void TogglePreference(uint job)
    {
        Jobs.Add(job);
        if (!PreferredJobs.Add(job)) PreferredJobs.Remove(job);
    }
    public void ToggleRolePreference(Role role)
    {
        var selected = Jobs.Where(job => job <= byte.MaxValue && PartyScout.Core.Jobs.RoleOf((byte)job) == role).ToArray();
        if (selected.All(PreferredJobs.Contains)) PreferredJobs.ExceptWith(selected);
        else PreferredJobs.UnionWith(selected);
    }

    internal bool Targets(Listing listing) => Duty != 0 && listing.IsDuty && listing.Duty == Duty;

    internal SearchProfile CopyForDuty(uint duty)
    {
        var copy = (SearchProfile)MemberwiseClone();
        copy.Duty = duty;
        copy.Jobs = [.. Jobs];
        copy.PreferredJobs = [.. PreferredJobs];
        return copy;
    }
}

public sealed class SearchGroup
{
    public Guid Id = Guid.NewGuid();
    public string Name = "Search group";
    public bool Enabled = true;
}

public class Settings
{
    public List<WatchedPlayer> CustomFriends = [];
    public bool WatchFriends = true, WatchFreeCompany = true;
    public uint CurrentPartyDuty;
    public List<SearchProfile> Profiles = [];
    public List<SearchGroup> Groups = [];
    public bool CompanionContactsExpanded = true;
    // Read the original single-search fields once when migrating existing configurations.
    public HashSet<uint> Jobs = [];
    public HashSet<uint> Duties = [];
    public string Include = "", Exclude = "";
    public bool Regex;
    public int[] RoleWeights = [10, 10, 5, 5, 5];
    public bool Alerts = true;
    public int JoinDelay = 30;
    public int DutyPartySeconds = 15;
    public bool Toast = true, Sound = true, ShowNativeFinder;
    public bool? OpenBetterMatch;
    public int SoundId = 6;
    public float Scale = 1, Rounding = 8, RowSpacing = 4, Opacity = .98f;
    public bool Compact = true, Debug;
    public RibbonMode? WindowMode;
    public ColourScheme Theme = ColourScheme.Grey;
    public uint ThemeSeed = 0x8B9A95;
    public InterfaceDesign Design = InterfaceDesign.Material;
    public bool UseDesignColours = true;
    public ListingEmphasis Emphasis;

    internal SearchGroup? GroupFor(Guid id) => Groups.Find(group => group.Id == id);
    internal bool IsGroupEnabled(Guid id) => id == Guid.Empty || GroupFor(id)?.Enabled == true;
    internal IEnumerable<SearchProfile> EnabledProfiles =>
        Profiles.Where(profile => profile.Enabled && profile.Duty != 0 && IsGroupEnabled(profile.GroupId));

    internal void SetGroupsEnabled(bool enabled)
    {
        foreach (var group in Groups) group.Enabled = enabled;
    }

    internal bool RemoveSearch(SearchProfile profile) => Profiles.Count > 1 && Profiles.Remove(profile);

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
        NormalizeSearches();
        Jobs = []; Duties = []; Include = Exclude = ""; Regex = false;
        if (RoleWeights is not { Length: 5 }) RoleWeights = [10, 10, 5, 5, 5];
        for (var i = 0; i < RoleWeights.Length; i++) RoleWeights[i] = Math.Clamp(RoleWeights[i], 0, 100);
        JoinDelay = Math.Clamp(JoinDelay, 10, 300); SoundId = Math.Clamp(SoundId, 1, 16);
        DutyPartySeconds = Math.Clamp(DutyPartySeconds, 0, 120);
        Scale = float.IsFinite(Scale) ? Math.Clamp(Scale, .75f, 1.5f) : 1;
        Rounding = float.IsFinite(Rounding) ? Math.Clamp(Rounding, 0, 18) : 10;
        RowSpacing = float.IsFinite(RowSpacing) ? Math.Clamp(RowSpacing, 2, 16) : 8;
        Opacity = float.IsFinite(Opacity) ? Math.Clamp(Opacity, .65f, 1) : .98f;
        WindowMode = WindowMode switch
        {
            RibbonMode.Open or RibbonMode.Half => RibbonMode.Open,
            RibbonMode.Collapsed => RibbonMode.Collapsed,
            _ => Compact ? RibbonMode.Collapsed : RibbonMode.Open,
        };
        // Preserve popup-only alerts for people who switched off toasts.
        OpenBetterMatch ??= !Toast;
        Compact = WindowMode == RibbonMode.Collapsed;
        if (!Enum.IsDefined(Theme) || (savedVersion is < 5 && Theme == ColourScheme.Teal)) Theme = ColourScheme.Grey;
        Design = InterfaceDesign.Material;
        if (!Enum.IsDefined(Emphasis)) Emphasis = ListingEmphasis.Composition;
        ThemeSeed &= 0xFFFFFF;
    }

    private void NormalizeSearches()
    {
        Groups ??= [];
        var groupIds = new HashSet<Guid>();
        Groups.RemoveAll(group => group is null || group.Id == Guid.Empty || !groupIds.Add(group.Id));
        foreach (var group in Groups)
        {
            var name = group.Name?.Trim();
            group.Name = string.IsNullOrEmpty(name) ? "Search group" : name[..Math.Min(name.Length, 80)];
        }

        for (var index = 0; index < Profiles.Count; index++)
        {
            var profile = Profiles[index];
            profile.Normalize();
            if (profile.GroupId != Guid.Empty && !groupIds.Contains(profile.GroupId))
            {
                profile.GroupId = Guid.Empty;
                profile.Enabled = false;
            }
            var oldDuties = profile.Duties;
            profile.Duties = null;
            if (profile.Duty != 0 || oldDuties is not { Count: > 0 }) continue;

            var duties = oldDuties.Where(duty => duty != 0).OrderDescending().ToArray();
            profile.Duty = duties.FirstOrDefault();
            if (duties.Length < 2) continue;
            if (profile.GroupId == Guid.Empty)
            {
                var group = new SearchGroup { Name = profile.Name };
                Groups.Add(group);
                groupIds.Add(group.Id);
                profile.GroupId = group.Id;
            }
            Profiles.InsertRange(index + 1, duties.Skip(1).Select(profile.CopyForDuty));
            index += duties.Length - 1;
        }
    }
}

public static class Jobs
{
    // Party Finder's job bits differ from ClassJob row IDs after THM.
    public static readonly byte[] Rows = [1, 2, 3, 4, 5, 6, 7, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42];
    internal static bool InProfilePicker(uint job, bool selected) => selected || job >= 19 && job != 26;
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
    public bool UsesJobPreferences { get; init; }
    public Dictionary<uint, double> JobPreferences { get; init; } = [];
    public uint[] PreferredJobs => JobPreferences.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).Select(pair => pair.Key).ToArray();
    public double PreferenceBonus => JobPreferences.Count == 0 ? 0 : JobPreferences.Values.Max();
    public double Rank => UsesJobPreferences ? Listing.Filled + PreferenceBonus : Score;
    public double RankForJob(uint job) => !PlayableJobs.Contains(job) ? double.NegativeInfinity :
        UsesJobPreferences ? Listing.Filled + JobPreferences.GetValueOrDefault(job) : Score;
    public uint SelectJob(uint currentJob, IEnumerable<uint> gearJobs) => PlayableJobs
        .Where(job => job == currentJob || gearJobs.Contains(job))
        .OrderByDescending(RankForJob).ThenByDescending(job => job == currentJob).ThenBy(job => job)
        .FirstOrDefault();
}

public sealed class ListingFilter
{
    private readonly Settings settings;
    private readonly IReadOnlyDictionary<uint, int> dutySizes;
    private readonly List<(SearchProfile Profile, Regex? Include, Regex? Exclude, string[] IncludeTerms, string[] ExcludeTerms)> profiles = [];
    private readonly Dictionary<SearchProfile, string> errors = [];
    public string? Error => errors.Count == 0 ? null : string.Join("\n", errors.Select(e => $"{e.Key.Name}: {e.Value}"));
    internal string? ErrorFor(SearchProfile profile) => errors.GetValueOrDefault(profile);
    public bool UsesJobPreferences { get; }

    public ListingFilter(Settings settings, IReadOnlyDictionary<uint, int>? dutySizes = null)
    {
        this.settings = settings;
        this.dutySizes = dutySizes ?? new Dictionary<uint, int>();
        var enabled = settings.EnabledProfiles.ToArray();
        UsesJobPreferences = enabled.Any(profile => profile.PreferredJobs.Overlaps(profile.Jobs));
        foreach (var profile in enabled)
        {
            try
            {
                const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
                var include = profile.Regex && profile.Include.Length > 0 ? new Regex(profile.Include, options, TimeSpan.FromMilliseconds(30)) : null;
                var exclude = profile.Regex && profile.Exclude.Length > 0 ? new Regex(profile.Exclude, options, TimeSpan.FromMilliseconds(30)) : null;
                profiles.Add((profile, include, exclude, Terms(profile.Include), Terms(profile.Exclude)));
            }
            catch (ArgumentException) { errors[profile] = "Invalid regular expression. This profile is paused until corrected."; }
        }
    }

    public Match? Evaluate(Listing listing, DateTimeOffset now)
    {
        if (!listing.Fresh(now) || listing.Filled >= listing.Total || listing.Private) return null;
        var jobs = new HashSet<uint>();
        var names = new List<string>();
        var preferences = new Dictionary<uint, double>();
        foreach (var (profile, include, exclude, includeTerms, excludeTerms) in profiles)
        {
            if (errors.ContainsKey(profile) || !profile.Targets(listing)) continue;
            if (profile.RequireFullDutySize && listing.IsDuty && dutySizes.TryGetValue(listing.Duty, out var size) && size > 0 && listing.Total < size) continue;
            if (!MatchesRecruitment(profile, listing)) continue;
            var playable = profile.Jobs.Where(listing.Accepts).ToArray();
            if (playable.Length == 0) continue;
            try
            {
                var description = listing.Description;
                var matches = profile.Regex
                    ? (include is null || include.IsMatch(description)) && (exclude is null || !exclude.IsMatch(description))
                    : includeTerms.All(t => description.Contains(t, StringComparison.OrdinalIgnoreCase)) &&
                      !excludeTerms.Any(t => description.Contains(t, StringComparison.OrdinalIgnoreCase));
                if (!matches) continue;
                jobs.UnionWith(playable); names.Add(profile.Name);
                foreach (var job in playable.Where(profile.PreferredJobs.Contains))
                    preferences[job] = Math.Max(preferences.GetValueOrDefault(job), .5 + profile.PreferredFillAllowance);
            }
            catch (RegexMatchTimeoutException) { errors[profile] = "Regular expression took too long. This profile is paused until corrected."; }
        }
        return jobs.Count == 0 ? null : new Match(listing, Jobs.Score(listing.JobsPresent.Take(listing.Total), settings), jobs.Order().ToArray())
            { ProfileNames = names.Distinct().ToArray(), UsesJobPreferences = UsesJobPreferences, JobPreferences = preferences };
    }

    private static bool MatchesRecruitment(SearchProfile profile, Listing listing)
    {
        var objective = listing.Objective == 0 ? PartyObjectives.Unspecified : (PartyObjectives)listing.Objective;
        if (profile.Objectives != PartyObjectives.Any && (profile.Objectives & objective) == 0) return false;
        if (profile.ClearRequirements == ClearRequirements.Any) return true;
        var conditions = listing.Conditions;
        var requirement = ClearRequirements.Any;
        // The native no-restriction bit is 1; weekly-unclaimed (8) also requires a clear.
        if (conditions == 0 || (conditions & 1) != 0) requirement |= ClearRequirements.Unrestricted;
        if ((conditions & (2 | 8)) != 0) requirement |= ClearRequirements.Cleared;
        if ((conditions & 4) != 0) requirement |= ClearRequirements.Incomplete;
        return (profile.ClearRequirements & requirement) != 0;
    }

    public double? CurrentRank(byte[] members, uint currentJob, uint duty = 0)
    {
        var score = Jobs.PartyScore(members, settings);
        if (score is null || !UsesJobPreferences) return score;
        if (currentJob > byte.MaxValue || !members.Contains((byte)currentJob)) return null;
        var bonus = settings.EnabledProfiles.Where(profile => profile.Jobs.Contains(currentJob) &&
            profile.PreferredJobs.Contains(currentJob) && (duty == 0 || profile.Duty == duty))
            .Select(profile => .5 + profile.PreferredFillAllowance).DefaultIfEmpty(0).Max();
        // Listings do not include you yet; compare their other members with your party's other members.
        return members.Length - 1 + bonus;
    }

    public bool InEnabledDuties(Listing listing) => settings.EnabledProfiles.Any(profile => profile.Targets(listing));

    // shortcut: capacities above PF's 48-slot limit are unknown; revisit if PF supports larger listings.
    public static int DutyCapacity(int membersPerParty, int parties) => membersPerParty > 0 && parties > 0 &&
        (long)membersPerParty * parties <= 48 ? membersPerParty * parties : 0;

    private static string[] Terms(string text) => text.Split([',', '\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

public sealed class AlertState
{
    private readonly Dictionary<ulong, double> dismissed = [];
    private readonly Dictionary<ulong, double> announced = [];
    public Match? Target { get; private set; }
    public DateTimeOffset? Deadline { get; private set; }
    public uint TargetJob { get; private set; }
    private uint lastCurrentJob;

    public bool Update(IEnumerable<Match> matches, double? baseline, bool enabled, bool timed, int delay, DateTimeOffset now, uint currentJob, IEnumerable<uint>? gearJobs = null)
    {
        var best = enabled && baseline is { } baselineScore ? matches.FirstOrDefault(m => m.Rank > baselineScore && m.Listing.Fresh(now) &&
            (!dismissed.TryGetValue(m.Listing.Id, out var score) || m.Rank > score)) : null;
        var changed = best?.Listing.Id != Target?.Listing.Id;
        var job = best?.SelectJob(currentJob, gearJobs ?? []) ?? 0;
        timed = timed && best is not null && job != 0 && best.Listing.Parties == 1 && best.RankForJob(job) > baseline;
        if (changed || job != TargetJob || currentJob != lastCurrentJob) Deadline = best is not null && timed ? now.AddSeconds(delay) : null;
        TargetJob = job;
        lastCurrentJob = currentJob;
        if (!timed) Deadline = null;
        if (best is not null && timed && Deadline is null) Deadline = now.AddSeconds(delay);
        Target = best;
        if (best is null || announced.TryGetValue(best.Listing.Id, out var previous) && best.Rank <= previous) return false;
        Remember(announced, best.Listing.Id, best.Rank);
        return true;
    }

    public void Dismiss()
    {
        if (Target is not { } target) return;
        Remember(dismissed, target.Listing.Id, target.Rank);
        Clear();
    }
    public void Clear() { Target = null; Deadline = null; TargetJob = 0; lastCurrentJob = 0; }
    public void SuppressExisting(IEnumerable<Match> matches)
    {
        foreach (var match in matches)
        {
            Remember(dismissed, match.Listing.Id, match.Rank);
            Remember(announced, match.Listing.Id, match.Rank);
        }
        Clear();
    }
    public void Reset() { Clear(); dismissed.Clear(); announced.Clear(); }
    private static void Remember(Dictionary<ulong, double> history, ulong id, double score)
    {
        if (history.Count >= 2048 && !history.ContainsKey(id)) history.Remove(history.Keys.First());
        history[id] = score;
    }
}
