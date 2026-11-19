using PartyScout.Core;

namespace PartyScout;

public sealed partial class Plugin
{
    private uint territory;
    private volatile bool monitoringSuspended;
    internal bool MonitoringSuspended => monitoringSuspended;
    private bool WatchRequested => Active || ContactActive;
    private bool Running => WatchRequested && !monitoringSuspended;

    private void StartEngine()
    {
        passivePreview = false;
        listings.Clear(); batch.Clear(); Matches = []; OverviewMatches = []; OverviewListings = []; collecting = false;
        while (incoming.TryDequeue(out _)) { }
        nextRefresh = nextSweepRestart = default;
        preview.Start(); native.SetHidden(FinderVisibility.HideFinder(Running, showNativeFinder));
        Status = "Connecting to Party Finder…";
        pf.ReceiveListing += Receive;
    }

    private void UpdateMonitoring(uint currentTerritory, bool loggedIn, bool inDuty, bool available)
    {
        if (!WatchRequested) return;
        if (!loggedIn || inDuty)
        {
            Stop(inDuty ? "Stopped for duty." : "Stopped on logout.", closeNative: true);
            return;
        }

        var changedTerritory = territory != currentTerritory;
        territory = currentTerritory;
        if (!available || (changedTerritory && !monitoringSuspended))
        {
            if (monitoringSuspended) return;
            monitoringSuspended = true;
            Alert.Clear(); Banners.Hide();
            StopWatchWork("Watch will resume when Party Finder is available.", closeNative: false);
            return;
        }
        if (!monitoringSuspended) return;
        monitoringSuspended = false;
        StartEngine();
    }

    internal void Stop(string reason, bool closeNative = false)
    {
        var wasWatching = WatchRequested;
        Active = contactActive = BrowseAll = monitoringSuspended = false;
        Alert.Reset(); Banners.Reset();
        ClearCompanyRoster();
        StopWatchWork(reason, closeNative && wasWatching);
    }

    private void StopWatchWork(string reason, bool closeNative)
    {
        FinishJoin(true);
        Automation = false;
        if (CompanyBusy) CancelCompanyLookup();
        Matches = []; OverviewMatches = []; OverviewListings = []; CurrentListing = null;
        CancelLookups(clearCache: true, keepPartyData: true);
        collecting = false;
        pf.ReceiveListing -= Receive;
        preview.Quiet(() =>
        {
            native.FinishFinderShow(); native.SetHidden(false); native.SetManualDetailHidden(false);
            if (closeNative) native.Close();
            else if (ownsFinder && !windows.NativeFinderInteracting && !preview.DetailOpen) native.CloseFinder();
            preview.Stop(); native.RestoreHiddenInputs(); return true;
        });
        ownsFinder = openingFinder = profileRefreshPending = passivePreview = false;
        Scanning = scanPageNeedsSnapshot = false; socialListings.Clear(); ResetSocialHits(); scanPageListingCount = 0; scanAttemptedDetails.Clear(); socialCollectionQueue.Clear(); scannedPages.Clear();
        statusUntil = default;
        Status = ScanStatus = reason;
        listings.Clear(); batch.Clear();
        while (incoming.TryDequeue(out _)) { }
    }
}
