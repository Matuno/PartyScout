using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PartyScout.Core;

namespace PartyScout;

// Requests are started on a worker, cancelled on selection/stop/unload, and never log URLs or bodies.
internal sealed class PlayerLookups : IDisposable
{
    private readonly HttpClient http;
    private readonly Dictionary<(string World, string Name, string Duty), (DateTimeOffset Until, TomestoneCharacterInfo Data)> progressCache = [];
    private readonly Dictionary<(string World, string Name, byte Job, string Duty), (DateTimeOffset Until, Performance[] Data)> parseCache = [];
    private string? token;
    private Credentials? tokenCredentials;
    private DateTimeOffset tokenUntil;

    internal PlayerLookups(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PartyScout/0.1.4");
    }
    public void Dispose() => http.Dispose();
    internal void Clear() { progressCache.Clear(); parseCache.Clear(); token = null; tokenCredentials = null; }

    private async Task<HttpResponseMessage> Send(Func<HttpRequestMessage> request, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var message = request();
            var response = await http.SendAsync(message, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(2);
                if (delay <= TimeSpan.FromSeconds(10))
                {
                    response.Dispose();
                    await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, ct).ConfigureAwait(false); continue;
                }
            }
            return response;
        }
    }
    private static string Failure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "Check API credentials in Settings.",
        HttpStatusCode.Forbidden => "API access denied; check permissions or subscription.",
        HttpStatusCode.TooManyRequests => "API rate limit reached; try later.",
        _ => $"Provider returned HTTP {(int)status}; try later.",
    };

    internal async Task<TomestoneCharacterInfo> Tomestone(PreviewMember member, string duty, Credentials credentials, CancellationToken ct)
    {
        var key = (member.World, member.Name, duty);
        if (progressCache.TryGetValue(key, out var cached) && cached.Until > DateTimeOffset.UtcNow) return cached.Data;
        var info = new TomestoneCharacterInfo();
        var (encounter, fallback) = TomestoneData.Encounter(duty);
        var character = Uri.EscapeDataString(member.World) + "/" + Uri.EscapeDataString(member.Name);
        async Task Fetch(string endpoint, Action<JsonElement> parse, bool profile = false)
        {
            try
            {
                using var response = await Send(() => new HttpRequestMessage(HttpMethod.Get, "https://tomestone.gg/api/character/" + endpoint)
                    { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", credentials.Tomestone), Accept = { new MediaTypeWithQualityHeaderValue("application/json") } } }, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound) { if (profile) info.NotFound = true; return; }
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    if (error.RootElement.GetStringOrNull("error")?.Contains("hidden", StringComparison.OrdinalIgnoreCase) == true)
                    { info.ActivityHidden = true; return; }
                }
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException(Failure(response.StatusCode));
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() == 0) info.NoLogs = true;
                else parse(doc.RootElement);
            }
            catch (Exception ex) when (!profile && !ct.IsCancellationRequested && ex is (HttpRequestException or OperationCanceledException or InvalidOperationException or JsonException))
            { info.LookupFailed = true; }
        }
        await Fetch("profile/" + character, root =>
        {
            TomestoneData.ParseProfileEncounters(info, root, duty, encounter);
            if (fallback != null)
            {
                if (TomestoneData.HasData(info)) info.Phase = "P2";
                else
                {
                    TomestoneData.ParseProfileEncounters(info, root, duty, fallback);
                    if (TomestoneData.HasData(info)) info.Phase = "P1";
                }
            }
        }, profile: true).ConfigureAwait(false);
        if (!info.NotFound && !info.NoLogs && !info.ActivityHidden)
            foreach (var part in new[] { encounter, fallback })
            {
                if (part is null || TomestoneData.HasData(info)) continue;
                var query = $"{character}?expansion={Uri.EscapeDataString(part.Expansion)}&zone={Uri.EscapeDataString(part.Zone)}&encounter={Uri.EscapeDataString(part.Encounter)}";
                await Fetch("progression-graph/" + query, root => info.ProgPoint = TomestoneData.ParseProgPointFromGraph(root)).ConfigureAwait(false);
                if (!info.ActivityHidden) await Fetch("activity/" + query, root => TomestoneData.ParseActivityResponse(info, root)).ConfigureAwait(false);
                if (fallback != null && TomestoneData.HasData(info)) info.Phase = part == encounter ? "P2" : "P1";
            }
        if (TomestoneData.HasData(info) || info.BestPercent.HasValue) info.NoLogs = false;
        ct.ThrowIfCancellationRequested();
        if (!info.LookupFailed)
        {
            if (progressCache.Count >= 2048) progressCache.Clear();
            progressCache[key] = (DateTimeOffset.UtcNow.AddMinutes(5), info);
        }
        return info;
    }

    internal async Task<Dictionary<PreviewMember, Performance[]>> FFLogs(PreviewMember[] members, string duty,
        Credentials credentials, CancellationToken ct)
    {
        var encounters = FFLogsData.Encounter(duty);
        var result = new Dictionary<PreviewMember, Performance[]>();
        var pending = new List<PreviewMember>();
        foreach (var member in members)
            if (parseCache.TryGetValue((member.World, member.Name, member.Job, duty), out var cached) && cached.Until > DateTimeOffset.UtcNow) result[member] = cached.Data;
            else pending.Add(member);
        if (pending.Count == 0) return result;
        if (token is null || tokenCredentials != credentials || tokenUntil <= DateTimeOffset.UtcNow)
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.ClientId + ":" + credentials.ClientSecret));
            using var response = await Send(() => new HttpRequestMessage(HttpMethod.Post, "https://www.fflogs.com/oauth/token")
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Basic", basic) },
                Content = new StringContent("grant_type=client_credentials", Encoding.UTF8, "application/x-www-form-urlencoded"),
            }, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(Failure(response.StatusCode));
            using var auth = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            token = auth.RootElement.GetStringOrNull("access_token") ?? throw new InvalidOperationException("FFLogs did not return an access token.");
            tokenCredentials = credentials;
            tokenUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(auth.RootElement.GetNumberOrNull("expires_in") ?? 3600, 60, 86400) - 30);
        }
        var fields = string.Join(" ", encounters.Select((e, i) => $"e{i}: encounterRankings(encounterID: {e.Id}, difficulty: {e.Difficulty})").Append("zone: zoneRankings"));
        var selectors = pending.Select((m, i) => $"p{i}: {FFLogsData.Selector(m)} {{ {fields} }}");
        var body = JsonSerializer.Serialize(new { query = "{ characterData { " + string.Join(" ", selectors) + " } }" });
        using var queryResponse = await Send(() => new HttpRequestMessage(HttpMethod.Post, "https://www.fflogs.com/api/v2/client")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }, ct).ConfigureAwait(false);
        if (!queryResponse.IsSuccessStatusCode)
        {
            if (queryResponse.StatusCode == HttpStatusCode.Unauthorized) token = null;
            throw new InvalidOperationException(Failure(queryResponse.StatusCode));
        }
        using var doc = JsonDocument.Parse(await queryResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var failedMembers = new HashSet<int>();
        var failedZones = new HashSet<int>();
        if (doc.RootElement.TryGetPath(out var errors, "errors") && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            foreach (var error in errors.EnumerateArray())
            {
                if (!error.TryGetPath(out var path, "path") || path.ValueKind != JsonValueKind.Array || path.GetArrayLength() < 2 ||
                    path[0].ValueKind != JsonValueKind.String || path[0].GetString() != "characterData" ||
                    path[1].ValueKind != JsonValueKind.String || path[1].GetString() is not { } alias ||
                    !alias.StartsWith('p') || !int.TryParse(alias.AsSpan(1), out var index) || index < 0 || index >= pending.Count)
                    throw new InvalidOperationException("FFLogs could not complete this lookup; check access or try later.");
                failedMembers.Add(index);
                if (path.GetArrayLength() == 2 || path[2].ValueKind == JsonValueKind.String && path[2].GetString() == "zone")
                    failedZones.Add(index);
            }
        if (!doc.RootElement.TryGetPath(out var characters, "data", "characterData")) throw new InvalidOperationException("FFLogs response did not contain character data.");
        var noKills = new HashSet<(int Member, int Phase)>();
        for (var i = 0; i < pending.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var member = pending[i];
            var parts = new Performance[Math.Max(1, encounters.Length)];
            var zone = characters.TryGetPath(out var zoneData, $"p{i}", "zone") ? FFLogsData.ZoneAverage(zoneData) : (null, false);
            for (var phase = 0; phase < parts.Length; phase++)
            {
                Performance part = characters.TryGetPath(out var character, $"p{i}")
                    ? encounters.Length == 0 ? new(null, null, null, null, "Duty not mapped by FFLogs")
                      : character.TryGetPath(out var rankings, $"e{phase}") ? FFLogsData.Parse(rankings, member.Job) : new(null, null, null, null, "Data unavailable") { Failed = failedMembers.Contains(i) }
                    : failedMembers.Contains(i) ? new(null, null, null, null, "Lookup unavailable") { Failed = true }
                    : new(null, null, null, null, "Not found or private");
                parts[phase] = part with { LatestZoneAverage = zone.Item1, LatestZoneMedian = zone.Item2, LatestZoneFailed = failedZones.Contains(i) };
            }
            result[member] = parts;
            for (var phase = 0; phase < parts.Length; phase++)
                if (!parts[phase].Failed && parts[phase].Kills == 0) noKills.Add((i, phase));
        }
        if (noKills.Count > 0)
        {
            try
            {
                var lowest = await RecentWipes(pending, encounters, noKills, ct).ConfigureAwait(false);
                foreach (var (member, phase) in noKills)
                    if (lowest.TryGetValue((member, phase), out var wipe))
                        result[pending[member]][phase] = result[pending[member]][phase] with { LowestWipe = wipe };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                foreach (var (member, phase) in noKills)
                    result[pending[member]][phase] = result[pending[member]][phase] with { Failed = true, Status = "Recent pulls unavailable; retrying later" };
            }
        }
        for (var i = 0; i < pending.Count; i++)
        {
            var member = pending[i]; var parts = result[member];
            if (!failedMembers.Contains(i) && !parts.Any(p => p.Failed || p.LatestZoneFailed))
            {
                if (parseCache.Count >= 2048) parseCache.Clear();
                parseCache[(member.World, member.Name, member.Job, duty)] = (DateTimeOffset.UtcNow.AddMinutes(5), parts);
            }
        }
        return result;
    }

    // Like Passport Checker, use each player's ten most recent public reports for no-clear phases.
    private async Task<Dictionary<(int Member, int Phase), double>> RecentWipes(List<PreviewMember> members,
        (int Id, int Difficulty)[] encounters, HashSet<(int Member, int Phase)> wanted, CancellationToken ct)
    {
        async Task<JsonDocument> Query(string query)
        {
            using var response = await Send(() => new HttpRequestMessage(HttpMethod.Post, "https://www.fflogs.com/api/v2/client")
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
                Content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json"),
            }, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized) token = null;
                throw new InvalidOperationException(Failure(response.StatusCode));
            }
            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetPath(out var errors, "errors") && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            { doc.Dispose(); throw new InvalidOperationException("FFLogs recent reports unavailable."); }
            return doc;
        }

        var indices = wanted.Select(p => p.Member).Distinct().ToArray();
        var selectors = indices.Select(i => $"p{i}: {FFLogsData.Selector(members[i])} {{ recentReports(limit: 10) {{ data {{ code }} }} }}");
        using var reports = await Query("{ characterData { " + string.Join(" ", selectors) + " } }").ConfigureAwait(false);
        if (!reports.RootElement.TryGetPath(out var characters, "data", "characterData"))
            throw new InvalidOperationException("FFLogs recent reports unavailable.");
        var playersByReport = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var i in indices)
        {
            if (!characters.TryGetPath(out var data, $"p{i}", "recentReports", "data") || data.ValueKind != JsonValueKind.Array) continue;
            foreach (var report in data.EnumerateArray().Take(10))
            {
                if (report.GetStringOrNull("code") is not { Length: > 0 and <= 64 } code) continue;
                if (!playersByReport.TryGetValue(code, out var players)) playersByReport[code] = players = [];
                players.Add(i);
            }
        }
        var lowest = new Dictionary<(int Member, int Phase), double>();
        var phases = wanted.Select(p => p.Phase).Distinct().ToArray();
        var fields = string.Join(" ", phases.Select(i => $"f{i}: fights(encounterID: {encounters[i].Id}, difficulty: {encounters[i].Difficulty}) {{ kill fightPercentage friendlyPlayers }}"));
        foreach (var codes in playersByReport.Keys.Chunk(50))
        {
            var fieldsByReport = codes.Select((code, i) => $"r{i}: report(code: {JsonSerializer.Serialize(code)}) {{ masterData {{ actors(type: \"Player\") {{ id name server }} }} {fields} }}");
            using var fights = await Query("{ reportData { " + string.Join(" ", fieldsByReport) + " } }").ConfigureAwait(false);
            if (!fights.RootElement.TryGetPath(out var reportData, "data", "reportData"))
                throw new InvalidOperationException("FFLogs recent fights unavailable.");
            for (var i = 0; i < codes.Length; i++)
            {
                if (!reportData.TryGetPath(out var actors, $"r{i}", "masterData", "actors") || actors.ValueKind != JsonValueKind.Array) continue;
                var ids = new Dictionary<int, HashSet<int>>();
                foreach (var actor in actors.EnumerateArray())
                {
                    var id = actor.GetNumberOrNull("id");
                    var name = actor.GetStringOrNull("name");
                    var server = actor.GetStringOrNull("server");
                    if (id is not (>= 1 and <= int.MaxValue) || id != Math.Truncate(id.Value) || name is null || server is null) continue;
                    foreach (var member in playersByReport[codes[i]])
                    {
                        if (!name.Equals(members[member].Name, StringComparison.OrdinalIgnoreCase) ||
                            !server.Equals(members[member].World, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!ids.TryGetValue(member, out var actorIds)) ids[member] = actorIds = [];
                        actorIds.Add((int)id.Value);
                    }
                }
                foreach (var phase in phases)
                {
                    if (!reportData.TryGetPath(out var entries, $"r{i}", $"f{phase}") || entries.ValueKind != JsonValueKind.Array) continue;
                    foreach (var fight in entries.EnumerateArray())
                    {
                        if (!fight.TryGetPath(out var kill, "kill") || kill.ValueKind != JsonValueKind.False ||
                            fight.GetNumberOrNull("fightPercentage") is not { } value || value is < 0 or > 100 ||
                            !fight.TryGetPath(out var players, "friendlyPlayers") || players.ValueKind != JsonValueKind.Array) continue;
                        var participants = players.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out _))
                            .Select(p => p.GetInt32()).ToHashSet();
                        foreach (var (member, actorIds) in ids)
                        {
                            var key = (member, phase);
                            if (actorIds.Overlaps(participants) && wanted.Contains(key) && (!lowest.TryGetValue(key, out var old) || value < old)) lowest[key] = value;
                        }
                    }
                }
            }
        }
        return lowest;
    }
}
