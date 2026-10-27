using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using PartyScout.Core;

namespace PartyScout;

internal sealed partial class ScoutWindows
{
    private void Setup()
    {
        selectedProfile = Math.Clamp(selectedProfile, 0, C.Profiles.Count - 1);
        var height = ImGui.GetContentRegionAvail().Y;
        if (ImGui.BeginChild("Profiles", new Vector2(112 * Scale, height), false))
        {
            ImGui.TextDisabled("PROFILES");
            if (Icon(FontAwesomeIcon.Plus, "addProfile", "Add search profile", new Vector2(ImGui.GetFrameHeight())))
            {
                C.Profiles.Add(new SearchProfile { Name = $"Search {C.Profiles.Count + 1}" });
                selectedProfile = C.Profiles.Count - 1; dutySearch = ""; p.Post(p.Changed);
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(C.Profiles.Count == 1))
                if (Icon(FontAwesomeIcon.Trash, "removeProfile", "Remove selected profile", new Vector2(ImGui.GetFrameHeight())))
                { C.Profiles.RemoveAt(selectedProfile); selectedProfile = Math.Max(0, selectedProfile - 1); dutySearch = ""; p.Post(p.Changed); }
            for (var i = 0; i < C.Profiles.Count; i++)
            {
                var item = C.Profiles[i];
                ImGui.PushID(i);
                if (ImGui.Checkbox("##Enabled", ref item.Enabled)) p.Post(p.Changed);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(item.Enabled ? "Pause this profile" : "Enable this profile");
                ImGui.SameLine();
                if (ImGui.Selectable(item.Name + "##Profile", i == selectedProfile)) { selectedProfile = i; dutySearch = ""; }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(item.Name);
                ImGui.PopID();
            }
        }
        ImGui.EndChild(); ImGui.SameLine();
        if (ImGui.BeginChild("ProfileEditor", Vector2.Zero, false))
        {
            var profile = C.Profiles[selectedProfile];
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextWithHint("##ProfileName", "Profile name", ref profile.Name, 80)) p.Post(p.Save);
            if (ImGui.CollapsingHeader("Jobs", ImGuiTreeNodeFlags.DefaultOpen))
            {
                var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
                foreach (var role in Enum.GetValues<Role>())
                {
                    ImGui.TextDisabled(role switch { Role.Ranged => "Ranged", Role.Caster => "Caster", _ => role.ToString() });
                    ImGui.SameLine(60 * Scale);
                    var first = true;
                    foreach (var job in p.JobChoices.Where(j => j.Role == role && (j.Id >= 19 || profile.Jobs.Contains(j.Id))))
                    {
                        if (!first)
                        {
                            if (ImGui.GetItemRectMax().X + 44 * Scale <= right) ImGui.SameLine();
                            else ImGui.SetCursorPosX(60 * Scale);
                        }
                        first = false;
                        var selected = profile.Jobs.Contains(job.Id);
                        using var colour = ImRaii.PushColor(ImGuiCol.Button, selected ? palette.Active : palette.Control)
                            .Push(ImGuiCol.Text, selected ? Accent : palette.Text);
                        if (ImGui.SmallButton(job.Name + "##job" + job.Id))
                        { if (selected) profile.Jobs.Remove(job.Id); else profile.Jobs.Add(job.Id); p.Post(p.Changed); }
                    }
                }
                if (profile.Jobs.Count == 0) ImGui.TextColored(Gold, "Choose at least one job.");
            }
            ImGui.SetNextItemWidth(-1);
            ImGui.SetNextWindowSizeConstraints(new Vector2(360, 0) * Scale, new Vector2(520, 300) * Scale);
            var dutyLabel = profile.Duties.Count == 0 ? "All duties" : profile.Duties.Count == 1 ? p.DutyLabel(profile.Duties.First()) : $"{profile.Duties.Count} duties";
            if (ImGui.BeginCombo("##Duties", dutyLabel))
            {
                ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##DutySearch", "Search duties…", ref dutySearch, 150);
                if (profile.Duties.Count > 0 && ImGui.SmallButton("Clear selection")) { profile.Duties.Clear(); p.Post(p.Changed); }
                if (ImGui.BeginTabBar("DutyCategories"))
                {
                    if (ImGui.BeginTabItem("High-end")) { DutyChoices(profile, true); ImGui.EndTabItem(); }
                    if (ImGui.BeginTabItem("Other")) { DutyChoices(profile, false); ImGui.EndTabItem(); }
                    ImGui.EndTabBar();
                }
                ImGui.EndCombo();
            }
            if (ImGui.Checkbox("Require full duty size", ref profile.RequireFullDutySize)) p.Post(p.Changed);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Exclude listings with fewer total slots than the duty requires. Unknown sizes are not filtered.");
            if (ImGui.Checkbox("Regex", ref profile.Regex)) p.Post(p.Changed);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Case insensitive. Terms: include ALL, exclude ANY; separate with commas or newlines. Regex: one pattern per field.");
            if (ImGui.BeginTable("Terms", 2, ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextColumn(); ImGui.TextDisabled(profile.Regex ? "Include pattern" : "Include all");
                if (ImGui.InputTextMultiline("##Include", ref profile.Include, 2048, new Vector2(-1, 42 * Scale))) p.Post(p.Changed);
                ImGui.TableNextColumn(); ImGui.TextDisabled(profile.Regex ? "Exclude pattern" : "Exclude any");
                if (ImGui.InputTextMultiline("##Exclude", ref profile.Exclude, 2048, new Vector2(-1, 42 * Scale))) p.Post(p.Changed);
                ImGui.EndTable();
            }
            if (ImGui.CollapsingHeader("Weights & response · all profiles"))
            {
                ImGui.TextWrapped("Points per filled member. Parses never affect ranking.");
                foreach (var role in Enum.GetValues<Role>())
                {
                    ImGui.SetNextItemWidth(110 * Scale);
                    if (ImGui.SliderInt(RoleName(role), ref C.RoleWeights[(int)role], 0, 100)) p.Post(p.Changed);
                }
                if (ImGui.Checkbox("Better-match popup", ref C.Alerts)) p.Post(p.AlertsChanged);
                if (ImGui.Checkbox("Join if I do not respond", ref C.TimedJoin))
                    p.Post(() => { p.Changed(); if (C.TimedJoin && p.Active && !p.Automation) p.ToggleAutomation(); });
                if (C.TimedJoin)
                {
                    ImGui.SetNextItemWidth(110 * Scale);
                    if (ImGui.SliderInt("Seconds to respond", ref C.JoinDelay, 10, 300)) p.Post(p.Changed);
                }
                ImGui.TextWrapped("Switching leaves your current party. A target can fill before you join. Stop Auto cancels the remaining steps.");
            }
            if (p.FilterError is { } error) ImGui.TextWrapped(error);
            if (p.SaveError is { } saveError) ImGui.TextWrapped(saveError);
        }
        ImGui.EndChild();
    }
    private static string RoleName(Role role) => role switch { Role.Ranged => "Physical ranged", Role.Caster => "Magical ranged", _ => role.ToString() };
    private void DutyChoices(SearchProfile profile, bool highEnd)
    {
        if (ImGui.BeginChild("DutyChoices", new Vector2(0, 160 * Scale), false))
            foreach (var duty in p.DutyChoices.Where(d => d.HighEnd == highEnd && d.Name.Contains(dutySearch, StringComparison.CurrentCultureIgnoreCase)))
            {
                var selected = profile.Duties.Contains(duty.Id);
                if (ImGui.Checkbox(duty.Name + "##duty" + duty.Id, ref selected))
                { if (selected) profile.Duties.Add(duty.Id); else profile.Duties.Remove(duty.Id); p.Post(p.Changed); }
            }
        ImGui.EndChild();
    }
}
