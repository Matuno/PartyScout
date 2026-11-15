namespace PartyScout;

public sealed partial class Plugin
{
    internal void Stop(string reason, bool closeNative = false)
    {
        var wasActive = Running;
        FinishJoin(true);
        Active = Automation = contactActive = false;
        CancelCompanyLookup();
        Alert.Reset(); Inbox.Clear(); DismissToast(); BrowseAll = profileRefreshPending = false;
        Matches = []; OverviewMatches = []; OverviewListings = []; CurrentListing = null;
        CancelLookups(clearCache: !Running);
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
        ClearCompanyRoster(); Scanning = socialSweepRequested = BrowseAll = false; socialListings.Clear(); ResetSocialHits(); scanPageListings.Clear(); scanAttemptedDetails.Clear(); socialCollectionQueue.Clear(); scannedPages.Clear();
        Status = reason;
        listings.Clear(); batch.Clear();
        while (incoming.TryDequeue(out _)) { }
    }
}
