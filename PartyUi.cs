using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using PartyScout.Core;

namespace PartyScout;

internal sealed partial class ScoutWindows
{
    private string customName = "", worldSearch = "", currentDutySearch = "";
    private ushort customWorld;
    private static readonly Vector4[] RoleColors = [new(.28f, .48f, .92f, 1), new(.27f, .70f, .38f, 1), new(.85f, .29f, .32f, 1)];

    private void Overview()
    {
        if (p.View.Party.InParty && ImGui.Selectable($"Your party · {(p.CurrentScore is { } yourScore ? $"{yourScore:0.#} {p.ScoreUnit}" : "score unavailable")}##baseline")) SelectTab(RibbonTab.Current);
        if (p.FilterError is { } error) ImGui.TextWrapped(error);
        if (p.OverviewMatches.Length == 0) EmptyMatches();
        else SectionTitle("Your matches");
        ImGui.PushID("matches");
        foreach (var match in p.OverviewMatches) ListingCard(match.Listing, match);
        ImGui.PopID();
        AllListingsDivider();
        if (p.BrowseAll)
        {
            var matched = p.OverviewMatches.Select(m => m.Listing.Id).ToHashSet();
            var other = p.OverviewListings.Where(l => !matched.Contains(l.Id)).ToArray();
            if (other.Length == 0) ImGui.TextDisabled(p.Scanning ? "Looking for more listings…" : "No other listings for your enabled duties.");
            ImGui.PushID("other");
            foreach (var listing in other) ListingCard(listing, null);
            ImGui.PopID();
        }
        FriendsOverview();
    }

    private void EmptyMatches(bool showSearches = false)
    {
        ImGui.TextWrapped(!C.Profiles.Any(profile => profile.Enabled && profile.Jobs.Count > 0)
            ? "Choose your duties and playable jobs to find a party." : p.Active ? "No matching parties yet." : "Press Watch to start searching.");
        if (ImGui.Button("Edit searches")) SelectTab(RibbonTab.Setup);
        if (!showSearches) return;
        var enabled = C.Profiles.Where(profile => profile.Enabled).ToArray();
        if (enabled.Length == 0) return;
        ImGui.Spacing(); SectionTitle("Enabled searches");
        var summaryIndex = 0;
        foreach (var profile in enabled.Take(3))
        {
            var jobs = profile.Jobs.Count == 0 ? "No jobs" : p.JobNames(p.JobChoices.Where(job => profile.Jobs.Contains(job.Id)).Select(job => job.Id));
            var duties = profile.Duties.Count == 0 ? "All duties" : profile.Duties.Count == 1 ? p.DutyLabel(profile.Duties.First()) : $"{profile.Duties.Count} duties";
            ImGui.PushID(summaryIndex++);
            ImGui.BeginGroup();
            var start = ImGui.GetCursorScreenPos();
            ImGui.Indent(8 * Scale);
            ItemTitle(profile.Name);
            using (ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(0, 1) * Scale))
            if (ImGui.BeginTable("SearchSummary", 2, ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthFixed, Math.Max(42 * Scale, ImGui.CalcTextSize("Duty").X + 8 * Scale));
                ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableNextColumn(); FieldLabel("Duty"); ImGui.TableNextColumn(); OneLine(duties, palette.Text);
                ImGui.TableNextColumn(); FieldLabel("Jobs"); ImGui.TableNextColumn(); OneLine(jobs, palette.Text);
                ImGui.EndTable();
            }
            ImGui.Unindent(8 * Scale);
            ImGui.EndGroup();
            var end = ImGui.GetItemRectMax();
            ImGui.GetWindowDrawList().AddRectFilled(start, new Vector2(start.X + 2 * Scale, end.Y), ImGui.GetColorU32(palette.Border), 1 * Scale);
            ImGui.PopID();
            ImGui.Spacing();
        }
        if (enabled.Length > 3) ImGui.TextDisabled("More searches…");
    }

    private void AllListingsDivider()
    {
        var label = p.BrowseAll ? "Show fewer" : "Show all";
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var buttonWidth = ImGui.CalcTextSize(label).X + 2 * ImGui.GetStyle().FramePadding.X;
        var lineWidth = Math.Max(0, (width - buttonWidth - 8 * Scale) / 2);
        var y = start.Y + ImGui.GetFrameHeight() / 2;
        var draw = ImGui.GetWindowDrawList(); var color = ImGui.GetColorU32(palette.Border);
        draw.AddLine(new Vector2(start.X, y), new Vector2(start.X + lineWidth, y), color);
        draw.AddLine(new Vector2(start.X + width - lineWidth, y), new Vector2(start.X + width, y), color);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + lineWidth + 4 * Scale);
        using var transparent = ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero);
        if (ImGui.SmallButton(label + "##allListings")) p.Post(() => p.SetBrowseAll(!p.BrowseAll));
    }

    private void ListingCard(Listing listing, Match? match, bool compact = false, bool inspectable = true, string? contacts = null)
    {
        var fresh = listing.Fresh(DateTimeOffset.UtcNow);
        var yourParty = p.View.Party.InParty && listing.Leader != 0 && listing.Leader == p.View.Party.Leader;
        var better = match is not null && fresh && p.View.Party.InParty && match.Rank > p.CurrentScore;
        var detail = p.Collected(listing.Id);
        var canInspect = inspectable && p.Active && !p.Joining && listing.Expires > DateTimeOffset.UtcNow;
        var playableJobs = match?.PlayableJobs ?? C.Profiles.Where(profile => profile.Enabled &&
                (profile.Duties.Count == 0 || profile.Duties.Contains(listing.Duty)))
            .SelectMany(profile => profile.Jobs).Distinct().Where(listing.Accepts).ToArray();
        var description = listing.Description.Replace('\r', ' ');
        var width = Math.Max(1, ImGui.GetContentRegionAvail().X - 28 * Scale);
        var line = ImGui.GetTextLineHeightWithSpacing();
        var height = 16 * Scale + line +
            Math.Max(1, (listing.Total + 7) / 8) * (22 * Scale + ImGui.GetStyle().ItemSpacing.Y);
        if (description.Length > 0) height += compact ? line : ImGui.CalcTextSize(description, false, width).Y + ImGui.GetStyle().ItemSpacing.Y;
        if (C.Emphasis == ListingEmphasis.Performance) height += line;
        ImGui.PushID(listing.Id.ToString());
        var title = (contacts is null ? "" : contacts + " · ") + p.DutyName(listing);
        if (yourParty) title = "Your party · " + title;
        else if (better) title = "↑ " + title;
        bool expanded;
        using (p.HeadingFont.Push())
        using (ImRaii.PushColor(ImGuiCol.Header, palette.Panel))
        {
            var flags = ImGuiTreeNodeFlags.OpenOnArrow | (match is not null && contacts is null ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
            expanded = ImGui.CollapsingHeader("##party", flags);
            var min = ImGui.GetItemRectMin(); var max = ImGui.GetItemRectMax();
            var count = $"{listing.Filled}/{listing.Total}";
            var countWidth = ImGui.CalcTextSize(count).X;
            var left = min.X + ImGui.GetFontSize() + 2 * ImGui.GetStyle().FramePadding.X;
            var right = max.X - ImGui.GetStyle().FramePadding.X;
            var available = Math.Max(0, right - countWidth - 8 * Scale - left);
            var display = Fit(title, available);
            var y = min.Y + (max.Y - min.Y - ImGui.GetTextLineHeight()) / 2;
            var draw = ImGui.GetWindowDrawList();
            draw.AddText(new Vector2(left, y), ImGui.GetColorU32(palette.Text), display);
            draw.AddText(new Vector2(right - countWidth, y), ImGui.GetColorU32(Muted), count);
            if (ImGui.IsItemHovered())
            {
                if (canInspect && ImGui.GetMousePos().X >= left)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    if (ImGui.IsItemClicked()) p.Post(() => p.Inspect(listing));
                }
                if (display != title) ImGui.SetTooltip(title);
            }
        }
        if (!expanded) { ImGui.PopID(); return; }
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y);
        using var colour = ImRaii.PushColor(ImGuiCol.ChildBg, palette.Panel);
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(14, 8) * Scale);
        var parentFontSize = ImGui.GetFontSize();
        if (ImGui.BeginChild("Listing", new Vector2(0, height), false,
            ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            MatchParentFontSize(parentFontSize);
            if (yourParty || better)
            {
                var pos = ImGui.GetWindowPos();
                ImGui.GetWindowDrawList().AddRectFilled(pos + new Vector2(0, 8 * Scale), pos + new Vector2(3 * Scale, ImGui.GetWindowSize().Y - 8 * Scale),
                    ImGui.GetColorU32(yourParty ? Accent : palette.Good), 2 * Scale);
            }
            if (description.Length > 0)
            {
                if (compact) OneLine(description.Replace('\n', ' '), palette.Text);
                else ImGui.TextWrapped(description);
            }
            SlotStrip(listing, detail, playableJobs);
            if (C.Emphasis == ListingEmphasis.Performance) SummaryLine(detail, listing.Filled);
            RecruitmentLine(listing with { Languages = detail?.Recruitment?.Languages ?? listing.Languages });
            if (canInspect && ImGui.IsWindowHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) p.Post(() => p.Inspect(listing));
            }
        }
        ImGui.EndChild(); ImGui.PopID();
    }

    private void YourParty()
    {
        if (p.CurrentParty is not { } detail)
        { ImGui.TextWrapped(p.View.Party.InParty ? "Waiting for your party roster…" : "You are not in a party."); return; }
        SectionTitle("Your party");
        ImGui.SameLine(); ImGui.TextDisabled(p.CurrentScore is { } score ? $"{score:0.#} {p.ScoreUnit}" : "score unavailable");
        if (p.UsesJobPreferences && ImGui.IsItemHovered()) ImGui.SetTooltip("Fit compares other members plus preference for the job you are playing. Your own slot is excluded here because listings do not include you yet.");
        FieldLabel("Duty");
        if (BeginDutyPicker("##CurrentDuty", detail.Duty == 0 ? "Select your party’s duty…" : p.DutyLabel(detail.Duty)))
        {
            ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##CurrentDutySearch", "Search duties…", ref currentDutySearch, 150);
            HashSet<uint> selected = detail.Duty == 0 ? [] : [detail.Duty];
            DutyPickerContents(currentDutySearch, selected, false, id => p.Post(() => p.SetCurrentDuty(id)));
            ImGui.EndCombo();
        }
        if (detail.Recruitment is { } listing) RecruitmentLine(listing);
        if (detail.Description.Length > 0) { FieldLabel("Party description"); ImGui.TextWrapped(detail.Description); }
        ImGui.Spacing(); SectionTitle("Members");
        SummaryLine(detail, detail.Members.Length);
        DataNotice(); Roster(detail);
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
    private void DataNotice()
    {
        if (!p.HasTomestone || !p.HasFFLogs)
        {
            if (ImGui.SmallButton("Connect player data")) ShowSettings(2);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{(!p.HasTomestone ? "Tomestone key missing. " : "")}{(!p.HasFFLogs ? "FFLogs credentials missing." : "")}");
        }
        if (C.Debug) ImGui.TextWrapped(p.LookupStatus);
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
        ImGui.TableSetupColumn("Avg", ImGuiTableColumnFlags.WidthFixed, 60 * Scale);
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
                relation != SocialKind.None ? Accent : occupied ? palette.Text : Muted);
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
            OneLine(occupied ? RosterSummary.Progress(data) : "—", RosterSummary.FullClear(data) ? palette.Good : Muted);
            if (occupied && ImGui.IsItemHovered()) ImGui.SetTooltip($"{RosterSummary.Progress(data)}\n{data?.Progress?.DisplayPercent}\nTomestone clears: {data?.Progress?.TotalClears?.ToString() ?? "unknown"} · {data?.Progress?.CompletionWeek}\n{data?.ProgressError ?? (data?.Progress is null ? "Public progression pending or unavailable." : $"Checked {data.ProgressAt:HH:mm} UTC")}\nFFLogs best pull uses up to 10 recent public reports.");
            ImGui.PopID();
        }
        ImGui.EndTable();
    }
    private string OpenJobs(ulong mask) => mask == 0 ? "Slot restrictions unavailable" : p.JobNames(Jobs.Rows.Where(j => (mask & Jobs.Mask(j)) != 0).Select(j => (uint)j));
    private static string Fit(string value, float width)
    {
        value = value.Replace('\n', ' ').Replace('\r', ' ');
        if (ImGui.CalcTextSize(value).X <= width) return value;
        var low = 0; var high = value.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (ImGui.CalcTextSize(value[..mid] + "…").X <= width) low = mid; else high = mid - 1;
        }
        return value[..low] + "…";
    }
    private static int? StaleSeconds(DateTimeOffset seen, DateTimeOffset now)
    {
        var age = now - seen;
        return age < TimeSpan.FromSeconds(25) ? null : (int)Math.Clamp(age.TotalSeconds, 1, int.MaxValue);
    }
    private void OneLine(string text, Vector4 color)
    {
        var fitted = Fit(text, ImGui.GetContentRegionAvail().X);
        ImGui.TextColored(color, fitted);
        if (fitted != text && ImGui.IsItemHovered()) ImGui.SetTooltip(text);
    }
    private bool TextLink(string id, string text, Vector4 color)
    {
        var position = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var clicked = ImGui.Selectable("##" + id, false, ImGuiSelectableFlags.None, new Vector2(width, ImGui.GetTextLineHeight()));
        ImGui.GetWindowDrawList().AddText(position, ImGui.GetColorU32(color), Fit(text, width));
        return clicked;
    }
    private void SlotStrip(Listing listing, PartyDetail? detail, IReadOnlyCollection<uint>? playableJobs = null)
    {
        for (var i = 0; i < Math.Min(48, listing.Total); i++)
        {
            if (i % 8 != 0) ImGui.SameLine(0, 3 * Scale);
            var member = detail?.Members.FirstOrDefault(m => m.Slot == i);
            var job = member?.Job ?? (i < listing.JobsPresent.Length ? listing.JobsPresent[i] : (byte)0);
            var mask = i < listing.Slots.Length ? listing.Slots[i] : 0;
            var occupied = member is not null || job > 0;
            var playable = playableJobs is not null && SlotRoles.CanPlay(mask, occupied, playableJobs);
            var tooltip = member is { Resolved: true } ? member.Name + " · " + member.World :
                job > 0 ? p.JobNames([job]) : member is not null ? "Occupied · job unavailable" : "Open: " + p.JobNames(Jobs.Rows.Where(j => (mask & Jobs.Mask(j)) != 0).Select(j => (uint)j));
            if (playable) tooltip += "\nYour jobs: " + p.JobNames(playableJobs!.Where(j => (mask & Jobs.Mask(j)) != 0));
            Slot(job, mask, occupied, tooltip, playable);
        }
    }
    private void Slot(byte job, ulong mask, bool occupied, string tooltip, bool playable = false)
    {
        var colors = occupied ? SlotRoles.Colors(Jobs.Mask(job)) : SlotRoles.Colors(mask);
        var size = 22 * Scale; var pos = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList();
        ImGui.Dummy(new Vector2(size));
        if (colors.Length == 0) draw.AddRectFilled(pos, pos + new Vector2(size), ImGui.GetColorU32(new Vector4(.25f, .28f, .33f, 1)), 3 * Scale);
        else for (var stripe = 0; stripe < colors.Length; stripe++)
            draw.AddRectFilled(pos + new Vector2(size * stripe / colors.Length, 0), pos + new Vector2(size * (stripe + 1) / colors.Length, size), ImGui.GetColorU32(RoleColors[colors[stripe]]));
        if (occupied && job > 0 && p.Textures.GetFromGameIcon(new GameIconLookup((uint)(62100 + job))).GetWrapOrDefault() is { } texture)
            draw.AddImage(texture.Handle, pos + new Vector2(1 * Scale), pos + new Vector2(size - 1 * Scale));
        if (playable)
        {
            using var font = ImRaii.PushFont(UiBuilder.IconFont);
            var star = FontAwesomeIcon.Star.ToIconString();
            var starPos = pos + (new Vector2(size) - ImGui.CalcTextSize(star)) / 2;
            draw.AddText(starPos + new Vector2(1 * Scale), ImGui.GetColorU32(new Vector4(0, 0, 0, .8f)), star);
            draw.AddText(starPos, ImGui.GetColorU32(Vector4.One), star);
        }
        draw.AddRect(pos, pos + new Vector2(size), ImGui.GetColorU32(new Vector4(1, 1, 1, .22f)), 3 * Scale);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
    }
    private void RecruitmentLine(Listing listing)
    {
        var remaining = listing.Expires - DateTimeOffset.UtcNow;
        Badge(FontAwesomeIcon.Clock, $"{Math.Max(0, (int)remaining.TotalMinutes):00}:{Math.Max(0, remaining.Seconds):00}", "Recruitment time remaining");
        ImGui.SameLine(); Badge(FontAwesomeIcon.Bullseye, (listing.Objective & 8) != 0 ? "Farm" : (listing.Objective & 4) != 0 ? "Prog" : (listing.Objective & 2) != 0 ? "Clear" : "Any", "Party objective");
        ImGui.SameLine(); Badge(FontAwesomeIcon.ShieldAlt, $"i{listing.ItemLevel}", "Minimum item level");
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
            OneLine(string.Join(" · ", restrictions), Muted);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("CLR: duty complete · UNC: incomplete · WK: weekly reward unclaimed\nUNSYNC: unrestricted · MIN: minimum item level · NE: no Echo\n1/J: one per job · PW: password · NEW: beginners welcome\nGREED: greed only · LM: lootmaster · J/E/D/F: languages");
        }
    }
    private static void Symbol(FontAwesomeIcon icon, Vector4 color)
    { using (ImRaii.PushFont(UiBuilder.IconFont)) ImGui.TextColored(color, icon.ToIconString()); }
    private void Badge(FontAwesomeIcon icon, string text, string tooltip)
    {
        Symbol(icon, Muted); ImGui.SameLine(0, 3 * Scale); ImGui.TextDisabled(text);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
    }
    private Vector4 ParseColor(double value) => value switch
    {
        >= 99 => new(1, .72f, .75f, 1), >= 95 => new(1, .58f, .23f, 1), >= 75 => new(.77f, .52f, 1, 1),
        >= 50 => new(.40f, .67f, 1, 1), >= 25 => new(.43f, .83f, .50f, 1), _ => Muted,
    };

    private void Inbox()
    {
        if (p.Inbox.Entries.Count == 0) { ImGui.TextWrapped("Better-match alerts will appear here during this session."); return; }
        if (Icon(FontAwesomeIcon.CheckDouble, "markRead", "Mark all alerts read", new Vector2(ImGui.GetFrameHeight()))) p.Inbox.ReadAll();
        ImGui.SameLine();
        if (ImGui.SmallButton("Dismiss all"))
            foreach (var entry in p.Inbox.Entries.ToArray()) p.Post(() => p.DismissInbox(entry.Match));
        foreach (var entry in p.Inbox.Entries.ToArray())
        {
            var match = entry.Match;
            ImGui.PushID(match.Listing.Id.ToString());
            var fresh = p.Matches.FirstOrDefault(m => m.Listing.Id == match.Listing.Id && m.Listing.Fresh(DateTimeOffset.UtcNow));
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled($"{entry.At.ToLocalTime():HH:mm}" + (fresh is null ? " · No current match" : " · Click party to inspect"));
            ImGui.SameLine(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - ImGui.GetFrameHeight());
            if (Icon(FontAwesomeIcon.Times, "dismiss", "Dismiss alert", new Vector2(ImGui.GetFrameHeight()))) p.Post(() => p.DismissInbox(match));
            ListingCard(fresh?.Listing ?? match.Listing, fresh ?? match, inspectable: fresh is not null && p.Active && !p.Joining);
            ImGui.PopID();
        }
    }

    private void FriendsOverview(bool compact = false)
    {
        ImGui.Spacing(); SectionTitle("Friends & contacts");
        var parties = p.SocialHits.GroupBy(h => h.Listing.Id).ToArray();
        if (parties.Length == 0) ImGui.TextDisabled(p.ContactActive ? "No contact parties found yet." : "Contact scan paused.");
        ImGui.PushID("friends");
        foreach (var party in compact ? parties.Take(2) : parties)
        {
            var hits = party.ToArray();
            var names = string.Join(", ", hits.Select(h => h.Member.Name).Distinct());
            ListingCard(hits[0].Listing, null, compact, contacts: names);
        }
        ImGui.PopID();
    }

    private void ContactSources()
    {
        if (ImGui.Checkbox("Friends", ref C.WatchFriends)) p.Post(p.Save);
        ImGui.SameLine(); if (ImGui.Checkbox("FC", ref C.WatchFreeCompany)) p.Post(p.Save);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Find FC members using the public Lodestone roster, including while visiting another data center.");
        if (SectionDisclosure("Sources & scan details"))
        {
            ImGui.TextWrapped(p.ScanStatus);
            if (!p.QuietBackgroundAvailable) ImGui.TextWrapped("Some background sounds or errors cannot be muted on this game build.");
            ImGui.TextDisabled($"Rosters {p.ScannedRosters}/{p.ScannedListings}");
            ImGui.TextWrapped(p.SocialRosterStatus);
            using (ImRaii.Disabled(!p.ContactActive)) if (Icon(FontAwesomeIcon.Sync, "refreshRosters", "Refresh friend and FC rosters", new Vector2(ImGui.GetFrameHeight()))) p.Post(p.RefreshSocialRosters);
        }
        if (SectionDisclosure($"Custom contacts · {C.CustomFriends.Count}"))
        {
            ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##CustomName", "Character name", ref customName, 64);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##CustomWorld", p.WorldChoices.FirstOrDefault(w => w.Id == customWorld).Name ?? "Home world…"))
            {
                ImGui.InputTextWithHint("##WorldSearch", "Search worlds…", ref worldSearch, 80);
                foreach (var world in p.WorldChoices.Where(w => w.Name.Contains(worldSearch, StringComparison.OrdinalIgnoreCase)))
                    if (ImGui.Selectable(world.Name, world.Id == customWorld)) customWorld = world.Id;
                ImGui.EndCombo();
            }
            using (ImRaii.Disabled(string.IsNullOrWhiteSpace(customName) || customWorld == 0 || C.CustomFriends.Count >= 512))
                if (Icon(FontAwesomeIcon.UserPlus, "addPerson", "Add contact", new Vector2(ImGui.GetFrameHeight())))
                {
                    var name = customName.Trim();
                    if (!C.CustomFriends.Any(w => w.World == customWorld && w.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    { C.CustomFriends.Add(new WatchedPlayer { Name = name, World = customWorld }); p.Post(p.Save); }
                    customName = "";
                }
            foreach (var person in C.CustomFriends.ToArray())
            {
                ImGui.PushID(person.Name + ":" + person.World);
                if (Icon(FontAwesomeIcon.Trash, "removePerson", "Remove contact", new Vector2(ImGui.GetFrameHeight()))) { C.CustomFriends.Remove(person); p.Post(p.Save); }
                ImGui.SameLine(); OneLine(person.Name + " @ " + p.WorldChoices.FirstOrDefault(w => w.Id == person.World).Name, palette.Text);
                ImGui.PopID();
            }
        }
    }
}
