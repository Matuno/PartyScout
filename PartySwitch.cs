using PartyScout.Core;

namespace PartyScout;

public sealed partial class Plugin
{
    private volatile Match? joining;
    private Listing? confirmedJoinListing;
    private int joinStep;
    private uint joinJob, sourceJob;
    private uint[] gearsetJobs = [];
    private DateTimeOffset nextGearsetRead;
    internal uint JoinJob(Match match) => match.SelectJob(View.CurrentJob, gearsetJobs);
    private void RefreshGearsets(DateTimeOffset now)
    {
        if (now < nextGearsetRead) return;
        gearsetJobs = native.GearsetJobs();
        nextGearsetRead = now.AddSeconds(2);
    }
    private bool joinAutomatic, confirmed, joinDetailRequested, joinJobRequested;
    private long sourcePartyId;
    private ulong sourceLeader;
    private int sourceMembers;
    private byte[] sourceJobs = [];
    private double? sourceRank;
    private DateTimeOffset nextJoinAllowed, joinUntil, nextStep;

    internal void BeginJoin(Match requested, bool automatic = false, uint? selectedJob = null)
    {
        var now = DateTimeOffset.UtcNow;
        var match = Matches.FirstOrDefault(m => m.Listing.Id == requested.Listing.Id);
        var job = selectedJob ?? View.CurrentJob;
        if (!Active || Joining || !SafeToJoin() || native.HasOtherDialog || now < nextJoinAllowed ||
            match is null || !match.Listing.Fresh(now) || !match.PlayableJobs.Contains(job) ||
            job != View.CurrentJob && !native.GearsetJobs().Contains(job) ||
            match.Listing.Leader == View.Party.Leader || match.Listing.Parties != 1 ||
            automatic && (!Automation || CurrentScore is not { } baseline || match.RankForJob(job) <= baseline))
        {
            ActionStatus("Join unavailable: check your gear sets, queues, dialogs and the party listing.");
            if (automatic) { Alert.Dismiss(); Automation = false; }
            return;
        }
        if (Scanning) PauseScan("Contact scanning paused during the party switch.");
        preview.Quiet(() => { preview.Pause(true); return true; }); CancelLookups();
        joining = match; confirmedJoinListing = null; joinAutomatic = automatic; joinStep = 0; confirmed = joinDetailRequested = joinJobRequested = false;
        sourceJob = View.CurrentJob; joinJob = job;
        sourcePartyId = View.Party.PartyId; sourceLeader = View.Party.Leader; sourceMembers = View.Party.Jobs.Length;
        sourceJobs = View.Party.Jobs.ToArray();
        sourceRank = CurrentScore;
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
        if (joinStep == 6)
        {
            if (View.Party.PartyId != sourcePartyId || View.Party.Leader != sourceLeader || View.Party.Jobs.Length != sourceMembers)
            { CancelJoin("Your party changed. Review it before switching."); return; }
            if (View.CurrentJob != sourceJob && View.CurrentJob != joinJob)
            { CancelJoin("Your job changed during the switch. Check your party."); return; }
            if (View.CurrentJob == joinJob && (!joinAutomatic || !View.Party.InParty || CurrentScore is not null)) joinStep = 1;
            return;
        }
        if (View.CurrentJob != (joinStep <= 1 ? sourceJob : joinJob) && View.CurrentJob != joinJob)
        { CancelJoin("Your job changed during the switch. Check your party."); return; }
        if (joinStep == 0 || joinStep == 3)
        {
            OpenFinderForWork();
            if (!native.FinderReady) return;
            joinStep = joinStep == 0 ? 1 : 4;
            if (native.HasOtherDialog) { CancelJoin("Target listing could not be opened."); return; }
            var opened = preview.Quiet(() => native.OpenListing(target.Listing.Id));
            joinDetailRequested |= opened;
            if (!opened) CancelJoin("Target listing could not be opened.");
            return;
        }
        if (joinStep is 1 or 4)
        {
            if (joinStep == 1 && (View.Party.PartyId != sourcePartyId || View.Party.Leader != sourceLeader || View.Party.Jobs.Length != sourceMembers))
            { CancelJoin("Your party changed. Review it before switching."); return; }
            var detail = native.Detail(target.Listing.Id, now);
            if (detail is null) return;
            detail = detail with { IsDuty = target.Listing.IsDuty };
            var checkedMatch = filter.Evaluate(detail, now);
            var baseline = View.Party.InParty && !joinJobRequested ? CurrentScore : sourceRank;
            var rosterChanged = joinJobRequested && View.Party.InParty &&
                !sourceJobs.Select((job, index) => index == Array.IndexOf(sourceJobs, (byte)sourceJob) ? (byte)joinJob : job)
                    .Order().SequenceEqual(View.Party.Jobs.Order());
            if (detail.Leader != target.Listing.Leader || detail.Duty != target.Listing.Duty || detail.Parties != 1 ||
                checkedMatch is null || !checkedMatch.PlayableJobs.Contains(joinJob) ||
                joinAutomatic && (baseline is null || View.Party.InParty && CurrentScore is null || rosterChanged || checkedMatch.RankForJob(joinJob) <= baseline))
            { CancelJoin("The target changed or no longer matches. No further action taken."); return; }
            confirmedJoinListing = detail;
            if (joinStep == 1 && View.CurrentJob != joinJob)
            {
                if (joinJobRequested) { CancelJoin("Your job changed during the switch. Check your party."); return; }
                if (View.Party.PartyId != sourcePartyId || View.Party.Leader != sourceLeader || View.Party.Jobs.Length != sourceMembers || native.HasOtherDialog)
                { CancelJoin("Your party changed. Review it before switching."); return; }
                sourceRank = CurrentScore; sourceJobs = View.Party.Jobs.ToArray();
                joinStep = 6;
                joinJobRequested = true;
                if (!native.EquipJob(joinJob)) CancelJoin("Could not equip the selected job's gear set. Your party was not changed.");
                else Status = "Changing job before joining…";
                return;
            }
            if (joinStep == 1 && View.Party.InParty)
            {
                if (View.Party.PartyId != sourcePartyId || View.Party.Leader != sourceLeader || View.Party.Jobs.Length != sourceMembers || native.HasOtherDialog)
                { CancelJoin("Your party changed. Review it before switching."); return; }
                if (!joinJobRequested) sourceRank = CurrentScore;
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
                if (success) RememberJoinedParty(confirmedJoinListing ?? target.Listing);
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
        joining = null; confirmedJoinListing = null;
    }
    private void CancelJoin(string reason) { FinishJoin(true); Automation = false; ActionStatus(reason); }
}
