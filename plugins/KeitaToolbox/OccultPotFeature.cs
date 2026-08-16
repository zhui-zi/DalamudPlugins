using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net.Http;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Chat;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Newtonsoft.Json;
using OmenTools;
using OmenTools.Extensions;
using OmenTools.Interop.Game.Helpers;
using OmenTools.OmenService;
using static OmenTools.Global.Globals;
using System.Text.RegularExpressions;
using Dalamud.Hooking;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using InteropGenerator.Runtime;
using Dalamud.Utility;
using Dalamud.Game.ClientState.Conditions;
using DalamudStatusFlags = Dalamud.Game.ClientState.Objects.Enums.StatusFlags;
using OmenBattleChara = OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds.IBattleChara;
using OmenGameObject = OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds.IGameObject;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools.Dalamud;
using OmenTools.Info.Game;
using OmenTools.Info.Game.Enums;
using OmenTools.Info.Game.Packets.Upstream;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models.Native;
using OmenTools.Threading;
using OmenTools.Threading.TaskHelper;
using EventFramework = FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework;
using EventHandlerContent = FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandlerContent;

namespace KeitaToolbox;

internal sealed class OccultPotFeature : IDisposable
{
    private const long Respawn = 1800;
    private const float AethernetInteractionDistance = 3.8f;

    private Config        config = null!;
    private IDtrBarEntry? entry;

    private Hook<RaptureLogModule.Delegates.AddMsgSourceEntry>? addMsgSourceEntryHook;
    private long lastArchivistReplyAt;

    private long   pendingArchivistReplyTime;
    private string pendingArchivistReplyMsg = string.Empty;

    private PotRegion lastPotRegion = PotRegion.North;
    private bool      continuationActive;

    private string     digDirection      = string.Empty;
    private bool       awaitingDirection;
    private bool       treasureRevealed;
    private bool       treasureInteractionStarted;
    private bool       treasureInteractionPositionSpoofed;
    private Vector3    treasureInteractionOriginalPosition;
    private uint       treasureEntityId;
    private readonly HashSet<uint> preexistingCofferEntityIds = [];
    private Vector3[]  autoDigCofferPositions = [];
    private readonly HashSet<Vector3> autoDigTriedPositions = [];
    private int        digRelocateCount;
    private const int  MaxDigRelocate = 1;
    private long       nextSpawnTime     = -1;
    private TaskHelper? autoDigTask;
    private bool       autoDigActive;
    private bool       autoDigDying;
    private bool       autoDigLureAcquired;
    private bool       autoDigLureExhausted;
    private long       autoDigLureMissingAt;
    private bool       undergroundDangerActive;
    private bool       allowUndergroundPositionUpdate;
    private float?     undergroundPacketHeight;
    private float?     undergroundSurfaceHeight;
    private TaskHelper? undergroundTestTask;
    private bool       undergroundTestActive;
    private bool       undergroundTestMovementReady;
    private bool       undergroundTestMoveOutward;
    private bool       undergroundTestStopRequested;
    private Vector3    undergroundTestSurfacePosition;
    private Vector3    undergroundTestOuterPosition;
    private uint       undergroundTestTerritory;
    private long       undergroundTestNextMoveAt;
    private long       undergroundTestStopDeadline;
    private bool       standbyDeathReturning;
    private long       deathReturnAt;
    private bool       deathReturnStarted;
    private long       nextDeathReturnAttemptAt;
    private bool       suppressBocchiReturn;
    private long       nextBocchiSuppressAt;
    private long       autoDigBocchiStoppedFor = -1;
    private long       autoDigBocchiPreparationFor = -1;
    private bool       autoDigBocchiWaitingForCurrentContent;
    private uint       autoDigBocchiAllowedFateID;
    private uint       autoDigBocchiAllowedCriticalEncounterID;
    private long       autoDigBocchiTravelStopRetriedFor = -1;
    private long       autoDigBocchiTravelStopRetryAt;
    private Pot?       pendingPostFateAutoDigTarget;
    private long       pendingPostFateAutoDigUntil;
    private long       autoDigStartedFor = -1;
    private Pot?       autoDigTarget;
    private string     autoDigStatus = string.Empty;
    private bool       emergencyReturnTriggered;
    private bool       emergencyReturnRecovering;
    private long       emergencyReturnRecoverAt;
    private bool       battleContentSettling;
    private bool       postBattleContentObserved;
    private bool       postBattleTreasureCheckPending;
    private DateTime   postBattleCompletedAt = DateTime.MinValue;
    private long       postBattleCheckExpireAt;
    private DateTime   lastTreasuresightCastAt = DateTime.MinValue;
    private bool       treasuresightCastObserved;
    private long       declineInviteAt;
    private uint       declineInviteTime;
    private string     declineInviterName = string.Empty;
    private bool       declineInviteSent;
    private ulong      autoReviveTargetID;
    private long       autoReviveAt;
    private readonly Dictionary<ulong, long> autoReviveRetryAfter = [];
    private ulong      autoReviveConfirmTargetID;
    private string     autoReviveConfirmTargetName = string.Empty;
    private long       autoReviveConfirmUntil;
    private bool       bmrAiSuppressionActive;
    private bool       bmrAiWasEnabled;
    private long       bmrAiSuppressionReleaseAt;

    private readonly Queue<CurrencyExchangeRequest> currencyExchangeQueue = [];
    private readonly Dictionary<uint, long> currencyExchangeRetryAfter = [];
    private CurrencyExchangeRequest? pendingCurrencyExchange;
    private int        pendingCurrencyBeforeCount;
    private int        pendingFixativeBeforeCount;
    private long       pendingCurrencyActionAt;
    private long       pendingCurrencyDeadline;
    private bool       pendingCurrencyConfirmationClicked;
    private long       nextCurrencyExchangeAt;
    private string     currencyExchangeStatus = string.Empty;

    private bool       cofferHuntActive;
    private long       cofferHuntStartedAt;
    private uint       cofferHuntTerritory;
    private bool       drHuntStarted;
    private long       pendingCofferHuntAutoDigFor = -1;
    private const long CofferHuntRequiredLeadSeconds = 600;
    private const long CofferHuntStopLeadSeconds     = 300;
    private const long PostBattleCheckTimeoutMS      = 180_000;
    private const long BmrAiSuppressionReleaseGraceMS = 500;
    private const uint TreasuresightActionID         = 0xA2B3;
    private const uint TreasuresightGeneralActionID  = 32;

    private volatile bool crossDCQuerying;
    private ushort        crossDCTargetDC;
    private string        crossDCTargetWorld = string.Empty;
    private uint          crossDCTargetTerritory;
    private bool          crossingDC;
    private volatile string crossDCReason = string.Empty;

    private const uint LureItemID = 2003296;

    private const uint CurrencyExchangeNpcDataID = 1059485;
    private const uint SilverCoinItemID           = 51975;
    private const uint GoldCoinItemID             = 51976;
    private const uint UltimateFixativeItemID      = 51978;
    private const int  CurrencyStackCap           = 9999;
    private const long CurrencyExchangeSessionTimeoutMS = 5_000;
    private const long CurrencyExchangeConfirmTimeoutMS = 5_000;
    private const long CurrencyExchangeRetryCooldownMS = 30_000;
    private const long CurrencyExchangeSpacingMS = 250;

    private static readonly CurrencyExchangeSpec SilverCurrencyExchange =
        new("十二城邦白银币", SilverCoinItemID, 0x1B0614, 1200);
    private static readonly CurrencyExchangeSpec GoldCurrencyExchange =
        new("十二城邦白金币", GoldCoinItemID, 0x1B0615, 1920);
    private static readonly CurrencyExchangeSpec[] CurrencyExchanges =
        [SilverCurrencyExchange, GoldCurrencyExchange];

    private static readonly string[] DigDirections = ["西北", "西南", "东北", "东南", "正东", "正西", "正南", "正北"];

    private unsafe delegate void ShowBattleTalkDelegate(UIModule* module, CStringPointer name, CStringPointer text, float duration, byte style);
    private Hook<ShowBattleTalkDelegate>? showBattleTalkHook;

    private unsafe delegate void ShowBattleTalkImageDelegate(
        UIModule* module, CStringPointer name, CStringPointer text, float duration, uint image, byte style, int sound, uint entityID);
    private Hook<ShowBattleTalkImageDelegate>? showBattleTalkImageHook;

    private Pot?   displayPot;
    private string displayText       = string.Empty;
    private long   notifiedSpawnTime = -1;
    private bool   overlayOpen;

    private readonly Pot[] pots =
    [
        new() { TerritoryID = 1252, FateID = 1976, World = new(204.66835f,  111.81729f, -204.96242f), DirName = "北", Aetheryte = "古树湿原", AetherytePos = new(302.4757f,   102.99427f, 305.8504f) },
        new() { TerritoryID = 1252, FateID = 1977, World = new(-479.8395f,  75f,         524.78894f), DirName = "南", Aetheryte = "石塔水沼", AetherytePos = new(-384.55502f, 97.29398f,  277.75458f) },
        new() { TerritoryID = 1346, FateID = 2072, World = new(233f,         7.729229f,  -470f),      DirName = "北", AetheryteData = CrescentAetheryte.SinkingSanctuary,  AetherytePlaceNameID = CrescentAetheryte.SinkingSanctuary.DataID,  AetherytePos = CrescentAetheryte.SinkingSanctuary.Position },
        new() { TerritoryID = 1346, FateID = 2073, World = new(-505.2822f,  53.14409f,   244.041f),  DirName = "南", AetheryteData = CrescentAetheryte.SuspendedMasonry, AetherytePlaceNameID = CrescentAetheryte.SuspendedMasonry.DataID, AetherytePos = CrescentAetheryte.SuspendedMasonry.Position }
    ];

    private static readonly (string Command, string Label)[] ChatChannels =
    [
        ("/s",    "说话"),
        ("/y",    "呼喊"),
        ("/sh",   "喊话"),
        ("/p",    "小队"),
        ("/a",    "团队"),
        ("/fc",   "部队"),
        ("/e",    "默语"),
        ("/l1",   "通讯贝 1"),
        ("/l2",   "通讯贝 2"),
        ("/l3",   "通讯贝 3"),
        ("/l4",   "通讯贝 4"),
        ("/l5",   "通讯贝 5"),
        ("/l6",   "通讯贝 6"),
        ("/l7",   "通讯贝 7"),
        ("/l8",   "通讯贝 8"),
        ("/cwl1", "跨界贝 1"),
        ("/cwl2", "跨界贝 2"),
        ("/cwl3", "跨界贝 3"),
        ("/cwl4", "跨界贝 4"),
        ("/cwl5", "跨界贝 5"),
        ("/cwl6", "跨界贝 6"),
        ("/cwl7", "跨界贝 7"),
        ("/cwl8", "跨界贝 8")
    ];

    private const string TrackerBaseURL     = "https://infi.ovh/api/";
    private const string TrackerTable       = "OccultTrackerV3";
    private const string TrackerAnonKey     = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJyb2xlIjoiYW5vbiJ9.Ur6wgi_rD4dr3uLLvbLoaEvfLCu4QFWdrF-uHRtbl_s";
    private const string TrackerVersion     = "DR-OccultPotNotifier";
    private const string CrowdsourceBaseURL = "https://ce-crowdsource.atmoomen.top";
    private const int    SyncRefreshSeconds = 60;
    private const int    FastRetrySeconds   = 5;
    private const int    MissingTrackerChecksBeforeCreate = 2;

    private static readonly HashSet<uint> SouthHornFateIds =
        [1962, 1963, 1964, 1965, 1966, 1967, 1968, 1969, 1970, 1971, 1972];

    private static readonly HashSet<uint> NorthHornFateIds =
        [2074, 2075, 2076, 2077, 2078, 2079, 2080, 2081, 2082, 2083, 2084];

    private static readonly HttpClient Client = CreateClient();

    private          string      lastFingerprint   = string.Empty;
    private          string      missingFingerprint = string.Empty;
    private          string      trackerID         = string.Empty;
    private          TrackerRow? currentTracker;
    private          int         missingTrackerChecks;
    private          long   lastSyncAt;
    private volatile bool   syncInFlight;
    private volatile bool   syncRequested;
    private volatile bool   hasOnlineData;
    private readonly object syncLock = new();
    private (uint Territory, long NorthSpawn, long NorthSeen, long SouthSpawn, long SouthSeen)? pendingSync;

    #region 地图标记 - 常量

    private const uint OccultTerritory      = 1252;
    private const uint OccultNorthTerritory = 1346;
    private const uint OccultMapID          = 967;
    private const uint OccultNorthMapID     = 1135;
    private const uint OccultNorthSubMapID  = 1244;

    private const uint PowerTowerEventID     = 48;
    private const uint MagicTowerEventID     = 64;
    private const uint GrandMagicTowerEventID = 65;
    private const uint TowerAssignedStatusID = 4228;
    private const uint ResurrectionRestrictedStatusID = 4262;
    private const uint ResurrectionDeniedStatusID     = 4263;

    private const uint  PhantomChemistReviveActionID  = 41634;
    private const uint  PhantomWhiteMageReviveActionID = 49070;
    private const uint  AutoAttackActionID             = 7;
    private const uint  PhantomChemistStatusID    = 4367;
    private const uint  PhantomWhiteMageStatusID  = 5329;
    private const uint  RaiseStatusID          = 148;
    private const uint  AlternateRaiseStatusID = 1140;
    private const float PhantomReviveRange     = 30f;
    private const long  AutoReviveRetryDelayMS = 30_000;
    private const long  DeathReturnRescueWaitMS = 180_000;
    private const long  AutoDigLureMissingGraceMS = 2_000;
    private const long  PostFateLureWaitMS = 20_000;
    private const long  TreasureProbeReadyTimeoutMS = 15_000;
    private const float UndergroundDepth     = 20f;
    private const float UndergroundMinHeight = -5f;
    private const float PotTreasureOpenRadius = 5f;
    private const float UndergroundMoveSpeed = 24f;
    private const float UndergroundReturnSpeed = 6f;
    private const float UndergroundReturnMaxStep = 0.5f;
    private const float UndergroundReturnTolerance = 0.05f;
    private const int   UndergroundReturnTimeoutMS = 8_000;
    private const int   UndergroundSettleMS = 750;
    private const int   MountTimeoutMS      = 20_000;
    private const float UndergroundTestMoveDistance  = 12f;
    private const float UndergroundTestMoveTolerance = 1.5f;
    private const int   UndergroundTestEndpointPauseMS = 1_000;
    private const int   UndergroundTestStopTimeoutMS = 10_000;
    private const string UndergroundTestCommand = "occultundergroundtest";
    private static readonly string[] DrInnerLoopRouteAliases = ["内环", "Inner Loop", "内回り", "내부"];
    private static readonly string[] DrOuterLoopRouteAliases = ["外环", "Outer Loop", "外回り", "외부"];
    private const uint LureStatusID = 1531;

    private const uint IconGoldChest = 60354;
    private const uint IconBronze    = 60356;
    private const uint IconSilver    = 60355;
    private const uint IconReroll    = 61473;
    private const uint IconCarrot    = 25207;
    private const uint IconSurvey    = 60468;

    private static readonly Vector4 SwitchActiveColor = KnownColor.SeaGreen.ToVector4();

    private const ImGuiWindowFlags SwitcherFlags =
        ImGuiWindowFlags.NoTitleBar          |
        ImGuiWindowFlags.NoResize            |
        ImGuiWindowFlags.NoScrollbar         |
        ImGuiWindowFlags.NoScrollWithMouse   |
        ImGuiWindowFlags.AlwaysAutoResize    |
        ImGuiWindowFlags.NoFocusOnAppearing  |
        ImGuiWindowFlags.NoNavFocus;


    private const MarkerSet PotMask = MarkerSet.NorthPot | MarkerSet.SouthPot | MarkerSet.Reroll;

    private static readonly (string Label, MarkerSet Flag)[] SwitchButtons =
    [
        ("青铜", MarkerSet.BronzeTreasure),
        ("白银", MarkerSet.SilverTreasure),
        ("北罐", MarkerSet.NorthPot),
        ("南罐", MarkerSet.SouthPot),
        ("续罐", MarkerSet.Reroll),
        ("萝卜", MarkerSet.Bunny),
        ("调查", MarkerSet.Survey)
    ];

    #endregion

    #region 地图标记 - 状态

    private MarkerSet  currentMarkers = MarkerSet.None;
    private bool       autoSwitchEngaged;
    private bool       manualMarkerOverrideWhileLure;
    private MarkerSet  autoPotSet;
    private MarkerSet  placedMarkers  = MarkerSet.None;
    private uint       placedMapID;
    private uint       placedMiniMapID;
    private bool       markersDirty;

    private bool    lureActive;
    private Vector3 cofferPos = Vector3.Zero;

    private static bool InSouthHorn => GameState.TerritoryType == OccultTerritory;
    private static bool InOccultMapZone =>
        GameState.TerritoryType is OccultTerritory or OccultNorthTerritory;
    private static bool InOccultFieldZone => InOccultMapZone;
    private static unsafe bool InForkedTower
    {
        get
        {
            if (!InOccultMapZone) return false;

            var events = DynamicEventContainer.GetInstance();
            if (events != null && (uint)events->CurrentEventId is
                    PowerTowerEventID or MagicTowerEventID or GrandMagicTowerEventID)
                return true;

            var localPlayer = DService.Instance().ObjectTable.LocalPlayer;
            if (localPlayer == null) return false;

            foreach (var status in localPlayer.StatusList)
                if (status.StatusID is TowerAssignedStatusID or
                    ResurrectionRestrictedStatusID or ResurrectionDeniedStatusID)
                    return true;

            return false;
        }
    }
    private static bool IsOccultMapForTerritory(uint territory, uint mapID) =>
        territory switch
        {
            OccultTerritory      => mapID == OccultMapID,
            OccultNorthTerritory => mapID is OccultNorthMapID or OccultNorthSubMapID,
            _                    => false
        };

    #endregion

    public unsafe OccultPotFeature()
    {
        Init();
    }

    private unsafe void Init()
    {
        config = Config.Load(this) ?? new();
        config.ChatCommands ??= ["/p"];
        config.DisabledChatCommands ??= [];
        config.ChatCommands.ExceptWith(config.DisabledChatCommands);

        addMsgSourceEntryHook ??= DService.Instance().Hook.HookFromMemberFunction<RaptureLogModule.Delegates.AddMsgSourceEntry>
        (
            typeof(RaptureLogModule.MemberFunctionPointers),
            nameof(RaptureLogModule.MemberFunctionPointers.AddMsgSourceEntry),
            AddMsgSourceEntryDetour
        );
        addMsgSourceEntryHook.Enable();

        showBattleTalkHook ??= UIModule.Instance()->VirtualTable->HookVFuncFromName
            ("ShowBattleTalk", (ShowBattleTalkDelegate)ShowBattleTalkDetour);
        showBattleTalkHook.Enable();

        showBattleTalkImageHook ??= UIModule.Instance()->VirtualTable->HookVFuncFromName
            ("ShowBattleTalkImage", (ShowBattleTalkImageDelegate)ShowBattleTalkImageDetour);
        showBattleTalkImageHook.Enable();


        DService.Instance().Chat.ChatMessage += OnChatMessage;
        GamePacketManager.Instance().RegPreSendPacket(OnPreSendPacket);

        autoDigTask ??= new() { TimeoutMS = 600_000 };
        undergroundTestTask ??= new() { TimeoutMS = 60_000 };

        currentMarkers = config.DefaultMarkers;

        overlayOpen    = false;

        entry         ??= DService.Instance().DTRBar.Get("KeitaToolbox-OccultPotNotifier");
        entry.Shown   =   false;
        entry.Tooltip =   "新月岛 魔法罐助手\n左键在地图上标记下一个魔法罐位置 (<flag>)\n右键打开当前岛的众包追踪器";
        entry.OnClick =   OnDtrClick;

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "AreaMap", OnAreaMapRefresh);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,  "ContentsFinderConfirm", OnContentsFinderConfirm);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw,    "ContentsFinderConfirm", OnContentsFinderConfirm);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,  "ShopExchangeCurrency", OnCurrencyExchangeAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw,    "ShopExchangeCurrency", OnCurrencyExchangeAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,  "SelectYesno", OnCurrencyExchangeConfirmAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw,    "SelectYesno", OnCurrencyExchangeConfirmAddon);
        Plugin.PluginInterface.UiBuilder.Draw += OnPostDraw;
        Plugin.PluginInterface.UiBuilder.Draw += DrawOverlayWindow;

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    public void Dispose()
    {
        RestoreBmrAiAfterPotFate();
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Instance().Chat.ChatMessage             -= OnChatMessage;
        GamePacketManager.Instance().Unreg(OnPreSendPacket);
        Plugin.PluginInterface.UiBuilder.Draw             -= OnPostDraw;
        Plugin.PluginInterface.UiBuilder.Draw             -= DrawOverlayWindow;
        DService.Instance().AddonLifecycle.UnregisterListener(OnAreaMapRefresh);
        DService.Instance().AddonLifecycle.UnregisterListener(OnContentsFinderConfirm);
        DService.Instance().AddonLifecycle.UnregisterListener(OnCurrencyExchangeAddon);
        DService.Instance().AddonLifecycle.UnregisterListener(OnCurrencyExchangeConfirmAddon);
        FrameworkManager.Instance().Unreg(OnUpdate);
        FrameworkManager.Instance().Unreg(OnPotFateTarget);
        FrameworkManager.Instance().Unreg(OnAutoDigSafety);
        FrameworkManager.Instance().Unreg(OnAutoRevive);
        FrameworkManager.Instance().Unreg(OnUndergroundTestSafety);

        addMsgSourceEntryHook?.Dispose();
        addMsgSourceEntryHook = null;

        showBattleTalkHook?.Dispose();
        showBattleTalkHook = null;

        showBattleTalkImageHook?.Dispose();
        showBattleTalkImageHook = null;

        StopUndergroundTest(false);
        undergroundTestTask?.Dispose();
        undergroundTestTask = null;
        AbortAutoDig();
        autoDigTask?.Dispose();
        autoDigTask = null;

        ClearMapMarkers();

        if (entry != null)
        {
            entry.Remove();
            entry = null;
        }
    }

    private unsafe void AddMsgSourceEntryDetour(
        RaptureLogModule* thisPtr, ulong contentID, ulong accountID, int messageIndex, ushort worldID, ushort chatType)
    {
        addMsgSourceEntryHook!.Original(thisPtr, contentID, accountID, messageIndex, worldID, chatType);

        try
        {
            TryArchivistReply(messageIndex, chatType);
        }
        catch
        {
        }

        try
        {
            TryCaptureDigTalk(messageIndex);
        }
        catch
        {
        }
    }

    private unsafe void TryCaptureDigTalk(int messageIndex)
    {
        if (!config.EnableAutoDig || !InOccultMapZone) return;

        if (!RaptureLogModule.Instance()->GetLogMessageDetail(messageIndex, out _, out var rawMessage, out _, out _, out _, out _))
            return;

        HandlePotTalk(SeString.Parse(rawMessage.AsSpan()).TextValue);
    }

    private unsafe void TryArchivistReply(int messageIndex, ushort chatType)
    {
        if (!config.EnableArchivist || !config.UseOnlineTracker) return;
        if (chatType != (ushort)XivChatType.Shout) return;
        if (!InOccultMapZone) return;

        var now = Environment.TickCount64;
        if (now - lastArchivistReplyAt < config.ArchivistCooldownSeconds * 1000) return;

        if (!RaptureLogModule.Instance()->GetLogMessageDetail(messageIndex, out _, out var rawMessage, out _, out _, out _, out _))
            return;

        var message = SeString.Parse(rawMessage.AsSpan()).TextValue;

        bool matched;
        try { matched = Regex.IsMatch(message, config.ArchivistRegex, RegexOptions.IgnoreCase); }
        catch { return; }

        if (!matched) return;

        var predictionMsg = GetNextPredictedMessage();
        if (predictionMsg == null) return;

        lastArchivistReplyAt = now;
        pendingArchivistReplyMsg = $"/sh {predictionMsg}";
        pendingArchivistReplyTime = Environment.TickCount64 + 3000;
    }

    private string? GetNextPredictedMessage()
    {
        if (!TryGetCurrentPots(out var north, out var south))
            return null;

        var alive = north.Alive ? north : south.Alive ? south : null;
        if (alive != null)
            return $"{alive.DirName}罐正在进行中";

        Pot? lastSpawned = null;
        if (north.SpawnTime > 0)
            lastSpawned = north;
        if (south.SpawnTime > 0 && (lastSpawned == null || south.SpawnTime > lastSpawned.SpawnTime))
            lastSpawned = south;

        if (lastSpawned == null) return null;

        var nextPot  = ReferenceEquals(lastSpawned, north) ? south : north;
        var nextTime = lastSpawned.SpawnTime + Respawn;
        var now      = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (nextTime <= now)
            return $"{nextPot.DirName}罐即将刷新";

        var minute = DateTimeOffset.FromUnixTimeSeconds(nextTime).ToLocalTime().Minute;
        return $"{nextPot.DirName}{minute}";
    }

    public void DrawSettings()
    {
        ImGui.TextDisabled("启用工具箱版本后，请在 DailyRoutines 中停用同名模块，避免重复提醒或自动操作。");
        ImGui.Spacing();

        if (!ImGui.BeginTabBar("###OccultPotSettingsTabs")) return;

        if (ImGui.BeginTabItem("显示与提醒"))
        {
            ConfigUIDisplayAndAlerts();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("地图标记"))
        {
            ConfigUIMarkers();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("自动挖罐"))
        {
            ConfigUIAutoDig();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("战斗辅助"))
        {
            ConfigUICombatAssistance();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("货币兑换"))
        {
            ConfigUICurrencyExchange();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    public bool HandleCommand(string arguments)
    {
        var trimmed = arguments.Trim();
        if (!trimmed.StartsWith(UndergroundTestCommand, StringComparison.OrdinalIgnoreCase))
            return false;

        OnUndergroundTestCommand(
            UndergroundTestCommand,
            trimmed[UndergroundTestCommand.Length..]);
        return true;
    }

    private void ConfigUIDisplayAndAlerts()
    {
        ConfigSection("倒计时显示");
        using (ImRaii.PushIndent())
        {
            if (ImGui.RadioButton("服务器信息栏", config.DisplayMode == PotDisplayMode.DtrBar))
            {
                config.DisplayMode = PotDisplayMode.DtrBar;
                config.Save(this);
            }

            ImGui.SameLine();
            if (ImGui.RadioButton("悬浮窗", config.DisplayMode == PotDisplayMode.Overlay))
            {
                config.DisplayMode = PotDisplayMode.Overlay;
                config.Save(this);
            }

            ImGui.SameLine();
            if (ImGui.RadioButton("不显示", config.DisplayMode == PotDisplayMode.None))
            {
                config.DisplayMode = PotDisplayMode.None;
                config.Save(this);
            }
        }

        ConfigSection("在线数据");
        using (ImRaii.PushIndent())
        using (ImRaii.Disabled())
        {
            config.UseOnlineTracker = true;
            ImGui.Checkbox("从在线追踪器同步并上报数据", ref config.UseOnlineTracker);
        }

        ConfigSection("刷新提醒");
        using (ImRaii.PushIndent())
        {
            var minutes = Math.Clamp(config.LeadSeconds / 60, 1, 15);
            ImGui.SetNextItemWidth(150f * GlobalUIScale);
            if (ImGui.SliderInt("提前提醒（分钟）###LeadMinutes", ref minutes, 1, 15))
                config.LeadSeconds = minutes * 60;
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save(this);

            if (ImGui.Checkbox("语音播报 (EdgeTTS)", ref config.SendTTS))
                config.Save(this);

            if (ImGui.Checkbox("游戏内通知", ref config.SendNotification))
                config.Save(this);

            if (ImGui.Checkbox("转发到聊天频道 (附带 <flag> 坐标)", ref config.SendChat))
                config.Save(this);

            if (config.SendChat)
            {
                using (ImRaii.PushIndent())
                {
                    ImGui.TextUnformatted("频道 (可多选):");
                    for (var i = 0; i < ChatChannels.Length; i++)
                    {
                        var (cmd, label) = ChatChannels[i];

                        var on = config.ChatCommands.Contains(cmd);
                        if (ImGui.Checkbox($"{label}###Chat{i}", ref on))
                        {
                            if (on)
                            {
                                config.ChatCommands.Add(cmd);
                                config.DisabledChatCommands.Remove(cmd);
                            }
                            else
                            {
                                config.ChatCommands.Remove(cmd);
                                config.DisabledChatCommands.Add(cmd);
                            }
                            config.Save(this);
                        }

                        if (i % 4 != 3 && i != ChatChannels.Length - 1)
                            ImGui.SameLine();
                    }

                    ImGui.SetNextItemWidth(150f * GlobalUIScale);
                    if (ImGui.SliderInt("附加提示音 (<se.?>)###ChatSoundEffect", ref config.ChatSoundEffect, 0, 13))
                        config.Save(this);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("设为 0 表示不附加提示音");
                }
            }
        }

        ConfigSection("史官自动回复");
        using (ImRaii.PushIndent())
        {
            if (ImGui.Checkbox("启用自动回复喊话频道 (需开启在线追踪器)", ref config.EnableArchivist))
                config.Save(this);

            if (!config.UseOnlineTracker && config.EnableArchivist)
            {
                ImGui.SameLine();
                ImGui.TextColored(KnownColor.Orange.ToVector4(), "警告: 请先开启“从在线追踪器同步”功能");
            }

            using (ImRaii.Disabled(!config.EnableArchivist || !config.UseOnlineTracker))
            {
                ImGui.SetNextItemWidth(250f * GlobalUIScale);
                if (ImGui.InputText("触发正则###ArchivistRegex", ref config.ArchivistRegex, 128))
                    config.Save(this);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("匹配喊话频道的关键词，使用正则表达式。例如: lw罐|史官");

                ImGui.SetNextItemWidth(150f * GlobalUIScale);
                if (ImGui.SliderInt("回复间隔 (秒)###ArchivistCooldown", ref config.ArchivistCooldownSeconds, 10, 300))
                    config.Save(this);
                if (ImGui.IsItemDeactivatedAfterEdit())
                    config.Save(this);
            }
        }
    }

    private static void ConfigSection(string title)
    {
        ImGui.Spacing();
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), title);
        ImGui.Separator();
        ImGui.Spacing();
    }

    private DangerZoneHandlingMode DangerZoneHandling =>
        config.AutoDigDiscardDanger     ? DangerZoneHandlingMode.Skip :
        config.AutoDigUndergroundDanger ? DangerZoneHandlingMode.Underground :
        config.AutoDigSkipDanger        ? DangerZoneHandlingMode.Manual :
                                          DangerZoneHandlingMode.Ground;

    private void SetDangerZoneHandling(DangerZoneHandlingMode mode)
    {

        config.AutoDigSkipDanger        = mode != DangerZoneHandlingMode.Ground;
        config.AutoDigUndergroundDanger = mode == DangerZoneHandlingMode.Underground;
        config.AutoDigDiscardDanger     = mode == DangerZoneHandlingMode.Skip;
        config.Save(this);
    }

    private void ConfigUIAutoDig()
    {
        ImGui.Spacing();
        ImGui.TextColored(KnownColor.OrangeRed.ToVector4(), "实验性功能，可能存在未知问题和封号风险，请自行承担风险");
        using (ImRaii.PushIndent())
        {
            if (ImGui.Checkbox("启用全自动挖罐", ref config.EnableAutoDig))
            {
                config.Save(this);
                if (!config.EnableAutoDig) AbortAutoDig();
            }

            if (config.EnableAutoDig)
            {
                ConfigSection("路线与危险区域");
                var dangerHandling = DangerZoneHandling;
                var dangerHandlingName = dangerHandling switch
                {
                    DangerZoneHandlingMode.Manual      => "等待手动处理",
                    DangerZoneHandlingMode.Skip        => "跳过",
                    DangerZoneHandlingMode.Underground => "DR 遁地自动处理",
                    _                                  => "地面自动处理"
                };
                if (ImGui.BeginCombo("危险区处理方式", dangerHandlingName))
                {
                    if (ImGui.Selectable("等待手动处理", dangerHandling == DangerZoneHandlingMode.Manual))
                        SetDangerZoneHandling(DangerZoneHandlingMode.Manual);
                    if (ImGui.Selectable("跳过", dangerHandling == DangerZoneHandlingMode.Skip))
                        SetDangerZoneHandling(DangerZoneHandlingMode.Skip);
                    if (ImGui.Selectable("地面自动处理", dangerHandling == DangerZoneHandlingMode.Ground))
                        SetDangerZoneHandling(DangerZoneHandlingMode.Ground);
                    if (ImGui.Selectable("DR 遁地自动处理", dangerHandling == DangerZoneHandlingMode.Underground))
                        SetDangerZoneHandling(DangerZoneHandlingMode.Underground);
                    ImGui.EndCombo();
                }
                if (DangerZoneHandling == DangerZoneHandlingMode.Underground)
                {
                    ImGui.TextColored(KnownColor.Orange.ToVector4(),
                        "进入危险候选后按 DR 方式拦截位置更新；地下位置跟随当地地表并保持约 20 星码深度，最低 Y=-5。找到箱子后短暂回地面读条，完成即遁回地下。");
                    ImGui.TextColored(KnownColor.Red.ToVector4(),
                        "警告：无法隐藏撒娇罐，可能会引起绿玩惊诧。");
                    ImGui.TextColored(KnownColor.Gray.ToVector4(),
                        $"遁地寻宝测试：/ktb {UndergroundTestCommand} [on|off]\n" +
                        "需在新月岛野外、非战斗状态使用；再次执行或使用 off 可安全退出。");
                }
                else if (DangerZoneHandling == DangerZoneHandlingMode.Skip)
                    ImGui.TextColored(KnownColor.Gray.ToVector4(),
                        "遇到危险区时自动取消身上的撒娇罐 Buff，并结束本轮挖罐。");
                using (ImRaii.Disabled(DangerZoneHandling != DangerZoneHandlingMode.Manual))
                {
                    if (ImGui.Checkbox("遇到危险区宝箱时使用 EdgeTTS 提示手动处理", ref config.AutoDigDangerTts))
                        config.Save(this);
                }
                if (ImGui.Checkbox("自动拒绝新月岛内的入队邀请（延迟 1 秒）", ref config.AutoDeclineInvite))
                    config.Save(this);

                ConfigSection("死亡与紧急返回");
                if (ImGui.Checkbox("死亡后停止（默认关：不停止）", ref config.AutoDigStopOnDeath))
                    config.Save(this);
                using (ImRaii.Disabled(config.AutoDigStopOnDeath))
                {
                    if (ImGui.Checkbox("死亡后自动归返起始点", ref config.AutoDigReturnOnDeath))
                        config.Save(this);
                    using (ImRaii.Disabled(!config.AutoDigReturnOnDeath))
                    {
                        if (ImGui.Checkbox("仅死亡 3 分钟仍无人施救时归返", ref config.AutoDigWaitForRescue))
                            config.Save(this);
                        if (config.AutoDigWaitForRescue)
                            ImGui.TextColored(KnownColor.Gray.ToVector4(),
                                "等待施救期间不发送罐子通知、语音或聊天消息；开始自动归返后恢复通知。");
                    }
                }

                if (ImGui.Checkbox("半血以下被高等级敌人攻击时紧急返回", ref config.AutoDigEmergencyReturn))
                    config.Save(this);

                ConfigSection("完成后操作");
                if (ImGui.Checkbox("挖完自动跨区（选刷新最短且 >5 分钟的大区）", ref config.EnableAutoCrossDC))
                    config.Save(this);
                if (config.EnableAutoCrossDC)
                    ImGui.TextColored(KnownColor.Orange.ToVector4(),
                        "需启用 DR「特殊场景探索进入指令」(/pdrfe) + 快捷跨界传送指令(/pdr worldtravel)；跨大区有崩游戏风险。");

                if (ImGui.Checkbox("FATE / CE 后按岛况自动寻宝", ref config.EnableCofferHunt))
                    config.Save(this);
                if (config.EnableCofferHunt)
                {
                    using (ImRaii.PushIndent())
                    {
                        if (ImGui.Checkbox("DR 寻宝使用外环路线", ref config.CofferHuntOuterLoop))
                            config.Save(this);
                        ImGui.TextColored(KnownColor.Gray.ToVector4(),
                            "BOCCHI 返回并使用魔寻宝后，仅在青铜 > 15、白银 > 2、下个罐子 > 10 分钟时开启。\n" +
                            "需启用 BOCCHI 自动魔寻宝，以及 DR「新月岛综合助手」模块；传送到非起始点魔路水晶后，仅在周围 50 yalms 无其他玩家时启动，发现玩家则换水晶重试。\n" +
                            "进入 5 分钟自动前往窗口后回程并衔接挖罐；否则回程并恢复 BOCCHI 非法模式。");
                    }
                }
                if (autoDigActive)
                {
                    ConfigSection("运行状态");
                    ImGui.TextColored(KnownColor.LawnGreen.ToVector4(), "自动挖罐运行中…");
                    if (ImGui.Button("立即停止"))
                        AbortAutoDig();
                }
            }
        }
    }

    private void ConfigUICombatAssistance()
    {
        ConfigSection("魔法罐 FATE 自动选中目标");
        using (ImRaii.PushIndent())
        {
            if (ImGui.Checkbox("位于魔法罐 FATE 区域时持续选中敌人", ref config.KeepPotFateEnemyTargeted))
                config.Save(this);
        }

        ConfigSection("BMR AI");
        using (ImRaii.PushIndent())
        {
            if (ImGui.Checkbox("魔法罐 FATE 期间保持 BMR AI 关闭", ref config.KeepBmrAiDisabledDuringPotFate))
            {
                config.Save(this);
                if (!config.KeepBmrAiDisabledDuringPotFate)
                    RestoreBmrAiAfterPotFate();
            }

            ImGui.TextColored(KnownColor.Gray.ToVector4(),
                "进入魔法罐 FATE 的圆形区域后持续关闭 Bossmod Reborn AI。\n" +
                "离开或 FATE 结束时，仅恢复由本功能关闭且进入前原本开启的 AI。");
        }

        ConfigUIAutoRevive();
    }

    private void ConfigUICurrencyExchange()
    {
        ConfigSection("终极固定剂兑换");
        using (ImRaii.PushIndent())
        {
            if (ImGui.Checkbox("白银币或白金币达到 9999 时自动兑换", ref config.EnableAutoCurrencyExchange))
                config.Save(this);

            var silverCount = GetCurrencyCount(SilverCoinItemID);
            var goldCount   = GetCurrencyCount(GoldCoinItemID);
            ImGui.TextUnformatted($"十二城邦白银币: {silverCount}/{CurrencyStackCap}（可兑换 {silverCount / SilverCurrencyExchange.Cost} 个）");
            ImGui.TextUnformatted($"十二城邦白金币: {goldCount}/{CurrencyStackCap}（可兑换 {goldCount / GoldCurrencyExchange.Cost} 个）");

            var busy = pendingCurrencyExchange.HasValue || currencyExchangeQueue.Count > 0;
            var canExchange = CanExchangeCurrenciesNow(out var unavailableReason);
            var hasAffordableCurrency = silverCount >= SilverCurrencyExchange.Cost ||
                                        goldCount >= GoldCurrencyExchange.Cost;
            using (ImRaii.Disabled(busy || !canExchange || !hasAffordableCurrency))
            {
                if (ImGui.Button("立即全部兑换"))
                    QueueAllCurrencyExchanges(false);
            }

            if (busy)
                ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), "兑换队列处理中…");
            else if (!canExchange)
                ImGui.TextColored(KnownColor.Gray.ToVector4(), unavailableReason);
            else if (!hasAffordableCurrency)
                ImGui.TextColored(KnownColor.Gray.ToVector4(), "当前两种货币均不足一次兑换。");

            if (!string.IsNullOrWhiteSpace(currencyExchangeStatus))
                ImGui.TextWrapped(currencyExchangeStatus);
        }
    }

    private void ConfigUIAutoRevive()
    {
        ConfigSection("辅助职业自动复活");
        using (ImRaii.PushIndent())
        {
            if (ImGui.Checkbox("自动复活周围倒地玩家（延迟 1 秒）", ref config.EnableAutoRevive))
            {
                ResetAutoReviveCandidate();
                config.Save(this);
            }

            using (ImRaii.Disabled(!config.EnableAutoRevive))
            {
                if (ImGui.RadioButton("仅同小队成员", config.AutoRevivePartyOnly))
                {
                    config.AutoRevivePartyOnly = true;
                    ResetAutoReviveCandidate();
                    config.Save(this);
                }

                ImGui.SameLine();
                if (ImGui.RadioButton("周围所有玩家", !config.AutoRevivePartyOnly))
                {
                    config.AutoRevivePartyOnly = false;
                    ResetAutoReviveCandidate();
                    config.Save(this);
                }
            }
        }
    }

    private void ConfigUIMarkers()
    {
        ConfigSection("默认地图标记（仅限新月岛）");
        using (ImRaii.PushIndent())
        {
            ImGui.TextUnformatted("默认显示的标记（可多选）：");
            {
                var set = config.DefaultMarkers;
                for (var i = 0; i < SwitchButtons.Length; i++)
                {
                    var (label, flag) = SwitchButtons[i];
                    var on = set.HasFlag(flag);
                    if (ImGui.Checkbox($"{label}###CfgMarker{flag}", ref on))
                        SetUserMarkers(on ? set | flag : set & ~flag);

                    if (i % 3 != 2 && i != SwitchButtons.Length - 1)
                        ImGui.SameLine();
                }
            }
        }

        ConfigSection("快速切换悬浮窗");
        using (ImRaii.PushIndent())
        {
            if (ImGui.Checkbox("显示快速切换悬浮窗 (打开地图时贴附在地图旁)", ref config.ShowFastSwitcher))
                config.Save(this);
            if (config.ShowFastSwitcher)
            {
                using (ImRaii.PushIndent())
                {
                    if (ImGui.Checkbox("悬浮窗显示在地图下方", ref config.SwitcherBelowMap))
                    {
                        if (config.SwitcherBelowMap)
                            config.SwitcherMoveable = false;
                        config.Save(this);
                    }

                    if (ImGui.Checkbox("允许拖动悬浮窗", ref config.SwitcherMoveable))
                    {
                        if (config.SwitcherMoveable)
                            config.SwitcherBelowMap = false;
                        config.Save(this);
                    }
                }
            }
        }

        ConfigSection("携带撒娇罐时自动切换");
        using (ImRaii.PushIndent())
        {
            if (ImGui.Checkbox("携带「撒娇罐」时自动切换标记", ref config.AutoSwitchOnLure))
                config.Save(this);
        }

        ConfigSection("宝箱位置提示");
        using (ImRaii.PushIndent())
        {
            if (ImGui.Checkbox("绘制宝箱位置圆圈（自动寻宝仅显示附近候选点）", ref config.DrawCofferCircle))
                config.Save(this);
            if (config.DrawCofferCircle)
            {
                using (ImRaii.PushIndent())
                {
                    var circleColor = config.CircleColor;
                    if (ImGui.ColorEdit4("圆圈颜色###CircleColor", ref circleColor, ImGuiColorEditFlags.NoInputs))
                    {
                        config.CircleColor = circleColor with { W = 1f };
                        config.Save(this);
                    }
                }
            }
        }
    }

    private void DrawOverlayContents()
    {
        var text = string.IsNullOrEmpty(displayText) ? "等待刷新数据…" : displayText;
        if (ImGui.Selectable(text) && displayPot != null)
            OpenPotMap(displayPot);

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            OpenCrowdsourceTracker();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("左键在地图上标记魔法罐位置 (<flag>)\n右键打开当前岛的众包追踪器");

        if (config.EnableAutoDig)
        {
            var status = InForkedTower
                             ? "塔内暂停"
                             : autoDigActive
                             ? string.IsNullOrEmpty(autoDigStatus) ? "运行中" : autoDigStatus
                             : "待命";
            ImGui.TextColored(KnownColor.Gray.ToVector4(), $"自动挖罐: {status}");
        }

        ImGui.Separator();

        using (ImRaii.Disabled(!InOccultMapZone || autoDigActive || cofferHuntActive || undergroundTestActive))
        {
            if (ImGui.Button("手动寻宝"))
                ManualStartCofferHunt();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("使用 DR 手动巡宝箱\n支持南征与北征；进入距刷罐 5 分钟窗口或寻宝完成时自动停止");

        ImGui.SameLine();
        using (ImRaii.Disabled(!autoDigActive && !cofferHuntActive))
        {
            if (ImGui.Button("终止挖罐"))
                StopAutoDigManually();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("立即停止当前自动挖罐 / 寻宝（本轮罐子不再自动触发；下个罐子仍会照常开始）");
    }

    private void DrawOverlayWindow()
    {
        if (!overlayOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(360f, 0f), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(
                "新月岛 魔法罐助手###KeitaToolboxOccultPotOverlay",
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse))
        {
            DrawOverlayContents();
        }

        ImGui.End();
    }

    private void OnZoneChanged(uint zone)
    {
        RestoreBmrAiAfterPotFate();
        StopUndergroundTest(false);
        FrameworkManager.Instance().Unreg(OnUpdate);
        FrameworkManager.Instance().Unreg(OnPotFateTarget);
        FrameworkManager.Instance().Unreg(OnAutoDigSafety);
        FrameworkManager.Instance().Unreg(OnAutoRevive);
        ResetAutoReviveCandidate();
        ResetAutoReviveConfirmation();
        autoReviveRetryAfter.Clear();
        ResetCurrencyExchange();
        battleContentSettling = false;
        autoDigBocchiStoppedFor = -1;
        autoDigBocchiPreparationFor = -1;
        autoDigBocchiWaitingForCurrentContent = false;
        autoDigBocchiAllowedFateID = 0;
        autoDigBocchiAllowedCriticalEncounterID = 0;
        autoDigBocchiTravelStopRetriedFor = -1;
        autoDigBocchiTravelStopRetryAt = 0;
        pendingPostFateAutoDigTarget = null;
        pendingPostFateAutoDigUntil = 0;
        lastTreasuresightCastAt = DateTime.MinValue;
        treasuresightCastObserved = false;

        if (!crossingDC)
            AbortAutoDig();

        foreach (var pot in pots)
            pot.Reset();
        displayPot         = null;
        displayText        = string.Empty;
        notifiedSpawnTime  = -1;
        nextSpawnTime      = -1;
        lastFingerprint     = string.Empty;
        missingFingerprint  = string.Empty;
        trackerID           = string.Empty;
        currentTracker      = null;
        missingTrackerChecks = 0;
        lastSyncAt          = 0;
        syncRequested       = false;
        hasOnlineData       = false;
        lock (syncLock)
            pendingSync = null;
        HideDisplay();

        lureActive         = false;
        cofferPos          = Vector3.Zero;
        continuationActive = false;
        autoSwitchEngaged  = false;
        manualMarkerOverrideWhileLure = false;
        autoPotSet         = MarkerSet.None;
        currentMarkers     = config.DefaultMarkers;
        ClearMapMarkers();

        if (InOccultFieldZone)
            FrameworkManager.Instance().Reg(OnAutoRevive, 200);


        if (!InOccultMapZone && !crossingDC) return;

        FrameworkManager.Instance().Reg(OnUpdate, 1_000);
        FrameworkManager.Instance().Reg(OnPotFateTarget, 100);
        FrameworkManager.Instance().Reg(OnAutoDigSafety, 100);
    }

    private void OnPotFateTarget(IFramework _)
    {
        MaintainPotFateTarget();
        MaintainBmrAiSuppression();
    }

    private void OnAutoDigSafety(IFramework _)
    {
        RestoreBocchiAfterEmergencyReturn();

        if (ShouldEmergencyReturn(DService.Instance().ObjectTable.LocalPlayer))
        {
            if (undergroundTestActive)
                StopUndergroundTest(false);
            TriggerEmergencyReturn();
            return;
        }

        if (!config.EnableAutoDig || !autoDigActive || autoDigTarget is not { } fate) return;

        if (autoDigStatus == "打 FATE" && !IsFateActive(fate.FateID))
            BeginBocchiReturnSuppression();

        if (suppressBocchiReturn)
            KeepBocchiReturnSuppressed();
    }

    private static bool IsFateActive(ushort fateID)
    {
        foreach (var fate in DService.Instance().Fate)
            if (fate.FateId == fateID)
                return true;

        return false;
    }

    private void BeginBocchiReturnSuppression()
    {
        if (suppressBocchiReturn) return;

        suppressBocchiReturn = true;
        nextBocchiSuppressAt = Environment.TickCount64 + 1000;
        var usedEmergencyStop = EmergencyStopBocchi();
        DService.Instance().Log.Information(
            $"[OccultPotNotifier] Magic Pot FATE cleanup; BOCCHI emergency stop direct={usedEmergencyStop}");
    }

    private void KeepBocchiReturnSuppressed()
    {
        var now = Environment.TickCount64;
        if (now < nextBocchiSuppressAt) return;

        SendCommand("/bocchiillegal off");
        nextBocchiSuppressAt = now + 1000;
    }

    private void EndBocchiReturnSuppression()
    {
        suppressBocchiReturn = false;
        nextBocchiSuppressAt = 0;
    }

    private unsafe void OnAutoRevive(IFramework _)
    {
        if (!config.EnableAutoRevive || !InOccultFieldZone)
        {
            ResetAutoReviveCandidate();
            ResetAutoReviveConfirmation();
            return;
        }

        var condition        = DService.Instance().Condition;
        var localPlayer      = DService.Instance().ObjectTable.LocalPlayer;
        var reviveSupportJob = CrescentSupportJob.GetCurrentSupportJob();
        var reviveActionID   = reviveSupportJob?.JobType switch
        {
            CrescentSupportJobType.Chemist   => PhantomChemistReviveActionID,
            CrescentSupportJobType.WhiteMage => PhantomWhiteMageReviveActionID,
            _                                => 0u
        };
        if (reviveActionID == 0 && localPlayer != null)
        {
            foreach (var status in localPlayer.StatusList)
            {
                switch (status.StatusID)
                {
                    case PhantomChemistStatusID:
                        reviveSupportJob = CrescentSupportJob.Chemist;
                        reviveActionID   = PhantomChemistReviveActionID;
                        break;
                    case PhantomWhiteMageStatusID:
                        reviveSupportJob = CrescentSupportJob.WhiteMage;
                        reviveActionID   = PhantomWhiteMageReviveActionID;
                        break;
                }

                if (reviveActionID != 0) break;
            }
        }

        if (localPlayer is not { IsDead: false } ||
            reviveActionID == 0 ||
            reviveSupportJob?.IsActionUnlocked(reviveActionID) != true ||
            condition[ConditionFlag.BetweenAreas])
        {
            ResetAutoReviveCandidate();
            ResetAutoReviveConfirmation();
            return;
        }

        var now = Environment.TickCount64;
        if (autoReviveConfirmTargetID != 0)
        {
            OmenBattleChara? confirming = null;
            foreach (var obj in DService.Instance().ObjectTable)
            {
                if (obj.GameObjectID != autoReviveConfirmTargetID || obj is not OmenBattleChara battleChara) continue;
                confirming = battleChara;
                break;
            }

            if (confirming != null &&
                (!confirming.IsDead || HasOwnRaise(confirming, localPlayer.EntityID)))
            {
                autoReviveRetryAfter[autoReviveConfirmTargetID] = now + AutoReviveRetryDelayMS;
                NotifyHelper.Instance().Chat($"已自动复活{autoReviveConfirmTargetName}");
                ResetAutoReviveConfirmation();
                return;
            }

            if (now < autoReviveConfirmUntil) return;
            ResetAutoReviveConfirmation();
        }

        OmenBattleChara? pending = null;
        if (autoReviveTargetID != 0)
        {
            foreach (var obj in DService.Instance().ObjectTable)
            {
                if (obj.GameObjectID != autoReviveTargetID || obj is not OmenBattleChara battleChara) continue;
                pending = battleChara;
                break;
            }

            if (!IsValidReviveTarget(pending, localPlayer.Position, config.AutoRevivePartyOnly, localPlayer.EntityID))
            {
                ResetAutoReviveCandidate();
                pending = null;
            }
        }

        if (pending != null)
        {
            if (now < autoReviveAt) return;

            var target = (GameObject*)pending.Address;
            if (target == null)
            {
                autoReviveAt = now + 1000;
                return;
            }

            if (ActionManager.CanUseActionOnTarget(reviveActionID, target) &&
                UseActionManager.Instance().IsActionOffCooldown(ActionType.Action, reviveActionID) &&
                UseActionManager.Instance().UseAction(ActionType.Action, reviveActionID, pending.EntityID))
            {
                autoReviveConfirmTargetID   = pending.GameObjectID;
                autoReviveConfirmTargetName = pending.Name.ToString();
                autoReviveConfirmUntil      = now + 3000;
                ResetAutoReviveCandidate();
                return;
            }

            autoReviveAt = now + 1000;
            return;
        }

        OmenBattleChara? nearest    = null;
        var              bestDistSq = PhantomReviveRange * PhantomReviveRange;
        foreach (var obj in DService.Instance().ObjectTable)
        {
            if (obj is not OmenBattleChara battleChara ||
                !IsValidReviveTarget(battleChara, localPlayer.Position, config.AutoRevivePartyOnly, localPlayer.EntityID))
                continue;

            if (autoReviveRetryAfter.TryGetValue(battleChara.GameObjectID, out var retryAfter))
            {
                if (now < retryAfter) continue;
                autoReviveRetryAfter.Remove(battleChara.GameObjectID);
            }

            var distSq = Vector3.DistanceSquared(localPlayer.Position, battleChara.Position);
            if (distSq > bestDistSq) continue;

            nearest    = battleChara;
            bestDistSq = distSq;
        }

        if (nearest == null) return;

        autoReviveTargetID = nearest.GameObjectID;
        autoReviveAt       = now + 1000;
    }

    private static bool IsValidReviveTarget
    (
        OmenBattleChara? target,
        Vector3 playerPosition,
        bool partyOnly,
        uint localPlayerEntityID
    )
    {
        if (target == null ||
            target.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc ||
            !target.IsDead ||
            !target.IsTargetable ||
            HasOwnRaise(target, localPlayerEntityID) ||
            Vector3.DistanceSquared(playerPosition, target.Position) > PhantomReviveRange * PhantomReviveRange)
            return false;

        if (!partyOnly) return true;

        foreach (var member in DService.Instance().PartyList)
            if (member.EntityId == target.EntityID)
                return true;

        return false;
    }

    private static bool HasOwnRaise(OmenBattleChara target, uint localPlayerEntityID)
    {
        foreach (var status in target.StatusList)
        {
            if (status.SourceID == localPlayerEntityID &&
                status.StatusID is RaiseStatusID or AlternateRaiseStatusID)
                return true;
        }

        return false;
    }

    private static bool HasRaise(OmenBattleChara target)
    {
        foreach (var status in target.StatusList)
            if (status.StatusID is RaiseStatusID or AlternateRaiseStatusID)
                return true;

        return false;
    }

    private void ResetAutoReviveCandidate()
    {
        autoReviveTargetID = 0;
        autoReviveAt       = 0;
    }

    private void ResetAutoReviveConfirmation()
    {
        autoReviveConfirmTargetID   = 0;
        autoReviveConfirmTargetName = string.Empty;
        autoReviveConfirmUntil      = 0;
    }

    private static unsafe int GetCurrencyCount(uint itemID)
    {
        var inventory = InventoryManager.Instance();
        return inventory == null ? 0 : inventory->GetInventoryItemCount(itemID);
    }

    private static bool CanExchangeCurrenciesNow(out string reason)
    {
        if (GameState.TerritoryType != OccultNorthTerritory)
        {
            reason = "仅可在新月岛北方海角使用。";
            return false;
        }

        if (InForkedTower)
        {
            reason = "歧路之塔内暂停兑换。";
            return false;
        }

        var localPlayer = DService.Instance().ObjectTable.LocalPlayer;
        if (localPlayer is not { IsDead: false })
        {
            reason = "角色未登录或已倒地。";
            return false;
        }

        var condition = DService.Instance().Condition;
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            reason = "过图期间暂停兑换。";
            return false;
        }

        if (AddonSelectYesnoEvent.CheckConfirm())
        {
            reason = "存在待处理确认窗口，暂缓兑换。";
            return false;
        }

        if (condition[ConditionFlag.InCombat])
        {
            reason = "战斗中暂停兑换。";
            return false;
        }

        if (condition[ConditionFlag.OccupiedInQuestEvent])
        {
            reason = "事件占用期间暂停兑换。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private void QueueAllCurrencyExchanges(bool automatic)
    {
        if (pendingCurrencyExchange.HasValue || currencyExchangeQueue.Count > 0)
            return;

        if (!CanExchangeCurrenciesNow(out var reason))
        {
            if (!automatic)
                currencyExchangeStatus = reason;
            return;
        }

        var now = Environment.TickCount64;
        var queued = 0;
        foreach (var exchange in CurrencyExchanges)
        {
            var count = GetCurrencyCount(exchange.CurrencyItemID);
            if (count < exchange.Cost || automatic && count < CurrencyStackCap)
                continue;

            if (automatic &&
                currencyExchangeRetryAfter.TryGetValue(exchange.CurrencyItemID, out var retryAfter) &&
                now < retryAfter)
                continue;

            currencyExchangeQueue.Enqueue(new(exchange, automatic, 0));
            queued++;
        }

        if (queued == 0)
        {
            if (!automatic)
                currencyExchangeStatus = "当前两种货币均不足一次兑换。";
            return;
        }

        currencyExchangeStatus = automatic
                                     ? "检测到货币达到 9999，已加入自动兑换队列。"
                                     : $"已加入 {queued} 种货币的全部兑换队列。";
    }

    private unsafe void OnCurrencyExchangeAddon(AddonEvent _, AddonArgs args)
    {
        if (!pendingCurrencyExchange.HasValue || args.Addon == nint.Zero)
            return;

        args.Addon.ToStruct()->IsVisible = false;
    }

    private void OnCurrencyExchangeConfirmAddon(AddonEvent _, AddonArgs args)
    {
        if (pendingCurrencyExchange is not { } pending ||
            pendingCurrencyActionAt != 0 ||
            pendingCurrencyConfirmationClicked ||
            args.Addon.IsNull)
        {
            return;
        }

        if (!AddonSelectYesnoEvent.ClickYes([pending.Spec.Name, "终极固定剂"]))
            return;

        pendingCurrencyConfirmationClicked = true;
        currencyExchangeStatus = $"已自动确认{pending.Spec.Name}兑换，等待库存更新…";
        Plugin.Log.Information(
            $"[OccultPotNotifier] Auto-confirmed remote currency exchange event={pending.Spec.EventID:X}, item={pending.Spec.CurrencyItemID}, quantity={pending.Quantity}");
    }

    private static unsafe void SuppressCurrencyExchangeWindow()
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("ShopExchangeCurrency");
        if (addon != null)
            addon->IsVisible = false;
    }

    private static unsafe bool TrySendCurrencyExchangeAction(
        CurrencyExchangeRequest request,
        out int itemIndex)
    {
        itemIndex = -1;

        var agent = AgentShop.Instance();
        if (agent == null || !agent->IsAgentActive() || agent->ItemReceive == null)
            return false;

        var items = agent->ItemReceiveSpan;
        for (var index = 0; index < items.Length; index++)
        {
            if (items[index].ItemId != UltimateFixativeItemID)
                continue;

            itemIndex = index;
            AgentId.Shop.SendEvent(1, 0, itemIndex, request.Quantity, 0);
            return true;
        }

        return false;
    }

    private static unsafe void CloseCurrencyExchangeWindow()
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("ShopExchangeCurrency");
        if (addon == null)
            return;

        addon->IsVisible = false;
        addon->Close(true);
    }

    private void DriveCurrencyExchange()
    {
        if (GameState.TerritoryType != OccultNorthTerritory)
            return;

        var now = Environment.TickCount64;
        if (pendingCurrencyExchange is { } pending)
        {
            SuppressCurrencyExchangeWindow();

            if (pendingCurrencyActionAt > 0)
            {
                if (now < pendingCurrencyActionAt)
                    return;

                try
                {
                    if (!TrySendCurrencyExchangeAction(pending, out var itemIndex))
                    {
                        if (now < pendingCurrencyDeadline)
                            return;

                        CompleteCurrencyExchangeSession(pending.Spec.EventID);
                        FailCurrencyExchange(pending, now, $"未加载{pending.Spec.Name}兑换数据，30 秒后重试。");
                        return;
                    }

                    pendingCurrencyActionAt = 0;
                    pendingCurrencyDeadline = now + CurrencyExchangeConfirmTimeoutMS;
                    currencyExchangeStatus = $"已发送{pending.Spec.Name}兑换 ×{pending.Quantity}，等待库存确认…";
                    Plugin.Log.Information(
                        $"[OccultPotNotifier] Sent remote currency exchange through AgentShop event={pending.Spec.EventID:X}, item={pending.Spec.CurrencyItemID}, shopIndex={itemIndex}, quantity={pending.Quantity}");
                }
                catch (Exception ex)
                {
                    CompleteCurrencyExchangeSession(pending.Spec.EventID);
                    FailCurrencyExchange(pending, now, $"发送{pending.Spec.Name}兑换动作包失败。", ex);
                }

                return;
            }

            var currentCount = GetCurrencyCount(pending.Spec.CurrencyItemID);
            var currentFixativeCount = GetCurrencyCount(UltimateFixativeItemID);
            var expectedCurrencyCount = pendingCurrencyBeforeCount - pending.Quantity * pending.Spec.Cost;
            var currencyConfirmed = currentCount <= expectedCurrencyCount;
            var fixativeConfirmed = currentFixativeCount >= pendingFixativeBeforeCount + pending.Quantity;
            if (currencyConfirmed || fixativeConfirmed)
            {
                CompleteCurrencyExchangeSession(pending.Spec.EventID);
                CloseCurrencyExchangeWindow();
                currencyExchangeRetryAfter.Remove(pending.Spec.CurrencyItemID);
                pendingCurrencyExchange = null;
                pendingCurrencyBeforeCount = 0;
                pendingFixativeBeforeCount = 0;
                pendingCurrencyActionAt = 0;
                pendingCurrencyDeadline = 0;
                pendingCurrencyConfirmationClicked = false;
                nextCurrencyExchangeAt = now + CurrencyExchangeSpacingMS;

                var message = $"{pending.Spec.Name}已兑换为终极固定剂 ×{pending.Quantity}";
                currencyExchangeStatus = currencyExchangeQueue.Count == 0
                                             ? $"{message}；本轮兑换完成。"
                                             : $"{message}；正在等待下一种货币。";
                NotifyHelper.Instance().Chat(message);
                Plugin.Log.Information(
                    $"[OccultPotNotifier] Confirmed remote currency exchange item={pending.Spec.CurrencyItemID}, quantity={pending.Quantity}");
                return;
            }

            if (now < pendingCurrencyDeadline)
                return;

            CompleteCurrencyExchangeSession(pending.Spec.EventID);
            var timeoutMessage = pending.Automatic
                                     ? $"未确认{pending.Spec.Name}库存下降，30 秒后再自动尝试。"
                                     : $"未确认{pending.Spec.Name}库存下降，请检查背包容量后重试。";
            FailCurrencyExchange(pending, now, timeoutMessage);
            return;
        }

        if (!CanExchangeCurrenciesNow(out _))
            return;

        if (currencyExchangeQueue.Count == 0 && config.EnableAutoCurrencyExchange)
            QueueAllCurrencyExchanges(true);

        if (now < nextCurrencyExchangeAt)
            return;

        while (currencyExchangeQueue.Count > 0)
        {
            var request = currencyExchangeQueue.Dequeue();
            if (request.Automatic && !config.EnableAutoCurrencyExchange)
                continue;

            var currentCount = GetCurrencyCount(request.Spec.CurrencyItemID);
            if (request.Automatic && currentCount < CurrencyStackCap)
                continue;

            var quantity = currentCount / request.Spec.Cost;
            if (quantity <= 0)
                continue;

            try
            {
                var fixativeBeforeCount = GetCurrencyCount(UltimateFixativeItemID);
                new EventStartPackt(LocalPlayerState.EntityID, request.Spec.EventID).Send();

                pendingCurrencyExchange = request with { Quantity = quantity };
                pendingCurrencyBeforeCount = currentCount;
                pendingFixativeBeforeCount = fixativeBeforeCount;
                pendingCurrencyActionAt = now;
                pendingCurrencyDeadline = now + CurrencyExchangeSessionTimeoutMS;
                pendingCurrencyConfirmationClicked = false;
                currencyExchangeStatus = $"正在建立{request.Spec.Name}兑换会话…";
                Plugin.Log.Information(
                    $"[OccultPotNotifier] Started remote currency exchange npc={CurrencyExchangeNpcDataID}, player={LocalPlayerState.EntityID:X}, event={request.Spec.EventID:X}, item={request.Spec.CurrencyItemID}, quantity={quantity}");
            }
            catch (Exception ex)
            {
                FailCurrencyExchange(request with { Quantity = quantity }, now, $"建立{request.Spec.Name}兑换会话失败。", ex);
            }

            return;
        }
    }

    private void FailCurrencyExchange(
        CurrencyExchangeRequest request,
        long now,
        string message,
        Exception? exception = null)
    {
        currencyExchangeRetryAfter[request.Spec.CurrencyItemID] = now + CurrencyExchangeRetryCooldownMS;
        pendingCurrencyExchange = null;
        pendingCurrencyBeforeCount = 0;
        pendingFixativeBeforeCount = 0;
        pendingCurrencyActionAt = 0;
        pendingCurrencyDeadline = 0;
        pendingCurrencyConfirmationClicked = false;
        nextCurrencyExchangeAt = now + CurrencyExchangeSpacingMS;
        currencyExchangeStatus = message;
        CloseCurrencyExchangeWindow();
        NotifyHelper.Instance().NotificationWarning(message);

        if (exception == null)
            Plugin.Log.Warning(
                $"[OccultPotNotifier] Remote currency exchange timed out item={request.Spec.CurrencyItemID}, quantity={request.Quantity}");
        else
            Plugin.Log.Error(exception,
                $"[OccultPotNotifier] Remote currency exchange failed item={request.Spec.CurrencyItemID}, quantity={request.Quantity}");
    }

    private static void CompleteCurrencyExchangeSession(uint eventID)
    {
        try
        {
            new EventCompletePackt(eventID, 0).Send();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex,
                $"[OccultPotNotifier] Failed to complete remote currency exchange session event={eventID:X}");
        }
    }

    private void ResetCurrencyExchange()
    {
        currencyExchangeQueue.Clear();
        currencyExchangeRetryAfter.Clear();
        pendingCurrencyExchange = null;
        pendingCurrencyBeforeCount = 0;
        pendingFixativeBeforeCount = 0;
        pendingCurrencyActionAt = 0;
        pendingCurrencyDeadline = 0;
        pendingCurrencyConfirmationClicked = false;
        nextCurrencyExchangeAt = 0;
        currencyExchangeStatus = string.Empty;
    }

    private void OnUpdate(IFramework _)
    {
        TryAutoDeclineInvite();

        if (!InOccultMapZone)
        {
            if (!crossingDC)
            {
                FrameworkManager.Instance().Unreg(OnUpdate);
                HideDisplay();
                return;
            }


            DriveAutoDig(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return;
        }

        DriveCurrencyExchange();

        if (pendingArchivistReplyTime > 0 && Environment.TickCount64 >= pendingArchivistReplyTime)
        {
            var msg = pendingArchivistReplyMsg;
            pendingArchivistReplyTime = 0;
            pendingArchivistReplyMsg = string.Empty;
            if (!string.IsNullOrEmpty(msg))
                ChatManager.Instance().SendMessage(msg);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var fate in DService.Instance().Fate)
        {
            var pot = GetPot(fate.FateId);
            if (pot == null) continue;

            if (fate.Position != default)
                pot.World = fate.Position;

            pot.LastSeenAlive   = now;
            pot.SpawnTime       = fate.StartTimeEpoch;
            pot.LocallyObserved = true;

            lastPotRegion = pot.DirName == "北" ? PotRegion.North : PotRegion.South;

            if (!pot.Alive)
            {
                pot.Alive = true;
                syncRequested = true;
            }
        }

        foreach (var pot in pots)
        {
            if (pot.Alive && pot.LastSeenAlive != now)
            {
                pot.Alive     = false;
                pot.DeathTime = pot.LastSeenAlive;
                syncRequested = true;
                HandlePotFateEnded(pot);
            }
        }

        if (InOccultMapZone && config.UseOnlineTracker)
            TrySyncOnline(now);
        if (InOccultMapZone)
            ApplyPendingSync();

        UpdatePrediction(now);
        ApplyDisplay();


        if (!autoDigActive)
            UpdateLure();


        UpdateMapMarkers();

        if (undergroundTestActive) return;

        DrivePostBattleCofferHunt(now);
        ObserveTreasuresightCast();
        MaybeStopCofferHunt(now);
        MaybeCofferHuntDone();
        CheckStandbyDeath();

        DriveAutoDig(now);
    }


    private unsafe void TryAutoDeclineInvite()
    {
        if (!config.EnableAutoDig || !config.AutoDeclineInvite || !InOccultMapZone)
        {
            ResetAutoDeclineInvite();
            return;
        }

        var proxy       = InfoProxyPartyInvite.Instance();
        var inviterName = proxy == null ? string.Empty : proxy->InviterName.ToString();
        if (proxy == null || string.IsNullOrWhiteSpace(inviterName))
        {
            ResetAutoDeclineInvite();
            return;
        }


        if (declineInviteTime != proxy->InviteTime ||
            !string.Equals(declineInviterName, inviterName, StringComparison.Ordinal))
        {
            declineInviteTime = proxy->InviteTime;
            declineInviterName = inviterName;
            declineInviteSent = false;
            declineInviteAt = Environment.TickCount64 + 1000;
            return;
        }

        if (declineInviteSent || Environment.TickCount64 < declineInviteAt) return;

        if (proxy->RespondToInvitation(inviterName, false))
        {
            declineInviteSent = true;
            declineInviteAt = 0;
            var agent = AgentPartyInvite.Instance();
            if (agent != null) agent->Hide();
        }
        else
            declineInviteAt = Environment.TickCount64 + 1000;
    }

    private void ResetAutoDeclineInvite()
    {
        declineInviteAt = 0;
        declineInviteTime = 0;
        declineInviterName = string.Empty;
        declineInviteSent = false;
    }

    private void Notify(Pot pot, int minutes)
    {
        if (IsWaitingForRescue()) return;

        var message = $"魔法罐约{minutes}分钟后在{pot.DirName}处刷新";

        if (config.SendNotification)
            NotifyHelper.Instance().NotificationInfo(message);

        if (config.SendTTS)
            Speak(message);

        if (config.SendChat && config.ChatCommands.Count > 0)
        {
            SetPotFlag(pot);
            var seSuffix = config.ChatSoundEffect > 0 ? $" <se.{config.ChatSoundEffect}>" : string.Empty;
            foreach (var cmd in config.ChatCommands)
                ChatManager.Instance().SendMessage($"{cmd} 魔法罐约{minutes}分钟后在{pot.DirName}<flag>处刷新{seSuffix}");
        }
    }

    private void UpdatePrediction(long now)
    {
        if (!TryGetCurrentPots(out var north, out var south))
        {
            displayPot   = null;
            displayText  = string.Empty;
            nextSpawnTime = -1;
            return;
        }

        var alive = north.Alive ? north : south.Alive ? south : null;
        if (alive != null)
        {
            displayPot        = alive;
            displayText       = $"魔法罐: 进行中 ({alive.DirName})";
            notifiedSpawnTime = -1;
            return;
        }

        Pot? lastSpawned = null;
        if (north.SpawnTime > 0)
            lastSpawned = north;
        if (south.SpawnTime > 0 && (lastSpawned == null || south.SpawnTime > lastSpawned.SpawnTime))
            lastSpawned = south;

        if (lastSpawned == null)
        {
            displayPot  = null;
            displayText = "魔法罐: 等待刷新";
            return;
        }


        var nextPot  = ReferenceEquals(lastSpawned, north) ? south : north;
        var nextTime = lastSpawned.SpawnTime + Respawn;

        ShowCountdown(now, nextTime, nextPot);
    }

    private void ShowCountdown(long now, long nextTime, Pot pot)
    {
        displayPot    = pot;
        nextSpawnTime = nextTime;

        var remaining = nextTime - now;
        if (remaining <= 0)
        {
            displayText = $"魔法罐: 即将刷新 ({pot.DirName})";
            return;
        }

        var span = TimeSpan.FromSeconds(remaining);
        displayText = $"下个魔法罐 {span:mm\\:ss} ({pot.DirName})";

        if (notifiedSpawnTime != nextTime && remaining <= config.LeadSeconds)
        {
            Notify(pot, (int)Math.Ceiling(remaining / 60.0));
            notifiedSpawnTime = nextTime;
        }
    }

    private void ApplyDisplay()
    {
        if (entry != null)
        {
            if (config.DisplayMode == PotDisplayMode.DtrBar)
            {
                entry.Text  = displayText;
                entry.Shown = true;
            }
            else
                entry.Shown = false;
        }

        overlayOpen = config.DisplayMode == PotDisplayMode.Overlay;
    }

    private void HideDisplay()
    {
        if (entry != null)
            entry.Shown = false;
        overlayOpen = false;
    }

    private void OnDtrClick(DtrInteractionEvent e)
    {
        if (e.ClickType == MouseClickType.Right)
        {
            OpenCrowdsourceTracker();
            return;
        }

        if (displayPot == null) return;
        OpenPotMap(displayPot);
    }

    private void OpenCrowdsourceTracker()
    {
        var dataCenterID = CurrentDataCenter();
        var zoneServerID = GameState.ZoneServerID;
        if (dataCenterID == 0 || zoneServerID == 0)
        {
            NotifyHelper.Instance().NotificationInfo("暂未取得当前副本实例 ID，请稍后再试。");
            return;
        }

        Util.OpenLink($"{CrowdsourceBaseURL}/dc/{dataCenterID}/instance/{zoneServerID}");
    }

    private unsafe void OpenPotMap(Pot pot)
    {
        var agent = AgentMap.Instance();
        if (agent == null) return;

        var mapID = PotMapID(pot.TerritoryID);
        agent->SelectedMapId = mapID;
        if (!agent->IsAgentActive())
            agent->Show();

        agent->SetFlagMapMarker(pot.TerritoryID, mapID, pot.World);
        agent->OpenMap(mapID, pot.TerritoryID, "魔法罐");
    }

    private unsafe void SetPotFlag(Pot pot)
    {
        var agent = AgentMap.Instance();
        if (agent == null) return;

        agent->SetFlagMapMarker(pot.TerritoryID, PotMapID(pot.TerritoryID), pot.World);
    }

    private static uint PotMapID(uint territory) =>
        territory == OccultNorthTerritory ? OccultNorthMapID : OccultMapID;

    private Pot? GetPot(ushort fateID)
    {
        foreach (var pot in pots)
        {
            if (pot.TerritoryID == GameState.TerritoryType && pot.FateID == fateID)
                return pot;
        }

        return null;
    }



    private unsafe void MaintainPotFateTarget()
    {
        if (!config.KeepPotFateEnemyTargeted ||
            DService.Instance().ObjectTable.LocalPlayer is not { IsDead: false } localPlayer)
            return;

        ushort activePotFateID = 0;
        var nearestFateCenterDistance = float.MaxValue;
        foreach (var fate in DService.Instance().Fate)
        {
            if (GetPot(fate.FateId) == null || fate.Radius <= 0f) continue;

            var offset = localPlayer.Position - fate.Position;
            var centerDistance = offset.X * offset.X + offset.Z * offset.Z;
            if (centerDistance > fate.Radius * fate.Radius || centerDistance >= nearestFateCenterDistance)
                continue;

            activePotFateID = fate.FateId;
            nearestFateCenterDistance = centerDistance;
        }

        var targetSystem = TargetSystem.Instance();
        if (activePotFateID == 0 || targetSystem == null)
            return;

        OmenBattleChara? nearest = null;
        var nearestDistance = float.MaxValue;

        foreach (var obj in DService.Instance().ObjectTable)
        {
            if (obj is not OmenBattleChara enemy || !IsValidPotFateEnemy(enemy, activePotFateID))
                continue;

            if (enemy.Address == (nint)targetSystem->Target)
                return;

            var distance = Vector3.DistanceSquared(localPlayer.Position, enemy.Position);
            if (distance >= nearestDistance) continue;

            nearest = enemy;
            nearestDistance = distance;
        }

        if (nearest != null)
            targetSystem->Target = (GameObject*)nearest.Address;
    }

    private void MaintainBmrAiSuppression()
    {
        if (!config.KeepBmrAiDisabledDuringPotFate)
        {
            RestoreBmrAiAfterPotFate();
            return;
        }

        var localPlayer = DService.Instance().ObjectTable.LocalPlayer;
        if (localPlayer == null) return;

        var insidePotFate = false;
        foreach (var fate in DService.Instance().Fate)
        {
            if (GetPot(fate.FateId) == null || fate.Radius <= 0f) continue;

            var offset = localPlayer.Position - fate.Position;
            if (offset.X * offset.X + offset.Z * offset.Z <= fate.Radius * fate.Radius)
            {
                insidePotFate = true;
                break;
            }
        }

        if (!insidePotFate)
        {
            if (!bmrAiSuppressionActive) return;

            var now = Environment.TickCount64;
            if (bmrAiSuppressionReleaseAt == 0)
            {
                bmrAiSuppressionReleaseAt = now + BmrAiSuppressionReleaseGraceMS;
                return;
            }

            if (now >= bmrAiSuppressionReleaseAt)
                RestoreBmrAiAfterPotFate();
            return;
        }

        bmrAiSuppressionReleaseAt = 0;
        if (!BmrAi.TryGetEnabled(out var enabled)) return;

        if (!bmrAiSuppressionActive)
        {
            bmrAiSuppressionActive = true;
            bmrAiWasEnabled = enabled;
        }

        if (!enabled) return;

        SendCommand("/bmrai off");
        DService.Instance().Log.Information(
            "[OccultPotNotifier] Bossmod Reborn AI disabled for Magic Pot FATE");
    }

    private void RestoreBmrAiAfterPotFate()
    {
        if (!bmrAiSuppressionActive)
        {
            bmrAiSuppressionReleaseAt = 0;
            return;
        }

        var shouldRestore = bmrAiWasEnabled;
        bmrAiSuppressionActive = false;
        bmrAiWasEnabled = false;
        bmrAiSuppressionReleaseAt = 0;

        if (!shouldRestore || !BmrAi.TryGetEnabled(out var enabled) || enabled) return;

        SendCommand("/bmrai on");
        DService.Instance().Log.Information(
            "[OccultPotNotifier] Bossmod Reborn AI restored after Magic Pot FATE");
    }

    private static unsafe bool IsValidPotFateEnemy(OmenBattleChara enemy, ushort activePotFateID)
    {
        if (enemy.Address == 0 ||
            enemy.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc ||
            (enemy.StatusFlags & DalamudStatusFlags.Hostile) == 0 ||
            enemy.IsDead || enemy.CurrentHp == 0 || !enemy.IsTargetable)
            return false;

        var gameObject = (GameObject*)enemy.Address;
        return gameObject != null &&
               gameObject->BattleNpcSubKind == BattleNpcSubKind.Combatant &&
               gameObject->FateId == activePotFateID &&
               ActionManager.CanUseActionOnTarget(AutoAttackActionID, gameObject);
    }

    private bool TryGetCurrentPots(out Pot north, out Pot south)
    {
        north = null!;
        south = null!;

        foreach (var pot in pots)
        {
            if (pot.TerritoryID != GameState.TerritoryType) continue;

            if (pot.DirName == "北")
                north = pot;
            else if (pot.DirName == "南")
                south = pot;
        }

        return north != null && south != null;
    }

    #region 地图标记 - 逻辑

    private void OnAreaMapRefresh(AddonEvent type, AddonArgs args) => markersDirty = true;


    private unsafe void OnContentsFinderConfirm(AddonEvent type, AddonArgs args)
    {
        if (!crossingDC) return;
        if (args.Addon == nint.Zero) return;

        var addon = args.Addon.ToStruct();
        if (addon->AtkValues[7].UInt != 0) return;

        ((AddonContentsFinderConfirm*)addon)->CommenceButton->Click();
    }

    private unsafe void ShowBattleTalkDetour(UIModule* module, CStringPointer name, CStringPointer text, float duration, byte style)
    {
        showBattleTalkHook!.Original(module, name, text, duration, style);
        HandlePotTalk(text.HasValue ? text.ExtractText() : string.Empty);
    }

    private unsafe void ShowBattleTalkImageDetour(
        UIModule* module, CStringPointer name, CStringPointer text, float duration, uint image, byte style, int sound, uint entityID)
    {
        showBattleTalkImageHook!.Original(module, name, text, duration, image, style, sound, entityID);
        HandlePotTalk(text.HasValue ? text.ExtractText() : string.Empty);
    }

    private void OnChatMessage(IHandleableChatMessage message) =>
        HandlePotTalk(message.Message.TextValue);

    private void OnPreSendPacket(
        ref bool isPrevented,
        int opcode,
        ref nint packet,
        ref bool isPrioritize)
    {
        if (!InOccultMapZone || !undergroundDangerActive || allowUndergroundPositionUpdate ||
            opcode != UpstreamOpcode.PositionUpdateInstanceOpcode)
            return;

        isPrevented = true;
    }

    private void HandlePotTalk(string line)
    {
        if (string.IsNullOrEmpty(line) || !InOccultMapZone) return;

        if (autoDigActive && awaitingDirection && line.Contains("财宝") && line.Contains("方向"))
        {
            foreach (var dir in DigDirections)
            {
                if (line.Contains(dir))
                {
                    digDirection = dir;
                    awaitingDirection = false;
                    break;
                }
            }
        }

        if (autoDigActive && autoDigLureAcquired &&
            line.Contains("撒娇罐") && line.Contains("耗尽") && line.Contains("力量"))
            autoDigLureExhausted = true;


        if (autoDigActive && autoDigLureAcquired && !cofferHuntActive && line.Contains("发现了财宝"))
        {
            treasureRevealed = true;
            awaitingDirection = false;
        }


        if (!continuationActive && line.Contains("更多的圣灵药") && line.Contains("再帮你找一次财宝"))
        {
            continuationActive = true;
            markersDirty       = true;
        }
    }

    #region 自动挖罐



    private void HandlePotFateEnded(Pot target)
    {
        if (!config.EnableAutoDig || InForkedTower) return;

        if (autoDigActive)
        {
            if (ReferenceEquals(autoDigTarget, target))
                BeginBocchiReturnSuppression();
            return;
        }

        pendingPostFateAutoDigTarget = target;
        pendingPostFateAutoDigUntil = Environment.TickCount64 + PostFateLureWaitMS;
        TryStartPendingPostFateAutoDig();
    }

    private void TryStartPendingPostFateAutoDig()
    {
        var target = pendingPostFateAutoDigTarget;
        if (target == null) return;

        if (Environment.TickCount64 >= pendingPostFateAutoDigUntil)
        {
            pendingPostFateAutoDigTarget = null;
            pendingPostFateAutoDigUntil = 0;
            return;
        }

        if (!HasLure()) return;

        pendingPostFateAutoDigTarget = null;
        pendingPostFateAutoDigUntil = 0;
        StartPostFateAutoDig(target);
    }

    private void StartPostFateAutoDig(Pot target)
    {
        if (autoDigTask == null) return;

        autoDigActive = true;
        autoDigTarget = target;
        pendingCofferHuntAutoDigFor = -1;
        if (nextSpawnTime > 0) autoDigStartedFor = nextSpawnTime;
        digDirection = string.Empty;
        awaitingDirection = false;
        treasureRevealed = false;
        RestoreMagicPotCofferInteractionPosition();
        treasureInteractionStarted = false;
        treasureEntityId = 0;
        ResetAutoDigCandidateSearch();
        ResetAutoDigLureState();
        ResetDeathReturn();
        EndBocchiReturnSuppression();
        EndUndergroundDangerMode();
        autoDigStatus = "等待 FATE 结算";

        autoDigTask.Abort();
        BeginBocchiReturnSuppression();
        DService.Instance().Log.Information(
            $"[OccultPotNotifier] Magic Pot FATE 0x{target.FateID:X} ended with lure; post-FATE auto-dig started");
        autoDigTask.DelayNext(2000);
        autoDigTask.Enqueue(WaitOutOfCombat(15000));
        autoDigTask.Enqueue(PlayerReady);
        autoDigTask.DelayNext(1500);
        autoDigTask.Enqueue(BeginDig);
    }

    private void DriveAutoDig(long now)
    {
        if (!config.EnableAutoDig) return;
        if (!InOccultMapZone && !crossingDC) return;
        if (undergroundTestActive) return;

        if (InForkedTower)
        {
            if (autoDigActive || cofferHuntActive || standbyDeathReturning)
            {
                AbortAutoDig();
                autoDigStartedFor = -1;
            }

            return;
        }

        if (pendingPostFateAutoDigTarget != null)
        {
            TryStartPendingPostFateAutoDig();
            if (pendingPostFateAutoDigTarget != null) return;
        }

        if (autoDigActive)
        {

            if (autoDigStatus.StartsWith("前往") || autoDigStatus.StartsWith("跨区") ||
                (autoDigDying && deathReturnStarted))
                ClickSelectYesno();

            var localPlayer = DService.Instance().ObjectTable.LocalPlayer;
            if (localPlayer is { IsDead: true })
            {
                HandleAutoDigDeath();
                return;
            }

            if (autoDigDying)
            {

                if (localPlayer is not { IsDead: false } || DService.Instance().Condition[ConditionFlag.BetweenAreas])
                    return;

                autoDigStartedFor = -1;
                FinishAutoDig();
                BocchiOn();
                return;
            }

            if (ShouldFinishExpiredLure())
            {
                FinishExpiredLureSearch();
                return;
            }

            return;
        }

        if (displayPot == null || nextSpawnTime <= 0) return;

        var cofferHuntHandoff = pendingCofferHuntAutoDigFor == nextSpawnTime;
        if (pendingCofferHuntAutoDigFor > 0 && !cofferHuntHandoff)
            pendingCofferHuntAutoDigFor = -1;
        if (autoDigStartedFor == nextSpawnTime && !cofferHuntHandoff) return;

        var remaining = nextSpawnTime - now;
        if (!cofferHuntHandoff && remaining is > 300 or < 30) return;

        var currentPlayer = DService.Instance().ObjectTable.LocalPlayer;
        GetCurrentBattleContentIDs(
            currentPlayer,
            out var currentFateID,
            out var currentCriticalEncounterID);
        var inCombat = DService.Instance().Condition[ConditionFlag.InCombat];
        var inOrSettlingBattleContent = currentPlayer != null &&
                                        InOrSettlingFateOrCriticalEngagement(currentPlayer);


        if (autoDigBocchiPreparationFor != nextSpawnTime)
        {
            autoDigBocchiPreparationFor = nextSpawnTime;
            autoDigBocchiWaitingForCurrentContent = inCombat || inOrSettlingBattleContent;
            autoDigBocchiAllowedFateID = currentFateID;
            autoDigBocchiAllowedCriticalEncounterID = currentCriticalEncounterID;

            if (autoDigBocchiWaitingForCurrentContent)
            {
                DService.Instance().Log.Information(
                    $"[OccultPotNotifier] Magic Pot preparation armed; waiting for current FATE/CE to finish, " +
                    $"fate={currentFateID}, ce={currentCriticalEncounterID}, remaining={remaining}s");
            }
        }


        if (autoDigBocchiStoppedFor != nextSpawnTime)
        {
            if (autoDigBocchiWaitingForCurrentContent)
            {
                var sameFate = autoDigBocchiAllowedFateID != 0 &&
                               currentFateID == autoDigBocchiAllowedFateID;
                var sameCriticalEncounter = autoDigBocchiAllowedCriticalEncounterID != 0 &&
                                            currentCriticalEncounterID == autoDigBocchiAllowedCriticalEncounterID;
                var switchedToDifferentContent =
                    (currentFateID != 0 && currentFateID != autoDigBocchiAllowedFateID) ||
                    (currentCriticalEncounterID != 0 &&
                     currentCriticalEncounterID != autoDigBocchiAllowedCriticalEncounterID);


                if (inCombat || sameFate || sameCriticalEncounter ||
                    (!switchedToDifferentContent && inOrSettlingBattleContent))
                    return;

                autoDigBocchiWaitingForCurrentContent = false;
                DService.Instance().Log.Information(
                    "[OccultPotNotifier] Current FATE/CE completed; Magic Pot preparation is taking control");
            }

            autoDigBocchiStoppedFor = nextSpawnTime;
            autoDigBocchiTravelStopRetriedFor = -1;
            autoDigBocchiTravelStopRetryAt = Environment.TickCount64 + 1000;
            var usedEmergencyStop = EmergencyStopBocchi();
            DService.Instance().Log.Information(
                $"[OccultPotNotifier] Magic Pot preparation takeover; BOCCHI emergency stop direct={usedEmergencyStop}, remaining={remaining}s");
            return;
        }


        if (inCombat || inOrSettlingBattleContent)
            return;

        if (BocchiAutomator.IsTravellingToFateOrCriticalEncounter())
        {
            if (autoDigBocchiTravelStopRetriedFor != nextSpawnTime &&
                Environment.TickCount64 >= autoDigBocchiTravelStopRetryAt)
            {
                autoDigBocchiTravelStopRetriedFor = nextSpawnTime;
                var usedEmergencyStop = EmergencyStopBocchi();
                DService.Instance().Log.Information(
                    $"[OccultPotNotifier] Magic Pot preparation reclaimed a new BOCCHI FATE/CE trip; emergency stop direct={usedEmergencyStop}");
            }

            return;
        }

        pendingCofferHuntAutoDigFor = -1;
        autoDigStartedFor = nextSpawnTime;
        StartAutoDig(displayPot);
    }

    private bool ShouldEmergencyReturn(OmenBattleChara? localPlayer)
    {
        if (emergencyReturnRecovering) return false;

        if (!config.EnableAutoDig || !config.AutoDigEmergencyReturn || localPlayer is not { IsDead: false } ||
            localPlayer.MaxHp == 0 || (ulong)localPlayer.CurrentHp * 2 >= localPlayer.MaxHp ||
            InForkedTower || InOrSettlingFateOrCriticalEngagement(localPlayer) ||
            BocchiAutomator.IsTravellingToFateOrCriticalEncounter())
        {
            emergencyReturnTriggered = false;
            return false;
        }

        foreach (var obj in DService.Instance().ObjectTable)
        {
            if (obj is not OmenBattleChara enemy ||
                enemy.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc ||
                (enemy.StatusFlags & DalamudStatusFlags.Hostile) == 0 ||
                enemy.IsDead ||
                enemy.Level <= localPlayer.Level ||
                enemy.TargetObjectID != localPlayer.GameObjectID)
                continue;

            return !emergencyReturnTriggered;
        }

        emergencyReturnTriggered = false;
        return false;
    }

    private unsafe bool InOrSettlingFateOrCriticalEngagement(OmenBattleChara localPlayer)
    {
        if (IsInFateOrCriticalEngagement(localPlayer))
        {
            battleContentSettling = true;
            return true;
        }

        if (battleContentSettling && HasRemainingHostileAggro()) return true;

        battleContentSettling = false;
        return false;
    }

    private static unsafe bool IsInFateOrCriticalEngagement(OmenBattleChara localPlayer)
    {
        var gameObject = (GameObject*)localPlayer.Address;
        var events     = DynamicEventContainer.GetInstance();
        return (gameObject != null && gameObject->FateId != 0) ||
               (events != null && events->CurrentEventId != 0);
    }

    private static unsafe void GetCurrentBattleContentIDs(
        OmenBattleChara? localPlayer,
        out uint fateID,
        out uint criticalEncounterID)
    {
        var gameObject = localPlayer == null ? null : (GameObject*)localPlayer.Address;
        var events = DynamicEventContainer.GetInstance();
        fateID = gameObject == null ? 0 : (uint)gameObject->FateId;
        criticalEncounterID = events == null ? 0 : (uint)events->CurrentEventId;
    }

    private static bool HasRemainingHostileAggro()
    {
        if (DService.Instance().Condition[ConditionFlag.InCombat]) return true;
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return false;

        foreach (var obj in DService.Instance().ObjectTable)
        {
            if (obj is OmenBattleChara
                {
                    ObjectKind: Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc,
                    IsDead: false
                } enemy &&
                (enemy.StatusFlags & DalamudStatusFlags.Hostile) != 0 &&
                enemy.TargetObjectID == localPlayer.GameObjectID)
                return true;
        }

        return false;
    }

    private void TriggerEmergencyReturn()
    {
        emergencyReturnTriggered = true;
        emergencyReturnRecovering = true;
        emergencyReturnRecoverAt = Environment.TickCount64 + 6000;
        SendCommand("/bocchiillegal off");
        AbortAutoDig();
        GameMain.ExecuteCommand(214);
        Speak("遭遇危险，已返回");
    }

    private void RestoreBocchiAfterEmergencyReturn()
    {
        if (!emergencyReturnRecovering || Environment.TickCount64 < emergencyReturnRecoverAt || !PlayerReady())
            return;

        emergencyReturnRecovering = false;
        emergencyReturnRecoverAt = 0;
        SendCommand("/bocchiillegal on");
    }

    private void HandleAutoDigDeath()
    {
        if (cofferHuntActive) StopCofferHunt();

        if (config.AutoDigStopOnDeath)
        {
            AbortAutoDig();
            return;
        }

        if (!config.AutoDigReturnOnDeath) return;

        if (!autoDigDying)
        {
            autoDigDying  = true;
            autoDigStatus = config.AutoDigWaitForRescue ? "死亡，等待施救" : "死亡归返";
            awaitingDirection = false;
            ResetAutoDigCandidateSearch();
            BeginDeathReturn();
            EndBocchiReturnSuppression();
            autoDigTask?.Abort();
            VnavStop();
            SendCommand("/bocchiillegal off");
        }

        TriggerDeathReturn();
    }


    private static bool ClickSelectYesno() => AddonSelectYesnoEvent.ClickYes();


    private void BeginDeathReturn()
    {
        deathReturnAt             = Environment.TickCount64 + (config.AutoDigWaitForRescue ? DeathReturnRescueWaitMS : 0);
        deathReturnStarted        = false;
        nextDeathReturnAttemptAt  = 0;
    }

    private bool IsWaitingForRescue() =>
        config.AutoDigWaitForRescue &&
        !deathReturnStarted &&
        (autoDigDying || standbyDeathReturning) &&
        DService.Instance().ObjectTable.LocalPlayer is { IsDead: true };

    private bool TriggerDeathReturn()
    {
        var now = Environment.TickCount64;
        if (now < deathReturnAt) return false;
        if (config.AutoDigWaitForRescue &&
            DService.Instance().ObjectTable.LocalPlayer is { IsDead: true } localPlayer &&
            HasRaise(localPlayer))
            return false;

        if (!deathReturnStarted)
        {
            deathReturnStarted = true;
            autoDigStatus      = "死亡归返";
            NotifyDeath();
        }

        if (now < nextDeathReturnAttemptAt) return true;

        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Revive, 8);
        nextDeathReturnAttemptAt = now + 1000;
        return true;
    }

    private void NotifyDeath()
    {
        NotifyHelper.Instance().NotificationInfo("检测到死亡，自动归返起始点…");
    }

    private void CheckStandbyDeath()
    {
        if (autoDigActive) return;
        if (!config.EnableAutoDig || !config.AutoDigReturnOnDeath || config.AutoDigStopOnDeath) return;
        if (!InOccultMapZone) return;

        var lp   = DService.Instance().ObjectTable.LocalPlayer;
        var cond = DService.Instance().Condition;

        if (lp is { IsDead: true })
        {
            if (!standbyDeathReturning)
            {
                standbyDeathReturning = true;
                autoDigStatus = "死亡归返";
                StopCofferHunt();
                VnavStop();
                if (config.AutoDigWaitForRescue) autoDigStatus = "死亡，等待施救";
                BeginDeathReturn();
                SendCommand("/bocchiillegal off");
            }

            if (TriggerDeathReturn()) ClickSelectYesno();
            return;
        }

        if (standbyDeathReturning && lp is { IsDead: false } && !cond[ConditionFlag.BetweenAreas])
        {
            standbyDeathReturning = false;
            ResetDeathReturn();
            autoDigStatus = string.Empty;
            BocchiOn();
        }
    }

    private void StartAutoDig(Pot target)
    {
        if (autoDigTask == null) return;

        autoDigActive = true;
        autoDigTarget = target;
        digDirection  = string.Empty;
        awaitingDirection = false;
        treasureRevealed = false;
        RestoreMagicPotCofferInteractionPosition();
        treasureInteractionStarted = false;
        treasureEntityId = 0;
        ResetAutoDigCandidateSearch();
        ResetAutoDigLureState();
        ResetDeathReturn();
        EndBocchiReturnSuppression();
        EndUndergroundDangerMode();
        autoDigStatus = $"前往{target.DirName}罐";

        autoDigTask.Abort();

        autoDigTask.Enqueue(() => SendCommand("/bocchiillegal off"));
        autoDigTask.DelayNext(800);
        autoDigTask.Enqueue(WaitOutOfCombat(10000));
        autoDigTask.Enqueue(PlayerReady);
        EnqueuePtp(target);
        EnqueueMoveTo(
            RandomOffset(target.World, 6f),
            4f,
            timeoutMs: target.TerritoryID == OccultNorthTerritory ? 240000 : 90000);
        autoDigTask.Enqueue(() => { autoDigStatus = "等待刷新"; return target.Alive; });
        autoDigTask.Enqueue(() => { Dismount(); ClearCurrentTarget(); return true; });
        autoDigTask.DelayNext(1000);
        autoDigTask.Enqueue(() =>
        {
            autoDigStatus = "打 FATE";
            return BocchiOn();
        });
        autoDigTask.Enqueue(WaitBocchiCombat(target, 5000));
        autoDigTask.Enqueue(() => !target.Alive);
        autoDigTask.Enqueue(() =>
        {
            autoDigStatus = "等待 FATE 结算";
            BeginBocchiReturnSuppression();
            return true;
        });
        autoDigTask.DelayNext(2000);
        autoDigTask.Enqueue(WaitOutOfCombat(15000));
        autoDigTask.Enqueue(PlayerReady);
        autoDigTask.DelayNext(1500);
        autoDigTask.Enqueue(BeginDig);
    }

    private bool BeginDig()
    {
        if (autoDigTask == null) return true;



        EndBocchiReturnSuppression();

        digRelocateCount = 0;

        autoDigStatus = "等待撒娇罐";
        autoDigTask.Enqueue(WaitLure(20000));
        autoDigTask.Enqueue(() =>
        {
            if (!HasLure())
            {
                autoDigStatus = config.EnableAutoCrossDC ? "未获得撒娇罐，准备跨区" : "未获得撒娇罐，结束本轮";
                EnqueueFinish();
                return true;
            }
            autoDigLureAcquired  = true;
            autoDigLureExhausted = false;
            autoDigLureMissingAt = 0;
            EnqueueDigCycle(false);
            return true;
        });
        return true;
    }

    private void EnqueueDigCycle(bool continuation)
    {
        if (autoDigTask == null) return;

        var territory = autoDigTarget?.TerritoryID ?? GameState.TerritoryType;
        var regionKey = continuation ? "R" : autoDigTarget?.DirName == "南" ? "S" : "N";
        digDirection = string.Empty;
        awaitingDirection = false;
        autoDigCofferPositions = [];

        if (continuation)
        {
            if (territory == OccultNorthTerritory)
            {
                if (DangerZoneHandling != DangerZoneHandlingMode.Underground)
                {
                    HandleNorthContinuationDanger();
                    return;
                }

                autoDigStatus = "北征续罐：地表取方位";
            }
            else
            {
                autoDigStatus = "续罐→水晶洞窟";
                autoDigTask.Enqueue(() => SendCommand("/pdr ptp 水晶洞窟"));
                autoDigTask.DelayNext(1000);
                autoDigTask.Enqueue(WaitArrive(CrystalCavernPos, 50f, 20000));
                autoDigTask.DelayNext(8000);
            }
        }

        autoDigTask.Enqueue(() => { Dismount(); return true; });
        autoDigTask.DelayNext(700);
        autoDigTask.Enqueue(() => { autoDigStatus = "取方位"; UseLureForDirection(); return true; });
        autoDigTask.DelayNext(3000);
        autoDigTask.Enqueue(WaitDirection(6000));
        autoDigTask.Enqueue(() =>
        {
            if (string.IsNullOrEmpty(digDirection))
            {
                Dismount();
                UseLureForDirection();
            }
            return true;
        });
        autoDigTask.DelayNext(3000);
        autoDigTask.Enqueue(WaitDirection(6000));
        autoDigTask.Enqueue(() =>
        {
            awaitingDirection = false;
            if (string.IsNullOrEmpty(digDirection))
            {
                if (HasLure())
                    TryRelocate(continuation, "未取得方位，重新尝试");
                else
                    EnqueueFinish();
                return true;
            }

            var positions = ResolveDigPositions(territory, regionKey, digDirection);
            autoDigCofferPositions = positions;
            if (positions.Length == 0)
                TryRelocate(continuation, $"{digDirection}方向没有未尝试候选点，重新定位");
            else
            {
                digRelocateCount = 0;
                EnqueueDigRoute(regionKey, digDirection, positions);
            }

            return true;
        });
    }

    private static readonly HashSet<string> SouthHornDangerZones =
        ["S正北", "S正南", "S正西", "S西北", "S西南", "R正南", "R正西", "R西北", "R西南"];


    private static readonly Vector2[] NorthHornDangerPositions =
    [
        new(440.298f,  -926.5872f), // 30.2, 3.0
        new(-834f,     -587.4f),    // 4.6, 9.8
        new(-975.4507f, -526.2878f), // 1.9, 10.9
        new(-960f,     -425.8f),    // 2.2, 12.9
        new(-586.3f,   -715.2f),    // 9.6, 7.3
        new(-88.43135f,   4.891054f), // 19.7, 21.5
        new(-259.6f,     56.9f),    // 16.3, 22.6
        new(-172.6f,    103.2f)     // 17.9, 23.5
    ];

    private const float NorthHornDangerRadius = 20f;


    private static readonly Vector3 CrystalCavernPos = new(-354.6388f, 99.993385f, -120.4032f);
    private static bool IsNorthHornDangerPosition(uint territory, Vector3 position)
    {
        if (territory != OccultNorthTerritory)
            return false;

        var radiusSquared = NorthHornDangerRadius * NorthHornDangerRadius;
        foreach (var danger in NorthHornDangerPositions)
        {
            var dx = position.X - danger.X;
            var dz = position.Z - danger.Y;
            if (dx * dx + dz * dz <= radiusSquared)
                return true;
        }

        return false;
    }

    private static bool IsDangerPosition(uint territory, string regionKey, string direction, Vector3 position) =>
        territory switch
        {
            OccultNorthTerritory => IsNorthHornDangerPosition(territory, position),
            OccultTerritory      => SouthHornDangerZones.Contains(regionKey + direction),
            _                    => false
        };



    private static Vector3[] OrderDigPositions(
        uint territory,
        string regionKey,
        string direction,
        Vector3[] positions,
        Vector3 from)
    {
        if (positions.Length <= 1) return positions;

        Array.Sort(positions, (a, b) =>
        {
            var aDelta = new Vector2(a.X - from.X, a.Z - from.Z);
            var bDelta = new Vector2(b.X - from.X, b.Z - from.Z);
            return aDelta.LengthSquared().CompareTo(bDelta.LengthSquared());
        });

        var ordered = new Vector3[positions.Length];
        var index   = 0;
        foreach (var position in positions)
            if (!IsDangerPosition(territory, regionKey, direction, position))
                ordered[index++] = position;
        foreach (var position in positions)
            if (IsDangerPosition(territory, regionKey, direction, position))
                ordered[index++] = position;

        return ordered;
    }

    private bool EnqueueDangerManual(string warning)
    {
        if (DangerZoneHandling != DangerZoneHandlingMode.Manual || autoDigTask == null)
            return false;

        if (config.AutoDigDangerTts)
            Speak(warning);


        autoDigStatus = "危险区，请手动挖";
        autoDigTask.Enqueue(WaitBuffGone(420000));
        autoDigTask.DelayNext(10000);
        autoDigTask.Enqueue(() => BocchiOn());
        autoDigTask.Enqueue(() => { FinishAutoDig(); return true; });
        return true;
    }

    private bool EnqueueDangerSkip(string notification)
    {
        if (DangerZoneHandling != DangerZoneHandlingMode.Skip || autoDigTask == null)
            return false;

        awaitingDirection = false;
        ResetAutoDigCandidateSearch();
        VnavStop();
        ResetAutoDigLureState();
        autoDigStatus = "危险区，跳过本轮挖罐";
        StatusManager.ExecuteStatusOff(LureStatusID);
        NotifyHelper.Instance().NotificationInfo(notification);
        autoDigTask.Enqueue(WaitBuffGone(5000));
        autoDigTask.Enqueue(() => BocchiOn());
        autoDigTask.Enqueue(() => { FinishAutoDig(); return true; });
        return true;
    }

    private static string RegionName(string regionKey) => regionKey switch
    {
        "N" => "北罐",
        "S" => "南罐",
        "R" => "续罐",
        _   => string.Empty
    };

    private void ResetAutoDigCandidateSearch()
    {
        autoDigCofferPositions = [];
        autoDigTriedPositions.Clear();
        preexistingCofferEntityIds.Clear();
        digRelocateCount = 0;
    }



    private void TryRelocate(bool continuation, string status)
    {
        if (autoDigTask == null) return;

        if (digRelocateCount >= MaxDigRelocate)
        {
            Speak("多次未找到宝箱，放弃本次挖宝");
            EnqueueFinish();
            return;
        }

        digRelocateCount++;
        autoDigStatus = status;
        autoDigTask.DelayNext(3000);
        EnqueueDigCycle(continuation);
    }

    private void EnqueueDigRoute(string regionKey, string direction, Vector3[] positions)
    {
        if (autoDigTask == null) return;

        autoDigCofferPositions = positions;

        autoDigStatus = $"挖宝 {RegionName(regionKey)}{direction}";
        EnqueueDigStep(regionKey, direction, positions, 0);
    }

    private Vector3[] ResolveDigPositions(uint territory, string regionKey, string direction)
    {
        var pool = OccultData.PotPositions(territory, regionKey == "R", regionKey == "S");
        if (pool.Length == 0) return [];

        var available = new List<Vector3>(pool.Length);
        foreach (var position in pool)
            if (!autoDigTriedPositions.Contains(position))
                available.Add(position);
        if (available.Count == 0) return [];


        var from = DService.Instance().ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        var positions = OccultData.RefinePositionsByDirection(available.ToArray(), from, direction);
        return OrderDigPositions(territory, regionKey, direction, positions, from);
    }


    private void EnqueueDigStep(string regionKey, string direction, Vector3[] positions, int index)
    {
        if (autoDigTask == null) return;

        if (index >= positions.Length)
        {
            autoDigCofferPositions = [];
            if (HasLure())
                TryRelocate(regionKey == "R", "未找到宝箱，重新定位");
            else
                EnqueueFinish();
            return;
        }

        autoDigCofferPositions = positions[index..];

        var territory = autoDigTarget?.TerritoryID ?? GameState.TerritoryType;
        var dangerPosition = IsDangerPosition(territory, regionKey, direction, positions[index]) ||
                             territory == OccultNorthTerritory && regionKey == "R";
        if (dangerPosition)
        {
            if (EnqueueDangerSkip($"已跳过危险区候选点，并取消撒娇罐 Buff") ||
                EnqueueDangerManual($"危险区候选点，{RegionName(regionKey)}{direction}方向，请手动处理"))
                return;
        }

        autoDigTriedPositions.Add(positions[index]);
        var useUndergroundRoute = undergroundDangerActive ||
                                  dangerPosition && DangerZoneHandling == DangerZoneHandlingMode.Underground;
        if (useUndergroundRoute)
        {
            EnqueueUndergroundMoveTo(positions[index], 3f);
            EnqueueReturnToSurface(positions[index]);
        }
        else
            EnqueueMoveTo(
                positions[index],
                3f,
                mount: true,
                timeoutMs: territory == OccultNorthTerritory ? 240000 : 90000);
        autoDigTask.Enqueue(WaitDismounted(5000));
        autoDigTask.DelayNext(700);
        autoDigTask.Enqueue(() =>
        {
            treasureRevealed = false;
            RestoreMagicPotCofferInteractionPosition();
            treasureInteractionStarted = false;
            treasureEntityId = 0;
            digDirection      = string.Empty;
            awaitingDirection = false;
            return true;
        });
        autoDigTask.Enqueue(WaitTreasureAtPoint(positions[index], 5000));
        autoDigTask.Enqueue(WaitTreasureOpened(30000));
        autoDigTask.Enqueue(() =>
        {


            if (!treasureRevealed)
            {
                var nextDirection = direction;
                Vector3[] remaining;
                if (!string.IsNullOrEmpty(digDirection))
                {
                    nextDirection = digDirection;
                    remaining = ResolveDigPositions(territory, regionKey, digDirection);
                    autoDigStatus = $"宝箱在{digDirection}方向，继续定位";
                }
                else
                {
                    var from = DService.Instance().ObjectTable.LocalPlayer?.Position ?? positions[index];
                    remaining = OrderDigPositions(
                        territory,
                        regionKey,
                        direction,
                        positions[(index + 1)..],
                        from);
                }

                if (remaining.Length != 0) digRelocateCount = 0;
                EnqueueDigStep(regionKey, nextDirection, remaining, 0);
                return true;
            }

            ResetAutoDigCandidateSearch();

            autoDigTask.DelayNext(2500);
            autoDigTask.Enqueue(() =>
            {
                if (!HasLure())
                    EnqueueFinish();
                else if ((autoDigTarget?.TerritoryID ?? GameState.TerritoryType) == OccultNorthTerritory)
                {
                    if (DangerZoneHandling == DangerZoneHandlingMode.Underground)
                    {
                        digRelocateCount = 0;
                        EnqueueDigCycle(true);
                    }
                    else
                        HandleNorthContinuationDanger();
                }
                else
                {
                    digRelocateCount = 0;
                    EnqueueDigCycle(true);
                }
                return true;
            });
            return true;
        });
    }

    private void EnqueueFinish()
    {
        if (autoDigTask == null) return;

        awaitingDirection = false;
        ResetAutoDigCandidateSearch();

        var targetTerritory = autoDigTarget?.TerritoryID ?? GameState.TerritoryType;
        autoDigTask.Enqueue(() => { EndUndergroundDangerMode(); return true; });

        if (config.EnableAutoCrossDC)
        {
            autoDigTask.Enqueue(() => { autoDigStatus = "查询跨区"; StartCrossDCQuery(targetTerritory); return true; });
            autoDigTask.Enqueue(WaitCrossDCQuery(15000));
            autoDigTask.Enqueue(EnqueueCrossDCOrStay);
        }
        else
        {
            autoDigTask.Enqueue(() => { EndBocchiReturnSuppression(); UseReturn(); return true; });
            autoDigTask.DelayNext(6000);
            autoDigTask.Enqueue(PlayerReady);
            autoDigTask.Enqueue(() => BocchiOn());
            autoDigTask.Enqueue(() => { FinishAutoDig(); return true; });
        }
    }

    private void HandleNorthContinuationDanger()
    {
        continuationActive = true;
        markersDirty       = true;
        if (EnqueueDangerSkip("已跳过北征续罐危险区，并取消撒娇罐 Buff"))
            return;

        NotifyHelper.Instance().NotificationInfo("北征续罐按危险区处理，已停在原地，请手动继续");
        if (EnqueueDangerManual("危险区宝箱，北征续罐，请手动处理"))
            return;

        autoDigStatus = "北征续罐，请手动处理";
        FinishAutoDig();
    }

    #region 挖罐间隙自动寻宝（DailyRoutines）

    private unsafe void DrivePostBattleCofferHunt(long nowUnix)
    {
        if (!config.EnableCofferHunt || !InOccultMapZone || InForkedTower)
        {
            ResetPostBattleCofferCheck();
            return;
        }

        var player = DService.Instance().ObjectTable.LocalPlayer;
        if (player is null) return;

        if (IsInFateOrCriticalEngagement(player))
        {
            postBattleContentObserved         = true;
            postBattleTreasureCheckPending    = false;
            return;
        }

        if (postBattleContentObserved)
        {
            if (HasRemainingHostileAggro()) return;

            postBattleContentObserved         = false;
            postBattleTreasureCheckPending    = true;
            postBattleCompletedAt             = DateTime.Now;
            postBattleCheckExpireAt           = Environment.TickCount64 + PostBattleCheckTimeoutMS;
        }

        if (!postBattleTreasureCheckPending || autoDigActive || cofferHuntActive || crossingDC ||
            standbyDeathReturning)
            return;

        var nowTick = Environment.TickCount64;
        if (nowTick >= postBattleCheckExpireAt || nextSpawnTime <= 0 ||
            nextSpawnTime - nowUnix <= CofferHuntRequiredLeadSeconds)
        {
            ResetPostBattleCofferCheck();
            return;
        }

        if (!BocchiAutomator.TryGetTreasureScanAfter(
                postBattleCompletedAt,
                lastTreasuresightCastAt,
                out var bronzeChests,
                out var silverChests))
            return;

        ResetPostBattleCofferCheck();
        if (bronzeChests <= 15 || silverChests <= 2) return;

        autoDigTask ??= new();
        autoDigTask.Abort();
        autoDigActive = true;
        autoDigStatus = $"宝箱数量满足：青铜 {bronzeChests} / 白银 {silverChests}";
        SendCommand("/bocchiillegal off");
        NotifyHelper.Instance().NotificationInfo(
            $"宝箱数量满足自动寻宝：青铜 {bronzeChests}、白银 {silverChests}");
        StartCofferHunt();
    }

    private void ResetPostBattleCofferCheck()
    {
        postBattleContentObserved         = false;
        postBattleTreasureCheckPending    = false;
        postBattleCompletedAt             = DateTime.MinValue;
        postBattleCheckExpireAt           = 0;
    }

    private unsafe void ObserveTreasuresightCast()
    {
        var player = DService.Instance().ObjectTable.LocalPlayer;
        var character = player == null ? null : (BattleChara*)player.Address;
        CastInfo* castInfo = character == null ? null : character->GetCastInfo();
        var castingTreasuresight = castInfo != null && castInfo->IsCasting &&
                                   (castInfo->ActionId == TreasuresightActionID ||
                                    castInfo->ActionType == (byte)ActionType.GeneralAction &&
                                    castInfo->ActionId == TreasuresightGeneralActionID);
        if (!castingTreasuresight)
        {
            treasuresightCastObserved = false;
            return;
        }

        if (treasuresightCastObserved) return;

        treasuresightCastObserved = true;
        lastTreasuresightCastAt = DateTime.Now;
        DService.Instance().Log.Information(
            $"[OccultPotNotifier] BOCCHI Treasuresight cast observed: {lastTreasuresightCastAt:O}");
    }

    private void ManualStartCofferHunt()
    {
        if (!InOccultMapZone || autoDigActive || cofferHuntActive || undergroundTestActive) return;

        autoDigTask ??= new();
        autoDigTask.Abort();
        autoDigActive = true;
        StartCofferHunt();
    }

    private void StopAutoDigManually()
    {
        AbortAutoDig();
        if (nextSpawnTime > 0) autoDigStartedFor = nextSpawnTime;
    }

    private void StartCofferHunt()
    {
        if (autoDigTask == null) return;

        cofferHuntActive    = true;
        cofferHuntTerritory = GameState.TerritoryType;
        drHuntStarted       = false;
        EndBocchiReturnSuppression();
        StartDrCofferHunt();
    }

    private void StartDrCofferHunt()
    {
        if (autoDigTask == null) return;

        var candidates = GetShuffledDrAetherytes(cofferHuntTerritory);
        if (candidates.Count == 0)
        {
            ClearCofferHuntState();
            NotifyHelper.Instance().NotificationWarning("当前区域没有可用于 DR 寻宝的非初始点魔路水晶");
            EnqueueReturnStandby();
            return;
        }

        var basePosition = GetCofferHuntBasePosition(cofferHuntTerritory);
        autoDigStatus = "DR 寻宝准备：返回初始点";
        autoDigTask.Enqueue(() => SendCommand("/bocchiillegal off"));
        autoDigTask.Enqueue(() => { UseReturn(); return true; });
        autoDigTask.Enqueue(WaitDrReturnToBase(basePosition, 15000));

        autoDigTask.Enqueue(() =>
        {
            autoDigStatus = "DR 寻宝准备：前往初始点水晶";
            VnavMoveTo(basePosition);
            return true;
        });
        autoDigTask.Enqueue(WaitArrive(basePosition, 3f, 20000));
        autoDigTask.Enqueue(() => { VnavStop(); return true; });
        autoDigTask.DelayNext(800);

        foreach (var aetheryte in candidates)
            EnqueueDrCofferHuntAttempt(aetheryte);

        autoDigTask.Enqueue(() =>
        {
            if (drHuntStarted) return true;

            SendCommand("/pdr ptreasure abort");
            ClearCofferHuntState();
            NotifyHelper.Instance().NotificationWarning("DR 寻宝未能启动：已尝试所有非初始点魔路水晶");
            EnqueueReturnStandby();
            return true;
        });
    }

    private static Func<bool?> WaitDrReturnToBase(Vector3 basePosition, int timeoutMs)
    {
        long deadline = 0;
        return () =>
        {
            var now = Environment.TickCount64;
            if (deadline == 0) deadline = now + timeoutMs;
            ClickSelectYesno();
            return (PlayerReady() && Arrived(basePosition, 50f)) || now >= deadline;
        };
    }

    private void EnqueueDrCofferHuntAttempt(CrescentAetheryte aetheryte)
    {
        if (autoDigTask == null) return;
        var commandIssued = false;
        var roadFound      = false;
        var roadPosition   = Vector3.Zero;
        var teleportStarted = false;

        // CrescentAetheryte.Position is the teleport landing point, not the crystal object.
        // Re-acquire the current crystal before every hop because a landing point can sit outside interaction range.
        autoDigTask.Enqueue(WaitFindNearbyAethernetWhen(
            () => !drHuntStarted,
            position =>
            {
                roadFound    = true;
                roadPosition = position;
                autoDigStatus = "DR 寻宝准备：靠近当前魔路水晶";
                VnavMoveTo(position);
            },
            5000));
        autoDigTask.Enqueue(WaitAethernetInteractionRangeWhen(
            () => !drHuntStarted && roadFound,
            () => roadPosition,
            15000));

        autoDigTask.Enqueue(() =>
        {
            if (drHuntStarted) return true;

            VnavStop();
            if (!roadFound || !InAethernetInteractionRange(roadPosition))
            {
                autoDigStatus = "DR 寻宝跳过：未走到当前魔路水晶交互范围";
                return true;
            }

            autoDigStatus = $"DR 寻宝准备：传送至{aetheryte.Name}";
            return true;
        });
        autoDigTask.Enqueue(WaitAethernetMenuOpenWhen(
            () => !drHuntStarted && roadFound && InAethernetInteractionRange(roadPosition),
            5000));
        autoDigTask.Enqueue(() =>
        {
            if (drHuntStarted) return true;

            teleportStarted = TryAethernetTeleportFromOpenMenu(aetheryte);
            if (!teleportStarted && GetLifestreamActiveCustomAetheryte() != 0)
                teleportStarted = TryLifestreamAethernetTeleport(aetheryte.DataID);

            if (!teleportStarted)
                autoDigStatus = $"DR 寻宝跳过：未能启动前往{aetheryte.Name}的魔路传送";
            return true;
        });
        autoDigTask.DelayNext(1000);
        autoDigTask.Enqueue(() => drHuntStarted || !teleportStarted || PlayerReady());
        autoDigTask.Enqueue(WaitArriveUnlessDrStarted(
            aetheryte.Position,
            50f,
            20000,
            () => teleportStarted));
        autoDigTask.DelayNext(800);
        autoDigTask.Enqueue(() =>
        {
            if (drHuntStarted) return true;
            if (!Arrived(aetheryte.Position, 50f)) return true;
            if (HasNearbyOtherPlayer(50f))
            {
                autoDigStatus = $"DR 寻宝跳过：{aetheryte.Name}周围有玩家";
                return true;
            }

            autoDigStatus = $"DR 寻宝启动：{aetheryte.Name}";
            SendDrCofferHuntStartCommand();
            commandIssued = true;
            return true;
        });
        autoDigTask.Enqueue(WaitDrCofferHuntStarted(aetheryte.Position, 10000, () => commandIssued));
        autoDigTask.Enqueue(() =>
        {
            if (drHuntStarted || !commandIssued) return true;
            SendCommand("/pdr ptreasure abort");
            return true;
        });
        autoDigTask.DelayNext(500);
    }

    private void SendDrCofferHuntStartCommand()
    {
        var routeAliases = config.CofferHuntOuterLoop
                               ? DrOuterLoopRouteAliases
                               : DrInnerLoopRouteAliases;
        foreach (var routeAlias in routeAliases)
            SendCommand($"/pdr ptreasure {routeAlias}");

        DService.Instance().Log.Information(
            $"[OccultPotNotifier] DailyRoutines treasure route dispatched: {(config.CofferHuntOuterLoop ? "outer" : "inner")}");
    }

    private Func<bool?> WaitArriveUnlessDrStarted(
        Vector3 position,
        float tolerance,
        int timeoutMs,
        Func<bool>? enabled = null)
    {
        long deadline = 0;
        return () =>
        {
            if (drHuntStarted) return true;
            if (enabled is not null && !enabled()) return true;
            if (deadline == 0) deadline = Environment.TickCount64 + timeoutMs;
            return Arrived(position, tolerance) || Environment.TickCount64 >= deadline;
        };
    }

    private Func<bool?> WaitDrCofferHuntStarted(Vector3 aetherytePosition, int timeoutMs, Func<bool> commandIssued)
    {
        long deadline = 0;
        return () =>
        {
            if (drHuntStarted) return true;
            if (!commandIssued()) return true;
            var now = Environment.TickCount64;
            if (deadline == 0) deadline = now + timeoutMs;

            var player = DService.Instance().ObjectTable.LocalPlayer;
            if (player is not null && !DService.Instance().Condition[ConditionFlag.BetweenAreas] &&
                Vector2.Distance(player.Position.ToVector2(), aetherytePosition.ToVector2()) > 10f)
            {
                drHuntStarted       = true;
                cofferHuntStartedAt = now;
                autoDigStatus       = "DR 寻宝中";
                NotifyHelper.Instance().NotificationInfo("DR 寻宝已启动");
                return true;
            }

            return now >= deadline;
        };
    }

    private static bool HasNearbyOtherPlayer(float radius)
    {
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return true;
        var radiusSquared = radius * radius;

        foreach (var obj in DService.Instance().ObjectTable)
        {
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc ||
                obj.GameObjectID == localPlayer.GameObjectID)
                continue;

            var deltaX = obj.Position.X - localPlayer.Position.X;
            var deltaZ = obj.Position.Z - localPlayer.Position.Z;
            if ((deltaX * deltaX) + (deltaZ * deltaZ) <= radiusSquared)
                return true;
        }

        return false;
    }

    private static List<CrescentAetheryte> GetShuffledDrAetherytes(uint territory)
    {
        var result = new List<CrescentAetheryte>();
        var source = territory == OccultTerritory
                         ? CrescentAetheryte.SouthHornAetherytes
                         : CrescentAetheryte.NorthHornAetherytes;
        var baseDataID = territory == OccultTerritory
                             ? CrescentAetheryte.ExpeditionBaseCamp.DataID
                             : CrescentAetheryte.NorthHornBaseCamp.DataID;

        foreach (var aetheryte in source)
            if (aetheryte.DataID != baseDataID)
                result.Add(aetheryte);

        for (var i = result.Count - 1; i > 0; i--)
        {
            var swapIndex = Random.Shared.Next(i + 1);
            (result[i], result[swapIndex]) = (result[swapIndex], result[i]);
        }

        return result;
    }

    private static Vector3 GetCofferHuntBasePosition(uint territory) =>
        territory == OccultTerritory
            ? CrescentAetheryte.ExpeditionBaseCamp.Position
            : CrescentAetheryte.NorthHornBaseCamp.Position;


    private void MaybeCofferHuntDone()
    {
        if (!cofferHuntActive || !drHuntStarted ||
            Environment.TickCount64 - cofferHuntStartedAt < 30000)
            return;

        var player = DService.Instance().ObjectTable.LocalPlayer;
        if (player is null || DService.Instance().Condition[ConditionFlag.BetweenAreas] ||
            Vector2.Distance(player.Position.ToVector2(), GetCofferHuntBasePosition(cofferHuntTerritory).ToVector2()) > 50f)
            return;

        var now       = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var remaining = nextSpawnTime - now;
        pendingCofferHuntAutoDigFor = displayPot != null && nextSpawnTime > 0 &&
                                      remaining <= CofferHuntStopLeadSeconds
                                          ? nextSpawnTime
                                          : -1;

        ClearCofferHuntState();
        autoDigTask?.Abort();
        VnavStop();
        autoDigStatus = pendingCofferHuntAutoDigFor > 0 ? "寻宝完成，回程后自动挖罐" : "寻宝完成，回程待命";
        NotifyHelper.Instance().NotificationInfo(
            pendingCofferHuntAutoDigFor > 0
                ? "DR 已挖完宝箱，回程后衔接自动挖罐"
                : "DR 已挖完宝箱，回程后恢复 BOCCHI 非法模式");
        EnqueueReturnStandby();
    }

    private void MaybeStopCofferHunt(long nowUnix)
    {
        if (!cofferHuntActive) return;
        if (nextSpawnTime <= 0 || nextSpawnTime - nowUnix > CofferHuntStopLeadSeconds) return;

        pendingCofferHuntAutoDigFor = displayPot != null && nextSpawnTime > 0 ? nextSpawnTime : -1;
        StopCofferHunt();
        autoDigTask?.Abort();
        autoDigStatus = "寻宝结束，回程";
        EnqueueReturnStandby();
    }

    private void StopCofferHunt()
    {
        SendCommand("/pdr ptreasure abort");
        ClearCofferHuntState();
        VnavStop();
    }

    private void ClearCofferHuntState()
    {
        cofferHuntActive    = false;
        cofferHuntTerritory = 0;
        drHuntStarted       = false;
    }

    private void EnqueueReturnStandby()
    {
        if (autoDigTask == null) return;
        var basePosition = GetCofferHuntBasePosition(GameState.TerritoryType);
        autoDigTask.Enqueue(() => { EndBocchiReturnSuppression(); UseReturn(); return true; });
        autoDigTask.Enqueue(WaitDrReturnToBase(basePosition, 15000));
        autoDigTask.Enqueue(PlayerReady);
        autoDigTask.Enqueue(() =>
        {
            var handoffToAutoDig = pendingCofferHuntAutoDigFor > 0 &&
                                   pendingCofferHuntAutoDigFor == nextSpawnTime &&
                                   displayPot != null;
            if (!handoffToAutoDig)
            {
                pendingCofferHuntAutoDigFor = -1;
                BocchiOn();
            }

            FinishAutoDig();
            return true;
        });
    }

    #endregion


    private bool EnqueueCrossDCOrStay()
    {
        if (autoDigTask == null) return true;

        if (crossDCTargetDC == 0 || string.IsNullOrEmpty(crossDCTargetWorld))
        {

            var reason = string.IsNullOrEmpty(crossDCReason) ? "无更优大区" : crossDCReason;
            autoDigStatus = $"未跨区: {reason}";
            NotifyHelper.Instance().NotificationInfo($"自动跨区未执行: {reason}");
            if (config.SendTTS) Speak("未跨区");
            autoDigTask.Enqueue(() => { EndBocchiReturnSuppression(); return BocchiOn(); });
            autoDigTask.Enqueue(() => { FinishAutoDig(); return true; });
            return true;
        }

        var world = crossDCTargetWorld;
        var territory = crossDCTargetTerritory;
        var entryCommand = territory == OccultNorthTerritory ? "/pdrfe ocn" : "/pdrfe ocs";
        autoDigStatus = $"跨区 → {world}";
        crossingDC    = true;
        NotifyHelper.Instance().NotificationInfo($"自动跨区 → {world} ({crossDCReason})");


        autoDigTask.Enqueue(() => SendCommand("/pdr leaveduty"));
        autoDigTask.Enqueue(WaitZone(territory, false, 20000));
        autoDigTask.DelayNext(3000);
        autoDigTask.Enqueue(PlayerReady);


        autoDigTask.Enqueue(() => SendCommand($"/pdr worldtravel {world}"));
        autoDigTask.DelayNext(15000);
        autoDigTask.Enqueue(PlayerReady);
        autoDigTask.DelayNext(3000);


        autoDigTask.Enqueue(() => SendCommand(entryCommand));
        autoDigTask.Enqueue(WaitZone(territory, true, 60000));
        autoDigTask.DelayNext(3000);

        autoDigTask.Enqueue(() =>
        {
            crossingDC = false;
            EndBocchiReturnSuppression();
            BocchiOn();
            FinishAutoDig();
            return true;
        });

        return true;
    }

    private void StartCrossDCQuery(uint territory)
    {
        crossDCQuerying    = true;
        crossDCTargetDC    = 0;
        crossDCTargetWorld = string.Empty;
        crossDCTargetTerritory = territory;
        crossDCReason      = "查询中…";
        _ = CrossDCQueryAsync(territory);
    }


    private Func<bool?> WaitCrossDCQuery(int timeoutMs)
    {
        long deadline = 0;
        return () =>
        {
            if (deadline == 0) deadline = Environment.TickCount64 + timeoutMs;
            if (!crossDCQuerying) return true;
            if (Environment.TickCount64 >= deadline)
            {
                crossDCReason = "查询超时";
                return true;
            }
            return false;
        };
    }

    private async Task CrossDCQueryAsync(uint territory)
    {
        try
        {
            var currentDC           = CurrentDataCenter();
            var (homeDC, homeWorld) = HomeInfo();
            var json = await Client.GetStringAsync(
                $"{TrackerBaseURL}{TrackerTable}?territory=eq.{territory}&datacenter=in.(101,102,103,104)&select=datacenter,pot_history,last_update&order=last_update.desc&limit=60");
            var rows = JsonConvert.DeserializeObject<CrossDCRow[]>(json);
            if (rows == null) { crossDCReason = "查询无数据(rows=null)"; return; }

            var now  = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var seen = new HashSet<ushort>();
            var best = ((ushort)0, long.MaxValue);

            foreach (var row in rows)
            {
                var dc = (ushort)row.Datacenter;
                if (!CrossDCWorlds.ContainsKey(dc) || !seen.Add(dc)) continue;

                var remaining = PredictRemaining(row.PotHistory, now);
                if (remaining <= 300) continue;
                if (remaining < best.Item2) best = (dc, remaining);
            }

            if (best.Item1 == 0)
                crossDCReason = seen.Count == 0 ? "查询到 0 个大区数据" : $"各大区罐子均 ≤5 分钟(已查{seen.Count}区)";
            else if (best.Item1 == currentDC)
                crossDCReason = $"当前{currentDC}区罐子最近({best.Item2 / 60}分),留守不跨";
            else
            {
                crossDCTargetDC    = best.Item1;

                crossDCTargetWorld = best.Item1 == homeDC && !string.IsNullOrEmpty(homeWorld)
                                         ? homeWorld
                                         : CrossDCWorlds[best.Item1];
                crossDCReason = $"→ {crossDCTargetWorld}({best.Item2 / 60}分)";
            }
        }
        catch (Exception ex)
        {
            crossDCReason = $"查询异常: {ex.GetType().Name}";
        }
        finally
        {
            crossDCQuerying = false;
        }
    }

    private static long PredictRemaining(string potHistory, long now)
    {
        if (string.IsNullOrEmpty(potHistory)) return long.MaxValue;

        SharedPot[]? pots;
        try   { pots = JsonConvert.DeserializeObject<SharedPot[]>(potHistory); }
        catch { return long.MaxValue; }
        if (pots == null) return long.MaxValue;

        long lastSpawn = -1;
        foreach (var pot in pots)
            if (pot.SpawnTime > lastSpawn) lastSpawn = pot.SpawnTime;
        if (lastSpawn <= 0) return long.MaxValue;

        return lastSpawn + Respawn - now;
    }

    private static ushort CurrentDataCenter() =>
        DService.Instance().ObjectTable.LocalPlayer is { } localPlayer
            ? (ushort)localPlayer.CurrentWorld.Value.DataCenter.RowId
            : (ushort)0;


    private static (ushort DC, string World) HomeInfo()
    {
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return (0, string.Empty);
        var home = localPlayer.HomeWorld.Value;
        return ((ushort)home.DataCenter.RowId, home.Name.ExtractText());
    }

    private static readonly Dictionary<ushort, string> CrossDCWorlds = new()
    {
        [101] = "晨曦王座",
        [102] = "白金幻象",
        [103] = "紫水栈桥",
        [104] = "红茶川"
    };

    private class CrossDCRow
    {
        [JsonProperty("datacenter")]
        public int Datacenter { get; set; }

        [JsonProperty("pot_history")]
        public string PotHistory = string.Empty;
    }

    private static bool SendCommand(string command)
    {
        ChatManager.Instance().SendMessage(command);
        return true;
    }

    private static void Speak(string text)
    {
        var normalized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length == 0) return;

        ChatManager.Instance().SendMessage($"/edgetts speak {normalized}");
    }


    private static bool BocchiOn()
    {
        SendCommand("/bocchiillegal on");
        return true;
    }


    private static bool EmergencyStopBocchi()
    {
        if (BocchiAutomator.TryEmergencyStop()) return true;

        SendCommand("/bocchiillegal off");
        return false;
    }

    private static bool PlayerReady()
    {
        var localPlayer = DService.Instance().ObjectTable.LocalPlayer;
        return localPlayer is { IsDead: false } && !DService.Instance().Condition[ConditionFlag.BetweenAreas];
    }

    private static unsafe void ClearCurrentTarget()
    {
        var targetSystem = TargetSystem.Instance();
        if (targetSystem != null)
            targetSystem->Target = null;
    }


    private static Func<bool?> WaitBocchiCombat(Pot target, int retryMs)
    {
        long nextRetryAt      = 0;
        long reenableAt       = 0;
        bool restartAttempted = false;

        return () =>
        {
            if (!target.Alive) return true;

            var now = Environment.TickCount64;

            if (reenableAt > 0)
            {
                if (now < reenableAt) return false;

                ClearCurrentTarget();
                BocchiOn();
                reenableAt = 0;
            }

            if (DService.Instance().Condition[ConditionFlag.InCombat]) return true;
            if (nextRetryAt == 0) nextRetryAt = now + retryMs;

            if (!restartAttempted && now >= nextRetryAt)
            {
                SendCommand("/bocchiillegal off");
                restartAttempted = true;
                reenableAt       = now + 700;
            }

            return false;
        };
    }


    private static void UseReturn() =>
        ChatManager.Instance().SendMessage("/return");


    private static unsafe void UseLureItem() =>
        AgentInventoryContext.Instance()->UseItem(LureItemID, InventoryType.KeyItems);


    private unsafe void UseLureForDirection()
    {
        if (undergroundDangerActive)
        {
            FailAutoDigMovement("仍处于遁地状态，已停止使用圣灵药以避免下坐骑");
            return;
        }

        digDirection      = string.Empty;
        awaitingDirection = true;

        UseLureItem();
    }


    private void EnqueuePtp(Pot target)
    {
        if (autoDigTask == null) return;

        if (target.TerritoryID == OccultNorthTerritory && GetNearestNorthAetheryte(target.World) is { } northAetheryte)
        {
            EnqueueNorthAetheryteTravel(northAetheryte);
            return;
        }

        var aetheryteName = target.AetheryteData?.Name ?? target.Aetheryte;
        if (string.IsNullOrWhiteSpace(aetheryteName)) return;

        autoDigTask.Enqueue(() => SendCommand($"/pdr ptp {aetheryteName}"));
        autoDigTask.DelayNext(1000);
        autoDigTask.Enqueue(WaitArrive(target.AetherytePos, 50f, 20000));
    }

    private void EnqueueNorthAetheryteTravel(CrescentAetheryte aetheryte)
    {
        if (autoDigTask == null) return;

        var directStarted      = false;
        var directRoadFound    = false;
        var directRoadPosition = Vector3.Zero;
        var directRoadReady    = false;
        var needsBaseApproach  = false;
        var baseRoadFound      = false;
        var baseRoadPosition   = Vector3.Zero;
        var baseRoadReady      = false;
        var baseTeleportStarted = false;
        var basePosition         = CrescentAetheryte.NorthHornBaseCamp.Position;


        autoDigTask.Enqueue(WaitDismounted(5000));
        autoDigTask.Enqueue(() =>
        {
            if (Arrived(aetheryte.Position, 50f)) return true;

            autoDigStatus = $"前往{autoDigTarget?.DirName ?? string.Empty}罐：传送至{aetheryte.Name}";
            directStarted = TryNativeAethernetTeleport(aetheryte);
            if (!directStarted && TryGetNearbyAethernetPosition(out directRoadPosition))
            {
                directRoadFound = true;
                VnavMoveTo(directRoadPosition);
            }
            return true;
        });
        autoDigTask.Enqueue(WaitAethernetInteractionRangeWhen(
            () => !directStarted && directRoadFound,
            () => directRoadPosition,
            10000));
        autoDigTask.Enqueue(() =>
        {
            if (!directStarted && directRoadFound)
            {
                VnavStop();
                directRoadReady = InAethernetInteractionRange(directRoadPosition);
                if (directRoadReady)
                    directStarted = TryNativeAethernetTeleport(aetheryte);
            }
            return true;
        });
        autoDigTask.Enqueue(WaitArriveWhen(() => directStarted, () => aetheryte.Position, 50f, 10000));
        autoDigTask.Enqueue(() =>
        {
            needsBaseApproach = !Arrived(aetheryte.Position, 50f);
            if (needsBaseApproach) UseReturn();
            return true;
        });
        autoDigTask.Enqueue(WaitDelayWhen(() => needsBaseApproach, 6000));
        autoDigTask.Enqueue(() => !needsBaseApproach || PlayerReady());
        autoDigTask.Enqueue(() =>
        {
            if (needsBaseApproach) VnavMoveTo(basePosition);
            return true;
        });
        autoDigTask.Enqueue(WaitArriveWhen(() => needsBaseApproach, () => basePosition, 3f, 20000));
        autoDigTask.Enqueue(() =>
        {
            if (needsBaseApproach) VnavStop();
            return true;
        });
        autoDigTask.Enqueue(WaitFindNearbyAethernetWhen(
            () => needsBaseApproach,
            position =>
            {
                baseRoadFound    = true;
                baseRoadPosition = position;
                VnavMoveTo(position);
            },
            5000));
        autoDigTask.Enqueue(WaitAethernetInteractionRangeWhen(
            () => needsBaseApproach && baseRoadFound,
            () => baseRoadPosition,
            20000));
        autoDigTask.Enqueue(() =>
        {
            if (!needsBaseApproach) return true;

            VnavStop();
            baseRoadReady = baseRoadFound && InAethernetInteractionRange(baseRoadPosition);
            if (baseRoadReady)
            {
                baseTeleportStarted = TryNativeAethernetTeleport(aetheryte);
                if (!baseTeleportStarted)
                    baseTeleportStarted = TryLifestreamAethernetTeleport(aetheryte.DataID);
            }

            if (!baseTeleportStarted)
                NotifyHelper.Instance().NotificationWarning($"未能启动前往{aetheryte.Name}的魔路传送，将直接寻路到罐点");
            return true;
        });
        autoDigTask.Enqueue(WaitArriveWhen(
            () => needsBaseApproach && baseTeleportStarted,
            () => aetheryte.Position,
            50f,
            20000));
    }

    private static CrescentAetheryte? GetNearestNorthAetheryte(Vector3 destination)
    {
        CrescentAetheryte? nearest = null;
        var nearestDistance = float.MaxValue;

        foreach (var candidate in CrescentAetheryte.NorthHornAetherytes)
        {
            var distance = Vector3.DistanceSquared(candidate.Position, destination);
            if (distance >= nearestDistance) continue;

            nearest         = candidate;
            nearestDistance = distance;
        }

        return nearest;
    }

    private static unsafe bool TryGetNearbyAethernetPosition(out Vector3 position)
    {
        position = Vector3.Zero;
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return false;

        if (TryGetNearbyAethernetObject(out _, out position))
            return true;

        var eventFramework = EventFramework.Instance();
        if (eventFramework != null &&
            eventFramework->TryGetNearestEvent(
                x => x.EventId.ContentId == EventHandlerContent.CustomTalk,
                x => x.NameString.Equals(LuminaWrapper.GetEObjName(2006473), StringComparison.OrdinalIgnoreCase) ||
                     x.NameString.Equals(LuminaWrapper.GetEObjName(2014664), StringComparison.OrdinalIgnoreCase),
                localPlayer.Position,
                out _,
                out var eventObjectID) &&
            DService.Instance().ObjectTable.SearchByID(eventObjectID) is { } targetObject &&
            LocalPlayerState.DistanceTo3DSquared(targetObject.Position) <= 100f * 100f)
        {
            position = targetObject.Position;
            return true;
        }

        // North Horn crystals are not always exposed through the same CustomTalk event/name as South Horn.
        // Fall back to Lifestream's interaction coordinates instead of the OmenTools teleport landing points.
        return TryGetKnownNearbyAethernetPosition(localPlayer.Position, out position);
    }

    private static bool TryGetKnownNearbyAethernetPosition(Vector3 playerPosition, out Vector3 position)
    {
        position = Vector3.Zero;
        var candidates = GameState.TerritoryType == OccultTerritory
                             ? CrescentAetheryte.SouthHornAetherytes
                             : GameState.TerritoryType == OccultNorthTerritory
                                 ? CrescentAetheryte.NorthHornAetherytes
                                 : null;
        if (candidates == null) return false;

        var nearestDistance = 100f * 100f;
        foreach (var candidate in candidates)
        {
            if (!TryGetAethernetInteractionPosition(candidate, out var interactionPosition)) continue;

            var deltaX   = playerPosition.X - interactionPosition.X;
            var deltaZ   = playerPosition.Z - interactionPosition.Z;
            var distance = (deltaX * deltaX) + (deltaZ * deltaZ);
            if (distance >= nearestDistance) continue;

            nearestDistance = distance;
            position        = interactionPosition;
        }

        return position != Vector3.Zero;
    }

    private static bool TryGetAethernetInteractionPosition(CrescentAetheryte aetheryte, out Vector3 position)
    {
        var interactionXZ = aetheryte.DataID switch
        {
            4927 => new Vector2(830.7f, -696.0f),
            4928 => new Vector2(-173.0f, -611.1f),
            4929 => new Vector2(-358.1f, -121.0f),
            4930 => new Vector2(306.9f, 305.7f),
            4947 => new Vector2(-384.1f, 281.4f),
            5571 => new Vector2(880.0f, 880.1f),
            5572 => new Vector2(357.7f, -554.3f),
            5573 => new Vector2(-547.2f, 594.4f),
            5574 => new Vector2(-388.6f, -440.5f),
            5575 => new Vector2(-13.7f, -40.5f),
            5576 => new Vector2(451.7f, 528.8f),
            _    => default
        };

        if (interactionXZ == default)
        {
            position = Vector3.Zero;
            return false;
        }

        position = new(interactionXZ.X, aetheryte.Position.Y, interactionXZ.Y);
        return true;
    }

    private static unsafe Func<bool?> WaitFindNearbyAethernetWhen(
        Func<bool> enabled,
        Action<Vector3> onFound,
        int timeoutMs)
    {
        long deadline = 0;
        return () =>
        {
            if (!enabled()) return true;

            var now = Environment.TickCount64;
            if (deadline == 0) deadline = now + timeoutMs;
            if (TryGetNearbyAethernetPosition(out var position))
            {
                onFound(position);
                return true;
            }

            return now >= deadline;
        };
    }

    private static Func<bool?> WaitAethernetInteractionRangeWhen(
        Func<bool> enabled,
        Func<Vector3> position,
        int timeoutMs)
    {
        long deadline = 0;
        return () =>
        {
            if (!enabled()) return true;

            var now = Environment.TickCount64;
            if (deadline == 0) deadline = now + timeoutMs;
            return InAethernetInteractionRange(position()) || now >= deadline;
        };
    }

    private static unsafe Func<bool?> WaitAethernetMenuOpenWhen(Func<bool> enabled, int timeoutMs)
    {
        long deadline       = 0;
        long nextInteractAt = 0;

        return () =>
        {
            if (!enabled()) return true;

            var agent = AgentTelepotTown.Instance();
            if (agent != null && agent->IsAgentActive()) return true;

            var now = Environment.TickCount64;
            if (deadline == 0) deadline = now + timeoutMs;
            if (now >= nextInteractAt)
            {
                TryInteractWithNearbyAethernet();
                nextInteractAt = now + 800;
            }

            return now >= deadline;
        };
    }

    private static bool InAethernetInteractionRange(Vector3 position)
    {
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return false;
        return Vector3.DistanceSquared(localPlayer.Position, position) <=
               AethernetInteractionDistance * AethernetInteractionDistance;
    }

    private static unsafe bool TryGetNearbyAethernetObject(out nint address, out Vector3 position)
    {
        address  = 0;
        position = Vector3.Zero;
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return false;

        var roadName       = LuminaWrapper.GetEObjName(2006473);
        var occultRoadName = LuminaWrapper.GetEObjName(2014664);
        var shardName      = LuminaWrapper.GetEObjName(2014665);
        var bestDistance   = 100f * 100f;

        foreach (var obj in DService.Instance().ObjectTable)
        {
            if (!obj.IsTargetable || obj.Address == 0) continue;

            var gameObject = (GameObject*)obj.Address;
            var name       = obj.Name.ToString();
            var isRoad     = gameObject != null && gameObject->ObjectKind == ObjectKind.Aetheryte ||
                             name.Equals(roadName, StringComparison.OrdinalIgnoreCase) ||
                             name.Equals(occultRoadName, StringComparison.OrdinalIgnoreCase) ||
                             name.Equals(shardName, StringComparison.OrdinalIgnoreCase);
            if (!isRoad) continue;

            var distance = Vector3.DistanceSquared(localPlayer.Position, obj.Position);
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            address      = obj.Address;
            position     = obj.Position;
        }

        return address != 0;
    }

    private static unsafe bool TryInteractWithNearbyAethernet()
    {
        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null ||
            !TryGetNearbyAethernetObject(out var address, out var position) ||
            !InAethernetInteractionRange(position))
            return false;

        var gameObject = (GameObject*)address;
        if (gameObject == null) return false;

        targetSystem->Target = gameObject;
        targetSystem->InteractWithObject(gameObject, false);
        return true;
    }

    private static unsafe bool TryNativeAethernetTeleport(CrescentAetheryte aetheryte)
    {
        if (aetheryte.TeleportTo()) return true;
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return false;

        var eventFramework = EventFramework.Instance();
        if (eventFramework == null ||
            !eventFramework->TryGetNearestEvent(
                x => x.EventId.ContentId == EventHandlerContent.CustomTalk,
                x => x.NameString.Equals(LuminaWrapper.GetEObjName(2006473), StringComparison.OrdinalIgnoreCase) ||
                     x.NameString.Equals(LuminaWrapper.GetEObjName(2014664), StringComparison.OrdinalIgnoreCase),
                localPlayer.Position,
                out var eventID,
                out var eventObjectID) ||
            DService.Instance().ObjectTable.SearchByID(eventObjectID) is not { } targetObject ||
            LocalPlayerState.DistanceTo3DSquared(targetObject.Position) > 16f)
            return false;

        new EventStartPackt(eventObjectID, eventID).Send();
        new EventCompletePackt(721820, 16777216, aetheryte.DataID).Send();
        return true;
    }

    private static unsafe bool TryAethernetTeleportFromOpenMenu(CrescentAetheryte aetheryte)
    {
        var agent = AgentTelepotTown.Instance();
        return agent != null && agent->IsAgentActive() && aetheryte.TeleportTo();
    }

    private static bool TryLifestreamAethernetTeleport(uint placeNameID)
    {
        try
        {
            return DService.Instance().PI
                           .GetIpcSubscriber<uint, bool>("Lifestream.AethernetTeleportByPlaceNameId")
                           .InvokeFunc(placeNameID);
        }
        catch
        {
            return false;
        }
    }

    private static uint GetLifestreamActiveCustomAetheryte()
    {
        try
        {
            return DService.Instance().PI
                           .GetIpcSubscriber<uint>("Lifestream.GetActiveCustomAetheryte")
                           .InvokeFunc();
        }
        catch
        {
            return 0;
        }
    }


    private void EnqueueMoveTo(Vector3 position, float tolerance, bool mount = true, int timeoutMs = 90000)
    {
        if (autoDigTask == null) return;

        if (mount)
            autoDigTask.Enqueue(WaitMounted());

        autoDigTask.Enqueue(() => { VnavMoveTo(position); return true; });
        autoDigTask.Enqueue(WaitArrive(position, tolerance, timeoutMs));
        autoDigTask.Enqueue(() => { VnavStop(); return true; });
    }

    private void EnqueueUndergroundMoveTo(Vector3 position, float tolerance, int timeoutMs = 90000)
    {
        if (autoDigTask == null) return;

        autoDigTask.Enqueue(WaitMounted());
        autoDigTask.Enqueue(() =>
        {
            if (!DService.Instance().Condition[ConditionFlag.Mounted])
                return FailAutoDigMovement("未能确认坐骑状态，已停止遁地移动");

            if (!undergroundDangerActive)
                DService.Instance().Log.Information(
                    $"[OccultPotNotifier] Enter underground danger route: {position.X:F2}, {position.Y:F2}, {position.Z:F2}");
            BeginUndergroundDangerMode();
            return true;
        });
        autoDigTask.Enqueue(WaitUndergroundArrive(position, tolerance, timeoutMs));
    }

    private unsafe void EnqueueReturnToSurface(Vector3 position)
    {
        if (autoDigTask == null) return;

        long deadline = 0;
        float startPacketHeight = 0;
        autoDigTask.Enqueue(() =>
        {
            if (!undergroundDangerActive) return true;
            if (!DService.Instance().Condition[ConditionFlag.Mounted] ||
                DService.Instance().ObjectTable.LocalPlayer is not { IsDead: false } localPlayer)
                return FailAutoDigMovement("未能保持坐骑状态，已停止遁地寻宝");

            var now = Environment.TickCount64;
            if (deadline == 0)
            {
                deadline          = now + UndergroundReturnTimeoutMS;
                startPacketHeight = undergroundPacketHeight ?? GetUndergroundHeight(position);
                VnavStop();
                autoDigStatus = "危险区：平滑返回地表";
                DService.Instance().Log.Information(
                    $"[OccultPotNotifier] Smooth surface return started: Y={startPacketHeight:F2} -> {position.Y:F2}");
            }

            if (now >= deadline)
                return FailAutoDigMovement("平滑返回地表超时，已停止遁地寻宝");

            var currentPacketHeight = undergroundPacketHeight ?? startPacketHeight;
            var remainingHeight     = position.Y - currentPacketHeight;
            var maxStep = MathF.Min(
                UndergroundReturnSpeed * MathF.Max(GameState.DeltaTime, 0f),
                UndergroundReturnMaxStep);
            if (maxStep <= 0f) return false;

            var step = MathF.Min(MathF.Abs(remainingHeight), maxStep);
            var nextPacketHeight = currentPacketHeight + MathF.CopySign(step, remainingHeight);
            if (MathF.Abs(remainingHeight) <= UndergroundReturnTolerance || step >= MathF.Abs(remainingHeight))
                nextPacketHeight = position.Y;

            allowUndergroundPositionUpdate = true;
            try
            {
                ((GameObject*)localPlayer.Address)->SetPosition(position.X, position.Y, position.Z);
                new PositionUpdateInstancePacket(
                    localPlayer.Rotation,
                    new Vector3(position.X, nextPacketHeight, position.Z),
                    PositionUpdateInstancePacket.MoveType.NormalMove0).Send();
                undergroundPacketHeight  = nextPacketHeight;
                undergroundSurfaceHeight = position.Y;
            }
            catch
            {
                return FailAutoDigMovement("恢复地表位置失败，已停止遁地寻宝");
            }
            finally
            {
                allowUndergroundPositionUpdate = false;
            }

            if (nextPacketHeight != position.Y) return false;

            EndUndergroundDangerMode();
            DService.Instance().Log.Information(
                $"[OccultPotNotifier] Smooth surface return completed: {position.X:F2}, {position.Y:F2}, {position.Z:F2}");
            return true;
        });
    }

    private unsafe Func<bool?> WaitUndergroundArrive(Vector3 position, float tolerance, int timeoutMs)
    {
        long deadline        = 0;
        long settleAfter     = 0;
        long nextMountTry    = 0;
        long remountDeadline = 0;
        return () =>
        {
            var now = Environment.TickCount64;
            if (deadline == 0)
            {
                deadline = now + timeoutMs;
                settleAfter = now + UndergroundSettleMS;
                VnavStop();
            }

            if (!InOccultMapZone || DService.Instance().ObjectTable.LocalPlayer is not { IsDead: false })
                return FailAutoDigMovement("角色状态异常，已停止遁地移动");


            if (!DService.Instance().Condition[ConditionFlag.Mounted])
            {
                VnavStop();
                autoDigStatus = "危险区：重新上坐骑";
                if (remountDeadline == 0) remountDeadline = now + MountTimeoutMS;
                if (now >= remountDeadline)
                    return FailAutoDigMovement("遁地移动途中无法重新上坐骑，已安全停止");

                var condition = DService.Instance().Condition;
                if (!condition.IsCasting &&
                    !condition[ConditionFlag.BetweenAreas] &&
                    !condition[ConditionFlag.OccupiedInQuestEvent] &&
                    now >= nextMountTry)
                {
                    Mount();
                    nextMountTry = now + 1500;
                }
                return false;
            }

            if (remountDeadline != 0)
            {
                deadline        = now + timeoutMs;
                settleAfter     = now + UndergroundSettleMS;
                remountDeadline = 0;
            }

            autoDigStatus = "危险区：遁地移动";
            try
            {
                MoveUndergroundTo(position);
            }
            catch
            {
                return FailAutoDigMovement("遁地移动执行异常，已恢复并停止自动挖罐");
            }



            if (now >= settleAfter && Arrived(position, tolerance)) return true;
            if (now >= deadline)
                return FailAutoDigMovement("遁地移动超时，未到达目标点，已安全停止");

            return false;
        };
    }

    private unsafe void BeginUndergroundDangerMode()
    {
        undergroundDangerActive = true;
        undergroundPacketHeight = null;
        undergroundSurfaceHeight = null;
        autoDigStatus = "危险区：遁地移动";
        var playerController = PlayerController.Instance();
        if (playerController != null)
            playerController->MoveControllerWalk.IsMovementInputLocked = true;
    }

    private unsafe void EndUndergroundDangerMode()
    {
        if (undergroundDangerActive)
            DService.Instance().Log.Information("[OccultPotNotifier] Leave underground danger route");
        undergroundDangerActive = false;
        allowUndergroundPositionUpdate = false;
        undergroundPacketHeight = null;
        undergroundSurfaceHeight = null;
        var playerController = PlayerController.Instance();
        if (playerController != null)
            playerController->MoveControllerWalk.IsMovementInputLocked = false;
    }

    private static float GetUndergroundHeight(Vector3 surfacePosition) =>
        MathF.Max(surfacePosition.Y - UndergroundDepth, UndergroundMinHeight);

    private unsafe void MoveUndergroundTo(Vector3 position)
    {
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer)
            return;

        var playerController = PlayerController.Instance();
        if (playerController != null && playerController->MoveState == 3)
            playerController->MoveState = 1;

        var current = localPlayer.Position;
        var horizontalDelta = new Vector3(position.X, current.Y, position.Z) - current;
        var distance = horizontalDelta.Length();
        var step = UndergroundMoveSpeed * GameState.DeltaTime;
        var reachedTarget = distance < 0.1f || step >= distance;
        var next = reachedTarget
                       ? new Vector3(position.X, current.Y, position.Z)
                       : current + (horizontalDelta / distance * step);

        if (reachedTarget)
            undergroundSurfaceHeight = position.Y;
        else if (RaycastHelper.TryGetGroundHit(next, out var groundHit))
            undergroundSurfaceHeight = groundHit.Point.Y;
        else if (undergroundSurfaceHeight == null)
            undergroundSurfaceHeight = current.Y;

        var surfaceHeight = undergroundSurfaceHeight ?? position.Y;
        var targetPacketHeight = GetUndergroundHeight(new Vector3(next.X, surfaceHeight, next.Z));
        var currentPacketHeight = undergroundPacketHeight ?? targetPacketHeight;
        var nextPacketHeight = targetPacketHeight < currentPacketHeight
                                   ? targetPacketHeight
                                   : MathF.Min(targetPacketHeight, currentPacketHeight + step);
        undergroundPacketHeight = nextPacketHeight;

        var localPosition = new Vector3(next.X, nextPacketHeight + UndergroundDepth, next.Z);
        var packetPosition = new Vector3(next.X, nextPacketHeight, next.Z);

        ((GameObject*)localPlayer.Address)->SetPosition(localPosition.X, localPosition.Y, localPosition.Z);
        new PositionUpdateInstancePacket(
            localPlayer.Rotation,
            packetPosition,
            PositionUpdateInstancePacket.MoveType.NormalMove0).Send();
    }

    private void OnUndergroundTestCommand(string command, string args)
    {
        var arg = args.Trim().ToLowerInvariant();
        if (arg is not ("" or "on" or "off" or "toggle"))
        {
            NotifyHelper.Instance().NotificationInfo(
                $"测试指令：/ktb {UndergroundTestCommand} [on|off]");
            return;
        }

        var shouldStop = arg == "off" || undergroundTestActive && arg != "on";
        if (shouldStop)
        {
            if (undergroundTestActive)
                RequestUndergroundTestStop();
            else
                NotifyHelper.Instance().NotificationInfo("遁地测试当前未开启");
            return;
        }

        if (undergroundTestActive)
        {
            NotifyHelper.Instance().NotificationInfo("遁地测试已开启；使用 off 或再次执行指令退出");
            return;
        }

        StartUndergroundTest();
    }

    private void StartUndergroundTest()
    {
        var localPlayer = DService.Instance().ObjectTable.LocalPlayer;
        var condition   = DService.Instance().Condition;
        if (!InOccultMapZone || InForkedTower || localPlayer is not { IsDead: false })
        {
            NotifyHelper.Instance().NotificationWarning("遁地测试只能由存活角色在新月岛野外使用");
            return;
        }

        if (autoDigActive || cofferHuntActive || standbyDeathReturning || crossingDC || undergroundDangerActive ||
            condition[ConditionFlag.InCombat] || condition[ConditionFlag.BetweenAreas] ||
            condition[ConditionFlag.OccupiedInQuestEvent] ||
            InOrSettlingFateOrCriticalEngagement(localPlayer) ||
            BocchiAutomator.IsTravellingToFateOrCriticalEncounter())
        {
            NotifyHelper.Instance().NotificationWarning("当前有战斗、寻路或挖罐流程，不能开始遁地测试");
            return;
        }

        if (undergroundTestTask == null) return;

        undergroundTestActive          = true;
        undergroundTestMovementReady   = false;
        undergroundTestMoveOutward     = true;
        undergroundTestStopRequested   = false;
        undergroundTestSurfacePosition = localPlayer.Position;
        undergroundTestOuterPosition   = Vector3.Zero;
        undergroundTestTerritory       = GameState.TerritoryType;
        undergroundTestNextMoveAt      = 0;
        undergroundTestStopDeadline    = 0;
        undergroundTestTask.Abort();
        FrameworkManager.Instance().Reg(OnUndergroundTestSafety, 50);

        undergroundTestTask.Enqueue(WaitUndergroundTestMounted());
        undergroundTestTask.Enqueue(() =>
        {
            if (!undergroundTestActive) return true;
            if (DService.Instance().ObjectTable.LocalPlayer is not { IsDead: false } player ||
                !DService.Instance().Condition[ConditionFlag.Mounted])
                return FailUndergroundTest("角色或坐骑状态异常，遁地测试已取消");

            undergroundTestSurfacePosition = player.Position;
            DService.Instance().Log.Information(
                $"[OccultPotNotifier] Enter underground test: {player.Position.X:F2}, {player.Position.Y:F2}, {player.Position.Z:F2}");
            BeginUndergroundDangerMode();
            return true;
        });
        undergroundTestTask.Enqueue(WaitUndergroundTestSettled());
        undergroundTestTask.Enqueue(() =>
        {
            if (!undergroundTestActive) return true;

            if (DService.Instance().ObjectTable.LocalPlayer is not { IsDead: false } player)
                return FailUndergroundTest("角色状态异常，遁地移动测试已取消");

            var forward = new Vector3(MathF.Sin(player.Rotation), 0, MathF.Cos(player.Rotation));
            undergroundTestOuterPosition = undergroundTestSurfacePosition +
                                           (forward * UndergroundTestMoveDistance);
            undergroundTestMoveOutward   = true;
            undergroundTestNextMoveAt    = Environment.TickCount64 + 1_500;
            undergroundTestMovementReady = true;
            var undergroundHeight = GetUndergroundHeight(player.Position);
            NotifyHelper.Instance().NotificationInfo(
                $"遁地测试已进入 Y={undergroundHeight:F0}；将沿面向往返 {UndergroundTestMoveDistance:F0} 米，再次执行指令退出");
            return true;
        });

        NotifyHelper.Instance().NotificationInfo("遁地测试准备中：正在确认坐骑状态");
    }

    private Func<bool?> WaitUndergroundTestMounted()
    {
        long deadline = 0;
        long nextTry  = 0;
        return () =>
        {
            if (!undergroundTestActive) return true;

            var now = Environment.TickCount64;
            if (DService.Instance().Condition[ConditionFlag.Mounted]) return true;
            if (deadline == 0) deadline = now + MountTimeoutMS;
            if (now >= deadline)
                return FailUndergroundTest("无法上坐骑，遁地测试已取消");

            if (!InOccultMapZone || DService.Instance().ObjectTable.LocalPlayer is not { IsDead: false })
                return FailUndergroundTest("角色状态异常，遁地测试已取消");

            var condition = DService.Instance().Condition;
            if (!condition.IsCasting &&
                !condition[ConditionFlag.InCombat] &&
                !condition[ConditionFlag.BetweenAreas] &&
                !condition[ConditionFlag.OccupiedInQuestEvent] &&
                now >= nextTry)
            {
                Mount();
                nextTry = now + 1500;
            }
            return false;
        };
    }

    private Func<bool?> WaitUndergroundTestSettled()
    {
        long settleAfter = 0;
        return () =>
        {
            if (!undergroundTestActive) return true;
            if (!InOccultMapZone || DService.Instance().ObjectTable.LocalPlayer is not { IsDead: false } ||
                !DService.Instance().Condition[ConditionFlag.Mounted])
                return FailUndergroundTest("角色或坐骑状态异常，遁地测试已安全停止");

            var now = Environment.TickCount64;
            if (settleAfter == 0) settleAfter = now + UndergroundSettleMS;

            try
            {
                MoveUndergroundTo(undergroundTestSurfacePosition);
            }
            catch
            {
                return FailUndergroundTest("遁地位置更新异常，测试已安全停止");
            }

            return now >= settleAfter;
        };
    }

    private void OnUndergroundTestSafety(IFramework _)
    {
        if (!undergroundTestActive)
        {
            FrameworkManager.Instance().Unreg(OnUndergroundTestSafety);
            return;
        }

        var localPlayer = DService.Instance().ObjectTable.LocalPlayer;
        if (!InOccultMapZone || InForkedTower || GameState.TerritoryType != undergroundTestTerritory ||
            autoDigActive || cofferHuntActive ||
            localPlayer is not { IsDead: false } ||
            DService.Instance().Condition[ConditionFlag.InCombat] ||
            DService.Instance().Condition[ConditionFlag.BetweenAreas] ||
            InOrSettlingFateOrCriticalEngagement(localPlayer) ||
            BocchiAutomator.IsTravellingToFateOrCriticalEncounter())
        {
            FailUndergroundTest("角色状态或区域已变化，遁地测试已安全停止");
            return;
        }

        if (undergroundDangerActive && !DService.Instance().Condition[ConditionFlag.Mounted])
        {
            FailUndergroundTest("测试中失去坐骑状态，已立即恢复地表位置");
            return;
        }

        if (!undergroundDangerActive || !undergroundTestMovementReady) return;

        var now = Environment.TickCount64;
        if (undergroundTestStopRequested && now >= undergroundTestStopDeadline)
        {
            FailUndergroundTest("返回测试起点超时，已在当前位置恢复地表");
            return;
        }

        var target = undergroundTestStopRequested || !undergroundTestMoveOutward
                         ? undergroundTestSurfacePosition
                         : undergroundTestOuterPosition;
        if (Arrived(target, UndergroundTestMoveTolerance))
        {
            if (undergroundTestStopRequested)
            {
                StopUndergroundTest(true);
                return;
            }

            undergroundTestMoveOutward = !undergroundTestMoveOutward;
            undergroundTestNextMoveAt  = now + UndergroundTestEndpointPauseMS;
            return;
        }

        if (now < undergroundTestNextMoveAt) return;

        try
        {
            MoveUndergroundTo(target);
        }
        catch
        {
            FailUndergroundTest("遁地往返移动异常，测试已安全停止");
        }
    }

    private void RequestUndergroundTestStop()
    {
        if (!undergroundDangerActive || !undergroundTestMovementReady ||
            !DService.Instance().Condition[ConditionFlag.Mounted])
        {
            StopUndergroundTest(true);
            return;
        }

        if (undergroundTestStopRequested)
        {
            NotifyHelper.Instance().NotificationInfo("遁地测试正在返回起点");
            return;
        }

        undergroundTestStopRequested = true;
        undergroundTestNextMoveAt    = 0;
        undergroundTestStopDeadline  = Environment.TickCount64 + UndergroundTestStopTimeoutMS;
        NotifyHelper.Instance().NotificationInfo("遁地测试正在地下返回起点，随后恢复地表位置");
    }

    private bool FailUndergroundTest(string message)
    {
        DService.Instance().Log.Warning($"[OccultPotNotifier] {message}");
        StopUndergroundTest(false);
        NotifyHelper.Instance().NotificationWarning(message);
        return true;
    }

    private unsafe void StopUndergroundTest(bool notify)
    {
        if (!undergroundTestActive) return;

        undergroundTestTask?.Abort();
        FrameworkManager.Instance().Unreg(OnUndergroundTestSafety);

        if (undergroundDangerActive && InOccultMapZone &&
            GameState.TerritoryType == undergroundTestTerritory &&
            !DService.Instance().Condition[ConditionFlag.BetweenAreas] &&
            DService.Instance().ObjectTable.LocalPlayer is { } localPlayer)
        {
            const PositionUpdateInstancePacket.MoveType moveType =
                PositionUpdateInstancePacket.MoveType.NormalMove0;
            allowUndergroundPositionUpdate = true;
            try
            {
                new PositionUpdateInstancePacket(
                    localPlayer.Rotation,
                    new Vector3(
                        localPlayer.Position.X,
                        undergroundTestSurfacePosition.Y,
                        localPlayer.Position.Z),
                    moveType).Send();
                DService.Instance().Log.Information("[OccultPotNotifier] Underground test restored surface position");
            }
            finally
            {
                allowUndergroundPositionUpdate = false;
            }
        }

        undergroundTestActive          = false;
        undergroundTestMovementReady   = false;
        undergroundTestMoveOutward     = false;
        undergroundTestStopRequested   = false;
        undergroundTestSurfacePosition = Vector3.Zero;
        undergroundTestOuterPosition   = Vector3.Zero;
        undergroundTestTerritory       = 0;
        undergroundTestNextMoveAt      = 0;
        undergroundTestStopDeadline    = 0;
        EndUndergroundDangerMode();
        if (!autoDigActive)
            autoDigStatus = string.Empty;

        if (notify)
            NotifyHelper.Instance().NotificationInfo("遁地测试已结束并恢复地表位置");
    }


    private static Func<bool?> WaitArrive(Vector3 position, float tolerance, int timeoutMs)
    {
        long deadline = 0;
        return () =>
        {
            if (deadline == 0) deadline = Environment.TickCount64 + timeoutMs;
            return Arrived(position, tolerance) || Environment.TickCount64 >= deadline;
        };
    }

    private static Func<bool?> WaitArriveWhen(Func<bool> enabled, Func<Vector3> position, float tolerance, int timeoutMs)
    {
        long deadline = 0;
        return () =>
        {
            if (!enabled()) return true;
            if (deadline == 0) deadline = Environment.TickCount64 + timeoutMs;
            return Arrived(position(), tolerance) || Environment.TickCount64 >= deadline;
        };
    }

    private static Func<bool?> WaitDelayWhen(Func<bool> enabled, int delayMs)
    {
        long deadline = 0;
        return () =>
        {
            if (!enabled()) return true;
            if (deadline == 0) deadline = Environment.TickCount64 + delayMs;
            return Environment.TickCount64 >= deadline;
        };
    }


    private static Func<bool?> WaitOutOfCombat(int timeoutMs)
    {
        long deadline = 0;
        return () =>
        {
            if (deadline == 0) deadline = Environment.TickCount64 + timeoutMs;
            return !DService.Instance().Condition[ConditionFlag.InCombat] || Environment.TickCount64 >= deadline;
        };
    }


    private Func<bool?> WaitDirection(int timeoutMs)
    {
        long deadline = 0;
        return () =>
        {
            if (deadline == 0) deadline = Environment.TickCount64 + timeoutMs;
            return !string.IsNullOrEmpty(digDirection) || Environment.TickCount64 >= deadline;
        };
    }


    private static Func<bool?> WaitLure(int timeoutMs)
    {
        long deadline = 0;
        return () =>
        {
            if (deadline == 0) deadline = Environment.TickCount64 + timeoutMs;
            return HasLure() || Environment.TickCount64 >= deadline;
        };
    }


    private static Func<bool?> WaitBuffGone(int timeoutMs)
    {
        long deadline = 0;
        return () =>
        {
            if (deadline == 0) deadline = Environment.TickCount64 + timeoutMs;
            return !HasLure() || Environment.TickCount64 >= deadline;
        };
    }

    private static Vector3 RandomOffset(Vector3 pos, float maxRadius)
    {
        var angle  = Random.Shared.NextSingle() * MathF.Tau;
        var radius = Random.Shared.NextSingle() * maxRadius;
        return new Vector3(pos.X + (MathF.Cos(angle) * radius), pos.Y, pos.Z + (MathF.Sin(angle) * radius));
    }


    private Func<bool?> WaitMounted(int timeoutMs = MountTimeoutMS)
    {
        long deadline = 0;
        long nextTry  = 0;
        return () =>
        {
            var now = Environment.TickCount64;
            if (DService.Instance().Condition[ConditionFlag.Mounted]) return true;
            if (deadline == 0) deadline = now + timeoutMs;
            if (now >= deadline)
                return FailAutoDigMovement("无法上坐骑，已停止自动移动");

            if (!InOccultMapZone || DService.Instance().ObjectTable.LocalPlayer is not { IsDead: false })
                return FailAutoDigMovement("角色状态异常，无法上坐骑，已停止自动移动");

            var condition = DService.Instance().Condition;
            if (!condition.IsCasting &&
                !condition[ConditionFlag.BetweenAreas] &&
                !condition[ConditionFlag.OccupiedInQuestEvent] &&
                now >= nextTry)
            {
                Mount();
                nextTry = now + 1500;
            }
            return false;
        };
    }


    private Func<bool?> WaitDismounted(int timeoutMs)
    {
        long deadline = 0;
        long nextTry  = 0;
        return () =>
        {
            var now = Environment.TickCount64;
            if (deadline == 0) deadline = now + timeoutMs;
            if (!DService.Instance().Condition[ConditionFlag.Mounted]) return true;
            if (now >= deadline)
                return FailAutoDigMovement("无法下坐骑，已停止后续交互");
            if (now >= nextTry) { Dismount(); nextTry = now + 500; }
            return false;
        };
    }

    private bool FailAutoDigMovement(string message)
    {
        DService.Instance().Log.Warning($"[OccultPotNotifier] {message}");
        NotifyHelper.Instance().NotificationInfo(message);
        AbortAutoDig();
        BocchiOn();
        return true;
    }




    private Func<bool?> WaitTreasureAtPoint(Vector3 target, int timeoutMs)
    {
        long readyDeadline  = 0;
        long resultDeadline = 0;
        bool lureUsed       = false;
        bool lureHadBeforeUse = false;
        return () =>
        {
            var now = Environment.TickCount64;
            if (readyDeadline == 0) readyDeadline = now + TreasureProbeReadyTimeoutMS;



            if (lureUsed && lureHadBeforeUse && !HasLure() && NewCofferNearby(PotTreasureOpenRadius))
                treasureRevealed = true;
            if (treasureRevealed)
            {
                awaitingDirection = false;
                return true;
            }

            if (lureUsed)
            {
                if (!string.IsNullOrEmpty(digDirection)) return true;
                if (now < resultDeadline) return false;
                awaitingDirection = false;
                return true;
            }


            if (!Arrived(target, 3f))
                return FailAutoDigMovement("未能稳定到达候选点，已停止自动挖罐");

            var condition = DService.Instance().Condition;
            if (condition[ConditionFlag.Mounted])
            {
                Dismount();
                return false;
            }

            if (condition.IsCasting ||
                condition[ConditionFlag.InCombat] ||
                condition[ConditionFlag.BetweenAreas] ||
                condition[ConditionFlag.OccupiedInQuestEvent])
            {
                autoDigStatus = "候选点：等待使用圣灵药";
                if (now < readyDeadline) return false;
                return FailAutoDigMovement("候选点长时间无法使用圣灵药，已停止自动挖罐");
            }

            CaptureExistingCoffers();
            lureHadBeforeUse = HasLure();
            UseLureForDirection();
            lureUsed       = true;
            resultDeadline = now + timeoutMs;
            return false;
        };
    }



    private unsafe Func<bool?> WaitTreasureOpened(int timeoutMs)
    {
        long deadline              = 0;
        long interactionRequestedAt = 0;
        long interactionEndedAt     = 0;
        bool readBarObserved         = false;
        return () =>
        {
            if (!treasureRevealed) return true;

            var now = Environment.TickCount64;
            if (deadline == 0) deadline = now + timeoutMs;
            autoDigStatus = readBarObserved ? "读条开启撒娇罐宝箱" : "交互撒娇罐宝箱";

            var tracked = FindMagicPotCoffer(treasureEntityId);
            if (tracked == null && treasureInteractionStarted)
            {
                RestoreMagicPotCofferInteractionPosition();
                DService.Instance().Log.Information(
                    $"[OccultPotNotifier] Magic Pot coffer opened: 0x{treasureEntityId:X8}");
                return true;
            }

            var condition = DService.Instance().Condition;
            var interactionBusy = condition.IsCasting ||
                                  condition[ConditionFlag.OccupiedInQuestEvent];
            if (treasureInteractionStarted && interactionBusy)
            {
                readBarObserved     = true;
                interactionEndedAt = 0;
                return false;
            }

            if (readBarObserved)
            {

                if (interactionEndedAt == 0) interactionEndedAt = now + 1000;
                if (now < interactionEndedAt) return false;


                readBarObserved             = false;
                treasureInteractionStarted = false;
                interactionRequestedAt     = 0;
                interactionEndedAt         = 0;
            }


            if (!treasureInteractionStarted || now - interactionRequestedAt >= 5000)
            {
                if (TryInteractWithMagicPotCoffer())
                {
                    treasureInteractionStarted = true;
                    interactionRequestedAt     = now;
                }
            }

            if (now >= deadline)
                return FailAutoDigMovement("撒娇罐宝箱交互或读条超时，已停止自动挖罐");
            return false;
        };
    }

    private static Func<bool?> WaitZone(uint territory, bool wantInside, int timeoutMs)
    {
        long deadline = 0;
        return () =>
        {
            if (deadline == 0) deadline = Environment.TickCount64 + timeoutMs;
            return (GameState.TerritoryType == territory) == wantInside ||
                   Environment.TickCount64 >= deadline;
        };
    }


    private static unsafe bool HasLure()
    {
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return false;

        var chara = (BattleChara*)localPlayer.Address;
        if (chara != null && chara->GetStatusManager()->HasStatus(LureStatusID)) return true;

        foreach (var status in localPlayer.StatusList)
            if (status.StatusID == LureStatusID) return true;

        return false;
    }

    private bool ShouldFinishExpiredLure()
    {
        if (!autoDigLureAcquired || cofferHuntActive || treasureRevealed || autoDigDying || crossingDC)
        {
            autoDigLureMissingAt = 0;
            return false;
        }

        if (autoDigLureExhausted) return true;

        var condition = DService.Instance().Condition;
        if (DService.Instance().ObjectTable.LocalPlayer is null || condition[ConditionFlag.BetweenAreas])
        {
            autoDigLureMissingAt = 0;
            return false;
        }

        if (HasLure())
        {
            autoDigLureMissingAt = 0;
            return false;
        }

        var now = Environment.TickCount64;
        if (autoDigLureMissingAt == 0)
        {
            autoDigLureMissingAt = now;
            return false;
        }

        return now - autoDigLureMissingAt >= AutoDigLureMissingGraceMS;
    }

    private void FinishExpiredLureSearch()
    {
        autoDigTask?.Abort();
        awaitingDirection = false;
        ResetAutoDigCandidateSearch();
        VnavStop();
        ResetAutoDigLureState();
        autoDigStatus = "撒娇罐力量耗尽，结束挖宝";
        NotifyHelper.Instance().NotificationInfo("撒娇罐力量已耗尽，已停止寻找本轮宝箱");
        EnqueueFinish();
    }

    private void ResetAutoDigLureState()
    {
        autoDigLureAcquired  = false;
        autoDigLureExhausted = false;
        autoDigLureMissingAt = 0;
    }

    private static bool Mount()
    {
        if (DService.Instance().Condition[ConditionFlag.Mounted]) return true;
        UseActionManager.Instance().UseAction(ActionType.GeneralAction, 9);
        return true;
    }

    private void Dismount()
    {
        if (undergroundDangerActive)
        {
            DService.Instance().Log.Warning(
                "[OccultPotNotifier] Blocked dismount while underground danger route is active");
            return;
        }

        if (DService.Instance().Condition[ConditionFlag.Mounted])
            ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Dismount);
    }

    private static void VnavMoveTo(Vector3 position) =>
        ChatManager.Instance().SendMessage(FormattableString.Invariant($"/vnav moveto {position.X} {position.Y} {position.Z}"));

    private static void VnavStop() =>
        ChatManager.Instance().SendMessage("/vnav stop");

    private static bool Arrived(Vector3 position, float tolerance)
    {
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return false;

        var deltaX = localPlayer.Position.X - position.X;
        var deltaZ = localPlayer.Position.Z - position.Z;
        return (deltaX * deltaX) + (deltaZ * deltaZ) <= tolerance * tolerance;
    }

    private void CaptureExistingCoffers()
    {
        preexistingCofferEntityIds.Clear();
        foreach (var obj in DService.Instance().ObjectTable)
        {
            if (!IsMagicPotCoffer(obj)) continue;
            var entityId = unchecked((uint)obj.GameObjectID);
            if (entityId != 0) preexistingCofferEntityIds.Add(entityId);
        }
    }



    private static bool IsMagicPotCoffer(OmenGameObject obj)
    {
        // Magic Pot coffers use a dedicated read-bar EventObj protocol.
        // Keep the exact four-ID whitelist separate from ordinary treasure handling.
        if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj) return false;

        return obj.DataID is 0x1EB708 or 0x1EBE15 or 0x1EBE16 or 0x1EBE17;
    }


    private bool NewCofferNearby(float radius)
    {
        var coffer = FindNearestNewMagicPotCoffer(radius);
        if (coffer == null) return false;

        treasureEntityId = unchecked((uint)coffer.GameObjectID);
        return treasureEntityId != 0;
    }

    private OmenGameObject? FindNearestNewMagicPotCoffer(float radius)
    {
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return null;

        OmenGameObject? nearest = null;
        var bestSquared = radius * radius;
        foreach (var obj in DService.Instance().ObjectTable)
        {
            if (!IsMagicPotCoffer(obj)) continue;

            var entityId = unchecked((uint)obj.GameObjectID);
            if (entityId == 0 || preexistingCofferEntityIds.Contains(entityId) ||
                treasureEntityId != 0 && entityId != treasureEntityId)
                continue;

            var deltaX = obj.Position.X - localPlayer.Position.X;
            var deltaZ = obj.Position.Z - localPlayer.Position.Z;
            var distanceSquared = (deltaX * deltaX) + (deltaZ * deltaZ);
            if (distanceSquared >= bestSquared) continue;

            bestSquared = distanceSquared;
            nearest     = obj;
        }

        return nearest;
    }

    private static OmenGameObject? FindMagicPotCoffer(uint entityId)
    {
        if (entityId == 0) return null;

        foreach (var obj in DService.Instance().ObjectTable)
            if (unchecked((uint)obj.GameObjectID) == entityId && IsMagicPotCoffer(obj))
                return obj;

        return null;
    }

    private unsafe bool TryInteractWithMagicPotCoffer()
    {
        var coffer = FindMagicPotCoffer(treasureEntityId) ?? FindNearestNewMagicPotCoffer(PotTreasureOpenRadius);
        if (coffer == null || !coffer.IsTargetable ||
            DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer)
            return false;

        var gameObject  = (GameObject*)coffer.Address;
        var targetSystem = TargetSystem.Instance();
        if (gameObject == null || targetSystem == null || gameObject->EntityId == 0) return false;

        treasureEntityId = gameObject->EntityId;
        VnavStop();


        if (undergroundDangerActive && !treasureInteractionPositionSpoofed)
        {
            const PositionUpdateInstancePacket.MoveType moveType =
                PositionUpdateInstancePacket.MoveType.NormalMove0;
            treasureInteractionOriginalPosition = localPlayer.Position;
            allowUndergroundPositionUpdate = true;
            try
            {
                new PositionUpdateInstancePacket(localPlayer.Rotation, coffer.Position, moveType).Send();
                treasureInteractionPositionSpoofed = true;
            }
            finally
            {
                allowUndergroundPositionUpdate = false;
            }
        }

        targetSystem->Target = gameObject;
        targetSystem->InteractWithObject(gameObject, false);
        DService.Instance().Log.Information(
            $"[OccultPotNotifier] Magic Pot coffer read-bar interaction requested: 0x{treasureEntityId:X8}");
        return true;
    }



    private unsafe void RestoreMagicPotCofferInteractionPosition()
    {
        if (!treasureInteractionPositionSpoofed) return;

        if (DService.Instance().ObjectTable.LocalPlayer is { } localPlayer)
        {
            const PositionUpdateInstancePacket.MoveType moveType =
                PositionUpdateInstancePacket.MoveType.NormalMove0;
            var undergroundPosition = new Vector3(
                treasureInteractionOriginalPosition.X,
                GetUndergroundHeight(treasureInteractionOriginalPosition),
                treasureInteractionOriginalPosition.Z);
            allowUndergroundPositionUpdate = true;
            try
            {
                new PositionUpdateInstancePacket(
                    localPlayer.Rotation,
                    undergroundPosition,
                    moveType).Send();
                DService.Instance().Log.Information(
                    $"[OccultPotNotifier] Magic Pot coffer opened; returned underground to Y={undergroundPosition.Y:F0}");
            }
            finally
            {
                allowUndergroundPositionUpdate = false;
            }
        }

        treasureInteractionPositionSpoofed = false;
        treasureInteractionOriginalPosition = Vector3.Zero;
    }

    private void FinishAutoDig()
    {
        autoDigActive = false;
        autoDigDying  = false;
        awaitingDirection = false;
        treasureRevealed = false;
        RestoreMagicPotCofferInteractionPosition();
        treasureInteractionStarted = false;
        treasureEntityId = 0;
        ResetAutoDigCandidateSearch();
        ResetAutoDigLureState();
        ResetDeathReturn();
        if (cofferHuntActive) StopCofferHunt();
        standbyDeathReturning = false;
        EndBocchiReturnSuppression();
        EndUndergroundDangerMode();
        autoDigStatus = string.Empty;
        autoDigTarget = null;
        VnavStop();
    }

    private void AbortAutoDig()
    {
        autoDigTask?.Abort();
        pendingCofferHuntAutoDigFor = -1;
        pendingPostFateAutoDigTarget = null;
        pendingPostFateAutoDigUntil = 0;
        ResetPostBattleCofferCheck();
        autoDigActive = false;
        autoDigDying  = false;
        awaitingDirection = false;
        treasureRevealed = false;
        RestoreMagicPotCofferInteractionPosition();
        treasureInteractionStarted = false;
        treasureEntityId = 0;
        ResetAutoDigCandidateSearch();
        ResetAutoDigLureState();
        ResetDeathReturn();
        if (cofferHuntActive) StopCofferHunt();
        standbyDeathReturning = false;
        EndBocchiReturnSuppression();
        EndUndergroundDangerMode();
        crossingDC    = false;
        autoDigStatus = string.Empty;
        autoDigTarget = null;
        VnavStop();
    }

    private void ResetDeathReturn()
    {
        deathReturnAt            = 0;
        deathReturnStarted       = false;
        nextDeathReturnAttemptAt = 0;
    }

    #endregion

    private void UpdateLure()
    {
        if (!InOccultMapZone)
        {
            ClearLure();
            return;
        }

        if (!HasLure())
        {
            ClearLure();
            return;
        }

        lureActive = true;

        var playerPos = DService.Instance().ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;


        var activePositions = OccultData.PotPositions(
            GameState.TerritoryType,
            continuationActive,
            lastPotRegion == PotRegion.South);
        cofferPos = OccultData.NearestInSet(activePositions, playerPos);

        if (config.AutoSwitchOnLure && !manualMarkerOverrideWhileLure)
        {
            autoSwitchEngaged = true;


            autoPotSet = continuationActive
                             ? MarkerSet.Reroll
                             : lastPotRegion == PotRegion.South
                                 ? MarkerSet.SouthPot
                                 : MarkerSet.NorthPot;

            RecomputeMarkers();
        }
        else
            DisengageAutoSwitch();
    }

    private void ClearLure()
    {
        lureActive         = false;
        cofferPos          = Vector3.Zero;
        continuationActive = false;
        manualMarkerOverrideWhileLure = false;
        DisengageAutoSwitch();
    }

    private void DisengageAutoSwitch()
    {
        if (!autoSwitchEngaged) return;

        autoSwitchEngaged = false;
        autoPotSet        = MarkerSet.None;
        RecomputeMarkers();
    }

    private void RecomputeMarkers()
    {
        var effective = autoSwitchEngaged
                            ? autoPotSet | (config.DefaultMarkers & ~PotMask)
                            : config.DefaultMarkers;

        if (currentMarkers == effective) return;

        currentMarkers = effective;
        markersDirty   = true;
    }

    private unsafe void UpdateMapMarkers()
    {
        if (!InOccultMapZone) return;

        var agent = AgentMap.Instance();
        if (agent == null) return;

        var selectedMapID = IsOccultMapForTerritory(GameState.TerritoryType, agent->SelectedMapId)
                                ? agent->SelectedMapId
                                : agent->CurrentMapId;
        if (markersDirty ||
            placedMarkers != currentMarkers ||
            placedMapID != selectedMapID ||
            placedMiniMapID != agent->CurrentMapId)
            PlaceMapMarkers();
    }

    private unsafe void PlaceMapMarkers()
    {
        var agent = AgentMap.Instance();
        if (agent == null) return;

        agent->ResetMapMarkers();
        agent->ResetMiniMapMarkers();

        markersDirty    = false;
        placedMarkers   = currentMarkers;
        placedMapID     = IsOccultMapForTerritory(GameState.TerritoryType, agent->SelectedMapId)
                              ? agent->SelectedMapId
                              : agent->CurrentMapId;
        placedMiniMapID = agent->CurrentMapId;

        if (!InOccultMapZone ||
            currentMarkers == MarkerSet.None ||
            !IsOccultMapForTerritory(GameState.TerritoryType, placedMapID))
            return;

        PlaceMapMarkersForMap(agent, placedMapID, addAreaMap: true, addMiniMap: placedMapID == placedMiniMapID);
        if (placedMiniMapID != placedMapID &&
            IsOccultMapForTerritory(GameState.TerritoryType, placedMiniMapID))
            PlaceMapMarkersForMap(agent, placedMiniMapID, addAreaMap: false, addMiniMap: true);
    }

    private unsafe void PlaceMapMarkersForMap(
        AgentMap* agent,
        uint mapID,
        bool addAreaMap,
        bool addMiniMap)
    {
        if (currentMarkers.HasFlag(MarkerSet.BronzeTreasure))
            foreach (var pos in OccultData.TreasurePositions(
                         GameState.TerritoryType,
                         mapID,
                         silver: false))
                AddMarker(agent, pos, IconBronze, addAreaMap, addMiniMap);

        if (currentMarkers.HasFlag(MarkerSet.SilverTreasure))
            foreach (var pos in OccultData.TreasurePositions(
                         GameState.TerritoryType,
                         mapID,
                         silver: true))
                AddMarker(agent, pos, IconSilver, addAreaMap, addMiniMap);

        if (currentMarkers.HasFlag(MarkerSet.NorthPot))
            foreach (var pos in OccultData.NorthPotPositions(GameState.TerritoryType, mapID))
                AddMarker(agent, pos, IconGoldChest, addAreaMap, addMiniMap);

        if (currentMarkers.HasFlag(MarkerSet.SouthPot))
            foreach (var pos in OccultData.SouthPotPositions(GameState.TerritoryType, mapID))
                AddMarker(agent, pos, IconGoldChest, addAreaMap, addMiniMap);

        if (currentMarkers.HasFlag(MarkerSet.Reroll))
            foreach (var pos in OccultData.RerollPositions(GameState.TerritoryType, mapID))
                AddMarker(agent, pos, IconReroll, addAreaMap, addMiniMap);

        if (currentMarkers.HasFlag(MarkerSet.Bunny))
            foreach (var pos in OccultData.BunnyPositions(GameState.TerritoryType, mapID))
                AddMarker(agent, pos, IconCarrot, addAreaMap, addMiniMap);

        if (currentMarkers.HasFlag(MarkerSet.Survey))
            foreach (var pos in OccultData.SurveyPositions(GameState.TerritoryType, mapID))
                AddMarker(agent, pos, IconSurvey, addAreaMap, addMiniMap);
    }

    private static unsafe void AddMarker(
        AgentMap* agent,
        Vector3 pos,
        uint icon,
        bool addAreaMap,
        bool addMiniMap)
    {
        if (addAreaMap)
            agent->AddMapMarker(pos, icon);
        if (addMiniMap)
            agent->AddMiniMapMarker(pos, icon);
    }

    private unsafe void ClearMapMarkers()
    {

        if (placedMarkers != MarkerSet.None)
        {
            var agent = AgentMap.Instance();
            if (agent != null)
            {
                agent->ResetMapMarkers();
                agent->ResetMiniMapMarkers();
            }
        }

        placedMarkers   = MarkerSet.None;
        placedMapID     = 0;
        placedMiniMapID = 0;
        markersDirty    = false;
    }

    private void SetUserMarkers(MarkerSet set)
    {
        config.DefaultMarkers = set;
        config.Save(this);

        if (lureActive)
        {
            manualMarkerOverrideWhileLure = true;
            autoSwitchEngaged             = false;
            autoPotSet                    = MarkerSet.None;
        }

        RecomputeMarkers();
    }

    private void ToggleMarker(MarkerSet flag)
    {
        var baseSet = lureActive ? currentMarkers : config.DefaultMarkers;
        SetUserMarkers(baseSet.HasFlag(flag) ? baseSet & ~flag : baseSet | flag);
    }

    private void OnPostDraw()
    {
        if (!InOccultMapZone) return;

        DrawCofferCircle();
        DrawFastSwitcher();
    }

    private void DrawCofferCircle()
    {
        if (!config.DrawCofferCircle) return;

        if (autoDigActive)
        {
            var playerPos = DService.Instance().ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
            var nearest   = OccultData.NearestInSet(autoDigCofferPositions, playerPos);
            if (nearest != Vector3.Zero)
                DrawCofferCircleAt(nearest);
            return;
        }

        if (!lureActive || cofferPos == Vector3.Zero) return;
        DrawCofferCircleAt(cofferPos);
    }

    private void DrawCofferCircleAt(Vector3 position)
    {
        if (GameViewHelper.WorldToScreen(position, out var screen, out var inView) && inView)
            ImGui.GetForegroundDrawList()
                 .AddCircleFilled(screen, 8f * GlobalUIScale, ImGui.ColorConvertFloat4ToU32(config.CircleColor));
    }

    private unsafe void DrawFastSwitcher()
    {
        if (!config.ShowFastSwitcher) return;

        var agent = AgentMap.Instance();
        if (agent == null) return;

        var displayedMapID = agent->SelectedMapId == 0
                                 ? agent->CurrentMapId
                                 : agent->SelectedMapId;
        if (!IsOccultMapForTerritory(GameState.TerritoryType, displayedMapID)) return;

        var addon = (AtkUnitBase*)RaptureAtkUnitManager.Instance()->GetAddonByName("AreaMap");
        if (addon == null || !addon->IsVisible || addon->RootNode == null) return;

        var scale  = addon->Scale;
        var height = addon->RootNode->Height * scale;
        var posX   = addon->X + (5f * scale);
        var posY   = config.SwitcherBelowMap
                         ? addon->Y + height
                         : addon->Y - (ImGui.GetFrameHeightWithSpacing() + (ImGui.GetStyle().WindowPadding.Y * 2f));

        var switcherFlags = SwitcherFlags;
        if (config.SwitcherMoveable)
            ImGui.SetNextWindowPos(new Vector2(posX, posY), ImGuiCond.FirstUseEver);
        else
        {
            switcherFlags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings;
            ImGui.SetNextWindowPos(new Vector2(posX, posY), ImGuiCond.Always);
        }
        ImGui.SetNextWindowBgAlpha(0.8f);

        if (ImGui.Begin("###OccultPotFastSwitcher", switcherFlags))
        {
            for (var i = 0; i < SwitchButtons.Length; i++)
            {
                var (label, flag) = SwitchButtons[i];
                var on = currentMarkers.HasFlag(flag);

                if (on)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button,        SwitchActiveColor);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, SwitchActiveColor);
                }

                if (ImGui.Button($"{label}###Switch{flag}"))
                    ToggleMarker(flag);

                if (on)
                    ImGui.PopStyleColor(2);

                if (i != SwitchButtons.Length - 1)
                    ImGui.SameLine();
            }
        }

        ImGui.End();
    }

    #endregion

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {TrackerAnonKey}");
        client.DefaultRequestHeaders.Add(
            "Prefer",
            "return=representation, resolution=ignore-duplicates, on_conflict=last_fate");
        client.DefaultRequestHeaders.Add("User-Agent",    "DailyRoutines-OccultPotNotifier");
        return client;
    }

    private void TrySyncOnline(long now)
    {
        if (syncInFlight) return;
        if (!TryBuildContext(out var context)) return;

        var refresh = hasOnlineData ? SyncRefreshSeconds : FastRetrySeconds;
        var due     = syncRequested ||
                      context.Fingerprint != lastFingerprint ||
                      now - lastSyncAt >= refresh;
        if (!due) return;

        lastFingerprint = context.Fingerprint;
        lastSyncAt      = now;
        syncRequested   = false;
        syncInFlight    = true;
        _ = SyncAsync(context, now);
    }

    private bool TryBuildContext(out SyncContext context)
    {
        context = default;

        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return false;
        if (!TryGetCurrentPots(out var north, out var south)) return false;

        var dcID = localPlayer.CurrentWorld.Value.DataCenter.RowId;
        if (dcID == 0) return false;

        var territory = GameState.TerritoryType;
        var fateIDs = territory == OccultTerritory ? SouthHornFateIds : NorthHornFateIds;
        uint fateID    = 0;
        long bestEpoch = 0;
        foreach (var fate in DService.Instance().Fate)
        {
            if (!fateIDs.Contains(fate.FateId)) continue;
            if (fate.StartTimeEpoch <= 0)             continue;
            if (fate.StartTimeEpoch > bestEpoch)
            {
                bestEpoch = fate.StartTimeEpoch;
                fateID    = fate.FateId;
            }
        }

        if (fateID == 0) return false;

        context = new SyncContext
        {
            Fingerprint   = ComputeHash(dcID, fateID, (int)bestEpoch),
            Datacenter    = (ushort)dcID,
            Territory     = territory,
            Server        = GameState.ZoneServerID,
            FateID        = fateID,
            FateTimestamp = (int)bestEpoch,
            NorthFateID   = north.FateID,
            SouthFateID   = south.FateID,
            North         = PotObs.From(north),
            South         = PotObs.From(south)
        };
        return true;
    }

    private static string ComputeHash(uint dcID, uint fateID, int timestamp)
    {
        Span<byte> buffer = stackalloc byte[12];
        BitConverter.TryWriteBytes(buffer[..4],  dcID);
        BitConverter.TryWriteBytes(buffer[4..8], fateID);
        BitConverter.TryWriteBytes(buffer[8..],  timestamp);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buffer, hash);

        var sb = new StringBuilder(64);
        foreach (var b in hash)
            sb.Append(b.ToString("X2"));
        return sb.ToString();
    }

    private async Task SyncAsync(SyncContext context, long now)
    {
        try
        {
            var json = await Client.GetStringAsync(
                $"{TrackerBaseURL}{TrackerTable}?last_fate=eq.{context.Fingerprint}&territory=eq.{context.Territory}");
            var rows = JsonConvert.DeserializeObject<TrackerRow[]>(json);
            if (rows is not { Length: > 0 } && context.Territory == OccultTerritory)
            {
                var legacyJson = await Client.GetStringAsync(
                    $"{TrackerBaseURL}{TrackerTable}?last_fate=eq.{context.Fingerprint}&territory=eq.0");
                rows = JsonConvert.DeserializeObject<TrackerRow[]>(legacyJson);
            }

            if (rows is { Length: > 0 })
            {
                var row    = SelectTracker(rows);
                var shared = ParseSharedPotHistory(row);
                BindTracker(row);
                QueueSharedPotHistory(shared, context);
                await PatchPotHistoryAsync(row, context, now, shared);
            }
            else if (currentTracker is { RowID: > 0 } row && row.Territory == context.Territory)
            {
                var shared = ParseSharedPotHistory(row);
                QueueSharedPotHistory(shared, context);
                await PatchPotHistoryAsync(row, context, now, shared);
            }
            else if (context.HasObservation)
            {
                if (missingFingerprint == context.Fingerprint)
                    missingTrackerChecks++;
                else
                {
                    missingFingerprint   = context.Fingerprint;
                    missingTrackerChecks = 1;
                }

                if (missingTrackerChecks >= MissingTrackerChecksBeforeCreate)
                {
                    var created = await CreateRowAsync(context, now);
                    if (created != null)
                        BindTracker(created);
                }
            }
            else
            {
                missingFingerprint   = string.Empty;
                missingTrackerChecks = 0;
            }
        }
        catch
        {
        }
        finally
        {
            syncInFlight = false;
        }
    }

    private TrackerRow SelectTracker(TrackerRow[] rows)
    {
        if (currentTracker is { RowID: > 0 })
        {
            foreach (var row in rows)
            {
                if (row.RowID == currentTracker.RowID)
                    return row;
            }
        }

        var selected = rows[0];
        foreach (var row in rows)
        {
            if (row.LastUpdate > selected.LastUpdate ||
                (row.LastUpdate == selected.LastUpdate && row.RowID > selected.RowID))
                selected = row;
        }

        return selected;
    }

    private void BindTracker(TrackerRow row)
    {
        currentTracker       = row;
        trackerID            = row.TrackerID ?? string.Empty;
        hasOnlineData        = true;
        missingFingerprint   = string.Empty;
        missingTrackerChecks = 0;
    }

    private static SharedPot[]? ParseSharedPotHistory(TrackerRow row)
    {
        if (string.IsNullOrEmpty(row.PotHistory)) return null;

        try
        {
            return JsonConvert.DeserializeObject<SharedPot[]>(row.PotHistory);
        }
        catch
        {
            return null;
        }
    }

    private void QueueSharedPotHistory(SharedPot[]? shared, SyncContext context)
    {
        if (shared == null) return;

        long ns = -1, nl = -1, ss = -1, sl = -1;
        foreach (var sp in shared)
        {
            if (sp.FateID == context.NorthFateID) { ns = sp.SpawnTime; nl = sp.LastSeen; }
            else if (sp.FateID == context.SouthFateID) { ss = sp.SpawnTime; sl = sp.LastSeen; }
        }

        lock (syncLock)
            pendingSync = (context.Territory, ns, nl, ss, sl);
    }

    private async Task PatchPotHistoryAsync(TrackerRow row, SyncContext context, long now, SharedPot[]? shared)
    {
        if (row.RowID <= 0) return;

        var potChanged         = false;
        var fingerprintChanged = !string.Equals(row.LastFateHash, context.Fingerprint, StringComparison.Ordinal);
        var metadataChanged    = row.Server != context.Server ||
                                 row.Fate != context.FateID ||
                                 row.FateTimestamp != context.FateTimestamp;
        var north              = MergePot(context.NorthFateID, context.North, shared, ref potChanged);
        var south              = MergePot(context.SouthFateID, context.South, shared, ref potChanged);
        if (!potChanged && !fingerprintChanged && !metadataChanged) return;

        var update = new Dictionary<string, object>
        {
            ["last_fate"]      = context.Fingerprint,
            ["territory"]      = context.Territory,
            ["server"]         = context.Server,
            ["fate"]           = context.FateID,
            ["fate_timestamp"] = context.FateTimestamp,
            ["last_update"]    = now
        };

        string? potHistory = null;
        if (potChanged)
        {
            potHistory            = JsonConvert.SerializeObject(new[] { north, south });
            update["pot_history"] = potHistory;
        }

        var body = JsonConvert.SerializeObject(update);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await Client.PatchAsync($"{TrackerBaseURL}{TrackerTable}?id=eq.{row.RowID}", content);
        response.EnsureSuccessStatusCode();

        row.LastFateHash  = context.Fingerprint;
        row.Server        = context.Server;
        row.Fate          = context.FateID;
        row.FateTimestamp = context.FateTimestamp;
        row.LastUpdate    = now;
        if (potHistory != null)
            row.PotHistory = potHistory;
    }

    private async Task<TrackerRow?> CreateRowAsync(SyncContext context, long now)
    {
        var potHistory = JsonConvert.SerializeObject(new[]
        {
            UploadPot.From(context.NorthFateID, context.North),
            UploadPot.From(context.SouthFateID, context.South)
        });

        var body = JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            ["version"]           = TrackerVersion,
            ["territory"]         = context.Territory,
            ["last_fate"]         = context.Fingerprint,
            ["tracker_type"]      = 1,
            ["datacenter"]        = context.Datacenter,
            ["server"]            = context.Server,
            ["fate"]              = context.FateID,
            ["fate_timestamp"]    = context.FateTimestamp,
            ["encounter_history"] = "[]",
            ["fate_history"]      = "[]",
            ["pot_history"]       = potHistory,
            ["last_update"]       = now
        });

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await Client.PostAsync($"{TrackerBaseURL}{TrackerTable}", content);
        response.EnsureSuccessStatusCode();

        var respJson = await response.Content.ReadAsStringAsync();
        var created  = JsonConvert.DeserializeObject<TrackerRow[]>(respJson);
        return created is { Length: > 0 } ? created[0] : null;
    }

    private static UploadPot MergePot(uint fateID, PotObs local, SharedPot[]? shared, ref bool changed)
    {
        long spawn = -1, death = 0, lastSeen = -1;
        if (shared != null)
        {
            foreach (var sp in shared)
            {
                if (sp.FateID != fateID) continue;
                spawn    = sp.SpawnTime;
                death    = sp.DeathTime;
                lastSeen = sp.LastSeen;
                break;
            }
        }

        if (local.Observed && local.LastSeen > lastSeen)
        {
            spawn    = local.Spawn;
            death    = local.Death;
            lastSeen = local.LastSeen;
            changed  = true;
        }

        return new UploadPot { FateID = fateID, SpawnTime = spawn, DeathTime = death, LastSeen = lastSeen };
    }

    private void ApplyPendingSync()
    {
        (uint Territory, long NorthSpawn, long NorthSeen, long SouthSpawn, long SouthSeen)? data;
        lock (syncLock)
        {
            data        = pendingSync;
            pendingSync = null;
        }

        if (data == null || data.Value.Territory != GameState.TerritoryType) return;
        if (!TryGetCurrentPots(out var north, out var south)) return;

        MergeSynced(north, data.Value.NorthSpawn, data.Value.NorthSeen);
        MergeSynced(south, data.Value.SouthSpawn, data.Value.SouthSeen);
    }

    private static void MergeSynced(Pot pot, long spawn, long lastSeen)
    {
        if (pot.Alive) return;
        if (lastSeen > pot.LastSeenAlive) pot.LastSeenAlive = lastSeen;
        if (spawn    > pot.SpawnTime)     pot.SpawnTime     = spawn;
    }

    private struct SyncContext
    {
        public string Fingerprint;
        public ushort Datacenter;
        public uint   Territory;
        public uint   Server;
        public uint   FateID;
        public int    FateTimestamp;
        public ushort NorthFateID;
        public ushort SouthFateID;
        public PotObs North;
        public PotObs South;

        public readonly bool HasObservation =>
            (North.Observed && North.Spawn > 0) || (South.Observed && South.Spawn > 0);
    }

    private readonly struct PotObs
    {
        public bool Observed { get; init; }
        public long Spawn    { get; init; }
        public long Death    { get; init; }
        public long LastSeen { get; init; }

        public static PotObs From(Pot pot) => new()
        {
            Observed = pot.LocallyObserved,
            Spawn    = pot.SpawnTime,
            Death    = pot.DeathTime,
            LastSeen = pot.LastSeenAlive
        };
    }

    private class TrackerRow
    {
        [JsonProperty("id")]
        public long RowID { get; set; }

        [JsonProperty("territory")]
        public uint Territory { get; set; }

        [JsonProperty("tracker_id")]
        public string TrackerID = string.Empty;

        [JsonProperty("last_update")]
        public long LastUpdate;

        [JsonProperty("last_fate")]
        public string LastFateHash = string.Empty;

        [JsonProperty("server")]
        public uint Server;

        [JsonProperty("fate")]
        public uint Fate;

        [JsonProperty("fate_timestamp")]
        public int FateTimestamp;

        [JsonProperty("pot_history")]
        public string PotHistory = string.Empty;
    }

    private struct SharedPot
    {
        [JsonProperty("fate_id")]
        public uint FateID { get; set; }

        [JsonProperty("spawn_time")]
        public long SpawnTime { get; set; }

        [JsonProperty("death_time")]
        public long DeathTime { get; set; }

        [JsonProperty("last_seen")]
        public long LastSeen { get; set; }
    }

    private class UploadPot
    {
        [JsonProperty("fate_id")]
        public uint FateID;

        [JsonProperty("spawn_time")]
        public long SpawnTime;

        [JsonProperty("death_time")]
        public long DeathTime;

        [JsonProperty("last_seen")]
        public long LastSeen;

        [JsonProperty("respawn_times")]
        public long[] RespawnTimes = [];

        public static UploadPot From(uint fateID, PotObs obs) => new()
        {
            FateID    = fateID,
            SpawnTime = obs.Observed ? obs.Spawn    : -1,
            DeathTime = obs.Observed ? obs.Death    : 0,
            LastSeen  = obs.Observed ? obs.LastSeen : -1
        };
    }

    private enum PotDisplayMode
    {
        None,
        DtrBar,
        Overlay
    }

    private enum PotRegion
    {
        North,
        South
    }

    [Flags]
    private enum MarkerSet : uint
    {
        None           = 0,
        BronzeTreasure = 1 << 0,
        SilverTreasure = 1 << 1,
        NorthPot       = 1 << 2,
        SouthPot       = 1 << 3,
        Reroll         = 1 << 4,
        Bunny          = 1 << 5,
        Survey         = 1 << 6
    }

    private sealed class Pot
    {
        public uint    TerritoryID;
        public ushort  FateID;
        public Vector3 World;
        public string  DirName = string.Empty;

        public string  Aetheryte = string.Empty;
        public CrescentAetheryte? AetheryteData;
        public uint    AetherytePlaceNameID;      // Lifestream AethernetTeleportByPlaceNameId
        public Vector3 AetherytePos;

        public bool Alive;
        public long SpawnTime       = -1;
        public long DeathTime       = -1;
        public long LastSeenAlive   = -1;
        public bool LocallyObserved;

        public void Reset()
        {
            Alive           = false;
            SpawnTime       = -1;
            DeathTime       = -1;
            LastSeenAlive   = -1;
            LocallyObserved = false;
        }
    }

    private enum DangerZoneHandlingMode
    {
        Ground,
        Manual,
        Skip,
        Underground
    }

    private readonly record struct CurrencyExchangeSpec(
        string Name,
        uint CurrencyItemID,
        uint EventID,
        int Cost);

    private readonly record struct CurrencyExchangeRequest(
        CurrencyExchangeSpec Spec,
        bool Automatic,
        int Quantity);

    private sealed class Config
    {
        public PotDisplayMode DisplayMode      = PotDisplayMode.DtrBar;
        public bool           UseOnlineTracker = true;

        public bool            SendTTS          = true;
        public bool            SendNotification = true;
        public bool            SendChat;
        public HashSet<string> ChatCommands     = ["/p"];
        public HashSet<string> DisabledChatCommands = [];
        public int             ChatSoundEffect  = 0;
        public int             LeadSeconds      = 300;

        public bool   EnableArchivist          = false;
        public string ArchivistRegex           = "lw罐|史官|礼问罐";
        public int    ArchivistCooldownSeconds = 60;

        public MarkerSet DefaultMarkers   = MarkerSet.None;
        public bool      ShowFastSwitcher = true;
        public bool      SwitcherBelowMap;
        public bool      SwitcherMoveable;
        public bool      AutoSwitchOnLure = true;
        public bool      DrawCofferCircle = true;
        public Vector4   CircleColor      = new(1f, 0.85f, 0.2f, 1f);

        public bool      KeepPotFateEnemyTargeted = true;
        public bool      KeepBmrAiDisabledDuringPotFate;
        public bool      EnableAutoDig;
        public bool      AutoDigSkipDanger    = true;
        public bool      AutoDigUndergroundDanger;
        public bool      AutoDigDiscardDanger;
        public bool      AutoDigDangerTts     = true;
        public bool      AutoDigStopOnDeath;
        public bool      AutoDigReturnOnDeath = true;
        public bool      AutoDigWaitForRescue;
        public bool      AutoDigEmergencyReturn;
        public bool      EnableAutoCrossDC;
        public bool      AutoDeclineInvite;


        [JsonProperty("EnableBocchiHunt")]
        public bool      EnableCofferHunt;
        public bool      CofferHuntOuterLoop = true;

        public bool      EnableAutoRevive;
        public bool      AutoRevivePartyOnly = true;
        public bool      EnableAutoCurrencyExchange;

        public static Config Load(OccultPotFeature _)
        {
            var payload = Plugin.Config.OccultPotAssistantConfig;
            if (!string.IsNullOrWhiteSpace(payload))
            {
                try
                {
                    return JsonConvert.DeserializeObject<Config>(payload) ?? new Config();
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning(ex, "Failed to load the Magic Pot Assistant configuration.");
                }
            }

            return new Config
            {
                EnableAutoRevive = Plugin.Config.Features.OccultPotAutoRevive,
                AutoRevivePartyOnly = Plugin.Config.OccultPot.AutoRevivePartyOnly,
            };
        }

        public void Save(OccultPotFeature _)
        {
            Plugin.Config.OccultPotAssistantConfig = JsonConvert.SerializeObject(this);
            Plugin.Config.Features.OccultPotAutoRevive = EnableAutoRevive;
            Plugin.Config.OccultPot.AutoRevivePartyOnly = AutoRevivePartyOnly;
            Plugin.Config.Save();
        }
    }


    // Static field positions verified against EurekaTrackerAutoPopper.
    private static class OccultData
    {
        private const float CofferRange = 80f;


        public static readonly (Vector3 Pos, uint Tag)[] Treasures =
        [
            (new(-283.98572f, 115.983765f, 377.03516f), 1597u),
            (new(277.7904f, 103.77649f, 241.90125f), 1596u),
            (new(-401.66327f, 85.03845f, 332.5398f), 1596u),
            (new(-372.67108f, 74.99805f, 527.4281f), 1596u),
            (new(609.61304f, 107.98804f, 117.2655f), 1596u),
            (new(256.1532f, 73.16687f, 492.3628f), 1596u),
            (new(870.6644f, 95.68933f, -388.35742f), 1596u),
            (new(-825.1621f, 2.9754639f, -832.2728f), 1597u),
            (new(697.322f, 69.99304f, 597.9247f), 1597u),
            (new(666.5292f, 79.11792f, -480.36932f), 1596u),
            (new(-444.11383f, 90.684326f, 26.230225f), 1596u),
            (new(642.96936f, 69.99304f, 407.79736f), 1596u),
            (new(-645.68555f, 202.99072f, 710.17017f), 1597u),
            (new(779.0187f, 96.08594f, -256.2448f), 1596u),
            (new(-118.97461f, 4.989685f, -708.4612f), 1596u),
            (new(726.28357f, 108.140625f, -67.91791f), 1596u),
            (new(596.45984f, 70.29822f, 622.76636f), 1596u),
            (new(294.8805f, 56.076904f, 640.2228f), 1596u),
            (new(-491.02008f, 2.9754639f, -529.59485f), 1596u),
            (new(770.7484f, 107.98804f, -143.5722f), 1597u),
            (new(471.18323f, 70.29822f, 530.022f), 1596u),
            (new(788.8761f, 120.378296f, 109.391846f), 1596u),
            (new(-648.0049f, 74.99805f, 403.95203f), 1596u),
            (new(55.283447f, 111.31445f, -289.0822f), 1596u),
            (new(-487.11377f, 98.527466f, -205.46277f), 1596u),
            (new(354.1161f, 95.65869f, -288.92963f), 1596u),
            (new(35.721313f, 65.11023f, 648.9509f), 1596u),
            (new(-197.19238f, 74.906494f, 618.3412f), 1596u),
            (new(-729.427f, 4.989685f, -724.81885f), 1596u),
            (new(433.70715f, 70.29822f, 683.52783f), 1596u),
            (new(517.7539f, 67.88733f, 236.1333f), 1597u),
            (new(-756.8322f, 76.55444f, 97.3678f), 1596u),
            (new(475.73047f, 95.994385f, -87.08331f), 1596u),
            (new(-661.7075f, 2.9754639f, -579.4919f), 1596u),
            (new(-884.123f, 3.7994385f, -682.0325f), 1596u),
            (new(-343.16016f, 52.32312f, -382.1317f), 1596u),
            (new(-550.13354f, 106.98096f, 627.74084f), 1596u),
            (new(-158.64807f, 98.61902f, -132.73828f), 1596u),
            (new(-729.9153f, 116.53308f, -79.05707f), 1596u),
            (new(142.1073f, 16.403442f, -574.0597f), 1596u),
            (new(-451.6823f, 2.9754639f, -775.5703f), 1596u),
            (new(-225.02484f, 74.99805f, 804.9896f), 1596u),
            (new(-856.9619f, 68.833374f, -93.15637f), 1596u),
            (new(-682.7955f, 135.60681f, -195.26971f), 1597u),
            (new(835.08044f, 69.99304f, 699.09204f), 1596u),
            (new(-140.45929f, 22.354431f, -414.2672f), 1596u),
            (new(140.97803f, 55.98523f, 770.99243f), 1596u),
            (new(8.987488f, 103.196655f, 426.96265f), 1596u),
            (new(386.92297f, 96.787964f, -451.37714f), 1596u),
            (new(-676.41724f, 170.9773f, 640.37524f), 1596u),
            (new(245.59387f, 109.11719f, -18.173523f), 1596u),
            (new(826.688f, 121.99585f, 434.9889f), 1596u),
            (new(-713.80176f, 62.05847f, 192.61462f), 1596u),
            (new(-25.68097f, 102.22009f, 150.16394f), 1596u),
            (new(-798.24524f, 105.57703f, -310.5669f), 1597u),
            (new(490.40967f, 62.45508f, -590.56995f), 1596u),
            (new(-256.88562f, 120.98877f, 125.078125f), 1596u),
            (new(-585.2903f, 4.989685f, -864.8356f), 1596u),
            (new(-716.1517f, 170.9773f, 794.4304f), 1596u),
            (new(-767.4525f, 115.61755f, -235.00421f), 1596u),
            (new(-600.27466f, 138.99438f, 802.6398f), 1596u),
            (new(617.08997f, 66.300415f, -703.8834f), 1596u),
            (new(-729.5491f, 106.98096f, 561.1504f), 1596u),
            (new(869.29126f, 109.97168f, 581.2008f), 1596u),
            (new(-394.88824f, 106.73682f, 175.43298f), 1596u),
            (new(-784.7562f, 138.99438f, 699.7634f), 1596u),
            (new(381.73486f, 22.171326f, -743.64844f), 1596u),
            (new(-680.5371f, 104.844604f, -354.78754f), 1596u)
        ];

        public static readonly Vector3[] NorthPots =
        [
            new(571.5841f, 51.451305f, -813.1642f),
            new(662.4388f, 120f, 161.1339f),
            new(606.4641f, 108.07402f, 184.8517f),
            new(-312.2778f, 103.19944f, -35.25348f),
            new(587.7039f, 78.8956f, -545.8168f),
            new(891.2597f, 120f, -20.672f),
            new(878.1131f, 108.28959f, -91.1057f),
            new(803.6609f, 95.99998f, -354.1809f),
            new(341.4413f, 95.99999f, 194.7507f),
            new(570.2421f, 64.66201f, 272.1734f),
            new(-216.372f, 5.4469404f, -510.1361f),
            new(684.4223f, 96.10129f, -165.4811f),
            new(-188.1745f, 2.999999f, -717.2005f),
            new(-476.3011f, 101.44228f, -86.69939f),
            new(80.19762f, 101.27949f, 391.2263f),
            new(-534.6993f, 2.999998f, -651.6244f),
            new(-165.2374f, 95.33837f, 437.4505f),
            new(330.8659f, 6.7168036f, -654.5339f),
            new(-333.3444f, 2.9999998f, -861.1722f),
            new(-313.2906f, 108.10962f, 70.76207f),
            new(-459.1735f, 93.57443f, 5.054043f),
            new(-54.69518f, 99.40573f, 405.0261f),
            new(-382.4396f, 109.30187f, -378.3482f),
            new(263.2559f, 100.38499f, 326.6834f),
            new(224.7233f, 68.7328f, 518.668f),
            new(19.73968f, 26.045855f, -420.977f),
            new(705.2716f, 68.143616f, 358.6714f),
            new(-660.5336f, 98f, -216.7666f),
            new(-324.2736f, 121f, 203.2017f),
            new(-386.5904f, -0.13994062f, -461.0976f)
        ];

        public static readonly Vector3[] SouthPots =
        [
            new(-195.4419f, 110.15342f, -287.8911f),
            new(74.73397f, 110.494316f, -394.1289f),
            new(-386.437f, 98.60658f, -221.7847f),
            new(-554.6146f, 99.01769f, -309.1231f),
            new(107.0611f, 105.699875f, 146.7059f),
            new(825.9521f, 70f, 772.4054f),
            new(-836.7586f, 106.999985f, 597.2944f),
            new(67.45271f, 69.477974f, 745.8658f),
            new(69.70596f, 111.56108f, -239.064f),
            new(301.8741f, 103.784424f, 70.59854f),
            new(-38.97946f, 102.073296f, -175.4589f),
            new(-60.72729f, 69.687035f, 828.4997f),
            new(17.60418f, 65.93209f, 674.6207f),
            new(393.2685f, 57.545956f, 844.6924f),
            new(393.0191f, 104f, -124.1651f),
            new(-798.7886f, 84.22545f, -4.822005f),
            new(440.8355f, 70.3f, 876.4097f),
            new(-734.1434f, 170.99998f, 683.7238f),
            new(423.3505f, 70.3f, 578.9013f),
            new(200.1241f, 56f, 624.2285f),
            new(-603.3457f, 139f, 858.6771f),
            new(-829.598f, 62.66814f, 66.82948f),
            new(-645.3027f, 135.69208f, -73.54771f),
            new(-836.1612f, 107f, 770.2822f),
            new(-676.6202f, 128.57442f, 1.531581f),
            new(-713.6796f, 203f, 710.08f),
            new(781.2514f, 70f, 560.0701f),
            new(-746.1318f, 172.00023f, 828.8809f),
            new(-730.5441f, 107.694275f, -371.4776f),
            new(-810.8279f, 114.053925f, -226.8324f)
        ];

        public static readonly Vector3[] Rerolls =
        [
            new(-676.4631f, 5f, -769.7955f),
            new(-823.9183f, 140.00032f, 677.6934f),
            new(-886.4718f, 107f, 712.4964f),
            new(-625.7809f, 171f, 810.8691f),
            new(-813.9943f, 5f, -663.3634f),
            new(-842.8967f, 75.76903f, -125.0559f),
            new(-680.0345f, 201f, 739.9117f),
            new(-793.0552f, 5f, -777.3126f),
            new(-708.6777f, 171f, 669.5714f),
            new(-718.0424f, 5f, -633.8791f),
            new(-868.8489f, 67.5054f, -59.44909f),
            new(-803.5182f, 3f, -602.7497f),
            new(-732.2048f, 139f, 828.8491f),
            new(-659.1158f, 12.198493f, -508.7968f),
            new(-785.997f, 162.39513f, 790.5948f),
            new(-840.8771f, 107.26465f, -250.273f),
            new(-708.687f, 141.16982f, -139.3283f),
            new(-796.66f, 114.15647f, -228.9318f),
            new(-776.6315f, 5f, -486.978f),
            new(-758.8058f, 127.66496f, -183.164f)
        ];

        public static readonly Vector3[] Bunnies =
        [
            new(283.6546f, 55.999996f, 587.3107f),
            new(-439.0463f, 115.82392f, 184.4665f),
            new(477.4074f, 96.10128f, 138.6543f),
            new(-743.601f, 96.39003f, 84.43998f),
            new(-575.6361f, 162.39511f, 668.7043f),
            new(865.0009f, 95.99958f, -214.6744f),
            new(248.9159f, 55.999996f, 791.1138f),
            new(-490.3187f, 3f, -741.0153f),
            new(720.4133f, 120f, 271.05f),
            new(466.2025f, 70.3f, 563.2519f),
            new(-701.8768f, 201f, 718.7181f),
            new(-273.0878f, 75f, 850.0336f),
            new(650.2321f, 108f, 141.1927f),
            new(827.2007f, 108f, -156.4444f),
            new(845.5334f, 98f, 777.4331f),
            new(772.3591f, 70.3f, 531.1259f),
            new(-84.73673f, 2.999999f, -796.0166f),
            new(-843.8602f, 83.657074f, -36.78173f),
            new(-727.8528f, 81.47683f, 328.9311f),
            new(-400.528f, 2.999999f, -518.3032f),
            new(-806.5123f, 107f, 887.6146f),
            new(-174.0473f, 121.00001f, 107.6488f),
            new(-771.6308f, 5f, -694.0016f),
            new(-710.266f, 3f, -451.5128f),
            new(-554.0244f, 110.698654f, -365.897f)
        ];

        // North Horn data verified against EurekaTrackerAutoPopper (2026-08-08, 840f246; point sets unchanged since e8306ff).
        private static readonly (Vector3 Pos, uint Tag, uint MapID)[] NorthHornTreasures =
        [
            (new(383.3138f, 33f, -175.6476f), 1597u, OccultNorthMapID),
            (new(-2.305847f, 66.69136f, -814.9053f), 1597u, OccultNorthMapID),
            (new(-22.66858f, 42.08691f, 628.9946f), 1597u, OccultNorthMapID),
            (new(-633.6964f, 82.71846f, -146.0046f), 1597u, OccultNorthMapID),
            (new(634.7919f, 60.51484f, -831.787f), 1597u, OccultNorthMapID),
            (new(-645.4403f, 160.0992f, 967.9435f), 1597u, OccultNorthMapID),
            (new(-815.8082f, -21.83485f, -699.3701f), 1597u, OccultNorthMapID),
            (new(223.6532f, -161.8637f, -30.64362f), 1597u, OccultNorthSubMapID),
            (new(676.9965f, 190.9779f, 957.4468f), 1596u, OccultNorthMapID),
            (new(812.0001f, 192f, 669f), 1596u, OccultNorthMapID),
            (new(673.7398f, 161.1653f, 729.666f), 1596u, OccultNorthMapID),
            (new(758.147f, 130f, 506.8132f), 1596u, OccultNorthMapID),
            (new(246.2266f, 66.54174f, 676.6658f), 1596u, OccultNorthMapID),
            (new(719.3481f, 69.65454f, 268.3043f), 1596u, OccultNorthMapID),
            (new(449.408f, 0.1465552f, 105.2345f), 1596u, OccultNorthMapID),
            (new(649.5436f, 46.24511f, -157.7742f), 1596u, OccultNorthMapID),
            (new(478.4506f, 12.4224f, -202.9711f), 1596u, OccultNorthMapID),
            (new(254.7441f, 36.93214f, -605f), 1596u, OccultNorthMapID),
            (new(-26f, 0.2318999f, -437.6877f), 1596u, OccultNorthMapID),
            (new(-265.7608f, 30.17087f, -439.5194f), 1596u, OccultNorthMapID),
            (new(-232.4192f, 53.23654f, -719.9717f), 1596u, OccultNorthMapID),
            (new(147.8688f, 61f, -868.7524f), 1596u, OccultNorthMapID),
            (new(658.8088f, 66.1263f, -364.6757f), 1596u, OccultNorthMapID),
            (new(950.2007f, 74.00013f, -358.9755f), 1596u, OccultNorthMapID),
            (new(658.7231f, 60.52044f, -552.306f), 1596u, OccultNorthMapID),
            (new(389.5362f, 60.68167f, -733.0182f), 1596u, OccultNorthMapID),
            (new(77.06985f, 21.19984f, 536.2695f), 1596u, OccultNorthMapID),
            (new(-12.09888f, 66.65052f, 773.8625f), 1596u, OccultNorthMapID),
            (new(-278.0559f, 47.78407f, 567.9728f), 1596u, OccultNorthMapID),
            (new(-436.4424f, 0.2028036f, 166.2191f), 1596u, OccultNorthMapID),
            (new(-256.9473f, 100.6667f, 812.1967f), 1596u, OccultNorthMapID),
            (new(-504.0914f, 85.75282f, 758.3212f), 1596u, OccultNorthMapID),
            (new(-612.2136f, 66.98989f, 578.548f), 1596u, OccultNorthMapID),
            (new(-775.8944f, 70.7192f, 377.1531f), 1596u, OccultNorthMapID),
            (new(-631.7785f, 78.25452f, 240f), 1596u, OccultNorthMapID),
            (new(-923.1418f, 113.2651f, 197.9475f), 1596u, OccultNorthMapID),
            (new(-590.2075f, 87.97915f, -7f), 1596u, OccultNorthMapID),
            (new(-878.9666f, 13.13452f, -314.2021f), 1596u, OccultNorthMapID),
            (new(-581.4894f, 40.91439f, -257.4107f), 1596u, OccultNorthMapID),
            (new(-254.1409f, 1.820912f, -266.3119f), 1596u, OccultNorthMapID),
            (new(-707.3763f, 41.58638f, -396.9889f), 1596u, OccultNorthMapID),
            (new(-697.2709f, 34.89849f, -565.0217f), 1596u, OccultNorthMapID),
            (new(-439.5511f, 43.04438f, -558.4492f), 1596u, OccultNorthMapID),
            (new(-525.7809f, 46.85732f, -783.4683f), 1596u, OccultNorthMapID),
            (new(85.59845f, 3.302996f, -281.1396f), 1596u, OccultNorthMapID),
            (new(43.7818f, 2.454146f, -108.1916f), 1596u, OccultNorthMapID),
            (new(-168.2038f, 3.379924f, -153.4577f), 1596u, OccultNorthMapID),
            (new(-162.0424f, 3.589863f, 98.44962f), 1596u, OccultNorthMapID),
            (new(633.1317f, 60.64236f, -910.2271f), 1596u, OccultNorthMapID),
            (new(639.049f, 60.62531f, -698.7261f), 1596u, OccultNorthMapID),
            (new(815.4435f, 60.5542f, -657.3135f), 1596u, OccultNorthMapID),
            (new(865.4569f, 70.21528f, -874.0874f), 1596u, OccultNorthMapID),
            (new(-592f, 160.1012f, 767.6685f), 1596u, OccultNorthMapID),
            (new(-699.8373f, 160f, 926.3793f), 1596u, OccultNorthMapID),
            (new(-857.7925f, 159.85f, 772.2366f), 1596u, OccultNorthMapID),
            (new(-800.3965f, 157.8f, 633.3867f), 1596u, OccultNorthMapID),
            (new(-857.5991f, -12.23519f, -609.8169f), 1596u, OccultNorthMapID),
            (new(-928.626f, -11.22762f, -744.9562f), 1596u, OccultNorthMapID),
            (new(-736.0236f, 21.03466f, -881.4858f), 1596u, OccultNorthMapID),
            (new(-416.7736f, 45.93657f, -945.4311f), 1596u, OccultNorthMapID),
            (new(-144.7256f, -129.7955f, 304.9379f), 1596u, OccultNorthSubMapID),
            (new(41.2326f, -140.7708f, 168.5024f), 1596u, OccultNorthSubMapID),
            (new(161f, -151.7595f, 16.00002f), 1596u, OccultNorthSubMapID),
            (new(313.9192f, -139.5295f, 180.0712f), 1596u, OccultNorthSubMapID),
            (new(447.8859f, 62.90584f, 463.3448f), 1596u, OccultNorthMapID),
            (new(279.0932f, 143f, -356.1478f), 1596u, OccultNorthMapID),
            (new(-287.7408f, -92f, 125.6662f), 1596u, OccultNorthSubMapID),
            (new(222.9122f, 90.40005f, 913.6289f), 1596u, OccultNorthMapID)
        ];

        private static readonly Vector3[] NorthHornNorthPots =
        [
            new(714.698f, 69.24771f, 262.6901f),
            new(-455.989f, 39.688915f, -365.5418f),
            new(593f, 39.622505f, 34f),
            new(-251.781f, 65.949005f, -864.3828f),
            new(151.9998f, 61.106945f, -842.0175f),
            new(385f, 33f, -177f),
            new(452.6f, 57.10005f, -310.3f),
            new(-223.8233f, 10.891144f, -353.9438f),
            new(1.768392f, 71.555756f, -872.2798f),
            new(-252.1626f, 66.55432f, -879.5855f),
            new(440.298f, 60.615795f, -926.5872f),
            new(782.4979f, 70.34123f, -56.4099f),
            new(-190f, 61.75258f, -763f),
            new(939.2178f, 80.269966f, -273.1175f),
            new(912.2978f, 61.18964f, -461.5099f),
            new(889.2178f, 53.999996f, 155.9825f),
            new(32.4f, 56.835186f, -777.3f),
            new(-530f, 67.77658f, -58f),
            new(948.5978f, 63.594563f, -567.0099f),
            new(830.0979f, 77.75924f, -148.9099f),
            new(928.8978f, 74.0003f, -332.8099f),
            new(-498.7f, 11.051006f, 128.9f),
            new(546.56f, 36.120197f, 143.3104f),
            new(927.0178f, 54f, -155.2175f),
            new(929.4178f, 54f, -1.817501f),
            new(-86f, 60.596237f, -737f),
            new(321.198f, 59.85f, -889.8872f),
            new(-536.1014f, 87.01824f, 149.8447f),
            new(-596f, 41.869873f, -285f),
            new(810.8979f, 78.39757f, -278.8099f)
        ];

        private static readonly Vector3[] NorthHornSouthPots =
        [
            new(47.6f, 3.8843424f, -218.3f),
            new(-172.6f, 6.0019975f, 103.2f),
            new(-330f, 42f, -628f),
            new(-184.5137f, 71.1816f, 667.8036f),
            new(-747.4032f, 28.970308f, -492.1095f),
            new(-512f, 41.999996f, -389f),
            new(52f, 25.316154f, 552f),
            new(-127f, 71.47446f, 808.4f),
            new(28.10088f, 3.9999995f, -16.69861f),
            new(-109.5452f, 8.047999f, -210.1855f),
            new(-975.4507f, 17.57744f, -526.2878f),
            new(-834f, 18.913685f, -587.4f),
            new(190.3622f, 3.880325f, -204.7095f),
            new(-259.6f, 3.6823246f, 56.9f),
            new(210f, 98.400055f, 916f),
            new(-628.4385f, 49.07533f, -449.5009f),
            new(-88.43135f, 2.400001f, 4.891054f),
            new(-15.89468f, 4.0000005f, -20.29277f),
            new(-586.3f, 47.81013f, -715.2f),
            new(237.9156f, -0.29999995f, 309.4334f),
            new(194.2296f, -0.3000001f, 352.9844f),
            new(0.9425046f, 41.80327f, 623.2599f),
            new(-339.8588f, 85.47024f, 861.5197f),
            new(71.10001f, 81.074875f, 942.3f),
            new(11.98766f, 68.15505f, 795.707f),
            new(93.4f, 3.7155468f, -114.3f),
            new(-113.4943f, 5.0879984f, -74.15943f),
            new(-853.493f, 58f, -323.8983f),
            new(-960f, 48f, -425.8f),
            new(-269.6122f, 107.93719f, 875.6997f)
        ];

        private static readonly Vector3[] NorthHornRerolls =
        [
            new(782.8808f, 60.390976f, -611.7695f),
            new(925.6533f, 70.21527f, -906.2195f),
            new(909f, 97.05797f, -961.8f),
            new(-661f, 160f, 937f),
            new(-527f, 160.1012f, 834f),
            new(-631.9453f, 160f, 808.8979f),
            new(-809f, 6.3495464f, -879f),
            new(671.2f, 60.99496f, -550.1f),
            new(701f, 59.999992f, -945f),
            new(-623f, 160f, 883f),
            new(-585f, 160f, 842f),
            new(-656.9f, 23.036425f, -799.3f),
            new(-839.9977f, 160f, 740f),
            new(-487.8f, 48.000015f, -953.2f),
            new(-603f, 32f, -869f),
            new(-637.2283f, 32f, -950.4841f),
            new(-866f, -41.01304f, -775f),
            new(626.3f, 61.119125f, -844.9f),
            new(943.4631f, 70.21487f, -879.5159f),
            new(-449.6f, 45.6567f, -967.0001f)
        ];

        private static readonly Vector3[] NorthHornBunnies =
        [
            new(-857.4f, 71.45287f, 379.6f),
            new(7.60699f, 4.3169565f, -35.67316f),
            new(287.2872f, 142.99992f, -366.9024f),
            new(-608.8f, 59.286507f, 373.9f),
            new(-254f, 54.388798f, -739f),
            new(-560.9f, 50.74249f, -447f),
            new(-500f, 48.000004f, -867.6f),
            new(226f, 90.400055f, 904f),
            new(-258.7481f, 3.588304f, 53.59217f),
            new(-604f, 160.05638f, 939.1f),
            new(756.858f, 68.92707f, -79.33746f),
            new(-814.6948f, 5.6813054f, -561.0853f),
            new(-129.7795f, 8.029996f, -171.18f),
            new(-847.9f, 114f, 196.6f),
            new(-808f, 6.3495464f, -879f),
            new(960f, 97.05797f, -879f),
            new(625.8f, 61.06923f, -846.3f),
            new(-956.1f, 157.8f, 720.2f),
            new(-581f, 160f, 791f),
            new(108f, 22.332209f, -556f),
            new(-35f, 72.89336f, -860f),
            new(882.1526f, 53.999996f, 115.9092f),
            new(923f, 80.26997f, -277f),
            new(853.9f, 70.20017f, -343.3f),
            new(-124f, 76.75548f, 777f)
        ];

        // Survey point data verified against EurekaTrackerAutoPopper (2026-08-08, 840f246; point sets unchanged since cd0dcf5).
        private static readonly (Vector3 Pos, uint MapID)[] SouthHornSurveys =
        [
            (new(857f, 73.17932f, -692f), OccultMapID),
            (new(491.9049f, 95.99999f, -222.5223f), OccultMapID),
            (new(-61.66174f, 30.94501f, -459.6475f), OccultMapID),
            (new(-564.0192f, 121.285f, 53.97119f), OccultMapID),
            (new(140.3372f, 66.89866f, 570.2448f), OccultMapID),
            (new(-291.7067f, 95.64343f, 404.8981f), OccultMapID),
            (new(89.89087f, 124.9975f, 3.982544f), OccultMapID),
            (new(-145.0065f, 73.98034f, 619.9891f), OccultMapID),
            (new(-635.0652f, 203f, 717.9521f), OccultMapID),
            (new(-885.008f, 3.8f, -782.0096f), OccultMapID),
            (new(757.9918f, 70.3f, 614.0687f), OccultMapID),
            (new(726.0089f, 120f, 59.86108f), OccultMapID)
        ];

        private static readonly (Vector3 Pos, uint MapID)[] NorthHornSurveys =
        [
            (new(880f, 259.9927f, 830f), OccultNorthMapID),
            (new(756f, 130f, 511f), OccultNorthMapID),
            (new(311f, -0.3000001f, 242f), OccultNorthMapID),
            (new(917f, 54f, 57f), OccultNorthMapID),
            (new(278f, 143f, -360.5961f), OccultNorthMapID),
            (new(940f, 97.05797f, -898f), OccultNorthMapID),
            (new(-196f, 70f, -813f), OccultNorthMapID),
            (new(-514f, 159.3551f, 869f), OccultNorthMapID),
            (new(-837.1359f, 160.031f, 743.2947f), OccultNorthMapID),
            (new(-906f, 114f, 150f), OccultNorthMapID),
            (new(-700.4601f, 41.93895f, -370.4427f), OccultNorthMapID),
            (new(-876f, -48.85687f, -903f), OccultNorthMapID),
            (new(-8f, 2.259441f, -88f), OccultNorthMapID),
            (new(62f, -141.3853f, 124f), OccultNorthSubMapID)
        ];

        public static IEnumerable<Vector3> TreasurePositions(
            uint territory,
            uint mapID,
            bool silver)
        {
            var wantedTag = silver ? 1597u : 1596u;
            if (territory == OccultTerritory)
            {
                foreach (var (pos, tag) in Treasures)
                    if (tag == wantedTag)
                        yield return pos;
                yield break;
            }

            foreach (var (pos, tag, pointMapID) in NorthHornTreasures)
                if (tag == wantedTag && pointMapID == mapID)
                    yield return pos;
        }

        public static IEnumerable<Vector3> NorthPotPositions(uint territory, uint mapID) =>
            PositionsForMap(territory, mapID, NorthPots, NorthHornNorthPots);

        public static IEnumerable<Vector3> SouthPotPositions(uint territory, uint mapID) =>
            PositionsForMap(territory, mapID, SouthPots, NorthHornSouthPots);

        public static IEnumerable<Vector3> RerollPositions(uint territory, uint mapID) =>
            PositionsForMap(territory, mapID, Rerolls, NorthHornRerolls);

        public static IEnumerable<Vector3> BunnyPositions(uint territory, uint mapID) =>
            PositionsForMap(territory, mapID, Bunnies, NorthHornBunnies);

        public static IEnumerable<Vector3> SurveyPositions(uint territory, uint mapID)
        {
            var positions = territory switch
            {
                OccultTerritory      => SouthHornSurveys,
                OccultNorthTerritory => NorthHornSurveys,
                _                    => Array.Empty<(Vector3 Pos, uint MapID)>()
            };

            foreach (var (position, pointMapID) in positions)
                if (pointMapID == mapID)
                    yield return position;
        }

        private static IEnumerable<Vector3> PositionsForMap(
            uint territory,
            uint mapID,
            Vector3[] southHornPositions,
            Vector3[] northHornPositions)
        {
            var positions = territory switch
            {
                OccultTerritory when mapID == OccultMapID => southHornPositions,
                OccultNorthTerritory when mapID == OccultNorthMapID => northHornPositions,
                _ => Array.Empty<Vector3>()
            };

            foreach (var position in positions)
                yield return position;
        }

        public static Vector3[] PotPositions(uint territory, bool continuation, bool south)
        {
            if (territory == OccultNorthTerritory)
                return continuation
                           ? NorthHornRerolls
                           : south
                               ? NorthHornSouthPots
                               : NorthHornNorthPots;
            if (continuation)
                return Rerolls;
            return south ? SouthPots : NorthPots;
        }




        public static Vector3[] RefinePositionsByDirection(Vector3[] positions, Vector3 from, string direction)
        {
            if (positions.Length == 0) return positions;

            var directionSector = DirectionSector(direction);
            var matches = new List<Vector3>();
            if (directionSector >= 0)
            {
                foreach (var pos in positions)
                {
                    var delta = new Vector2(pos.X - from.X, pos.Z - from.Z);
                    if (delta.LengthSquared() < 1f) continue;
                    if (DirectionSector(delta) == directionSector)
                        matches.Add(pos);
                }
            }

            var result = directionSector >= 0 ? matches.ToArray() : (Vector3[])positions.Clone();
            Array.Sort(result, (a, b) =>
            {
                var aDelta = new Vector2(a.X - from.X, a.Z - from.Z);
                var bDelta = new Vector2(b.X - from.X, b.Z - from.Z);
                return aDelta.LengthSquared().CompareTo(bDelta.LengthSquared());
            });
            return result;
        }



        private static int DirectionSector(string direction) => direction switch
        {
            "正北" => 0,
            "东北" => 1,
            "正东" => 2,
            "东南" => 3,
            "正南" => 4,
            "西南" => 5,
            "正西" => 6,
            "西北" => 7,
            _      => -1
        };

        private static int DirectionSector(Vector2 delta)
        {
            // Divide the plane into strict 45-degree sectors centered on eight directions.
            const float sectorSize = MathF.PI / 4f;
            var angle  = MathF.Atan2(delta.X, -delta.Y);
            var sector = (int)MathF.Floor((angle + sectorSize / 2f) / sectorSize);
            return (sector + 8) % 8;
        }


        public static Vector3 NearestInSet(Vector3[] positions, Vector3 player)
        {
            var bestDist = CofferRange;
            var bestPos  = Vector3.Zero;

            foreach (var pos in positions)
            {
                var dist = Vector3.Distance(player, pos);
                if (dist < bestDist) { bestDist = dist; bestPos = pos; }
            }

            return bestPos;
        }

    }


    // Read the installed Bossmod Reborn runtime state; use its public command to change it.
    private static class BmrAi
    {
        public static bool TryGetEnabled(out bool enabled)
        {
            enabled = false;

            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name != "BossModReborn") continue;

                    var managerType = assembly.GetType("BossMod.AI.AIManager");
                    const BindingFlags staticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                    var manager = managerType?.GetField("Instance", staticFlags)?.GetValue(null);
                    if (manager == null) return false;

                    const BindingFlags instanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    enabled = manager.GetType().GetField("Beh", instanceFlags)?.GetValue(manager) != null;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }
    }

    // Read BOCCHI state only; BOCCHI remains the sole owner of combat logic.
    private static class BocchiAutomator
    {
        public static bool TryEmergencyStop()
        {
            try
            {
                var bocchi = ResolvePlugin();
                if (bocchi == null) return false;

                var automatorModule = ResolveModule(bocchi, "BOCCHI.Modules.Automator.AutomatorModule");
                if (automatorModule == null) return false;

                const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var emergencyStop = automatorModule.GetType().GetMethod(
                    "DisableIllegalMode",
                    bf,
                    null,
                    Type.EmptyTypes,
                    null);
                if (emergencyStop == null) return false;

                emergencyStop.Invoke(automatorModule, null);
                return true;
            }
            catch (Exception ex)
            {
                DService.Instance().Log.Warning(
                    $"[OccultPotNotifier] BOCCHI emergency stop failed: {ex.GetType().Name}");
                return false;
            }
        }

        public static bool IsTravellingToFateOrCriticalEncounter()
        {
            try
            {
                var bocchi = ResolvePlugin();
                if (bocchi == null) return false;

                var currentAutomator = ResolveService(bocchi, "BOCCHI.Automator.Services.IAutomator");
                var currentState = currentAutomator == null
                                       ? null
                                       : GetMember(currentAutomator, "CurrentState")?.ToString();
                if (currentState is "Pathfinding" or "WaitingForCriticalEncounter" or
                                    "WaitingToStartCriticalEncounter" or "InFate" or "InCriticalEncounter")
                    return true;

                var automatorModule = ResolveModule(bocchi, "BOCCHI.Modules.Automator.AutomatorModule");
                var automator = automatorModule == null
                                    ? null
                                    : GetMember(automatorModule, "automator") ?? GetMember(automatorModule, "Automator");
                var activity = automator == null ? null : GetMember(automator, "Activity");
                var activityType = activity?.GetType().FullName;

                return activityType is "BOCCHI.Modules.Automator.FateActivity" or
                                       "BOCCHI.Modules.Automator.CriticalEncounter";
            }
            catch
            {
                return false;
            }
        }

        public static bool TryGetTreasureScanAfter(
            DateTime completedAt,
            DateTime observedCastAt,
            out int bronzeChests,
            out int silverChests)
        {
            bronzeChests = 0;
            silverChests = 0;

            try
            {
                var bocchi = ResolvePlugin();
                if (bocchi == null) return false;

                var tracker = ResolveService(bocchi, "BOCCHI.Treasure.Services.ITreasureTracker");
                var source = "service";
                if (tracker == null)
                {
                    tracker = ResolveModuleMember(
                        bocchi,
                        "BOCCHI.Modules.Treasure.TreasureModule",
                        "Tracker");
                    source = "module";
                }

                if (tracker == null || GetMember(tracker, "CountInitialised") is not bool countInitialised ||
                    !countInitialised)
                    return false;

                var lastParsedValue = GetMember(tracker, "lastParseWideText") ??
                                      GetMember(tracker, "LastParseWideText");
                var lastParsed = lastParsedValue is DateTime parsed ? parsed : DateTime.MinValue;
                var lastCast = observedCastAt;
                var automator = ResolveService(bocchi, "BOCCHI.Automator.Services.IAutomator");
                if (automator != null)
                {
                    _ = GetMember(automator, "CurrentState");
                    var stateMachine = GetMember(automator, "stateMachine");
                    var castHandler = FindStateHandler(
                        stateMachine,
                        "BOCCHI.Automator.StateMachine.Handlers.CastingTreasureSightHandler");
                    if (castHandler != null && GetMember(castHandler, "lastCast") is DateTime cast &&
                        cast > lastCast)
                        lastCast = cast;
                }

                if (lastCast <= completedAt || lastParsed < lastCast) return false;

                bronzeChests = Convert.ToInt32(GetMember(tracker, "BronzeChests") ?? 0);
                silverChests = Convert.ToInt32(GetMember(tracker, "SilverChests") ?? 0);
                DService.Instance().Log.Information(
                    $"[OccultPotNotifier] BOCCHI treasure scan acquired via {source}: " +
                    $"cast={lastCast:O}, parsed={lastParsed:O}, bronze={bronzeChests}, silver={silverChests}");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static object? ResolveService(object bocchi, string serviceTypeName)
        {
            var serviceProvider = GetMember(bocchi, "services") as IServiceProvider;
            if (serviceProvider == null) return null;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var serviceType = assembly.GetType(serviceTypeName);
                if (serviceType != null)
                    return serviceProvider.GetService(serviceType);
            }

            return null;
        }

        private static object? ResolveModuleMember(object bocchi, string moduleTypeName, string memberName)
        {
            var module = ResolveModule(bocchi, moduleTypeName);
            return module == null ? null : GetMember(module, memberName);
        }

        private static object? ResolveModule(object bocchi, string moduleTypeName)
        {
            var moduleType = bocchi.GetType().Assembly.GetType(moduleTypeName);
            var modules = GetMember(bocchi, "Modules");
            if (moduleType == null || modules == null) return null;

            const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            MethodInfo? getModule = null;
            foreach (var method in modules.GetType().GetMethods(bf))
            {
                if (method.Name != "GetModule" || !method.IsGenericMethodDefinition ||
                    method.GetGenericArguments().Length != 1 || method.GetParameters().Length != 0)
                    continue;

                getModule = method;
                break;
            }

            return getModule?.MakeGenericMethod(moduleType).Invoke(modules, null);
        }

        private static object? FindStateHandler(object? stateMachine, string handlerTypeName)
        {
            if (stateMachine == null ||
                GetMember(stateMachine, "handlers") is not System.Collections.IEnumerable handlers)
                return null;

            foreach (var entry in handlers)
            {
                if (entry == null) continue;
                var handler = GetMember(entry, "Value");
                if (handler?.GetType().FullName == handlerTypeName)
                    return handler;
            }

            return null;
        }

        private static object? ResolvePlugin()
        {
            Assembly? dalamud = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                if (asm.GetName().Name == "Dalamud") { dalamud = asm; break; }
            if (dalamud == null) return null;

            var svcOpen = dalamud.GetType("Dalamud.Service`1");
            var pmType  = dalamud.GetType("Dalamud.Plugin.Internal.PluginManager");
            if (svcOpen == null || pmType == null) return null;

            var pm = svcOpen.MakeGenericType(pmType).GetMethod("Get")?.Invoke(null, null);
            if (pm == null) return null;
            if (pm.GetType().GetProperty("InstalledPlugins")?.GetValue(pm) is not System.Collections.IEnumerable installed) return null;

            object? bocchi = null;
            foreach (var lp in installed)
            {
                var lpType = lp.GetType().Name == "LocalDevPlugin" ? lp.GetType().BaseType : lp.GetType();
                if (lpType?.GetProperty("InternalName")?.GetValue(lp) as string != "BOCCHI") continue;
                bocchi = GetMember(lp, "instance") ?? GetMember(lp, "Instance");
                break;
            }
            return bocchi;
        }

        private static object? GetMember(object obj, string name)
        {
            var t = obj.GetType();
            const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            while (t != null)
            {
                if (t.GetProperty(name, bf) is { } p) return p.GetValue(obj);
                if (t.GetField(name, bf)    is { } f) return f.GetValue(obj);
                t = t.BaseType;
            }
            return null;
        }
    }
}
