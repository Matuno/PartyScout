using System.Numerics;

namespace PartyScout.Core;

public static class AnchorPlacement
{
    public static Vector2 KeepTopLeft(Vector2 position, Vector2 size, Vector2 viewport, Vector2 viewportSize) =>
        Vector2.Clamp(position, viewport, Vector2.Max(viewport, viewport + viewportSize - size));

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
