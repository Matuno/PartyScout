namespace PartyScout;

public sealed partial class Plugin
{
    private HashSet<(string Name, ushort World)> companyRoster = [];
    private CancellationTokenSource? companyCancellation;
    private Task? companyTask;
    private DateTimeOffset nextCompanyLookup;
    private ulong companyOwner;
    private int companyGeneration;
    private string companyStatus = "FC public roster not loaded";
    private bool CompanyBusy => companyTask is { IsCompleted: false };

    private void UpdateCompanyRoster(DateTimeOffset now, ulong knownCompany)
    {
        if (!Settings.WatchContacts || !Settings.WatchFreeCompany || !player.IsLoaded || player.ContentId == 0) return;
        var owner = player.ContentId;
        if (companyOwner != 0 && companyOwner != owner) ClearCompanyRoster();
        companyOwner = owner;
        if (CompanyBusy || now < nextCompanyLookup) return;
        var name = player.CharacterName;
        var worldId = (ushort)player.HomeWorld.RowId;
        var world = WorldChoices.FirstOrDefault(w => w.Id == worldId).Name;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(world) || worldId == 0) return;

        companyCancellation?.Dispose(); companyCancellation = new();
        var ct = companyCancellation.Token; var generation = ++companyGeneration;
        nextCompanyLookup = now.AddMinutes(5); companyStatus = "FC public roster loading…";
        companyTask = Task.Run(async () =>
        {
            try
            {
                var members = await lookups.FreeCompany(name, world, knownCompany, ct).ConfigureAwait(false);
                Post(() =>
                {
                    if (generation != companyGeneration || ct.IsCancellationRequested || !Active ||
                        !Settings.WatchContacts || !Settings.WatchFreeCompany || player.ContentId != owner) return;
                    companyRoster = members.Select(m => (m.Trim().ToUpperInvariant(), worldId)).ToHashSet();
                    companyStatus = $"FC public {companyRoster.Count} (Lodestone)";
                    nextCompanyLookup = DateTimeOffset.UtcNow.AddHours(6); ResetSocialHits();
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch
            {
                Post(() =>
                {
                    if (generation != companyGeneration || ct.IsCancellationRequested || !Active) return;
                    companyStatus = "FC public roster unavailable; retrying later";
                    nextCompanyLookup = DateTimeOffset.UtcNow.AddMinutes(5);
                });
            }
        });
    }

    private void CancelCompanyLookup()
    {
        if (companyCancellation is null || companyCancellation.IsCancellationRequested) return;
        companyGeneration++; companyCancellation.Cancel();
        companyStatus = companyRoster.Count > 0 ? $"FC public {companyRoster.Count} (Lodestone)" : "FC public roster paused";
        nextCompanyLookup = default;
    }

    private void RefreshCompanyRoster() { CancelCompanyLookup(); nextCompanyLookup = default; }
    private void ClearCompanyRoster()
    {
        CancelCompanyLookup(); companyRoster.Clear(); companyOwner = 0;
        companyStatus = "FC public roster not loaded"; nextCompanyLookup = default;
    }
}
