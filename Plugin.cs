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
using Dalamud.Game.Command;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Game;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using PartyScout.Core;
using Action = System.Action;
using Role = PartyScout.Core.Role;

namespace PartyScout;

public sealed partial class Plugin : IDalamudPlugin
{
    private readonly object sync = new();
    private readonly IDalamudPluginInterface pi;
    private readonly IFramework framework;
    private readonly IClientState client;
    private readonly ICondition conditions;
    private readonly IPartyFinderGui pf;
    private readonly ICommandManager commands;
    private readonly IPluginLog log;
    private readonly INotificationManager notifications;
    private readonly IChatGui chat;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly NativePartyFinder native;
    internal ITextureProvider Textures { get; }
    internal (ushort Id, string Name)[] WorldChoices { get; }
    private readonly ScoutWindows windows;
    private readonly ConcurrentQueue<Action> actions = new();
    private readonly ConcurrentQueue<Listing> incoming = new();
    private readonly Dictionary<ulong, Listing> listings = [], batch = [];
    private readonly ICallGateProvider<string> listTests;
    private readonly ICallGateProvider<string, string> runTests;
    private readonly string assemblyHash;
    private readonly Dictionary<uint, int> dutySizes;
    private ListingFilter filter;
    private DateTimeOffset nextRefresh, collectingUntil, nextEvaluation, statusUntil;
    private uint territory;
    private bool collecting, disposed;
    private volatile bool active;
    private int finderShowEvents, finderDrawEvents, finderVisibleDraws, finderOffThreadEvents;
    private int finderBlockedDraws, detailBlockedDraws, detailOffThreadEvents;
    private readonly bool quietListingError, quietPlateErrors;
    private int lastBatchCount;
    private IActiveNotification? toast;

    internal Configuration Settings { get; }
    internal bool Active { get => active; private set => active = value; }
    internal bool Automation { get; private set; }
    internal AlertState Alert { get; } = new();
    internal AlertInbox Inbox { get; } = new();
    internal Match[] Matches { get; private set; } = [];
    internal Match[] OverviewMatches { get; private set; } = [];
    internal Listing[] OverviewListings { get; private set; } = [];
    internal Listing? CurrentListing { get; private set; }
    internal NativeView View { get; private set; } = new(false, Vector2.Zero, Vector2.Zero, new(false, 0, [], 0, false), 0);
    internal string Status { get; private set; } = "Open Party Scout to start watching.";
    internal string? SaveError { get; private set; }
    internal string? FilterError => filter.Error;
    internal DateTimeOffset LastRefresh { get; private set; }
    internal string? ActionMessage => Joining || DateTimeOffset.UtcNow < statusUntil ? Status : null;
    internal int? CurrentScore => View.Party.InParty ? Jobs.PartyScore(View.Party.Jobs, Settings) : -1;
    internal bool Joining => joining is not null;
    internal bool QuietBackgroundAvailable => preview.QuietAudioAvailable && quietListingError && quietPlateErrors &&
        Volatile.Read(ref finderOffThreadEvents) == 0 && Volatile.Read(ref detailOffThreadEvents) == 0;
    internal (uint Id, string Name, Role Role)[] JobChoices { get; }
    internal (uint Id, string Name, bool HighEnd)[] DutyChoices { get; }

    public Plugin(IDalamudPluginInterface pluginInterface, IFramework framework, IClientState clientState,
        ICondition condition, IPartyFinderGui partyFinder, ICommandManager commandManager, IPluginLog log,
        IGameGui gui, IPartyList party, IPlayerState player, IDataManager data, INotificationManager notificationManager, IGameInteropProvider interop, ITextureProvider textures, IChatGui chatGui, IAddonLifecycle addonLifecycle)
    {
        pi = pluginInterface; this.framework = framework; client = clientState; conditions = condition; pf = partyFinder;
        commands = commandManager; this.log = log; notifications = notificationManager; chat = chatGui; this.addonLifecycle = addonLifecycle;
        Textures = textures;
        Settings = pi.GetPluginConfig() as Configuration ?? new Configuration();
        var migrate = Settings.Version < 5 || Settings.Profiles is not { Count: > 0 };
        Settings.Normalize(Settings.Version); Settings.Version = 5;
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
        }
        catch { log.Warning("Party Scout could not verify background error messages; they will remain visible."); }
        englishDuties = data.GetExcelSheet<ContentFinderCondition>(ClientLanguage.English)
            .Where(d => d.RowId > 0 && d.Name.ToString().Length > 0).ToDictionary(d => d.RowId, d => d.Name.ToString());
        JobChoices = Jobs.Rows.Select(id => data.GetExcelSheet<ClassJob>().GetRow(id))
            .Select(j => (j.RowId, j.Abbreviation.ToString(), Jobs.RoleOf((byte)j.RowId)!.Value)).ToArray();
        DutyChoices = data.GetExcelSheet<ContentFinderCondition>().Where(d => d.RowId > 0 && d.Name.ToString().Length > 0)
            .Select(d => (d.RowId, d.Name.ToString(), d.HighEndDuty)).OrderBy(d => d.Item2, StringComparer.CurrentCultureIgnoreCase).ToArray();
        listTests = pi.GetIpcProvider<string>("DalamudWorkspace.ListTests.v1.PartyScout");
        runTests = pi.GetIpcProvider<string, string>("DalamudWorkspace.RunTests.v1.PartyScout");
        listTests.RegisterFunc(() => JsonSerializer.Serialize(new[] { new { name = "smoke", readOnly = true, description = "Checks monitor state, native adapter and loaded assembly." } }));
        runTests.RegisterFunc(Smoke);
        commands.AddHandler("/partyscout", new CommandInfo(Command) { HelpMessage = "Open Party Scout. /partyscout stop cancels monitoring; /partyscout settings opens appearance and notifications." });
        foreach (var eventType in new[] { AddonEvent.PreShow, AddonEvent.PostShow, AddonEvent.PostSetup, AddonEvent.PreDraw })
        {
            addonLifecycle.RegisterListener(eventType, "LookingForGroup", OnFinderLifecycle);
            addonLifecycle.RegisterListener(eventType, "LookingForGroupDetail", OnJoinLifecycle);
            addonLifecycle.RegisterListener(eventType, "SelectYesno", OnJoinLifecycle);
        }
        chat.LogMessage += OnLogMessage;
        pi.UiBuilder.Draw += Draw; pi.UiBuilder.OpenMainUi += OpenMain; pi.UiBuilder.OpenConfigUi += OpenSettings;
        framework.Update += Update;
    }

    internal void Post(Action action) { if (!disposed) actions.Enqueue(action); }
    private void Command(string command, string args) => Post(() =>
    {
        if (args.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase)) Stop("Stopped.");
        else if (args.Trim().Equals("settings", StringComparison.OrdinalIgnoreCase)) windows.SettingsOpen = true;
        else Open();
    });
    private void OpenMain() => Post(Open);
    private void OpenSettings() => Post(() => windows.SettingsOpen = true);
    internal void Open()
    {
        windows.MainOpen = true;
        if (Active) return;
        if (!SafeToMonitor()) { ActionStatus("Available when logged in, outside duties and combat."); return; }
        Active = true; Automation = false; territory = client.TerritoryType;
        Alert.Reset(); Inbox.Clear(); listings.Clear(); batch.Clear(); Matches = []; OverviewMatches = []; OverviewListings = []; collecting = false;
        while (incoming.TryDequeue(out _)) { }
        nextRefresh = DateTimeOffset.MinValue;
        preview.Start(); native.SetHidden(true);
        preview.Quiet(() => { native.Open(); return true; }); Status = "Connecting to Party Finder…";
        pf.ReceiveListing += Receive;
        if (Settings.WatchContacts) { preview.Quiet(() => { native.RefreshSocial(); return true; }); nextSocialRead = default; }
        if (WantsSweep) StartSweep();
    }
    internal void Stop(string reason, bool closeNative = false)
    {
        var wasActive = Active;
        FinishJoin(true);
        Active = Automation = false; collecting = false;
        pf.ReceiveListing -= Receive; Alert.Reset(); Inbox.Clear(); DismissToast();
        preview.Quiet(() => { native.FinishFinderShow(); native.SetHidden(false); if (closeNative && wasActive) native.Close(); preview.Stop(); return true; });
        CancelLookups(true); Scanning = socialSweepRequested = BrowseAll = inspectionSweep = false; socialListings.Clear(); ResetSocialHits(); scanPageListings.Clear(); scanAttemptedDetails.Clear(); socialCollectionQueue.Clear(); scannedPages.Clear();
        Status = reason;
        listings.Clear(); batch.Clear(); Matches = []; OverviewMatches = []; OverviewListings = []; CurrentListing = null;
        while (incoming.TryDequeue(out _)) { }
    }
    internal void ToggleMonitoring() { if (Active) Stop("Monitoring paused."); else Open(); }
    internal void ToggleAutomation()
    {
        Automation = Active && !Automation;
        if (Automation) Settings.TimedJoin = true;
        else { if (Joining) CancelJoin("Party switch cancelled. Check your party."); Alert.Clear(); }
        Save();
    }
    internal void Changed()
    {
        Settings.Normalize(); filter = new ListingFilter(Settings, dutySizes);
        if (Joining) CancelJoin("Party switch cancelled. Check your party.");
        DismissToast();
        if (!Settings.TimedJoin) Automation = false;
        Save(); RebuildMatches(DateTimeOffset.UtcNow); Alert.SuppressExisting(Matches);
    }
    internal void AlertsChanged()
    {
        if (!Settings.Alerts) DismissToast();
        if (!Automation) Alert.Clear();
        Save();
    }
    internal void Save()
    {
        try { pi.SavePluginConfig(Settings); SaveError = null; }
        catch (Exception ex) { SaveError = "Could not save settings. Changes remain in this session; retry in Settings."; log.Error(ex, "Party Scout configuration save failed."); }
    }
    internal string DutyName(Listing listing) => listing.IsDuty ? DutyChoices.FirstOrDefault(d => d.Id == listing.Duty).Name ?? $"Duty {listing.Duty}" : "Other recruitment";
    internal string JobNames(IEnumerable<uint> jobs) => string.Join(" / ", jobs.Select(id => JobChoices.FirstOrDefault(j => j.Id == id).Name ?? id.ToString()));
    internal void DismissAlert()
    {
        if (Alert.Target is { } target) Inbox.Remove(target.Listing.Id);
        Alert.Dismiss(); DismissToast();
    }
    internal void DismissInbox(Match match)
    {
        if (Alert.Target?.Listing.Id == match.Listing.Id) DismissToast();
        Alert.Dismiss(match); Inbox.Remove(match.Listing.Id);
    }
    private void DismissToast() { toast?.DismissNow(); toast = null; }

    private void Receive(IPartyFinderListing l, IPartyFinderListingEventArgs args)
    {
        if (disposed || incoming.Count >= 1000 || l.Id == 0 || l.SlotsAvailable is < 1 or > 48 || l.SlotsFilled > l.SlotsAvailable) return;
        var now = DateTimeOffset.UtcNow;
        var slots = l.Slots.Select(s => s.Accepting.Aggregate(0UL, (mask, flag) => mask | (ulong)flag)).ToArray();
        var search = (uint)l.SearchArea;
        incoming.Enqueue(new Listing(l.Id, l.ContentId, l.RawDuty, l.DutyType == DutyType.Normal,
            l.Description.TextValue, l.SlotsAvailable, l.SlotsFilled, l.RawJobsPresent.ToArray(), slots,
            (search & (1 << 5)) != 0, (search & (1 << 1)) != 0, l.Parties, l.MinimumItemLevel,
            now, now.AddSeconds(l.SecondsRemaining))
            { Host = l.Name.TextValue, HomeWorld = (ushort)l.HomeWorld.RowId, Objective = (byte)l.Objective, Conditions = (byte)l.Conditions,
                DutySettings = (byte)l.DutyFinderSettings, Loot = (byte)l.LootRules, Beginners = l.BeginnersWelcome });
    }
    private void OnLogMessage(ILogMessage message)
    {
        if (!Active || !framework.IsInFrameworkUpdateThread) return;
        if (message.LogMessageId != 974 && message.LogMessageId is not (>= 5855 and <= 5860)) return;
        var manual = windows.NativeFinderInteracting || native.AdventurerPlateOpen();
        if (preview.QuietLog(message.LogMessageId, DateTimeOffset.UtcNow, manual, quietListingError, quietPlateErrors))
            message.PreventOriginal();
    }
    private void OnFinderLifecycle(AddonEvent type, AddonArgs args)
    {
        if (!Active) return;
        if (type == AddonEvent.PreDraw) { args.PreventOriginal(); Interlocked.Increment(ref finderBlockedDraws); }
        if (type == AddonEvent.PreShow && args is AddonShowArgs quietShow) quietShow.SilenceOpenSoundEffect = true;
        if (!framework.IsInFrameworkUpdateThread) { Interlocked.Increment(ref finderOffThreadEvents); return; }
        if (type == AddonEvent.PreShow && args is AddonShowArgs) { finderShowEvents++; native.PrepareFinderShow(args.Addon.Address); }
        else if (type is AddonEvent.PostShow or AddonEvent.PostSetup or AddonEvent.PreDraw)
        {
            native.SetHidden(true);
            if (type == AddonEvent.PostShow) native.FinishFinderShow(args.Addon.Address);
            if (type == AddonEvent.PreDraw) { finderDrawEvents++; if (native.FinderRootVisible) finderVisibleDraws++; }
        }
    }
    private void OnJoinLifecycle(AddonEvent type, AddonArgs args)
    {
        if (!Active) return;
        if (args.AddonName == "LookingForGroupDetail")
        {
            if (type == AddonEvent.PreDraw) { args.PreventOriginal(); Interlocked.Increment(ref detailBlockedDraws); }
            if (type == AddonEvent.PreShow && args is AddonShowArgs quietShow) quietShow.SilenceOpenSoundEffect = true;
        }
        if (!framework.IsInFrameworkUpdateThread)
        {
            if (args.AddonName == "LookingForGroupDetail") Interlocked.Increment(ref detailOffThreadEvents);
            else if (joining is not null) Interlocked.Increment(ref finderOffThreadEvents);
            return;
        }
        if (joining is not { } target) return;
        if (type == AddonEvent.PostShow) native.FinishJoinShow(args.Addon.Address);
        if (!native.OwnsJoinWindow(args.AddonName, args.Addon.Address, target.Listing.Id, joinStep == 5)) return;
        if (type == AddonEvent.PreShow && args is AddonShowArgs show)
        { show.SilenceOpenSoundEffect = true; native.PrepareJoinShow(args.AddonName, args.Addon.Address); }
        else if (type is AddonEvent.PostShow or AddonEvent.PostSetup or AddonEvent.PreDraw)
            native.HideJoinWindow(args.AddonName, args.Addon.Address);
    }
    private bool SafeToMonitor() => client.IsLoggedIn && !client.IsPvP && !conditions.Any(ConditionFlag.InCombat,
        ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51, ConditionFlag.BoundByDuty, ConditionFlag.BoundByDuty56,
        ConditionFlag.BoundByDuty95, ConditionFlag.LoggingOut) && !native.DutyPrompt;
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
                if (Active && !Joining && (previousParty.InParty != View.Party.InParty || previousParty.Leader != View.Party.Leader || previousParty.PartyId != View.Party.PartyId))
                { Alert.Reset(); Automation = false; DismissToast(); }
                if (Active && (!SafeToMonitor() || territory != client.TerritoryType)) Stop("Stopped for duty or client-state change.", true);
                while (actions.TryDequeue(out var action)) action();
                if (!Active) return;
                native.SetHidden(true);
                var now = DateTimeOffset.UtcNow;
                var received = false;
                while (incoming.TryDequeue(out var listing))
                {
                    received = true;
                    if (socialSweepRequested)
                    {
                        if (socialListings.Count >= 1000 && !socialListings.ContainsKey(listing.Id))
                            socialListings.Remove(socialListings.MinBy(kv => kv.Value.Seen).Key);
                        socialListings[listing.Id] = listing;
                    }
                    if (collecting) batch[listing.Id] = listing;
                    else RememberListing(listing);
                }
                if (collecting && now >= collectingUntil)
                {
                    var pageIds = Scanning && scanPageNeedsSnapshot ? native.PageListingIds() : null;
                    if (pageIds is not null && now < pageUntil && native.Page() is { } waitingPage &&
                        batch.Keys.Count(pageIds.Contains) < waitingPage.ExpectedListings)
                    { collectingUntil = now.AddSeconds(1); }
                    else
                    {
                        lastBatchCount = pageIds is null ? batch.Count : batch.Keys.Count(pageIds.Contains);
                        foreach (var l in batch.Values)
                        {
                            RememberListing(l);
                            if (Scanning && scanPageNeedsSnapshot && pageIds!.Contains(l.Id))
                                scanPageListings[l.Id] = l;
                        }
                        batch.Clear(); collecting = false; LastRefresh = now;
                        if (Settings.Debug) log.Debug("Party Scout refreshed: {Count} listings.", listings.Count);
                    }
                }
                if (now >= nextRefresh && !collecting && joining is null && !preview.Busy && !Scanning)
                {
                    preview.Quiet(() => { native.Open(); return true; }); nextRefresh = now.AddSeconds(10);
                    if (preview.Quiet(native.Refresh)) { collecting = true; collectingUntil = now.AddSeconds(3); batch.Clear(); }
                    else if (Settings.Debug) log.Debug("Party Scout waiting for native PF refresh cooldown.");
                }
                if (received || now >= nextEvaluation) { RebuildMatches(now); nextEvaluation = now.AddSeconds(1); }
                if (!Joining) { Collect(now); TickScan(now); }
                if (WantsSweep && !Scanning && !Joining && !preview.DetailOpen && !windows.NativeFinderInteracting && !native.AdventurerPlateOpen() && now >= nextSweepRestart) StartSweep();
                if (windows.EditingSetup) { if (Joining) CancelJoin("Party switch cancelled. Check your party."); Alert.SuppressExisting(Matches); DismissToast(); }
                else if (Joining) TickJoin(now);
                else UpdateAlert(now);
                native.SetHidden(true);
            }
            catch (Exception ex)
            {
                Stop("Stopped after an error. See /xllog for Party Scout details."); ActionStatus(Status); log.Error(ex, "Party Scout monitor stopped.");
            }
        }
    }
    private void RebuildMatches(DateTimeOffset now)
    {
        foreach (var id in listings.Where(kv => !SweepPolicy.ShowContact(kv.Value, now)).Select(kv => kv.Key).ToArray()) listings.Remove(id);
        CurrentListing = View.Party.InParty && View.Party.Leader != 0 ? listings.Values.FirstOrDefault(l => l.Fresh(now) && l.Leader == View.Party.Leader) : null;
        Match[] Ordered(IEnumerable<Match> matches) => matches
            .OrderByDescending(m => m.Score).ThenByDescending(m => m.Listing.Filled).ThenBy(m => m.Listing.Id).ToArray();
        Matches = Ordered(listings.Values.Select(l => filter.Evaluate(l, now)).OfType<Match>());
        OverviewMatches = WantsSweep
            ? Ordered(listings.Values.Select(l => filter.Evaluate(l, l.Seen)).OfType<Match>()) : Matches;
        OverviewListings = listings.Values.Where(filter.InEnabledDuties)
            .OrderByDescending(l => Jobs.Score(l.JobsPresent.Take(l.Total), Settings)).ThenByDescending(l => l.Filled).ThenBy(l => l.Id).ToArray();
    }
    private void RememberListing(Listing listing)
    {
        if (listings.Count >= 1000 && !listings.ContainsKey(listing.Id))
            listings.Remove(listings.MinBy(kv => kv.Value.Seen).Key);
        listings[listing.Id] = listing;
    }
    private void UpdateAlert(DateTimeOffset now)
    {
        var changed = Alert.Update(Matches.Where(m => m.Listing.Leader != View.Party.Leader), CurrentScore,
            Active && (Settings.Alerts || Automation),
            Automation && Settings.TimedJoin && SafeToJoin() && now >= nextJoinAllowed, Settings.JoinDelay, now, View.CurrentJob);
        if (changed)
        {
            if (Alert.Target is { } found) Inbox.Add(found, now);
            DismissToast();
            if (Settings.Alerts && Settings.Toast && Alert.Target is { } target)
                toast = notifications.AddNotification(new Notification { Title = "Party Scout · Better match", Content = $"{DutyName(target.Listing)} · {target.Score} points\n{target.Listing.Description}", Type = NotificationType.Info, InitialDuration = TimeSpan.FromSeconds(8) });
            if (Settings.Alerts && Settings.Sound) NativePartyFinder.PlaySound(Settings.SoundId);
        }
        if (Alert.Target is null) DismissToast();
        if (Alert.Deadline is { } deadline && now >= deadline && Alert.Target is { } match) BeginJoin(match, true);
    }
    private void ActionStatus(string message) { Status = message; statusUntil = DateTimeOffset.UtcNow.AddSeconds(12); }
    private void Draw() { lock (sync) { if (!disposed) windows.Draw(); } }

    public void Dispose()
    {
        lock (sync) { disposed = true; Active = Automation = false; while (actions.TryDequeue(out _)) { } }
        framework.Update -= Update; pf.ReceiveListing -= Receive; chat.LogMessage -= OnLogMessage; addonLifecycle.UnregisterListener(OnFinderLifecycle); addonLifecycle.UnregisterListener(OnJoinLifecycle);
        pi.UiBuilder.Draw -= Draw; pi.UiBuilder.OpenMainUi -= OpenMain; pi.UiBuilder.OpenConfigUi -= OpenSettings;
        commands.RemoveHandler("/partyscout"); runTests.UnregisterFunc(); listTests.UnregisterFunc(); DismissToast();
        lookupCancellation?.Cancel();
        void ReleaseNative() { FinishJoin(true); preview.Quiet(() => { native.FinishFinderShow(); native.SetHidden(false); preview.Dispose(); return true; }); }
        if (framework.IsInFrameworkUpdateThread) ReleaseNative();
        else framework.Run(ReleaseNative).GetAwaiter().GetResult();
        lookupTask?.GetAwaiter().GetResult(); lookupCancellation?.Dispose(); lookups.Clear(); lookups.Dispose();
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
                return JsonSerializer.Serialize(new { ok = !disposed && (!Automation || Active) && (!Joining || Active) && (!Scanning || Active) && (!preview.Busy || Active && !Joining),
                test = "smoke", assemblySha256 = assemblyHash, frameworkThread = framework.IsInFrameworkUpdateThread,
                active = Active, automation = Automation, joining = Joining, nativeOpen = native.Capture().Open,
                hiddenByPlugin = native.Hidden, matches = Matches.Length, displayedMatches = OverviewMatches.Length, cachedListings = listings.Count,
                browseAll = BrowseAll, scopedListings = OverviewListings.Length, inspectionSweep,
                profiles = Settings.Profiles.Count, enabledProfiles = Settings.Profiles.Count(p => p.Enabled),
                highEndDuties = DutyChoices.Count(d => d.HighEnd), editingSetup = windows.EditingSetup,
                ribbonMode = windows.Mode.ToString(), mainOpen = windows.MainOpen, theme = Settings.Theme.ToString(), inboxEntries = Inbox.Entries.Count,
                previewOpen = preview.DetailOpen, previewMembers = Inspected?.Members.Length ?? 0,
                resolvedMembers = Inspected?.Members.Count(m => m.Resolved) ?? 0, lookupBusy = LookupBusy,
                collectedParties = preview.Parties.Count, currentMembers = CurrentParty?.Members.Length ?? 0,
                scanning = Scanning, scanStatus = ScanStatus, scannedPages = scannedPages.Count,
                scanStage, scanPage = page, scanPageListings = scanPageListings.Count, lastBatchCount,
                scanPageNeedsSnapshot,
                scanAttemptedDetails = scanAttemptedDetails.Count, previewBusy = preview.Busy,
                nativeFinderHovered = windows.NativeFinderHovered, nativeFinderInteracting = windows.NativeFinderInteracting, initialRefreshAttempts = InitialRefreshAttempts,
                lastInitialRefreshAccepted = LastInitialRefreshAccepted,
                scannedListings = socialListings.Count, scannedRosters = ScannedRosters,
                contactWatch = Settings.WatchContacts, completedSweeps, lastSweepPages,
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
