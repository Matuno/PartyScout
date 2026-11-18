using System.Numerics;

namespace PartyScout.Core;

public static class AnchorPlacement
{
    internal static bool FinderHovered(Vector2 mouse, Vector2 finder, Vector2 size, Vector2 detail, Vector2 detailSize) =>
        Contains(finder, size, mouse) && !Contains(detail, detailSize, mouse);

    private static bool Contains(Vector2 position, Vector2 size, Vector2 point) =>
        point.X >= position.X && point.X < position.X + size.X &&
        point.Y >= position.Y && point.Y < position.Y + size.Y;

    public static Vector2 KeepTopLeft(Vector2 position, Vector2 size, Vector2 viewport, Vector2 viewportSize) =>
        Vector2.Clamp(position, viewport, Vector2.Max(viewport, viewport + viewportSize - size));

    internal static Vector2 CompanionPanel(Vector2 glyph, float side, Vector2 panel, Vector2 view, Vector2 room)
    {
        var rightAligned = glyph.X + side / 2 > view.X + room.X / 2;
        var roomBelow = view.Y + room.Y - glyph.Y;
        var roomAbove = glyph.Y + side - view.Y;
        var y = roomBelow >= panel.Y || roomBelow >= roomAbove ? glyph.Y : glyph.Y + side - panel.Y;
        return KeepTopLeft(new(rightAligned ? glyph.X + side - panel.X : glyph.X, y), panel, view, room);
    }

    internal static (float X, float Width, float ContentX, float FullWidth) BannerStrip(float x, float side, float left, float right, float gap, float desired, float progress)
    {
        var extendsLeft = x - left > right - (x + side);
        var edge = extendsLeft ? x - gap : x + side + gap;
        var full = Math.Clamp(extendsLeft ? edge - left : right - edge, 0, desired);
        var width = MathF.Floor(full * Math.Clamp(progress, 0, 1));
        return extendsLeft ? (edge - width, width, edge - width, full) : (edge, width, edge + width - full, full);
    }

    public static bool TryPlace(Vector2 finder, Vector2 finderSize, Vector2 viewport, Vector2 viewportSize,
        Vector2 buttonSize, float gap, out Vector2 position)
    {
        position = default;
        if (viewportSize.X < buttonSize.X || viewportSize.Y < buttonSize.Y) return false;
        var end = finder + finderSize;
        Vector2[] candidates = [new(end.X + gap, finder.Y), new(finder.X - buttonSize.X - gap, finder.Y),
            new(finder.X, end.Y + gap), new(finder.X, finder.Y - buttonSize.Y - gap)];
        foreach (var candidate in candidates)
        {
            var p = Vector2.Clamp(candidate, viewport, viewport + viewportSize - buttonSize);
            if (p.X >= end.X + gap || p.X + buttonSize.X <= finder.X - gap ||
                p.Y >= end.Y + gap || p.Y + buttonSize.Y <= finder.Y - gap)
            { position = p; return true; }
        }
        return false;
    }
}
