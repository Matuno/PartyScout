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
            if (ImGui.CollapsingHeader("Jobs", ImGuiTreeNodeFlags.DefaultOpen)) JobPicker(profile);
            ImGui.SetNextItemWidth(-1);
            ImGui.SetNextWindowSizeConstraints(new Vector2(360, 0) * Scale, new Vector2(520, 300) * Scale);
            var dutyLabel = profile.Duties.Count == 0 ? "All duties" : profile.Duties.Count == 1 ? p.DutyLabel(profile.Duties.First()) : $"{profile.Duties.Count} duties";
            if (ImGui.BeginCombo("##Duties", dutyLabel))
            {
                ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##DutySearch", "Search duties…", ref dutySearch, 150);
                using (ImRaii.Disabled(profile.Duties.Count == 0))
                    if (ImGui.SmallButton("Clear selection")) { profile.Duties.Clear(); p.Post(p.Changed); }
                DutyPickerContents(dutySearch, profile.Duties, true, id =>
                { if (!profile.Duties.Add(id)) profile.Duties.Remove(id); p.Post(p.Changed); });
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
                ImGui.TextWrapped(p.UsesJobPreferences ? "Fill and job preference rank first; role points break ties. Parses never affect ranking." : "Points per filled member. Parses never affect ranking.");
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
    private void JobPicker(SearchProfile profile)
    {
        var labelWidth = Math.Max(40 * Scale, ImGui.CalcTextSize("WHM").X + 2 * ImGui.GetStyle().FramePadding.X);
        if (ImGui.BeginTable("ProfileJobs", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, Math.Max(76 * Scale, ImGui.CalcTextSize("Ranged").X + 24 * Scale));
            ImGui.TableSetupColumn("Jobs", ImGuiTableColumnFlags.WidthStretch);
            foreach (var role in Enum.GetValues<Role>())
            {
                var choices = p.JobChoices.Where(job => job.Role == role && Jobs.InProfilePicker(job.Id, profile.Jobs.Contains(job.Id))).ToArray();
                var enabled = choices.Where(job => profile.Jobs.Contains(job.Id)).ToArray();
                ImGui.PushID((int)role); ImGui.TableNextRow(); ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled(role switch { Role.Ranged => "Ranged", Role.Caster => "Caster", _ => role.ToString() });
                ImGui.SameLine(0, 2 * Scale);
                using (ImRaii.Disabled(enabled.Length == 0))
                using (ImRaii.PushColor(ImGuiCol.Text, enabled.Length > 0 && enabled.All(job => profile.PreferredJobs.Contains(job.Id)) ? Gold : Muted))
                    if (Icon(FontAwesomeIcon.Star, "preferRole", "Toggle preference for selected jobs in this role", new Vector2(20 * Scale, ImGui.GetFrameHeight())))
                    { profile.ToggleRolePreference(role); p.Post(p.Changed); }
                ImGui.TableNextColumn();
                var starWidth = ImGui.GetFrameHeight();
                var jobWidth = labelWidth + starWidth + 2 * Scale;
                var columns = Math.Clamp((int)((ImGui.GetContentRegionAvail().X + 6 * Scale) / (jobWidth + 6 * Scale)), 1, 4);
                for (var i = 0; i < choices.Length; i++)
                {
                    var job = choices[i];
                    if (i % columns != 0) ImGui.SameLine(0, 6 * Scale);
                    ImGui.PushID((int)job.Id); ImGui.BeginGroup();
                    using (ImRaii.PushColor(ImGuiCol.Button, profile.Jobs.Contains(job.Id) ? palette.Active : palette.Control)
                        .Push(ImGuiCol.Text, profile.Jobs.Contains(job.Id) ? palette.Text : Muted))
                        if (ImGui.Button(job.Name + "##play", new Vector2(labelWidth, ImGui.GetFrameHeight())))
                        { profile.ToggleJob(job.Id); p.Post(p.Changed); }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(profile.Jobs.Contains(job.Id) ? "Remove " + job.Name : "I can play " + job.Name);
                    ImGui.SameLine(0, 2 * Scale);
                    using (ImRaii.PushColor(ImGuiCol.Text, profile.PreferredJobs.Contains(job.Id) ? Gold : Muted))
                        if (Icon(FontAwesomeIcon.Star, "preferJob", profile.PreferredJobs.Contains(job.Id) ? "Preferred · click to accept without preference" : "Prefer " + job.Name, new Vector2(starWidth)))
                        { profile.TogglePreference(job.Id); p.Post(p.Changed); }
                    ImGui.EndGroup(); ImGui.PopID();
                }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        ImGui.TextColored(Gold, "Starred = preferred");
        if (profile.Jobs.Count == 0) ImGui.TextColored(Gold, "Choose at least one job.");
        ImGui.TextDisabled("Favour preferred jobs"); ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##PreferenceFill", ref profile.PreferredFillAllowance, "At equal fill\0Allow 1 fewer member\0Allow 2 fewer members\0")) p.Post(p.Changed);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Preferred jobs can outweigh this many fewer filled slots. Multiple preferred vacancies count once. Without stars on any enabled profile, ranking uses role points.");
    }
    private static string RoleName(Role role) => role switch { Role.Ranged => "Physical ranged", Role.Caster => "Magical ranged", _ => role.ToString() };
    private void DutyPickerContents(string search, IReadOnlySet<uint> selected, bool multiple, Action<uint> choose)
    {
        void Row(uint id, string name)
        {
            var isSelected = selected.Contains(id);
            if (multiple ? ImGui.Checkbox(name + "##duty" + id, ref isSelected) : ImGui.Selectable(name + "##duty" + id, isSelected))
                choose(id);
        }

        ImGui.TextDisabled("SELECTED");
        if (ImGui.BeginChild("SelectedDuties", new Vector2(0, 2 * ImGui.GetFrameHeightWithSpacing()), false))
        {
            if (selected.Count == 0) ImGui.TextDisabled(multiple ? "All duties" : "No duty selected");
            else
            {
                ImGui.PushID("selected");
                foreach (var id in selected.OrderBy(p.DutyLabel, StringComparer.CurrentCultureIgnoreCase).ToArray()) Row(id, p.DutyLabel(id));
                ImGui.PopID();
            }
        }
        ImGui.EndChild(); ImGui.Separator();

        if (ImGui.BeginChild("DutyChoices", new Vector2(0, 140 * Scale), false))
        {
            var query = search.Trim();
            var searching = query.Length > 0;
            var found = false;
            foreach (var group in p.DutyGroups)
            {
                if (!group.Any(duty => DutyPicker.Matches(duty, query))) continue;
                found = true; ImGui.PushID((int)group.Key);
                var label = DutyPicker.Label(group.Key);
                var open = true;
                if (searching) ImGui.TextColored(Accent, label);
                else open = ImGui.CollapsingHeader(label);
                if (open)
                    foreach (var expansion in group.Where(duty => DutyPicker.Matches(duty, query)).GroupBy(duty => (duty.ExpansionId, duty.Expansion)))
                    {
                        ImGui.PushID(expansion.Key.ExpansionId.ToString());
                        var expanded = true;
                        if (searching) { ImGui.TextDisabled(expansion.Key.Expansion); ImGui.Indent(12 * Scale); }
                        else expanded = ImGui.TreeNode(expansion.Key.Expansion);
                        if (expanded)
                        {
                            foreach (var duty in expansion) Row(duty.Id, duty.Name);
                            if (searching) ImGui.Unindent(12 * Scale); else ImGui.TreePop();
                        }
                        ImGui.PopID();
                    }
                ImGui.PopID();
            }
            if (!found) ImGui.TextDisabled("No matching duties.");
        }
        ImGui.EndChild();
    }
}
