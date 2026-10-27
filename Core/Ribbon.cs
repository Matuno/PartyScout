using System.Numerics;

namespace PartyScout.Core;

public enum RibbonMode { Collapsed, Half, Open }
public enum ColourScheme { Grey, Teal, Adaptive }
public enum ListingEmphasis { Composition, Performance, Description }

internal sealed record RibbonPalette(Vector4 Background, Vector4 Panel, Vector4 Control, Vector4 Hover, Vector4 Border,
    Vector4 Text, Vector4 Muted, Vector4 Accent, Vector4 Active, Vector4 Good)
{
    internal static Vector4 Rgb(uint rgb) => new((rgb >> 16 & 255) / 255f, (rgb >> 8 & 255) / 255f, (rgb & 255) / 255f, 1);
    internal static RibbonPalette From(ColourScheme scheme, uint seed) => scheme switch
    {
        ColourScheme.Teal => new(Rgb(0x0B201F), Rgb(0x143433), Rgb(0x214B47), Rgb(0x29625B), Rgb(0x315851),
            Rgb(0xF0FAF8), Rgb(0xBBD7D2), Rgb(0x8EE7CF), Rgb(0x225951), Rgb(0xBFE8B5)),
        ColourScheme.Adaptive => Adaptive(seed),
        _ => new(Rgb(0x20242C), Rgb(0x27303A), Rgb(0x36424F), Rgb(0x455360), Rgb(0x46515C),
            Rgb(0xE8EDF0), Rgb(0xC1CBD0), Rgb(0xC0D3DA), Rgb(0x435864), Rgb(0xB5D2C0)),
    };

    private static RibbonPalette Adaptive(uint seed)
    {
        var rgb = Rgb(seed);
        var max = Math.Max(rgb.X, Math.Max(rgb.Y, rgb.Z));
        var min = Math.Min(rgb.X, Math.Min(rgb.Y, rgb.Z));
        var delta = max - min;
        var hue = delta == 0 ? 0 : max == rgb.X ? (rgb.Y - rgb.Z) / delta % 6 : max == rgb.Y ? (rgb.Z - rgb.X) / delta + 2 : (rgb.X - rgb.Y) / delta + 4;
        hue = (hue / 6 + 1) % 1;
        var light = (max + min) / 2;
        var sat = delta == 0 ? 0 : delta / (1 - Math.Abs(2 * light - 1));
        Vector4 Tone(float s, float l)
        {
            var chroma = (1 - Math.Abs(2 * l - 1)) * sat * s;
            var x = chroma * (1 - Math.Abs(hue * 6 % 2 - 1));
            var v = (int)(hue * 6) switch
            {
                0 => new Vector3(chroma, x, 0), 1 => new Vector3(x, chroma, 0),
                2 => new Vector3(0, chroma, x), 3 => new Vector3(0, x, chroma),
                4 => new Vector3(x, 0, chroma), _ => new Vector3(chroma, 0, x),
            };
            return new Vector4(v + new Vector3(l - chroma / 2), 1);
        }
        return new(Tone(.4f, .10f), Tone(.4f, .14f), Tone(.35f, .22f), Tone(.35f, .28f), Tone(.3f, .34f),
            Tone(.15f, .95f), Tone(.3f, .86f), Tone(.8f, .85f), Tone(.35f, .30f), Rgb(0xBFE8B5));
    }
}

internal sealed record InboxEntry(Match Match, DateTimeOffset At, bool Read = false);
internal sealed class AlertInbox
{
    internal const int Capacity = 32;
    private readonly List<InboxEntry> entries = [];
    internal IReadOnlyList<InboxEntry> Entries => entries;
    internal bool Unread => entries.Any(e => !e.Read);
    internal void Add(Match match, DateTimeOffset now)
    {
        var old = entries.FindIndex(e => e.Match.Listing.Id == match.Listing.Id);
        if (old >= 0 && entries[old].Match.Score >= match.Score) return;
        if (old >= 0) entries.RemoveAt(old);
        entries.Insert(0, new(match, now));
        if (entries.Count > Capacity) entries.RemoveAt(entries.Count - 1);
    }
    internal void ReadAll() { for (var i = 0; i < entries.Count; i++) entries[i] = entries[i] with { Read = true }; }
    internal void Remove(ulong id) => entries.RemoveAll(e => e.Match.Listing.Id == id);
    internal void Clear() => entries.Clear();
}

internal sealed record PhaseAverage(double? Average, int Coverage, int Total);
internal static class RosterSummary
{
    internal static PhaseAverage[] Averages(IEnumerable<MemberData?> members, int occupied)
    {
        var data = members.ToArray();
        var phases = Math.Max(1, data.Select(d => d?.Parses?.Length ?? 0).DefaultIfEmpty().Max());
        return Enumerable.Range(0, phases).Select(phase =>
        {
            var values = data.Where(d => d?.ParseError is null).Select(d => d?.Parses?.ElementAtOrDefault(phase)?.Average)
                .OfType<double>().Where(v => double.IsFinite(v) && v is >= 0 and <= 100).ToArray();
            return new PhaseAverage(values.Length == 0 ? null : values.Average(), values.Length, occupied);
        }).ToArray();
    }
    internal static bool FullClear(MemberData? data) =>
        data?.ProgressError is null && data?.Progress is { } progress && progress.Phase != "P1" && (progress.Cleared || progress.TotalClears > 0) ||
        data?.ParseError is null && data?.Parses is { Length: > 0 } parses && parses.All(p => !p.Failed && p.Kills > 0);
    internal static string Progress(MemberData? data)
    {
        if (data?.ProgressError is null && data?.Progress is { } progress &&
            (progress.Cleared || progress.TotalClears > 0 || progress.ProgPoint is not null))
        {
            var phase = progress.Phase is { } p ? p + " · " : "";
            return phase + (progress.Cleared || progress.TotalClears > 0 ? "Cleared" : progress.ProgPoint ?? "No public prog");
        }
        if (data?.ParseError is null && data?.Parses is { Length: > 0 } parts)
        {
            if (FullClear(data)) return "FFLogs clear";
            for (var i = parts.Length - 1; i >= 0; i--)
                if (!parts[i].Failed && parts[i].LowestWipe is { } wipe)
                    return $"FFLogs {(parts.Length > 1 ? $"P{i + 1} " : "")}{wipe:0.0}% left";
        }
        if (data?.ProgressError is not null) return "Unavailable";
        return data?.Progress is { } missing ? missing.ActivityHidden ? "Private" : missing.LookupFailed ? "Unavailable" : missing.NotFound ? "Not found" : "No public prog" : "—";
    }
}
