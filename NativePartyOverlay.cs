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
        var size = new Vector2(360, Math.Clamp(86 + detail.Members.Length * 29, 150, 420)) * Scale;
        if (!AnchorPlacement.TryPlace(window.Position + viewport.Pos, window.Size, viewport.WorkPos, viewport.WorkSize,
            size, 8 * Scale, out var position)) return;
        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        using var background = ImRaii.PushColor(ImGuiCol.WindowBg, palette.Panel);
        if (ImGui.Begin("Party members###PartyScoutNativeParty", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            ImGui.SetWindowFontScale(1);
            ImGui.TextColored(Accent, "PARTY MEMBERS");
            if (detail.Duty != 0) OneLine(p.DutyLabel(detail.Duty), Muted);
            if (!p.HasTomestone || !p.HasFFLogs) ImGui.TextDisabled("Some player data is not configured.");
            if (ImGui.BeginChild("NativeMembers", Vector2.Zero, false))
            {
                if (ImGui.BeginTable("NativeRoster", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
                {
                    ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthFixed, 26 * Scale);
                    ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch, 1.4f);
                    ImGui.TableSetupColumn("Avg", ImGuiTableColumnFlags.WidthFixed, 58 * Scale);
                    ImGui.TableSetupColumn("Progress", ImGuiTableColumnFlags.WidthStretch, 1f);
                    ImGui.TableHeadersRow();
                    foreach (var member in detail.Members.OrderBy(m => m.Slot < 0 ? int.MaxValue : m.Slot))
                    {
                        ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0);
                        Slot(member.Job, 0, true, member.Job > 0 ? p.JobNames([member.Job]) : "Job unavailable");
                        ImGui.TableSetColumnIndex(1);
                        OneLine(member.Resolved ? member.Name : member.State == NameState.Pending ? "Resolving…" : "Unavailable", palette.Text);
                        if (member.Resolved && ImGui.IsItemHovered()) ImGui.SetTooltip(member.Name + " @ " + member.World);
                        var data = p.DataFor(member, detail.Duty);
                        ImGui.TableSetColumnIndex(2);
                        var average = RosterSummary.PlayerAverage(data);
                        OneLine(average, Muted);
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip(average + "\nThis player's average for the listed duty and job. Z is the latest public FFLogs zone average.");
                        ImGui.TableSetColumnIndex(3);
                        var progress = RosterSummary.Progress(data);
                        OneLine(progress, RosterSummary.FullClear(data) ? palette.Good : Muted);
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip(progress);
                    }
                    ImGui.EndTable();
                }
            }
            ImGui.EndChild();
        }
        ImGui.End();
    }
}
