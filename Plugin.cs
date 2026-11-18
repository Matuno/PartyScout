using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Chat;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Game;
using Dalamud.Interface;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using PartyScout.Core;
using Action = System.Action;
using DutyCategory = PartyScout.Core.DutyCategory;
using Role = PartyScout.Core.Role;

namespace PartyScout;

public sealed partial class Plugin : IDalamudPlugin
{
    private readonly object sync = new();
    private readonly IDalamudPluginInterface pi;
    private readonly IFramework framework;
    private readonly IClientState client;
    private readonly IPlayerState player;
    private readonly ICondition conditions;
    private readonly IDutyState dutyState;
    private readonly IPartyFinderGui pf;
    private readonly ICommandManager commands;
    private readonly IPluginLog log;
    private readonly IChatGui chat;
    private readonly IToastGui toastGui;
    private readonly Dictionary<string, uint> quietPlateToastMessages = new(StringComparer.Ordinal);
    private readonly IAddonLifecycle addonLifecycle;
    private readonly NativePartyFinder native;
    internal ITextureProvider Textures { get; }
    internal (ushort Id, string Name)[] WorldChoices { get; }
    private readonly ScoutWindows windows;
    private readonly ConcurrentQueue<Action> actions = new();
    private readonly ConcurrentQueue<(Listing Listing, int Segment)> incoming = new();
    private readonly Dictionary<ulong, Listing> listings = [], batch = [];
    private readonly ICallGateProvider<string> listTests;
    private readonly ICallGateProvider<string, string> runTests;
    private readonly string assemblyHash;
    private readonly Dictionary<uint, int> dutySizes;
    private readonly Dictionary<uint, string> jobNames;
    private readonly Dictionary<uint, string> dutyNames;
    private ListingFilter filter;
    private DateTimeOffset nextRefresh, collectingUntil, nextEvaluation, statusUntil;
    private uint territory;
    private bool collecting, disposed;
    private volatile bool active, contactActive, showNativeFinder;
    private int finderShowEvents, finderDrawEvents, finderVisibleDraws, finderOffThreadEvents;
    private int finderBlockedDraws, detailBlockedDraws, detailOffThreadEvents;
    private readonly bool quietListingError, quietPlateErrors;
    private int lastBatchCount;

    internal Configuration Settings { get; }
    internal bool Active { get => active; private set => active = value; }
    internal bool ContactActive => contactActive;
    private bool Running => Active || contactActive;
    internal bool ShowNativeFinder => showNativeFinder;
    internal bool Automation { get; private set; }
    internal AlertState Alert { get; } = new();
    internal MatchBanners Banners { get; } = new();
    internal Match[] Matches { get; private set; } = [];
    internal Match[] OverviewMatches { get; private set; } = [];
    internal Listing[] OverviewListings { get; private set; } = [];
    internal Listing? CurrentListing { get; private set; }
    internal NativeView View { get; private set; } = new(false, Vector2.Zero, Vector2.Zero, new(false, 0, [], 0, false), 0);
    internal string Status { get; private set; } = "Open Party Scout to start watching.";
    internal string? SaveError { get; private set; }
    internal string? FilterError => filter.Error;
    internal string? FilterErrorFor(SearchProfile profile) => filter.ErrorFor(profile);
    internal DateTimeOffset LastRefresh { get; private set; }
    internal string? ActionMessage => Joining || DateTimeOffset.UtcNow < statusUntil ? Status : null;
    internal bool UsesJobPreferences => filter.UsesJobPreferences;
    internal string ScoreUnit => UsesJobPreferences ? "fit" : "pts";
    internal double? CurrentScore => View.Party.InParty ? filter.CurrentRank(View.Party.Jobs, View.CurrentJob, partyContext.Duty) : -1;
    internal bool Joining => joining is not null;
    internal bool QuietBackgroundAvailable => preview.QuietAudioAvailable && quietListingError && quietPlateErrors &&
        Volatile.Read(ref finderOffThreadEvents) == 0 && Volatile.Read(ref detailOffThreadEvents) == 0;
    internal (uint Id, string Name, Role Role)[] JobChoices { get; }
    internal DutyChoice[] DutyChoices { get; }
    internal IGrouping<DutyCategory, DutyChoice>[] DutyGroups { get; }

    public Plugin(IDalamudPluginInterface pluginInterface, IFramework framework, IClientState clientState,
        ICondition condition, IPartyFinderGui partyFinder, ICommandManager commandManager, IPluginLog log,
        IGameGui gui, IPartyList party, IPlayerState player, IDataManager data, IGameInteropProvider interop, ITextureProvider textures, IChatGui chatGui, IToastGui toastGui, IAddonLifecycle addonLifecycle, IDutyState dutyState)
    {
        pi = pluginInterface; this.framework = framework; client = clientState; this.player = player; conditions = condition; pf = partyFinder;
        commands = commandManager; this.log = log; chat = chatGui; this.toastGui = toastGui; this.addonLifecycle = addonLifecycle;
        this.dutyState = dutyState;
        Textures = textures;
        Settings = pi.GetPluginConfig() as Configuration ?? new Configuration();
        var migrate = Settings.Version < Configuration.CurrentVersion || Settings.Profiles is not { Count: > 0 } || Settings.CurrentPartyDuty != 0 ||
            Settings.WindowMode == RibbonMode.Half || Settings.OpenBetterMatch is null;
        Settings.CurrentPartyDuty = 0;
        Settings.Normalize(Settings.Version); Settings.Version = Configuration.CurrentVersion; showNativeFinder = Settings.ShowNativeFinder;
        RefreshFonts();
        dutySizes = data.GetExcelSheet<ContentFinderCondition>().Where(d => d.RowId > 0 && d.ContentMemberType.IsValid)
            .ToDictionary(d => d.RowId, d => ListingFilter.DutyCapacity(d.ContentMemberType.Value.MembersPerParty, d.ContentMemberType.Value.PartyCount));
        filter = new ListingFilter(Settings, dutySizes);
        if (migrate) Save();
        assemblyHash = HashAssembly(pi.AssemblyLocation);
        native = new NativePartyFinder(gui, party, player);
        try { credentials = new Credentials(Settings.TomestoneApiKey, Settings.FFLogsClientId, Settings.FFLogsClientSecret).Validated(); }
        catch { CredentialStatus = "Saved API credentials have an invalid format. Re-enter them in Settings."; }
        var worlds = data.GetExcelSheet<World>().ToDictionary(w => (ushort)w.RowId,
            w => (w.Name.ToString(), w.DataCenter.Value.Region.RowId switch { 1 => "JP", 2 or 7 => "NA", 3 => "EU", 4 => "OC", _ => "" }));
        WorldChoices = worlds.Where(w => w.Key > 0 && w.Value.Item1.Length > 0 && w.Value.Item2.Length > 0)
            .Select(w => (w.Key, w.Value.Item1)).OrderBy(w => w.Item2).ToArray();
        windows = new ScoutWindows(this);
        preview = new PartyPreview(framework, native, interop, () => windows.NativeFinderInteracting,
            id => joining?.Listing.Id == id,
            id => worlds.TryGetValue(id, out var world) ? world : ("", ""));
        try
        {
            var logText = data.GetExcelSheet<Lumina.Excel.Sheets.LogMessage>(ClientLanguage.English)
                .Where(row => row.RowId == 974 || row.RowId is >= 5855 and <= 5860)
                .ToDictionary(row => row.RowId, row => row.Text.ToString());
            quietListingError = logText.GetValueOrDefault(974u) == "Could not retrieve party recruitment information.";
            quietPlateErrors = logText.GetValueOrDefault(5855u) == "Unable to retrieve adventurer plate information." &&
                Enumerable.Range(5856, 5).All(id => logText.GetValueOrDefault((uint)id)?.StartsWith("Unable to display adventurer plate", StringComparison.Ordinal) == true);
            if (quietPlateErrors)
                foreach (var row in data.GetExcelSheet<Lumina.Excel.Sheets.LogMessage>().Where(row => row.RowId is >= 5855 and <= 5860))
                {
                    var text = row.Text.ToString();
                    if (!string.IsNullOrWhiteSpace(text)) quietPlateToastMessages[text] = row.RowId;
                }
        }
        catch { log.Warning("Party Scout could not verify background error messages; they will remain visible."); }
        englishDuties = data.GetExcelSheet<ContentFinderCondition>(ClientLanguage.English)
            .Where(d => d.RowId > 0 && d.Name.ToString().Length > 0).ToDictionary(d => d.RowId, d => d.Name.ToString());
        var classJobs = data.GetExcelSheet<ClassJob>();
        jobNames = classJobs.Select(j => (j.RowId, Name: j.Abbreviation.ToString()))
            .Where(j => !string.IsNullOrWhiteSpace(j.Name)).ToDictionary(j => j.RowId, j => j.Name);
        JobChoices = Jobs.Rows.Select(id => classJobs.GetRow(id))
            .Select(j => (j.RowId, j.Abbreviation.ToString(), Jobs.RoleOf((byte)j.RowId)!.Value)).ToArray();
        var dutyCategories = data.GetExcelSheet<ContentUICategory>(ClientLanguage.English).ToDictionary(d => d.RowId, d => d.Name.ToString());
        var expansions = data.GetExcelSheet<ExVersion>().ToDictionary(d => d.RowId, d => d.Name.ToString());
        DutyChoices = DutyPicker.Order(data.GetExcelSheet<ContentFinderCondition>().Where(d => d.RowId > 0 && d.Name.ToString().Length > 0)
            .Select(d =>
            {
                var expansion = DutyPicker.Expansion(d.ContentType.RowId, d.Content.RowId, d.RequiredExVersion.RowId);
                var name = expansions.GetValueOrDefault(expansion);
                return new DutyChoice(d.RowId, d.Name.ToString(), d.HighEndDuty,
                    DutyPicker.Category(d.ContentType.RowId, dutyCategories.GetValueOrDefault(d.ContentUICategory.RowId) ?? "", d.HighEndDuty),
                    string.IsNullOrWhiteSpace(name) ? uint.MaxValue : expansion, string.IsNullOrWhiteSpace(name) ? "Other" : name);
            })).ToArray();
        DutyGroups = DutyChoices.GroupBy(d => d.Category).ToArray();
        dutyNames = DutyChoices.ToDictionary(duty => duty.Id, duty => duty.Name);
        listTests = pi.GetIpcProvider<string>("DalamudWorkspace.ListTests.v1.PartyScout");
        runTests = pi.GetIpcProvider<string, string>("DalamudWorkspace.RunTests.v1.PartyScout");
        listTests.RegisterFunc(() => JsonSerializer.Serialize(new[] { new { name = "smoke", readOnly = true, description = "Checks monitor state, native adapter and loaded assembly." } }));
        runTests.RegisterFunc(Smoke);
        commands.AddHandler("/partyscout", new CommandInfo(Command) { HelpMessage = "Open Party Scout. Subcommands: party, people, stop, settings." });
        foreach (var eventType in new[] { AddonEvent.PreShow, AddonEvent.PostShow, AddonEvent.PostSetup, AddonEvent.PreDraw })
        {
            addonLifecycle.RegisterListener(eventType, "LookingForGroup", OnFinderLifecycle);
            addonLifecycle.RegisterListener(eventType, "LookingForGroupDetail", OnJoinLifecycle);
            addonLifecycle.RegisterListener(eventType, "SelectYesno", OnJoinLifecycle);
        }
        chat.LogMessage += OnLogMessage; toastGui.ErrorToast += OnErrorToast;
        pi.UiBuilder.Draw += Draw; pi.UiBuilder.OpenMainUi += OpenMain; pi.UiBuilder.OpenConfigUi += OpenSettings;
        framework.Update += Update;
    }

    internal void Post(Action action) { if (!disposed) actions.Enqueue(action); }
    private void Command(string command, string args) => Post(() =>
    {
        if (args.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase)) Stop("Stopped.");
        else if (args.Trim().Equals("party", StringComparison.OrdinalIgnoreCase)) windows.OpenParty();
        else if (args.Trim().Equals("people", StringComparison.OrdinalIgnoreCase)) windows.OpenContacts();
        else if (args.Trim().Equals("settings", StringComparison.OrdinalIgnoreCase)) windows.ShowSettings();
        else Open();
    });
    private void OpenMain() => Post(Open);
    private void OpenSettings() => Post(() => windows.ShowSettings());
    internal void Open()
    {
        windows.MainOpen = true;
        if (Active) return;
        if (!SafeToMonitor()) { ActionStatus("Available when logged in, outside duties and combat."); return; }
        var wasRunning = Running;
        Active = true; Automation = false;
        if (!wasRunning) StartEngine();
        nextRefresh = DateTimeOffset.MinValue;
        if (Settings.WatchFriends || Settings.WatchFreeCompany || Settings.CustomFriends.Count > 0) StartScan();
    }
    private void StartEngine()
    {
        passivePreview = false;
        territory = client.TerritoryType;
        Alert.Reset(); Banners.Reset(); listings.Clear(); batch.Clear(); Matches = []; OverviewMatches = []; OverviewListings = []; collecting = false;
        while (incoming.TryDequeue(out _)) { }
        nextRefresh = DateTimeOffset.MinValue;
        preview.Start(); native.SetHidden(FinderVisibility.HideFinder(Running, showNativeFinder));
        Status = "Connecting to Party Finder…";
        pf.ReceiveListing += Receive;
    }
    internal void ToggleMonitoring() { if (Active) Stop("Monitoring paused."); else Open(); }
    internal void ToggleNativeFinder()
    {
        showNativeFinder = !showNativeFinder; Settings.ShowNativeFinder = showNativeFinder;
        if (Running)
        {
            if (showNativeFinder) native.FinishFinderShow();
            native.SetHidden(FinderVisibility.HideFinder(Running, showNativeFinder));
            if (showNativeFinder || NativeInspectionVisible)
            {
                native.SetManualDetailHidden(false);
            }
            else if (!Joining && preview.DetailOpen) native.SetManualDetailHidden(true);
        }
        Save();
    }
    internal void ToggleAutomation()
    {
        Automation = Active && !Automation;
        if (Automation) return;
        if (Joining) CancelJoin("Party switch cancelled. Check your party.");
        Alert.Clear();
    }
    internal void Changed()
    {
        var preferred = filter.UsesJobPreferences;
        Settings.Normalize(); filter = new ListingFilter(Settings, dutySizes);
        if (preferred != filter.UsesJobPreferences) Alert.Reset();
        if (Joining) CancelJoin("Party switch cancelled. Check your party.");
        Save(); RebuildMatches(DateTimeOffset.UtcNow, announce: false); Alert.SuppressExisting(Matches);
    }
    internal void AlertsChanged()
    {
        if (!Settings.Alerts || !Settings.Toast) Banners.Hide();
        if (!Automation) Alert.Clear();
        Save();
    }
    internal void Save()
    {
        try { pi.SavePluginConfig(Settings); SaveError = null; }
        catch (Exception ex) { SaveError = "Could not save settings. Changes remain in this session; retry in Settings."; log.Error(ex, "Party Scout configuration save failed."); }
    }
    internal string DutyName(Listing listing) => listing.IsDuty ? dutyNames.GetValueOrDefault(listing.Duty) ?? $"Duty {listing.Duty}" : "Other recruitment";
    internal string DutyLabel(uint duty) => duty == 0 ? "Select duty" : dutyNames.GetValueOrDefault(duty) ?? $"Duty {duty}";
    internal string JobNames(IEnumerable<uint> jobs) => string.Join(" / ", jobs.Select(id => jobNames.GetValueOrDefault(id) ?? "Unknown job"));
    internal void DismissAlert()
    {
        if (Joining) CancelJoin("Party switch cancelled. Check your party.");
        if (Banners.Current is { Count: 1 } banner && Alert.Target?.Listing.Id == banner.Listing.Id) Banners.Dismiss();
        Alert.Dismiss();
    }

    private void Receive(IPartyFinderListing l, IPartyFinderListingEventArgs args)
    {
        if (disposed || incoming.Count >= 1000 || l.Id == 0 || l.SlotsAvailable is < 1 or > 48 || l.SlotsFilled > l.SlotsAvailable) return;
        var now = DateTimeOffset.UtcNow;
        var slots = l.Slots.Select(s => s.Accepting.Aggregate(0UL, (mask, flag) => mask | (ulong)flag)).ToArray();
        var search = (uint)l.SearchArea;
        incoming.Enqueue((new Listing(l.Id, l.ContentId, l.RawDuty, l.DutyType == DutyType.Normal,
            l.Description.TextValue, l.SlotsAvailable, l.SlotsFilled, l.RawJobsPresent.ToArray(), slots,
            (search & (1 << 5)) != 0, (search & (1 << 1)) != 0, l.Parties, l.MinimumItemLevel,
            now, now.AddSeconds(l.SecondsRemaining))
            { Host = l.Name.TextValue, HomeWorld = (ushort)l.HomeWorld.RowId, Objective = (byte)l.Objective, Conditions = (byte)l.Conditions,
                DutySettings = (byte)l.DutyFinderSettings, Loot = (byte)l.LootRules, Beginners = l.BeginnersWelcome }, args.BatchNumber));
    }
    private void OnLogMessage(ILogMessage message)
    {
        if ((!Running && !passivePreview) || !framework.IsInFrameworkUpdateThread) return;
        if (message.LogMessageId != 974 && message.LogMessageId is not (>= 5855 and <= 5860)) return;
        var manual = message.LogMessageId == 974
            ? windows.NativeFinderInteracting || native.AdventurerPlateOpen()
            : native.AdventurerPlateOpen();
        if (preview.QuietLog(message.LogMessageId, DateTimeOffset.UtcNow, manual, quietListingError, quietPlateErrors))
            message.PreventOriginal();
    }
    private void OnErrorToast(ref SeString message, ref bool isHandled)
    {
        if ((!Running && !passivePreview) || !framework.IsInFrameworkUpdateThread ||
            !quietPlateToastMessages.TryGetValue(message.TextValue, out var id)) return;
        var manual = native.AdventurerPlateOpen();
        if (preview.QuietLog(id, DateTimeOffset.UtcNow, manual, false, quietPlateErrors)) isHandled = true;
    }
    private void OnFinderLifecycle(AddonEvent type, AddonArgs args)
    {
        if (!Running) return;
        var hide = FinderVisibility.HideFinder(Running, showNativeFinder);
        var quietOpen = openingFinder;
        if (type == AddonEvent.PreShow && !quietOpen && framework.IsInFrameworkUpdateThread) ownsFinder = false;
        if (!hide && !quietOpen) return;
        if (type == AddonEvent.PreDraw && hide) { args.PreventOriginal(); Interlocked.Increment(ref finderBlockedDraws); }
        if (type == AddonEvent.PreShow && args is AddonShowArgs quietShow) quietShow.SilenceOpenSoundEffect = true;
        if (!framework.IsInFrameworkUpdateThread) { Interlocked.Increment(ref finderOffThreadEvents); return; }
        if (type == AddonEvent.PreShow && args is AddonShowArgs) { finderShowEvents++; native.PrepareFinderShow(args.Addon.Address); }
        else if (type is AddonEvent.PostShow or AddonEvent.PostSetup or AddonEvent.PreDraw)
        {
            if (hide) native.SetHidden(true);
            if (type == AddonEvent.PostShow) { native.FinishFinderShow(args.Addon.Address); openingFinder = false; }
            if (type == AddonEvent.PreDraw) { finderDrawEvents++; if (native.FinderRootVisible) finderVisibleDraws++; }
        }
    }
    private void OnJoinLifecycle(AddonEvent type, AddonArgs args)
    {
        if (!Running) return;
        var hidingDetail = args.AddonName == "LookingForGroupDetail" && FinderVisibility.HideDetail(Running, showNativeFinder, preview.SuppressDetail, Joining, NativeInspectionVisible);
        if (hidingDetail)
        {
            if (type == AddonEvent.PreDraw) { args.PreventOriginal(); Interlocked.Increment(ref detailBlockedDraws); }
            if (type == AddonEvent.PreShow && args is AddonShowArgs quietShow) quietShow.SilenceOpenSoundEffect = true;
        }
        if (!framework.IsInFrameworkUpdateThread)
        {
            if (hidingDetail) Interlocked.Increment(ref detailOffThreadEvents);
            else if (joining is not null) Interlocked.Increment(ref finderOffThreadEvents);
            return;
        }
        if (joining is not { } target) return;
        if (type == AddonEvent.PostShow) native.FinishJoinShow(args.Addon.Address);
        if (!native.OwnsJoinWindow(args.AddonName, args.Addon.Address, target.Listing.Id, joinStep == JoinStep.WaitForJoin)) return;
        if (type == AddonEvent.PreShow && args is AddonShowArgs show)
        { show.SilenceOpenSoundEffect = true; native.PrepareJoinShow(args.AddonName, args.Addon.Address); }
        else if (type is AddonEvent.PostShow or AddonEvent.PostSetup or AddonEvent.PreDraw)
            native.HideJoinWindow(args.AddonName, args.Addon.Address);
    }
    private bool InDuty => dutyState.ContentFinderCondition.RowId != 0 || dutyState.IsDutyStarted ||
        conditions.Any(ConditionFlag.BoundByDuty, ConditionFlag.BoundByDuty56, ConditionFlag.BoundByDuty95);
    private bool SafeToMonitor() => client.IsLoggedIn && !client.IsPvP && !InDuty && !conditions.Any(ConditionFlag.InCombat,
        ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51, ConditionFlag.LoggingOut) && !native.DutyPrompt;
    private bool SafeToJoin() => SafeToMonitor() && !conditions.Any(ConditionFlag.InDutyQueue, ConditionFlag.WaitingForDuty,
        ConditionFlag.WaitingForDutyFinder, ConditionFlag.Occupied, ConditionFlag.OccupiedInCutSceneEvent,
        ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78, ConditionFlag.Crafting, ConditionFlag.Gathering) && !View.Party.Alliance;

    private void Update(IFramework _)
    {
        lock (sync)
        {
            if (disposed) return;
            try
            {
                var previousParty = View.Party;
                View = native.Capture();
                var now = DateTimeOffset.UtcNow;
                ObserveCurrentParty(previousParty, now, client.IsLoggedIn ? dutyState.ContentFinderCondition.RowId : 0);
                if (Active && !Joining && (previousParty.InParty != View.Party.InParty || previousParty.Leader != View.Party.Leader || previousParty.PartyId != View.Party.PartyId))
                { Alert.Reset(); Automation = false; Banners.Hide(); }
                if (Running && (!SafeToMonitor() || territory != client.TerritoryType)) Stop("Stopped for duty or client-state change.", true);
                if (windows.DutyPreviewPending) windows.UpdateDutyPreview(now, NativePartyFinder.PullCountdownActive());
                if (Active) RefreshGearsets(now);
                while (actions.TryDequeue(out var action)) action();
                if (!Running) { CollectManualWhenIdle(now); return; }
                native.SetHidden(FinderVisibility.HideFinder(Running, showNativeFinder));
                if (showNativeFinder || NativeInspectionVisible) native.SetManualDetailHidden(false);
                else if (!Joining && preview.DetailOpen) native.SetManualDetailHidden(true);
                var listingsChanged = false;
                while (incoming.TryDequeue(out var message))
                {
                    var listing = message.Listing;
                    if (collecting) { batch[listing.Id] = listing; batchEnded |= message.Segment == 0; }
                    else { RememberListing(listing); listingsChanged = true; }
                }
                if (collecting && FinishListingBatch(now))
                {
                    listingsChanged = true;
                    if (Settings.Debug) log.Debug("Party Scout refreshed: {Count} listings.", listings.Count);
                }
                if (listingsChanged || now >= nextEvaluation) { RebuildMatches(now); nextEvaluation = now.AddSeconds(1); }
                if (CanStartSweep(now)) StartSweep(now); else TickProfileRefresh(now);
                if (!Joining) { Collect(now); TickScan(now); }
                if (windows.EditingSetup) { if (Joining) CancelJoin("Party switch cancelled. Check your party."); Alert.SuppressExisting(Matches); Banners.Hide(); }
                else if (Joining) TickJoin(now);
                else if (Active) UpdateAlert(now);
                native.SetHidden(FinderVisibility.HideFinder(Running, showNativeFinder));
            }
            catch (Exception ex)
            {
                Stop("Stopped after an error. See /xllog for Party Scout details.", true); ActionStatus(Status); log.Error(ex, "Party Scout monitor stopped.");
            }
        }
    }
    private void RebuildMatches(DateTimeOffset now, bool announce = true)
    {
        foreach (var id in listings.Where(kv => !SweepPolicy.ShowContact(kv.Value, now)).Select(kv => kv.Key).ToArray()) listings.Remove(id);
        CurrentListing = View.Party.InParty && View.Party.Leader != 0 ? listings.Values.FirstOrDefault(l => l.Fresh(now) && l.Leader == View.Party.Leader) : null;
        Match[] Ordered(IEnumerable<Match> matches) => matches
            .OrderByDescending(m => m.Rank).ThenByDescending(m => m.Score).ThenByDescending(m => m.Listing.Filled).ThenBy(m => m.Listing.Id).ToArray();
        var retainRecent = WantsSweep;
        OverviewMatches = Active ? Ordered(listings.Values
            .Select(listing => filter.Evaluate(listing, retainRecent ? listing.Seen : now)).OfType<Match>()) : [];
        Matches = retainRecent ? OverviewMatches.Where(match => match.Listing.Fresh(now)).ToArray() : OverviewMatches;
        var notify = announce && Active && !Joining && !windows.EditingSetup && Settings.Alerts && Settings.Toast;
        Banners.Observe(notify ? Matches : OverviewMatches, View.Party.InParty ? View.Party.Leader : 0, now, notify);
        OverviewListings = BrowseAll ? listings.Values.Where(filter.InEnabledDuties)
            .Select(listing => (Listing: listing, Score: Jobs.Score(listing.JobsPresent.Take(listing.Total), Settings)))
            .OrderByDescending(entry => UsesJobPreferences ? entry.Listing.Filled : entry.Score)
            .ThenByDescending(entry => entry.Score).ThenByDescending(entry => entry.Listing.Filled)
            .ThenBy(entry => entry.Listing.Id).Select(entry => entry.Listing).ToArray() : [];
    }
    private void UpdateAlert(DateTimeOffset now)
    {
        var changed = Alert.Update(Matches.Where(m => m.Listing.Leader != View.Party.Leader), CurrentScore,
            Active && (Settings.Alerts || Automation),
            Automation && SafeToJoin() && now >= nextJoinAllowed, Settings.JoinDelay, now, View.CurrentJob, gearsetJobs);
        if (changed && Settings.Alerts && Settings.Sound) NativePartyFinder.PlaySound(Settings.SoundId);
        if (Alert.Deadline is { } deadline && now >= deadline && Alert.Target is { } match) BeginJoin(match, true, Alert.TargetJob);
    }
    private void ActionStatus(string message) { Status = message; statusUntil = DateTimeOffset.UtcNow.AddSeconds(12); }
    private void Draw() { lock (sync) { if (!disposed) windows.Draw(); } }

    public void Dispose()
    {
        lock (sync) { disposed = true; Active = Automation = contactActive = false; while (actions.TryDequeue(out _)) { } }
        framework.Update -= Update; pf.ReceiveListing -= Receive; chat.LogMessage -= OnLogMessage; toastGui.ErrorToast -= OnErrorToast; addonLifecycle.UnregisterListener(OnFinderLifecycle); addonLifecycle.UnregisterListener(OnJoinLifecycle);
        pi.UiBuilder.Draw -= Draw; pi.UiBuilder.OpenMainUi -= OpenMain; pi.UiBuilder.OpenConfigUi -= OpenSettings;
        bodyFont?.Dispose(); headingFont?.Dispose(); iconFont?.Dispose();
        commands.RemoveHandler("/partyscout"); runTests.UnregisterFunc(); listTests.UnregisterFunc();
        lookupCancellation?.Cancel(); companyCancellation?.Cancel();
        void ReleaseNative()
        {
            FinishJoin(true);
            preview.Quiet(() =>
            {
                native.FinishFinderShow(); native.SetHidden(false); native.SetManualDetailHidden(false);
                if (ownsFinder && !windows.NativeFinderInteracting && !preview.DetailOpen) native.CloseFinder();
                preview.Dispose(); native.RestoreHiddenInputs(); return true;
            });
            ownsFinder = openingFinder = profileRefreshPending = false;
        }
        if (framework.IsInFrameworkUpdateThread) ReleaseNative();
        else framework.Run(ReleaseNative).GetAwaiter().GetResult();
        lookupTask?.GetAwaiter().GetResult(); companyTask?.GetAwaiter().GetResult(); lookupCancellation?.Dispose(); companyCancellation?.Dispose(); lookups.Clear(); lookups.Dispose();
    }
    private string Smoke(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 32768) return "{\"ok\":false,\"error\":\"invalid-request\"}";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String || n.GetString() != "smoke")
                return "{\"ok\":false,\"error\":\"unknown-test\"}";
        }
        catch (JsonException) { return "{\"ok\":false,\"error\":\"invalid-request\"}"; }
        string Capture()
        {
            lock (sync)
            {
                var page = native.Page();
                return JsonSerializer.Serialize(new { ok = !disposed && (!Automation || Active) && (!Joining || Active) && (!Scanning || Running) && (!InDuty || !Running) &&
                    (!preview.Busy || (Running || passivePreview) && (!Joining || joinStep == JoinStep.OpenTarget && !joinDetailRequested)),
                test = "smoke", assemblySha256 = assemblyHash, uiLibrary = typeof(DalamudUi.Material).Assembly.GetName().Name,
                fontsReady = FontsReady, fontsFailed = FontsFailed, frameworkThread = framework.IsInFrameworkUpdateThread,
                active = Active, automation = Automation, joining = Joining, nativeOpen = native.Capture().Open,
                joinWaitingForScan = Joining && joinStep == JoinStep.OpenTarget && (collecting || preview.Busy),
                hiddenByPlugin = native.Hidden, showNativeFinder, manualDetailHiddenByPlugin = native.ManualDetailHidden, ownsFinder, profileRefreshPending, matches = Matches.Length, displayedMatches = OverviewMatches.Length, cachedListings = listings.Count,
                browseAll = BrowseAll, scopedListings = OverviewListings.Length,
                profiles = Settings.Profiles.Count, enabledProfiles = Settings.EnabledProfiles.Count(), searchGroups = Settings.Groups.Count,
                highEndDuties = DutyChoices.Count(d => d.HighEnd), editingSetup = windows.EditingSetup,
                inDuty = InDuty, partyWindowOpen = windows.PartyOpen, partyInCompanion = windows.PartyInCompanion, companionPage = windows.CompanionPage,
                dutyPreview = windows.DutyPreviewPending, currentPartyDutyKnown = CurrentParty is { Duty: > 0 },
                currentPartyDescriptionCaptured = partyContext.Recruitment is { Description.Length: > 0 },
                nativeDetailOverlay = NativeParty is not null, passivePreview,
                plateRequestPending = preview.PlatePending, cachedMemberNames = preview.CachedNames,
                    ribbonMode = windows.Mode.ToString(), design = Settings.Design.ToString(), mainOpen = windows.MainOpen, workspaceOpen = windows.WorkspaceOpen, theme = Settings.Theme.ToString(), bannerMatches = Banners.Current?.Count ?? 0, matchesWindowOpen = windows.MatchesOpen, searchesWindowOpen = windows.SearchesOpen,
                previewOpen = preview.DetailOpen, previewMembers = Inspected?.Members.Length ?? 0,
                resolvedMembers = Inspected?.Members.Count(m => m.Resolved) ?? 0, lookupBusy = LookupBusy,
                collectedParties = preview.Parties.Count, currentMembers = CurrentParty?.Members.Length ?? 0,
                summaryRosters = preview.SummaryRosters, detailRequests = preview.DetailRequests, plateRequests = preview.PlateRequests,
                profileDetailRequests, contactDetailRequests, offPageDetailRequests,
                earlyListingBatches = EarlyListingBatches, lastListingBatchMs = LastListingBatchMs,
                scanning = Scanning, scanStatus = ScanStatus, scannedPages = scannedPages.Count,
                scanStage, scanPage = page, scanPageListings = scanPageListingCount, lastBatchCount,
                scanPageNeedsSnapshot,
                scanAttemptedDetails = scanAttemptedDetails.Count, previewBusy = preview.Busy,
                nativeFinderHovered = windows.NativeFinderHovered, nativeFinderInteracting = windows.NativeFinderInteracting, initialRefreshAttempts = InitialRefreshAttempts,
                lastInitialRefreshAccepted = LastInitialRefreshAccepted,
                scannedListings = socialListings.Count, scannedRosters = ScannedRosters,
                contactWatch = ContactActive, companyMembers = companyRoster.Count, companyBusy = CompanyBusy, companyStatus, completedSweeps, lastSweepPages,
                quietAudioAvailable = preview.QuietAudioAvailable, quietListingError, quietPlateErrors,
                suppressedListingErrors = preview.SuppressedListingErrors, suppressedPlateErrors = preview.SuppressedPlateErrors,
                suppressedSounds = preview.SuppressedSounds, passedThroughSounds = preview.PassedThroughSounds,
                mutedSystemSounds = preview.MutedSystemSounds,
                finderShowEvents, finderDrawEvents, finderVisibleDraws, finderOffThreadEvents,
                finderBlockedDraws = Volatile.Read(ref finderBlockedDraws), detailBlockedDraws = Volatile.Read(ref detailBlockedDraws), detailOffThreadEvents = Volatile.Read(ref detailOffThreadEvents),
                tomestoneConfigured = HasTomestone, ffLogsConfigured = HasFFLogs, previewAvailable = preview.Error is null,
                filterValid = filter.Error is null, lastRefreshUtc = LastRefresh, debug = Settings.Debug });
            }
        }
        return framework.IsInFrameworkUpdateThread ? Capture() : framework.Run(Capture).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
    }
    private static string HashAssembly(FileInfo file)
    {
        using var stream = File.OpenRead(file.FullName);
        using (var pe = new PEReader(stream, PEStreamOptions.LeaveOpen))
        {
            var metadata = pe.GetMetadataReader();
            if (metadata.GetGuid(metadata.GetModuleDefinition().Mvid) != typeof(Plugin).Assembly.ManifestModule.ModuleVersionId)
                throw new InvalidOperationException("Loaded Party Scout differs from registered DLL.");
        }
        stream.Position = 0; return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
