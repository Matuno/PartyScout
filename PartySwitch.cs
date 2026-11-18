using PartyScout.Core;

namespace PartyScout;

public sealed partial class Plugin
{
    private volatile Match? joining;
    private Listing? confirmedJoinListing;
    private enum JoinStep { OpenTarget, CheckTarget, WaitForLeave, ReopenTarget, RecheckTarget, WaitForJoin, WaitForJob }
    private JoinStep joinStep;
    private uint joinJob, sourceJob;
    private uint[] gearsetJobs = [];
    private DateTimeOffset nextGearsetRead;
    internal uint JoinJob(Match match) => match.SelectJob(View.CurrentJob, gearsetJobs);
    internal Match? SwitchingTo => joining;
    internal uint SwitchingJob => joinJob;
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
    private bool SourcePartyChanged => View.Party.PartyId != sourcePartyId || View.Party.Leader != sourceLeader || View.Party.Jobs.Length != sourceMembers;
    private byte[] sourceJobs = [];
    private double? sourceRank;
    private DateTimeOffset nextJoinAllowed, joinUntil, nextStep, joinDetailStarted;

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
        CancelLookups();
        joining = match; confirmedJoinListing = null; joinAutomatic = automatic; joinStep = JoinStep.OpenTarget; confirmed = joinDetailRequested = joinJobRequested = false;
        sourceJob = View.CurrentJob; joinJob = job;
        sourcePartyId = View.Party.PartyId; sourceLeader = View.Party.Leader; sourceMembers = View.Party.Jobs.Length;
        sourceJobs = View.Party.Jobs.ToArray();
        sourceRank = CurrentScore;
        nextJoinAllowed = now.AddSeconds(30); joinUntil = now.AddSeconds(20); nextStep = now;
        Banners.Hide(); Alert.Dismiss(); Status = "Checking target party…";
    }
    private void TickJoin(DateTimeOffset now)
    {
        if (joining is not { } target) return;
        if (joinDetailRequested) native.HideJoinDetail(target.Listing.Id);
        if (!SafeToJoin() || !Active || joinAutomatic && !Automation || now >= joinUntil)
        { CancelJoin("Party switch cancelled or timed out. Check your party before trying again."); return; }
        if (now < nextStep) return;
        nextStep = now.AddMilliseconds(750);
        if (joinStep == JoinStep.WaitForJob)
        {
            if (SourcePartyChanged)
            { CancelJoin("Your party changed. Review it before switching."); return; }
            if (View.CurrentJob != sourceJob && View.CurrentJob != joinJob)
            { CancelJoin("Your job changed during the switch. Check your party."); return; }
            if (View.CurrentJob == joinJob && (!joinAutomatic || !View.Party.InParty || CurrentScore is not null)) joinStep = JoinStep.CheckTarget;
            return;
        }
        if (View.CurrentJob != (joinStep is JoinStep.OpenTarget or JoinStep.CheckTarget ? sourceJob : joinJob) && View.CurrentJob != joinJob)
        { CancelJoin("Your job changed during the switch. Check your party."); return; }
        if (joinStep is JoinStep.OpenTarget or JoinStep.ReopenTarget)
        {
            if (joinStep == JoinStep.OpenTarget)
            {
                preview.Tick(now);
                if (collecting || preview.Busy) return;
                preview.Quiet(() => { preview.Pause(true); return true; });
            }
            OpenFinderForWork();
            if (!native.FinderReady) return;
            joinStep = joinStep == JoinStep.OpenTarget ? JoinStep.CheckTarget : JoinStep.RecheckTarget;
            if (native.HasOtherDialog) { CancelJoin("Target listing could not be opened."); return; }
            joinDetailStarted = now;
            var opened = preview.Quiet(() => native.OpenListing(target.Listing.Id));
            joinDetailRequested |= opened;
            if (!opened) CancelJoin("Target listing could not be opened.");
            return;
        }
        if (joinStep is JoinStep.CheckTarget or JoinStep.RecheckTarget)
        {
            if (joinStep == JoinStep.CheckTarget && SourcePartyChanged)
            { CancelJoin("Your party changed. Review it before switching."); return; }
            if (!preview.ReceivedSince(target.Listing.Id, joinDetailStarted)) return;
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
            if (joinStep == JoinStep.CheckTarget && View.CurrentJob != joinJob)
            {
                if (joinJobRequested) { CancelJoin("Your job changed during the switch. Check your party."); return; }
                if (SourcePartyChanged || native.HasOtherDialog)
                { CancelJoin("Your party changed. Review it before switching."); return; }
                sourceRank = CurrentScore; sourceJobs = View.Party.Jobs.ToArray();
                joinStep = JoinStep.WaitForJob;
                joinJobRequested = true;
                if (!native.EquipJob(joinJob)) CancelJoin("Could not equip the selected job's gear set. Your party was not changed.");
                else Status = "Changing job before joining…";
                return;
            }
            if (joinStep == JoinStep.CheckTarget && View.Party.InParty)
            {
                if (SourcePartyChanged || native.HasOtherDialog)
                { CancelJoin("Your party changed. Review it before switching."); return; }
                if (!joinJobRequested) sourceRank = CurrentScore;
                joinStep = JoinStep.WaitForLeave;
                if (!preview.Quiet(native.Leave)) CancelJoin("The game declined to leave your party.");
                else Status = "Leaving current party…";
                return;
            }
            if (View.Party.InParty || native.HasOtherDialog) { CancelJoin("Party or dialog changed before joining."); return; }
            joinStep = JoinStep.WaitForJoin;
            if (!preview.Quiet(() => native.Join(target.Listing.Id))) CancelJoin("Join button unavailable. Check the native PF requirements.");
            else Status = "Join requested; waiting for the game…";
            return;
        }
        if (joinStep == JoinStep.WaitForLeave) { if (!View.Party.InParty) joinStep = JoinStep.ReopenTarget; return; }
        if (joinStep == JoinStep.WaitForJoin)
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
        preview.Quiet(() => { native.ReleaseJoinWindows(target.Listing.Id, cancel, joinStep == JoinStep.WaitForJoin, joinDetailRequested); return true; });
        joining = null; confirmedJoinListing = null;
    }
    private void CancelJoin(string reason) { FinishJoin(true); Automation = false; ActionStatus(reason); }
}
