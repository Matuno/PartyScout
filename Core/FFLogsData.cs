// Adapted from PassportCheckerReborn b6bfcb4 (AGPL-3.0); see THIRD-PARTY-NOTICES.md.
using System.Text.Json;

namespace PartyScout.Core;

internal static class FFLogsData
{
    private const int DifficultyNormal = 100;
    private const int DifficultySavage = 101;

    // English duty name to FFLogs encounter. Needs a new entry each content tier.
    private static readonly Dictionary<string, (int EncounterId, int Difficulty)> Encounters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AAC Light-heavyweight M1 (Savage)"] = (93, DifficultySavage),
        ["AAC Light-heavyweight M2 (Savage)"] = (94, DifficultySavage),
        ["AAC Light-heavyweight M3 (Savage)"] = (95, DifficultySavage),
        ["AAC Light-heavyweight M4 (Savage)"] = (96, DifficultySavage),

        ["AAC Cruiserweight M1 (Savage)"] = (97, DifficultySavage),
        ["AAC Cruiserweight M2 (Savage)"] = (98, DifficultySavage),
        ["AAC Cruiserweight M3 (Savage)"] = (99, DifficultySavage),
        ["AAC Cruiserweight M4 (Savage)"] = (100, DifficultySavage),

        ["AAC Heavyweight M1 (Savage)"] = (101, DifficultySavage),
        ["AAC Heavyweight M2 (Savage)"] = (102, DifficultySavage),
        ["AAC Heavyweight M3 (Savage)"] = (103, DifficultySavage),
        ["AAC Heavyweight M4 (Savage) P1"] = (104, DifficultySavage),
        ["AAC Heavyweight M4 (Savage) P2"] = (105, DifficultySavage),

        ["The Cloud of Darkness (Chaotic)"] = (2061, DifficultyNormal),

        ["Tsukuyomi's Pain (Unreal)"] = (3012, DifficultyNormal),
        ["Shinryu's Domain (Unreal)"] = (3013, DifficultyNormal),

        ["Worqor Lar Dor (Extreme)"] = (1071, DifficultyNormal),
        ["Everkeep (Extreme)"] = (1072, DifficultyNormal),
        ["The Minstrel's Ballad: Sphene's Burden"] = (1078, DifficultyNormal),
        ["Recollection (Extreme)"] = (1080, DifficultyNormal),
        ["The Minstrel's Ballad: Necron's Embrace"] = (1081, DifficultyNormal),
        ["The Windward Wilds (Extreme)"] = (1082, DifficultyNormal),
        ["Hell on Rails (Extreme)"] = (1083, DifficultyNormal),
        ["The Unmaking (Extreme)"] = (1084, DifficultyNormal),

        ["The Unending Coil of Bahamut (Ultimate)"] = (1073, DifficultyNormal),
        ["The Weapon's Refrain (Ultimate)"] = (1074, DifficultyNormal),
        ["The Epic of Alexander (Ultimate)"] = (1075, DifficultyNormal),
        ["Dragonsong's Reprise (Ultimate)"] = (1076, DifficultyNormal),
        ["The Omega Protocol (Ultimate)"] = (1077, DifficultyNormal),
        ["Futures Rewritten (Ultimate)"] = (1079, DifficultyNormal),
        ["Dancing Mad (Ultimate)"] = (1085, DifficultyNormal),
    };

    // Duties the Party Finder lists once but FFLogs splits into two encounters.
    private static readonly Dictionary<string, (int Phase1, int Phase2)> MultiPartEncounters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AAC Heavyweight M4 (Savage)"] = (104, 105),
    };

    private static readonly (string Job, string Spec, uint ClassJobId)[] Jobs =
    [
        ("PLD", "Paladin", 19), ("WAR", "Warrior", 21), ("DRK", "DarkKnight", 32), ("GNB", "Gunbreaker", 37),
        ("WHM", "WhiteMage", 24), ("SCH", "Scholar", 28), ("AST", "Astrologian", 33), ("SGE", "Sage", 40),
        ("MNK", "Monk", 20), ("DRG", "Dragoon", 22), ("NIN", "Ninja", 30), ("SAM", "Samurai", 34),
        ("RPR", "Reaper", 39), ("VPR", "Viper", 41),
        ("BRD", "Bard", 23), ("MCH", "Machinist", 31), ("DNC", "Dancer", 38),
        ("BLM", "BlackMage", 25), ("SMN", "Summoner", 27), ("RDM", "RedMage", 35), ("PCT", "Pictomancer", 42),
    ];

    private static readonly Dictionary<string, string> JobBySpec =
        Jobs.ToDictionary(job => job.Spec, job => job.Job, StringComparer.OrdinalIgnoreCase);

    internal static (int Id, int Difficulty)[] Encounter(string duty)
        => MultiPartEncounters.TryGetValue(duty, out var parts)
            ? [(parts.Phase1, DifficultySavage), (parts.Phase2, DifficultySavage)]
            : Encounters.TryGetValue(duty, out var encounter) ? [(encounter.EncounterId, encounter.Difficulty)] : [];

    internal static string Selector(PreviewMember member)
        => $"character(name: {JsonSerializer.Serialize(member.Name)}, serverSlug: {JsonSerializer.Serialize(member.World.ToLowerInvariant())}, serverRegion: {JsonSerializer.Serialize(member.Region)})";

    internal static (double? Value, bool Median) ZoneAverage(JsonElement rankings)
    {
        var best = rankings.GetNumberOrNull("bestPerformanceAverage");
        if (best is >= 0 and <= 100) return (best, false);
        var median = rankings.GetNumberOrNull("medianPerformanceAverage");
        return median is >= 0 and <= 100 ? (median, true) : (null, false);
    }

    internal static Performance Parse(JsonElement rankings, byte job)
    {
        double? best = null, jobBest = null;
        double total = 0;
        var samples = 0;
        string? bestJob = null;
        var currentSpec = Jobs.FirstOrDefault(j => j.ClassJobId == job).Spec;
        if (rankings.TryGetPath(out var ranks, "ranks") && ranks.ValueKind == JsonValueKind.Array)
            foreach (var rank in ranks.EnumerateArray())
            {
                if (rank.GetNumberOrNull("rankPercent") is not { } percent || !double.IsFinite(percent) || percent is < 0 or > 100) continue;
                var spec = rank.GetStringOrNull("spec");
                if (best is null || percent > best) { best = percent; bestJob = spec is null ? null : JobBySpec.GetValueOrDefault(spec); }
                if (spec != null && spec.Equals(currentSpec, StringComparison.OrdinalIgnoreCase))
                {
                    total += percent; samples++;
                    if (jobBest is null || percent > jobBest) jobBest = percent;
                }
            }
        var kills = rankings.GetNumberOrNull("totalKills");
        return new Performance(kills is >= 0 and <= int.MaxValue ? (int)kills : null, jobBest, best, bestJob,
            best.HasValue ? "" : "No public parses") { Average = samples > 0 ? total / samples : null, Samples = samples };
    }

}
