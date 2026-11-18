namespace PartyScout.Core;

internal static class TypographyScale
{
    internal static readonly ushort[] TextRanges =
        [0x0020, 0x024f, 0x0370, 0x052f, 0x1e00, 0x1eff, 0x2000, 0x206f, 0x20a0, 0x20cf, 0x2190, 0x21ff, 0];
    // Game glyphs must not overwrite the vector glyphs in TextRanges.
    internal static readonly ushort[] GameFallbackRanges =
        [0x0250, 0x036f, 0x0530, 0x1dff, 0x1f00, 0x1fff, 0x2070, 0x209f, 0x20d0, 0x218f, 0x2200, 0xffff, 0];

    internal static float WindowScale(float requested, float rasterized, bool ready) =>
        requested / (ready ? rasterized : 1);
}
