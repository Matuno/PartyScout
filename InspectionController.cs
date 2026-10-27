using PartyScout.Core;

namespace PartyScout;

public sealed partial class Plugin
{
    private readonly PartyPreview preview;
    private readonly PlayerLookups lookups = new();
    private readonly CollectionQueue collectionQueue = new();
    private readonly Dictionary<(ulong Id, string Name, string World, byte Job, uint Duty), MemberData> memberData = [];
    private Credentials credentials = new();
    private CancellationTokenSource? lookupCancellation;
    private Task? lookupTask;
    private int inspectionGeneration;
    private ulong selectedListing, lastManual;
    private ulong currentDutyOverrideLeader;
    private uint currentDutyOverride;
    private DateTimeOffset nextPartyRead, nextSocialRead, nextCollection, nextSelectedRequest, progressRetry, parsesRetry;
    private readonly Dictionary<uint, string> englishDuties;
    private readonly Dictionary<ulong, SocialPerson> socialPeople = [];
    internal PartyDetail? CurrentParty { get; private set; }
    internal PartyDetail? Inspected => Collected(selectedListing);
    internal bool HasInspection => selectedListing != 0;
    internal string? PreviewError => preview.Error;
    internal bool LookupBusy => lookupTask is { IsCompleted: false };
    internal string LookupStatus { get; private set; } = "Collecting matched parties in the background.";
    internal string CredentialStatus { get; private set; } = "";
    private string nativeSocialStatus = "Social rosters have not been read.";
    internal string SocialRosterStatus => nativeSocialStatus + (Settings.WatchFreeCompany ? " · " + companyStatus : "");
    internal bool HasTomestone => credentials.HasTomestone;
    internal bool HasFFLogs => credentials.HasFFLogs;
    internal string CollectionDiagnostics => $"Collected parties: {preview.Parties.Count} · Data rows: {memberData.Count} · Native request: {preview.Busy}\n{LookupStatus}\n{ScanStatus}";
    internal PartyDetail? Collected(ulong id)
    {
        var detail = preview.Parties.GetValueOrDefault(id);
        var latest = listings.GetValueOrDefault(id) ?? socialListings.GetValueOrDefault(id);
        return detail is not null && latest is not null && !CollectionQueue.Compatible(latest, detail) ? null : detail;
    }
    internal MemberData? DataFor(PreviewMember member, uint duty) => memberData.GetValueOrDefault(Inspection.MemberKey(member, duty));
    internal SocialKind Relation(PreviewMember member) => Social.Match(member, socialPeople, Settings, companyRoster);
    internal string InspectedDuty => Inspected is { } d ? DutyLabel(d.Duty) : "";
    internal string DutyLabel(uint duty) => DutyChoices.FirstOrDefault(d => d.Id == duty).Name ?? "Select duty";

    internal void Inspect(Listing listing)
    {
        if (!Active || Joining || listing.Expires <= DateTimeOffset.UtcNow) { ActionStatus("Start monitoring and select an available listing to inspect."); return; }
        if (selectedListing != listing.Id) CancelLookups();
        selectedListing = listing.Id; nextSelectedRequest = default;
        if (Collected(listing.Id) is null && !native.PageListingIds().Contains(listing.Id)) RequestInspectionSweep();
        else FinishInspectionSweep();
        windows.ShowParty(); Save();
    }
    private void CancelLookups(bool clearCache = false)
    {
        inspectionGeneration++;
        lookupCancellation?.Cancel(); lookupCancellation?.Dispose(); lookupCancellation = null;
        if (clearCache)
        {
            memberData.Clear(); collectionQueue.Clear(); CurrentParty = null; selectedListing = lastManual = 0;
            socialPeople.Clear(); progressRetry = parsesRetry = default; nextPartyRead = nextSocialRead = nextCollection = nextSelectedRequest = default;
            currentDutyOverrideLeader = 0; currentDutyOverride = 0;
        }
        if (clearCache && !LookupBusy) lookups.Clear();
        LookupStatus = "Collecting matched parties in the background.";
    }
    private void Collect(DateTimeOffset now)
    {
        if (now < nextCollection) return;
        nextCollection = now.AddMilliseconds(200);
        if ((!Settings.WatchContacts || !Settings.WatchFreeCompany) && CompanyBusy) CancelCompanyLookup();
        if (now >= nextSocialRead)
        {
            nextSocialRead = now.AddSeconds(10);
            var roster = native.ReadSocial(); socialPeople.Clear();
            foreach (var person in roster.People)
            {
                socialPeople[person.Id] = socialPeople.TryGetValue(person.Id, out var prior) ? person with { Kind = prior.Kind | person.Kind } : person;
                preview.Remember(person.Id, person.Name, person.World);
            }
            var fcCount = roster.People.Count(p => p.Kind == SocialKind.FreeCompany);
            nativeSocialStatus = $"Friends {roster.People.Count(p => p.Kind == SocialKind.Friend)} · " +
                (roster.FreeCompanyVerified ? $"FC native {fcCount}/{(roster.FreeCompanyTotal > 0 ? roster.FreeCompanyTotal.ToString() : "?")}" : "FC native unavailable");
            UpdateCompanyRoster(now, roster.FreeCompanyId);
        }
        preview.Tick(now);
        if (inspectionSweep && (selectedListing == 0 ||
            (listings.GetValueOrDefault(selectedListing) ?? socialListings.GetValueOrDefault(selectedListing)) is not { } pendingListing ||
            pendingListing.Expires <= now || Collected(selectedListing) is not null)) FinishInspectionSweep();
        foreach (var detail in preview.Parties.Values)
        {
            if (detail.Recruitment is not { } recruitment) continue;
            if (listings.TryGetValue(detail.Id, out var latest) && detail.Collected > latest.Seen)
                listings[detail.Id] = recruitment with { IsDuty = latest.IsDuty };
            if (socialListings.TryGetValue(detail.Id, out var scanned) && detail.Collected > scanned.Seen)
                socialListings[detail.Id] = recruitment with { IsDuty = scanned.IsDuty };
        }
        if (preview.DetailOpen && preview.ManualId != lastManual) { selectedListing = lastManual = preview.ManualId; }
        if (now >= nextPartyRead)
        {
            nextPartyRead = now.AddSeconds(1);
            var members = native.CurrentMembers().Select(preview.Resolve).ToArray();
            var recruitment = CurrentListing ?? preview.Parties.Values.FirstOrDefault(p =>
                p.Recruitment is { } l && l.Leader == View.Party.Leader && l.Fresh(now))?.Recruitment;
            var duty = currentDutyOverrideLeader == View.Party.Leader && currentDutyOverride > 0 ? currentDutyOverride : recruitment?.Duty ?? Settings.CurrentPartyDuty;
            CurrentParty = members.Length == 0 ? null : new PartyDetail(0, duty, "", members) { Recruitment = recruitment, Collected = now };
        }
        if (!collecting && !windows.NativeFinderInteracting && preview.CanPrefetch(now) &&
            (!Scanning || scanStage == 1 && now < scanPageDeadline))
        {
            var visible = native.PageListingIds();
            var selected = listings.GetValueOrDefault(selectedListing) ?? socialListings.GetValueOrDefault(selectedListing);
            if (selected is not null && selected.Expires > now && visible.Contains(selected.Id) &&
                Collected(selected.Id) is null && now >= nextSelectedRequest)
            {
                nextSelectedRequest = now.AddSeconds(15);
                if (preview.Prefetch(selected.Id, now) && Scanning) scanAttemptedDetails.Add(selected.Id);
            }
            var profileTargets = Matches.Select(m => m.Listing).Where(l => SweepPolicy.OnPage(l, visible, now) &&
                (!Scanning || !scanAttemptedDetails.Contains(l.Id))).ToArray();
            var id = preview.Busy ? 0 : collectionQueue.Next(profileTargets, preview.Parties, now);
            if (id == 0 && !preview.Busy && Scanning && socialSweepRequested)
            {
                var profileIds = profileTargets.Select(l => l.Id).ToHashSet();
                id = socialCollectionQueue.Next(scanPageListings.Values.Where(l => SweepPolicy.OnPage(l, visible, now) &&
                    !profileIds.Contains(l.Id) && !scanAttemptedDetails.Contains(l.Id)), preview.Parties, now);
            }
            if (id != 0)
            {
                if (Scanning) scanAttemptedDetails.Add(id);
                preview.Prefetch(id, now);
            }
        }
        var matched = Matches.Select(m => Collected(m.Listing.Id)).OfType<PartyDetail>()
            .Concat(socialSweepRequested ? socialListings.Values.Where(l => l.Expires > now && filter.Evaluate(l, l.Seen) is not null).Select(l => Collected(l.Id)).OfType<PartyDetail>() : [])
            .Concat(new[] { CurrentParty, Inspected }.OfType<PartyDetail>()).DistinctBy(p => p.Id).ToArray();
        var resolve = socialSweepRequested && (Settings.CustomFriends.Count > 0 || Settings.WatchFreeCompany && companyRoster.Count > 0)
            ? matched.Concat(socialListings.Values.Where(l => SweepPolicy.ShowContact(l, now)).Select(l => Collected(l.Id)).OfType<PartyDetail>()).DistinctBy(p => p.Id) : matched;
        preview.ResolveNext(resolve, now);
        if (!LookupBusy)
            foreach (var detail in matched.OrderByDescending(d => d.Id == selectedListing)
                .ThenBy(d => d.Members.Where(m => m.Resolved).Select(m => DataFor(m, d.Duty)?.ProgressAt ?? DateTimeOffset.MinValue).DefaultIfEmpty().Min()))
                if (StartLookup(detail, null, now)) break;
        foreach (var key in memberData.Where(kv => now - (kv.Value.ProgressAt > kv.Value.ParsesAt ? kv.Value.ProgressAt : kv.Value.ParsesAt) > TimeSpan.FromMinutes(10)).Select(kv => kv.Key).ToArray()) memberData.Remove(key);
    }
    internal void SetCurrentDuty(uint duty)
    {
        Settings.CurrentPartyDuty = duty;
        currentDutyOverride = duty; currentDutyOverrideLeader = View.Party.Leader;
        if (CurrentParty is { } party) CurrentParty = party with { Duty = duty };
        Save();
    }
    internal void Lookup(bool tomestone)
    {
        if (Inspected is { } detail) StartLookup(detail, tomestone, DateTimeOffset.UtcNow);
    }
    private MemberData EnsureData(PreviewMember member, uint duty)
    {
        var key = Inspection.MemberKey(member, duty);
        if (!memberData.TryGetValue(key, out var data))
        {
            if (memberData.Count >= 4096) memberData.Remove(memberData.MinBy(kv => kv.Value.ProgressAt > kv.Value.ParsesAt ? kv.Value.ProgressAt : kv.Value.ParsesAt).Key);
            memberData[key] = data = new();
        }
        return data;
    }
    private bool StartLookup(PartyDetail detail, bool? provider, DateTimeOffset now)
    {
        if (!Active || Joining || LookupBusy || !englishDuties.TryGetValue(detail.Duty, out var duty)) return false;
        var members = detail.Members.Where(m => m.Resolved).ToArray();
        bool Due(DateTimeOffset at, string? error) => now - at >= TimeSpan.FromMinutes(error is null ? 5 : 1);
        var progressMembers = HasTomestone && provider != false && now >= progressRetry ? members.Where(m =>
        {
            var data = EnsureData(m, detail.Duty);
            return Due(data.ProgressAt, data.ProgressError ?? (data.Progress?.LookupFailed == true ? "Incomplete" : null));
        }).ToArray() : [];
        var parseMembers = HasFFLogs && provider != true && now >= parsesRetry ? members.Where(m =>
        {
            if (m.Region.Length == 0) return false;
            var data = EnsureData(m, detail.Duty);
            return Due(data.ParsesAt, data.ParseError ?? (data.Parses?.Any(p => p.Failed || p.LatestZoneFailed) == true ? "Unavailable" : null));
        }).ToArray() : [];
        if (progressMembers.Length == 0 && parseMembers.Length == 0) return false;
        var creds = credentials; var generation = inspectionGeneration;
        lookupCancellation?.Dispose(); lookupCancellation = new(); var ct = lookupCancellation.Token;
        LookupStatus = "Pre-collecting public duty statistics…";
        void Apply(PreviewMember member, Action<MemberData> update) => Post(() =>
        { if (generation == inspectionGeneration && !ct.IsCancellationRequested) update(EnsureData(member, detail.Duty)); });
        lookupTask = Task.Run(async () =>
        {
            try
            {
                var progressIncomplete = false;
                foreach (var member in progressMembers)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var result = await lookups.Tomestone(member, duty, creds, ct).ConfigureAwait(false);
                        Apply(member, data => { data.Progress = result; data.ProgressError = null; data.ProgressAt = DateTimeOffset.UtcNow; });
                        if (result.LookupFailed)
                        {
                            if (!progressIncomplete) Post(() => { if (generation == inspectionGeneration) progressRetry = DateTimeOffset.UtcNow.AddMinutes(1); });
                            progressIncomplete = true;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        var message = LookupError(ex);
                        Apply(member, data => { data.ProgressError = message; data.ProgressAt = DateTimeOffset.UtcNow; });
                        if (ex is InvalidOperationException)
                        { Post(() => { if (generation == inspectionGeneration) progressRetry = DateTimeOffset.UtcNow.AddMinutes(1); }); break; }
                    }
                }
                if (parseMembers.Length > 0)
                    try
                    {
                        var result = await lookups.FFLogs(parseMembers, duty, creds, ct).ConfigureAwait(false);
                        foreach (var member in parseMembers)
                            Apply(member, data =>
                            {
                                data.Parses = result.GetValueOrDefault(member);
                                data.ParseError = data.Parses?.All(p => p.Failed) == true ? "FFLogs lookup unavailable; retrying later." : null;
                                data.ParsesAt = DateTimeOffset.UtcNow;
                            });
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        var message = LookupError(ex);
                        foreach (var member in parseMembers) Apply(member, data => { data.ParseError = message; data.ParsesAt = DateTimeOffset.UtcNow; });
                        Post(() => { if (generation == inspectionGeneration) parsesRetry = DateTimeOffset.UtcNow.AddMinutes(1); });
                    }
                Post(() => { if (generation == inspectionGeneration) LookupStatus = progressIncomplete ? "Some public duty data is unavailable; retrying later." : "Public duty data cached · private or missing records stay unavailable."; });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch { Post(() => { if (generation == inspectionGeneration) LookupStatus = "Collection failed; it will retry later."; }); }
            finally { if (ct.IsCancellationRequested) lookups.Clear(); }
        });
        return true;
    }
    private static string LookupError(Exception ex) => ex is InvalidOperationException ? ex.Message
        : ex is OperationCanceledException ? "Provider timed out; retrying later." : "Provider unavailable; retrying later.";
    internal void SaveCredentials(Credentials entered)
    {
        if (LookupBusy) { CredentialStatus = "Pause Watch before changing credentials during collection."; return; }
        try
        {
            var next = new Credentials(entered.Tomestone.Trim().Length > 0 ? entered.Tomestone.Trim() : credentials.Tomestone,
                entered.ClientId.Trim().Length > 0 ? entered.ClientId.Trim() : credentials.ClientId,
                entered.ClientSecret.Trim().Length > 0 ? entered.ClientSecret.Trim() : credentials.ClientSecret).Validated();
            Settings.TomestoneApiKey = next.Tomestone; Settings.FFLogsClientId = next.ClientId; Settings.FFLogsClientSecret = next.ClientSecret;
            credentials = next; lookups.Clear(); CancelLookups(true); Save(); CredentialStatus = SaveError is null ? "Credentials saved." : "Credentials updated; retry saving settings.";
        }
        catch { CredentialStatus = "Invalid credential format. Existing credentials were kept."; }
    }
    internal void ImportCredentials()
    {
        try
        {
            var imported = Credentials.Import(Path.Combine(pi.ConfigFile.DirectoryName!, "PassportCheckerReborn.json"));
            if (!imported.HasTomestone && !imported.HasFFLogs) { CredentialStatus = "No configured credentials found in Passport Checker Reborn."; return; }
            SaveCredentials(imported);
        }
        catch { CredentialStatus = "Could not read Passport Checker Reborn's settings. Enter credentials below."; }
    }
    internal void ForgetCredentials()
    {
        if (LookupBusy) { CredentialStatus = "Pause Watch before removing credentials during collection."; return; }
        credentials = new(); Settings.TomestoneApiKey = Settings.FFLogsClientId = Settings.FFLogsClientSecret = "";
        lookups.Clear(); CancelLookups(true); Save(); CredentialStatus = "Saved credentials removed from Party Scout.";
    }
}
