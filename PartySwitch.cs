using PartyScout.Core;

namespace PartyScout;

public sealed partial class Plugin
{
    private volatile Match? joining;
    private int joinStep;
    private bool joinAutomatic, confirmed, joinDetailRequested;
    private long sourcePartyId;
    private ulong sourceLeader;
    private int sourceMembers;
    private DateTimeOffset nextJoinAllowed, joinUntil, nextStep;

    internal void BeginJoin(Match requested, bool automatic = false)
    {
        var now = DateTimeOffset.UtcNow;
        var match = Matches.FirstOrDefault(m => m.Listing.Id == requested.Listing.Id);
        if (!Active || Joining || !SafeToJoin() || native.HasOtherDialog || now < nextJoinAllowed ||
            match is null || !match.Listing.Fresh(now) || !match.PlayableJobs.Contains(View.CurrentJob) ||
            match.Listing.Leader == View.Party.Leader || match.Listing.Parties != 1 ||
            automatic && (!Automation || CurrentScore is not { } baseline || match.Score <= baseline))
        {
            ActionStatus("Join unavailable: use a compatible current job, leave queues/dialogs, or wait for a fresh normal-party listing.");
            if (automatic) { Alert.Dismiss(); Automation = false; }
            return;
        }
        if (Scanning) PauseScan("Contact scanning paused during the party switch.");
        preview.Quiet(() => { preview.Pause(true); return true; }); CancelLookups();
        joining = match; joinAutomatic = automatic; joinStep = 0; confirmed = joinDetailRequested = false;
        sourcePartyId = View.Party.PartyId; sourceLeader = View.Party.Leader; sourceMembers = View.Party.Jobs.Length;
        nextJoinAllowed = now.AddSeconds(30); joinUntil = now.AddSeconds(20); nextStep = now;
        Inbox.Remove(match.Listing.Id); Alert.Dismiss(); DismissToast(); Status = "Checking target party…";
    }
    private void TickJoin(DateTimeOffset now)
    {
        if (joining is not { } target) return;
        if (joinDetailRequested) native.HideJoinDetail(target.Listing.Id);
        if (!SafeToJoin() || !Active || joinAutomatic && !Automation || now >= joinUntil)
        { CancelJoin("Party switch cancelled or timed out. Check your party before trying again."); return; }
        if (now < nextStep) return;
        nextStep = now.AddMilliseconds(750);
        if (joinStep == 0 || joinStep == 3)
        {
            joinStep = joinStep == 0 ? 1 : 4;
            if (native.HasOtherDialog) { CancelJoin("Target listing could not be opened."); return; }
            var opened = preview.Quiet(() => native.OpenListing(target.Listing.Id));
            joinDetailRequested |= opened;
            if (!opened) CancelJoin("Target listing could not be opened.");
            return;
        }
        if (joinStep is 1 or 4)
        {
            var detail = native.Detail(target.Listing.Id, now);
            if (detail is null) return;
            detail = detail with { IsDuty = target.Listing.IsDuty };
            var checkedMatch = filter.Evaluate(detail, now);
            if (detail.Leader != target.Listing.Leader || detail.Duty != target.Listing.Duty || detail.Parties != 1 ||
                checkedMatch is null || !checkedMatch.PlayableJobs.Contains(View.CurrentJob) ||
                joinAutomatic && View.Party.InParty && (CurrentScore is not { } baseline || checkedMatch.Score <= baseline))
            { CancelJoin("The target changed or no longer matches. No further action taken."); return; }
            if (joinStep == 1 && View.Party.InParty)
            {
                if (View.Party.PartyId != sourcePartyId || View.Party.Leader != sourceLeader || View.Party.Jobs.Length != sourceMembers || native.HasOtherDialog)
                { CancelJoin("Your party changed. Review it before switching."); return; }
                joinStep = 2;
                if (!preview.Quiet(native.Leave)) CancelJoin("The game declined to leave your party.");
                else Status = "Leaving current party…";
                return;
            }
            if (View.Party.InParty || native.HasOtherDialog) { CancelJoin("Party or dialog changed before joining."); return; }
            joinStep = 5;
            if (!preview.Quiet(() => native.Join(target.Listing.Id))) CancelJoin("Join button unavailable. Check the native PF requirements.");
            else Status = "Join requested; waiting for the game…";
            return;
        }
        if (joinStep == 2) { if (!View.Party.InParty) joinStep = 3; return; }
        if (joinStep == 5)
        {
            if (View.Party.InParty)
            {
                var success = View.Party.Leader == target.Listing.Leader;
                FinishJoin(false);
                if (!success) Automation = false;
                ActionStatus(success ? "Joined the selected party." : "Party changed. Auto-join paused; check your party.");
                return;
            }
            if (!confirmed && preview.Quiet(() => native.ConfirmJoin(target.Listing.Id))) confirmed = true;
        }
    }
    private void FinishJoin(bool cancel)
    {
        if (joining is not { } target) return;
        preview.Quiet(() => { native.ReleaseJoinWindows(target.Listing.Id, cancel, joinStep == 5, joinDetailRequested); return true; });
        joining = null;
    }
    private void CancelJoin(string reason) { FinishJoin(true); Automation = false; ActionStatus(reason); }
}
