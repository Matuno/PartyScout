using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using DalamudUi;
using PartyScout.Core;

namespace PartyScout;

internal sealed partial class ScoutWindows
{
    private string customName = "", worldSearch = "";
    private bool materialContactsExpanded;
    private ushort customWorld;
    private const float CompactSlotSize = 16;
    private const float SlotGap = 3;
    private static readonly Vector4[] RoleColors = [new(.28f, .48f, .92f, 1), new(.27f, .70f, .38f, 1), new(.85f, .29f, .32f, 1)];

    private void OverviewAll()
    {
        var matches = p.OverviewMatches;
        SectionTitle("Your matches");
        if (p.FilterError is { } error) ImGui.TextWrapped(error);
        if (matches.Length == 0) SecondaryText(EmptyMatchesMessage());
        ImGui.PushID("matches");
        foreach (var match in matches) ListingCard(match.Listing, match);
        ImGui.PopID();
        AllListingsDivider();
        if (p.BrowseAll)
        {
            var matched = matches.Select(m => m.Listing.Id).ToHashSet();
            var other = p.OverviewListings.Where(l => !matched.Contains(l.Id)).ToArray();
            if (other.Length == 0) ImGui.TextDisabled(OtherListingsStatus());
            ImGui.PushID("other");
            foreach (var listing in other) ListingCard(listing, null);
            ImGui.PopID();
        }
    }

    private string EmptyMatchesMessage() => !p.Active ? "Watching is paused." :
        C.EnabledProfiles.Any(profile => profile.Jobs.Count > 0) ? "No parties match your searches yet." :
        "Enable a search with a duty and a playable job.";

    private string OtherListingsStatus() => !p.Active ? "Start Watch to find other listings." :
        p.Scanning ? "Looking for more listings…" : "No other listings for your enabled duties.";

    private void AllListingsDivider()
    {
        var label = p.BrowseAll ? "Hide other listings" : "Show all listings";
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var buttonWidth = Math.Min(width, ImGui.CalcTextSize(label).X + 2 * WorkspaceTheme.Dark.ButtonPaddingMedium * Scale);
        var lineWidth = Math.Max(0, (width - buttonWidth - 8 * Scale) / 2);
        var y = start.Y + ImGui.GetFrameHeight() / 2;
        var draw = ImGui.GetWindowDrawList(); var color = ImGui.GetColorU32(Theme.Divider);
        draw.AddLine(new Vector2(start.X, y), new Vector2(start.X + lineWidth, y), color);
        draw.AddLine(new Vector2(start.X + width - lineWidth, y), new Vector2(start.X + width, y), color);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (width - buttonWidth) / 2);
        if (ScoutButton(label + "##allListings", new Vector2(buttonWidth, ImGui.GetFrameHeight()), small: true)) p.Post(() => p.SetBrowseAll(!p.BrowseAll));
        ScoutTooltip("browseHint", p.BrowseAll ? "Hide other listings" : "Include filtered listings for your enabled duties");
    }

    private bool CanInspect(Listing listing, DateTimeOffset now) =>
        p.Active && !p.Joining && listing.Expires > now;

    private (bool Yours, bool Better) MarkListing(Listing listing, Match? match, DateTimeOffset now)
    {
        var yours = p.View.Party.InParty && listing.Leader != 0 && listing.Leader == p.View.Party.Leader;
        var better = !yours && match is not null && listing.Fresh(now) && p.View.Party.InParty && match.Rank > p.CurrentScore;
        return (yours, better);
    }

    private void ListingSummary(Match match)
    {
        var listing = match.Listing;
        var now = DateTimeOffset.UtcNow;
        var canInspect = CanInspect(listing, now);
        var (yours, better) = MarkListing(listing, match, now);
        var duty = p.DutyName(listing);
        var title = (yours ? "Your party · " : better ? "↑ " : "") + duty;
        var description = string.IsNullOrWhiteSpace(listing.Description) ? p.JobNames(match.PlayableJobs) : listing.Description;
        var width = Math.Max(1, ImGui.GetContentRegionAvail().X);
        var lineHeight = ImGui.GetTextLineHeight();
        var padding = 8 * Scale;
        var height = MatchRowHeight;
        using (ImRaii.Disabled(!canInspect))
        using (ImRaii.PushColor(ImGuiCol.Button, Theme.Paper).Push(ImGuiCol.ButtonHovered, Theme.Overlay))
            if (ImGui.Button($"##party{listing.Id}", new Vector2(width, height))) OpenListingPage(listing);
        if (!ImGui.IsItemVisible()) return;
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var fill = $"{listing.Filled}/{listing.Total}";
        var fillWidth = ImGui.CalcTextSize(fill).X;
        var left = min.X + 12 * Scale;
        var right = max.X - 12 * Scale;
        var titlePos = PixelAligned(new Vector2(left, min.Y + (height - 2 * lineHeight) / 2));
        var titleWidth = Math.Max(0, right - fillWidth - left - padding);
        var slots = CompactSlotLayout(listing.Total, (right - left) * .65f);
        var slotPos = PixelAligned(new Vector2(right - slots.Width, titlePos.Y + (3 * lineHeight - CompactSlotSize * Scale) / 2));
        var drawing = ImGui.GetWindowDrawList();
        using (p.HeadingFont.Push())
            drawing.AddText(titlePos, ImGui.GetColorU32(yours ? Accent : better ? Theme.Success : Theme.Text), Fit(title, titleWidth));
        drawing.AddText(PixelAligned(new Vector2(right - fillWidth, titlePos.Y)), ImGui.GetColorU32(Muted), fill);
        var descriptionWidth = right - left - (slots.Count > 0 ? slots.Width + padding : 0);
        drawing.AddText(titlePos + new Vector2(0, lineHeight), ImGui.GetColorU32(Muted), Fit(description, Math.Max(0, descriptionWidth)));
        if (canInspect && ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (!ImGui.IsMouseHoveringRect(slotPos, slotPos + new Vector2(slots.Width, CompactSlotSize * Scale)))
            ScoutTooltip("listingSummary" + listing.Id, $"{title} · {fill}\n{description}");
        if (slots.Count > 0) DrawCompactSlotStrip(listing, match, slotPos, slots.Count);
    }

    private void ListingCard(Listing listing, Match? match)
    {
        var now = DateTimeOffset.UtcNow;
        var (yourParty, better) = MarkListing(listing, match, now);
        var canInspect = CanInspect(listing, now);
        ImGui.PushID(listing.Id.ToString());
        var title = p.DutyName(listing);
        if (yourParty) title = "Your party · " + title;
        else if (better) title = "↑ " + title;
        var stateId = ImGui.GetID("expanded");
        var expanded = ImGui.GetStateStorage().GetInt(stateId, match is not null ? 1 : 0) != 0;
        Action? inspect = canInspect ? () => OpenListingPage(listing) : null;
        if (Material.Accordion("party", title, $"{listing.Filled}/{listing.Total}", ref expanded, () =>
        {
            var detail = p.Collected(listing.Id);
            var start = ImGui.GetCursorScreenPos();
            var bodyWidth = Math.Max(1, ImGui.GetContentRegionAvail().X - 16 * Scale);
            ImGui.BeginGroup();
            var description = listing.Description.Replace('\r', ' ');
            if (C.Emphasis == ListingEmphasis.Composition) SlotStrip(listing, detail, match);
            if (description.Length > 0) ImGui.TextWrapped(description);
            if (C.Emphasis == ListingEmphasis.Performance) SummaryLine(detail, listing.Filled);
            if (C.Emphasis != ListingEmphasis.Composition) SlotStrip(listing, detail, match);
            RecruitmentLine(listing with { Languages = detail?.Recruitment?.Languages ?? listing.Languages }, 16 * Scale);
            if (match is { ProfileNames.Length: > 0 } && start.X + bodyWidth - ImGui.GetItemRectMax().X > 106 * Scale)
            {
                ImGui.SameLine();
                OneLine(string.Join(" / ", match.ProfileNames), Muted, 16 * Scale);
                ScoutTooltip("profileNames", "Matched searches · " + string.Join(" / ", match.ProfileNames));
            }
            ImGui.EndGroup();
            var bottom = ImGui.GetItemRectMax().Y;
            if (yourParty || better)
                ImGui.GetWindowDrawList().AddRectFilled(new Vector2(start.X - 16 * Scale, start.Y), new Vector2(start.X - 13 * Scale, bottom),
                    ImGui.GetColorU32(yourParty ? Accent : Theme.Success), 2 * Scale);
            if (canInspect && ImGui.IsWindowHovered() &&
                ImGui.IsMouseHoveringRect(start, new Vector2(start.X + bodyWidth, bottom)))
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) inspect!();
            }
        }, inspect, p.HeadingFont, true, (origin, width) =>
        {
            var slots = CompactSlotLayout(listing.Total, width);
            if (slots.Count > 0) DrawCompactSlotStrip(listing, match, PixelAligned(origin), slots.Count);
        }))
            ImGui.GetStateStorage().SetInt(stateId, expanded ? 1 : 0);
        ImGui.PopID();
    }

    private void YourParty()
    {
        if (p.CurrentParty is not { } detail)
        { SecondaryText(p.View.Party.InParty ? "Waiting for your party roster…" : "You’re playing solo."); return; }
        if (DutyPreviewPending)
        {
            if (dutyPartyPending) { dutyPartyPending = false; dutyPartyUntil = DateTimeOffset.UtcNow.AddSeconds(C.DutyPartySeconds); }
            var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
            ImGui.AlignTextToFramePadding(); OneLine($"Duty loaded · {SecondsLeft(dutyPartyUntil)}s", Gold, IconSide + 4 * Scale);
            ImGui.SameLine(right - IconSide);
            if (Icon(FontAwesomeIcon.Times, "closeDutyPreview", "Collapse party preview")) DismissDutyParty();
            ScoutDivider();
        }
        SectionTitle("Joined duty");
        ItemTitle(detail.Duty == 0 ? "Duty not captured" : p.DutyLabel(detail.Duty));
        if (detail.Description.Length > 0) ImGui.TextWrapped(detail.Description);
        if (detail.Recruitment is { } listing) RecruitmentLine(listing);
        ImGui.Spacing(); ScoutDivider(); ImGui.Spacing();
        SectionTitle($"Members · {detail.Members.Length}");
        SummaryLine(detail, detail.Members.Length);
        Roster(detail); DataNotice();
    }

    private void ListingPage()
    {
        PageHeading("Party details");
        if (openedListing is not { } listing) return;
        var detail = p.Collected(listing.Id);
        if (detail?.Recruitment is { } recent) listing = recent with { IsDuty = listing.IsDuty };
        using (ImRaii.Disabled(!CanInspect(listing, DateTimeOffset.UtcNow)))
            if (ScoutButton("Open in Party Finder", small: true, variant: MaterialButtonVariant.Outlined)) p.Post(() => p.Inspect(listing));
        PartyHeading(p.DutyName(listing), $"{listing.Filled}/{listing.Total}");
        if (listing.Description.Length > 0) ImGui.TextWrapped(listing.Description);
        RecruitmentLine(listing);
        ScoutDivider();
        if (detail is null) SecondaryText("Waiting for party members…");
        else { Roster(detail); DataNotice(); }
    }

    private ulong followedNativeListing;
    private void FollowNativeParty()
    {
        if (p.View.Detail is not { } active) { followedNativeListing = 0; return; }
        if (followedNativeListing == active.ListingId || p.NativeParty?.Recruitment is not { } listing) return;
        followedNativeListing = active.ListingId;
        if (!EditingSetup && (!WorkspaceOpen || companionView is CompanionView.Matches or CompanionView.AllMatches or CompanionView.Listing))
            OpenListingPage(listing);
    }

    private void SummaryLine(PartyDetail? detail, int occupied)
    {
        var data = detail?.Members.Select(m => p.DataFor(m, detail.Duty)).ToArray() ?? [];
        var clears = data.Count(RosterSummary.FullClear);
        var knownProgress = data.Count(d => d?.ProgressError is null && d?.Progress is { } progress && TomestoneData.HasData(progress) ||
            d?.ParseError is null && d?.Parses?.Any(p => !p.Failed && p.Kills.HasValue) == true);
        OneLine(knownProgress == 0 ? "Clear data unavailable" : $"CLR {clears}/{occupied} · {knownProgress} checked", Muted);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("CLR counts members with full-clear evidence; unknown data is not zero. Player averages are shown on each member.\nThese statistics never affect role-weight ranking.");
    }
    private void DataNotice(bool diagnostics = true)
    {
        if (!p.HasTomestone || !p.HasFFLogs)
        {
            if (ScoutButton("Connect player data", small: true)) ShowSettings((int)SettingsPage.PlayerData);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{(!p.HasTomestone ? "Tomestone key missing. " : "")}{(!p.HasFFLogs ? "FFLogs credentials missing." : "")}");
        }
        if (diagnostics && C.Debug) ImGui.TextWrapped(p.LookupStatus);
    }

    private void Roster(PartyDetail detail)
    {
        var listing = detail.Recruitment;
        var current = detail.Id == 0;
        var total = Inspection.RosterSlots(detail);
        var roster = Inspection.DisplayRoster(detail.Members, total);
        if (!ImGui.BeginTable("Roster", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg)) return;
        ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthFixed, 26 * Scale);
        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableSetupColumn("Parse avg.", ImGuiTableColumnFlags.WidthFixed, Math.Max(60 * Scale, ImGui.CalcTextSize("Parse avg.").X));
        ImGui.TableSetupColumn("Progress", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableHeadersRow();
        for (var slot = 0; slot < roster.Length; slot++)
        {
            ImGui.PushID(slot); ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0);
            var unknownSlot = slot >= total;
            var member = roster[slot];
            var job = member?.Job ?? (!unknownSlot && !current && listing is not null ? listing.JobsPresent.ElementAtOrDefault(slot) : (byte)0);
            var mask = !unknownSlot && !current && listing is not null ? listing.Slots.ElementAtOrDefault(slot) : 0;
            var occupied = member is not null || job > 0;
            var data = member is null ? null : p.DataFor(member, detail.Duty);
            Slot(job, mask, occupied, unknownSlot ? "Member's slot is unavailable" : occupied ? "Listed job: " + (job > 0 ? p.JobNames([job]) : "Unknown") : OpenJobs(mask));
            ImGui.TableSetColumnIndex(1);
            var relation = member is null ? SocialKind.None : p.Relation(member);
            OneLine((relation != SocialKind.None ? "* " : "") + (member is null ? occupied ? "Identity pending" : "Open slot" : member.Resolved ? member.Name : member.State == NameState.Pending ? "Resolving…" : "Unavailable"),
                relation != SocialKind.None ? Accent : occupied ? Theme.Text : Muted);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(member is { Resolved: true } ? $"{member.Name} @ {member.World}\n{(relation != SocialKind.None ? relation : "")}" : occupied ? "The game has not resolved this member’s identity." : OpenJobs(mask));
            ImGui.TableSetColumnIndex(2);
            var average = RosterSummary.PlayerAverage(data);
            var value = data?.ParseError is null && data?.Parses is [{ Failed: false } part] ? part.Average : null;
            OneLine(occupied ? average : "—", value is { } number ? ParseColor(number) : Muted);
            if (occupied && ImGui.IsItemHovered())
            {
                var parts = data?.Parses ?? [];
                var explanation = string.Join("\n", parts.Select((part, phase) =>
                    $"{(parts.Length > 1 ? $"P{phase + 1} · " : "")}Mean: {part.Average?.ToString("0.0") ?? "—"} ({part.Samples} public percentiles)\nListed job best: {part.JobBest?.ToString("0.0") ?? "—"} · Any job best: {part.AnyBest?.ToString("0.0") ?? "—"} {part.BestJob}\nClears: {part.Kills?.ToString() ?? "unknown"} · {part.Status}\nLatest zone average: {part.LatestZoneAverage?.ToString("0.0") ?? "—"}"));
                ImGui.SetTooltip(average + "\n" + (data?.ParseError ?? (parts.Length == 0 ? p.HasFFLogs ? "Public parse data pending or unavailable." : "Configure FFLogs in Settings." : explanation)) + "\nZ indicates the latest public FFLogs zone average.");
            }
            ImGui.TableSetColumnIndex(3);
            OneLine(occupied ? RosterSummary.Progress(data) : "—", RosterSummary.FullClear(data) ? Theme.Success : Muted);
            if (occupied && ImGui.IsItemHovered()) ImGui.SetTooltip($"{RosterSummary.Progress(data)}\n{data?.Progress?.DisplayPercent}\nTomestone clears: {data?.Progress?.TotalClears?.ToString() ?? "unknown"} · {data?.Progress?.CompletionWeek}\n{data?.ProgressError ?? (data?.Progress is null ? "Public progression pending or unavailable." : $"Checked {data.ProgressAt:HH:mm} UTC")}\nFFLogs best pull uses up to 10 recent public reports.");
            ImGui.PopID();
        }
        ImGui.EndTable();
    }
    private string OpenJobs(ulong mask) => mask == 0 ? "Slot restrictions unavailable" : p.JobNames(Jobs.Rows.Where(j => (mask & Jobs.Mask(j)) != 0).Select(j => (uint)j));
    private static string Fit(string value, float width)
        => TextFit.Ellipsize(value.Replace('\n', ' ').Replace('\r', ' '), width, static text => ImGui.CalcTextSize(text).X);
    private void OneLine(string text, Vector4 color, float rightInset = 0)
    {
        var fitted = Fit(text, Math.Max(0, ImGui.GetContentRegionAvail().X - rightInset));
        ImGui.TextColored(color, fitted);
        if (fitted != text && ImGui.IsItemHovered()) ImGui.SetTooltip(text);
    }
    private (int Count, float Width) CompactSlotLayout(int total, float available)
    {
        var stride = (CompactSlotSize + SlotGap) * Scale;
        var gap = SlotGap * Scale;
        for (var count = Math.Min(8, total); count > 0; count--)
        {
            var width = count * stride - gap;
            if (count < total) width += gap + ImGui.CalcTextSize($"+{total - count}").X;
            if (width <= available) return (count, width);
        }
        return (0, 0);
    }

    private void DrawCompactSlotStrip(Listing listing, Match? match, Vector2 origin, int count)
    {
        using var id = ImRaii.PushId(listing.Id.ToString());
        SlotStrip(listing, p.Collected(listing.Id), match, CompactSlotSize, count, origin);
        if (count < listing.Total)
        {
            var pos = origin + new Vector2(count * (CompactSlotSize + SlotGap) * Scale, (CompactSlotSize * Scale - ImGui.GetTextLineHeight()) / 2);
            ImGui.GetWindowDrawList().AddText(PixelAligned(pos), ImGui.GetColorU32(Muted), $"+{listing.Total - count}");
        }
    }

    private void SlotStrip(Listing listing, PartyDetail? detail, Match? match, float slotSize = 22, int limit = 48, Vector2? origin = null)
    {
        var profiles = match is null ? C.EnabledProfiles.Where(profile => profile.Targets(listing)).ToArray() : [];
        var playableJobs = match?.PlayableJobs ?? profiles.SelectMany(profile => profile.Jobs).Distinct().Where(listing.Accepts).ToArray();
        IReadOnlyCollection<uint> preferredJobs = match is not null ? match.JobPreferences.Keys :
            profiles.SelectMany(profile => profile.PreferredJobs.Where(profile.Jobs.Contains)).Distinct().Where(listing.Accepts).ToArray();
        for (var i = 0; i < Math.Min(limit, listing.Total); i++)
        {
            if (origin is null && i % 8 != 0) ImGui.SameLine(0, SlotGap * Scale);
            var member = detail?.Members.FirstOrDefault(m => m.Slot == i);
            var job = member?.Job ?? (i < listing.JobsPresent.Length ? listing.JobsPresent[i] : (byte)0);
            var mask = i < listing.Slots.Length ? listing.Slots[i] : 0;
            var occupied = member is not null || job > 0;
            var fit = SlotRoles.Fit(mask, occupied, playableJobs, preferredJobs);
            var tooltip = member is { Resolved: true } ? member.Name + " · " + member.World :
                job > 0 ? p.JobNames([job]) : member is not null ? "Occupied · job unavailable" : "Open: " + OpenJobs(mask);
            if (fit != SlotFit.None)
            {
                tooltip += "\nEligible: " + p.JobNames(playableJobs.Where(j => (mask & Jobs.Mask(j)) != 0));
                if (fit == SlotFit.Preferred) tooltip += "\nPreferred: " + p.JobNames(preferredJobs.Where(j => (mask & Jobs.Mask(j)) != 0));
            }
            ImGui.PushID(i);
            Slot(job, mask, occupied, tooltip, fit, slotSize, origin + new Vector2(i * (slotSize + SlotGap) * Scale, 0));
            ImGui.PopID();
        }
    }
    private void Slot(byte job, ulong mask, bool occupied, string tooltip, SlotFit fit = SlotFit.None, float slotSize = 22, Vector2? position = null)
    {
        var size = slotSize * Scale;
        var pos = position ?? ImGui.GetCursorScreenPos();
        if (position is null) ImGui.Dummy(new Vector2(size));
        // The card already owns the space. Register the chip only for its tooltip.
        else if (!ImGuiP.ItemAdd(new ImRect(pos, pos + new Vector2(size)), 0)) return;
        var colors = occupied ? SlotRoles.Colors(Jobs.Mask(job)) : SlotRoles.Colors(mask);
        var draw = ImGui.GetWindowDrawList();
        if (colors.Length == 0) draw.AddRectFilled(pos, pos + new Vector2(size), ImGui.GetColorU32(new Vector4(.25f, .28f, .33f, 1)), 3 * Scale);
        else for (var stripe = 0; stripe < colors.Length; stripe++)
            draw.AddRectFilled(pos + new Vector2(size * stripe / colors.Length, 0), pos + new Vector2(size * (stripe + 1) / colors.Length, size), ImGui.GetColorU32(RoleColors[colors[stripe]]));
        if (occupied && job > 0 && p.Textures.GetFromGameIcon(new GameIconLookup((uint)(62100 + job))).GetWrapOrDefault() is { } texture)
            draw.AddImage(texture.Handle, pos + new Vector2(1 * Scale), pos + new Vector2(size - 1 * Scale));
        if (fit != SlotFit.None)
        {
            using var font = p.IconFont.Push();
            var glyph = (fit == SlotFit.Preferred ? FontAwesomeIcon.Star : FontAwesomeIcon.Check).ToIconString();
            var factor = .7f * slotSize / 22;
            var glyphSize = ImGui.CalcTextSize(glyph) * factor;
            var glyphPos = pos + (new Vector2(size) - glyphSize) / 2;
            glyphPos = PixelAligned(glyphPos);
            var fontSize = ImGui.GetFontSize() * factor;
            draw.AddText(ImGui.GetFont(), fontSize, glyphPos + new Vector2(1 * Scale), ImGui.GetColorU32(new Vector4(0, 0, 0, .8f)), glyph, 0);
            draw.AddText(ImGui.GetFont(), fontSize, glyphPos, ImGui.GetColorU32(Vector4.One), glyph, 0);
        }
        draw.AddRect(pos, pos + new Vector2(size), ImGui.GetColorU32(new Vector4(1, 1, 1, .22f)), 3 * Scale);
        ScoutTooltip("slotTooltip", tooltip);
    }
    private void RecruitmentLine(Listing listing, float rightInset = 0)
    {
        var remaining = listing.Expires - DateTimeOffset.UtcNow;
        Badge(FontAwesomeIcon.Clock, $"{Math.Max(0, (int)remaining.TotalMinutes):00}:{Math.Max(0, remaining.Seconds):00}", "Recruitment time remaining");
        ImGui.SameLine(); Badge(FontAwesomeIcon.Bullseye, (listing.Objective & 8) != 0 ? "Farm" : (listing.Objective & 4) != 0 ? "Prog" : (listing.Objective & 2) != 0 ? "Clear" : "Any", "Party objective");
        if (listing.ItemLevel > 0)
        { ImGui.SameLine(); Badge(FontAwesomeIcon.ShieldAlt, $"i{listing.ItemLevel}", "Minimum item level"); }
        var restrictions = new List<string>();
        if ((listing.Conditions & 2) != 0) restrictions.Add("CLR");
        if ((listing.Conditions & 4) != 0) restrictions.Add("UNC");
        if ((listing.Conditions & 8) != 0) restrictions.Add("WK");
        if ((listing.DutySettings & 1) != 0) restrictions.Add("UNSYNC");
        if ((listing.DutySettings & 2) != 0) restrictions.Add("MIN");
        if ((listing.DutySettings & 4) != 0) restrictions.Add("NE");
        if (listing.OnePerJob) restrictions.Add("1/J");
        if (listing.Private) restrictions.Add("PW");
        if (listing.Beginners) restrictions.Add("NEW");
        if (listing.Loot == 1) restrictions.Add("GREED");
        if (listing.Loot == 2) restrictions.Add("LM");
        if (listing.Languages > 0) restrictions.Add(string.Join("", new[] { (1, "J"), (2, "E"), (4, "D"), (8, "F") }.Where(l => (listing.Languages & l.Item1) != 0).Select(l => l.Item2)));
        if (restrictions.Count > 0)
        {
            ImGui.SameLine();
            OneLine(string.Join(" · ", restrictions), Muted, rightInset);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("CLR: duty complete · UNC: incomplete · WK: weekly reward unclaimed\nUNSYNC: unrestricted · MIN: minimum item level · NE: no Echo\n1/J: one per job · PW: password · NEW: beginners welcome\nGREED: greed only · LM: lootmaster · J/E/D/F: languages");
        }
    }
    private void Symbol(FontAwesomeIcon icon, Vector4 color)
    {
        var start = ImGui.GetCursorScreenPos();
        var height = ImGui.GetTextLineHeight();
        var side = Math.Min(height, 14 * Scale);
        ImGui.Dummy(new Vector2(side, height));
        using var font = p.IconFont.Push();
        var glyph = icon.ToIconString();
        var natural = ImGui.CalcTextSize(glyph);
        var factor = side / Math.Max(1, Math.Max(natural.X, natural.Y));
        var pos = PixelAligned(start + (new Vector2(side, height) - natural * factor) / 2);
        ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize() * factor, pos, ImGui.GetColorU32(color), glyph, 0);
    }
    private void Badge(FontAwesomeIcon icon, string text, string tooltip)
    {
        Symbol(icon, Muted); ImGui.SameLine(0, 3 * Scale); ImGui.TextDisabled(text);
        ScoutTooltip("badgeTooltip" + icon, tooltip);
    }
    private Vector4 ParseColor(double value) => value switch
    {
        >= 99 => new(1, .72f, .75f, 1), >= 95 => new(1, .58f, .23f, 1), >= 75 => new(.77f, .52f, 1, 1),
        >= 50 => new(.40f, .67f, 1, 1), >= 25 => new(.43f, .83f, .50f, 1), _ => Muted,
    };

    private static string ContactName(SocialHit contact) => string.IsNullOrWhiteSpace(contact.Member.Name) ? "Unknown contact" : contact.Member.Name;

    private void ContactParties(IEnumerable<IGrouping<ulong, SocialHit>> parties, float rowHeight)
    {
        var now = DateTimeOffset.UtcNow;
        var lineHeight = ImGui.GetTextLineHeight();
        var width = Math.Max(1, ImGui.GetContentRegionAvail().X);
        using var colors = ImRaii.PushColor(ImGuiCol.Button, Theme.Paper).Push(ImGuiCol.ButtonHovered, Theme.Overlay);
        foreach (var party in parties)
        {
            var listing = party.First().Listing;
            var canInspect = CanInspect(listing, now);
            using (ImRaii.Disabled(!canInspect))
                if (ImGui.Button($"##contactParty{listing.Id}", new Vector2(width, party.Count() * rowHeight))) OpenListingPage(listing);
            if (!ImGui.IsItemVisible()) continue;

            var min = ImGui.GetItemRectMin();
            var right = ImGui.GetItemRectMax().X - 10 * Scale;
            var pos = min + new Vector2(10 * Scale, (rowHeight - lineHeight) / 2);
            var fill = $"{listing.Filled}/{listing.Total}";
            var fillLeft = right - ImGui.CalcTextSize(fill).X;
            var (yours, _) = MarkListing(listing, null, now);
            var duty = (yours ? "Your party · " : "") + p.DutyName(listing);
            var draw = ImGui.GetWindowDrawList();
            var muted = ImGui.GetColorU32(Muted);
            draw.AddText(PixelAligned(new(fillLeft, pos.Y)), muted, fill);
            var first = true;
            foreach (var hit in party)
            {
                var nameBudget = (first ? fillLeft - 8 * Scale : right) - pos.X;
                float nameWidth;
                using (p.HeadingFont.Push())
                {
                    var name = Fit(ContactName(hit), Math.Max(0, nameBudget));
                    draw.AddText(PixelAligned(pos), ImGui.GetColorU32(Theme.Text), name);
                    nameWidth = ImGui.CalcTextSize(name).X;
                }
                if (first && nameWidth < nameBudget)
                    draw.AddText(PixelAligned(pos + new Vector2(nameWidth, 0)), muted, Fit(" · " + duty, nameBudget - nameWidth));
                pos.Y += rowHeight;
                first = false;
            }

            if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) continue;
            if (canInspect) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            var people = string.Join("\n", party.Select(hit => hit.Member.World.Length == 0 ? ContactName(hit) : $"{ContactName(hit)} @ {hit.Member.World}"));
            ScoutTooltip($"contactParty{listing.Id}", $"{duty} · {fill}\n{people}");
        }
    }

    private void ContactSources()
    {
        if (ScoutCheckbox("Friends", ref C.WatchFriends)) p.Post(p.Save);
        ImGui.SameLine(); if (ScoutCheckbox("FC", ref C.WatchFreeCompany)) p.Post(p.Save);
        ScoutTooltip("fcTooltip", "Find FC members using the public Lodestone roster, including while visiting another data center.");
        var contactsLabel = $"Custom contacts · {C.CustomFriends.Count}";
        Material.Accordion("customContacts", contactsLabel, ref materialContactsExpanded, CustomContactEditor, false, true);
        if (ScoutButton("Scan details", small: true)) OpenCompanionPage(CompanionView.ContactScan);
    }

    private void ContactScanDetails()
    {
        ImGui.TextWrapped(p.ScanStatus);
        if (!p.QuietBackgroundAvailable) ImGui.TextWrapped("Some background sounds or errors cannot be muted on this game build.");
        ImGui.TextDisabled($"Rosters {p.ScannedRosters}/{p.ScannedListings}");
        ImGui.TextWrapped(p.SocialRosterStatus);
        using (ImRaii.Disabled(!p.ContactActive))
            if (ScoutButton("Refresh rosters", small: true, variant: MaterialButtonVariant.Outlined)) p.Post(p.RefreshSocialRosters);
    }

    private void CustomContactEditor()
    {
        var fieldWidth = ImGui.CalcItemWidth();
        ImGui.SetNextItemWidth(fieldWidth); ScoutTextField("##CustomName", "Character name", ref customName, 64);
        ImGui.SetNextItemWidth(fieldWidth);
        if (ImGui.BeginCombo("##CustomWorld", p.WorldChoices.FirstOrDefault(w => w.Id == customWorld).Name ?? "Home world…"))
        {
            ImGui.SetNextItemWidth(-1);
            ScoutTextField("##WorldSearch", "Search worlds…", ref worldSearch, 80);
            foreach (var world in p.WorldChoices.Where(w => w.Name.Contains(worldSearch, StringComparison.OrdinalIgnoreCase)))
                if (ImGui.Selectable(world.Name, world.Id == customWorld)) customWorld = world.Id;
            ImGui.EndCombo();
        }
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(customName) || customWorld == 0 || C.CustomFriends.Count >= 512))
            if (ScoutButton("Add contact", small: true, variant: MaterialButtonVariant.Outlined))
            {
                var name = customName.Trim();
                if (!C.CustomFriends.Any(w => w.World == customWorld && w.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                { C.CustomFriends.Add(new WatchedPlayer { Name = name, World = customWorld }); p.Post(p.Save); }
                customName = "";
            }
        foreach (var person in C.CustomFriends.ToArray())
        {
            ImGui.PushID(person.Name + ":" + person.World);
            if (Icon(FontAwesomeIcon.Trash, "removePerson", "Remove contact")) { C.CustomFriends.Remove(person); p.Post(p.Save); }
            ImGui.SameLine(); OneLine(person.Name + " @ " + p.WorldChoices.FirstOrDefault(w => w.Id == person.World).Name, Theme.Text);
            ImGui.PopID();
        }
    }
}
