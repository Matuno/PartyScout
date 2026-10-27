// Adapted from PassportCheckerReborn b6bfcb4 (AGPL-3.0); see THIRD-PARTY-NOTICES.md.
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PartyScout.Core;

internal static partial class TomestoneData
{
    private static readonly string[] EncounterCategories = ["savage", "ultimate", "extremes", "criterion", "chaotic", "quantum"];

    // English duty name to Tomestone's expansion, zone and encounter slugs, as TomestoneViewer has them.
    private static readonly Dictionary<string, TomestoneEncounterParams> Encounters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Futures Rewritten (Ultimate)"] = new("dawntrail", "ultimates", "futures-rewritten-ultimate"),
        ["Dancing Mad (Ultimate)"] = new("dawntrail", "ultimates", "dancing-mad-ultimate"),
        ["The Omega Protocol (Ultimate)"] = new("endwalker", "ultimates", "the-omega-protocol-ultimate"),
        ["Dragonsong's Reprise (Ultimate)"] = new("endwalker", "ultimates", "dragonsongs-reprise-ultimate"),
        ["The Epic of Alexander (Ultimate)"] = new("shadowbringers", "ultimates", "the-epic-of-alexander-ultimate"),
        ["The Weapon's Refrain (Ultimate)"] = new("stormblood", "ultimates", "the-weapons-refrain-ultimate"),
        ["The Unending Coil of Bahamut (Ultimate)"] = new("stormblood", "ultimates", "the-unending-coil-of-bahamut-ultimate"),

        ["AAC Heavyweight M1 (Savage)"] = new("dawntrail", "aac-heavyweight-savage", "vamp-fatale"),
        ["AAC Heavyweight M2 (Savage)"] = new("dawntrail", "aac-heavyweight-savage", "red-hot-deep-blue"),
        ["AAC Heavyweight M3 (Savage)"] = new("dawntrail", "aac-heavyweight-savage", "the-tyrant"),
        ["AAC Heavyweight M4 (Savage) P1"] = new("dawntrail", "aac-heavyweight-savage", "lindwurm"),
        ["AAC Heavyweight M4 (Savage) P2"] = new("dawntrail", "aac-heavyweight-savage", "lindwurm-ii"),

        ["AAC Light-heavyweight M1 (Savage)"] = new("dawntrail", "aac-light-heavyweight-savage", "black-cat"),
        ["AAC Light-heavyweight M2 (Savage)"] = new("dawntrail", "aac-light-heavyweight-savage", "honey-b-lovely"),
        ["AAC Light-heavyweight M3 (Savage)"] = new("dawntrail", "aac-light-heavyweight-savage", "brute-bomber"),
        ["AAC Light-heavyweight M4 (Savage)"] = new("dawntrail", "aac-light-heavyweight-savage", "wicked-thunder"),

        ["AAC Cruiserweight M1 (Savage)"] = new("dawntrail", "aac-cruiserweight-savage", "dancing-green"),
        ["AAC Cruiserweight M2 (Savage)"] = new("dawntrail", "aac-cruiserweight-savage", "sugar-riot"),
        ["AAC Cruiserweight M3 (Savage)"] = new("dawntrail", "aac-cruiserweight-savage", "brute-abombinator"),
        ["AAC Cruiserweight M4 (Savage)"] = new("dawntrail", "aac-cruiserweight-savage", "howling-blade"),

        ["Worqor Lar Dor (Extreme)"] = new("dawntrail", "trials-extreme", "valigarmanda"),
        ["Everkeep (Extreme)"] = new("dawntrail", "trials-extreme", "everkept"),
        ["The Minstrel's Ballad: Sphene's Burden"] = new("dawntrail", "trials-extreme", "queen-eternal"),
        ["Recollection (Extreme)"] = new("dawntrail", "trials-extreme", "zelenia"),
        ["The Minstrel's Ballad: Necron's Embrace"] = new("dawntrail", "trials-extreme", "necron"),
        ["The Windward Wilds (Extreme)"] = new("dawntrail", "trials-extreme", "guardian-arkveld"),
        ["Hell on Rails (Extreme)"] = new("dawntrail", "trials-extreme", "doomtrain"),
        ["The Unmaking (Extreme)"] = new("dawntrail", "trials-extreme", "enuo"),

        ["Tsukuyomi's Pain (Unreal)"] = new("dawntrail", "unreal", "tsukuyomis-pain"),
        ["Shinryu's Domain (Unreal)"] = new("dawntrail", "unreal", "shinryu"),

        ["The Cloud of Darkness (Chaotic)"] = new("dawntrail", "chaotic", "the-cloud-of-darkness"),
    };

    // Duties the Party Finder lists once but Tomestone splits in two. The last part stands for the
    // full clear, so it is asked for first; the other is the fallback when it has no data.
    private static readonly Dictionary<string, (TomestoneEncounterParams Preferred, TomestoneEncounterParams Fallback)> MultiPartEncounters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["AAC Heavyweight M4 (Savage)"] = (
                new("dawntrail", "aac-heavyweight-savage", "lindwurm-ii"),
                new("dawntrail", "aac-heavyweight-savage", "lindwurm")),
        };

    // The activity endpoint has answered in several shapes; each is tried in turn.
    private static readonly string[][] ActivityArrayPaths = [["results"], ["data"], ["data", "paginator", "data"]];

    [GeneratedRegex(@"[^a-z0-9\s-]")]
    private static partial Regex SlugStripRegex();

    internal static (TomestoneEncounterParams? Preferred, TomestoneEncounterParams? Fallback) Encounter(string duty)
        => MultiPartEncounters.TryGetValue(duty, out var parts) ? parts : (Encounters.GetValueOrDefault(duty), null);

    internal static bool HasData(TomestoneCharacterInfo info) => info.Cleared || info.ProgPoint != null || info.TotalClears.HasValue;

    // The furthest mechanic reached, from a graph of { duration, mechanic: { name, number } } points.
    internal static string? ParseProgPointFromGraph(JsonElement root)
    {
        if (!(root.TryGetPath(out var graph, "data", "graph") || root.TryGetPath(out graph, "graph"))
            || graph.ValueKind != JsonValueKind.Array)
        {
            return ParseDirectProgPoint(root);
        }

        var longest = 0.0;
        string? mechanic = null;
        foreach (var point in graph.EnumerateArray())
        {
            if (point.GetNumberOrNull("duration") is not { } duration || duration <= longest)
            {
                continue;
            }

            longest = duration;
            if (point.TryGetPath(out var mechanicEl, "mechanic"))
            {
                mechanic = FormatMechanic(mechanicEl) ?? mechanic;
            }
        }

        return mechanic;
    }

    internal static string? ParseDirectProgPoint(JsonElement root)
    {
        if (root.TryGetPath(out var percent, "percent") || root.TryGetPath(out percent, "bestPercent"))
        {
            return percent.ToString();
        }

        return root.GetStringOrNull("progPoint") ?? root.GetStringOrNull("prog_point");
    }

    // "Splattershed", or "Splattershed #2" for a mechanic that comes round more than once.
    internal static string? FormatMechanic(JsonElement mechanic)
    {
        var name = mechanic.GetStringOrNull("name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var number = (int)(mechanic.GetNumberOrNull("number") ?? 0);
        return number > 1 ? $"{name} #{number}" : name;
    }

    internal static void ParseActivityResponse(TomestoneCharacterInfo info, JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            ParseActivityArray(info, root);
            return;
        }

        foreach (var path in ActivityArrayPaths)
        {
            if (root.TryGetPath(out var array, path) && array.ValueKind == JsonValueKind.Array)
            {
                ParseActivityArray(info, array);
                return;
            }
        }

        if ((root.GetNumberOrNull("clears") ?? root.GetNumberOrNull("killsCount")) is { } clears)
        {
            if (clears is >= 0 and <= int.MaxValue) info.TotalClears = (int)clears;
        }

        if (root.GetNumberOrNull("bestPercent") is { } bestPercent)
        {
            info.BestPercent = bestPercent;
        }
    }

    internal static void ParseActivityArray(TomestoneCharacterInfo info, JsonElement array)
    {
        var totalKills = 0;
        double? bestPercent = null;

        foreach (var entry in array.EnumerateArray())
        {
            var activity = entry.TryGetPath(out var nested, "activity") ? nested : entry;

            if (activity.GetNumberOrNull("killsCount") is { } kills and > 0)
            {
                if (kills <= int.MaxValue - totalKills) totalKills += (int)kills;
            }

            if (activity.TryGetPath(out var bestEl, "bestPercent")
                && ReadPercent(bestEl) is { } percent
                && (bestPercent is null || percent > bestPercent))
            {
                bestPercent = percent;
            }
        }

        if (totalKills > 0)
        {
            info.TotalClears = totalKills;
        }

        if (bestPercent.HasValue)
        {
            info.BestPercent = bestPercent;
        }
    }

    // A number, or a string such as "57%".
    internal static double? ReadPercent(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String when double.TryParse(element.GetString()?.TrimEnd('%'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    // Savage and ultimate categories group their encounters; the others list them flat.
    internal static void ParseProfileEncounters(
        TomestoneCharacterInfo info, JsonElement root, string dutyName, TomestoneEncounterParams? encounter)
    {
        if (!root.TryGetPath(out var encounters, "encounters"))
        {
            return;
        }

        foreach (var category in EncounterCategories)
        {
            if (!encounters.TryGetPath(out var groups, category) || groups.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var group in groups.EnumerateArray())
            {
                if (!group.TryGetPath(out var nested, "encounters") || nested.ValueKind != JsonValueKind.Array)
                {
                    if (MatchesEncounter(group, dutyName, encounter))
                    {
                        ExtractEncounterData(info, group);
                        return;
                    }

                    continue;
                }

                foreach (var candidate in nested.EnumerateArray())
                {
                    if (MatchesEncounter(candidate, dutyName, encounter))
                    {
                        ExtractEncounterData(info, candidate);
                        return;
                    }
                }
            }
        }
    }

    internal static bool MatchesEncounter(JsonElement candidate, string dutyName, TomestoneEncounterParams? encounter)
    {
        var zoneName = candidate.GetStringOrNull("zoneName");
        if (encounter == null)
        {
            return string.Equals(zoneName, dutyName, StringComparison.OrdinalIgnoreCase);
        }

        // The slug is that of the zone name for ultimates, and of the boss name for everything else.
        return string.Equals(Slugify(zoneName), encounter.Encounter, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Slugify(candidate.GetStringOrNull("name")), encounter.Encounter, StringComparison.OrdinalIgnoreCase);
    }

    internal static void ExtractEncounterData(TomestoneCharacterInfo info, JsonElement encounter)
    {
        // The profile only gives a cleared encounter's week, not a kill count. Hidden activity leaves out
        // the logged clear, but the achievement stays public.
        if ((encounter.TryGetPath(out var clear, "activity") || encounter.TryGetPath(out clear, "achievement"))
            && clear.ValueKind == JsonValueKind.Object)
        {
            info.Cleared = true;

            var week = clear.GetStringOrNull("completionWeek");
            if (!string.IsNullOrWhiteSpace(week))
            {
                info.CompletionWeek = week;
            }
        }

        if (!encounter.TryGetPath(out var progression, "progression"))
        {
            return;
        }

        if (progression.TryGetPath(out var mechanicEl, "mechanic") && FormatMechanic(mechanicEl) is { } mechanic)
        {
            info.ProgPoint = mechanic;
        }

        // Already formatted by the API, such as "35.82%".
        var display = progression.GetStringOrNull("displayPercent");
        if (!string.IsNullOrWhiteSpace(display))
        {
            info.DisplayPercent = display;
        }

        if (string.IsNullOrWhiteSpace(info.ProgPoint) && progression.TryGetPath(out var percent, "percent"))
        {
            info.ProgPoint = percent.ToString();
        }
    }

    // "Honey B. Lovely" becomes "honey-b-lovely".
    internal static string Slugify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var stripped = SlugStripRegex().Replace(text.Trim().ToLowerInvariant(), string.Empty);
        return string.Join("-", stripped.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

}

public record TomestoneEncounterParams(string Expansion, string Zone, string Encounter);

public class TomestoneCharacterInfo
{
    public string? Phase { get; set; }
    public bool Cleared { get; set; }
    public string? ProgPoint { get; set; }
    public string? DisplayPercent { get; set; }
    public int? TotalClears { get; set; }
    public string? CompletionWeek { get; set; }
    public double? BestPercent { get; set; }
    public bool NoLogs { get; set; }

    // Tomestone has no page for this name and world.
    public bool NotFound { get; set; }

    // The profile is public but its prog and logs are not.
    public bool ActivityHidden { get; set; }

    // A request failed, so missing data may only be missing from this lookup.
    public bool LookupFailed { get; set; }
}
