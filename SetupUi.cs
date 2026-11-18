using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using DalamudUi;
using PartyScout.Core;

namespace PartyScout;

internal sealed partial class ScoutWindows
{
    private bool materialJobsExpanded = true, materialFiltersExpanded = true, materialRecruitmentExpanded;
    private bool creatingProfile, showingProfileFilters;
    private string newGroupName = "";

    private void Setup()
    {
        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        if (ScoutButton("+ New search", small: true, variant: MaterialButtonVariant.Contained))
        {
            OpenProfileEditor(new SearchProfile { Name = $"Search {C.Profiles.Count + 1}" });
            creatingProfile = true;
        }
        var groupWidth = ImGui.CalcTextSize("+ New group").X + 2 * Theme.ButtonPaddingSmall * Scale;
        ImGui.SameLine();
        ImGui.SetCursorPosX(right - groupWidth);
        if (ScoutButton("+ New group", new Vector2(groupWidth, IconSide), small: true))
        {
            newGroupName = "";
            OpenCompanionPage(CompanionView.NewGroup);
        }
        using (ImRaii.Disabled(C.Groups.All(group => group.Enabled)))
            if (ScoutButton("Enable all groups", small: true)) { C.SetGroupsEnabled(true); p.Post(p.Changed); }
        ImGui.SameLine();
        using (ImRaii.Disabled(C.Groups.All(group => !group.Enabled)))
            if (ScoutButton("Pause all groups", small: true)) { C.SetGroupsEnabled(false); p.Post(p.Changed); }
        if (p.SaveError is { } error) OneLine(error, Gold);

        foreach (var group in C.Groups) SearchGroupCard(group);
        if (C.Profiles.Any(profile => profile.GroupId == Guid.Empty)) SearchGroupCard(null);
    }

    private void SearchGroupCard(SearchGroup? group)
    {
        var id = group?.Id ?? Guid.Empty;
        Material.Paper(id.ToString(), () =>
        {
            var inset = 8 * Scale;
            ImGui.Indent(inset);
            var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - inset;
            ImGui.AlignTextToFramePadding();
            using (p.HeadingFont.Push())
                OneLine((group?.Name ?? "Ungrouped").ToUpperInvariant(), group?.Enabled == false ? Muted : Accent,
                    inset + (group is null ? 0 : 50 * Scale));
            if (group is not null)
            {
                ImGui.SameLine();
                ImGui.SetCursorPosX(right - 42 * Scale);
                if (PreferenceSwitch("##group", ref group.Enabled)) p.Post(p.Changed);
                ScoutTooltip("groupToggle", "Pause or resume this group. Individual search switches are kept.");
            }
            for (var i = 0; i < C.Profiles.Count; i++)
            {
                var profile = C.Profiles[i];
                if (profile.GroupId != id) continue;
                ImGui.PushID(i);
                SearchProfileRow(profile, group?.Enabled != false, right, inset);
                ImGui.PopID();
            }
            ImGui.Dummy(new Vector2(0, 4 * Scale));
            ImGui.Unindent(inset);
        }, elevation: 0);
    }

    private void SearchProfileRow(SearchProfile profile, bool groupEnabled, float right, float inset)
    {
        using (ImRaii.Disabled(!groupEnabled || profile.Duty == 0))
            if (PreferenceSwitch("##enabled", ref profile.Enabled)) p.Post(p.Changed);
        ScoutTooltip("enabledTooltip", profile.Enabled ? "Pause this search" : "Enable this search");
        ImGui.SameLine(0, 4 * Scale);
        var nameX = ImGui.GetCursorPosX();
        var actionGap = 4 * Scale;
        ImGui.AlignTextToFramePadding();
        using (p.HeadingFont.Push()) OneLine(profile.Name, groupEnabled ? Theme.Text : Muted, inset + 2 * (IconSide + actionGap));
        ImGui.SameLine();
        ImGui.SetCursorPosX(right - 2 * IconSide - actionGap);
        if (Icon(FontAwesomeIcon.PencilAlt, "edit", "Edit search")) OpenProfileEditor(profile);
        ImGui.SameLine(0, actionGap);
        using (ImRaii.Disabled(C.Profiles.Count <= 1))
            if (Icon(FontAwesomeIcon.Trash, "delete", C.Profiles.Count <= 1 ? "Keep at least one search" : "Delete search"))
            { profileToDelete = profile; OpenCompanionPage(CompanionView.DeleteSearch); }

        ImGui.SetCursorPosX(nameX);
        var jobs = profile.Jobs.Count == 0 ? "No jobs" : p.JobNames(p.JobChoices.Where(job => profile.Jobs.Contains(job.Id)).Select(job => job.Id));
        OneLine(profile.Duty == 0 ? "Choose a duty" : $"{p.DutyLabel(profile.Duty)} · {jobs}", profile.Duty == 0 ? Gold : Muted, inset);
        if (p.FilterErrorFor(profile) is { } error)
        {
            ImGui.SetCursorPosX(nameX);
            OneLine(error, Gold, inset);
        }
    }

    private void NewGroupPage()
    {
        PageHeading("New group");
        ImGui.SetNextItemWidth(-1); ScoutTextField("##GroupName", "Group name", ref newGroupName, 80);
        var name = newGroupName.Trim();
        var duplicate = C.Groups.Any(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase));
        if (duplicate) SecondaryText("A group already has this name.");
        if (ScoutButton("Cancel")) BackToCompanion();
        ImGui.SameLine();
        using (ImRaii.Disabled(name.Length == 0 || duplicate))
            if (ScoutButton("Create group", variant: MaterialButtonVariant.Contained))
            { C.Groups.Add(new SearchGroup { Name = name }); p.Post(p.Changed); BackToCompanion(); }
    }

    private void DeleteSearchPage()
    {
        PageHeading("Delete search");
        if (profileToDelete is not { } target) return;
        ImGui.TextWrapped($"Remove “{target.Name}” from your searches?");
        if (ScoutButton("Keep search", variant: MaterialButtonVariant.Contained)) { profileToDelete = null; BackToCompanion(); return; }
        ImGui.SameLine();
        using (ImRaii.Disabled(C.Profiles.Count <= 1 || !C.Profiles.Contains(target)))
        {
            if (ScoutButton("Delete search") && C.RemoveSearch(target))
            {
                if (ReferenceEquals(editingProfile, target)) editingProfile = null;
                profileToDelete = null;
                p.Post(p.Changed);
                BackToCompanion();
            }
        }
    }

    private void OpenProfileEditor(SearchProfile profile)
    {
        editingProfile = profile; focusProfileEditor = true;
        creatingProfile = showingProfileFilters = false;
        dutySearch = ""; materialJobsExpanded = true;
        materialFiltersExpanded = profile.Include.Length > 0 || profile.Exclude.Length > 0 || profile.Regex;
        materialRecruitmentExpanded = profile.Objectives != PartyObjectives.Any || profile.ClearRequirements != ClearRequirements.Any;
        OpenCompanionPage(CompanionView.EditSearch);
    }

    private void EditSearchPage()
    {
        if (editingProfile is not { } profile || !creatingProfile && !C.Profiles.Contains(profile)) { BackToCompanion(); return; }
        PageHeading(showingProfileFilters ? "Filters" : creatingProfile ? "New search" : "Edit search",
            showingProfileFilters ? () => { showingProfileFilters = false; focusProfileEditor = true; } : null);
        if (companionView != CompanionView.EditSearch) return;
        var resetScroll = focusProfileEditor;
        focusProfileEditor = false;
        ProfileEditor(profile, resetScroll);
    }

    private void ChooseDutyPage()
    {
        PageHeading("Choose duty");
        if (companionView != CompanionView.ChooseDuty || editingProfile is not { } profile) return;
        ImGui.SetNextItemWidth(-1); ScoutTextField("##DutySearch", "Search duties…", ref dutySearch, 150);
        DutyPickerContents(dutySearch, profile.Duty, id =>
        {
            if (profile.Duty != id) { profile.Duty = id; ProfileChanged(); }
            BackToCompanion();
        });
    }

    private void ProfileChanged(bool criteria = true)
    {
        if (!creatingProfile) p.Post(criteria ? p.Changed : p.Save);
    }

    private void ProfileEditor(SearchProfile profile, bool resetScroll)
    {
        var fontSize = ImGui.GetFontSize();
        var footerHeight = IconSide + 2 * ImGui.GetStyle().ItemSpacing.Y + 2 * Scale;
        if (ImGui.BeginChild("ProfileEditor", new Vector2(0, -footerHeight), false))
        {
            MatchParentFontSize(fontSize);
            if (resetScroll) ImGui.SetScrollY(0);
            if (showingProfileFilters) DrawProfileFilters(profile);
            else DrawProfileIdentity(profile);
        }
        ImGui.EndChild();
        ScoutDivider();
        var done = creatingProfile ? "Create search" : "Done";
        var filter = showingProfileFilters ? "Search" : "Filters";
        var doneWidth = ImGui.CalcTextSize(done).X + 2 * Theme.ButtonPaddingSmall * Scale;
        var filterWidth = ImGui.CalcTextSize(filter).X + 2 * Theme.ButtonPaddingSmall * Scale;
        var cancelWidth = ImGui.CalcTextSize("Cancel").X + 2 * Theme.ButtonPaddingSmall * Scale;
        if (!creatingProfile)
        {
            ImGui.AlignTextToFramePadding();
            OneLine("Auto-save · Alerts paused while editing", Muted, doneWidth + filterWidth + 2 * ImGui.GetStyle().ItemSpacing.X);
            ImGui.SameLine();
        }
        if (ScoutButton(filter, new Vector2(filterWidth, IconSide), small: true))
        {
            showingProfileFilters = !showingProfileFilters;
            focusProfileEditor = true;
        }
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - doneWidth -
            (creatingProfile ? cancelWidth + ImGui.GetStyle().ItemSpacing.X : 0));
        if (creatingProfile)
        {
            if (ScoutButton("Cancel", new Vector2(cancelWidth, IconSide), small: true)) BackToCompanion();
            ImGui.SameLine();
        }
        using (ImRaii.Disabled(creatingProfile && profile.Duty == 0))
            if (ScoutButton(done, new Vector2(doneWidth, IconSide), small: true,
                variant: creatingProfile ? MaterialButtonVariant.Contained : MaterialButtonVariant.Text))
            {
                if (creatingProfile) { C.Profiles.Add(profile); p.Post(p.Changed); }
                BackToCompanion();
            }
    }

    private void DrawProfileIdentity(SearchProfile profile)
    {
        if (ImGui.BeginTable("ProfileIdentity", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 115 * Scale);
            ImGui.TableNextColumn(); ImGui.SetNextItemWidth(-1);
            if (ScoutTextField("##ProfileName", "Search name", ref profile.Name, 80)) ProfileChanged(criteria: false);
            ImGui.TableNextColumn();
            if (PreferenceSwitch("Enabled", ref profile.Enabled)) ProfileChanged();
            ImGui.EndTable();
        }
        var group = C.GroupFor(profile.GroupId);
        ImGui.AlignTextToFramePadding(); FieldLabel("Group"); ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##SearchGroup", group?.Name ?? "Ungrouped"))
        {
            if (ImGui.Selectable("Ungrouped", group is null)) { profile.GroupId = Guid.Empty; ProfileChanged(); }
            foreach (var choice in C.Groups)
            {
                ImGui.PushID(choice.Id.ToString());
                if (ImGui.Selectable(choice.Name, choice == group)) { profile.GroupId = choice.Id; ProfileChanged(); }
                ImGui.PopID();
            }
            ImGui.EndCombo();
        }
        ScoutDivider();
        SectionTitle("Duty");
        var duty = profile.Duty == 0 ? "Choose a duty" : p.DutyLabel(profile.Duty);
        if (ScoutButton(Fit(duty, Math.Max(0, ImGui.GetContentRegionAvail().X - 24 * Scale)) + "###selectDuty", new Vector2(-1, IconSide),
                small: true, variant: MaterialButtonVariant.Outlined)) OpenCompanionPage(CompanionView.ChooseDuty);
        ScoutTooltip("selectedDuty", duty);
        if (PreferenceSwitch("Require full duty size", ref profile.RequireFullDutySize)) ProfileChanged();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Exclude listings with fewer total slots than the duty requires. Unknown sizes are not filtered.");
        ImGui.Spacing();
        Material.Accordion("jobs", $"Playable jobs · {profile.Jobs.Count}", ref materialJobsExpanded, () => JobPicker(profile), false, true);
    }

    private void DrawProfileFilters(SearchProfile profile)
    {
        var recruitmentLabel = profile.Objectives != PartyObjectives.Any || profile.ClearRequirements != ClearRequirements.Any
            ? "Objective & clear requirements · filtered" : "Objective & clear requirements";
        ImGui.Spacing();
        Material.Accordion("recruitmentFilters", recruitmentLabel, ref materialRecruitmentExpanded, () => RecruitmentFilters(profile), false, true);
        ImGui.Spacing();
        var termsLabel = profile.Include.Length > 0 || profile.Exclude.Length > 0 ? "Description filters · active" : "Description filters";
        if (p.FilterErrorFor(profile) is not null) termsLabel = "Description filters · check pattern";
        Material.Accordion("filters", termsLabel, ref materialFiltersExpanded, () => DescriptionFilters(profile), false, true);
        ImGui.Spacing();
        if (ScoutButton("Shared ranking", small: true)) ShowSettings((int)SettingsPage.Ranking);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Shared preferences for all searches");
        if (p.FilterErrorFor(profile) is null && p.FilterError is { } error) ImGui.TextWrapped(error);
        if (p.SaveError is { } saveError) ImGui.TextWrapped(saveError);
    }

    private void RecruitmentFilters(SearchProfile profile)
    {
        ImGui.TextWrapped("Leave a group unchecked to include any listing.");
        FieldLabel("Party objective");
        if (ImGui.BeginTable("Objectives", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoPadOuterX, new Vector2(ImGui.CalcItemWidth(), 0)))
        {
            ImGui.TableNextColumn(); ObjectiveChoice(profile, "Practice", PartyObjectives.Practice);
            ImGui.TableNextColumn(); ObjectiveChoice(profile, "Duty completion", PartyObjectives.Completion);
            ImGui.TableNextColumn(); ObjectiveChoice(profile, "Loot", PartyObjectives.Loot);
            ImGui.TableNextColumn(); ObjectiveChoice(profile, "No objective", PartyObjectives.Unspecified);
            ImGui.EndTable();
        }
        FieldLabel("Clear requirement");
        if (ImGui.BeginTable("ClearRequirements", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoPadOuterX, new Vector2(ImGui.CalcItemWidth(), 0)))
        {
            ImGui.TableNextColumn(); RequirementChoice(profile, "No restriction", ClearRequirements.Unrestricted);
            ImGui.TableNextColumn(); RequirementChoice(profile, "Must have cleared", ClearRequirements.Cleared);
            ScoutTooltip("weeklyClear", "Includes cleared parties requiring an unclaimed weekly reward.");
            ImGui.TableNextColumn(); RequirementChoice(profile, "Must be uncleared", ClearRequirements.Incomplete);
            ImGui.EndTable();
        }
    }

    private void ObjectiveChoice(SearchProfile profile, string label, PartyObjectives flag)
    {
        var selected = (profile.Objectives & flag) != 0;
        if (!ScoutCheckbox(label, ref selected)) return;
        profile.Objectives = selected ? profile.Objectives | flag : profile.Objectives & ~flag;
        ProfileChanged();
    }

    private void RequirementChoice(SearchProfile profile, string label, ClearRequirements flag)
    {
        var selected = (profile.ClearRequirements & flag) != 0;
        if (!ScoutCheckbox(label, ref selected)) return;
        profile.ClearRequirements = selected ? profile.ClearRequirements | flag : profile.ClearRequirements & ~flag;
        ProfileChanged();
    }

    private void DescriptionFilters(SearchProfile profile)
    {
        if (ScoutCheckbox("Regex", ref profile.Regex)) ProfileChanged();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Case insensitive. Terms: include ALL, exclude ANY; separate with commas or newlines. Regex: one pattern per field.");
        if (ImGui.BeginTable("Terms", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoPadOuterX, new Vector2(ImGui.CalcItemWidth(), 0)))
        {
            ImGui.TableNextColumn(); FieldLabel(profile.Regex ? "Include pattern" : "Include all");
            ImGui.SetNextItemWidth(-1);
            if (ScoutTextField("##Include", "", ref profile.Include, 2048, multiline: true)) ProfileChanged();
            ImGui.TableNextColumn(); FieldLabel(profile.Regex ? "Exclude pattern" : "Exclude any");
            ImGui.SetNextItemWidth(-1);
            if (ScoutTextField("##Exclude", "", ref profile.Exclude, 2048, multiline: true)) ProfileChanged();
            ImGui.EndTable();
        }
        if (p.FilterErrorFor(profile) is { } profileError)
        {
            using var color = ImRaii.PushColor(ImGuiCol.Text, Gold);
            ImGui.TextWrapped(profileError);
        }
    }

    private void JobPicker(SearchProfile profile)
    {
        SecondaryText("Select the jobs you can play. Use + to star a preference.");
        var labelWidth = 64 * Scale;
        if (ImGui.BeginTable("ProfileJobs", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoPadOuterX, new Vector2(ImGui.CalcItemWidth(), 0)))
        {
            ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("Ranged").X + IconSide + 4 * Scale);
            ImGui.TableSetupColumn("Jobs", ImGuiTableColumnFlags.WidthStretch);
            foreach (var role in Enum.GetValues<Role>())
            {
                var choices = p.JobChoices.Where(job => job.Role == role && Jobs.InProfilePicker(job.Id, profile.Jobs.Contains(job.Id))).ToArray();
                var enabled = choices.Where(job => profile.Jobs.Contains(job.Id)).ToArray();
                var allPreferred = enabled.Length > 0 && enabled.All(job => profile.PreferredJobs.Contains(job.Id));
                ImGui.PushID((int)role); ImGui.TableNextRow(); ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled(role switch { Role.Ranged => "Ranged", Role.Caster => "Caster", _ => role.ToString() });
                ImGui.SameLine(0, 2 * Scale);
                using (ImRaii.Disabled(enabled.Length == 0))
                using (ImRaii.PushColor(ImGuiCol.Text, allPreferred ? Gold : Muted))
                    if (Icon(allPreferred ? FontAwesomeIcon.Star : FontAwesomeIcon.Plus, "preferRole", "Toggle preference for selected jobs in this role"))
                    { profile.ToggleRolePreference(role); ProfileChanged(); }
                ImGui.TableNextColumn();
                var starWidth = IconSide;
                var jobWidth = labelWidth + starWidth + 2 * Scale;
                var columns = Math.Clamp((int)((ImGui.GetContentRegionAvail().X + 6 * Scale) / (jobWidth + 6 * Scale)), 1, 4);
                for (var i = 0; i < choices.Length; i++)
                {
                    var job = choices[i];
                    if (i % columns != 0) ImGui.SameLine(0, 6 * Scale);
                    ImGui.PushID((int)job.Id); ImGui.BeginGroup();
                    var selected = profile.Jobs.Contains(job.Id);
                    using (ImRaii.PushColor(ImGuiCol.Button, profile.Jobs.Contains(job.Id) ? Theme.PrimaryActive with { W = .30f } : Theme.Overlay)
                        .Push(ImGuiCol.Text, profile.Jobs.Contains(job.Id) ? Theme.Text : Muted))
                        if (Material.Button(job.Name + "##play", selected ? MaterialButtonVariant.Contained : MaterialButtonVariant.Outlined,
                                false, new Vector2(labelWidth, IconSide)))
                        { profile.ToggleJob(job.Id); ProfileChanged(); }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(profile.Jobs.Contains(job.Id) ? "Remove " + job.Name : "I can play " + job.Name);
                    ImGui.SameLine(0, 2 * Scale);
                    var preferred = profile.PreferredJobs.Contains(job.Id);
                    using (ImRaii.PushColor(ImGuiCol.Text, preferred ? Gold : Muted))
                    using (ImRaii.PushColor(ImGuiCol.Button, preferred ? Theme.PrimaryActive with { W = .30f } : Theme.Paper))
                        if (Icon(preferred ? FontAwesomeIcon.Star : FontAwesomeIcon.Plus, "preferJob", preferred ? "Preferred · click to accept without preference" : "Prefer " + job.Name + " (also selects it)"))
                        { profile.TogglePreference(job.Id); ProfileChanged(); }
                    ImGui.EndGroup(); ImGui.PopID();
                }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        if (profile.Jobs.Count == 0) ImGui.TextColored(Gold, "Choose at least one job.");
        FieldLabel("Favour preferred jobs"); ImGui.SetNextItemWidth(-1);
        if (ScoutSelect("##PreferenceFill", ref profile.PreferredFillAllowance, "At equal fill", "Allow 1 fewer member", "Allow 2 fewer members")) ProfileChanged();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Preferred jobs can outweigh this many fewer filled slots. Multiple preferred vacancies count once. Without stars on any enabled profile, ranking uses role points.");
    }
    private static string RoleName(Role role) => role switch { Role.Ranged => "Physical ranged", Role.Caster => "Magical ranged", _ => role.ToString() };
    private void DutyPickerContents(string search, uint selected, Action<uint> choose)
    {
        void Row(uint id, string name)
        {
            if (!ImGui.Selectable(name + "##duty" + id, id == selected)) return;
            choose(id);
        }

        if (selected != 0) FieldValue("Selected", p.DutyLabel(selected));

        var fontSize = ImGui.GetFontSize();
        if (ImGui.BeginChild("DutyChoices", Vector2.Zero, false))
        {
            MatchParentFontSize(fontSize);
            var query = search.Trim();
            var searching = query.Length > 0;
            var found = false;
            foreach (var group in p.DutyGroups)
            {
                if (!group.Any(duty => DutyPicker.Matches(duty, query))) continue;
                found = true; ImGui.PushID((int)group.Key);
                var label = DutyPicker.Label(group.Key);
                void DrawExpansions()
                {
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
                }
                if (searching) { ImGui.TextColored(Accent, label); DrawExpansions(); }
                else
                {
                    var stateId = ImGui.GetID("categoryExpanded");
                    var expanded = ImGui.GetStateStorage().GetInt(stateId, 0) != 0;
                    if (Material.Accordion("category", label, ref expanded, DrawExpansions, false, true))
                        ImGui.GetStateStorage().SetInt(stateId, expanded ? 1 : 0);
                }
                ImGui.PopID();
            }
            if (!found) ImGui.TextDisabled("No matching duties.");
        }
        ImGui.EndChild();
    }
}
