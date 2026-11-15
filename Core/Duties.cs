namespace PartyScout.Core;

internal enum DutyCategory
{
    Ultimates, SavageRaids, ExtremeUnrealTrials, VariantCriterion, ChaoticAllianceRaids,
    NormalRaids, AllianceRaids, Trials, Dungeons, DeepDungeons, Other,
}

internal readonly record struct DutyChoice(uint Id, string Name, bool HighEnd, DutyCategory Category, uint ExpansionId, string Expansion);

internal static class DutyPicker
{
    internal static DutyCategory Category(uint contentType, string uiCategory, bool highEnd) => contentType switch
    {
        28 => DutyCategory.Ultimates,
        37 => DutyCategory.ChaoticAllianceRaids,
        30 => DutyCategory.VariantCriterion,
        21 => DutyCategory.DeepDungeons,
        2 => DutyCategory.Dungeons,
        4 when highEnd || uiCategory.StartsWith("High-end Trials", StringComparison.OrdinalIgnoreCase) => DutyCategory.ExtremeUnrealTrials,
        4 => DutyCategory.Trials,
        5 when uiCategory.StartsWith("Savage Raids", StringComparison.OrdinalIgnoreCase) => DutyCategory.SavageRaids,
        5 when uiCategory.StartsWith("Alliance Raids", StringComparison.OrdinalIgnoreCase) => DutyCategory.AllianceRaids,
        5 when uiCategory.StartsWith("Normal Raids", StringComparison.OrdinalIgnoreCase) || uiCategory.StartsWith("Raids (", StringComparison.OrdinalIgnoreCase) => DutyCategory.NormalRaids,
        _ => DutyCategory.Other,
    };

    // Palace of the Dead's first five floor ranges require only the base game but belong to Heavensward.
    internal static uint Expansion(uint contentType, uint content, uint required) =>
        contentType == 21 && content is >= 60001 and <= 60005 && required == 0 ? 1u : required;

    internal static string Label(DutyCategory category) => category switch
    {
        DutyCategory.Ultimates => "Ultimates",
        DutyCategory.SavageRaids => "Savage raids",
        DutyCategory.ExtremeUnrealTrials => "Extreme & Unreal trials",
        DutyCategory.VariantCriterion => "Variant & Criterion",
        DutyCategory.ChaoticAllianceRaids => "Chaotic Alliance raids",
        DutyCategory.NormalRaids => "Normal raids",
        DutyCategory.AllianceRaids => "Alliance raids",
        DutyCategory.Trials => "Trials",
        DutyCategory.Dungeons => "Dungeons",
        DutyCategory.DeepDungeons => "Deep Dungeons",
        _ => "Other",
    };

    internal static bool Matches(DutyChoice duty, string search) => search.Length == 0 ||
        duty.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
        Label(duty.Category).Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
        duty.Expansion.Contains(search, StringComparison.CurrentCultureIgnoreCase);
}
