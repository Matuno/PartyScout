using PartyScout.Core;

namespace PartyScout;

public sealed partial class Plugin
{
    private readonly Dictionary<ulong, Listing> socialListings = [];
    private readonly HashSet<ulong> scanAttemptedDetails = [];
    private readonly CollectionQueue socialCollectionQueue = new();
    private readonly HashSet<string> scannedPages = [];
    private bool scanPageNeedsSnapshot, ownsFinder, profileRefreshPending, batchEnded;
    private volatile bool openingFinder;
    private int scanStage, completedSweeps, lastSweepPages, scanPageListingCount;
    private string previousPage = "";
    private DateTimeOffset nextScanStep, scanUntil, pageUntil, scanPageDeadline, nextSweepRestart, nextSocialHits, scanReadyUntil, refreshAttemptUntil, scanBlockedSince;
    private DateTimeOffset profileRefreshUntil, nextProfileAttempt;
    private DateTimeOffset batchStarted;
    internal int EarlyListingBatches { get; private set; }
    internal double LastListingBatchMs { get; private set; }
    private SocialHit[] cachedSocialHits = [];
    internal bool Scanning { get; private set; }
    internal bool BrowseAll { get; private set; }
    private bool WantsSweep => ContactActive || BrowseAll;
    private bool CanStartSweep(DateTimeOffset now) => WantsSweep && now >= nextSweepRestart && !Scanning && !collecting &&
        !Joining && !profileRefreshPending && !preview.Busy && nativeInspectionPending is null && !NativeInspectionVisible &&
        !windows.NativeFinderInteracting && !preview.DetailOpen && !native.AdventurerPlateOpen();
    internal string ScanStatus { get; private set; } = "Contact scanning is paused.";
    internal int InitialRefreshAttempts { get; private set; }
    internal bool? LastInitialRefreshAccepted { get; private set; }
    internal int ScannedListings => socialListings.Count;
    internal int ScannedRosters => socialListings.Keys.Count(preview.Parties.ContainsKey);
    private void ResetSocialHits() { cachedSocialHits = []; nextSocialHits = default; }
    private void OpenFinderForWork()
    {
        if (native.FinderActive) return;
        openingFinder = true;
        if (preview.Quiet(native.Open)) ownsFinder = true;
        else openingFinder = false;
    }
    private void CloseFinderIfIdle(DateTimeOffset now)
    {
        if (!ownsFinder) return;
        if (!native.FinderActive && !openingFinder) { ownsFinder = false; return; }
        var detailOpen = preview.DetailOpen;
        if (windows.NativeFinderInteracting || detailOpen && native.FinderRootVisible) { ownsFinder = false; return; }
        if (Scanning || collecting || profileRefreshPending || Joining || detailOpen ||
            native.AdventurerPlateOpen() || !preview.CanPrefetch(now)) return;
        preview.Quiet(() => { native.CloseFinder(); return true; });
        ownsFinder = openingFinder = false;
    }
    private void TickProfileRefresh(DateTimeOffset now)
    {
        if (!Active || Scanning || collecting || Joining || preview.Busy || now < nextRefresh ||
            nativeInspectionPending is not null || windows.NativeFinderInteracting || native.AdventurerPlateOpen()) return;
        // A summary request preserves the viewed listing. Reopening its finder can steal focus.
        if (preview.DetailOpen)
        {
            if (WantsSweep) ScanStatus = "Refreshing the current PF page while the listing is open.";
            if (!native.FinderActive) return;
        }
        else if (NativeInspectionVisible) return;
        if (!profileRefreshPending)
        {
            profileRefreshPending = true; profileRefreshUntil = now.AddSeconds(15); nextProfileAttempt = now.AddMilliseconds(500);
            OpenFinderForWork();
        }
        if (now >= profileRefreshUntil)
        { profileRefreshPending = false; nextRefresh = now.AddSeconds(10); return; }
        if (now < nextProfileAttempt) return;
        if (!native.FinderActive) { OpenFinderForWork(); nextProfileAttempt = now.AddSeconds(2); return; }
        if (!native.FinderReady) return;
        nextProfileAttempt = now.AddSeconds(2);
        if (!preview.Quiet(native.Refresh)) return;
        profileRefreshPending = false; collecting = true; batchEnded = false; batchStarted = now; collectingUntil = now.AddSeconds(3);
        nextRefresh = now.AddSeconds(10); batch.Clear();
    }
    private bool FinishListingBatch(DateTimeOffset now)
    {
        if (now < collectingUntil && !batchEnded) return false;
        var page = native.Page();
        var visible = native.PageListingIds();
        var count = batch.Keys.Count(visible.Contains);
        var pageReady = page is not null && (!Scanning || page.Area == 0 && page.Category == 0 &&
            (scanStage != 2 || PageKey(page) != previousPage));
        var pageComplete = pageReady && count >= page!.ExpectedListings;
        var complete = pageComplete && page!.ExpectedListings > 0 && batchEnded && count == batch.Count;
        if (!complete && now < collectingUntil) return false;
        if (Scanning && scanPageNeedsSnapshot && !pageComplete && now < pageUntil)
        { collectingUntil = now.AddSeconds(1); return false; }
        if (complete && now < collectingUntil) EarlyListingBatches++;
        LastListingBatchMs = (now - batchStarted).TotalMilliseconds;
        lastBatchCount = Scanning && scanPageNeedsSnapshot ? count : batch.Count;
        if (Scanning && scanPageNeedsSnapshot) scanPageListingCount = count;
        foreach (var listing in batch.Values) RememberListing(listing);
        batch.Clear(); collecting = false; LastRefresh = now;
        if (Scanning && scanPageNeedsSnapshot) nextScanStep = now.AddSeconds(1);
        return true;
    }
    private void RememberListing(Listing listing)
    {
        preview.ObserveListing(listing);
        if (listings.Count >= 1000 && !listings.ContainsKey(listing.Id))
            listings.Remove(listings.MinBy(kv => kv.Value.Seen).Key);
        listings[listing.Id] = listing;
        if (!ContactActive) return;
        if (socialListings.Count >= 1000 && !socialListings.ContainsKey(listing.Id))
            socialListings.Remove(socialListings.MinBy(kv => kv.Value.Seen).Key);
        socialListings[listing.Id] = listing;
    }
    internal SocialHit[] SocialHits
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            if (now < nextSocialHits) return cachedSocialHits;
            nextSocialHits = now.AddSeconds(1);
            var hits = new List<SocialHit>();
            foreach (var listing in socialListings.Values.Where(l => SweepPolicy.ShowContact(l, now)))
            {
                var detail = preview.Parties.TryGetValue(listing.Id, out var cached) && CollectionQueue.Compatible(listing, cached) ? cached : null;
                var members = detail?.Members ?? [new PreviewMember(listing.Leader, 0, listing.Host, WorldChoices.FirstOrDefault(w => w.Id == listing.HomeWorld).Name ?? "", "", NameState.Resolved) { WorldId = listing.HomeWorld }];
                foreach (var original in members)
                {
                    var member = original;
                    if (!member.Resolved && socialPeople.TryGetValue(member.Id, out var known))
                    {
                        var worldName = WorldChoices.FirstOrDefault(w => w.Id == known.World).Name ?? "";
                        if (known.Name.Length > 0)
                            member = member with { Name = known.Name, World = worldName, WorldId = known.World,
                                State = worldName.Length > 0 ? NameState.Resolved : member.State };
                    }
                    var kind = Relation(member);
                    var seen = Social.LastSeen(listing, detail, member);
                    if (kind != SocialKind.None && Social.Recent(seen, now)) hits.Add(new SocialHit(listing, member, kind, seen));
                }
            }
            cachedSocialHits = hits.OrderBy(h => h.Member.Name).ThenBy(h => h.Listing.Id).ToArray();
            return cachedSocialHits;
        }
    }

    internal void StartScan()
    {
        if (!Active || Joining || !SafeToMonitor()) { ScanStatus = "Start Watch to scan contacts outside duties and combat."; return; }
        if (contactActive) return;
        contactActive = true;
        preview.Quiet(() => { native.RefreshSocial(); return true; }); nextSocialRead = default;
        var now = DateTimeOffset.UtcNow;
        nextSweepRestart = now;
        if (CanStartSweep(now)) StartSweep(now);
    }
    internal void SetBrowseAll(bool enabled)
    {
        BrowseAll = enabled;
        if (!Active || Joining) return;
        var now = DateTimeOffset.UtcNow;
        if (enabled)
        {
            nextSweepRestart = now;
            if (CanStartSweep(now)) StartSweep(now);
        }
        else if (!WantsSweep && Scanning) PauseScan("Listing sweep paused.");
    }
    private void StartSweep(DateTimeOffset now)
    {
        Scanning = true; scanStage = 0; previousPage = ""; nextScanStep = default;
        InitialRefreshAttempts = 0; LastInitialRefreshAccepted = null; refreshAttemptUntil = scanBlockedSince = default;
        scannedPages.Clear(); scanPageListingCount = 0; scanAttemptedDetails.Clear();
        scanPageNeedsSnapshot = false; scanUntil = now.AddMinutes(20); scanReadyUntil = now.AddSeconds(15); nextRefresh = now.AddSeconds(10);
        OpenFinderForWork();
        ScanStatus = "Scanning accessible PF pages in short passes.";
    }
    internal void StopScan()
    {
        contactActive = false; CancelCompanyLookup();
        if (!BrowseAll) PauseScan("Contact scanning paused.");
        if (!Active) Stop("Contact scanning stopped.");
    }
    private void PauseScan(string reason)
    {
        Scanning = scanPageNeedsSnapshot = false;
        nextSweepRestart = DateTimeOffset.UtcNow.AddSeconds(2);
        ScanStatus = reason;
    }
    internal void RefreshSocialRosters() { if (ContactActive) { preview.Quiet(() => { native.RefreshSocial(); return true; }); nextSocialRead = default; RefreshCompanyRoster(); } }
    private bool PageCollectionPending(DateTimeOffset now)
    {
        return Matches.Any(match => collectionQueue.CanRequest(match.Listing, preview.Parties, now));
    }
    private void TickScan(DateTimeOffset now)
    {
        if (!Scanning || now < nextScanStep || collecting || preview.Busy) return;
        if (preview.DetailOpen)
        {
            PauseScan("Refreshing the current PF page while the listing is open.");
            return;
        }
        if (windows.NativeFinderInteracting || native.AdventurerPlateOpen())
        {
            if (scanBlockedSince == default) scanBlockedSince = now;
            if (now - scanBlockedSince >= TimeSpan.FromSeconds(15))
                RetrySweep("Waiting for native Party Finder or the adventurer plate to be free.", now);
            else ScanStatus = "Paused while you use native PF or an adventurer plate.";
            return;
        }
        scanBlockedSince = default;
        ScanStatus = "Scanning accessible PF pages in short passes.";
        if (now >= scanUntil) { RetrySweep("Contact scan timed out; retrying shortly.", now); return; }
        var page = native.Page();
        if (page is null)
        {
            if (scanStage == 0 && now < scanReadyUntil) { OpenFinderForWork(); nextScanStep = now.AddMilliseconds(500); return; }
            RetrySweep("Native PF page controls unavailable; retrying shortly.", now); return;
        }
        if (scanStage == 0)
        {
            if (page.Area != 0) { if (!preview.Quiet(() => native.AllScope(true))) RetrySweep("Cannot select DC listings; retrying shortly.", now); nextScanStep = now.AddSeconds(3); return; }
            if (page.Category != 0) { if (!preview.Quiet(() => native.AllScope(false))) RetrySweep("Cannot select all duties; retrying shortly.", now); nextScanStep = now.AddSeconds(3); return; }
            if (page.Previous) { if (!preview.Quiet(() => native.TurnPage(false))) RetrySweep("Cannot return to first page; retrying shortly.", now); nextScanStep = now.AddSeconds(3); return; }
            if (InitialRefreshAttempts == 0) refreshAttemptUntil = now.AddSeconds(15);
            var refreshed = preview.Quiet(native.Refresh); InitialRefreshAttempts++; LastInitialRefreshAccepted = refreshed;
            if (!refreshed)
            {
                if (now >= refreshAttemptUntil) RetrySweep("Native PF refresh unavailable; retrying shortly.", now);
                else nextScanStep = now.AddSeconds(2);
                return;
            }
            BeginPage(now); scanStage = 1; return;
        }
        if (page.Area != 0 || page.Category != 0)
        { RetrySweep("Native search scope changed; retrying shortly.", now); return; }
        var pageKey = PageKey(page);
        if (scanStage == 2)
        {
            if (!SweepPolicy.NextPageReady(previousPage, pageKey, scanPageNeedsSnapshot ? lastBatchCount : page.ExpectedListings, page.ExpectedListings))
            {
                if (now >= pageUntil) RetrySweep("The next PF page did not arrive completely; retrying shortly.", now);
                return;
            }
            scanStage = 1;
        }
        if (scanPageNeedsSnapshot && lastBatchCount < page.ExpectedListings)
        { RetrySweep("The PF page response was incomplete; retrying shortly.", now); return; }
        if (scanPageNeedsSnapshot) { scanPageNeedsSnapshot = false; scanPageDeadline = now.AddSeconds(5); }
        if (!scannedPages.Contains(pageKey) && scannedPages.Count >= 100)
        { RetrySweep("PF page limit reached; retrying shortly.", now); return; }
        if (SweepPolicy.WaitForDetails(PageCollectionPending(now), now, scanPageDeadline)) return;
        if (!scannedPages.Add(pageKey))
        { RetrySweep("PF repeated a page; retrying shortly.", now); return; }
        if (!page.Next)
        {
            lastSweepPages = scannedPages.Count; completedSweeps++;
            Scanning = false; nextSweepRestart = now.AddSeconds(10); nextRefresh = now.AddSeconds(10);
            ScanStatus = $"Last pass: {lastSweepPages} page(s). New passes keep contacts and search results current.";
            return;
        }
        previousPage = pageKey;
        if (!preview.Quiet(() => native.TurnPage(true))) { RetrySweep("The next-page action was unavailable; retrying shortly.", now); return; }
        BeginPage(now); scanStage = 2;
    }
    private void RetrySweep(string reason, DateTimeOffset now)
    {
        Scanning = false; scanPageNeedsSnapshot = false; nextSweepRestart = now.AddSeconds(30); nextRefresh = now.AddSeconds(10);
        ScanStatus = reason;
    }
    private static string PageKey(FinderPage page) => SweepPolicy.PageKey(page.Key);
    private void BeginPage(DateTimeOffset now)
    {
        collecting = true; batchEnded = false; batchStarted = now; batch.Clear(); scanPageListingCount = 0; scanPageNeedsSnapshot = true; lastBatchCount = 0;
        collectingUntil = now.AddSeconds(3); nextScanStep = now.AddSeconds(3); scanPageDeadline = now.AddSeconds(8); pageUntil = now.AddSeconds(15);
    }
}
