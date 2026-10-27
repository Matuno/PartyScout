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
        if (p.View.Party.InParty && ImGui.Selectable($"Your party · {(p.CurrentScore is { } yourScore ? $"{yourScore} pts" : "score unavailable")}##baseline")) SelectTab(RibbonTab.Current);
        ContactSummary();
        if (p.OverviewMatches.Length == 0)
        {
            ImGui.TextWrapped(p.Active ? "No matching parties yet." : "Press Watch to start searching.");
            if (Icon(FontAwesomeIcon.SlidersH, "editProfiles", "Edit search profiles", new Vector2(ImGui.GetFrameHeight()))) SelectTab(RibbonTab.Setup);
        }
        else ImGui.TextDisabled("YOUR MATCHES");
        foreach (var match in p.OverviewMatches) ListingCard(match.Listing, match);
        AllListingsDivider();
        if (!p.BrowseAll) return;
        var matched = p.OverviewMatches.Select(m => m.Listing.Id).ToHashSet();
        var other = p.OverviewListings.Where(l => !matched.Contains(l.Id)).ToArray();
        if (other.Length == 0) ImGui.TextDisabled(p.Scanning ? "Looking for more listings…" : "No other listings for your enabled duties.");
        foreach (var listing in other) ListingCard(listing, null);
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

    private void ListingCard(Listing listing, Match? match)
    {
        var fresh = listing.Fresh(DateTimeOffset.UtcNow);
        var rows = Math.Max(1, (listing.Total + 7) / 8);
        var description = listing.Description.Replace('\r', ' ');
        var wrapWidth = Math.Max(1, ImGui.GetContentRegionAvail().X - 2 * ImGui.GetStyle().WindowPadding.X);
        var descriptionHeight = description.Length == 0 ? 0 : ImGui.CalcTextSize(description, false, wrapWidth).Y + ImGui.GetStyle().ItemSpacing.Y;
        var lines = (fresh ? 4 : 5) - (C.Emphasis == ListingEmphasis.Description ? 1 : 0);
        var height = lines * ImGui.GetTextLineHeightWithSpacing() + (rows * 25 + 16) * Scale + descriptionHeight;
        var score = match?.Score ?? Jobs.Score(listing.JobsPresent.Take(listing.Total), C);
        var yourParty = p.View.Party.InParty && listing.Leader != 0 && listing.Leader == p.View.Party.Leader;
        ImGui.PushID(listing.Id.ToString());
        var border = yourParty ? Accent :
            match is not null && fresh && p.View.Party.InParty && score > p.CurrentScore ? palette.Good : palette.Border;
        border.W = .55f;
        using var colour = ImRaii.PushColor(ImGuiCol.ChildBg, palette.Panel)
            .Push(ImGuiCol.Border, border);
        if (ImGui.BeginChild("Listing", new Vector2(0, height), true, ImGuiWindowFlags.NoScrollbar))
        {
            var detail = p.Collected(listing.Id);
            var inspect = false;
            if (ImGui.BeginTable("Header", 2, ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Duty", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Score", ImGuiTableColumnFlags.WidthFixed, 88 * Scale);
                ImGui.TableNextColumn();
                inspect = TextLink("inspect", p.DutyName(listing), Accent);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(p.DutyName(listing) + "\n" + listing.Description);
                ImGui.TableNextColumn();
                var delta = match is not null && p.View.Party.InParty && p.CurrentScore is { } current ? score - current : 0;
                ImGui.TextColored(fresh && delta > 0 ? palette.Good : Muted, $"{score} pts{(delta > 0 ? $" +{delta}" : "")}");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Filled-role weight. " + (p.View.Party.InParty ? p.CurrentScore is { } baseline ? $"Your party: {baseline} pts." : "Your party's score is unavailable." : "No current party."));
                ImGui.EndTable();
            }
            if (C.Emphasis == ListingEmphasis.Performance) SummaryLine(detail, listing.Filled);
            else OneLine((yourParty ? "YOUR PARTY · " : "") +
                (match is null ? "Outside profile filters" : string.Join(" / ", match.ProfileNames) + " · " + p.JobNames(match.PlayableJobs)), Muted);
            if (!fresh) OneLine($"Last seen {Math.Max(1, (int)(DateTimeOffset.UtcNow - listing.Seen).TotalSeconds)}s ago · may have changed", Gold);
            SlotStrip(listing, detail);
            ImGui.SameLine(); ImGui.TextDisabled($"{listing.Filled}/{listing.Total}");
            if (detail is not null && StaleSeconds(detail.Collected, DateTimeOffset.UtcNow) is { } rosterAge)
            {
                ImGui.SameLine(); ImGui.TextColored(Gold, $"· roster {rosterAge}s");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Roster last checked " + rosterAge + " seconds ago; members may have changed.");
            }
            if (C.Emphasis == ListingEmphasis.Performance)
                OneLine(detail is null ? "Roster unavailable" : string.Join(" · ", detail.Members.Select(m => RosterSummary.Progress(p.DataFor(m, detail.Duty))).Distinct()), Muted);
            else if (C.Emphasis != ListingEmphasis.Description) SummaryLine(detail, listing.Filled);
            RecruitmentLine(listing with { Languages = detail?.Recruitment?.Languages ?? listing.Languages });
            if (description.Length > 0) { ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X); ImGui.TextWrapped(description); ImGui.PopTextWrapPos(); }
            if (ImGui.IsWindowHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                inspect |= ImGui.IsMouseClicked(ImGuiMouseButton.Left);
            }
            if (inspect) p.Post(() => p.Inspect(listing));
        }
        ImGui.EndChild(); ImGui.PopID();
    }

    private void ContactSummary()
    {
        var parties = p.SocialHits.GroupBy(hit => hit.Listing.Id).Take(3).ToArray();
        ImGui.TextColored(Accent, "FRIENDS & CONTACTS");
        ImGui.SameLine(); if (Icon(FontAwesomeIcon.UserFriends, "showPeople", "Show all friends and contacts", new Vector2(ImGui.GetFrameHeight()))) SelectTab(RibbonTab.People);
        if (parties.Length == 0) ImGui.TextDisabled(C.WatchContacts && p.Active ? "No contacts found yet." : "Contact scan paused.");
        ImGui.PushID("ContactSummary");
        foreach (var party in parties)
        {
            var hits = party.ToArray(); var listing = hits[0].Listing;
            var names = string.Join(", ", hits.Select(h => h.Member.Name.Length > 0 ? h.Member.Name : "Contact").Distinct());
            var age = StaleSeconds(hits.Min(h => h.Seen), DateTimeOffset.UtcNow) is { } seconds ? $" · seen {seconds}s ago" : "";
            ImGui.PushID(listing.Id.ToString());
            if (TextLink("contact", names + " · " + p.DutyName(listing) + age, palette.Text)) p.Post(() => p.Inspect(listing));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(names + " · " + p.DutyName(listing) + age);
            ImGui.PopID();
        }
        ImGui.PopID();
        ImGui.Separator();
    }

    private void Party()
    {
        if (p.PreviewError is { } error) ImGui.TextWrapped(error);
        if (p.Inspected is not { } detail)
        { ImGui.TextWrapped(p.HasInspection ? "Collecting this party’s roster…" : "Inspect a match or contact to see its roster."); return; }
        OneLine(p.InspectedDuty, Accent);
        if (StaleSeconds(detail.Collected, DateTimeOffset.UtcNow) is { } rosterAge)
            OneLine($"Roster last checked {rosterAge}s ago · members may have changed", Gold);
        if (detail.Recruitment is { } listing)
        {
            RecruitmentLine(listing);
            if (listing.Description.Length > 0) ImGui.TextWrapped(listing.Description);
            var current = p.Matches.FirstOrDefault(m => m.Listing.Id == listing.Id);
            using (ImRaii.Disabled(current is null || p.Joining || !p.Active || !current.PlayableJobs.Contains(p.View.CurrentJob) ||
                current.Listing.Parties != 1 || current.Listing.Leader == p.View.Party.Leader))
                if (ImGui.SmallButton(p.View.Party.InParty ? "Leave & join" : "Join party") && current is not null) p.Post(() => p.BeginJoin(current));
            if (p.CurrentScore >= 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"{Jobs.Score(listing.JobsPresent, C)} pts · yours {p.CurrentScore}");
            }
        }
        SummaryLine(detail, detail.Recruitment?.Filled ?? detail.Members.Length);
        DataNotice(); Roster(detail);
        if (p.ActionMessage is { } message) ImGui.TextWrapped(message);
    }

    private void YourParty()
    {
        if (p.CurrentParty is not { } detail)
        { ImGui.TextWrapped(!p.Active ? "Start Watch to read your current party." : p.View.Party.InParty ? "Waiting for your party roster…" : "You are not in a party."); return; }
        ImGui.TextColored(Accent, $"YOUR PARTY · {(p.CurrentScore is { } score ? $"{score} pts" : "SCORE UNAVAILABLE")}");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##CurrentDuty", detail.Duty == 0 ? "Select your party’s duty…" : p.DutyLabel(detail.Duty)))
        {
            ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##CurrentDutySearch", "Search duties…", ref currentDutySearch, 150);
            if (ImGui.BeginTabBar("CurrentDutyCategories"))
            {
                foreach (var highEnd in new[] { true, false })
                    if (ImGui.BeginTabItem(highEnd ? "High-end" : "Other"))
                    {
                        if (ImGui.BeginChild("CurrentDuties", new Vector2(0, 160 * Scale)))
                            foreach (var duty in p.DutyChoices.Where(d => d.HighEnd == highEnd && d.Name.Contains(currentDutySearch, StringComparison.CurrentCultureIgnoreCase)))
                                if (ImGui.Selectable(duty.Name + "##current" + duty.Id, duty.Id == detail.Duty))
                                { var id = duty.Id; p.Post(() => p.SetCurrentDuty(id)); }
                        ImGui.EndChild(); ImGui.EndTabItem();
                    }
                ImGui.EndTabBar();
            }
            ImGui.EndCombo();
        }
        if (detail.Recruitment is { } listing) RecruitmentLine(listing);
        SummaryLine(detail, detail.Members.Length);
        DataNotice(); Roster(detail);
    }

    private void SummaryLine(PartyDetail? detail, int occupied)
    {
        var data = detail?.Members.Select(m => p.DataFor(m, detail.Duty)).ToArray() ?? [];
        var parts = RosterSummary.Averages(data, occupied);
        var text = string.Join(" · ", parts.Select((part, i) =>
            $"{(parts.Length > 1 ? $"P{i + 1} " : "")}Avg {part.Average?.ToString("0.0") ?? "—"} ({part.Coverage}/{part.Total})"));
        var clears = data.Count(RosterSummary.FullClear);
        var knownProgress = data.Count(d => d?.ProgressError is null && d?.Progress is { } progress && TomestoneData.HasData(progress) ||
            d?.ParseError is null && d?.Parses?.Any(p => !p.Failed && p.Kills.HasValue) == true);
        OneLine(text + $" · CLR {(knownProgress == 0 ? "—" : clears.ToString())}", Muted);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Mean of member averages for this duty and each listed job; coverage is members with public data / occupied slots.\nPhases are separate. CLR counts members with full-clear evidence; unknown data is not zero.\nThese statistics never affect role-weight ranking.");
    }
    private void DataNotice()
    {
        if (!p.HasTomestone || !p.HasFFLogs)
        {
            if (ImGui.SmallButton("Connect player data")) SettingsOpen = true;
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
        var columns = ImGui.GetContentRegionAvail().X >= 440 * Scale ? 4 : 2;
        if (!ImGui.BeginTable("Roster", columns, ImGuiTableFlags.SizingStretchSame)) return;
        for (var slot = 0; slot < roster.Length; slot++)
        {
            ImGui.TableNextColumn(); ImGui.PushID(slot);
            var unknownSlot = slot >= total;
            var member = roster[slot];
            var job = member?.Job ?? (!unknownSlot && !current && listing is not null ? listing.JobsPresent.ElementAtOrDefault(slot) : (byte)0);
            var mask = !unknownSlot && !current && listing is not null ? listing.Slots.ElementAtOrDefault(slot) : 0;
            var occupied = member is not null || job > 0;
            var data = member is null ? null : p.DataFor(member, detail.Duty);
            var parts = data?.ParseError is null ? data?.Parses : null;
            var zone = data?.Parses?.FirstOrDefault();
            var zoneInfo = zone?.LatestZoneAverage is { } zoneAverage
                ? $"\nLatest FFLogs zone {(zone.LatestZoneMedian ? "median" : "best")} average: {zoneAverage:0.0}"
                : zone?.LatestZoneFailed == true ? "\nLatest FFLogs zone average unavailable; retrying later." : "";
            var rows = parts is { Length: > 1 } ? 5 : 4;
            using var card = ImRaii.PushColor(ImGuiCol.ChildBg, palette.Panel);
            if (ImGui.BeginChild("Member", new Vector2(0, 26 * Scale + (rows - 1) * ImGui.GetTextLineHeightWithSpacing() + 2 * ImGui.GetStyle().WindowPadding.Y), true, ImGuiWindowFlags.NoScrollbar))
            {
                Slot(job, mask, occupied, unknownSlot ? "Party member's slot is unavailable" : occupied ? "Listed job: " + (job > 0 ? p.JobNames([job]) : "Unknown") : OpenJobs(mask));
                ImGui.SameLine(); ImGui.TextUnformatted(unknownSlot ? "Slot unknown" + (job > 0 ? " · " + p.JobNames([job]) : "") : occupied ? job > 0 ? p.JobNames([job]) : "?" : "Open");
                if (member is not null)
                {
                    var relation = p.Relation(member);
                    OneLine((relation != SocialKind.None ? "* " : "") + (member.Resolved ? member.Name : member.State == NameState.Pending ? "Resolving…" : "Unavailable"), palette.Text);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(member.Resolved ? $"{member.Name} @ {member.World}\n{(relation != SocialKind.None ? relation : "")}" : "The game has not resolved this member’s identity.");
                    if (parts is { Length: > 0 })
                        for (var phase = 0; phase < parts.Length; phase++)
                        {
                            var parse = parts[phase];
                            var zoneOnly = parts.Length == 1 && parse.Average is null && parse.LatestZoneAverage.HasValue;
                            var shown = zoneOnly ? parse.LatestZoneAverage : parse.Average;
                            OneLine($"{(zoneOnly ? "Zone " : parts.Length > 1 ? $"P{phase + 1} " : "Avg ")}{shown?.ToString("0.0") ?? "—"}", shown is { } value ? ParseColor(value) : Muted);
                            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Mean of {parse.Samples} returned public percentiles for this duty and listed job.\nListed job best: {parse.JobBest?.ToString("0.0") ?? "—"} · Any job best: {parse.AnyBest?.ToString("0.0") ?? "—"} {parse.BestJob}\nClears: {parse.Kills?.ToString() ?? "unknown"}{zoneInfo}\n{parse.Status}\nChecked {data!.ParsesAt:HH:mm} UTC");
                        }
                    else
                    {
                        ImGui.TextDisabled("Avg —");
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip((data?.ParseError ?? (p.HasFFLogs ? "Public parse data pending or unavailable." : "Configure FFLogs in Settings.")) + zoneInfo);
                    }
                    OneLine(RosterSummary.Progress(data), RosterSummary.FullClear(data) ? palette.Good : Muted);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{RosterSummary.Progress(data)}\n{data?.Progress?.DisplayPercent}\nTomestone clears: {data?.Progress?.TotalClears?.ToString() ?? "unknown"} · {data?.Progress?.CompletionWeek}\n{data?.ProgressError ?? (data?.Progress is null ? "Public progression pending or unavailable." : data.Progress.LookupFailed ? "Some Tomestone data is unavailable; retrying." : $"Tomestone checked {data.ProgressAt:HH:mm} UTC")}\nFFLogs best pull uses up to 10 recent public reports.");
                }
                else
                {
                    if (occupied)
                    {
                        OneLine("Identity pending", Muted);
                        ImGui.TextDisabled("Avg —");
                        ImGui.TextDisabled("—");
                    }
                    else if (!current) OneLine(OpenJobs(mask), Muted);
                }
            }
            ImGui.EndChild(); ImGui.PopID();
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

    private void SlotStrip(Listing listing, PartyDetail? detail)
    {
        for (var i = 0; i < Math.Min(48, listing.Total); i++)
        {
            if (i % 8 != 0) ImGui.SameLine(0, 3 * Scale);
            var member = detail?.Members.FirstOrDefault(m => m.Slot == i);
            var job = member?.Job ?? (i < listing.JobsPresent.Length ? listing.JobsPresent[i] : (byte)0);
            var mask = i < listing.Slots.Length ? listing.Slots[i] : 0;
            Slot(job, mask, member is not null || job > 0, member is { Resolved: true } ? member.Name + " · " + member.World :
                job > 0 ? p.JobNames([job]) : member is not null ? "Occupied · job unavailable" : "Open: " + p.JobNames(Jobs.Rows.Where(j => (mask & Jobs.Mask(j)) != 0).Select(j => (uint)j)));
        }
    }
    private void Slot(byte job, ulong mask, bool occupied, string tooltip)
    {
        var colors = occupied ? SlotRoles.Colors(Jobs.Mask(job)) : SlotRoles.Colors(mask);
        var size = 22 * Scale; var pos = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList();
        ImGui.Dummy(new Vector2(size));
        if (colors.Length == 0) draw.AddRectFilled(pos, pos + new Vector2(size), ImGui.GetColorU32(new Vector4(.25f, .28f, .33f, 1)), 3 * Scale);
        else for (var stripe = 0; stripe < colors.Length; stripe++)
            draw.AddRectFilled(pos + new Vector2(size * stripe / colors.Length, 0), pos + new Vector2(size * (stripe + 1) / colors.Length, size), ImGui.GetColorU32(RoleColors[colors[stripe]]));
        if (occupied && job > 0 && p.Textures.GetFromGameIcon(new GameIconLookup((uint)(62100 + job))).GetWrapOrDefault() is { } texture)
            draw.AddImage(texture.Handle, pos + new Vector2(1 * Scale), pos + new Vector2(size - 1 * Scale));
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
        Symbol(icon, Muted); ImGui.SameLine(0, 3); ImGui.TextUnformatted(text);
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
            OneLine(p.DutyName(match.Listing), entry.Read ? palette.Text : Accent);
            ImGui.TextDisabled($"{entry.At.ToLocalTime():HH:mm} · {match.Score} pts");
            OneLine(string.Join(" / ", match.ProfileNames), Muted);
            OneLine(match.Listing.Description, palette.Text);
            var fresh = p.Matches.FirstOrDefault(m => m.Listing.Id == match.Listing.Id && m.Listing.Fresh(DateTimeOffset.UtcNow));
            using (ImRaii.Disabled(fresh is null || !p.Active || p.Joining))
                if (Icon(FontAwesomeIcon.Eye, "review", "Review party", new Vector2(ImGui.GetFrameHeight())) && fresh is not null) p.Post(() => p.Inspect(fresh.Listing));
            ImGui.SameLine(); if (Icon(FontAwesomeIcon.Times, "dismiss", "Dismiss alert", new Vector2(ImGui.GetFrameHeight()))) p.Post(() => p.DismissInbox(match));
            if (fresh is null) { ImGui.SameLine(); ImGui.TextDisabled("No current match"); }
            ImGui.Separator(); ImGui.PopID();
        }
    }

    private void People()
    {
        if (ImGui.Checkbox("Friends", ref C.WatchFriends)) p.Post(p.Save);
        ImGui.SameLine(); if (ImGui.Checkbox("FC", ref C.WatchFreeCompany)) p.Post(p.Save);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Find FC members using the public Lodestone roster, including while visiting another data center.");
        ImGui.SameLine(); using (ImRaii.Disabled(!p.Active || p.Joining))
            if (Toggle(C.WatchContacts ? FontAwesomeIcon.Stop : FontAwesomeIcon.Play, "Sweep", C.WatchContacts && p.Active, "Continuously scan native DC / All pages for contacts while monitoring search profiles."))
                p.Post(C.WatchContacts ? p.StopScan : p.StartScan);
        if (ImGui.CollapsingHeader("Sources & scan details"))
        {
            ImGui.TextWrapped(p.ScanStatus);
            if (!p.QuietBackgroundAvailable) ImGui.TextWrapped("Some background sounds or errors cannot be muted on this game build.");
            ImGui.TextDisabled($"Rosters {p.ScannedRosters}/{p.ScannedListings}");
            ImGui.TextWrapped(p.SocialRosterStatus);
            using (ImRaii.Disabled(!p.Active)) if (Icon(FontAwesomeIcon.Sync, "refreshRosters", "Refresh friend and FC rosters", new Vector2(ImGui.GetFrameHeight()))) p.Post(p.RefreshSocialRosters);
        }
        var parties = p.SocialHits.GroupBy(h => h.Listing.Id).ToArray();
        if (parties.Length == 0) ImGui.TextWrapped("No contact parties collected. Start a sweep to check friends, FC and custom contacts.");
        foreach (var party in parties)
        {
            var hits = party.ToArray(); var listing = hits[0].Listing;
            ImGui.PushID(listing.Id.ToString());
            var age = StaleSeconds(hits.Min(h => h.Seen), DateTimeOffset.UtcNow) is { } seconds ? $" · seen {seconds}s ago" : "";
            OneLine(string.Join(", ", hits.Select(h => $"{h.Member.Name} ({h.Kind})").Distinct()) + age, Accent);
            if (TextLink("social", p.DutyName(listing), palette.Text)) p.Post(() => p.Inspect(listing));
            SlotStrip(listing, p.Collected(listing.Id)); RecruitmentLine(listing);
            ImGui.Separator(); ImGui.PopID();
        }
        if (ImGui.CollapsingHeader($"Custom contacts · {C.CustomFriends.Count}"))
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
