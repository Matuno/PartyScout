namespace PartyScout.Core;

internal static class JoinPolicy
{
    internal static bool OwnsConfirmation(ulong target, ulong viewed, uint expectedAddon, uint actualAddon) =>
        target != 0 && target == viewed && expectedAddon != 0 && expectedAddon == actualAddon;
}
