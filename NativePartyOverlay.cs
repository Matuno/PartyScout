using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using PartyScout.Core;

namespace PartyScout;

internal sealed partial class ScoutWindows
{
    private void NativePartyOverlay()
    {
        if (p.View.Detail is not { } window || p.NativeParty is not { } detail) return;
        var viewport = ImGui.GetMainViewport();
        var rows = Math.Max(Inspection.RosterSlots(detail), detail.Members.Length);
        var size = new Vector2(360, Math.Clamp(86 + rows * 29, 150, 420)) * Scale;
        if (!AnchorPlacement.TryPlace(window.Position + viewport.Pos, window.Size, viewport.WorkPos, viewport.WorkSize,
            size, 8 * Scale, out var position)) return;
        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        using var background = ImRaii.PushColor(ImGuiCol.WindowBg, palette.Panel);
        if (ImGui.Begin("Party members###PartyScoutNativeParty", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            ImGui.SetWindowFontScale(C.Scale);
            Logo(22 * Scale); ImGui.SameLine();
            ImGui.AlignTextToFramePadding(); SectionTitle("Party members");
            if (detail.Duty != 0) OneLine(p.DutyLabel(detail.Duty), Muted);
            if (!p.HasTomestone || !p.HasFFLogs) ImGui.TextDisabled("Some player data is not configured.");
            if (ImGui.BeginChild("NativeMembers", Vector2.Zero, false))
            {
                Roster(detail);
            }
            ImGui.EndChild();
        }
        ImGui.End();
    }
}
