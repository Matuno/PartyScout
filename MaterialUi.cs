using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using DalamudUi;
using PartyScout.Core;

namespace PartyScout;

internal sealed partial class ScoutWindows
{
    private float IconSide => 28 * Scale;
    private float? companionMotion;

    private void Main()
    {
        var view = ImGui.GetMainViewport();
        var side = 40 * Scale;
        var size = new Vector2(side);
        var position = Vector2.Zero;
        var glyphWindow = default(ImGuiWindowPtr);
        var reveal = 0f;
        var reducedMotion = Material.PrefersReducedMotion;
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero)
            .Push(ImGuiStyleVar.WindowMinSize, Vector2.Zero).Push(ImGuiStyleVar.WindowBorderSize, 0))
        {
            ImGui.SetNextWindowSize(size);
            ImGui.SetNextWindowPos(view.WorkPos + new Vector2(Math.Max(0, view.WorkSize.X - side - 24 * Scale), 24 * Scale), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("Party Scout###PartyScoutCompanion", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground |
                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoFocusOnAppearing))
            {
                glyphWindow = ImGuiP.GetCurrentWindow();
                ImGui.SetWindowFontScale(FontWindowScale);
                var current = ImGui.GetWindowPos();
                position = AnchorPlacement.KeepTopLeft(current, size, view.WorkPos, view.WorkSize);
                if (position != current) ImGui.SetWindowPos(position);
                var clicked = ImGui.InvisibleButton("Scout glyph", size);
                if (ImGui.IsItemActivated()) draggingGlyph = false;
                if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                {
                    draggingGlyph = true;
                    position = AnchorPlacement.KeepTopLeft(position + ImGui.GetIO().MouseDelta, size, view.WorkPos, view.WorkSize);
                    ImGui.SetWindowPos(position);
                }
                if (clicked && !draggingGlyph) TogglePanel();
                if (ImGui.IsItemDeactivated()) draggingGlyph = false;
                var hovered = ImGui.IsItemHovered();
                if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                using var popupPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(12) * Scale);
                if (hovered && !ImGui.IsMouseDragging(ImGuiMouseButton.Left)) ScoutTooltip("glyphHint", $"Party Scout · {(p.Active ? "Watching" : "Paused")}\nClick to {(WorkspaceOpen ? "collapse" : "expand")} · drag to move · right-click for controls");
                if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) ImGui.OpenPopup("ScoutMenu");
                if (ImGui.BeginPopup("ScoutMenu"))
                {
                    if (ImGui.MenuItem(p.Active ? "Pause watching" : "Start watching")) p.Post(p.ToggleMonitoring);
                    if (ImGui.MenuItem("Auto-join", "", p.Automation, p.Active)) p.Post(p.ToggleAutomation);
                    if (ImGui.MenuItem("Match alerts", "", C.Alerts)) ToggleAlerts();
                    if (ImGui.MenuItem("Show native Party Finder", "", p.ShowNativeFinder)) p.Post(p.ToggleNativeFinder);
                    ImGui.Separator();
                    if (ImGui.MenuItem("All matches")) OpenCompanionPage(CompanionView.AllMatches);
                    if (ImGui.MenuItem("Search profiles")) ShowCompanionView(CompanionView.SearchProfiles);
                    if (ImGui.MenuItem("Your party")) ShowCompanionView(CompanionView.Party);
                    if (ImGui.MenuItem("Friends & contacts")) OpenContacts();
                    ImGui.Separator();
                    if (ImGui.MenuItem("Settings")) ShowSettings();
                    if (ImGui.MenuItem("Close companion and stop watching")) { DismissDutyParty(); MainOpen = false; }
                    ImGui.EndPopup();
                }
                var target = WorkspaceOpen ? 1f : 0f;
                var step = ImGui.GetIO().DeltaTime / .18f;
                reveal = reducedMotion || companionMotion is not { } previous ? target :
                    Math.Clamp(previous + (target == 1 ? step : -step), 0, 1);
                companionMotion = reveal;
                var draw = ImGui.GetWindowDrawList();
                if (reveal == 0)
                    draw.AddRectFilled(position, position + size, ImGui.GetColorU32(Theme.Surface), 8 * Scale);
                var ink = p.Active ? Accent : Muted with { W = .68f };
                DrawLogo(position + new Vector2(6 * Scale), 28 * Scale, hovered ? Theme.Text : ink);
                if (p.Active)
                {
                    var angle = reducedMotion ? -MathF.PI / 2 : (float)(ImGui.GetTime() % 4 / 4 * Math.Tau);
                    draw.PathArcTo(position + size / 2, 17 * Scale, angle, angle + MathF.PI / 2, 16);
                    draw.PathStroke(ImGui.GetColorU32(Theme.Success), ImDrawFlags.None, 1.5f * Scale);
                }
            }
            ImGui.End();
        }
        if (!MainOpen) { p.Post(() => p.Stop("Closed. Monitoring stopped.")); return; }
        var bounds = reveal > 0 ? CompanionPanel(position, side, reveal, glyphWindow) : (position, position + size);
        var bannerY = position.Y + side / 2;
        if (!EditingSetup && p.Active && CurrentOffer is { } offer && (!WorkspaceOpen || companionView != CompanionView.Offer) &&
            (p.Alert.Deadline.HasValue || p.Joining || C.Alerts && C.Toast && p.Banners.Current?.Listing.Id == offer.Listing.Id))
            OfferBanner(offer, bounds, bannerY, side);
        else if (C.Alerts && C.Toast && !EditingSetup && p.Active && !p.Joining && p.Banners.Current is { } banner &&
            !(banner.Count == 1 && companionView == CompanionView.Offer && p.Alert.Target?.Listing.Id == banner.Listing.Id))
            SlideBanner(banner, bounds, bannerY, side);
    }

    private void ToggleAlerts() { C.Alerts = !C.Alerts; p.Post(p.AlertsChanged); }

    private static readonly MaterialPalette CompanionToolbar = WorkspaceTheme.Dark with { ButtonPaddingSmall = 8 };
    private string WatchLabel => p.Active ? "Watching" : "Paused";
    private float CompanionHeaderHeight => 16 * Scale + IconSide;
    private float MatchRowHeight => Math.Max(48 * Scale, 2 * ImGui.GetTextLineHeight() + 8 * Scale);
    private float ContactRowHeight => ImGui.GetTextLineHeight() + 4 * Scale;

    private float CompanionButtonWidth(string label, bool icon = false) =>
        Math.Max(64 * Scale, ImGui.CalcTextSize(label).X + (2 * CompanionToolbar.ButtonPaddingSmall + (icon ? 24 : 0)) * Scale);

    private bool WrapCompanionTools(float width) =>
        CompanionButtonWidth(WatchLabel, true) + CompanionButtonWidth("Auto-join", true) + 3 * IconSide + 16 * Scale > width - 56 * Scale;

    private float CompanionControlsHeight(float width) => (76 + (WrapCompanionTools(width) ? 32 : 0)) * Scale;

    private (Vector2 Min, Vector2 Max) CompanionPanel(Vector2 glyph, float side, float reveal, ImGuiWindowPtr glyphWindow)
    {
        var view = ImGui.GetMainViewport();
        var cardGap = 8 * Scale;
        var room = Vector2.Max(glyph + new Vector2(side) - view.WorkPos, view.WorkPos + view.WorkSize - glyph);
        var minWidth = Math.Min(344 * Scale, room.X);
        var preferredWidth = Math.Clamp(panelSize == Vector2.Zero ? 368 * Scale : panelSize.X, minWidth, room.X);
        var minHeight = CompanionControlsHeight(preferredWidth) + 2 * cardGap + CompanionHeaderHeight / CompanionLayout.ContactsShare;
        var min = new Vector2(minWidth, Math.Min(MathF.Ceiling(minHeight), room.Y));
        var firstSize = Vector2.Clamp(new Vector2(368, 360) * Scale, min, room);
        var size = panelSize == Vector2.Zero ? firstSize : Vector2.Clamp(panelSize, min, room);
        var moving = reveal < 1;
        var holdingMouse = !moving && ImGui.IsMouseDown(ImGuiMouseButton.Left) && !draggingGlyph;
        var desiredHeight = companionView == CompanionView.Matches ? panelContentHeight : panelHeightLimit;
        if (desiredHeight > 0 && !holdingMouse)
        {
            size.Y = Math.Clamp(Math.Min(desiredHeight, panelHeightLimit), min.Y, room.Y);
            if (size != panelSize) ImGui.SetNextWindowSize(size);
        }
        else ImGui.SetNextWindowSize(firstSize, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(min, room);
        var positioning = holdingMouse ? ImGuiCond.Appearing : ImGuiCond.Always;
        ImGui.SetNextWindowPos(AnchorPlacement.CompanionPanel(glyph, side, size, view.WorkPos, view.WorkSize), positioning);
        using var layout = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero)
            .Push(ImGuiStyleVar.WindowBorderSize, 0)
            .Push(ImGuiStyleVar.ItemSpacing, new Vector2(8) * Scale);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (moving) flags |= ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoResize;
        var clipMin = glyph;
        var clipMax = glyph + new Vector2(side);
        if (ImGui.Begin("Party Scout###PartyScoutPeek", flags))
        {
            ImGuiP.BringWindowToDisplayBehind(ImGuiP.GetCurrentWindow(), glyphWindow);
            ImGui.SetWindowFontScale(FontWindowScale);
            var position = ImGui.GetWindowPos();
            var actual = ImGui.GetWindowSize();
            if (panelHeightLimit == 0) panelHeightLimit = Math.Max(firstSize.Y, actual.Y);
            else if (holdingMouse && actual.Y != panelSize.Y) panelHeightLimit = actual.Y;
            panelSize = actual;
            var opensUp = position.Y < glyph.Y;
            var controlsHeight = CompanionControlsHeight(actual.X);
            var header = position + new Vector2(0, opensUp ? actual.Y - controlsHeight : 0);
            var extent = new Vector2(actual.X, controlsHeight);
            var across = Math.Clamp(reveal / .75f, 0, 1);
            var down = Math.Clamp((reveal - .18f) / .82f, 0, 1);
            var amount = new Vector2(across * (2 - across), down * (2 - down));
            clipMin = glyph + (position - glyph) * amount;
            clipMax = glyph + new Vector2(side) + (position + actual - glyph - new Vector2(side)) * amount;
            ImGui.PushClipRect(clipMin, clipMax, true);
            using var alpha = ImRaii.PushStyle(ImGuiStyleVar.DisabledAlpha, 1);
            using var quiet = ImRaii.Disabled(moving);
            var backgroundMin = Vector2.Max(header, clipMin);
            var backgroundMax = Vector2.Min(header + extent, clipMax);
            if (backgroundMax.X > backgroundMin.X && backgroundMax.Y > backgroundMin.Y)
                ImGui.GetWindowDrawList().AddRectFilled(backgroundMin, backgroundMax, ImGui.GetColorU32(Theme.Surface), 8 * Scale);
            ImGui.SetCursorScreenPos(header);
            CompanionCard("CompanionControls", controlsHeight,
                () => DrawCompanionControls(WrapCompanionTools(actual.X), glyph, side, opensUp, controlsHeight), transparent: true);

            var body = position + new Vector2(0, opensUp ? 0 : controlsHeight + cardGap);
            ImGui.SetCursorScreenPos(body);
            if (companionView is CompanionView.Matches or CompanionView.AllMatches)
            {
                var heightLimit = holdingMouse ? actual.Y : Math.Clamp(panelHeightLimit, min.Y, room.Y);
                DrawCompanionMatches(controlsHeight, heightLimit, cardGap, opensUp ? header.Y - cardGap : null);
            }
            else
            {
                var height = Math.Max(1, actual.Y - controlsHeight - cardGap);
                CompanionCard("Companion" + companionView, height, DrawCompanionPage,
                    scroll: companionView is not (CompanionView.EditSearch or CompanionView.ChooseDuty or CompanionView.Offer));
            }
            ImGui.PopClipRect();
        }
        ImGui.End();
        return (clipMin, clipMax);
    }

    private void DrawCompanionPage()
    {
        switch (companionView)
        {
            case CompanionView.Party: YourParty(); break;
            case CompanionView.SearchProfiles: Setup(); break;
            case CompanionView.Listing: ListingPage(); break;
            case CompanionView.Contacts: PageHeading("Friends & contacts"); Contacts(); break;
            case CompanionView.ContactResults:
                PageHeading("Found contacts");
                var hits = p.SocialHits;
                if (hits.Length == 0) SecondaryText("No contacts found.");
                else ContactParties(hits.GroupBy(hit => hit.Listing.Id), ContactRowHeight);
                break;
            case CompanionView.ContactScan: PageHeading("Scan details"); ContactScanDetails(); break;
            case CompanionView.Settings: PageHeading("Settings"); Settings(); break;
            case CompanionView.EditSearch: EditSearchPage(); break;
            case CompanionView.ChooseDuty: ChooseDutyPage(); break;
            case CompanionView.NewGroup: NewGroupPage(); break;
            case CompanionView.DeleteSearch: DeleteSearchPage(); break;
            case CompanionView.Offer:
                if (CurrentOffer is { } offer) DrawOffer(offer);
                break;
        }
    }

    private void CompanionCard(string id, float height, Action content, bool scroll = false, bool transparent = false)
    {
        var font = ImGui.GetFontSize();
        using var style = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(12, 8) * Scale)
            .Push(ImGuiStyleVar.ChildRounding, 8 * Scale);
        using var surface = ImRaii.PushColor(ImGuiCol.ChildBg, transparent ? Vector4.Zero : Theme.Surface);
        var flags = ImGuiWindowFlags.AlwaysUseWindowPadding;
        if (!scroll) flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (ImGui.BeginChild(id, new Vector2(0, height), false, flags))
        {
            using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(8, 4) * Scale);
            MatchParentFontSize(font);
            content();
        }
        ImGui.EndChild();
    }

    private void DrawCompanionControls(bool wrap, Vector2 glyph, float side, bool opensUp, float height) => Material.WithPalette(CompanionToolbar, () =>
    {
        var start = ImGui.GetCursorScreenPos();
        var right = start.X + ImGui.GetContentRegionAvail().X;
        var dockedRight = glyph.X > ImGui.GetWindowPos().X;
        var toolLeft = dockedRight ? start.X : glyph.X + side + 4 * Scale;
        var toolRight = dockedRight ? glyph.X - 4 * Scale : right;
        ImGui.SetCursorScreenPos(new Vector2(toolLeft, glyph.Y + (side - 30 * Scale) / 2));
        if (Material.Button(WatchLabel + "###Watch", p.Textures, p.Active ? "stop" : "play_arrow",
            variant: MaterialButtonVariant.Outlined, buttonSize: MaterialButtonSize.Small,
            color: p.Active ? MaterialControlColor.Success : MaterialControlColor.Secondary)) p.Post(p.ToggleMonitoring);
        ScoutTooltip("watchHint", p.Active ? "Pause watching" : "Start watching Party Finder");
        ImGui.SameLine(0, 4 * Scale);
        if (Material.Button("Auto-join", p.Textures, p.Automation ? "stop" : "play_arrow",
            variant: p.Automation ? MaterialButtonVariant.Contained : MaterialButtonVariant.Text,
            buttonSize: MaterialButtonSize.Small, color: p.Automation ? MaterialControlColor.Warning : MaterialControlColor.Secondary,
            disabled: !p.Active)) p.Post(p.ToggleAutomation);
        ScoutTooltip("autoHint", p.Automation ? "Stop auto-join and cancel the countdown" : "Enable timed auto-join");
        var utilityX = (wrap ? right : toolRight) - (3 * IconSide + 8 * Scale);
        var utilityY = glyph.Y + (side - IconSide) / 2 + (wrap ? (opensUp ? -32 * Scale : side) : 0);
        ImGui.SetCursorScreenPos(new Vector2(utilityX, utilityY));
        using (ImRaii.PushColor(ImGuiCol.Text, C.Alerts ? Accent : Muted))
            if (Icon(C.Alerts ? FontAwesomeIcon.Bell : FontAwesomeIcon.BellSlash, "alerts", C.Alerts ? "Mute match alerts" : "Enable match alerts")) ToggleAlerts();
        ImGui.SameLine(0, 4 * Scale);
        if (Icon(p.ShowNativeFinder ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash, "finder",
            p.ShowNativeFinder ? "Hide the native list during scans" : "Show the native list during scans")) p.Post(p.ToggleNativeFinder);
        ImGui.SameLine(0, 4 * Scale);
        if (Icon(FontAwesomeIcon.Cog, "settings", "Settings")) ShowSettings();

        ImGui.SetCursorScreenPos(new Vector2(start.X, ImGui.GetWindowPos().Y + (opensUp ? 8 * Scale : height - 34 * Scale)));
        var tabWidth = Math.Max(3, ImGui.GetContentRegionAvail().X - 8 * Scale);
        var sideWidth = Math.Max(1, MathF.Floor(.3f * tabWidth));
        CompanionTab("Matches", CompanionView.Matches, sideWidth);
        ImGui.SameLine(0, 4 * Scale);
        CompanionTab("Search profiles", CompanionView.SearchProfiles, tabWidth - 2 * sideWidth);
        ImGui.SameLine(0, 4 * Scale);
        CompanionTab("Your party", CompanionView.Party, sideWidth);
    });

    private void CompanionTab(string label, CompanionView view, float width)
    {
        var fitted = Fit(label, Math.Max(0, width - 8 * Scale));
        var active = companionView <= CompanionView.Party ? companionView : companionBack.FirstOrDefault(page => page <= CompanionView.Party);
        if (Material.Button(fitted + "###" + label, MaterialButtonVariant.Text,
            active == view ? MaterialControlColor.Primary : MaterialControlColor.Secondary,
            size: new Vector2(width, 26 * Scale))) ShowCompanionView(view);
        if (fitted != label) ScoutTooltip(label, label);
        if (active != view) return;
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        ImGui.GetWindowDrawList().AddLine(new Vector2(min.X, max.Y - Scale), new Vector2(max.X, max.Y - Scale), ImGui.GetColorU32(Accent), 2 * Scale);
    }

    private void DrawCompanionMatches(float controlsHeight, float heightLimit, float cardGap, float? bottom)
    {
        var matches = p.OverviewMatches;
        var hits = p.SocialHits;
        var contacts = hits.GroupBy(hit => hit.Listing.Id).ToArray();
        var found = hits.DistinctBy(hit => (hit.Member.Name, hit.Member.World)).Count();
        var header = CompanionHeaderHeight;
        var rowGap = 4 * Scale;
        var available = heightLimit - controlsHeight - 2 * cardGap;
        var contactRow = ContactRowHeight;
        var contactBody = C.CompanionContactsExpanded
            ? CompanionLayout.ContactBodyHeight(hits.Length, contacts.Length, available, header, contactRow, rowGap) : 0;
        var contactHeight = header + (contactBody > 0 ? rowGap + contactBody : 0);
        var matchBudget = available - contactHeight;

        var status = p.SaveError ?? p.ActionMessage ?? p.FilterError;
        var empty = matches.Length == 0 && status is null ? EmptyMatchesMessage() : null;
        var line = ImGui.GetTextLineHeight() + rowGap;
        var showStatus = (status ?? empty) is not null && matchBudget >= header + line;
        var reviewOffer = p.Alert.Target is { } offer && C.Alerts && C.OpenBetterMatch != true && !p.Automation
            ? offer.Listing.Id : 0;
        var showReview = reviewOffer != 0 && matchBudget >= header + (showStatus ? line : 0) + IconSide + rowGap;
        var matchHeader = header + (showStatus ? line : 0) + (showReview ? IconSide + rowGap : 0);
        var matchRow = MatchRowHeight;
        var matchCount = CompanionLayout.FitRows(matches.Length, matchBudget, matchHeader, matchRow, rowGap);
        var showMatches = matchBudget >= header;
        var allMatches = companionView == CompanionView.AllMatches;
        var matchHeight = !showMatches ? 0 : allMatches ? matchBudget : matchHeader + matchCount * (matchRow + rowGap);
        if (bottom is { } edge)
        {
            var start = ImGui.GetCursorScreenPos();
            ImGui.SetCursorScreenPos(start with { Y = Math.Max(start.Y, edge - contactHeight - (showMatches ? matchHeight + cardGap : 0)) });
        }
        if (allMatches && showMatches)
        {
            CompanionCard("CompanionAllMatches", matchHeight, () => { PageHeading("All matches"); OverviewAll(); }, scroll: true);
        }
        else if (showMatches)
        {
            CompanionCard("CompanionMatches", matchHeight, () =>
            {
                DrawMatchCardHeading();
                if (showStatus) OneLine((status ?? empty)!, Muted);
                if (showReview && ScoutButton("Review better match", small: true)) OpenCompanionPage(CompanionView.Offer);
                for (var i = 0; i < matchCount; i++) ListingSummary(matches[i]);
            });
        }
        CompanionCard("CompanionContacts", contactHeight, () =>
        {
            DrawContactCardHeading(found);
            if (contactBody <= 0) return;
            var font = ImGui.GetFontSize();
            if (ImGui.BeginChild("ContactResults", new Vector2(0, contactBody), false))
            {
                MatchParentFontSize(font);
                ContactParties(contacts, contactRow);
            }
            ImGui.EndChild();
        });
        panelContentHeight = MathF.Ceiling(controlsHeight + 2 * cardGap +
            Math.Max(matchHeight + contactHeight, contactHeight / CompanionLayout.ContactsShare));
    }

    private void DrawMatchCardHeading()
    {
        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        var buttonWidth = ImGui.CalcTextSize("All matches").X + 24 * Scale;
        ImGui.AlignTextToFramePadding();
        using (p.HeadingFont.Push()) OneLine("YOUR MATCHES", Accent, buttonWidth + 4 * Scale);
        ImGui.SameLine(right - buttonWidth);
        if (ScoutButton("All matches", new Vector2(buttonWidth, IconSide), small: true)) OpenCompanionPage(CompanionView.AllMatches);
    }

    private void DrawContactCardHeading(int count)
    {
        var start = ImGui.GetCursorScreenPos();
        var right = start.X + ImGui.GetContentRegionAvail().X;
        var result = $"{count} found";
        var width = ImGui.CalcTextSize(result).X + (count > 0 ? 2 * Theme.ButtonPaddingSmall * Scale : 0);
        var expanded = C.CompanionContactsExpanded;
        var actions = expanded ? 2 * IconSide + 4 * Scale : IconSide;
        var inset = width + actions + 8 * Scale;
        ImGui.BeginGroup();
        var textY = start.Y + Math.Max(0, (IconSide - ImGui.GetTextLineHeight()) / 2);
        ImGui.SetCursorScreenPos(new Vector2(start.X, textY));
        using (p.HeadingFont.Push()) OneLine("FRIENDS & CONTACTS", Accent, inset);
        ImGui.SetCursorScreenPos(new Vector2(right - inset + 4 * Scale, count > 0 ? start.Y : textY));
        if (count > 0)
        {
            if (ScoutButton(result, new Vector2(width, IconSide), small: true)) OpenCompanionPage(CompanionView.ContactResults);
            ScoutTooltip("contactResults", "View all found contacts");
        }
        else
        {
            ImGui.TextColored(Muted, result);
            if (!p.ContactActive) ScoutTooltip("contactStatus", "Contact scan paused.");
        }
        if (expanded)
        {
            ImGui.SetCursorScreenPos(new Vector2(right - actions, start.Y));
            if (Icon(FontAwesomeIcon.SlidersH, "people", "Manage contact scanning")) OpenContacts();
        }
        ImGui.SetCursorScreenPos(new Vector2(right - IconSide, start.Y));
        if (Icon(expanded ? FontAwesomeIcon.ChevronUp : FontAwesomeIcon.ChevronDown, "contactsDisclosure",
            expanded ? "Collapse contacts" : "Expand contacts"))
        {
            C.CompanionContactsExpanded = !expanded;
            p.Post(p.Save);
        }
        ImGui.EndGroup();
    }

    private (Vector2 Position, float Width, float ContentX, float FullWidth) CompanionBanner(
        (Vector2 Min, Vector2 Max) bounds, float centerY, float height, float desiredWidth, float progress)
    {
        var view = ImGui.GetMainViewport();
        var end = view.WorkPos + view.WorkSize;
        var gap = 4 * Scale;
        var strip = AnchorPlacement.BannerStrip(bounds.Min.X, bounds.Max.X - bounds.Min.X,
            view.WorkPos.X, end.X, gap, desiredWidth, progress);
        if (strip.FullWidth >= 4 * IconSide)
        {
            var y = Math.Clamp(centerY - height / 2, view.WorkPos.Y, Math.Max(view.WorkPos.Y, end.Y - height));
            return (new(strip.X, y), strip.Width, strip.ContentX, strip.FullWidth);
        }
        var full = Math.Min(desiredWidth, view.WorkSize.X);
        var spaceBelow = end.Y - bounds.Max.Y;
        var spaceAbove = bounds.Min.Y - view.WorkPos.Y;
        var top = spaceBelow >= height + gap || spaceBelow >= spaceAbove ? bounds.Max.Y + gap : bounds.Min.Y - height - gap;
        var position = AnchorPlacement.KeepTopLeft(new(bounds.Min.X, top), new(full, height), view.WorkPos, view.WorkSize);
        return (position, MathF.Floor(full * progress), position.X, full);
    }

    private void OfferBanner(Match offer, (Vector2 Min, Vector2 Max) bounds, float centerY, float side)
    {
        var height = Math.Max(side, 2 * ImGui.GetTextLineHeight() * FontWindowScale + 14 * Scale);
        var strip = CompanionBanner(bounds, centerY, height, 326 * Scale, 1);
        if (strip.Width < 4 * IconSide) return;
        using var style = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(8, 5) * Scale)
            .Push(ImGuiStyleVar.WindowRounding, 6 * Scale).Push(ImGuiStyleVar.WindowMinSize, Vector2.Zero).Push(ImGuiStyleVar.WindowBorderSize, 0);
        using var background = ImRaii.PushColor(ImGuiCol.WindowBg, Theme.Paper);
        ImGui.SetNextWindowPos(strip.Position);
        ImGui.SetNextWindowSize(new Vector2(strip.Width, height));
        if (ImGui.Begin("##PartyScoutOfferBanner", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNavFocus))
        {
            ImGui.SetWindowFontScale(FontWindowScale);
            ImGuiP.BringWindowToDisplayFront(ImGuiP.GetCurrentWindow());
            var start = ImGui.GetCursorPos();
            var right = start.X + ImGui.GetContentRegionAvail().X;
            var reviewWidth = ImGui.CalcTextSize("Review").X + 20 * Scale;
            var fullReview = strip.Width >= 216 * Scale;
            var actions = IconSide + 4 * Scale + (fullReview ? reviewWidth : IconSide);
            OneLine(OfferNotice(), Gold, actions + 4 * Scale);
            OneLine($"Jobs · {MatchingJobs(offer)}", Theme.Text, actions + 4 * Scale);
            ImGui.SetCursorPos(new Vector2(right - actions, (height - IconSide) / 2));
            if (fullReview ? ScoutButton("Review", new Vector2(reviewWidth, IconSide), small: true)
                : Icon(FontAwesomeIcon.ArrowRight, "review", "Review better match")) OpenCompanionPage(CompanionView.Offer);
            ImGui.SameLine(0, 4 * Scale);
            if (Icon(FontAwesomeIcon.Times, "dismiss", p.Joining ? "Cancel switch" : "Dismiss better match")) DismissOffer();
        }
        ImGui.End();
    }

    private void SlideBanner(MatchBanner banner, (Vector2 Min, Vector2 Max) bounds, float centerY, float side)
    {
        var age = (DateTimeOffset.UtcNow - banner.Started).TotalSeconds;
        if (age < 0 || age >= MatchBanners.Duration.TotalSeconds || banner.Listing.Expires <= DateTimeOffset.UtcNow) return;
        var t = (float)Math.Clamp(Math.Min(age, MatchBanners.Duration.TotalSeconds - age) / .22, 0, 1);
        var progress = Material.PrefersReducedMotion ? 1 : 1 - (1 - t) * (1 - t);
        var height = Math.Max(side, 2 * ImGui.GetTextLineHeight() * FontWindowScale + 8 * Scale);
        var strip = CompanionBanner(bounds, centerY, height, 266 * Scale, progress);
        if (strip.Width < 1) return;
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero)
            .Push(ImGuiStyleVar.WindowRounding, 6 * Scale).Push(ImGuiStyleVar.WindowMinSize, Vector2.Zero).Push(ImGuiStyleVar.WindowBorderSize, 0);
        using var background = ImRaii.PushColor(ImGuiCol.WindowBg, Theme.Paper);
        ImGui.SetNextWindowPos(strip.Position);
        ImGui.SetNextWindowSize(new Vector2(strip.Width, height));
        if (ImGui.Begin("##PartyScoutMatchBanner", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNavFocus))
        {
            ImGui.SetWindowFontScale(FontWindowScale);
            ImGuiP.BringWindowToDisplayFront(ImGuiP.GetCurrentWindow());
            if (ImGui.InvisibleButton("Open new matches", new Vector2(strip.Width, height)))
            {
                if (banner.Count > 1) OpenCompanionPage(CompanionView.AllMatches);
                else if (p.Alert.Target?.Listing.Id == banner.Listing.Id) OpenCompanionPage(CompanionView.Offer);
                else OpenListingPage(banner.Listing);
                p.Banners.Dismiss();
            }
            if (ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            var draw = ImGui.GetWindowDrawList();
            var line = ImGui.GetTextLineHeight();
            var pos = PixelAligned(new Vector2(strip.ContentX + 12 * Scale, strip.Position.Y + (height - 2 * line) / 2));
            var label = banner.Count == 1 ? "New match" : $"{banner.Count} new matches";
            var fill = $"{banner.Listing.Filled}/{banner.Listing.Total}";
            var fillWidth = ImGui.CalcTextSize(fill).X;
            draw.AddText(pos, ImGui.GetColorU32(Muted), label);
            draw.AddText(new Vector2(MathF.Floor(strip.ContentX + strip.FullWidth - 12 * Scale - fillWidth), pos.Y), ImGui.GetColorU32(Muted), fill);
            using (p.HeadingFont.Push())
                draw.AddText(pos + new Vector2(0, line), ImGui.GetColorU32(Theme.Text), Fit(p.DutyName(banner.Listing), Math.Max(0, strip.FullWidth - 24 * Scale)));
        }
        ImGui.End();
    }

    private bool PreferenceSwitch(string label, ref bool value) =>
        Material.Switch(label, ref value);

    private bool ScoutCheckbox(string label, ref bool value) =>
        Material.Checkbox(label, ref value);

    private bool ScoutButton(string label, Vector2? size = null, bool small = false, MaterialButtonVariant variant = MaterialButtonVariant.Text) =>
        Material.Button(label, variant, small ? MaterialButtonSize.Small : MaterialButtonSize.Medium,
            size: size ?? new Vector2(0, IconSide));

    private bool ScoutTextField(string id, string hint, ref string value, int maxLength, bool multiline = false) =>
        Material.TextField(id, hint, ref value, MaterialTextFieldVariant.Filled,
            small: true, multiline: multiline, rows: 2, maxLength: maxLength);

    private bool ScoutSelect(string label, ref int selected, params string[] options)
    {
        var values = options.Select(option => new MaterialSelectOption(option)).ToArray();
        var changed = Material.Select(label, values, ref selected, "Select", false, null, true);
        if (!label.StartsWith("##", StringComparison.Ordinal)) { ImGui.SameLine(); ImGui.TextUnformatted(label); }
        return changed;
    }

    private bool ScoutSlider(string label, ref int value, int min, int max)
    {
        var next = (float)value;
        var changed = Material.Slider(label, ref next, min, max, step: 1);
        if (changed) value = (int)MathF.Round(next);
        ImGui.SameLine(); ImGui.TextUnformatted($"{label}: {value}");
        return changed;
    }

    private bool ScoutSlider(string label, ref float value, float min, float max, string format)
    {
        var changed = Material.Slider(label, ref value, min, max, step: format == "%.0f" ? 1 : .01f);
        ImGui.SameLine(); ImGui.TextUnformatted($"{label}: {value.ToString(format == "%.0f" ? "0" : "0.00")}");
        return changed;
    }

    private static void ScoutDivider() => Material.Divider();
    private static readonly MaterialTooltipOptions ScoutTooltipOptions = new(Interactive: false);
    private static void ScoutTooltip(string id, string text) => Material.Tooltip(id, text, ScoutTooltipOptions);
}
