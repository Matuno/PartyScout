using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using PartyScout.Core;

namespace PartyScout;

internal sealed record PartySnapshot(bool InParty, ulong Leader, byte[] Jobs, long PartyId, bool Alliance);
internal sealed record NativeView(bool Open, Vector2 FinderPosition, Vector2 FinderSize, PartySnapshot Party, uint CurrentJob);
internal sealed record FinderPage(byte Area, byte Category, string Key, bool Previous, bool Next, int ExpectedListings);
internal sealed record SocialRoster(SocialPerson[] People, bool FreeCompanyVerified, int FreeCompanyTotal, ulong FreeCompanyId);

// All methods run on IFramework; no native pointer survives a call.
internal sealed unsafe class NativePartyFinder(IGameGui gui, IPartyList party, IPlayerState player)
{
    private ushort hiddenAddon;
    private ushort quietFinderAddon;
    private bool previousFinderDisableFocus;
    private ushort hiddenDetail;
    private ushort backgroundDetailAddon;
    private ushort hiddenJoinDetail, hiddenJoinConfirmation, quietJoinDetail, quietJoinConfirmation;
    private bool previousJoinDetailFocus, previousJoinConfirmationFocus;
    private bool previousDisableShowOnOpen, previousDisableShowHideSoundEffects;
    private short previousShowSoundEffectId;
    private string previousFocusName = "";
    private ushort previousFocusId;
    public bool Hidden => hiddenAddon != 0;
    private AtkUnitBase* Addon(string name) => (AtkUnitBase*)gui.GetAddonByName(name).Address;
    private static AtkUnitBase* AddonById(uint id, string name)
    {
        var stage = AtkStage.Instance();
        var addon = id is > 0 and <= ushort.MaxValue && stage != null && stage->RaptureAtkUnitManager != null
            ? stage->RaptureAtkUnitManager->GetAddonById((ushort)id) : null;
        return addon != null && addon->NameString == name ? addon : null;
    }
    internal bool FinderRootVisible
    {
        get { var addon = Addon("LookingForGroup"); return addon != null && addon->IsReady && addon->IsVisible && addon->RootNode != null && addon->RootNode->IsVisible(); }
    }
    internal void PrepareFinderShow(nint address)
    {
        var addon = (AtkUnitBase*)address;
        if (addon == null) return;
        if (quietFinderAddon != addon->Id)
        {
            quietFinderAddon = addon->Id;
            previousFinderDisableFocus = addon->DisableFocusOnShow;
        }
        addon->DisableFocusOnShow = true;
    }
    internal void FinishFinderShow(nint address = 0)
    {
        var addon = address != 0 ? (AtkUnitBase*)address : Addon("LookingForGroup");
        if (quietFinderAddon != 0 && addon != null && addon->Id == quietFinderAddon)
            addon->DisableFocusOnShow = previousFinderDisableFocus;
        quietFinderAddon = 0;
    }

    public NativeView Capture()
    {
        var addon = Addon("LookingForGroup");
        var open = addon != null && addon->IsVisible && addon->IsReady && addon->RootNode != null && addon->RootNode->IsVisible();
        var position = open ? new Vector2(addon->X, addon->Y) : Vector2.Zero;
        var size = open ? new Vector2(addon->GetScaledWidth(true), addon->GetScaledHeight(true)) : Vector2.Zero;
        var jobs = Enumerable.Range(0, party.Length).Select(i => (byte)(party[i]?.ClassJob.RowId ?? 0)).ToArray();
        var leader = party[(int)party.PartyLeaderIndex]?.ContentId ?? 0;
        var cross = InfoProxyCrossRealm.Instance();
        var inCross = cross != null && cross->IsInCrossRealmParty;
        var alliance = party.IsAlliance;
        if (inCross && cross->LocalPlayerGroupIndex < 6)
        {
            ref var group = ref cross->CrossRealmGroups[cross->LocalPlayerGroupIndex];
            var count = Math.Min((int)group.GroupMemberCount, 8);
            jobs = new byte[count]; leader = 0;
            for (var i = 0; i < count; i++)
            {
                jobs[i] = group.GroupMembers[i].ClassJobId;
                if (group.GroupMembers[i].IsPartyLeader) leader = group.GroupMembers[i].ContentId;
            }
            alliance |= cross->IsInAllianceRaid;
        }
        return new NativeView(open, position, size, new PartySnapshot(inCross || party.Length > 0, leader, jobs, party.PartyId, alliance), player.ClassJob.RowId);
    }

    public bool DutyPrompt => Visible("ContentsFinderConfirm");
    public bool HasOtherDialog
    {
        get
        {
            for (var index = 1; index <= 64; index++)
            {
                var addon = (AtkUnitBase*)gui.GetAddonByName("SelectYesno", index).Address;
                if (addon == null) break;
                if (addon->IsVisible) return true;
            }
            return false;
        }
    }
    private bool Visible(string name) { var a = Addon(name); return a != null && a->IsVisible; }

    public void Open()
    {
        var agent = AgentLookingForGroup.Instance();
        if (agent != null && !agent->IsAgentActive()) agent->Show();
    }
    public bool Refresh()
    {
        var agent = AgentLookingForGroup.Instance();
        return agent != null && agent->IsAgentActive() && agent->RequestListingsUpdate();
    }
    public void SetHidden(bool hide)
    {
        var addon = Addon("LookingForGroup");
        if (addon == null || !addon->IsReady) { hiddenAddon = 0; return; }
        if (hide)
        {
            if (addon->IsVisible && addon->RootNode != null)
            {
                if (hiddenAddon == 0 && !addon->RootNode->IsVisible()) return;
                hiddenAddon = addon->Id;
                addon->RootNode->ToggleVisibility(false);
            }
            return;
        }
        if (hiddenAddon != 0)
        {
            if (addon->Id == hiddenAddon && addon->RootNode != null) addon->RootNode->ToggleVisibility(true);
            hiddenAddon = 0;
        }
    }
    public void Close()
    {
        SetHidden(false);
        foreach (var name in new[] { "LookingForGroupDetail", "LookingForGroup" })
        {
            var a = Addon(name); if (a != null && a->IsVisible) a->Close(true);
        }
    }
    public bool OpenListing(ulong id, bool reveal = true)
    {
        RememberFocus();
        var agent = AgentLookingForGroup.Instance();
        if (agent == null) return false;
        if (!reveal) SuppressBackgroundDetail();
        var opened = agent->OpenListing(id);
        if (!opened && !reveal) RestoreBackgroundDetail();
        return opened;
    }

    public Listing? Detail(ulong id, DateTimeOffset now)
    {
        var agent = AgentLookingForGroup.Instance();
        var addon = Addon("LookingForGroupDetail");
        if (agent == null || addon == null || !addon->IsReady || !addon->IsVisible) return null;
        ref var d = ref agent->LastViewedListing;
        if (d.ListingId != id || d.TotalSlots is < 1 or > 48 || d.SlotsFilled > d.TotalSlots) return null;
        return new Listing(d.ListingId, d.LeaderContentId, d.DutyId, true, d.CommentString,
            d.TotalSlots, d.SlotsFilled, d.Jobs.ToArray(), d.SlotFlags.ToArray(),
            (d.JoinConditionFlags & AgentLookingForGroup.JoinCondition.OnePlayerPerJob) != 0,
            (d.JoinConditionFlags & AgentLookingForGroup.JoinCondition.Private) != 0,
            d.NumberOfParties, d.AvgItemLv, now, now.AddSeconds(Math.Min(d.TimeLeft, 3600)))
        { Host = d.LeaderString, HomeWorld = d.HomeWorld, Objective = (byte)d.Objective, Conditions = (byte)d.CompletionStatus,
            DutySettings = (byte)d.DutyFinderSettingFlags, Loot = (byte)d.LootRule, Languages = (byte)d.LanguageFlags, Beginners = d.BeginnerFriendly != 0 };
    }

    internal static PartyDetail? CopyDetail(AgentLookingForGroup.Detailed* data, DateTimeOffset now)
    {
        if (data == null || data->ListingId == 0 || data->LeaderContentId == 0 || data->TotalSlots is < 1 or > 48 || data->SlotsFilled > data->TotalSlots) return null;
        var members = new List<PreviewMember>();
        var ids = new HashSet<ulong>();
        for (var i = 0; i < data->TotalSlots; i++)
            if (data->MemberContentIds[i] != 0 && ids.Add(data->MemberContentIds[i]))
                members.Add(new PreviewMember(data->MemberContentIds[i], data->Jobs[i],
                    data->MemberContentIds[i] == data->LeaderContentId ? data->LeaderString : "")
                    { Slot = i, WorldId = data->MemberContentIds[i] == data->LeaderContentId ? data->HomeWorld : (ushort)0 });
        if (!ids.Contains(data->LeaderContentId)) members.Insert(0, new PreviewMember(data->LeaderContentId, 0, data->LeaderString) { WorldId = data->HomeWorld });
        var listing = new Listing(data->ListingId, data->LeaderContentId, data->DutyId, true, data->CommentString,
            data->TotalSlots, data->SlotsFilled, data->Jobs.ToArray(), data->SlotFlags.ToArray(),
            (data->JoinConditionFlags & AgentLookingForGroup.JoinCondition.OnePlayerPerJob) != 0,
            (data->JoinConditionFlags & AgentLookingForGroup.JoinCondition.Private) != 0, data->NumberOfParties, data->AvgItemLv, now, now.AddSeconds(Math.Min(data->TimeLeft, 3600)))
            { Host = data->LeaderString, HomeWorld = data->HomeWorld, Objective = (byte)data->Objective, Conditions = (byte)data->CompletionStatus,
                DutySettings = (byte)data->DutyFinderSettingFlags, Loot = (byte)data->LootRule, Languages = (byte)data->LanguageFlags, Beginners = data->BeginnerFriendly != 0 };
        return new PartyDetail(data->ListingId, data->DutyId, data->CommentString, members.ToArray()) { Recruitment = listing, Collected = now };
    }
    internal ulong VisibleDetailId
    {
        get
        {
            var addon = Addon("LookingForGroupDetail"); var agent = AgentLookingForGroup.Instance();
            return addon != null && addon->IsReady && addon->IsVisible && agent != null ? agent->LastViewedListing.ListingId : 0;
        }
    }
    internal void HideBackgroundDetail(ulong id)
    {
        var addon = Addon("LookingForGroupDetail");
        if (VisibleDetailId != id || addon == null || addon->RootNode == null) return;
        hiddenDetail = addon->Id; addon->RootNode->ToggleVisibility(false);
        RestoreFocus(addon);
    }
    private void RestoreFocus(AtkUnitBase* addon)
    {
        var stage = AtkStage.Instance();
        if (stage != null && stage->RaptureAtkUnitManager != null && stage->RaptureAtkUnitManager->FocusedAddon == addon && previousFocusName.Length > 0)
        {
            var previous = Addon(previousFocusName);
            if (previous != null && previous->Id == previousFocusId && previous->IsVisible && previous->IsReady) previous->Focus();
        }
    }
    internal bool OwnsJoinWindow(string name, nint address, ulong target, bool waitingForConfirmation)
    {
        var addon = (AtkUnitBase*)address;
        var agent = AgentLookingForGroup.Instance();
        if (addon == null || agent == null || target == 0 || agent->LastViewedListing.ListingId != target) return false;
        return name == "LookingForGroupDetail" || name == "SelectYesno" && waitingForConfirmation &&
            JoinPolicy.OwnsConfirmation(target, agent->LastViewedListing.ListingId, agent->JoinConfirmAddonId, addon->Id);
    }
    internal void PrepareJoinShow(string name, nint address)
    {
        var addon = (AtkUnitBase*)address;
        if (addon == null) return;
        if (name == "LookingForGroupDetail")
        {
            if (quietJoinDetail != addon->Id) { quietJoinDetail = addon->Id; previousJoinDetailFocus = addon->DisableFocusOnShow; }
        }
        else if (name == "SelectYesno")
        {
            if (quietJoinConfirmation != addon->Id) { quietJoinConfirmation = addon->Id; previousJoinConfirmationFocus = addon->DisableFocusOnShow; }
        }
        else return;
        addon->DisableFocusOnShow = true;
    }
    internal void FinishJoinShow(nint address = 0)
    {
        var addon = (AtkUnitBase*)address;
        if (addon == null || addon->Id == quietJoinDetail)
        {
            var detail = addon != null ? addon : AddonById(quietJoinDetail, "LookingForGroupDetail");
            if (detail != null && detail->Id == quietJoinDetail) detail->DisableFocusOnShow = previousJoinDetailFocus;
            quietJoinDetail = 0;
        }
        if (addon == null || addon->Id == quietJoinConfirmation)
        {
            var confirmation = addon != null ? addon : AddonById(quietJoinConfirmation, "SelectYesno");
            if (confirmation != null && confirmation->Id == quietJoinConfirmation) confirmation->DisableFocusOnShow = previousJoinConfirmationFocus;
            quietJoinConfirmation = 0;
        }
    }
    internal void HideJoinWindow(string name, nint address)
    {
        var addon = (AtkUnitBase*)address;
        if (addon == null || !addon->IsReady || !addon->IsVisible || addon->RootNode == null) return;
        ref var hidden = ref (name == "LookingForGroupDetail" ? ref hiddenJoinDetail : ref hiddenJoinConfirmation);
        if (hidden == 0 && !addon->RootNode->IsVisible()) return;
        hidden = addon->Id;
        addon->RootNode->ToggleVisibility(false);
        RestoreFocus(addon);
    }
    internal void HideJoinDetail(ulong target)
    {
        var addon = Addon("LookingForGroupDetail");
        if (OwnsJoinWindow("LookingForGroupDetail", (nint)addon, target, false))
            HideJoinWindow("LookingForGroupDetail", (nint)addon);
    }
    internal void ReleaseJoinWindows(ulong target, bool cancel, bool waitingForConfirmation, bool requestedDetail)
    {
        FinishJoinShow();
        var currentAgent = AgentLookingForGroup.Instance();
        var confirmationId = hiddenJoinConfirmation != 0 ? hiddenJoinConfirmation :
            waitingForConfirmation && currentAgent != null ? currentAgent->JoinConfirmAddonId : (ushort)0;
        var confirmation = AddonById(confirmationId, "SelectYesno");
        if (confirmation != null && confirmation->IsVisible && confirmation->RootNode != null)
        {
            var owned = currentAgent != null && JoinPolicy.OwnsConfirmation(target, currentAgent->LastViewedListing.ListingId, currentAgent->JoinConfirmAddonId, confirmation->Id);
            if ((!cancel || !owned || !confirmation->IsReady || !Click(confirmation, ((AddonSelectYesno*)confirmation)->NoButton)) &&
                confirmationId == hiddenJoinConfirmation)
                confirmation->RootNode->ToggleVisibility(true);
        }
        hiddenJoinConfirmation = 0;
        var detail = hiddenJoinDetail != 0 ? AddonById(hiddenJoinDetail, "LookingForGroupDetail") :
            requestedDetail && VisibleDetailId == target ? Addon("LookingForGroupDetail") : null;
        if (detail != null && detail->RootNode != null)
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent != null && agent->LastViewedListing.ListingId == target) detail->Close(true);
            else detail->RootNode->ToggleVisibility(true);
        }
        hiddenJoinDetail = 0;
    }
    internal void RememberFocus()
    {
        var stage = AtkStage.Instance();
        var focused = stage != null && stage->RaptureAtkUnitManager != null ? stage->RaptureAtkUnitManager->FocusedAddon : null;
        if (focused == null) { previousFocusName = ""; previousFocusId = 0; }
        else if (focused->NameString != "LookingForGroupDetail") { previousFocusName = focused->NameString; previousFocusId = focused->Id; }
    }
    internal bool SuppressBackgroundDetail()
    {
        var addon = Addon("LookingForGroupDetail");
        if (addon == null || !addon->IsReady || addon->RootNode == null) return false;
        if (backgroundDetailAddon == addon->Id) return true;
        backgroundDetailAddon = addon->Id;
        previousDisableShowOnOpen = addon->DisableShowOnOpen;
        previousDisableShowHideSoundEffects = addon->DisableShowHideSoundEffects;
        previousShowSoundEffectId = addon->ShowSoundEffectId;
        addon->DisableShowOnOpen = true;
        addon->DisableShowHideSoundEffects = true;
        addon->ShowSoundEffectId = 0;
        return true;
    }
    private void RestoreBackgroundDetail()
    {
        var addon = Addon("LookingForGroupDetail");
        if (addon != null && addon->Id == backgroundDetailAddon)
        {
            addon->DisableShowOnOpen = previousDisableShowOnOpen;
            addon->DisableShowHideSoundEffects = previousDisableShowHideSoundEffects;
            addon->ShowSoundEffectId = previousShowSoundEffectId;
        }
        backgroundDetailAddon = 0;
    }
    internal void CloseUnexpectedBackgroundDetail(ulong id)
    {
        var addon = Addon("LookingForGroupDetail");
        if (addon != null && addon->Id == backgroundDetailAddon && VisibleDetailId == id)
        {
            if (addon->RootNode != null) addon->RootNode->ToggleVisibility(false);
            addon->Close(true);
        }
        hiddenDetail = 0;
    }
    internal void ReleaseBackgroundDetail(ulong id, bool close)
    {
        var addon = Addon("LookingForGroupDetail");
        var agent = AgentLookingForGroup.Instance();
        var manual = !close && id != 0 && addon != null && addon->Id == backgroundDetailAddon &&
            agent != null && agent->LastViewedListing.ListingId == id;
        var addonId = addon != null ? addon->Id : (ushort)0;
        if (addon != null && (addon->Id == backgroundDetailAddon || addon->Id == hiddenDetail))
        {
            if (close) addon->Close(true);
            else if (addon->Id == hiddenDetail && addon->RootNode != null) addon->RootNode->ToggleVisibility(true);
        }
        hiddenDetail = 0;
        RestoreBackgroundDetail();
        addon = Addon("LookingForGroupDetail");
        if (manual && addon != null && addon->Id == addonId && addon->IsReady && !addon->IsVisible) addon->Show(false, 0);
    }
    internal PreviewMember[] CurrentMembers()
    {
        var count = party.Length;
        if (count > 0)
        {
            var members = Inspection.SlottedMembers(count, i =>
            {
                var member = party[i];
                return member is null ? null : new PreviewMember(member.ContentId, (byte)member.ClassJob.RowId, member.Name.TextValue)
                    { WorldId = (ushort)member.World.RowId };
            });
            if (members.Length > 0) return members;
        }
        var cross = InfoProxyCrossRealm.Instance();
        if (cross == null || !cross->IsInCrossRealmParty || cross->LocalPlayerGroupIndex >= 6) return [];
        ref var group = ref cross->CrossRealmGroups[cross->LocalPlayerGroupIndex];
        var result = new List<PreviewMember>();
        for (var i = 0; i < Math.Min(8, (int)group.GroupMemberCount); i++)
        {
            ref var m = ref group.GroupMembers[i];
            if (m.ContentId != 0 || m.HomeWorld > 0 && m.NameString.Length > 0)
                result.Add(new PreviewMember(m.ContentId, m.ClassJobId, m.NameString) { WorldId = m.HomeWorld > 0 ? (ushort)m.HomeWorld : (ushort)0, Slot = i });
        }
        return result.ToArray();
    }
    internal SocialRoster ReadSocial()
    {
        var result = new List<SocialPerson>();
        void Read(InfoProxyCommonList* source, SocialKind kind, int limit)
        {
            if (source == null || source->CharData == null || source->EntryCount > limit) return;
            for (var i = 0; i < source->EntryCount; i++)
            {
                ref var m = ref source->CharData[i];
                if (m.ContentId != 0 && !m.WaitingForFriendListApproval) result.Add(new(m.ContentId, m.NameString, m.HomeWorld, kind));
            }
        }
        Read((InfoProxyCommonList*)InfoProxyFriendList.Instance(), SocialKind.Friend, 200);
        var fc = InfoProxyFreeCompanyMember.Instance();
        var start = result.Count;
        Read((InfoProxyCommonList*)fc, SocialKind.FreeCompany, 200);
        var company = InfoProxyFreeCompany.Instance();
        var verified = fc != null && company != null && fc->FreeCompanyId != 0 && company->Id == fc->FreeCompanyId &&
            result.Skip(start).Any(m => m.Id == player.ContentId);
        if (!verified) result.RemoveRange(start, result.Count - start);
        return new(result.ToArray(), verified, verified ? company->TotalMembers : 0, verified ? fc->FreeCompanyId : 0);
    }
    internal void RefreshSocial()
    {
        var friends = InfoProxyFriendList.Instance(); if (friends != null) friends->RequestData();
        var fc = InfoProxyFreeCompanyMember.Instance(); if (fc != null && fc->FreeCompanyId != 0) fc->RequestData();
    }
    internal FinderPage? Page()
    {
        var addon = (AddonLookingForGroup*)Addon("LookingForGroup"); var agent = AgentLookingForGroup.Instance();
        if (addon == null || !addon->IsReady || agent == null) return null;
        bool Enabled(AtkComponentButton* b) => b != null && b->OwnerNode != null && b->IsEnabled;
        return new(agent->SearchAreaTab, agent->CategoryTab, addon->CurrentPageTextNode == null ? "" : addon->CurrentPageTextNode->NodeText.ToString(), Enabled(addon->PreviousPageButton), Enabled(addon->NextPageButton), agent->NumberOfListingsDisplayed);
    }
    internal HashSet<ulong> PageListingIds()
    {
        var agent = AgentLookingForGroup.Instance();
        if (agent == null) return [];
        ReadOnlySpan<ulong> mapped = agent->Listings.ListingIds;
        var count = agent->NumberOfListingsDisplayed;
        // The SDK maps 50 IDs, but the 800-byte buffer also holds expanded 100-listing pages.
        fixed (ulong* first = mapped)
        {
            if (count > mapped.Length && count <= 100 && mapped.Length == 50 &&
                sizeof(AgentLookingForGroup.ListingsSub) == 100 * sizeof(ulong) && (void*)first == &agent->Listings)
                return SweepPolicy.PageListingIds(new ReadOnlySpan<ulong>(first, count), count);
        }
        return SweepPolicy.PageListingIds(mapped, count);
    }
    internal bool AdventurerPlateOpen()
    {
        var agent = AgentCharaCard.Instance();
        return agent != null && agent->IsAgentActive();
    }
    internal bool AllScope(bool area)
    {
        var addon = (AddonLookingForGroup*)Addon("LookingForGroup");
        if (addon == null || !addon->IsReady) return false;
        var radio = area ? addon->WorldSelectButtons[0].Value : addon->CategoryButtons[0].Value;
        return radio != null && radio->SetActive();
    }
    internal bool TurnPage(bool forward)
    {
        var addon = (AddonLookingForGroup*)Addon("LookingForGroup");
        return addon != null && addon->IsReady && Click((AtkUnitBase*)addon, forward ? addon->NextPageButton : addon->PreviousPageButton);
    }

    public bool Leave()
    {
        var proxy = InfoProxyPartyMember.Instance();
        return proxy != null && proxy->LeaveParty();
    }
    public bool Join(ulong id)
    {
        var agent = AgentLookingForGroup.Instance();
        var addon = (AddonLookingForGroupDetail*)Addon("LookingForGroupDetail");
        return agent != null && agent->LastViewedListing.ListingId == id && addon != null && addon->IsReady && addon->IsVisible &&
            Click(&addon->AtkUnitBase, addon->JoinPartyButton);
    }
    public bool ConfirmJoin(ulong id)
    {
        var agent = AgentLookingForGroup.Instance();
        var yesno = agent != null ? (AddonSelectYesno*)AddonById(agent->JoinConfirmAddonId, "SelectYesno") : null;
        // The PF agent records its own confirmation addon; never accept an unrelated Yes/No.
        if (agent == null || agent->LastViewedListing.ListingId != id || yesno == null ||
            !yesno->IsVisible || !yesno->IsReady || agent->JoinConfirmAddonId == 0 || yesno->Id != agent->JoinConfirmAddonId) return false;
        return Click(&yesno->AtkUnitBase, yesno->YesButton);
    }
    private static bool Click(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (button == null || button->OwnerNode == null || !button->IsEnabled || !button->OwnerNode->IsVisible()) return false;
        var e = button->OwnerNode->AtkResNode.AtkEventManager.Event;
        for (var count = 0; e != null && count < 64; count++, e = e->NextEvent)
        {
            if (e->State.EventType != AtkEventType.ButtonClick || e->Listener != (AtkEventListener*)addon) continue;
            var copy = *e; var data = new AtkEventData();
            addon->ReceiveEvent(AtkEventType.ButtonClick, (int)copy.Param, &copy, &data);
            return true;
        }
        return false;
    }
    public static void PlaySound(int id) => UIGlobals.PlayChatSoundEffect((uint)Math.Clamp(id, 1, 16));
}
