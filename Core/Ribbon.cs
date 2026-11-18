namespace PartyScout.Core;

// Half is retained for reading settings written by the three-view interface.
public enum RibbonMode { Collapsed, Half, Open }
public enum ColourScheme { Grey, Teal, Adaptive }
public enum ListingEmphasis { Composition, Performance, Description }
public enum InterfaceDesign { Ribbon, Navigator, Ledger, Focus, Dashboard, Compare, SearchDesk, Spotlight, Board, Compass, Material }

internal static class CompanionLayout
{
    internal const float ContactsShare = .3f;

    internal static int FitRows(int count, float available, float header, float row, float gap) =>
        Math.Clamp((int)MathF.Floor((available - header) / (row + gap)), 0, count);

    internal static float ContactBodyHeight(int people, int parties, float available, float header, float row, float gap)
    {
        if (people == 0) return 0;
        var height = Math.Min(people * row + (parties - 1) * gap, available * ContactsShare - header - gap);
        return height >= row ? height : 0;
    }
}

internal static class FinderVisibility
{
    internal static bool HideFinder(bool active, bool show) => active && !show;
    internal static bool HideDetail(bool active, bool show, bool background, bool joining, bool selected = false) =>
        active && (!(show || selected) || background || joining);
    internal static bool ShowDetailOverlay(bool active, bool show, bool joining, bool background, ulong visible, ulong manual, bool selected = false) =>
        visible != 0 && visible == manual && (!active || show || selected) && !joining && !background;
}

internal static class RosterSummary
{
    internal static string PlayerAverage(MemberData? data)
    {
        if (data?.ParseError is not null || data?.Parses is not { Length: > 0 } parts) return "—";
        return string.Join(" / ", parts.Select((part, i) =>
        {
            var zoneOnly = parts.Length == 1 && part.Average is null && part.LatestZoneAverage.HasValue;
            var value = part.Failed ? null : zoneOnly ? part.LatestZoneAverage : part.Average;
            return (parts.Length > 1 ? $"P{i + 1} " : zoneOnly ? "Z " : "") + (value?.ToString("0.0") ?? "—");
        }));
    }
    internal static bool FullClear(MemberData? data) =>
        data?.ProgressError is null && data?.Progress is { } progress && progress.Phase != "P1" && (progress.Cleared || progress.TotalClears > 0) ||
        data?.ParseError is null && data?.Parses is { Length: > 0 } parses && parses.All(p => !p.Failed && p.Kills > 0);
    internal static string Progress(MemberData? data)
    {
        var progress = data?.ProgressError is null ? data?.Progress : null;
        if (FullClear(data) && !(progress is { Phase: not "P1" } && (progress.Cleared || progress.TotalClears > 0)))
            return "FFLogs clear";
        if (progress is not null && TomestoneData.HasData(progress))
        {
            var phase = progress.Phase is { } p ? p + " · " : "";
            return phase + (progress.Cleared || progress.TotalClears > 0 ? "Cleared" :
                !string.IsNullOrWhiteSpace(progress.ProgPoint) ? progress.ProgPoint : $"{progress.BestPercent:0.##}%");
        }
        if (data?.ParseError is null && data?.Parses is { Length: > 0 } parts)
        {
            for (var i = parts.Length - 1; i >= 0; i--)
                if (!parts[i].Failed && parts[i].LowestWipe is { } wipe)
                    return $"FFLogs {(parts.Length > 1 ? $"P{i + 1} " : "")}{wipe:0.0}% left";
        }
        if (data?.ProgressError is not null) return "Unavailable";
        return data?.Progress is { } missing ? missing.ActivityHidden ? "Private" : missing.LookupFailed ? "Unavailable" : missing.NotFound ? "Not found" : "No public prog" : "—";
    }
}
