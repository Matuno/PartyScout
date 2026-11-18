using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using DalamudUi;
using PartyScout.Core;

namespace PartyScout;

internal sealed partial class ScoutWindows(Plugin p)
{
    private enum CompanionView { Matches, SearchProfiles, Party, AllMatches, Listing, Contacts, ContactResults, ContactScan, Settings, Offer, EditSearch, ChooseDuty, NewGroup, DeleteSearch }
    private enum SettingsPage { Alerts, Ranking, Display, PlayerData, Diagnostics }
    private readonly Stack<CompanionView> companionBack = new();
    private bool dutyPartyPending, collapsedAfterDuty;
    private DateTimeOffset dutyPartyUntil;
    internal bool DutyPreviewPending => dutyPartyPending || dutyPartyUntil != default;
    internal bool PartyInCompanion => WorkspaceOpen && companionView == CompanionView.Party;
    internal bool PartyOpen => PartyInCompanion;
    internal bool MatchesOpen => WorkspaceOpen && companionView == CompanionView.AllMatches;
    internal bool SearchesOpen => WorkspaceOpen && companionView == CompanionView.SearchProfiles;
    internal string CompanionPage => companionView.ToString();
    internal bool MainOpen;
    internal bool WorkspaceOpen => MainOpen && Mode == RibbonMode.Open;
    internal bool EditingSetup => WorkspaceOpen && (companionView is CompanionView.EditSearch or CompanionView.ChooseDuty or CompanionView.NewGroup or CompanionView.DeleteSearch ||
        companionBack.Contains(CompanionView.EditSearch) || companionView == CompanionView.Settings && settingsTab == (int)SettingsPage.Alerts);
    internal bool NativeFinderHovered { get; private set; }
    internal bool NativeFinderInteracting => NativeFinderHovered || DateTimeOffset.UtcNow - lastFinderHover < TimeSpan.FromSeconds(2);
    private DateTimeOffset lastFinderHover;
    internal RibbonMode Mode => DutyPreviewPending && p.CurrentParty is not null ? RibbonMode.Open : collapsedAfterDuty ? RibbonMode.Collapsed : C.WindowMode ?? RibbonMode.Collapsed;
    private int settingsTab = -1;
    private string dutySearch = "";
    private SearchProfile? editingProfile, profileToDelete;
    private bool focusProfileEditor;
    private string tomestoneKey = "", ffLogsId = "", ffLogsSecret = "";
    private bool fontRefreshQueued, draggingGlyph;
    private Vector2 panelSize;
    private float panelContentHeight, panelHeightLimit;
    private CompanionView companionView;
    private Listing? openedListing;
    private ulong autoOpenedOffer;
    private Match? CurrentOffer => p.SwitchingTo ?? p.Alert.Target;
    private static readonly MaterialPalette Theme = WorkspaceTheme.Dark;
    private Vector4 Accent => Theme.Primary;
    private Vector4 Muted => Theme.TextSecondary;
    private static readonly Vector4 Gold = new(1, .81f, .42f, 1);
    private Configuration C => p.Settings;
    private float Scale => ImGuiHelpers.GlobalScale * C.Scale;
    private static Vector2 PixelAligned(Vector2 point) => new(MathF.Floor(point.X), MathF.Floor(point.Y));
    private float FontWindowScale => TypographyScale.WindowScale(C.Scale, p.FontRasterScale, p.BodyFontReady);

    internal void ShowSettings(int page = -1)
    {
        if (page >= 0) settingsTab = page;
        OpenCompanionPage(CompanionView.Settings);
    }
    private void TogglePanel() => SetPanelOpen(!WorkspaceOpen);
    private void SetPanelOpen(bool open)
    {
        dutyPartyPending = false; dutyPartyUntil = default; collapsedAfterDuty = false;
        if (!open && p.BrowseAll) p.Post(() => p.SetBrowseAll(false));
        var mode = open ? RibbonMode.Open : RibbonMode.Collapsed;
        if (C.WindowMode != mode) { C.Compact = !open; C.WindowMode = mode; p.Post(p.Save); }
    }
    internal void OpenParty() => ShowCompanionView(CompanionView.Party);
    internal void ShowDutyParty() => dutyPartyPending = C.DutyPartySeconds > 0;
    internal void DismissDutyParty()
    {
        if (!DutyPreviewPending) return;
        dutyPartyPending = false; dutyPartyUntil = default; collapsedAfterDuty = true;
    }
    internal void UpdateDutyPreview(DateTimeOffset now, bool pulling)
    {
        if (!DutyPreviewPending) return;
        if (pulling || C.DutyPartySeconds == 0 || !dutyPartyPending && now >= dutyPartyUntil) DismissDutyParty();
        else if (dutyPartyPending && p.CurrentParty is not null)
        {
            MainOpen = true;
            companionView = CompanionView.Party;
            companionBack.Clear();
        }
    }
    private void ShowCompanionView(CompanionView view)
    {
        if (WorkspaceOpen && companionView == view) return;
        MainOpen = true;
        if (view != CompanionView.Matches && p.BrowseAll) p.Post(() => p.SetBrowseAll(false));
        companionBack.Clear();
        companionView = view;
        SetPanelOpen(true);
    }
    private void OpenCompanionPage(CompanionView view)
    {
        if (companionView != view)
        {
            if (companionBack.Contains(view)) { while (companionBack.Pop() != view) { } }
            else companionBack.Push(companionView);
            companionView = view;
        }
        MainOpen = true;
        SetPanelOpen(true);
    }
    private void BackToCompanion()
    {
        if (companionView == CompanionView.AllMatches && p.BrowseAll) p.Post(() => p.SetBrowseAll(false));
        companionView = companionBack.TryPop(out var previous) ? previous : CompanionView.Matches;
    }
    private void OpenListingPage(Listing listing)
    {
        openedListing = listing;
        OpenCompanionPage(CompanionView.Listing);
    }
    private void PageHeading(string title, Action? back = null)
    {
        if (Icon(FontAwesomeIcon.ArrowLeft, "back", "Back")) (back ?? BackToCompanion)();
        ImGui.SameLine(); ImGui.AlignTextToFramePadding(); ItemTitle(title);
    }

    internal void Draw()
    {
        using var font = p.BodyFont.Push();
        var viewport = ImGui.GetMainViewport().Pos;
        var detail = p.View.Detail;
        NativeFinderHovered = p.View.Open && AnchorPlacement.FinderHovered(ImGui.GetMousePos(),
            p.View.FinderPosition + viewport, p.View.FinderSize, (detail?.Position ?? Vector2.Zero) + viewport, detail?.Size ?? Vector2.Zero);
        if (NativeFinderHovered) lastFinderHover = DateTimeOffset.UtcNow;
        using var chrome = WorkspaceTheme.PushWindowColors();
        using var colors = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero)
            .Push(ImGuiCol.FrameBgHovered, Theme.PrimaryHover with { W = .32f }).Push(ImGuiCol.FrameBgActive, Theme.PrimaryActive with { W = .30f })
            .Push(ImGuiCol.TableHeaderBg, Theme.Overlay).Push(ImGuiCol.TableRowBg, Vector4.Zero)
            .Push(ImGuiCol.TableRowBgAlt, new Vector4(1, 1, 1, .035f))
            .Push(ImGuiCol.ScrollbarBg, Theme.Surface).Push(ImGuiCol.ScrollbarGrab, Theme.Divider)
            .Push(ImGuiCol.ScrollbarGrabHovered, Accent).Push(ImGuiCol.ScrollbarGrabActive, Accent)
            .Push(ImGuiCol.SliderGrab, Muted).Push(ImGuiCol.SliderGrabActive, Accent);
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(12) * Scale)
            .Push(ImGuiStyleVar.FramePadding, new Vector2(8, 5) * Scale)
            .Push(ImGuiStyleVar.ItemSpacing, new Vector2(8, 4) * Scale)
            .Push(ImGuiStyleVar.CellPadding, new Vector2(4, 3) * Scale)
            .Push(ImGuiStyleVar.WindowRounding, 4 * Scale)
            .Push(ImGuiStyleVar.ChildRounding, 4 * Scale)
            .Push(ImGuiStyleVar.FrameRounding, 4 * Scale)
            .Push(ImGuiStyleVar.FrameBorderSize, 0)
            .Push(ImGuiStyleVar.TabRounding, 4 * Scale);
        var previousScale = Material.ScaleFactor;
        Material.ScaleFactor = C.Scale;
        try { Material.WithPalette(Theme, DrawWindows); }
        finally { Material.ScaleFactor = previousScale; }
        if (!fontRefreshQueued && p.FontScaleNeedsRefresh && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            fontRefreshQueued = true;
            p.Post(() => { try { p.RefreshFonts(); } finally { fontRefreshQueued = false; } });
        }
    }

    private void DrawWindows()
    {
        if (p.View.Open) Anchor();
        FollowNativeParty();
        if (companionView == CompanionView.Offer && CurrentOffer is null) BackToCompanion();
        if (p.Alert.Target is null) autoOpenedOffer = 0;
        if (MainOpen && !EditingSetup && C.Alerts && C.OpenBetterMatch == true && p.Alert.Target is { } target && target.Listing.Id != autoOpenedOffer)
        {
            autoOpenedOffer = target.Listing.Id;
            OpenCompanionPage(CompanionView.Offer);
        }
        if (MainOpen) Main();
    }

    private void Anchor()
    {
        var viewport = ImGui.GetMainViewport();
        var size = new Vector2(112, 30) * Scale;
        if (!AnchorPlacement.TryPlace(p.View.FinderPosition + viewport.Pos, p.View.FinderSize, viewport.Pos, viewport.Size, size, 6 * Scale, out var position)) return;
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero)
            .Push(ImGuiStyleVar.WindowMinSize, Vector2.Zero).Push(ImGuiStyleVar.WindowBorderSize, 0);
        ImGui.SetNextWindowPos(position);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        if (ImGui.Begin("##PartyScoutAnchor", ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            ImGui.SetWindowFontScale(FontWindowScale);
            if (ImGui.Button("##openPartyScout", size))
            {
                if (Mode == RibbonMode.Collapsed) TogglePanel();
                p.Post(p.Open);
            }
            var min = ImGui.GetItemRectMin();
            const string label = "Party Scout";
            var textSize = ImGui.CalcTextSize(label);
            var left = Math.Max(3 * Scale, (size.X - textSize.X - 25 * Scale) / 2);
            DrawLogo(min + new Vector2(left, 6 * Scale), 18 * Scale);
            ImGui.GetWindowDrawList().AddText(new Vector2(MathF.Floor(min.X + left + 25 * Scale), MathF.Floor(min.Y + (size.Y - textSize.Y) / 2)),
                ImGui.GetColorU32(Theme.Text), label);
        }
        ImGui.End();
    }

    private static void MatchParentFontSize(float parentFontSize)
    {
        // ImGui 1.88 only inherits the immediate parent's window scale.
        ImGui.SetWindowFontScale(1);
        ImGui.SetWindowFontScale(parentFontSize / ImGui.GetFontSize());
    }
    private void SectionTitle(string text)
    {
        using var font = p.HeadingFont.Push();
        OneLine(text.ToUpperInvariant(), Accent);
    }
    private void ItemTitle(string text) { using var font = p.HeadingFont.Push(); OneLine(text, Theme.Text); }
    private void PartyHeading(string duty, string fill)
    {
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var mark = 16 * Scale;
        var line = ImGui.GetTextLineHeight();
        var height = Math.Max(mark, line);
        var gap = 8 * Scale;
        var fillWidth = ImGui.CalcTextSize(fill).X;
        ImGui.Dummy(new Vector2(width, height));
        DrawLogo(PixelAligned(start + new Vector2(0, (height - mark) / 2)), mark);
        var y = MathF.Floor(start.Y + (height - line) / 2);
        var draw = ImGui.GetWindowDrawList();
        using (p.HeadingFont.Push())
            draw.AddText(PixelAligned(new(start.X + mark + gap, y)), ImGui.GetColorU32(Theme.Text),
                Fit(duty, Math.Max(0, width - mark - fillWidth - 2 * gap)));
        draw.AddText(PixelAligned(new(start.X + width - fillWidth, y)), ImGui.GetColorU32(Muted), fill);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(duty);
    }
    private static void FieldLabel(string text) => ImGui.TextDisabled(text);
    private void SecondaryText(string text)
    {
        using var colour = ImRaii.PushColor(ImGuiCol.Text, Muted);
        ImGui.TextWrapped(text);
    }
    private void FieldValue(string label, string value)
    {
        FieldLabel(label); ImGui.SameLine(); OneLine(value, Theme.Text);
    }
    private void DrawLogo(Vector2 position, float size, Vector4? tint = null)
    {
        var draw = ImGui.GetWindowDrawList();
        var ink = ImGui.GetColorU32(tint ?? Theme.Text);
        Vector2 At(float x, float y) => position + new Vector2(x, y) * size;
        var stroke = Math.Max(1, size * .075f);
        draw.AddCircle(At(.5f, .5f), size * .37f, ink, 32, stroke);
        draw.AddLine(At(.5f, .03f), At(.5f, .19f), ink, stroke);
        draw.AddLine(At(.5f, .81f), At(.5f, .97f), ink, stroke);
        draw.AddLine(At(.03f, .5f), At(.19f, .5f), ink, stroke);
        draw.AddLine(At(.81f, .5f), At(.97f, .5f), ink, stroke);
        foreach (var radius in (ReadOnlySpan<float>)[.16f, .27f])
        {
            draw.PathArcTo(At(.5f, .49f), size * radius, -MathF.PI / 2, -.12f, 10);
            draw.PathStroke(ink, ImDrawFlags.None, stroke);
        }
        foreach (var x in (ReadOnlySpan<float>)[.32f, .5f, .68f])
        {
            draw.AddCircleFilled(At(x, .65f), size * .06f, ink);
            draw.AddCircleFilled(At(x, .79f), size * .09f, ink);
        }
        draw.AddTriangleFilled(At(.23f, .60f), At(.40f, .60f), At(.32f, .45f), ink);
        draw.AddTriangleFilled(At(.62f, .62f), At(.65f, .55f), At(.68f, .63f), ink);
        draw.AddTriangleFilled(At(.68f, .63f), At(.72f, .55f), At(.75f, .62f), ink);
    }
    private bool Icon(FontAwesomeIcon icon, string id, string tooltip)
    {
        var clicked = Material.IconButton(id, icon, p.IconFont, MaterialIconButtonSize.Small,
            color: ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
        ScoutTooltip(id + "Hint", tooltip);
        return clicked;
    }

    private static int SecondsLeft(DateTimeOffset until) => Math.Max(0, (int)Math.Ceiling((until - DateTimeOffset.UtcNow).TotalSeconds));

    private string MatchingJobs(Match match) => string.Join(" · ", match.PlayableJobs
        .OrderByDescending(match.RankForJob).ThenBy(job => job)
        .Select(job => p.JobNames([job]) + (match.JobPreferences.ContainsKey(job) ? " ★" : "")));

    private string OfferNotice() => p.Joining ? $"Joining as {p.JobNames([p.SwitchingJob])}…"
        : p.Alert.Deadline is { } end ? $"Auto-join as {p.JobNames([p.Alert.TargetJob])} in {SecondsLeft(end)}s"
        : "Better match found";

    private void DismissOffer()
    {
        p.Post(p.DismissAlert);
        if (companionView == CompanionView.Offer) BackToCompanion();
    }

    private void DrawOffer(Match target)
    {
        var listing = target.Listing;
        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        var rosterWidth = ImGui.CalcTextSize("Roster").X + 20 * Scale;
        PageHeading("Better match");
        if (companionView != CompanionView.Offer) return;
        ImGui.SameLine(right - rosterWidth);
        if (ScoutButton("Roster", new Vector2(rosterWidth, IconSide), small: true)) OpenListingPage(listing);

        var job = p.Alert.Deadline.HasValue ? p.Alert.TargetJob : p.JoinJob(target);
        var notice = p.Joining || p.Alert.Deadline.HasValue ? OfferNotice()
            : !p.Active ? "Start Watch before joining."
            : listing.Parties != 1 ? "Only standard parties can be joined."
            : job == 0 ? "No matching job has a saved gear set." : null;
        var font = ImGui.GetFontSize();
        var footer = IconSide + ImGui.GetStyle().ItemSpacing.Y + 4 * Scale;
        if (ImGui.BeginChild("OfferDetails", new Vector2(0, -footer), false))
        {
            MatchParentFontSize(font);
            PartyHeading(p.DutyName(listing), $"{listing.Filled}/{listing.Total}");
            SectionTitle("Matching jobs");
            ImGui.TextWrapped(MatchingJobs(target));
            if (notice is not null) OneLine(notice, Gold);
            if (listing.Description.Length > 0) ImGui.TextWrapped(listing.Description);
            SlotStrip(listing, p.Collected(listing.Id), target);
            RecruitmentLine(listing);
            if (target.ProfileNames.Length > 0) FieldValue("Search", string.Join(" / ", target.ProfileNames));
        }
        ImGui.EndChild();

        var dismiss = p.Joining ? "Cancel switch" : p.Alert.Deadline.HasValue ? "Cancel auto-join" : "Dismiss";
        if (ScoutButton(dismiss, small: true)) DismissOffer();
        ImGui.SameLine();
        using (ImRaii.Disabled(!p.Active || p.Joining || job == 0 || listing.Parties != 1))
        {
            var label = p.View.Party.InParty ? "Leave & join" : "Join";
            if (job != 0) label += $" as {p.JobNames([job])}";
            if (ScoutButton(label, new Vector2(-1, IconSide), small: true, variant: MaterialButtonVariant.Contained))
                p.Post(() => p.BeginJoin(target, selectedJob: job));
        }
    }
    private static readonly string[] SettingsSections = ["Alerts", "Ranking", "Display", "Player data", "Diagnostics"];

    private void Settings()
    {
        for (var idx = 0; idx < SettingsSections.Length; idx++)
        {
            var section = (SettingsPage)idx;
            var expanded = settingsTab == idx;
            if (Material.Accordion("settings" + idx, SettingsSections[idx], ref expanded, () => SettingsSection(section), false, true))
                settingsTab = expanded ? idx : -1;
        }
        if (p.SaveError is { } error) { ImGui.TextWrapped(error); if (ScoutButton("Retry save")) p.Post(p.Save); }
    }

    private void SettingsSection(SettingsPage section)
    {
        var changed = false;
        switch (section)
        {
            case SettingsPage.Alerts:
                void Notifications()
                {
                    if (PreferenceSwitch("Match alerts", ref C.Alerts)) p.Post(p.AlertsChanged);
                    using var muted = ImRaii.Disabled(!C.Alerts);
                    if (PreferenceSwitch("Slide in new matches", ref C.Toast)) p.Post(p.AlertsChanged);
                    var popup = C.OpenBetterMatch == true;
                    if (PreferenceSwitch("Open better matches automatically", ref popup)) { C.OpenBetterMatch = popup; changed = true; }
                    changed |= PreferenceSwitch("Play a sound", ref C.Sound);
                    using (ImRaii.Disabled(!C.Sound))
                    {
                        ImGui.SetNextItemWidth(160 * Scale); changed |= ScoutSlider("Sound", ref C.SoundId, 1, 16);
                        ImGui.SameLine(); if (Icon(FontAwesomeIcon.VolumeUp, "soundPreview", "Preview notification sound")) p.Post(() => NativePartyFinder.PlaySound(C.SoundId));
                    }
                }
                SectionTitle("Notifications"); Notifications();
                ImGui.Spacing(); ScoutDivider(); ImGui.Spacing();
                SectionTitle("Auto-join");
                ImGui.SetNextItemWidth(160 * Scale);
                changed |= ScoutSlider("Seconds to respond", ref C.JoinDelay, 10, 300);
                SecondaryText("Start Auto-join in the companion when you want it for this session. It can equip a gear set and leave your current party.");
                break;
            case SettingsPage.Ranking:
                SectionTitle("Role weights"); RankingSettings();
                break;
            case SettingsPage.Display:
                void Appearance()
                {
                    SectionTitle("Content");
                    var emphasis = (int)C.Emphasis;
                    ImGui.SetNextItemWidth(180 * Scale);
                    if (ScoutSelect("Party summary", ref emphasis, "Composition + fit", "Parses + progress", "Description")) { C.Emphasis = (ListingEmphasis)emphasis; changed = true; }
                    ImGui.SetNextItemWidth(140 * Scale); changed |= ScoutSlider("Preview (seconds)", ref C.DutyPartySeconds, 0, 120);
                    SecondaryText("The party preview closes when the pull countdown starts or the time expires. Set to 0 to disable.");
                    ImGui.Spacing(); SectionTitle("Size");
                    ImGui.SetNextItemWidth(180 * Scale); changed |= ScoutSlider("Element scale", ref C.Scale, .75f, 1.5f, "%.2f");
                    ImGui.Spacing(); SecondaryText("Drag the glyph to move it. Drag the panel corner to set its width and maximum height.");
                }
                Appearance();
                break;
            case SettingsPage.PlayerData:
                SectionTitle("Player data"); SecondaryText("Leave a field blank to keep its saved credential."); Credentials();
                break;
            case SettingsPage.Diagnostics:
                void Diagnostics()
                {
                    changed |= PreferenceSwitch("Debug diagnostics", ref C.Debug);
                    if (C.Debug)
                    {
                        ImGui.TextWrapped($"Active: {p.Active} · Auto: {p.Automation} · Joining: {p.Joining}\nNative PF: {p.View.Open} · Last refresh: {p.LastRefresh:HH:mm:ss} UTC\nFilter: {p.FilterError ?? "valid"}");
                        ImGui.TextWrapped(p.CollectionDiagnostics);
                        ImGui.TextDisabled(p.FontsReady ? "Text fonts ready." : p.FontsFailed ? "Text font unavailable; using a default font." : "Text fonts loading.");
                        ImGui.TextDisabled("Logs contain counts and errors; no player identity or listing text.");
                    }
                    ImGui.Spacing(); SecondaryText("Preferences save automatically. Monitoring and auto-join start paused after loading.");
                }
                SectionTitle("Troubleshooting"); Diagnostics();
                break;
        }
        if (changed) p.Post(p.Save);
    }

    private void RankingSettings()
    {
        SecondaryText(p.UsesJobPreferences ? "Fill and job preferences rank first. Role points break ties." : "Points per filled member. Prefer jobs in each search to use fill-based ranking.");
        foreach (var role in Enum.GetValues<Role>())
        {
            ImGui.SetNextItemWidth(160 * Scale);
            if (ScoutSlider(RoleName(role), ref C.RoleWeights[(int)role], 0, 100)) p.Post(p.Changed);
        }
        ImGui.TextDisabled("Player parses never affect ranking.");
    }

    internal void OpenContacts() => OpenCompanionPage(CompanionView.Contacts);

    private void Contacts()
    {
        using (ImRaii.Disabled(!p.Active || p.Joining))
            if (Material.Button(p.ContactActive ? "Stop contact scan" : "Scan contacts", p.Textures, p.ContactActive ? "stop" : "play_arrow",
                variant: MaterialButtonVariant.Outlined, buttonSize: MaterialButtonSize.Small)) p.Post(p.ContactActive ? p.StopScan : p.StartScan);
        ImGui.SameLine(); ImGui.TextDisabled(p.Active ? "Across all duties" : "Start Watch to scan contacts");
        if (p.ContactActive && p.View.Detail is not null)
            SecondaryText("Checking the current PF page. The full contact sweep resumes when you close the listing.");
        ImGui.Spacing(); ScoutDivider(); ImGui.Spacing();
        SectionTitle("Who to watch");
        ContactSources();
    }

    private void Credentials()
    {
        ImGui.Spacing(); ItemTitle("Tomestone · progression");
        FieldLabel(p.HasTomestone ? "Token saved" : "API token needed");
        ImGui.SetNextItemWidth(-1);
        Material.MaskedTextField("TomestoneKey", "Tomestone API token", ref tomestoneKey, 4096);

        ImGui.Spacing(); ScoutDivider(); ImGui.Spacing(); ItemTitle("FFLogs · performance");
        FieldLabel(p.HasFFLogs ? "Credentials saved" : "Client credentials needed");
        ImGui.SetNextItemWidth(-1);
        Material.MaskedTextField("FFLogsId", "FFLogs client ID", ref ffLogsId, 4096);

        ImGui.SetNextItemWidth(-1);
        Material.MaskedTextField("FFLogsSecret", "FFLogs client secret", ref ffLogsSecret, 4096);

        using (ImRaii.Disabled(p.LookupBusy))
        {
            if (ScoutButton("Save credentials", variant: MaterialButtonVariant.Contained))
            {
                var entered = new Credentials(tomestoneKey, ffLogsId, ffLogsSecret);
                p.Post(() => p.SaveCredentials(entered)); tomestoneKey = ffLogsId = ffLogsSecret = "";
            }
            ImGui.SameLine(); if (ScoutButton("Forget", small: true)) p.Post(p.ForgetCredentials);
            if (ScoutButton("Import from Passport Checker Reborn")) p.Post(p.ImportCredentials);
        }
        if (p.CredentialStatus.Length > 0) ImGui.TextWrapped(p.CredentialStatus);
        SecondaryText("While Watch is active, matched parties and your current party are checked automatically with configured providers. Checks send names, home worlds and duty; results stay in memory. Private or missing records stay unavailable.");
    }
}
