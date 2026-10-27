// Native member/name approach adapted from PassportCheckerReborn; see THIRD-PARTY-NOTICES.md.
using System.Collections.Concurrent;
using System.Text;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.Sound;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using InteropGenerator.Runtime;
using PartyScout.Core;

namespace PartyScout;

// Only callback-owned packets are read in detours. All state/native actions belong to IFramework.
internal sealed unsafe class PartyPreview : IDisposable
{
    private readonly IFramework framework;
    private readonly NativePartyFinder native;
    private readonly Func<bool> nativeFinderHovered;
    private readonly Func<ulong, bool> switchTarget;
    private readonly Func<ushort, (string Name, string Region)> world;
    private readonly ConcurrentQueue<PartyDetail> received = new();
    private readonly ConcurrentQueue<(ulong Id, string Name, ushort World)> replies = new();
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> recentListings = new();
    private readonly Dictionary<ulong, (string Name, ushort World, DateTimeOffset Until)> names = [];
    private readonly Hook<CharaCard.Delegates.HandleCurrentCharaCardDataPacket>? packetHook;
    private readonly Hook<AgentLookingForGroup.Delegates.PopulateListingData>? listingHook;
    private readonly Hook<UIGlobals.Delegates.PlaySoundEffect>? soundHook;
    private readonly Hook<SoundManager.Delegates.PlaySystemSound>? systemSoundHook;
    private long requested, background, plateErrorId, manualId;
    private DateTimeOffset requestUntil, nextRequest, listingUntil, listingStarted, nextRead, nextListing;
    private DateTimeOffset listingErrorUntil, plateErrorUntil;
    private bool listingFailure, plateFailure, manualPlate;
    [ThreadStatic] private static int quietSounds, mutedSystemCalls;
    private volatile bool enabled, keepLateReplies;
    internal Dictionary<ulong, PartyDetail> Parties { get; } = [];
    internal string? Error { get; }
    internal ulong ManualId { get => (ulong)Interlocked.Read(ref manualId); private set => Interlocked.Exchange(ref manualId, unchecked((long)value)); }
    internal bool DetailOpen => native.VisibleDetailId != 0 && native.VisibleDetailId != BackgroundId;
    internal ulong BackgroundId => (ulong)Interlocked.Read(ref background);
    internal bool Busy => BackgroundId != 0;
    internal bool PlatePending => Interlocked.Read(ref requested) != 0;
    internal bool ManualPlatePending => PlatePending && manualPlate;
    internal int CachedNames => names.Count;
    internal bool SuppressDetail => Busy || Volatile.Read(ref backgroundCallbacks) != 0;
    internal bool QuietAudioAvailable => soundHook is not null && systemSoundHook is not null;
    internal int SuppressedListingErrors { get; private set; }
    internal int SuppressedPlateErrors { get; private set; }
    internal int SuppressedSounds => Volatile.Read(ref suppressedSounds);
    internal int PassedThroughSounds => Volatile.Read(ref passedThroughSounds);
    internal int MutedSystemSounds => Volatile.Read(ref mutedSystemSounds);
    internal bool CanPrefetch(DateTimeOffset now) => !Busy && !DetailOpen && now >= nextListing;

    internal PartyPreview(IFramework framework, NativePartyFinder native, IGameInteropProvider interop, Func<bool> nativeFinderHovered,
        Func<ulong, bool> switchTarget, Func<ushort, (string Name, string Region)> world)
    {
        this.framework = framework; this.native = native; this.nativeFinderHovered = nativeFinderHovered; this.switchTarget = switchTarget; this.world = world;
        try
        {
            packetHook = interop.HookFromAddress<CharaCard.Delegates.HandleCurrentCharaCardDataPacket>(
                CharaCard.Addresses.HandleCurrentCharaCardDataPacket.Value, OnPacket);
            listingHook = interop.HookFromAddress<AgentLookingForGroup.Delegates.PopulateListingData>(
                AgentLookingForGroup.Addresses.PopulateListingData.Value, OnListing);
        }
        catch { Error = "Some member collection is unavailable on this game build."; }
        try
        {
            soundHook = interop.HookFromAddress<UIGlobals.Delegates.PlaySoundEffect>(
                UIGlobals.Addresses.PlaySoundEffect.Value, OnSound);
        }
        catch { }
        try
        {
            systemSoundHook = interop.HookFromAddress<SoundManager.Delegates.PlaySystemSound>(
                SoundManager.Addresses.PlaySystemSound.Value, OnSystemSound);
        }
        catch { }
    }
    private void OnListing(AgentLookingForGroup* agent, AgentLookingForGroup.Detailed* data)
    {
        ulong id = 0;
        var owned = BackgroundId;
        try
        {
            if ((enabled || keepLateReplies) && NativePartyFinder.CopyDetail(data, DateTimeOffset.UtcNow) is { } copy)
            { id = copy.Id; if (enabled && received.Count < 256) received.Enqueue(copy); }
        }
        catch { }
        var late = SweepPolicy.IsLateBackground(id, owned, WasBackgroundListing(id, DateTimeOffset.UtcNow), switchTarget(id), id != 0 && id == ManualId);
        if (late) owned = id;
        var manual = owned != 0 && (id != 0 && id == ManualId || nativeFinderHovered());
        if (!manual && framework.IsInFrameworkUpdateThread && owned != 0)
        {
            native.RememberFocus();
            native.SuppressBackgroundDetail();
            if (id != 0 && id == owned) native.HideBackgroundDetail(id);
        }
        var quiet = !manual && owned != 0;
        if (quiet) { quietSounds++; Interlocked.Increment(ref backgroundCallbacks); }
        try { listingHook!.Original(agent, data); }
        finally { if (quiet) { quietSounds--; Interlocked.Decrement(ref backgroundCallbacks); } }
        if (framework.IsInFrameworkUpdateThread && owned != 0 && manual)
        {
            native.ReleaseBackgroundDetail(id != 0 ? id : native.VisibleDetailId, false);
            Interlocked.Exchange(ref background, 0);
        }
        else if (framework.IsInFrameworkUpdateThread && owned != 0)
        {
            var shown = id != 0 ? id : native.VisibleDetailId;
            if (late && shown != 0) { native.SuppressBackgroundDetail(); CloseUnexpectedQuiet(shown); }
            else if (shown == owned) { listingErrorUntil = default; native.SuppressBackgroundDetail(); native.HideBackgroundDetail(shown); }
            else if (shown != 0) { native.SuppressBackgroundDetail(); CloseUnexpectedQuiet(shown); }
        }
    }
    private void OnSound(uint effectId, SoundData** outSoundDataPad, SoundData** outSoundData, byte a4)
    {
        if (quietSounds > 0 && outSoundDataPad == null && outSoundData == null)
        {
            Interlocked.Increment(ref suppressedSounds);
            return;
        }
        var quiet = quietSounds > 0;
        var before = mutedSystemCalls;
        soundHook!.Original(effectId, outSoundDataPad, outSoundData, a4);
        if (quiet && before == mutedSystemCalls) Interlocked.Increment(ref passedThroughSounds);
    }
    private SoundData* OnSystemSound(SoundManager* manager, CStringPointer path, float volume, uint soundNumber,
        uint fadeInDuration, bool autoRelease, SoundVolumeCategory volumeCategory)
    {
        if (quietSounds > 0) { mutedSystemCalls++; Interlocked.Increment(ref mutedSystemSounds); volume = 0; }
        return systemSoundHook!.Original(manager, path, volume, soundNumber, fadeInDuration, autoRelease, volumeCategory);
    }
    private int suppressedSounds, passedThroughSounds, mutedSystemSounds, backgroundCallbacks;
    internal bool Quiet(Func<bool> action)
    {
        quietSounds++;
        try { return action(); }
        finally { quietSounds--; }
    }
    private void CloseUnexpectedQuiet(ulong id) => Quiet(() => { native.CloseUnexpectedBackgroundDetail(id); return true; });
    private void ReleaseBackgroundQuiet(ulong id) => Quiet(() => { native.ReleaseBackgroundDetail(id, true); return true; });
    internal bool QuietLog(uint id, DateTimeOffset now, bool manual, bool listingVerified, bool plateVerified)
    {
        var listingPending = listingVerified && listingErrorUntil != default && now <= listingErrorUntil;
        var platePending = SweepPolicy.OwnedPlatePending(Interlocked.Read(ref requested), Interlocked.Read(ref plateErrorId), plateVerified, now, plateErrorUntil);
        if (!SweepPolicy.IsOwnedError(id, listingPending, platePending, manual)) return false;
        if (id == 974) { listingFailure = true; SuppressedListingErrors++; }
        else { plateFailure = true; SuppressedPlateErrors++; }
        return true;
    }
    private void OnPacket(CharaCard* self, CharaCardPacket* packet)
    {
        var owned = false;
        try
        {
            if ((enabled || keepLateReplies) && packet != null && packet->ContentId != 0 && packet->ContentId == (ulong)Interlocked.Read(ref requested))
            {
                owned = true;
                var text = Encoding.UTF8.GetString(packet->Name).TrimEnd('\0');
                var start = 0;
                while (start < text.Length && !char.IsLetter(text[start]) && text[start] != ' ') start++;
                if (replies.Count < 8) replies.Enqueue((packet->ContentId, text[start..].Trim(), packet->WorldId));
            }
        }
        catch { }
        var quiet = owned && (!framework.IsInFrameworkUpdateThread || !native.AdventurerPlateOpen());
        if (quiet) quietSounds++;
        try { packetHook!.Original(self, packet); }
        finally { if (quiet) quietSounds--; }
    }
    internal PreviewMember Resolve(PreviewMember member)
    {
        if (member.Name.Length > 0 && member.WorldId > 0)
        {
            Remember(member.Id, member.Name, member.WorldId);
            var direct = world(member.WorldId);
            return member with { World = direct.Name, Region = direct.Region,
                State = direct.Name.Length > 0 ? NameState.Resolved : NameState.Unavailable };
        }
        if (member.Id == 0) return member with { State = NameState.Unavailable };
        if (!names.TryGetValue(member.Id, out var known)) return member with { State = packetHook is null ? NameState.Unavailable : NameState.Pending };
        var w = world(known.World);
        return member with { Name = known.Name, WorldId = known.World, World = w.Name, Region = w.Region,
            State = known.Name.Length > 0 && w.Name.Length > 0 ? NameState.Resolved : NameState.Unavailable };
    }
    internal void Remember(ulong id, string name, ushort worldId)
    {
        if (id == 0 || name.Length == 0 || worldId == 0) return;
        CacheName(id, name, worldId, DateTimeOffset.UtcNow.AddMinutes(5));
    }
    private void CacheName(ulong id, string name, ushort worldId, DateTimeOffset until)
    {
        if (names.Count >= 4096 && !names.ContainsKey(id))
            foreach (var old in names.OrderBy(kv => kv.Value.Until).Take(512).Select(kv => kv.Key).ToArray()) names.Remove(old);
        names[id] = (name, worldId, until);
    }
    private void ApplyNames()
    {
        foreach (var detail in Parties.Values)
            for (var i = 0; i < detail.Members.Length; i++) detail.Members[i] = Resolve(detail.Members[i]);
    }
    internal void Start()
    {
        keepLateReplies = false; enabled = true;
        if (listingHook is not null && !listingHook.IsEnabled) listingHook.Enable();
        if (packetHook is not null && !packetHook.IsEnabled) packetHook.Enable();
        if (systemSoundHook is not null && !systemSoundHook.IsEnabled) systemSoundHook.Enable();
        if (soundHook is not null && !soundHook.IsEnabled) soundHook.Enable();
    }
    internal void Tick(DateTimeOffset now)
    {
        Start();
        if (now < nextRead && received.IsEmpty) return;
        nextRead = now.AddMilliseconds(200);
        var visible = native.VisibleDetailId;
        var owned = BackgroundId;
        if (visible == 0) ManualId = 0;
        if (visible != 0 && visible != BackgroundId)
        {
            if (visible != ManualId && (owned != 0 || WasBackgroundListing(visible, now)) && !nativeFinderHovered())
            { native.SuppressBackgroundDetail(); native.HideBackgroundDetail(visible); CloseUnexpectedQuiet(visible); }
            else
            {
                if (owned != 0)
                {
                    native.ReleaseBackgroundDetail(visible, false);
                    Interlocked.Exchange(ref background, 0);
                }
                ManualId = visible;
                var agent = AgentLookingForGroup.Instance();
                if (agent != null && (!Parties.TryGetValue(visible, out var previous) || now - previous.Collected > TimeSpan.FromSeconds(1)))
                    if (NativePartyFinder.CopyDetail(&agent->LastViewedListing, now) is { } manual) received.Enqueue(manual);
            }
        }
        var changed = false;
        if (listingFailure)
        {
            var failed = BackgroundId;
            if (failed != 0) ReleaseBackgroundQuiet(failed);
            Interlocked.Exchange(ref background, 0);
            listingFailure = false; listingErrorUntil = default;
        }
        if (plateFailure)
        {
            var failed = (ulong)Interlocked.Exchange(ref plateErrorId, 0);
            if (failed == (ulong)Interlocked.Read(ref requested)) Interlocked.Exchange(ref requested, 0);
            if (failed != 0 && (!names.TryGetValue(failed, out var known) || known.Name.Length == 0))
            { CacheName(failed, "", 0, now.AddMinutes(10)); changed = true; }
            plateFailure = false; plateErrorUntil = default;
        }
        while (received.TryDequeue(out var detail))
        {
            if (Parties.Count >= 1000 && !Parties.ContainsKey(detail.Id))
                Parties.Remove(Parties.MinBy(kv => kv.Value.Collected).Key);
            Parties[detail.Id] = detail; changed = true;
        }
        owned = BackgroundId;
        if (owned != 0)
        {
            if (visible == owned) native.HideBackgroundDetail(owned);
            if (now >= listingUntil || Parties.TryGetValue(owned, out var collected) && collected.Collected >= listingStarted)
            { ReleaseBackgroundQuiet(owned); Interlocked.Exchange(ref background, 0); }
        }
        while (replies.TryDequeue(out var reply))
        {
            if (reply.Id != (ulong)Interlocked.Read(ref requested)) continue;
            Interlocked.Exchange(ref requested, 0);
            Interlocked.Exchange(ref plateErrorId, 0);
            CacheName(reply.Id, reply.Name, reply.World, now.AddMinutes(5)); changed = true;
        }
        if (Interlocked.Read(ref requested) != 0 && now >= requestUntil)
        {
            var id = (ulong)Interlocked.Exchange(ref requested, 0);
            Interlocked.Exchange(ref plateErrorId, 0);
            CacheName(id, "", 0, now.AddMinutes(10)); changed = true;
        }
        if (changed) ApplyNames();
        foreach (var id in Parties.Where(kv => now - kv.Value.Collected > TimeSpan.FromMinutes(10) || kv.Value.Recruitment?.Expires <= now).Select(kv => kv.Key).ToArray()) Parties.Remove(id);
    }
    internal void ResolveNext(IEnumerable<PartyDetail> parties, DateTimeOffset now, bool manualDetail = false)
    {
        if (packetHook is null || Interlocked.Read(ref requested) != 0 || now < nextRequest ||
            SweepPolicy.PlateInteractionBlocks(manualDetail, nativeFinderHovered(), native.AdventurerPlateOpen())) return;
        foreach (var member in parties.SelectMany(p => p.Members).DistinctBy(m => m.Id))
        {
            if (member.Id == 0 || member.Resolved || names.TryGetValue(member.Id, out var known) && known.Until > now) continue;
            var card = CharaCard.Instance(); if (card == null) return;
            manualPlate = manualDetail;
            Interlocked.Exchange(ref requested, (long)member.Id);
            Interlocked.Exchange(ref plateErrorId, (long)member.Id); plateErrorUntil = now.AddSeconds(7);
            nextRequest = now.AddMilliseconds(900); requestUntil = now.AddSeconds(5);
            Quiet(() => { card->RequestCharaCardForContentId(member.Id); return true; }); return;
        }
    }
    internal bool Prefetch(ulong id, DateTimeOffset now)
    {
        if (id == 0 || Busy || DetailOpen || now < nextListing || !native.PageListingIds().Contains(id)) return false;
        if (recentListings.Count >= 256)
            foreach (var old in recentListings.OrderBy(pair => pair.Value).Take(64)) recentListings.TryRemove(old.Key, out _);
        recentListings[id] = now.AddSeconds(30);
        Interlocked.Exchange(ref background, (long)id); listingStarted = now; listingUntil = now.AddSeconds(5); nextListing = now.AddSeconds(1);
        listingErrorUntil = listingUntil; listingFailure = false;
        if (Quiet(() => native.OpenListing(id, false))) return true;
        Interlocked.Exchange(ref background, 0); return false;
    }
    internal void Pause(bool keepSound = false)
    {
        enabled = false; keepLateReplies = keepSound;
        ReleaseBackgroundQuiet(BackgroundId); Interlocked.Exchange(ref background, 0);
        if (!keepSound)
        {
            Interlocked.Exchange(ref requested, 0); recentListings.Clear();
            listingErrorUntil = plateErrorUntil = default; Interlocked.Exchange(ref plateErrorId, 0); listingFailure = plateFailure = false;
        }
        while (replies.TryDequeue(out _)) { }
        while (received.TryDequeue(out _)) { }
        if (!keepSound && packetHook?.IsEnabled == true) packetHook.Disable();
        if (!keepSound && listingHook?.IsEnabled == true) listingHook.Disable();
        if (!keepSound && soundHook?.IsEnabled == true) soundHook.Disable();
        if (!keepSound && systemSoundHook?.IsEnabled == true) systemSoundHook.Disable();
    }
    internal void Stop(bool clearNames = true) { Pause(); ManualId = 0; Parties.Clear(); if (clearNames) names.Clear(); }
    public void Dispose() { Stop(); soundHook?.Dispose(); systemSoundHook?.Dispose(); listingHook?.Dispose(); packetHook?.Dispose(); }
    private bool WasBackgroundListing(ulong id, DateTimeOffset now) =>
        recentListings.TryGetValue(id, out var until) && now <= until;
}
