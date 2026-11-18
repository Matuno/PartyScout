using Dalamud;
using Dalamud.Interface;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using PartyScout.Core;

namespace PartyScout;

public sealed partial class Plugin
{
    private IFontHandle? bodyFont, headingFont, iconFont;
    private float fontRasterScale = 1;
    private float? failedFontScale;
    internal IFontHandle BodyFont => bodyFont is { Available: true } font ? font : pi.UiBuilder.DefaultFontHandle;
    internal IFontHandle HeadingFont => BodyFontReady && headingFont is { Available: true } font ? font : BodyFont;
    internal IFontHandle IconFont => BodyFontReady && iconFont is { Available: true } font ? font : pi.UiBuilder.IconFontHandle;
    internal bool BodyFontReady => bodyFont is { Available: true };
    internal bool FontsReady => BodyFontReady && headingFont is { Available: true } && iconFont is { Available: true };
    internal bool FontsFailed => failedFontScale is not null || bodyFont?.LoadException is not null || headingFont?.LoadException is not null || iconFont?.LoadException is not null;
    internal float FontRasterScale => fontRasterScale;
    internal bool FontScaleNeedsRefresh => Math.Abs(Settings.Scale - fontRasterScale) >= .001f &&
        (failedFontScale is null || Math.Abs(Settings.Scale - failedFontScale.Value) >= .001f);

    internal void RefreshFonts()
    {
        var scale = Settings.Scale;
        if (bodyFont is not null && Math.Abs(scale - fontRasterScale) < .001f) return;
        var atlas = pi.UiBuilder.FontAtlas;
        IFontHandle? nextBody = null, nextHeading = null, nextIcon = null;
        using (atlas.SuppressAutoRebuild())
        {
            try
            {
                IFontHandle TextFont(DalamudAsset asset) => atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
                {
                    var size = UiBuilder.DefaultFontSizePx * scale;
                    var font = tk.AddDalamudAssetFont(asset, new SafeFontConfig { SizePx = size, GlyphRanges = TypographyScale.TextRanges });
                    tk.AddGameGlyphs(new GameFontStyle(GameFontFamily.Axis, size), TypographyScale.GameFallbackRanges, font);
                    tk.AttachExtraGlyphsForDalamudLanguage(new SafeFontConfig { SizePx = size, MergeFont = font });
                    tk.Font = font;
                }));
                nextBody = TextFont(DalamudAsset.NotoSansCjkRegular);
                nextHeading = TextFont(DalamudAsset.NotoSansCjkMedium);
                nextIcon = atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
                    tk.AddFontAwesomeIconFont(new SafeFontConfig { SizePx = UiBuilder.DefaultFontSizePx * scale })));
            }
            catch (Exception ex)
            {
                nextBody?.Dispose(); nextHeading?.Dispose(); nextIcon?.Dispose();
                failedFontScale = scale;
                log.Error(ex, "Party Scout could not prepare its UI fonts.");
                return;
            }
            var oldBody = bodyFont; var oldHeading = headingFont; var oldIcon = iconFont;
            bodyFont = nextBody; headingFont = nextHeading; iconFont = nextIcon;
            fontRasterScale = scale;
            failedFontScale = null;
            oldBody?.Dispose(); oldHeading?.Dispose(); oldIcon?.Dispose();
        }
    }
}
