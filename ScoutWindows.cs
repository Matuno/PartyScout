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
    private enum RibbonTab { Matches, Current, People, Setup, Selected, Inbox }
    internal bool MainOpen, SettingsOpen;
    internal bool EditingSetup { get; private set; }
    internal bool NativeFinderHovered { get; private set; }
    internal bool NativeFinderInteracting => NativeFinderHovered || DateTimeOffset.UtcNow - lastFinderHover < TimeSpan.FromSeconds(2);
    private DateTimeOffset lastFinderHover;
    internal RibbonMode Mode => C.WindowMode ?? RibbonMode.Collapsed;
    private RibbonTab tab = p.Settings.WindowMode == RibbonMode.Open ? RibbonTab.Setup : RibbonTab.Matches;
    private string dutySearch = "";
    private int selectedProfile;
    private string tomestoneKey = "", ffLogsId = "", ffLogsSecret = "";
    private bool resize = true, positioned;
    private Vector2 lastPosition, lastSize;
    private Vector2 dragDelta;
    private RibbonPalette palette = RibbonPalette.From(ColourScheme.Grey, 0);
    private Vector4 Accent => palette.Accent;
    private Vector4 Muted => palette.Muted;
    private static readonly Vector4 Gold = new(1, .81f, .42f, 1);
    private Configuration C => p.Settings;
    private float Scale => ImGuiHelpers.GlobalScale * C.Scale;

    internal void ShowParty() { MainOpen = true; tab = RibbonTab.Selected; SetMode(RibbonMode.Open); }
    private void SetMode(RibbonMode mode)
    {
        if (Mode == mode) return;
        C.WindowMode = mode; C.Compact = mode == RibbonMode.Collapsed; resize = true;
        if (mode != RibbonMode.Open && tab == RibbonTab.Setup) tab = RibbonTab.Matches;
        p.Post(p.Save);
    }
    private void SelectTab(RibbonTab selected)
    {
        tab = selected;
        if (selected == RibbonTab.Setup) SetMode(RibbonMode.Open);
        else if (Mode == RibbonMode.Collapsed) SetMode(RibbonMode.Half);
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
        if (MainOpen) Main();
        if (SettingsOpen) Settings();
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
            if (ImGui.Button("Party Scout", size)) p.Post(p.Open);
        }
        ImGui.End();
    }
    private void Main()
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(8, 6) * Scale)
            .Push(ImGuiStyleVar.ItemSpacing, new Vector2(4, C.RowSpacing) * Scale)
            .Push(ImGuiStyleVar.WindowBorderSize, 0);
        using var transparent = ImRaii.PushColor(ImGuiCol.WindowBg, Vector4.Zero);
        var viewport = ImGui.GetMainViewport();
        var compact = Mode == RibbonMode.Collapsed;
        var size = (Mode switch { RibbonMode.Collapsed => new Vector2(360, 52), RibbonMode.Half => new Vector2(438, 340), _ => new Vector2(552, 480) }) * Scale;
        if (compact && p.Alert.Deadline is not null) size.Y += 24 * Scale;
        if (resize || lastSize != size)
        {
            if (positioned)
                ImGui.SetNextWindowPos(AnchorPlacement.KeepTopLeft(lastPosition, size, viewport.WorkPos, viewport.WorkSize));
            ImGui.SetNextWindowSize(size);
            resize = false;
        }
        if (!positioned) ImGui.SetNextWindowPos(viewport.WorkPos + new Vector2(Math.Max(0, viewport.WorkSize.X - size.X - 24 * Scale), 24 * Scale), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Party Scout###PartyScoutMain", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.SetWindowFontScale(C.Scale);
            var available = ImGui.GetContentRegionAvail();
            dragDelta = Vector2.Zero;
            var dockHeight = ImGui.GetFrameHeight() + 12 * Scale + (p.Alert.Deadline is null ? 0 : ImGui.GetTextLineHeightWithSpacing());
            var dockColor = palette.Panel; dockColor.W = C.Opacity;
            using (ImRaii.PushColor(ImGuiCol.ChildBg, dockColor))
            {
                if (ImGui.BeginChild("ControlDock", new Vector2(Math.Min(344 * Scale, available.X), dockHeight), false,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
                {
                    ImGui.SetWindowFontScale(1);
                    ImGui.SetCursorPos(new Vector2(6, 6) * Scale);
                    Controls();
                    ImGui.SameLine();
                    using (ImRaii.Disabled(Mode == RibbonMode.Collapsed))
                        if (Icon(FontAwesomeIcon.ChevronUp, "less", "Collapse one level", new Vector2(ImGui.GetFrameHeight())))
                            SetMode(Mode == RibbonMode.Open ? RibbonMode.Half : RibbonMode.Collapsed);
                    ImGui.SameLine();
                    using (ImRaii.Disabled(Mode == RibbonMode.Open))
                        if (Icon(FontAwesomeIcon.ChevronDown, "more", "Expand one level", new Vector2(ImGui.GetFrameHeight())))
                        { if (Mode == RibbonMode.Half) { SetMode(RibbonMode.Open); tab = RibbonTab.Setup; } else SetMode(RibbonMode.Half); }
                    if (p.Alert.Deadline is { } end)
                        ImGui.TextColored(Gold, $"Joining in {Math.Max(0, (int)Math.Ceiling((end - DateTimeOffset.UtcNow).TotalSeconds))}s · stop Auto to cancel");
                }
                ImGui.EndChild();
            }
            if (!compact)
            {
                var panelColor = palette.Background; panelColor.W = C.Opacity;
                using (ImRaii.PushColor(ImGuiCol.ChildBg, panelColor))
                {
                    if (ImGui.BeginChild("RibbonContent", new Vector2(available.X, Math.Max(1, ImGui.GetContentRegionAvail().Y)), false,
                        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysUseWindowPadding))
                    {
                        ImGui.SetWindowFontScale(1);
                        Navigation();
                        if (tab == RibbonTab.Setup) { EditingSetup = true; Setup(); }
                        else
                        {
                            if (ImGui.BeginChild("ContentScroll", Vector2.Zero, false))
                            {
                                ImGui.SetWindowFontScale(1);
                                switch (tab)
                                {
                                    case RibbonTab.Matches: Overview(); break;
                                    case RibbonTab.Current: YourParty(); break;
                                    case RibbonTab.People: People(); break;
                                    case RibbonTab.Selected: Party(); break;
                                    case RibbonTab.Inbox: Inbox(); break;
                                }
                            }
                            ImGui.EndChild();
                        }
                    }
                    ImGui.EndChild();
                }
            }
            if (dragDelta != Vector2.Zero) ImGui.SetWindowPos(ImGui.GetWindowPos() + dragDelta);
            lastPosition = ImGui.GetWindowPos(); lastSize = ImGui.GetWindowSize(); positioned = true;
        }
        ImGui.End();
        if (!MainOpen) p.Post(() => p.Stop("Closed. Monitoring stopped."));
    }

    private void Navigation()
    {
        if (tab is RibbonTab.Inbox or RibbonTab.Selected)
        {
            if (Icon(FontAwesomeIcon.ArrowLeft, "back", "Back to matches", new Vector2(ImGui.GetFrameHeight()))) tab = RibbonTab.Matches;
            ImGui.SameLine(); ImGui.TextColored(Accent, tab == RibbonTab.Inbox ? "INBOX" : "PARTY ROSTER");
        }
        else
        {
            var tabs = new[]
            {
                (RibbonTab.Matches, "Matches"),
                (RibbonTab.Current, "Your party"),
                (RibbonTab.People, "People"),
                (RibbonTab.Setup, "Setup"),
            };
            foreach (var (value, label) in tabs)
            {
                if (value != RibbonTab.Matches) ImGui.SameLine();
                var selected = tab == value;
                using var colour = ImRaii.PushColor(ImGuiCol.Button, selected ? palette.Active : palette.Control)
                    .Push(ImGuiCol.Text, selected ? Accent : palette.Text);
                if (ImGui.SmallButton(label + "##" + value)) SelectTab(value);
            }
        }
        ImGui.Separator();
    }

    private void Controls()
    {
        var notice = p.FilterError ?? p.SaveError ?? p.ActionMessage;
        using (ImRaii.PushColor(ImGuiCol.Text, notice is not null ? Gold : p.Active ? palette.Good : Muted))
            ImGui.Button("PS", new Vector2(28 * Scale, ImGui.GetFrameHeight()));
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left)) dragDelta = ImGui.GetIO().MouseDelta;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(notice ?? "Party Scout · drag to move · right-click for close and views");
        if (ImGui.BeginPopupContextItem("ScoutMenu"))
        {
            if (notice is not null) { ImGui.TextWrapped(notice); ImGui.Separator(); }
            if (ImGui.MenuItem("Search profiles")) SelectTab(RibbonTab.Setup);
            if (ImGui.MenuItem("Your party")) SelectTab(RibbonTab.Current);
            if (ImGui.MenuItem("Close and stop")) MainOpen = false;
            ImGui.EndPopup();
        }
        ImGui.SameLine();
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
        ImGui.SameLine();
        if (Icon(FontAwesomeIcon.Inbox, "inbox", "Notification inbox", new Vector2(ImGui.GetFrameHeight()))) SelectTab(RibbonTab.Inbox);
        if (p.Inbox.Unread) ImGui.GetWindowDrawList().AddCircleFilled(ImGui.GetItemRectMax() - new Vector2(4, 20) * Scale, 3 * Scale, ImGui.GetColorU32(palette.Good));
        ImGui.SameLine();
        if (Icon(FontAwesomeIcon.Cog, "settings", "Notification and appearance settings", new Vector2(ImGui.GetFrameHeight()))) SettingsOpen = true;
    }
    private bool Toggle(FontAwesomeIcon icon, string label, bool active, string tooltip)
    {
        using var colour = ImRaii.PushColor(ImGuiCol.Text, active ? Accent : palette.Text);
        var clicked = ImGuiComponents.IconButtonWithText(icon, label, defaultColor: active ? palette.Active : null);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return clicked;
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
        ImGui.SetNextWindowSize(new Vector2(420, 0) * Scale, ImGuiCond.Appearing);
        var open = true;
        if (ImGui.Begin("Better party found###PartyScoutAlert", ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            ImGui.SetWindowFontScale(C.Scale);
            ImGui.TextColored(Accent, p.DutyName(target.Listing));
            ImGui.TextUnformatted($"{target.Score} points{(p.CurrentScore >= 0 ? $"  ·  your party {p.CurrentScore}" : "")}  ·  {target.Listing.Filled}/{target.Listing.Total}");
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 380 * Scale); ImGui.TextWrapped(target.Listing.Description); ImGui.PopTextWrapPos();
            ImGui.TextWrapped("Open for " + p.JobNames(target.PlayableJobs));
            ImGui.TextWrapped("Profiles: " + string.Join(" · ", target.ProfileNames));
            if (ImGui.SmallButton("Inspect party")) p.Post(() => p.Inspect(target.Listing));
            if (p.Alert.Deadline is { } end) ImGui.TextColored(Gold, $"Joining in {Math.Max(0, (int)Math.Ceiling((end - DateTimeOffset.UtcNow).TotalSeconds))}s");
            if (!target.PlayableJobs.Contains(p.View.CurrentJob)) ImGui.TextWrapped("Change to a compatible job before joining.");
            using (ImRaii.Disabled(p.Joining || !target.PlayableJobs.Contains(p.View.CurrentJob) || target.Listing.Parties != 1))
                if (ImGui.Button(p.View.Party.InParty ? "Leave current & join" : "Join party")) p.Post(() => p.BeginJoin(target));
            ImGui.SameLine(); if (ImGui.Button("Dismiss")) p.Post(p.DismissAlert);
        }
        ImGui.End();
        if (!open) p.Post(p.DismissAlert);
    }
    private void Settings()
    {
        ImGui.SetNextWindowSize(new Vector2(460, 440) * Scale, ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Party Scout · Settings###PartyScoutSettings", ref SettingsOpen, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.SetWindowFontScale(C.Scale);
            ImGui.TextColored(Accent, "NOTIFICATIONS");
            var changed = ImGui.Checkbox("Dalamud notification toast", ref C.Toast);
            changed |= ImGui.Checkbox("Play notification sound", ref C.Sound);
            ImGui.SetNextItemWidth(180 * Scale); changed |= ImGui.SliderInt("Chat sound", ref C.SoundId, 1, 16);
            if (ImGui.SmallButton("Preview sound")) p.Post(() => NativePartyFinder.PlaySound(C.SoundId));
            ImGui.Separator(); ImGui.TextColored(Accent, "APPEARANCE");
            var scheme = (int)C.Theme;
            ImGui.SetNextItemWidth(180 * Scale);
            if (ImGui.Combo("Colour scheme", ref scheme, "Slate\0Teal\0Adaptive\0"))
            { C.Theme = (ColourScheme)scheme; changed = true; }
            if (C.Theme == ColourScheme.Adaptive)
            {
                var rgb = RibbonPalette.Rgb(C.ThemeSeed);
                var seed = new Vector3(rgb.X, rgb.Y, rgb.Z);
                if (ImGui.ColorEdit3("Palette colour", ref seed, ImGuiColorEditFlags.NoInputs))
                { C.ThemeSeed = (uint)(seed.X * 255) << 16 | (uint)(seed.Y * 255) << 8 | (uint)(seed.Z * 255); changed = true; }
            }
            var emphasis = (int)C.Emphasis;
            ImGui.SetNextItemWidth(180 * Scale);
            if (ImGui.Combo("Listing focus", ref emphasis, "Composition + fit\0Parses + progress\0Description\0"))
            { C.Emphasis = (ListingEmphasis)emphasis; changed = true; }
            ImGui.SetNextItemWidth(180 * Scale); changed |= ImGui.SliderFloat("Element scale", ref C.Scale, .75f, 1.5f, "%.2f");
            ImGui.SetNextItemWidth(180 * Scale); changed |= ImGui.SliderFloat("Corner rounding", ref C.Rounding, 0, 18, "%.0f");
            ImGui.SetNextItemWidth(180 * Scale); changed |= ImGui.SliderFloat("Row spacing", ref C.RowSpacing, 2, 16, "%.0f");
            ImGui.SetNextItemWidth(180 * Scale); changed |= ImGui.SliderFloat("Window opacity", ref C.Opacity, .65f, 1, "%.2f");
            ImGui.Separator(); Credentials();
            ImGui.Separator(); changed |= ImGui.Checkbox("Debug diagnostics", ref C.Debug);
            if (C.Debug)
            {
                ImGui.TextWrapped($"Active: {p.Active} · Auto: {p.Automation} · Joining: {p.Joining}\nNative PF: {p.View.Open} · Last refresh: {p.LastRefresh:HH:mm:ss} UTC\nFilter: {p.FilterError ?? "valid"}");
                ImGui.TextWrapped(p.CollectionDiagnostics);
                ImGui.TextDisabled("Logs contain counts and errors; no player identity or listing text.");
            }
            if (changed) { resize = true; p.Post(p.Save); }
            if (p.SaveError is { } error) { ImGui.TextWrapped(error); if (ImGui.Button("Retry save")) p.Post(p.Save); }
            ImGui.TextWrapped("Preferences save automatically; credentials use Save. Monitoring and auto-join start paused after loading.");
        }
        ImGui.End();
    }

    private void Credentials()
    {
        ImGui.TextColored(Accent, "PLAYER LOOKUPS");
        ImGui.TextWrapped($"Tomestone: {(p.HasTomestone ? "configured" : "not configured")} · FFLogs: {(p.HasFFLogs ? "configured" : "not configured")}");
        ImGui.TextDisabled("Saved in normal Dalamud configuration. Blank fields keep existing values.");
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##TomestoneKey", "Tomestone API token", ref tomestoneKey, 4096, ImGuiInputTextFlags.Password);
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
        ImGui.TextWrapped("While Watch is active, matched parties and your current party are checked automatically with configured providers. Checks send names, home worlds and duty; results stay in memory. Private or missing records stay unavailable.");
    }
}
