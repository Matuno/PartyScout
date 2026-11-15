namespace PartyScout;

public sealed partial class Plugin
{
    internal void Stop(string reason, bool closeNative = false)
    {
        var wasActive = Running;
        FinishJoin(true);
        Active = Automation = false;
        Alert.Reset(); Inbox.Clear(); DismissToast(); BrowseAll = inspectionSweep = profileRefreshPending = false;
        Matches = []; OverviewMatches = []; OverviewListings = []; CurrentListing = null;
        if (closeNative) { contactActive = false; windows.ContactsOpen = false; }
        CancelLookups(clearCache: !Running);
        if (Running) { Status = reason; return; }
        collecting = false;
        pf.ReceiveListing -= Receive;
        preview.Quiet(() =>
        {
            native.FinishFinderShow(); native.SetHidden(false); native.SetManualDetailHidden(false);
            if (closeNative && wasActive) native.Close();
            else if (ownsFinder && !windows.NativeFinderInteracting && !preview.DetailOpen) native.CloseFinder();
            preview.Stop(); native.RestoreHiddenInputs(); return true;
        });
        ownsFinder = openingFinder = profileRefreshPending = passivePreview = false;
        ClearCompanyRoster(); Scanning = socialSweepRequested = BrowseAll = inspectionSweep = false; socialListings.Clear(); ResetSocialHits(); scanPageListings.Clear(); scanAttemptedDetails.Clear(); socialCollectionQueue.Clear(); scannedPages.Clear();
        Status = reason;
        listings.Clear(); batch.Clear();
        while (incoming.TryDequeue(out _)) { }
    }
}
