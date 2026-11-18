using System.Globalization;

namespace PartyScout.Core;

internal static class TextFit
{
    internal static string Ellipsize(string text, float width, Func<string, float> measure)
    {
        if (measure(text) <= width) return text;
        const string ellipsis = "…";
        if (measure(ellipsis) > width) return "";
        var boundaries = StringInfo.ParseCombiningCharacters(text);
        var low = 0; var high = boundaries.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            var end = mid == boundaries.Length ? text.Length : boundaries[mid];
            if (measure(text[..end] + ellipsis) <= width) low = mid; else high = mid - 1;
        }
        return text[..(low == boundaries.Length ? text.Length : boundaries[low])] + ellipsis;
    }
}
