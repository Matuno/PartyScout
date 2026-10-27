using System.Net;
using System.Text.RegularExpressions;

namespace PartyScout.Core;

internal static class LodestoneRoster
{
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.Singleline;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex Pager = new("class=\"btn__pager__current\"\\s*>\\s*Page\\s+(?<page>\\d+)\\s+of\\s+(?<pages>\\d+)", Options, Timeout);
    private static readonly Regex Total = new("class=\"parts__total\"\\s*>\\s*(?<total>\\d+)\\s+Total", Options, Timeout);
    private static readonly Regex Identity = new("<p\\s+class=\"entry__name\">(?<name>[^<]+)</p>\\s*<p\\s+class=\"entry__world\">\\s*(?:<i\\b[^>]*>\\s*</i>\\s*)?(?<world>[^<]+)</p>", Options, Timeout);
    private static readonly Regex Members = new("<div\\s+class=\"entry__freecompany__center\">\\s*(?<body><p\\s+class=\"entry__name\">.*?</p>\\s*<p\\s+class=\"entry__world\">.*?</p>)", Options, Timeout);
    private static readonly Regex Characters = new("<div\\s+class=\"entry\">\\s*<a\\s+href=\"/lodestone/character/\\d+/\"\\s+class=\"entry__link\">(?<body>.*?)</a>\\s*(?:<a\\s+href=\"/lodestone/freecompany/(?<company>\\d{1,20})/\"[^>]*>.*?</a>\\s*)?</div>", Options, Timeout);

    internal static (int Pages, int Total) Pagination(string html, int page, int maxTotal)
    {
        var pager = Pager.Match(html); var total = Total.Match(html);
        if (!pager.Success || !total.Success || !int.TryParse(pager.Groups["page"].Value, out var current) || current != page ||
            !int.TryParse(pager.Groups["pages"].Value, out var pages) || pages is < 1 or > 20 ||
            !int.TryParse(total.Groups["total"].Value, out var count) || count < 0 || count > maxTotal)
            throw new InvalidDataException("The public roster page was incomplete or changed.");
        return (pages, count);
    }

    private static (string Name, string World) Person(string html)
    {
        var match = Identity.Match(html);
        if (!match.Success) throw new InvalidDataException("A public roster entry was incomplete.");
        var name = WebUtility.HtmlDecode(match.Groups["name"].Value).Trim();
        var world = WebUtility.HtmlDecode(match.Groups["world"].Value).Trim();
        var bracket = world.IndexOf(" [", StringComparison.Ordinal);
        if (bracket >= 0) world = world[..bracket];
        if (name.Length is < 1 or > 64 || world.Length is < 1 or > 64)
            throw new InvalidDataException("A public roster entry was invalid.");
        return (name, world);
    }

    internal static (string Name, string World)[] MemberPage(string html) =>
        Members.Matches(html).Select(match => Person(match.Groups["body"].Value)).ToArray();

    internal static (string Name, string World, ulong Company)[] CharacterPage(string html) =>
        Characters.Matches(html).Select(match =>
        {
            var person = Person(match.Groups["body"].Value);
            var company = ulong.TryParse(match.Groups["company"].Value, out var id) ? id : 0;
            return (person.Name, person.World, company);
        }).ToArray();
}
