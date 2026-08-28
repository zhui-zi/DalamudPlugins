using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
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
using FFXIVClientStructs.FFXIV.Client.System.String;
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

internal sealed partial class OccultPotFeature : IDisposable
{
    private const long Respawn = 1800;
    private const float AethernetInteractionDistance = 3.8f;
    private const int AethernetTeleportStartTimeoutMS = 5_000;

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
    private long       undergroundLastPositionUpdateAt;
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
    private long       autoDigRetryFor = -1;
    private int        autoDigRetryCount;
    private long       autoDigRetryAt;
    private Pot?       autoDigTarget;
    private string     autoDigStatus = string.Empty;
    private bool       emergencyReturnTriggered;
    private bool       emergencyReturnRecovering;
    private long       emergencyReturnRecoverAt;
    private bool       battleContentSettling;
    private long       declineInviteAt;
    private uint       declineInviteTime;
    private string     declineInviterName = string.Empty;
    private bool       declineInviteSent;
    private nint       autoAcceptRaiseAddon;
    private long       autoAcceptRaiseAt;
    private bool       autoAcceptRaiseSent;
    private ulong      autoReviveTargetID;
    private long       autoReviveAt;
    private readonly Dictionary<ulong, long> autoReviveRetryAfter = [];
    private ulong      autoReviveConfirmTargetID;
    private string     autoReviveConfirmTargetName = string.Empty;
    private long       autoReviveConfirmUntil;
    private bool       bmrAiSuppressionActive;
    private bool       bmrAiWasEnabled;
    private long       bmrAiSuppressionReleaseAt;
    private CrescentSupportJob? potFatePreviousSupportJob;
    private CrescentSupportJob? potFateTargetSupportJob;
    private bool       potFateSupportJobSwitchActive;
    private bool       potFateTargetJobConfirmed;
    private bool       potFateSupportJobRestoring;
    private bool       potFateSupportJobRecoveryPending;
    private bool       potFateSupportJobSwitchSuppressed;
    private readonly BoundedRetryGate potFateSupportJobRetry = new(8, 750, 10_000);
    private Hook<BmrWalkInputDelegate>? bmrWalkInputHook;
    private Vector3?    potFateMovementDirection;
    private FieldInfo?  bmrMovementInstanceField;
    private FieldInfo?  bmrDesiredDirectionField;
    private Vector3?    lastInjectedBmrDirection;
    private object?     lastInjectedBmrMovementInstance;

    private readonly Queue<CurrencyExchangeRequest> currencyExchangeQueue = [];
    private readonly Dictionary<(uint CurrencyItemID, uint RewardItemID), long> currencyExchangeRetryAfter = [];
    private CurrencyExchangeRequest? pendingCurrencyExchange;
    private int        pendingCurrencyBeforeCount;
    private int        pendingRewardBeforeCount;
    private long       pendingCurrencyActionAt;
    private long       pendingCurrencyDeadline;
    private bool       pendingCurrencyConfirmationClicked;
    private bool       pendingCurrencyPromptLogged;
    private long       nextCurrencyExchangeAt;
    private string     currencyExchangeStatus = string.Empty;

    private bool       cofferHuntActive;
    private long       cofferHuntStartedAt;
    private uint       cofferHuntTerritory;
    private CofferHuntExecutor activeCofferHuntExecutor;
    private bool       drHuntStarted;
    private bool       drOuterRouteActive;
    private bool       bocchiHuntStarted;
    private bool       cofferHuntScanPending;
    private int        cofferHuntScanBronze;
    private int        cofferHuntScanSilver;
    private long       cofferHuntScanExpireAt;
    private long       pendingCofferHuntAutoDigFor = -1;
    private const long CofferHuntRequiredLeadSeconds = 600;
    private const int  CofferHuntSilverCap           = 8;
    private const int  CofferHuntBronzeCap           = 30;
    private const long CofferHuntScanTimeoutMS       = 180_000;
    private const float CofferHuntPlayerAvoidanceRadius = 40f;
    private const uint CofferHuntNorthInitialPreferredAetheryteDataID = 5576;
    private const float CofferHuntStartMinimumDistance = 10f;
    private const long CofferHuntStartHoldMS = 750;
    private const long BmrAiSuppressionReleaseGraceMS = 500;
    private const string BmrWalkInputSignature = "E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D";

    private volatile bool crossDCQuerying;
    private ushort        crossDCTargetDC;
    private string        crossDCTargetWorld = string.Empty;
    private uint          crossDCTargetTerritory;
    private bool          crossingDC;
    private volatile string crossDCReason = string.Empty;

    private const uint LureItemID = 2003296;

    private static readonly string[] DigDirections = ["西北", "西南", "东北", "东南", "正东", "正西", "正南", "正北"];
    private static readonly Regex CofferCountRegex = new(
        @"感知到了\s*(\d+)\s*个银宝箱、\s*(\d+)\s*个铜宝箱",
        RegexOptions.Compiled);

    private unsafe delegate void ShowBattleTalkDelegate(UIModule* module, CStringPointer name, CStringPointer text, float duration, byte style);
    private Hook<ShowBattleTalkDelegate>? showBattleTalkHook;

    private unsafe delegate void ShowBattleTalkImageDelegate(
        UIModule* module, CStringPointer name, CStringPointer text, float duration, uint image, byte style, int sound, uint entityID);
    private Hook<ShowBattleTalkImageDelegate>? showBattleTalkImageHook;

    private unsafe delegate void BmrWalkInputDelegate(
        nint self, float* sumLeft, float* sumForward, float* sumTurnLeft,
        byte* haveBackwardOrStrafe, byte* unknown, byte additiveInput);

    private Pot?   displayPot;
    private string displayText       = string.Empty;
    private long   notifiedSpawnTime = -1;
    private bool   overlayOpen;

    private readonly Pot[] pots =
    [
        new() { TerritoryID = 1252, FateID = 1976, World = new(204.66835f,  111.81729f, -204.96242f), FateCenter = new(200f,       111.7266f,  -215f),     FateRadius = 40f, DirName = "北", Aetheryte = "古树湿原", AetherytePos = new(302.4757f,   102.99427f, 305.8504f) },
        new() { TerritoryID = 1252, FateID = 1977, World = new(-479.8395f,  75f,         524.78894f), FateCenter = new(-481f,      75f,         528f),      FateRadius = 40f, DirName = "南", Aetheryte = "石塔水沼", AetherytePos = new(-384.55502f, 97.29398f,  277.75458f) },
        new() { TerritoryID = 1346, FateID = 2072, World = new(233f,         7.729229f,  -470f),      FateCenter = new(233f,       7.729229f,  -470f),     FateRadius = 40f, DirName = "北", AetheryteData = CrescentAetheryte.SinkingSanctuary,  AetherytePlaceNameID = CrescentAetheryte.SinkingSanctuary.DataID,  AetherytePos = CrescentAetheryte.SinkingSanctuary.Position },
        new() { TerritoryID = 1346, FateID = 2073, World = new(-505.2822f,  53.14409f,   244.041f),  FateCenter = new(-505.2822f, 53.14409f,   244.041f), FateRadius = 38f, DirName = "南", AetheryteData = CrescentAetheryte.SuspendedMasonry, AetherytePlaceNameID = CrescentAetheryte.SuspendedMasonry.DataID, AetherytePos = CrescentAetheryte.SuspendedMasonry.Position }
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
    private const string TrackerVersion     = "KeitaToolbox-MagicPot";
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

    #region Map marker constants

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
    private const int   UndergroundPositionUpdateIntervalMS = 100;
    private const int   UndergroundPositionUpdateMaxElapsedMS = 250;
    private const int   UndergroundSettleMS = 750;
    private const int   MountTimeoutMS      = 20_000;
    private const int   MountBlockedTimeoutMS = 30_000;
    private const int   AutoDigTravelRetryDelayMS = 5_000;
    private const int   AutoDigTravelRetryLimit = 1;
    private const float UndergroundTestMoveDistance  = 12f;
    private const float UndergroundTestMoveTolerance = 1.5f;
    private const int   UndergroundTestEndpointPauseMS = 1_000;
    private const int   UndergroundTestStopTimeoutMS = 10_000;
    private const string UndergroundTestCommand = "occultundergroundtest";
    private static readonly string[] DrInnerLoopRouteAliases = ["内环", "InnerLoop", "Inner Loop", "内回り", "내부"];
    private static readonly string[] DrOuterLoopRouteAliases = ["外环", "OuterLoop", "Outer Loop", "外回り", "외부"];
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

    #region Map marker state

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

        InitializeBmrMovementBridge();


        DService.Instance().Chat.ChatMessage += OnChatMessage;
        GamePacketManager.Instance().RegPreSendPacket(OnPreSendPacket);

        autoDigTask ??= new() { TimeoutMS = 600_000 };
        undergroundTestTask ??= new() { TimeoutMS = 60_000 };

        currentMarkers = config.DefaultMarkers;

        overlayOpen    = false;

        entry         ??= DService.Instance().DTRBar.Get("KeitaToolbox-MagicPot");
        entry.Shown   =   false;
        entry.Tooltip =   "新月岛 魔法罐助手\n左键在地图上标记下一个魔法罐位置 (<flag>)\n右键打开当前岛的众包追踪器";
        entry.OnClick =   OnDtrClick;

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "AreaMap", OnAreaMapRefresh);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,  "ContentsFinderConfirm", OnContentsFinderConfirm);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw,    "ContentsFinderConfirm", OnContentsFinderConfirm);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,  "ShopExchangeCurrency", OnCurrencyExchangeAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw,    "ShopExchangeCurrency", OnCurrencyExchangeAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,  "ShopExchangeCurrencyDialog", OnCurrencyExchangeDialogAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw,    "ShopExchangeCurrencyDialog", OnCurrencyExchangeDialogAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,  "SelectYesno", OnCurrencyExchangeConfirmAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw,    "SelectYesno", OnCurrencyExchangeConfirmAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostSetup,  "SelectYesno", OnAutoAcceptRaiseAddon);
        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PreDraw,    "SelectYesno", OnAutoAcceptRaiseAddon);
        Plugin.PluginInterface.UiBuilder.Draw += OnPostDraw;
        Plugin.PluginInterface.UiBuilder.Draw += DrawOverlayWindow;

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
        ResumePendingSupportJobRestore();
    }

    public void Dispose()
    {
        StopPotFateApproach();
        RestoreBmrAiAfterPotFate();
        RestoreSupportJobAfterPotFate();
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        DService.Instance().Chat.ChatMessage             -= OnChatMessage;
        GamePacketManager.Instance().Unreg(OnPreSendPacket);
        Plugin.PluginInterface.UiBuilder.Draw             -= OnPostDraw;
        Plugin.PluginInterface.UiBuilder.Draw             -= DrawOverlayWindow;
        DService.Instance().AddonLifecycle.UnregisterListener(OnAreaMapRefresh);
        DService.Instance().AddonLifecycle.UnregisterListener(OnContentsFinderConfirm);
        DService.Instance().AddonLifecycle.UnregisterListener(OnCurrencyExchangeAddon);
        DService.Instance().AddonLifecycle.UnregisterListener(OnCurrencyExchangeDialogAddon);
        DService.Instance().AddonLifecycle.UnregisterListener(OnCurrencyExchangeConfirmAddon);
        DService.Instance().AddonLifecycle.UnregisterListener(OnAutoAcceptRaiseAddon);
        FrameworkManager.Instance().Unreg(OnUpdate);
        FrameworkManager.Instance().Unreg(OnCurrencyExchangeUpdate);
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

        bmrWalkInputHook?.Dispose();
        bmrWalkInputHook = null;

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
        if ((!config.EnableAutoDig && !config.EnableCofferHunt) || !InOccultMapZone) return;

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
        DrawDependencyNotice();
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

    private static void DrawDependencyNotice()
    {
        const string text = "新月岛系列功能依赖：BOCCHI、Daily Routines、Lifestream、vnavmesh、BossMod Reborn。";

        var drawList = ImGui.GetWindowDrawList();
        var start    = ImGui.GetCursorScreenPos();
        var width    = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var textSize = ImGui.CalcTextSize(text, false, width);
        var travel   = width + 96f;
        var shimmerX = start.X - 48f + (float)(ImGui.GetTime() * 110d % travel);

        DrawDependencyLine(drawList, start, text, width, textSize, shimmerX);
        ImGui.Dummy(textSize);
    }

    private static void DrawDependencyLine(
        ImDrawListPtr drawList,
        Vector2 position,
        string text,
        float wrapWidth,
        Vector2 textSize,
        float shimmerX)
    {
        var font   = ImGui.GetFont();
        var size   = ImGui.GetFontSize();
        var shadow = ImGui.ColorConvertFloat4ToU32(new Vector4(0.18f, 0.08f, 0.01f, 0.9f));
        var gold   = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.65f, 0.12f, 1f));
        drawList.AddText(font, size, position + Vector2.One, shadow, text, wrapWidth);
        drawList.AddText(font, size, position, gold, text, wrapWidth);

        const int sliceCount = 12;
        const float sliceWidth = 8f;
        for (var i = 0; i < sliceCount; i++)
        {
            var x = shimmerX + (i * sliceWidth);
            if (x >= position.X + textSize.X || x + sliceWidth <= position.X) continue;

            var distance = MathF.Abs((i + 0.5f) - (sliceCount / 2f)) / (sliceCount / 2f);
            var alpha    = 0.2f + ((1f - distance) * 0.8f);
            var color    = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 0.82f, alpha));
            drawList.PushClipRect(
                new Vector2(x, position.Y),
                new Vector2(x + sliceWidth, position.Y + textSize.Y),
                true);
            drawList.AddText(font, size, position, color, text, wrapWidth);
            drawList.PopClipRect();
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
                if (!config.EnableAutoDig)
                {
                    AbortAutoDig();
                    ResetAutoDigTravelRetry();
                }
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
                else if (DangerZoneHandling == DangerZoneHandlingMode.Ground)
                    ImGui.TextColored(KnownColor.Gray.ToVector4(),
                        "北征寻宝移动会动态绕开同级或更高探索等级普通怪的仇恨圈。");
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
                    if (ImGui.Checkbox("死亡后自动返回起始点", ref config.AutoDigReturnOnDeath))
                        config.Save(this);
                    using (ImRaii.Disabled(!config.AutoDigReturnOnDeath))
                    {
                        if (ImGui.Checkbox("仅死亡 3 分钟仍无人施救时返回", ref config.AutoDigWaitForRescue))
                            config.Save(this);
                        if (config.AutoDigWaitForRescue)
                            ImGui.TextColored(KnownColor.Gray.ToVector4(),
                                "等待施救期间不发送罐子通知、语音或聊天消息；开始自动返回后恢复通知。");
                    }
                }

                if (ImGui.Checkbox("半血以下被高等级敌人攻击时紧急返回", ref config.AutoDigEmergencyReturn))
                    config.Save(this);

                ConfigSection("完成后操作");
                if (ImGui.Checkbox("挖完自动跨区（选刷新最短且 >5 分钟的大区）", ref config.EnableAutoCrossDC))
                    config.Save(this);
                if (config.EnableAutoCrossDC)
                    ImGui.TextColored(KnownColor.Orange.ToVector4(),
                        "岛内剩余不足 90 分钟且有可用目标时强制选择其他大区；需启用 /pdrfe 和 /pdr worldtravel；跨区有崩游戏风险。");

                using (ImRaii.Disabled(config.EnableAutoCrossDC))
                {
                    if (ImGui.Checkbox("挖完时岛内不足 90 分钟自动换岛", ref config.ReenterIslandWhenTimeLow))
                        config.Save(this);
                }
                if (config.ReenterIslandWhenTimeLow && !config.EnableAutoCrossDC)
                    ImGui.TextColored(KnownColor.Gray.ToVector4(),
                        "退出副本并等待 30 秒后重新进入当前南征或北征；需启用 /pdrfe。");

                if (ImGui.Checkbox("宝箱达到数量时自动寻宝", ref config.EnableCofferHunt))
                    config.Save(this);
                if (config.EnableCofferHunt)
                {
                    using (ImRaii.PushIndent())
                    {
                        ImGui.TextUnformatted("寻宝执行方式");
                        if (ImGui.RadioButton("DailyRoutines", config.CofferHuntExecutor == CofferHuntExecutor.DailyRoutines))
                        {
                            config.CofferHuntExecutor = CofferHuntExecutor.DailyRoutines;
                            config.Save(this);
                        }

                        if (ImGui.RadioButton("BOCCHI 宝箱猎人", config.CofferHuntExecutor == CofferHuntExecutor.Bocchi))
                        {
                            config.CofferHuntExecutor = CofferHuntExecutor.Bocchi;
                            config.Save(this);
                        }

                        ImGui.Spacing();
                        ImGui.TextUnformatted("魔法罐不足 5 分钟时");
                        if (ImGui.RadioButton(
                                "立即终止寻宝并进入挖罐流程",
                                config.CofferHuntHandoffMode == CofferHuntHandoffMode.InterruptForMagicPot))
                        {
                            config.CofferHuntHandoffMode = CofferHuntHandoffMode.InterruptForMagicPot;
                            config.Save(this);
                        }

                        if (ImGui.RadioButton(
                                "完成当前寻宝后再进入挖罐流程",
                                config.CofferHuntHandoffMode == CofferHuntHandoffMode.FinishCurrentHunt))
                        {
                            config.CofferHuntHandoffMode = CofferHuntHandoffMode.FinishCurrentHunt;
                            config.Save(this);
                        }

                        ImGui.TextColored(KnownColor.Gray.ToVector4(),
                            $"BOCCHI 自动使用魔寻宝后，仅在白银达到 {CofferHuntSilverCap} 或青铜达到 {CofferHuntBronzeCap}，且下个罐子 > 10 分钟时开启。\n" +
                            (config.CofferHuntExecutor == CofferHuntExecutor.DailyRoutines
                                 ? $"DailyRoutines 每次随机选择内环或外环，仅运行其中一条。传送到非起始点魔路水晶后，仅在周围 {CofferHuntPlayerAvoidanceRadius:0} yalms 无其他玩家时启动，发现玩家则换水晶重试。\n" +
                                   "成功启动后会优先复用该水晶；若水晶周围有人或传送失败，再尝试其他水晶。\n"
                                 : "BOCCHI 宝箱猎人从当前位置直接启动，不检查附近玩家，也不预先传送到指定魔路水晶。\n") +
                            (config.CofferHuntHandoffMode == CofferHuntHandoffMode.InterruptForMagicPot
                                 ? "魔法罐不足 5 分钟时立即终止寻宝，回程并衔接挖罐；寻宝提前完成时也会直接交接。"
                                 : "魔法罐不足 5 分钟时继续当前寻宝，完成后回程并衔接挖罐。"));
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

        ConfigSection("魔法罐 FATE 辅助职业");
        using (ImRaii.PushIndent())
        {
            if (ImGui.Checkbox("魔法罐 FATE 开始前 1 秒自动切换辅助职业", ref config.AutoSwitchToNinjaDuringPotFate))
            {
                config.Save(this);
                if (!config.AutoSwitchToNinjaDuringPotFate)
                    RestoreSupportJobAfterPotFate();
            }

            if (config.AutoSwitchToNinjaDuringPotFate)
            {
                using (ImRaii.PushIndent())
                {
                    if (ImGui.RadioButton(
                            "辅助忍者",
                            config.PotFateSupportJobTarget == PotFateSupportJobTarget.Ninja) &&
                        config.PotFateSupportJobTarget != PotFateSupportJobTarget.Ninja)
                    {
                        RestoreSupportJobAfterPotFate();
                        config.PotFateSupportJobTarget = PotFateSupportJobTarget.Ninja;
                        potFateSupportJobSwitchSuppressed = false;
                        config.Save(this);
                    }

                    if (ImGui.RadioButton(
                            "辅助武士",
                            config.PotFateSupportJobTarget == PotFateSupportJobTarget.Samurai) &&
                        config.PotFateSupportJobTarget != PotFateSupportJobTarget.Samurai)
                    {
                        RestoreSupportJobAfterPotFate();
                        config.PotFateSupportJobTarget = PotFateSupportJobTarget.Samurai;
                        potFateSupportJobSwitchSuppressed = false;
                        config.Save(this);
                    }
                }
            }

            ImGui.TextColored(KnownColor.Gray.ToVector4(),
                "依据当前岛的魔法罐倒计提前切换；FATE 结束或离开后恢复原辅助职业。");
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
                "离开或 FATE 结束时，仅恢复由本功能关闭且进入前原本开启的 AI。\n" +
                "AI 关闭时复用 BMR 移动输入靠近选中目标；手动移动和 BMR 机制移动优先。");
        }

        ConfigUIAutoRevive();

        ConfigSection("死亡时自动接受复活");
        using (ImRaii.PushIndent())
        {
            if (ImGui.Checkbox("自动接受他人复活（延迟 1 秒）", ref config.AutoAcceptRaise))
            {
                ResetAutoAcceptRaise();
                config.Save(this);
            }
        }
    }

    private void ConfigUICurrencyExchange()
    {
        ConfigSection("新月岛货币兑换");
        using (ImRaii.PushIndent())
        {
            var busy = pendingCurrencyExchange.HasValue || currencyExchangeQueue.Count > 0;
            ImGui.TextUnformatted("兑换目标");
            using (ImRaii.Disabled(busy))
            {
                if (ImGui.RadioButton("终极固定剂（仅北征）", config.CurrencyExchangeReward == CurrencyExchangeReward.UltimateFixative))
                {
                    config.CurrencyExchangeReward = CurrencyExchangeReward.UltimateFixative;
                    config.Save(this);
                }

                if (ImGui.RadioButton("辅助道具：古旧的钱箱（南征与北征）", config.CurrencyExchangeReward == CurrencyExchangeReward.OldCoffer))
                {
                    config.CurrencyExchangeReward = CurrencyExchangeReward.OldCoffer;
                    config.Save(this);
                }
            }

            if (ImGui.Checkbox("货币达到 9999 时，在初始点等待期间自动兑换", ref config.EnableAutoCurrencyExchange))
            {
                if (config.EnableAutoCurrencyExchange)
                    currencyExchangeRetryAfter.Clear();
                else
                    ResetCurrencyExchange();
                config.Save(this);
            }

            var exchanges = CurrencyExchangeCatalog.Get(GameState.TerritoryType, config.CurrencyExchangeReward);
            foreach (var exchange in exchanges)
            {
                var count = GetCurrencyCount(exchange.CurrencyItemID);
                ImGui.TextUnformatted($"{exchange.CurrencyName}: {count}/{CurrencyStackCap}（可兑换 {count / exchange.Cost} 个）");
            }

            var canExchange = CanExchangeCurrenciesNow(out var unavailableReason);
            var hasAffordableCurrency = exchanges.Any(exchange =>
                GetCurrencyCount(exchange.CurrencyItemID) >= exchange.Cost);
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
                ImGui.TextColored(KnownColor.Gray.ToVector4(), "当前货币不足一次兑换。");

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
                             : autoDigRetryFor == nextSpawnTime && Environment.TickCount64 < autoDigRetryAt
                             ? "准备重试"
                             : "待命";
            ImGui.TextColored(KnownColor.Gray.ToVector4(), $"自动挖罐: {status}");
        }

        ImGui.Separator();

        using (ImRaii.Disabled(!autoDigActive && !cofferHuntActive))
        {
            if (ImGui.Button("终止当前操作"))
                StopAutoDigManually();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("立即停止当前自动挖罐或寻宝（本轮罐子不再自动触发；下个罐子仍会照常开始）");
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
        StopPotFateApproach();
        RestoreBmrAiAfterPotFate();
        RestoreSupportJobAfterPotFate();
        potFateSupportJobSwitchSuppressed = false;
        StopUndergroundTest(false);
        FrameworkManager.Instance().Unreg(OnUpdate);
        FrameworkManager.Instance().Unreg(OnCurrencyExchangeUpdate);
        FrameworkManager.Instance().Unreg(OnPotFateTarget);
        FrameworkManager.Instance().Unreg(OnAutoDigSafety);
        FrameworkManager.Instance().Unreg(OnAutoRevive);
        ResetAutoReviveCandidate();
        ResetAutoReviveConfirmation();
        ResetAutoAcceptRaise();
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
        ClearPendingCofferHuntScan();

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
        FrameworkManager.Instance().Reg(OnCurrencyExchangeUpdate, CurrencyExchangeUpdateIntervalMS);
        FrameworkManager.Instance().Reg(OnPotFateTarget, 100);
        FrameworkManager.Instance().Reg(OnAutoDigSafety, 100);
    }

    private void OnPotFateTarget(IFramework _)
    {
        MaintainPotFateTarget();
        MaintainBmrAiSuppression();
        MaintainPotFateSupportJob();
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

        UpdateNorthHornAggroAvoidance();

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
        var bocchiStopMode = EmergencyStopBocchi();
        DService.Instance().Log.Information(
            $"[KeitaToolbox.MagicPot] Magic Pot FATE cleanup; BOCCHI stop={bocchiStopMode}");
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

    private bool CurrencyExchangeBlockedByAutomation =>
        autoDigActive || cofferHuntActive || undergroundTestActive || standbyDeathReturning ||
        crossingDC || undergroundDangerActive;

    private bool CanExchangeCurrenciesNow(out string reason)
    {
        if (!InOccultMapZone)
        {
            reason = "仅可在新月岛内使用。";
            return false;
        }

        if (CurrencyExchangeCatalog.Get(GameState.TerritoryType, config.CurrencyExchangeReward).Count == 0)
        {
            reason = "终极固定剂仅可在新月岛北方海角兑换。";
            return false;
        }

        if (InForkedTower)
        {
            reason = "歧路之塔内暂停兑换。";
            return false;
        }

        if (CurrencyExchangeBlockedByAutomation)
        {
            reason = "魔法罐自动化期间暂停兑换。";
            return false;
        }

        var localPlayer = DService.Instance().ObjectTable.LocalPlayer;
        if (localPlayer is not { IsDead: false })
        {
            reason = "角色未登录或已倒地。";
            return false;
        }

        if (!CurrencyExchangeLocationPolicy.IsNearInitialAetheryte(
                localPlayer.Position,
                GetCofferHuntBasePosition(GameState.TerritoryType)))
        {
            reason = "仅在靠近初始点魔路水晶等待时兑换。";
            return false;
        }

        var condition = DService.Instance().Condition;
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            reason = "过图期间暂停兑换。";
            return false;
        }

        if (HasActiveSelectYesno())
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
        foreach (var exchange in CurrencyExchangeCatalog.Get(GameState.TerritoryType, config.CurrencyExchangeReward))
        {
            var count = GetCurrencyCount(exchange.CurrencyItemID);
            var retryAfter = automatic &&
                             currencyExchangeRetryAfter.TryGetValue(
                                 (exchange.CurrencyItemID, exchange.RewardItemID),
                                 out var blockedUntil)
                                 ? blockedUntil
                                 : 0;
            if (count < exchange.Cost ||
                automatic && !CurrencyExchangeRetryPolicy.ShouldQueueAutomatic(
                    count,
                    CurrencyStackCap,
                    now,
                    retryAfter))
                continue;

            currencyExchangeQueue.Enqueue(new(exchange, automatic, 0));
            queued++;
        }

        if (queued == 0)
        {
            if (!automatic)
                currencyExchangeStatus = "当前货币不足一次兑换。";
            return;
        }

        currencyExchangeStatus = automatic
                                     ? "检测到货币达到 9999，已加入自动兑换队列。"
                                     : $"已加入 {queued} 种货币的全部兑换队列。";
        BeginCurrencyExchangeBocchiSuppression();
    }

    private unsafe void OnCurrencyExchangeConfirmAddon(AddonEvent _, AddonArgs args)
    {
        if (pendingCurrencyExchange is not { } pending ||
            pendingCurrencyActionAt != 0 ||
            pendingCurrencyConfirmationClicked ||
            args.Addon.IsNull)
        {
            return;
        }

        TryConfirmCurrencyExchangeSelectYesno(pending, args.Addon.ToStruct(), "SelectYesnoLifecycle");
    }

    private unsafe void TryConfirmPendingCurrencyExchange(CurrencyExchangeRequest pending)
    {
        if (pendingCurrencyConfirmationClicked)
            return;

        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("SelectYesno");
        TryConfirmCurrencyExchangeSelectYesno(pending, addon, "SelectYesnoFramework");
    }

    private unsafe void TryConfirmCurrencyExchangeSelectYesno(
        CurrencyExchangeRequest pending,
        AtkUnitBase* addonBase,
        string source)
    {
        if (addonBase == null || !addonBase->IsReady || !addonBase->IsVisible)
            return;

        var rewardConfirmText = pending.Spec.RewardName.Contains('：')
                                    ? pending.Spec.RewardName[(pending.Spec.RewardName.IndexOf('：') + 1)..]
                                    : pending.Spec.RewardName;
        var addon = (AddonSelectYesno*)addonBase;
        var promptText = GetSelectYesnoPromptText(addon);
        if (!pendingCurrencyPromptLogged)
        {
            pendingCurrencyPromptLogged = true;
            var promptMatches = CurrencyExchangeConfirmationPolicy.MatchesPrompt(
                promptText,
                pending.Spec.CurrencyName,
                rewardConfirmText);
            Plugin.Log.Information(
                $"[KeitaToolbox.MagicPot] Observed event-scoped currency confirmation source={source}, promptMatch={promptMatches}, prompt={promptText.Replace('\r', ' ').Replace('\n', ' ')}");
        }

        if (!AddonSelectYesnoEvent.ClickYes())
            return;

        addonBase->IsVisible = false;
        MarkCurrencyExchangeConfirmed(pending, source);
    }

    private void MarkCurrencyExchangeConfirmed(CurrencyExchangeRequest pending, string addonName)
    {
        pendingCurrencyConfirmationClicked = true;
        currencyExchangeStatus = $"已自动确认{pending.Spec.CurrencyName}兑换，等待库存更新…";
        Plugin.Log.Information(
            $"[KeitaToolbox.MagicPot] Auto-confirmed remote currency exchange addon={addonName}, event={pending.Spec.EventID:X}, currency={pending.Spec.CurrencyItemID}, reward={pending.Spec.RewardItemID}, quantity={pending.Quantity}");
    }

    private static unsafe bool HasActiveSelectYesno()
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("SelectYesno");
        return addon != null && addon->IsReady && addon->IsVisible;
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
            if (items[index].ItemId != request.Spec.RewardItemID)
                continue;

            itemIndex = index;
            AgentId.Shop.SendEvent(1, 0, itemIndex, request.Quantity, 0);
            return true;
        }

        return false;
    }

    private void DriveCurrencyExchange()
    {
        if (!InOccultMapZone)
            return;

        var now = Environment.TickCount64;
        MaintainCurrencyExchangeWindowCleanup(now);
        KeepCurrencyExchangeBocchiSuppressed(now);
        if (CurrencyExchangeBlockedByAutomation)
        {
            PauseCurrencyExchangeForAutomation();
            return;
        }

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
                        FailCurrencyExchange(pending, now, $"未加载{pending.Spec.CurrencyName}兑换数据。");
                        return;
                    }

                    pendingCurrencyActionAt = 0;
                    pendingCurrencyDeadline = now + CurrencyExchangeConfirmTimeoutMS;
                    currencyExchangeStatus = $"已发送{pending.Spec.CurrencyName}兑换 ×{pending.Quantity}，等待库存确认…";
                    Plugin.Log.Information(
                        $"[KeitaToolbox.MagicPot] Sent remote currency exchange through AgentShop event={pending.Spec.EventID:X}, currency={pending.Spec.CurrencyItemID}, reward={pending.Spec.RewardItemID}, shopIndex={itemIndex}, quantity={pending.Quantity}");
                }
                catch (Exception ex)
                {
                    CompleteCurrencyExchangeSession(pending.Spec.EventID);
                    FailCurrencyExchange(pending, now, $"发送{pending.Spec.CurrencyName}兑换动作包失败。", ex);
                }

                return;
            }

            TryConfirmPendingCurrencyExchange(pending);

            var currentCount = GetCurrencyCount(pending.Spec.CurrencyItemID);
            var currentRewardCount = GetCurrencyCount(pending.Spec.RewardItemID);
            var expectedCurrencyCount = pendingCurrencyBeforeCount - pending.Quantity * pending.Spec.Cost;
            var currencyConfirmed = currentCount <= expectedCurrencyCount;
            var rewardConfirmed = currentRewardCount >= pendingRewardBeforeCount + pending.Quantity;
            if (currencyConfirmed || rewardConfirmed)
            {
                CompleteCurrencyExchangeSession(pending.Spec.EventID);
                ScheduleCurrencyExchangeWindowCleanup(now);
                currencyExchangeRetryAfter.Remove((pending.Spec.CurrencyItemID, pending.Spec.RewardItemID));
                pendingCurrencyExchange = null;
                pendingCurrencyBeforeCount = 0;
                pendingRewardBeforeCount = 0;
                pendingCurrencyActionAt = 0;
                pendingCurrencyDeadline = 0;
                pendingCurrencyConfirmationClicked = false;
                pendingCurrencyPromptLogged = false;
                nextCurrencyExchangeAt = now + CurrencyExchangeSpacingMS;

                var message = $"{pending.Spec.CurrencyName}已兑换为{pending.Spec.RewardName} ×{pending.Quantity}";
                currencyExchangeStatus = currencyExchangeQueue.Count == 0
                                             ? $"{message}；本轮兑换完成。"
                                             : $"{message}；正在等待下一种货币。";
                NotifyHelper.Instance().Chat(message);
                Plugin.Log.Information(
                    $"[KeitaToolbox.MagicPot] Confirmed remote currency exchange item={pending.Spec.CurrencyItemID}, quantity={pending.Quantity}");
                if (currencyExchangeQueue.Count == 0)
                    EndCurrencyExchangeBocchiSuppression(true);
                return;
            }

            if (now < pendingCurrencyDeadline)
                return;

            CompleteCurrencyExchangeSession(pending.Spec.EventID);
            var timeoutMessage = pending.Automatic
                                     ? $"未确认{pending.Spec.CurrencyName}库存下降。"
                                     : $"未确认{pending.Spec.CurrencyName}库存下降，请检查背包容量后重试。";
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
                var rewardBeforeCount = GetCurrencyCount(request.Spec.RewardItemID);
                currencyExchangeWindowCleanupUntil = 0;
                new EventStartPackt(LocalPlayerState.EntityID, request.Spec.EventID).Send();

                pendingCurrencyExchange = request with { Quantity = quantity };
                pendingCurrencyBeforeCount = currentCount;
                pendingRewardBeforeCount = rewardBeforeCount;
                pendingCurrencyActionAt = now;
                pendingCurrencyDeadline = now + CurrencyExchangeSessionTimeoutMS;
                pendingCurrencyConfirmationClicked = false;
                pendingCurrencyPromptLogged = false;
                currencyExchangeStatus = $"正在建立{request.Spec.CurrencyName}兑换会话…";
                Plugin.Log.Information(
                    $"[KeitaToolbox.MagicPot] Started remote currency exchange territory={GameState.TerritoryType}, player={LocalPlayerState.EntityID:X}, event={request.Spec.EventID:X}, currency={request.Spec.CurrencyItemID}, reward={request.Spec.RewardItemID}, quantity={quantity}");
            }
            catch (Exception ex)
            {
                FailCurrencyExchange(request with { Quantity = quantity }, now, $"建立{request.Spec.CurrencyName}兑换会话失败。", ex);
            }

            return;
        }

        EndCurrencyExchangeBocchiSuppression(true);
    }

    private void PauseCurrencyExchangeForAutomation()
    {
        if (pendingCurrencyExchange is not { } pending)
        {
            currencyExchangeQueue.Clear();
            EndCurrencyExchangeBocchiSuppression(false);
            return;
        }

        CompleteCurrencyExchangeSession(pending.Spec.EventID);
        ScheduleCurrencyExchangeWindowCleanup(Environment.TickCount64);
        pendingCurrencyExchange = null;
        pendingCurrencyBeforeCount = 0;
        pendingRewardBeforeCount = 0;
        pendingCurrencyActionAt = 0;
        pendingCurrencyDeadline = 0;
        pendingCurrencyConfirmationClicked = false;
        pendingCurrencyPromptLogged = false;
        nextCurrencyExchangeAt = 0;
        currencyExchangeStatus = "魔法罐自动化期间已暂停兑换。";
        EndCurrencyExchangeBocchiSuppression(false);
        Plugin.Log.Information(
            $"[KeitaToolbox.MagicPot] Paused remote currency exchange during Magic Pot automation item={pending.Spec.CurrencyItemID}, quantity={pending.Quantity}");
    }

    private void FailCurrencyExchange(
        CurrencyExchangeRequest request,
        long now,
        string message,
        Exception? exception = null)
    {
        currencyExchangeRetryAfter[(request.Spec.CurrencyItemID, request.Spec.RewardItemID)] = now + CurrencyExchangeRetryCooldownMS;
        if (request.Automatic)
        {
            currencyExchangeQueue.Clear();
            message += " 本轮兑换已停止，将在再次满足条件时重试。";
        }

        pendingCurrencyExchange = null;
        pendingCurrencyBeforeCount = 0;
        pendingRewardBeforeCount = 0;
        pendingCurrencyActionAt = 0;
        pendingCurrencyDeadline = 0;
        pendingCurrencyConfirmationClicked = false;
        pendingCurrencyPromptLogged = false;
        nextCurrencyExchangeAt = now + CurrencyExchangeSpacingMS;
        currencyExchangeStatus = message;
        ScheduleCurrencyExchangeWindowCleanup(now);
        EndCurrencyExchangeBocchiSuppression(true);
        NotifyHelper.Instance().NotificationWarning(message);

        if (exception == null)
            Plugin.Log.Warning(
                $"[KeitaToolbox.MagicPot] Remote currency exchange timed out item={request.Spec.CurrencyItemID}, quantity={request.Quantity}");
        else
            Plugin.Log.Error(exception,
                $"[KeitaToolbox.MagicPot] Remote currency exchange failed item={request.Spec.CurrencyItemID}, quantity={request.Quantity}");
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
                $"[KeitaToolbox.MagicPot] Failed to complete remote currency exchange session event={eventID:X}");
        }
    }

    private void ResetCurrencyExchange()
    {
        EndCurrencyExchangeBocchiSuppression(false);
        currencyExchangeQueue.Clear();
        currencyExchangeRetryAfter.Clear();
        pendingCurrencyExchange = null;
        pendingCurrencyBeforeCount = 0;
        pendingRewardBeforeCount = 0;
        pendingCurrencyActionAt = 0;
        pendingCurrencyDeadline = 0;
        pendingCurrencyConfirmationClicked = false;
        pendingCurrencyPromptLogged = false;
        nextCurrencyExchangeAt = 0;
        currencyExchangeWindowCleanupUntil = 0;
        CloseCurrencyExchangeWindows();
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

        DriveCofferHuntFromTreasureScan(now);
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

    private unsafe void OnAutoAcceptRaiseAddon(AddonEvent _, AddonArgs args)
    {
        var agentModule = AgentModule.Instance();
        var agentInterface = agentModule == null ? null : agentModule->GetAgentByInternalId(AgentId.Revive);
        var reviveAgent = agentInterface == null ? null : (AgentRevive*)agentInterface;
        var reviveState = reviveAgent == null ? (byte)0 : reviveAgent->ReviveState;
        var resurrectionTimeLeft = reviveAgent == null ? 0 : reviveAgent->ResurrectionTimeLeft;
        var resurrectingPlayerID = reviveAgent == null ? 0 : reviveAgent->ResurrectingPlayerId;
        var localPlayer = DService.Instance().ObjectTable.LocalPlayer;
        var betweenAreas = DService.Instance().Condition[ConditionFlag.BetweenAreas] ||
                           DService.Instance().Condition[ConditionFlag.BetweenAreas51];
        var promptText = GetSelectYesnoPromptText(args);
        var promptMatchesRaise = AutoAcceptRaisePolicy.MatchesPrompt(promptText);

        if (!AutoAcceptRaisePolicy.CanAccept(
                config.AutoAcceptRaise,
                InOccultMapZone,
                localPlayer is { IsDead: true },
                betweenAreas,
                promptText))
        {
            ResetAutoAcceptRaise();
            return;
        }

        if (autoAcceptRaiseAddon != args.Addon.Address ||
            autoAcceptRaiseAt == 0 && !autoAcceptRaiseSent)
        {
            autoAcceptRaiseAddon = args.Addon.Address;
            autoAcceptRaiseAt = Environment.TickCount64 + 1000;
            autoAcceptRaiseSent = false;
            Plugin.Log.Information(
                $"[KeitaToolbox.MagicPot] Incoming raise detected; state={reviveState}, timeLeft={resurrectionTimeLeft}, playerID={resurrectingPlayerID}, textMatch={promptMatchesRaise}");
            return;
        }

        if (autoAcceptRaiseSent || Environment.TickCount64 < autoAcceptRaiseAt)
            return;

        if (AddonSelectYesnoEvent.ClickYes())
        {
            autoAcceptRaiseSent = true;
            autoAcceptRaiseAt = 0;
            Plugin.Log.Information("[KeitaToolbox.MagicPot] Accepted incoming raise after 1 second.");
        }
    }

    private static unsafe string GetSelectYesnoPromptText(AddonArgs args)
    {
        if (args.Addon.IsNull)
            return string.Empty;

        var addon = (AddonSelectYesno*)args.Addon.Address;
        return GetSelectYesnoPromptText(addon);
    }

    private static unsafe string GetSelectYesnoPromptText(AddonSelectYesno* addon)
    {
        if (addon == null || addon->PromptText == null)
            return string.Empty;

        return ((Utf8String*)&addon->PromptText->NodeText)->ToString();
    }

    private void ResetAutoAcceptRaise()
    {
        autoAcceptRaiseAddon = nint.Zero;
        autoAcceptRaiseAt = 0;
        autoAcceptRaiseSent = false;
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
        {
            StopPotFateApproach();
            return;
        }

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
        {
            StopPotFateApproach();
            return;
        }

        OmenBattleChara? selected = null;
        OmenBattleChara? nearest = null;
        var nearestDistance = float.MaxValue;

        foreach (var obj in DService.Instance().ObjectTable)
        {
            if (obj is not OmenBattleChara enemy || !IsValidPotFateEnemy(enemy, activePotFateID))
                continue;

            if (enemy.Address == (nint)targetSystem->Target)
            {
                selected = enemy;
                break;
            }

            var distance = Vector3.DistanceSquared(localPlayer.Position, enemy.Position);
            if (distance >= nearestDistance) continue;

            nearest = enemy;
            nearestDistance = distance;
        }

        selected ??= nearest;
        if (selected == null)
        {
            StopPotFateApproach();
            return;
        }

        if (selected.Address != (nint)targetSystem->Target)
            targetSystem->Target = (GameObject*)selected.Address;

        MaintainPotFateApproach(localPlayer, selected);
    }

    private unsafe void MaintainPotFateApproach(OmenBattleChara localPlayer, OmenBattleChara target)
    {
        if (BmrAi.TryGetEnabled(out var enabled) && enabled)
        {
            StopPotFateApproach();
            return;
        }

        var playerObject = (Character*)localPlayer.Address;
        var targetObject = (GameObject*)target.Address;
        var castInfo = playerObject == null ? null : playerObject->GetCastInfo();
        if (targetObject == null || castInfo != null && castInfo->IsCasting)
        {
            StopPotFateApproach();
            return;
        }

        var delta = target.Position - localPlayer.Position;
        delta.Y = 0f;
        var approachRange = localPlayer.ClassJob.Value.Role is 1 or 2
            ? Plugin.Config.Bmrai.MeleeDistance
            : Plugin.Config.Bmrai.RangedDistance;
        var stopDistance = Math.Max(0f, approachRange) + Math.Max(0f, targetObject->HitboxRadius);
        if (delta.LengthSquared() <= stopDistance * stopDistance)
        {
            StopPotFateApproach();
            return;
        }

        potFateMovementDirection = delta;
        TryInjectBmrMovementDirection(delta);
    }

    private void StopPotFateApproach()
    {
        potFateMovementDirection = null;

        if (lastInjectedBmrMovementInstance != null &&
            lastInjectedBmrDirection is { } lastDirection &&
            bmrDesiredDirectionField?.DeclaringType?.IsInstanceOfType(lastInjectedBmrMovementInstance) == true)
        {
            try
            {
                if (bmrDesiredDirectionField.GetValue(lastInjectedBmrMovementInstance) is Vector3 currentDirection &&
                    Vector3.DistanceSquared(currentDirection, lastDirection) <= 0.0001f)
                    bmrDesiredDirectionField.SetValue(lastInjectedBmrMovementInstance, null);
            }
            catch
            {
            }
        }

        lastInjectedBmrDirection = null;
        lastInjectedBmrMovementInstance = null;
    }

    private unsafe void InitializeBmrMovementBridge()
    {
        if (bmrWalkInputHook != null) return;

        try
        {
            bmrWalkInputHook = Plugin.Interop.HookFromSignature<BmrWalkInputDelegate>(
                BmrWalkInputSignature,
                BmrWalkInputDetour);
            bmrWalkInputHook.Enable();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to initialize the BMR movement bridge.");
            bmrWalkInputHook?.Dispose();
            bmrWalkInputHook = null;
        }
    }

    private unsafe void BmrWalkInputDetour(
        nint self, float* sumLeft, float* sumForward, float* sumTurnLeft,
        byte* haveBackwardOrStrafe, byte* unknown, byte additiveInput)
    {
        try
        {
            if (potFateMovementDirection is { } direction)
                TryInjectBmrMovementDirection(direction);
        }
        catch
        {
            ResetBmrMovementReflection();
        }
        finally
        {
            bmrWalkInputHook!.Original(
                self,
                sumLeft,
                sumForward,
                sumTurnLeft,
                haveBackwardOrStrafe,
                unknown,
                additiveInput);
        }
    }

    private bool TryInjectBmrMovementDirection(Vector3 direction)
    {
        try
        {
            if (!TryResolveBmrMovementOverride(out var movement, out var desiredDirectionField))
                return false;

            var current = desiredDirectionField.GetValue(movement);
            var currentIsOurs = ReferenceEquals(movement, lastInjectedBmrMovementInstance) &&
                                current is Vector3 currentDirection &&
                                lastInjectedBmrDirection is { } lastDirection &&
                                Vector3.DistanceSquared(currentDirection, lastDirection) <= 0.0001f;
            if (current != null && !currentIsOurs)
                return false;

            desiredDirectionField.SetValue(movement, direction);
            lastInjectedBmrDirection = direction;
            lastInjectedBmrMovementInstance = movement;
            return true;
        }
        catch
        {
            ResetBmrMovementReflection();
            return false;
        }
    }

    private bool TryResolveBmrMovementOverride(out object movement, out FieldInfo desiredDirectionField)
    {
        movement = null!;
        desiredDirectionField = null!;

        if (bmrMovementInstanceField?.GetValue(null) is { } cachedMovement &&
            bmrDesiredDirectionField != null)
        {
            movement = cachedMovement;
            desiredDirectionField = bmrDesiredDirectionField;
            return true;
        }

        ResetBmrMovementReflection();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name != "BossModReborn") continue;

            var movementType = assembly.GetType("BossMod.MovementOverride");
            const BindingFlags staticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            const BindingFlags instanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var instanceField = movementType?.GetField("Instance", staticFlags);
            var desiredField = movementType?.GetField("DesiredDirection", instanceFlags);
            if (instanceField?.GetValue(null) is not { } resolvedMovement || desiredField == null)
                continue;

            bmrMovementInstanceField = instanceField;
            bmrDesiredDirectionField = desiredField;
            movement = resolvedMovement;
            desiredDirectionField = desiredField;
            return true;
        }

        return false;
    }

    private void ResetBmrMovementReflection()
    {
        bmrMovementInstanceField = null;
        bmrDesiredDirectionField = null;
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
            "[KeitaToolbox.MagicPot] Bossmod Reborn AI disabled for Magic Pot FATE");
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
            "[KeitaToolbox.MagicPot] Bossmod Reborn AI restored after Magic Pot FATE");
    }

    private unsafe void MaintainPotFateSupportJob()
    {
        var participating = IsParticipatingInPotFate();
        var potFateActive = pots.Any(pot =>
            pot.TerritoryID == GameState.TerritoryType && pot.Alive);
        var shouldUseSupportJob = PotFateSupportJobPolicy.ShouldUseSupportJob(
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            nextSpawnTime,
            participating,
            potFateSupportJobSwitchActive);
        if (!config.AutoSwitchToNinjaDuringPotFate || !shouldUseSupportJob)
        {
            RestoreSupportJobAfterPotFate();
            if (!potFateActive && !participating)
                potFateSupportJobSwitchSuppressed = false;
            return;
        }

        if (potFateSupportJobSwitchActive &&
            potFateTargetSupportJob?.JobType != GetConfiguredPotFateSupportJob().JobType)
        {
            RestoreSupportJobAfterPotFate();
            return;
        }

        if (potFateSupportJobSwitchSuppressed)
            return;

        var localPlayer = DService.Instance().ObjectTable.LocalPlayer;
        if (localPlayer is not { IsDead: false } ||
            DService.Instance().Condition[ConditionFlag.BetweenAreas] ||
            DService.Instance().Condition[ConditionFlag.BetweenAreas51])
            return;

        var currentJob = CrescentSupportJob.GetCurrentSupportJob();
        if (!ResolvePendingSupportJobRecovery(currentJob))
            return;

        if (!potFateSupportJobSwitchActive)
        {
            if (currentJob is null) return;

            var targetJob = GetConfiguredPotFateSupportJob();
            potFatePreviousSupportJob = currentJob;
            potFateTargetSupportJob = targetJob;
            potFateSupportJobSwitchActive = true;
            potFateTargetJobConfirmed = currentJob.JobType == targetJob.JobType;
            potFateSupportJobRestoring = false;
            RememberPendingSupportJobRestore(currentJob, targetJob);
            if (!potFateTargetJobConfirmed)
                potFateSupportJobRetry.Start(Environment.TickCount64);
            DService.Instance().Log.Information(
                $"[KeitaToolbox.MagicPot] Magic Pot support job switch armed; previous={currentJob.JobType}, target={targetJob.JobType}");
        }

        var activeTargetJob = potFateTargetSupportJob;
        if (activeTargetJob is null)
        {
            ClearPotFateSupportJobSwitch();
            return;
        }

        if (currentJob?.JobType == activeTargetJob.JobType)
        {
            if (!potFateTargetJobConfirmed)
            {
                potFateTargetJobConfirmed = true;
                DService.Instance().Log.Information(
                    $"[KeitaToolbox.MagicPot] Support job confirmed for Magic Pot FATE; target={activeTargetJob.JobType}");
            }

            potFateSupportJobRetry.Clear();
            return;
        }

        var now = Environment.TickCount64;
        if (potFateSupportJobRetry.IsExpired(now))
        {
            DService.Instance().Log.Warning(
                $"[KeitaToolbox.MagicPot] Support job switch timed out; target={activeTargetJob.JobType}; suppressing retries until the next Magic Pot FATE");
            potFateSupportJobRetry.Clear();
            potFateSupportJobSwitchSuppressed = true;
            return;
        }

        if (potFateSupportJobRetry.TryTake(now))
            activeTargetJob.ChangeTo();
    }

    private CrescentSupportJob GetConfiguredPotFateSupportJob() =>
        config.PotFateSupportJobTarget switch
        {
            PotFateSupportJobTarget.Samurai => CrescentSupportJob.Samurai,
            _                               => CrescentSupportJob.Ninja,
        };

    private unsafe bool IsParticipatingInPotFate()
    {
        var localPlayer = DService.Instance().ObjectTable.LocalPlayer;
        var gameObject = localPlayer == null ? null : (GameObject*)localPlayer.Address;
        return gameObject != null && GetPot(gameObject->FateId) != null;
    }

    private void RestoreSupportJobAfterPotFate()
    {
        if (!potFateSupportJobSwitchActive) return;

        var previousJob = potFatePreviousSupportJob;
        var targetJob = potFateTargetSupportJob;
        if (previousJob is null || targetJob is null)
        {
            ClearPotFateSupportJobSwitch();
            return;
        }

        if (!InOccultMapZone)
        {
            DService.Instance().Log.Information(
                "[KeitaToolbox.MagicPot] Cleared pending support job restoration after leaving Occult Crescent");
            ClearPotFateSupportJobSwitch();
            return;
        }

        if (DService.Instance().ObjectTable.LocalPlayer is not { IsDead: false } ||
            DService.Instance().Condition[ConditionFlag.BetweenAreas] ||
            DService.Instance().Condition[ConditionFlag.BetweenAreas51])
            return;

        var currentJob = CrescentSupportJob.GetCurrentSupportJob();
        if (!ResolvePendingSupportJobRecovery(currentJob))
            return;

        if (!potFateTargetJobConfirmed)
        {
            if (currentJob?.JobType != targetJob.JobType)
            {
                DService.Instance().Log.Information(
                    $"[KeitaToolbox.MagicPot] Skipped support job restoration because the target switch was never confirmed; target={targetJob.JobType}");
                ClearPotFateSupportJobSwitch();
                return;
            }

            potFateTargetJobConfirmed = true;
        }

        if (currentJob is not null &&
            currentJob.JobType != targetJob.JobType &&
            currentJob.JobType != previousJob.JobType)
        {
            DService.Instance().Log.Information(
                $"[KeitaToolbox.MagicPot] Skipped support job restoration after a manual change; current={currentJob.JobType}");
            ClearPotFateSupportJobSwitch();
            return;
        }

        if (currentJob?.JobType == previousJob.JobType)
        {
            DService.Instance().Log.Information(
                $"[KeitaToolbox.MagicPot] Support job restored after Magic Pot FATE; job={previousJob.JobType}");
            ClearPotFateSupportJobSwitch();
            return;
        }

        var now = Environment.TickCount64;
        if (!potFateSupportJobRestoring)
        {
            potFateSupportJobRestoring = true;
            potFateSupportJobRetry.Start(now);
        }

        if (potFateSupportJobRetry.IsExpired(now))
        {
            DService.Instance().Log.Warning(
                $"[KeitaToolbox.MagicPot] Support job restoration timed out; previous={previousJob.JobType}");
            ClearPotFateSupportJobSwitch();
            return;
        }

        if (potFateSupportJobRetry.TryTake(now))
            previousJob.ChangeTo();
    }

    private void ResumePendingSupportJobRestore()
    {
        if (config.PendingSupportJobRestore < 0)
            return;

        if (!InOccultMapZone)
        {
            ClearPendingSupportJobRestore();
            return;
        }

        var previousJob = CrescentSupportJob.AllJobs.FirstOrDefault(
            job => (int)job.JobType == config.PendingSupportJobRestore);
        var targetJobType = config.PendingSupportJobTarget >= 0
                                ? config.PendingSupportJobTarget
                                : (int)CrescentSupportJobType.Ninja;
        var targetJob = CrescentSupportJob.AllJobs.FirstOrDefault(
            job => (int)job.JobType == targetJobType);
        if (previousJob is null || targetJob is null)
        {
            ClearPendingSupportJobRestore();
            return;
        }

        potFatePreviousSupportJob = previousJob;
        potFateTargetSupportJob = targetJob;
        potFateSupportJobSwitchActive = true;
        potFateSupportJobRecoveryPending = true;
        potFateSupportJobRestoring = false;
        potFateSupportJobRetry.Clear();
        DService.Instance().Log.Information(
            $"[KeitaToolbox.MagicPot] Resumed pending support job restoration; previous={previousJob.JobType}, target={targetJob.JobType}");
    }

    private bool ResolvePendingSupportJobRecovery(CrescentSupportJob? currentJob)
    {
        if (!potFateSupportJobRecoveryPending)
            return true;
        if (currentJob is null)
            return false;

        potFateSupportJobRecoveryPending = false;
        var targetJob = potFateTargetSupportJob;
        if (targetJob is not null && currentJob.JobType == targetJob.JobType)
        {
            potFateTargetJobConfirmed = true;
            return true;
        }

        DService.Instance().Log.Information(
            $"[KeitaToolbox.MagicPot] Cleared pending support job restoration because the target job is no longer active; current={currentJob.JobType}, target={targetJob?.JobType}");
        ClearPotFateSupportJobSwitch();
        return false;
    }

    private void RememberPendingSupportJobRestore(CrescentSupportJob previousJob, CrescentSupportJob targetJob)
    {
        var jobType = (int)previousJob.JobType;
        var targetJobType = (int)targetJob.JobType;
        if (config.PendingSupportJobRestore == jobType &&
            config.PendingSupportJobTarget == targetJobType)
            return;

        config.PendingSupportJobRestore = jobType;
        config.PendingSupportJobTarget = targetJobType;
        config.Save(this);
    }

    private void ClearPendingSupportJobRestore()
    {
        if (config.PendingSupportJobRestore < 0 && config.PendingSupportJobTarget < 0)
            return;

        config.PendingSupportJobRestore = -1;
        config.PendingSupportJobTarget = -1;
        config.Save(this);
    }

    private void ClearPotFateSupportJobSwitch()
    {
        potFatePreviousSupportJob = null;
        potFateTargetSupportJob = null;
        potFateSupportJobSwitchActive = false;
        potFateTargetJobConfirmed = false;
        potFateSupportJobRestoring = false;
        potFateSupportJobRecoveryPending = false;
        potFateSupportJobRetry.Clear();
        ClearPendingSupportJobRestore();
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

    #region Map marker logic

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

        var cofferMatch = CofferCountRegex.Match(line);
        if (cofferMatch.Success &&
            int.TryParse(cofferMatch.Groups[1].Value, out var silverChests) &&
            int.TryParse(cofferMatch.Groups[2].Value, out var bronzeChests))
            CaptureCofferHuntScan(bronzeChests, silverChests);

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
        client.DefaultRequestHeaders.Add("User-Agent",    "KeitaToolbox-MagicPot");
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
        public Vector3 FateCenter;
        public float   FateRadius;
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
        public bool      AutoSwitchToNinjaDuringPotFate;
        public PotFateSupportJobTarget PotFateSupportJobTarget;
        public int       PendingSupportJobRestore = -1;
        public int       PendingSupportJobTarget = -1;
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
        public bool      ReenterIslandWhenTimeLow;
        public bool      AutoDeclineInvite;


        [JsonProperty("EnableBocchiHunt")]
        public bool      EnableCofferHunt;
        public CofferHuntExecutor CofferHuntExecutor;
        public CofferHuntHandoffMode CofferHuntHandoffMode;
        public uint      CofferHuntSouthPreferredAetheryteDataID;
        public uint      CofferHuntNorthPreferredAetheryteDataID = CofferHuntNorthInitialPreferredAetheryteDataID;

        public bool      EnableAutoRevive;
        public bool      AutoRevivePartyOnly = true;
        public bool      AutoAcceptRaise;
        public bool      EnableAutoCurrencyExchange;
        public CurrencyExchangeReward CurrencyExchangeReward = CurrencyExchangeReward.UltimateFixative;

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
    private static partial class BocchiAutomator
    {
        public static string? TryEmergencyStop()
        {
            try
            {
                var bocchi = ResolvePlugin();
                if (bocchi == null) return null;

                var automatorModule = ResolveModule(bocchi, "BOCCHI.Modules.Automator.AutomatorModule");
                if (automatorModule == null) return null;

                const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var moduleType = automatorModule.GetType();
                var emergencyStop = moduleType.GetMethod(
                                        "RequestStopAll",
                                        bf,
                                        null,
                                        Type.EmptyTypes,
                                        null) ??
                                    moduleType.GetMethod(
                                        "DisableIllegalMode",
                                        bf,
                                        null,
                                        Type.EmptyTypes,
                                        null);
                if (emergencyStop == null) return null;

                emergencyStop.Invoke(automatorModule, null);
                return emergencyStop.Name;
            }
            catch (Exception ex)
            {
                DService.Instance().Log.Warning(
                    $"[KeitaToolbox.MagicPot] BOCCHI stop request failed: {ex.GetType().Name}");
                return null;
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

        public static bool TryStartTreasureHunter(out string result)
        {
            result = "BOCCHI 或宝箱模块不可用";
            try
            {
                var bocchi = ResolvePlugin();
                if (bocchi == null) return false;

                var treasureModule = ResolveModule(bocchi, "BOCCHI.Modules.Treasure.TreasureModule");
                var hunter = treasureModule == null ? null : GetMember(treasureModule, "hunter");
                if (hunter == null) return false;

                const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var runningField = FindField(hunter.GetType(), "running", bf);
                var stopwatch = GetMember(hunter, "stopwatch") as System.Diagnostics.Stopwatch;
                if (runningField == null || stopwatch == null)
                {
                    result = "BOCCHI 宝箱猎人接口不兼容";
                    return false;
                }

                if (runningField.GetValue(hunter) is true)
                {
                    result = "already running";
                    return true;
                }

                runningField.SetValue(hunter, true);
                stopwatch.Restart();
                result = "started";
                return true;
            }
            catch (Exception ex)
            {
                result = ex.GetType().Name;
                DService.Instance().Log.Warning(
                    $"[KeitaToolbox.MagicPot] BOCCHI treasure hunter start failed: {result}");
                return false;
            }
        }

        public static bool TryGetTreasureHunterRunning(out bool running)
        {
            running = false;
            try
            {
                var bocchi = ResolvePlugin();
                var treasureModule = bocchi == null
                                         ? null
                                         : ResolveModule(bocchi, "BOCCHI.Modules.Treasure.TreasureModule");
                var hunter = treasureModule == null ? null : GetMember(treasureModule, "hunter");
                if (hunter == null) return false;

                const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var runningField = FindField(hunter.GetType(), "running", bf);
                if (runningField?.GetValue(hunter) is not bool value) return false;

                running = value;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryStopTreasureHunter(out string result)
        {
            result = "BOCCHI 或宝箱模块不可用";
            try
            {
                var bocchi = ResolvePlugin();
                var treasureModule = bocchi == null
                                         ? null
                                         : ResolveModule(bocchi, "BOCCHI.Modules.Treasure.TreasureModule");
                var hunter = treasureModule == null ? null : GetMember(treasureModule, "hunter");
                if (hunter == null) return false;

                const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var teardown = hunter.GetType().GetMethod("Teardown", bf, null, Type.EmptyTypes, null);
                if (teardown == null)
                {
                    result = "BOCCHI 宝箱猎人接口不兼容";
                    return false;
                }

                teardown.Invoke(hunter, null);
                result = "stopped";
                return true;
            }
            catch (Exception ex)
            {
                result = ex.GetType().Name;
                DService.Instance().Log.Warning(
                    $"[KeitaToolbox.MagicPot] BOCCHI treasure hunter stop failed: {result}");
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

        private static FieldInfo? FindField(Type type, string name, BindingFlags flags)
        {
            for (var current = type; current != null; current = current.BaseType)
                if (current.GetField(name, flags) is { } field)
                    return field;

            return null;
        }
    }
}
