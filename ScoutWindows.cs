using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using PartyScout.Core;

namespace PartyScout;

internal sealed partial class ScoutWindows(Plugin p)
{
    private enum RibbonTab { Matches, Current, Setup, Inbox, Settings, Contacts }
    internal bool MainOpen, WorkspaceOpen;
    internal bool EditingSetup { get; private set; }
    internal bool NativeFinderHovered { get; private set; }
    internal bool NativeFinderInteracting => NativeFinderHovered || DateTimeOffset.UtcNow - lastFinderHover < TimeSpan.FromSeconds(2);
    private DateTimeOffset lastFinderHover;
    // The old full ribbon now opens as a peek; the workspace has independent geometry.
    internal RibbonMode Mode => C.WindowMode is RibbonMode.Half or RibbonMode.Open ? RibbonMode.Half : RibbonMode.Collapsed;
    private RibbonTab tab = RibbonTab.Matches;
    private int settingsTab;
    private string dutySearch = "";
    private int selectedProfile;
    private string tomestoneKey = "", ffLogsId = "", ffLogsSecret = "";
    private bool resize = true, positioned, focusWorkspace;
    private Vector2 lastPosition;
    private float companionWidth;
    private Vector2 dragDelta;
    private RibbonPalette palette = RibbonPalette.From(ColourScheme.Grey, 0);
    private Vector4 Accent => palette.Accent;
    private Vector4 Muted => palette.Muted;
    private static readonly Vector4 Gold = new(1, .81f, .42f, 1);
    private Configuration C => p.Settings;
    private float Scale => ImGuiHelpers.GlobalScale * C.Scale;

    internal void ShowSettings(int page = -1)
    {
        if (page >= 0) settingsTab = page;
        SelectTab(RibbonTab.Settings);
    }
    private void SetMode(RibbonMode mode)
    {
        if (Mode == mode) return;
        C.WindowMode = mode; C.Compact = mode == RibbonMode.Collapsed; resize = true;
        p.Post(p.Save);
    }
    private void SelectTab(RibbonTab selected)
    {
        tab = selected; WorkspaceOpen = true; focusWorkspace = true;
        if (selected == RibbonTab.Inbox) p.Inbox.ReadAll();
    }

    internal void Draw()
    {
        EditingSetup = false;
        var mouse = ImGui.GetMousePos();
        var finder = p.View.FinderPosition + ImGui.GetMainViewport().Pos;
        NativeFinderHovered = p.View.Open && mouse.X >= finder.X && mouse.Y >= finder.Y && mouse.X < finder.X + p.View.FinderSize.X && mouse.Y < finder.Y + p.View.FinderSize.Y;
        if (NativeFinderHovered) lastFinderHover = DateTimeOffset.UtcNow;
        palette = RibbonPalette.From(C.Theme, C.ThemeSeed);
        var background = palette.Background; background.W = C.Opacity;
        using var colors = ImRaii.PushColor(ImGuiCol.WindowBg, background)
            .Push(ImGuiCol.TitleBg, palette.Background).Push(ImGuiCol.TitleBgActive, palette.Panel)
            .Push(ImGuiCol.ChildBg, Vector4.Zero).Push(ImGuiCol.PopupBg, palette.Panel)
            .Push(ImGuiCol.Text, palette.Text).Push(ImGuiCol.TextDisabled, Muted)
            .Push(ImGuiCol.Border, palette.Border).Push(ImGuiCol.Separator, palette.Border)
            .Push(ImGuiCol.FrameBg, palette.Control).Push(ImGuiCol.FrameBgHovered, palette.Hover).Push(ImGuiCol.FrameBgActive, palette.Active)
            .Push(ImGuiCol.Button, palette.Control).Push(ImGuiCol.ButtonHovered, palette.Hover).Push(ImGuiCol.ButtonActive, palette.Active)
            .Push(ImGuiCol.Header, palette.Active).Push(ImGuiCol.HeaderHovered, palette.Hover).Push(ImGuiCol.HeaderActive, palette.Active)
            .Push(ImGuiCol.Tab, palette.Control).Push(ImGuiCol.TabHovered, palette.Hover).Push(ImGuiCol.TabActive, palette.Active)
            .Push(ImGuiCol.TableHeaderBg, palette.Control).Push(ImGuiCol.TableRowBg, Vector4.Zero)
            .Push(ImGuiCol.TableRowBgAlt, new Vector4(1, 1, 1, .035f))
            .Push(ImGuiCol.ScrollbarBg, palette.Background).Push(ImGuiCol.ScrollbarGrab, palette.Border)
            .Push(ImGuiCol.ScrollbarGrabHovered, Accent).Push(ImGuiCol.ScrollbarGrabActive, Accent)
            .Push(ImGuiCol.CheckMark, Accent).Push(ImGuiCol.SliderGrab, Muted).Push(ImGuiCol.SliderGrabActive, Accent);
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(12, 10) * Scale)
            .Push(ImGuiStyleVar.FramePadding, new Vector2(6, 4) * Scale)
            .Push(ImGuiStyleVar.ItemSpacing, new Vector2(6, C.RowSpacing) * Scale)
            .Push(ImGuiStyleVar.CellPadding, new Vector2(4, 3) * Scale)
            .Push(ImGuiStyleVar.WindowRounding, C.Rounding * Scale)
            .Push(ImGuiStyleVar.ChildRounding, C.Rounding * Scale)
            .Push(ImGuiStyleVar.FrameRounding, Math.Min(6, C.Rounding) * Scale)
            .Push(ImGuiStyleVar.FrameBorderSize, 0)
            .Push(ImGuiStyleVar.TabRounding, Math.Min(6, C.Rounding) * Scale);
        if (p.View.Open) Anchor();
        NativePartyOverlay();
        if (MainOpen) Main();
        if (WorkspaceOpen) Workspace();
        if (!EditingSetup && p.Alert.Target is { } target && (C.Alerts || p.Automation)) Alert(target);
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
            ImGui.SetWindowFontScale(C.Scale);
            if (ImGui.Button("      Party Scout", size)) p.Post(p.Open);
            DrawLogo(ImGui.GetItemRectMin() + new Vector2(5, 4) * Scale, 22 * Scale);
        }
        ImGui.End();
    }
    private void Main()
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(6, 6) * Scale)
            .Push(ImGuiStyleVar.ItemSpacing, new Vector2(4, Math.Min(6, C.RowSpacing)) * Scale)
            .Push(ImGuiStyleVar.WindowBorderSize, 0);
        using var transparent = ImRaii.PushColor(ImGuiCol.WindowBg, Vector4.Zero);
        var viewport = ImGui.GetMainViewport();
        var compact = Mode == RibbonMode.Collapsed;
        var size = new Vector2(340, compact ? 96 : 320) * Scale;
        if (compact && p.Alert.Deadline is not null) size.Y += 24 * Scale;
        ImGui.SetNextWindowSize(size, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(340 * Scale, compact ? size.Y : 230 * Scale),
            new Vector2(float.MaxValue, compact ? size.Y : float.MaxValue));
        if (resize && positioned) ImGui.SetNextWindowPos(lastPosition);
        if (!positioned) ImGui.SetNextWindowPos(viewport.WorkPos + new Vector2(Math.Max(0, viewport.WorkSize.X - size.X - 24 * Scale), 24 * Scale), ImGuiCond.FirstUseEver);
        if (ImGui.Begin($"Party Scout###PartyScoutCompanionV2{Mode}", ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (resize && positioned)
            {
                ImGui.SetWindowSize(new Vector2(Math.Max(340 * Scale, companionWidth), ImGui.GetWindowSize().Y));
                ImGui.SetWindowPos(AnchorPlacement.KeepTopLeft(lastPosition, ImGui.GetWindowSize(), viewport.WorkPos, viewport.WorkSize));
            }
            resize = false;
            ImGui.SetWindowFontScale(C.Scale);
            dragDelta = Vector2.Zero;
            var dockHeight = 2 * ImGui.GetFrameHeight() + 22 * Scale + ImGui.GetStyle().ItemSpacing.Y +
                (p.Alert.Deadline is null ? 0 : ImGui.GetTextLineHeightWithSpacing());
            var dockColor = palette.Panel; dockColor.W = C.Opacity;
            using (ImRaii.PushColor(ImGuiCol.ChildBg, dockColor))
            using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(8, 7) * Scale))
            {
                if (ImGui.BeginChild("MonitorDock", new Vector2(0, dockHeight), false,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysUseWindowPadding))
                {
                    ImGui.SetWindowFontScale(1);
                    if (ImGui.BeginTable("MonitorHeader", 5, ImGuiTableFlags.SizingStretchProp))
                    {
                        ImGui.TableSetupColumn("Menu", ImGuiTableColumnFlags.WidthFixed, 38 * Scale);
                        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch);
                        for (var i = 0; i < 3; i++) ImGui.TableSetupColumn("Action" + i, ImGuiTableColumnFlags.WidthFixed, 36 * Scale);
                        ImGui.TableNextColumn(); CompanionMenu();
                        ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding();
                        var best = p.OverviewMatches.FirstOrDefault();
                        OneLine(p.FilterError is not null || p.SaveError is not null ? "Needs attention" : !p.Active ? "Watch paused" :
                            best is null ? "Watching for parties" : p.DutyName(best.Listing) + $" · {best.Listing.Filled}/{best.Listing.Total}",
                            p.Active ? palette.Text : Muted);
                        if (best is not null && ImGui.IsItemHovered()) ImGui.SetTooltip(p.DutyName(best.Listing) + "\n" + best.Listing.Description);
                        ImGui.TableNextColumn(); InboxButton();
                        ImGui.TableNextColumn();
                        if (Icon(FontAwesomeIcon.ExternalLinkAlt, "workspace", "Open full workspace", new Vector2(ImGui.GetFrameHeight()))) SelectTab(tab);
                        ImGui.TableNextColumn();
                        if (Icon(compact ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronUp, "peek",
                            compact ? "Show matches below" : "Hide matches", new Vector2(ImGui.GetFrameHeight())))
                            SetMode(compact ? RibbonMode.Half : RibbonMode.Collapsed);
                        ImGui.EndTable();
                    }
                    SessionControls(); ImGui.SameLine(); FinderButton();
                    Countdown();
                }
                ImGui.EndChild();
            }
            if (!compact)
            {
                var panelColor = palette.Background; panelColor.W = C.Opacity;
                using (ImRaii.PushColor(ImGuiCol.ChildBg, panelColor))
                {
                    if (ImGui.BeginChild("Peek", Vector2.Zero, false, ImGuiWindowFlags.NoScrollbar |
                        ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysUseWindowPadding))
                    {
                        ImGui.SetWindowFontScale(1);
                        SectionTitle("Your matches");
                        var footerHeight = ImGui.GetFrameHeightWithSpacing();
                        var parentFontSize = ImGui.GetFontSize();
                        if (ImGui.BeginChild("PeekList", new Vector2(0, Math.Max(1, ImGui.GetContentRegionAvail().Y - footerHeight)), false))
                        {
                            MatchParentFontSize(parentFontSize);
                            if (p.FilterError is { } error) ImGui.TextWrapped(error);
                            if (p.OverviewMatches.Length == 0) EmptyMatches(showSearches: true);
                            ImGui.PushID("matches");
                            foreach (var match in p.OverviewMatches) ListingCard(match.Listing, match, compact: true);
                            ImGui.PopID();
                            FriendsOverview(compact: true);
                        }
                        ImGui.EndChild();
                        if (ImGui.Button("Browse all matches", new Vector2(-1, ImGui.GetFrameHeight()))) SelectTab(RibbonTab.Matches);
                    }
                    ImGui.EndChild();
                }
            }
            if (dragDelta != Vector2.Zero) ImGui.SetWindowPos(ImGui.GetWindowPos() + dragDelta);
            lastPosition = ImGui.GetWindowPos(); companionWidth = ImGui.GetWindowSize().X; positioned = true;
        }
        ImGui.End();
        if (!MainOpen) p.Post(() => p.Stop("Closed. Monitoring stopped."));
    }

    private void Workspace()
    {
        ImGui.SetNextWindowSize(new Vector2(800, 620) * Scale, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(640, 360) * Scale, new Vector2(float.MaxValue));
        if (focusWorkspace) { ImGui.SetNextWindowFocus(); focusWorkspace = false; }
        if (ImGui.Begin("Party Scout###PartyScoutWorkspaceV2", ref WorkspaceOpen, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.SetWindowFontScale(C.Scale);
            var fontSize = ImGui.GetFontSize();
            using (ImRaii.PushColor(ImGuiCol.ChildBg, palette.Panel))
            using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(8, 10) * Scale))
            {
                if (ImGui.BeginChild("WorkspaceNavigation", new Vector2(150 * Scale, 0), false, ImGuiWindowFlags.AlwaysUseWindowPadding))
                {
                    MatchParentFontSize(fontSize);
                    Logo(44 * Scale, coloured: true); ImGui.SameLine();
                    ImGui.BeginGroup();
                    ImGui.TextColored(Accent, "PARTY SCOUT");
                    ImGui.TextDisabled(p.Active ? "Watching" : "Paused");
                    ImGui.EndGroup();
                    NavigationHeading("DISCOVER");
                    WorkspaceLink(RibbonTab.Matches, "Matches");
                    WorkspaceLink(RibbonTab.Current, "Your party");
                    WorkspaceLink(RibbonTab.Inbox, p.Inbox.Unread ? "Inbox  •" : "Inbox");
                    WorkspaceLink(RibbonTab.Contacts, "Friends & contacts");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Configure contact scanning");
                    NavigationHeading("MANAGE");
                    WorkspaceLink(RibbonTab.Setup, "Search profiles");
                    WorkspaceLink(RibbonTab.Settings, "Settings");
                }
                ImGui.EndChild();
            }
            ImGui.SameLine();
            if (ImGui.BeginChild("WorkspaceMain", Vector2.Zero, false))
            {
                MatchParentFontSize(fontSize);
                var title = tab switch
                {
                    RibbonTab.Matches => "Matches", RibbonTab.Current => "Your party", RibbonTab.Setup => "Search profiles",
                    RibbonTab.Inbox => "Inbox", RibbonTab.Contacts => "Friends & contacts", _ => "Settings",
                };
                PageTitle(title);
                SessionControls(); ImGui.SameLine(); FinderButton();
                Countdown();
                ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
                if (tab == RibbonTab.Setup) { EditingSetup = true; Setup(); }
                else if (tab == RibbonTab.Settings) Settings();
                else
                {
                    var bodyFontSize = ImGui.GetFontSize();
                    if (ImGui.BeginChild("WorkspaceContent", Vector2.Zero, false))
                    {
                        MatchParentFontSize(bodyFontSize);
                        switch (tab)
                        {
                            case RibbonTab.Matches: Overview(); break;
                            case RibbonTab.Current: YourParty(); break;
                            case RibbonTab.Inbox: Inbox(); break;
                            case RibbonTab.Contacts: Contacts(); break;
                        }
                    }
                    ImGui.EndChild();
                }
            }
            ImGui.EndChild();
        }
        ImGui.End();
    }

    private void WorkspaceLink(RibbonTab value, string label)
    {
        var selected = tab == value;
        if (NavigationButton(label, value.ToString(), selected)) SelectTab(value);
    }

    private void NavigationHeading(string text)
    {
        ImGui.Spacing();
        ImGui.Indent(10 * Scale);
        FieldLabel(text);
        ImGui.Unindent(10 * Scale);
    }

    private bool NavigationButton(string label, string id, bool selected)
    {
        using var font = selected ? p.HeadingFont.Push() : null;
        using var colour = ImRaii.PushColor(ImGuiCol.Button, selected ? palette.Active : Vector4.Zero)
            .Push(ImGuiCol.Text, selected ? Accent : palette.Text);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ButtonTextAlign, new Vector2(0, .5f))
            .Push(ImGuiStyleVar.FramePadding, new Vector2(10, 4) * Scale);
        var fitted = Fit(label, Math.Max(1, ImGui.GetContentRegionAvail().X - 20 * Scale));
        var clicked = ImGui.Button(fitted + "##" + id, new Vector2(-1, ImGui.GetFrameHeight()));
        if (selected)
        {
            var min = ImGui.GetItemRectMin(); var max = ImGui.GetItemRectMax();
            ImGui.GetWindowDrawList().AddRectFilled(min + new Vector2(2, 5) * Scale,
                new Vector2(min.X + 4 * Scale, max.Y - 5 * Scale), ImGui.GetColorU32(Accent), 1 * Scale);
        }
        if (fitted != label && ImGui.IsItemHovered()) ImGui.SetTooltip(label);
        return clicked;
    }

    private void CompanionMenu()
    {
        var notice = p.FilterError ?? p.SaveError ?? p.ActionMessage;
        using (ImRaii.PushColor(ImGuiCol.Text, notice is not null ? Gold : p.Active ? palette.Good : Muted))
            if (ImGui.Button("##ScoutLogo", new Vector2(28 * Scale, ImGui.GetFrameHeight()))) ImGui.OpenPopup("ScoutMenu");
        var min = ImGui.GetItemRectMin(); var max = ImGui.GetItemRectMax();
        var side = Math.Min(max.X - min.X, max.Y - min.Y) - 4 * Scale;
        DrawLogo(min + (max - min - new Vector2(side)) / 2, side);
        ImGui.GetWindowDrawList().AddCircleFilled(max - new Vector2(3, 3) * Scale, 3 * Scale,
            ImGui.GetColorU32(notice is not null ? Gold : p.Active ? palette.Good : Muted));
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left)) dragDelta = ImGui.GetIO().MouseDelta;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(notice ?? "Party Scout · drag to move · click for views and close");
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) ImGui.OpenPopup("ScoutMenu");
        if (ImGui.BeginPopup("ScoutMenu"))
        {
            if (notice is not null) { ImGui.TextWrapped(notice); ImGui.Separator(); }
            if (ImGui.MenuItem("Search profiles")) SelectTab(RibbonTab.Setup);
            if (ImGui.MenuItem("Your party")) SelectTab(RibbonTab.Current);
            if (ImGui.MenuItem("Settings")) ShowSettings();
            if (ImGui.MenuItem("Friends and contacts")) OpenContacts();
            ImGui.Separator();
            if (ImGui.MenuItem("Close monitor and stop Watch")) MainOpen = false;
            ImGui.EndPopup();
        }
    }

    private void SessionControls()
    {
        if (Toggle(p.Active ? FontAwesomeIcon.Stop : FontAwesomeIcon.Play, "Watch", p.Active,
            p.Active ? "Stop monitoring and cancel actions" : "Start monitoring")) p.Post(p.ToggleMonitoring);
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Button, C.Alerts ? palette.Active : palette.Control)
            .Push(ImGuiCol.Text, C.Alerts ? Accent : Muted))
            if (Icon(C.Alerts ? FontAwesomeIcon.Bell : FontAwesomeIcon.BellSlash, "alerts", C.Alerts ? "Turn alerts off" : "Turn alerts on", new Vector2(ImGui.GetFrameHeight())))
            { C.Alerts = !C.Alerts; p.Post(p.AlertsChanged); }
        ImGui.SameLine();
        using (ImRaii.Disabled(!p.Active))
            if (Toggle(p.Automation ? FontAwesomeIcon.Stop : FontAwesomeIcon.Play, "Auto", p.Automation,
                p.Automation ? "Stop auto-join and cancel countdown" : "Enable timed auto-join for this session")) p.Post(p.ToggleAutomation);
    }
    private void FinderButton()
    {
        using (ImRaii.PushColor(ImGuiCol.Button, p.ShowNativeFinder ? palette.Active : palette.Control)
            .Push(ImGuiCol.Text, p.ShowNativeFinder ? Accent : Muted))
            if (Icon(p.ShowNativeFinder ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash, "nativeFinder",
                p.ShowNativeFinder ? "Hide Party Finder windows" : "Show Party Finder windows", new Vector2(ImGui.GetFrameHeight())))
                p.Post(p.ToggleNativeFinder);
    }
    private void InboxButton()
    {
        if (Icon(FontAwesomeIcon.Inbox, "inbox", "Notification inbox", new Vector2(ImGui.GetFrameHeight()))) SelectTab(RibbonTab.Inbox);
        if (p.Inbox.Unread) ImGui.GetWindowDrawList().AddCircleFilled(ImGui.GetItemRectMax() - new Vector2(4, 20) * Scale, 3 * Scale, ImGui.GetColorU32(palette.Good));
    }
    private void Countdown()
    {
        if (p.Alert.Deadline is { } end)
            ImGui.TextColored(Gold, $"Joining in {Math.Max(0, (int)Math.Ceiling((end - DateTimeOffset.UtcNow).TotalSeconds))}s · stop Auto to cancel");
    }
    private bool Toggle(FontAwesomeIcon icon, string label, bool active, string tooltip)
    {
        using var colour = ImRaii.PushColor(ImGuiCol.Text, active ? Accent : palette.Text);
        var clicked = ImGuiComponents.IconButtonWithText(icon, label, defaultColor: active ? palette.Active : null);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return clicked;
    }
    private static void MatchParentFontSize(float parentFontSize)
    {
        // ImGui 1.88 only inherits the immediate parent's window scale.
        ImGui.SetWindowFontScale(1);
        ImGui.SetWindowFontScale(parentFontSize / ImGui.GetFontSize());
    }
    private void PageTitle(string text) { using var font = p.HeadingFont.Push(); OneLine(text, palette.Text); }
    private void SectionTitle(string text) { using var font = p.HeadingFont.Push(); OneLine(text, palette.Text); }
    private void ItemTitle(string text) { using var font = p.HeadingFont.Push(); OneLine(text, palette.Text); }
    private static void FieldLabel(string text) => ImGui.TextDisabled(text);
    private void SecondaryText(string text)
    {
        using var colour = ImRaii.PushColor(ImGuiCol.Text, Muted);
        ImGui.TextWrapped(text);
    }
    private void FieldValue(string label, string value)
    {
        FieldLabel(label); ImGui.SameLine(); OneLine(value, palette.Text);
    }
    private bool SectionDisclosure(string label, ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None)
    {
        using var colour = ImRaii.PushColor(ImGuiCol.Text, Accent).Push(ImGuiCol.Header, Vector4.Zero);
        using var font = p.HeadingFont.Push();
        return ImGui.CollapsingHeader(label, flags);
    }
    private void DrawLogo(Vector2 position, float size)
    {
        var draw = ImGui.GetWindowDrawList();
        var ink = ImGui.GetColorU32(palette.Text);
        Vector2 At(float x, float y) => position + new Vector2(x, y) * size;
        var stroke = Math.Max(1, size * .075f);
        draw.AddCircle(At(.5f, .5f), size * .37f, ink, 32, stroke);
        draw.AddLine(At(.5f, .03f), At(.5f, .19f), ink, stroke);
        draw.AddLine(At(.5f, .81f), At(.5f, .97f), ink, stroke);
        draw.AddLine(At(.03f, .5f), At(.19f, .5f), ink, stroke);
        draw.AddLine(At(.81f, .5f), At(.97f, .5f), ink, stroke);
        foreach (var radius in new[] { .16f, .27f })
        {
            draw.PathArcTo(At(.5f, .49f), size * radius, -MathF.PI / 2, -.12f, 10);
            draw.PathStroke(ink, ImDrawFlags.None, stroke);
        }
        foreach (var x in new[] { .32f, .5f, .68f })
        {
            draw.AddCircleFilled(At(x, .65f), size * .06f, ink);
            draw.AddCircleFilled(At(x, .79f), size * .09f, ink);
        }
        draw.AddTriangleFilled(At(.23f, .60f), At(.40f, .60f), At(.32f, .45f), ink);
        draw.AddTriangleFilled(At(.62f, .62f), At(.65f, .55f), At(.68f, .63f), ink);
        draw.AddTriangleFilled(At(.68f, .63f), At(.72f, .55f), At(.75f, .62f), ink);
    }
    private void Logo(float size, bool coloured = false)
    {
        var position = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(size));
        if (coloured && p.Textures.GetFromManifestResource(typeof(Plugin).Assembly, "PartyScout.images.icon.png").GetWrapOrDefault() is { } logo)
            ImGui.GetWindowDrawList().AddImage(logo.Handle, position, position + new Vector2(size));
        else DrawLogo(position, size);
    }
    private static bool Icon(FontAwesomeIcon icon, string id, string tooltip, Vector2 size)
    {
        bool clicked;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph = icon.ToIconString();
            size.X = Math.Max(size.X, ImGui.CalcTextSize(glyph).X + 2 * ImGui.GetStyle().FramePadding.X);
            clicked = ImGui.Button(glyph + "##" + id, size);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return clicked;
    }
    private void Alert(Match target)
    {
        ImGui.SetNextWindowSize(new Vector2(440, 340) * Scale, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(380, 330) * Scale, new Vector2(float.MaxValue));
        var open = true;
        if (ImGui.Begin("Better party found###PartyScoutAlert", ref open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            ImGui.SetWindowFontScale(C.Scale);
            var job = p.JoinJob(target);
            var countdown = p.Alert.Deadline;
            var notice = countdown is { } end
                ? $"Auto-joining as {p.JobNames([p.Alert.TargetJob])} in {Math.Max(0, (int)Math.Ceiling((end - DateTimeOffset.UtcNow).TotalSeconds))}s · Dismiss cancels"
                : job == 0 ? "No available job has a saved gear set."
                : !p.Active ? "Start Watch before joining."
                : target.Listing.Parties != 1 ? "Only standard parties can be joined."
                : p.Joining ? "A party switch is already in progress." : null;
            var footerHeight = ImGui.GetFrameHeightWithSpacing() + 2 * ImGui.GetStyle().CellPadding.Y +
                (notice is null ? 0 : ImGui.GetTextLineHeightWithSpacing()) + 4 * Scale;
            var fontSize = ImGui.GetFontSize();
            if (ImGui.BeginChild("AlertDetails", new Vector2(0, -footerHeight), false))
            {
                MatchParentFontSize(fontSize);
                Logo(22 * Scale); ImGui.SameLine();
                ImGui.AlignTextToFramePadding(); SectionTitle(p.DutyName(target.Listing));
                var comparison = p.CurrentScore is { } score && score >= 0 ? $" · your party {score:0.#}" : "";
                OneLine($"{Plugin.MatchScoreLabel(target)}{comparison} · {target.Listing.Filled}/{target.Listing.Total} filled", Muted);
                ImGui.Spacing();
                SlotStrip(target.Listing, p.Collected(target.Listing.Id));
                ImGui.Spacing();
                FieldLabel("Party description");
                var description = string.IsNullOrWhiteSpace(target.Listing.Description) ? "No description provided." : target.Listing.Description;
                var width = Math.Max(1, ImGui.GetContentRegionAvail().X - 20 * Scale);
                var height = Math.Max(ImGui.GetTextLineHeight(), ImGui.CalcTextSize(description, false, width).Y) + 16 * Scale;
                using (ImRaii.PushColor(ImGuiCol.ChildBg, palette.Panel))
                using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(10, 8) * Scale))
                {
                    var detailFont = ImGui.GetFontSize();
                    if (ImGui.BeginChild("Description", new Vector2(0, height), false,
                        ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
                    {
                        MatchParentFontSize(detailFont);
                        ImGui.TextWrapped(description);
                    }
                    ImGui.EndChild();
                }
                ImGui.Spacing();
                FieldValue("Open for", p.JobNames(target.PlayableJobs));
                if (target.PreferredJobs.Length > 0)
                {
                    Symbol(FontAwesomeIcon.Star, Gold); ImGui.SameLine();
                    FieldValue("Preferred", p.JobNames(target.PreferredJobs));
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Preferred openings. The best available preference counts once.");
                }
                if (job != 0) FieldValue(job == p.View.CurrentJob ? "Joining as" : "Switching to", p.JobNames([job]));
                if (target.ProfileNames.Length > 0) FieldValue("Search", string.Join(" / ", target.ProfileNames));
            }
            ImGui.EndChild();
            if (notice is not null) OneLine(notice, Gold);
            if (ImGui.BeginTable("AlertActions", 3, ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextColumn();
                using (ImRaii.PushColor(ImGuiCol.Button, palette.Active))
                using (ImRaii.Disabled(!p.Active || p.Joining || job == 0 || target.Listing.Parties != 1))
                    if (ImGui.Button(p.View.Party.InParty ? "Leave & join" : "Join party", new Vector2(-1, ImGui.GetFrameHeight()))) p.Post(() => p.BeginJoin(target, selectedJob: job));
                ImGui.TableNextColumn();
                using (ImRaii.Disabled(!p.Active && !p.ContactActive))
                    if (ImGui.Button("View party", new Vector2(-1, ImGui.GetFrameHeight()))) p.Post(() => p.Inspect(target.Listing));
                ImGui.TableNextColumn();
                if (ImGui.Button("Dismiss", new Vector2(-1, ImGui.GetFrameHeight()))) p.Post(p.DismissAlert);
                ImGui.EndTable();
            }
        }
        ImGui.End();
        if (!open) p.Post(p.DismissAlert);
    }
    private void Settings()
    {
        var labels = new[] { "Alerts & join", "Appearance", "Player data", "Diagnostics" };
        if (ImGui.BeginTable("SettingsSections", 4, ImGuiTableFlags.SizingStretchSame))
        {
            for (var i = 0; i < labels.Length; i++)
            {
                ImGui.TableNextColumn();
                using var color = ImRaii.PushColor(ImGuiCol.Button, settingsTab == i ? palette.Active : palette.Control)
                    .Push(ImGuiCol.Text, settingsTab == i ? Accent : palette.Text);
                using var align = ImRaii.PushStyle(ImGuiStyleVar.ButtonTextAlign, new Vector2(0, .5f));
                var fitted = Fit(labels[i], Math.Max(1, ImGui.GetContentRegionAvail().X - 2 * ImGui.GetStyle().FramePadding.X));
                if (ImGui.Button(fitted + "##SettingsSection" + i, new Vector2(-1, ImGui.GetFrameHeight()))) settingsTab = i;
                if (fitted != labels[i] && ImGui.IsItemHovered()) ImGui.SetTooltip(labels[i]);
            }
            ImGui.EndTable();
        }
        ImGui.Separator();
        if (ImGui.BeginChild("SettingsBody", Vector2.Zero, false))
        {
            var changed = false;
            switch (settingsTab)
            {
                case 0:
                    EditingSetup = true;
                    SectionTitle("Notifications");
                    if (ImGui.Checkbox("Show alert window", ref C.Alerts)) p.Post(p.AlertsChanged);
                    changed |= ImGui.Checkbox("Show notification toast", ref C.Toast);
                    changed |= ImGui.Checkbox("Play a sound", ref C.Sound);
                    using (ImRaii.Disabled(!C.Sound))
                    {
                        ImGui.SetNextItemWidth(160 * Scale); changed |= ImGui.SliderInt("Sound", ref C.SoundId, 1, 16);
                        ImGui.SameLine(); if (Icon(FontAwesomeIcon.VolumeUp, "soundPreview", "Preview notification sound", new Vector2(ImGui.GetFrameHeight()))) p.Post(() => NativePartyFinder.PlaySound(C.SoundId));
                    }
                    ImGui.Spacing(); ImGui.Separator(); SectionTitle("Automatic joining");
                    if (ImGui.Checkbox("Join if I do not respond", ref C.TimedJoin))
                        p.Post(() => { p.Changed(); if (C.TimedJoin && p.Active && !p.Automation) p.ToggleAutomation(); });
                    using (ImRaii.Disabled(!C.TimedJoin))
                    {
                        ImGui.SetNextItemWidth(160 * Scale);
                        if (ImGui.SliderInt("Seconds to respond", ref C.JoinDelay, 10, 300)) p.Post(p.Changed);
                    }
                    SecondaryText("Auto must be started for each session. Stop Auto cancels the countdown. It may equip a saved gear set and leave your party; the target may fill before you join.");
                    ImGui.Spacing();
                    if (SectionDisclosure("Ranking · all searches"))
                    {
                        SecondaryText(p.UsesJobPreferences ? "Fill and job preferences rank first. Role points break ties." : "Points per filled member. Prefer jobs in each search to use fill-based ranking.");
                        foreach (var role in Enum.GetValues<Role>())
                        {
                            ImGui.SetNextItemWidth(160 * Scale);
                            if (ImGui.SliderInt(RoleName(role), ref C.RoleWeights[(int)role], 0, 100)) p.Post(p.Changed);
                        }
                        ImGui.TextDisabled("Player parses never affect ranking.");
                    }
                    break;
                case 1:
                    SectionTitle("Colour & density");
                    var scheme = (int)C.Theme;
                    ImGui.SetNextItemWidth(180 * Scale);
                    if (ImGui.Combo("Palette", ref scheme, "Slate\0Teal\0Adaptive\0")) { C.Theme = (ColourScheme)scheme; changed = true; }
                    if (C.Theme == ColourScheme.Adaptive)
                    {
                        var rgb = RibbonPalette.Rgb(C.ThemeSeed); var seed = new Vector3(rgb.X, rgb.Y, rgb.Z);
                        if (ImGui.ColorEdit3("Palette colour", ref seed, ImGuiColorEditFlags.NoInputs))
                        { C.ThemeSeed = (uint)(seed.X * 255) << 16 | (uint)(seed.Y * 255) << 8 | (uint)(seed.Z * 255); changed = true; }
                    }
                    var emphasis = (int)C.Emphasis;
                    ImGui.SetNextItemWidth(180 * Scale);
                    if (ImGui.Combo("Party summary", ref emphasis, "Composition + fit\0Parses + progress\0Description\0")) { C.Emphasis = (ListingEmphasis)emphasis; changed = true; }
                    ImGui.SetNextItemWidth(180 * Scale); changed |= ImGui.SliderFloat("Element scale", ref C.Scale, .75f, 1.5f, "%.2f");
                    ImGui.SetNextItemWidth(180 * Scale); changed |= ImGui.SliderFloat("Row spacing", ref C.RowSpacing, 2, 16, "%.0f");
                    ImGui.SetNextItemWidth(180 * Scale); changed |= ImGui.SliderFloat("Window opacity", ref C.Opacity, .65f, 1, "%.2f");
                    ImGui.SetNextItemWidth(180 * Scale); changed |= ImGui.SliderFloat("Corner rounding", ref C.Rounding, 0, 18, "%.0f");
                    ImGui.Spacing(); SecondaryText("Resize each window independently. The corner monitor stays in place when you open this workspace.");
                    break;
                case 2: Credentials(); break;
                case 3:
                    SectionTitle("Troubleshooting");
                    changed |= ImGui.Checkbox("Debug diagnostics", ref C.Debug);
                    if (C.Debug)
                    {
                        ImGui.TextWrapped($"Active: {p.Active} · Auto: {p.Automation} · Joining: {p.Joining}\nNative PF: {p.View.Open} · Last refresh: {p.LastRefresh:HH:mm:ss} UTC\nFilter: {p.FilterError ?? "valid"}");
                        ImGui.TextWrapped(p.CollectionDiagnostics);
                        ImGui.TextDisabled("Logs contain counts and errors; no player identity or listing text.");
                    }
                    ImGui.Spacing(); SecondaryText("Preferences save automatically. Monitoring and auto-join start paused after loading.");
                    break;
            }
            if (changed) { resize = true; p.Post(p.Save); }
            if (p.SaveError is { } error) { ImGui.TextWrapped(error); if (ImGui.Button("Retry save")) p.Post(p.Save); }
        }
        ImGui.EndChild();
    }

    internal void OpenContacts() => SelectTab(RibbonTab.Contacts);

    private void Contacts()
    {
        using (ImRaii.Disabled(!p.Active || p.Joining))
            if (Toggle(p.ContactActive ? FontAwesomeIcon.Stop : FontAwesomeIcon.Play, "Scan contacts", p.ContactActive,
                "Scans all duties alongside your searches while Watch is active.")) p.Post(p.ContactActive ? p.StopScan : p.StartScan);
        ImGui.SameLine(); ImGui.TextDisabled(p.Active ? "Across all duties" : "Start Watch to scan contacts");
        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
        SectionTitle("Who to watch");
        ContactSources();
    }

    private void Credentials()
    {
        SectionTitle("Connected services");
        ImGui.TextWrapped($"Tomestone: {(p.HasTomestone ? "configured" : "not configured")} · FFLogs: {(p.HasFFLogs ? "configured" : "not configured")}");
        SecondaryText("Blank fields keep saved credentials.");
        ImGui.Spacing(); FieldLabel("Tomestone · progression");
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##TomestoneKey", "Tomestone API token", ref tomestoneKey, 4096, ImGuiInputTextFlags.Password);
        ImGui.Spacing(); FieldLabel("FFLogs · performance");
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##FFLogsId", "FFLogs client ID", ref ffLogsId, 4096, ImGuiInputTextFlags.Password);
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##FFLogsSecret", "FFLogs client secret", ref ffLogsSecret, 4096, ImGuiInputTextFlags.Password);
        using (ImRaii.Disabled(p.LookupBusy))
        {
            if (ImGui.Button("Save credentials"))
            {
                var entered = new Credentials(tomestoneKey, ffLogsId, ffLogsSecret);
                p.Post(() => p.SaveCredentials(entered)); tomestoneKey = ffLogsId = ffLogsSecret = "";
            }
            if (ImGui.Button("Import from Passport Checker Reborn")) p.Post(p.ImportCredentials);
            ImGui.SameLine(); if (ImGui.SmallButton("Forget")) p.Post(p.ForgetCredentials);
        }
        if (p.CredentialStatus.Length > 0) ImGui.TextWrapped(p.CredentialStatus);
        SecondaryText("While Watch is active, matched parties and your current party are checked automatically with configured providers. Checks send names, home worlds and duty; results stay in memory. Private or missing records stay unavailable.");
    }
}
