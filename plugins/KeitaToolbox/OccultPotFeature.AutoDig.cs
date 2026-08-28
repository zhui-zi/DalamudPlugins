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

internal sealed partial class OccultPotFeature
{
    #region Automatic Magic Pot routing



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
            $"[KeitaToolbox.MagicPot] Magic Pot FATE 0x{target.FateID:X} ended with lure; post-FATE auto-dig started");
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

        if (autoDigRetryFor > 0 && autoDigRetryFor != nextSpawnTime)
            ResetAutoDigTravelRetry();

        var cofferHuntHandoff = pendingCofferHuntAutoDigFor == nextSpawnTime;
        if (pendingCofferHuntAutoDigFor > 0 && !cofferHuntHandoff)
            pendingCofferHuntAutoDigFor = -1;
        if (autoDigRetryFor == nextSpawnTime && Environment.TickCount64 < autoDigRetryAt) return;
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
                    $"[KeitaToolbox.MagicPot] Magic Pot preparation armed; waiting for current FATE/CE to finish, " +
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
                    "[KeitaToolbox.MagicPot] Current FATE/CE completed; Magic Pot preparation is taking control");
            }

            autoDigBocchiStoppedFor = nextSpawnTime;
            autoDigBocchiTravelStopRetriedFor = -1;
            autoDigBocchiTravelStopRetryAt = Environment.TickCount64 + 1000;
            var bocchiStopMode = EmergencyStopBocchi();
            DService.Instance().Log.Information(
                $"[KeitaToolbox.MagicPot] Magic Pot preparation takeover; BOCCHI stop={bocchiStopMode}, remaining={remaining}s");
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
                var bocchiStopMode = EmergencyStopBocchi();
                DService.Instance().Log.Information(
                    $"[KeitaToolbox.MagicPot] Magic Pot preparation reclaimed a new BOCCHI FATE/CE trip; BOCCHI stop={bocchiStopMode}");
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
            autoDigStatus = config.AutoDigWaitForRescue ? "死亡，等待施救" : "死亡返回";
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
            autoDigStatus      = "死亡返回";
            NotifyDeath();
        }

        if (now < nextDeathReturnAttemptAt) return true;

        ExecuteCommandManager.Instance().ExecuteCommand(ExecuteCommandFlag.Revive, 8);
        nextDeathReturnAttemptAt = now + 1000;
        return true;
    }

    private void NotifyDeath()
    {
        NotifyHelper.Instance().NotificationInfo("检测到死亡，自动返回起始点…");
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
                autoDigStatus = "死亡返回";
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
        var standbyRadius = MagicPotStandbyPolicy.GetOffsetRadius(
            target.World,
            target.FateCenter,
            target.FateRadius);
        EnqueueMoveTo(
            RandomOffset(target.World, standbyRadius),
            MagicPotStandbyPolicy.ArrivalTolerance,
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
                if (DangerZoneHandling is DangerZoneHandlingMode.Manual or DangerZoneHandlingMode.Skip)
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
                timeoutMs: territory == OccultNorthTerritory ? 240000 : 90000,
                avoidNorthHornAggro: territory == OccultNorthTerritory);
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
                    var from = DService.Instance().ObjectTable.LocalPlayer?.Position ?? positions[index];
                    var previousRemaining = positions[(index + 1)..];
                    var refined = OccultData.RefinePositionsByDirection(
                        previousRemaining,
                        from,
                        digDirection);

                    if (refined.Length != 0)
                    {
                        nextDirection = digDirection;
                        remaining = OrderDigPositions(
                            territory,
                            regionKey,
                            digDirection,
                            refined,
                            from);
                        autoDigStatus = $"宝箱在{digDirection}方向，继续定位";
                        DService.Instance().Log.Information(
                            $"[KeitaToolbox.MagicPot] Refined remaining treasure candidates: " +
                            $"direction={digDirection}, before={previousRemaining.Length}, after={remaining.Length}");
                    }
                    else
                    {
                        remaining = OrderDigPositions(
                            territory,
                            regionKey,
                            direction,
                            previousRemaining,
                            from);
                        autoDigStatus = $"{digDirection}方向未匹配，保留剩余候选";
                        DService.Instance().Log.Warning(
                            $"[KeitaToolbox.MagicPot] Treasure direction matched no remaining candidates; " +
                            $"direction={digDirection}, retained={remaining.Length}");
                    }
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
                    if (DangerZoneHandling is DangerZoneHandlingMode.Ground or DangerZoneHandlingMode.Underground)
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
        else if (CrossDCRoutingPolicy.ShouldReenterIsland(
                     config.ReenterIslandWhenTimeLow,
                     config.EnableAutoCrossDC,
                     GetIslandTimeLeftSeconds()))
        {
            autoDigTask.Enqueue(() => EnqueueIslandReentry(targetTerritory));
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

    private bool EnqueueIslandReentry(uint territory)
    {
        if (autoDigTask == null) return true;

        var entryCommand = territory == OccultNorthTerritory ? "/pdrfe ocn" : "/pdrfe ocs";
        crossingDC    = true;
        autoDigStatus = "换岛：退出新月岛";
        NotifyHelper.Instance().NotificationInfo("岛内剩余不足 90 分钟，准备自动换岛");

        autoDigTask.Enqueue(() => SendCommand("/pdr leaveduty"));
        autoDigTask.Enqueue(WaitZone(territory, false, 20000));
        autoDigTask.Enqueue(() =>
        {
            if (GameState.TerritoryType == territory)
                return FailIslandReentry("退出新月岛失败，已停止自动换岛");

            autoDigStatus = "换岛：等待 30 秒";
            return true;
        });
        autoDigTask.Enqueue(PlayerReady);
        autoDigTask.DelayNext(30000);
        autoDigTask.Enqueue(() =>
        {
            autoDigStatus = "换岛：重新进入新月岛";
            SendCommand(entryCommand);
            return true;
        });
        autoDigTask.Enqueue(WaitZone(territory, true, 60000));
        autoDigTask.Enqueue(() =>
        {
            if (GameState.TerritoryType != territory)
                return FailIslandReentry("重新进入新月岛超时，已停止自动换岛");

            crossingDC = false;
            EndBocchiReturnSuppression();
            BocchiOn();
            NotifyHelper.Instance().NotificationInfo("自动换岛完成");
            FinishAutoDig();
            return true;
        });

        return true;
    }

    private bool FailIslandReentry(string message)
    {
        DService.Instance().Log.Warning($"[KeitaToolbox.MagicPot] {message}");
        AbortAutoDig();
        NotifyHelper.Instance().NotificationInfo(message);
        BocchiOn();
        return true;
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
        var islandTimeLeft = GetIslandTimeLeftSeconds();
        var forceTravel = CrossDCRoutingPolicy.ShouldForceTravel(islandTimeLeft);
        _ = CrossDCQueryAsync(territory, forceTravel, islandTimeLeft);
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

    private async Task CrossDCQueryAsync(uint territory, bool forceTravel, float? islandTimeLeft)
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
            var candidates = new List<CrossDCCandidate>();

            foreach (var row in rows)
            {
                var dc = (ushort)row.Datacenter;
                if (!CrossDCWorlds.ContainsKey(dc) || !seen.Add(dc)) continue;

                var remaining = PredictRemaining(row.PotHistory, now);
                candidates.Add(new CrossDCCandidate(dc, remaining));
            }

            var target = CrossDCRoutingPolicy.SelectTarget(currentDC, candidates, forceTravel);
            if (target is null)
            {
                CrossDCCandidate? bestOverall = null;
                var hasEligibleOther = false;
                foreach (var candidate in candidates)
                {
                    if (candidate.RemainingSeconds is <= 300 or long.MaxValue) continue;
                    if (candidate.DataCenter != currentDC) hasEligibleOther = true;
                    if (bestOverall is null || candidate.RemainingSeconds < bestOverall.Value.RemainingSeconds)
                        bestOverall = candidate;
                }

                crossDCReason = forceTravel && !hasEligibleOther
                                    ? $"岛内剩余 {Math.Floor(islandTimeLeft!.Value / 60)} 分钟，但其他大区均无 >5 分钟罐子"
                                    : seen.Count == 0
                                        ? "查询到 0 个大区数据"
                                        : bestOverall is null
                                            ? $"各大区罐子均 ≤5 分钟(已查{seen.Count}区)"
                                            : $"当前{currentDC}区罐子最近({bestOverall.Value.RemainingSeconds / 60}分),留守不跨";
                return;
            }

            crossDCTargetDC = target.Value.DataCenter;

            crossDCTargetWorld = target.Value.DataCenter == homeDC && !string.IsNullOrEmpty(homeWorld)
                                     ? homeWorld
                                     : CrossDCWorlds[target.Value.DataCenter];
            var forceReason = forceTravel
                                  ? $"岛内剩余 {Math.Floor(islandTimeLeft!.Value / 60)} 分钟，"
                                  : string.Empty;
            crossDCReason = $"{forceReason}→ {crossDCTargetWorld}({target.Value.RemainingSeconds / 60}分)";
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

    private static unsafe float? GetIslandTimeLeftSeconds()
    {
        var eventFramework = EventFramework.Instance();
        var contentDirector = eventFramework == null ? null : eventFramework->GetContentDirector();
        return contentDirector != null && contentDirector->ContentTimeLeft > 0
                   ? contentDirector->ContentTimeLeft
                   : null;
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


    private static string EmergencyStopBocchi()
    {
        var stopMode = BocchiAutomator.TryEmergencyStop();
        if (!string.IsNullOrEmpty(stopMode)) return stopMode;

        SendCommand("/bocchiillegal off");
        return "command";
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
            return true;
        });
        autoDigTask.Enqueue(WaitAethernetMenuOpenWhen(
            () => needsBaseApproach && baseRoadReady,
            5000));
        autoDigTask.Enqueue(() =>
        {
            if (!needsBaseApproach) return true;

            if (baseRoadReady)
            {
                baseTeleportStarted = TryAethernetTeleportFromOpenMenu(aetheryte);
                if (!baseTeleportStarted && GetLifestreamActiveCustomAetheryte() != 0)
                    baseTeleportStarted = TryLifestreamAethernetTeleport(aetheryte.DataID);
            }

            if (!baseTeleportStarted)
                NotifyHelper.Instance().NotificationWarning($"未能启动前往{aetheryte.Name}的魔路传送");
            return true;
        });
        autoDigTask.DelayNext(1000);
        autoDigTask.Enqueue(() => !needsBaseApproach || !baseTeleportStarted || PlayerReady());
        autoDigTask.Enqueue(WaitArriveWhen(
            () => needsBaseApproach && baseTeleportStarted,
            () => aetheryte.Position,
            50f,
            20000));
        autoDigTask.Enqueue(() =>
        {
            if (!needsBaseApproach || Arrived(aetheryte.Position, 50f)) return true;
            return FailAutoDigMovement($"未能传送至{aetheryte.Name}，已停止自动移动");
        });
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

    private static Func<bool?> WaitAethernetTeleportStartedWhen(
        Func<bool> enabled,
        Func<Vector3> destination,
        float tolerance,
        int timeoutMs,
        Action onStarted,
        Action onTimedOut)
    {
        long deadline = 0;
        return () =>
        {
            if (!enabled()) return true;

            if (Arrived(destination(), tolerance))
            {
                onStarted();
                return true;
            }

            var condition = DService.Instance().Condition;
            if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
            {
                onStarted();
                return true;
            }

            var now = Environment.TickCount64;
            if (deadline == 0) deadline = now + timeoutMs;
            if (now < deadline) return false;

            onTimedOut();
            return true;
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
        if (TryAethernetTeleportFromOpenMenu(aetheryte)) return true;
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
        if (agent == null || !agent->IsAgentActive() ||
            !AethernetMenuPolicy.TryGetCrescentMenuIndex(
                GameState.TerritoryType,
                aetheryte.DataID,
                aetheryte.Index,
                out var index))
            return false;

        agent->TeleportToAetheryte(index);
        return true;
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


    private void EnqueueMoveTo(
        Vector3 position,
        float tolerance,
        bool mount = true,
        int timeoutMs = 90000,
        bool avoidNorthHornAggro = false)
    {
        if (autoDigTask == null) return;

        if (mount)
            autoDigTask.Enqueue(WaitMounted());

        autoDigTask.Enqueue(() =>
        {
            VnavMoveTo(position);
            if (avoidNorthHornAggro)
                BeginNorthHornAggroAvoidance(position);
            return true;
        });
        autoDigTask.Enqueue(WaitArrive(position, tolerance, timeoutMs));
        autoDigTask.Enqueue(() =>
        {
            StopNorthHornAggroAvoidance();
            VnavStop();
            return true;
        });
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
                    $"[KeitaToolbox.MagicPot] Enter underground danger route: {position.X:F2}, {position.Y:F2}, {position.Z:F2}");
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
                    $"[KeitaToolbox.MagicPot] Smooth surface return started: Y={startPacketHeight:F2} -> {position.Y:F2}; " +
                    $"depth={position.Y - startPacketHeight:F2}");
            }

            if (now >= deadline)
                return FailAutoDigMovement("平滑返回地表超时，已停止遁地寻宝");

            if (!TryBeginUndergroundPositionUpdate(UndergroundReturnSpeed, out var maxStep))
                return false;

            maxStep = MathF.Min(maxStep, UndergroundReturnMaxStep);
            var currentPacketHeight = undergroundPacketHeight ?? startPacketHeight;
            var remainingHeight     = position.Y - currentPacketHeight;

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
                $"[KeitaToolbox.MagicPot] Smooth surface return completed: {position.X:F2}, {position.Y:F2}, {position.Z:F2}");
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



            if (now >= settleAfter && UndergroundArrived(position, tolerance)) return true;
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
        undergroundLastPositionUpdateAt = 0;
        autoDigStatus = "危险区：遁地移动";
        var playerController = PlayerController.Instance();
        if (playerController != null)
            playerController->MoveControllerWalk.IsMovementInputLocked = true;
    }

    private unsafe void EndUndergroundDangerMode()
    {
        if (undergroundDangerActive)
            DService.Instance().Log.Information("[KeitaToolbox.MagicPot] Leave underground danger route");
        undergroundDangerActive = false;
        allowUndergroundPositionUpdate = false;
        undergroundPacketHeight = null;
        undergroundSurfaceHeight = null;
        undergroundLastPositionUpdateAt = 0;
        var playerController = PlayerController.Instance();
        if (playerController != null)
            playerController->MoveControllerWalk.IsMovementInputLocked = false;
    }

    private static float GetUndergroundHeight(Vector3 surfacePosition) =>
        MathF.Max(surfacePosition.Y - UndergroundDepth, UndergroundMinHeight);

    private bool UndergroundArrived(Vector3 position, float tolerance)
    {
        if (!Arrived(position, tolerance) || undergroundPacketHeight is not { } packetHeight)
            return false;

        return MathF.Abs(packetHeight - GetUndergroundHeight(position)) <= UndergroundReturnTolerance;
    }

    private bool TryBeginUndergroundPositionUpdate(float speed, out float step)
    {
        var now = Environment.TickCount64;
        var elapsedMs = undergroundLastPositionUpdateAt == 0
                            ? UndergroundPositionUpdateIntervalMS
                            : now - undergroundLastPositionUpdateAt;
        if (elapsedMs < UndergroundPositionUpdateIntervalMS)
        {
            step = 0f;
            return false;
        }

        undergroundLastPositionUpdateAt = now;
        elapsedMs = Math.Min(elapsedMs, UndergroundPositionUpdateMaxElapsedMS);
        step = speed * elapsedMs / 1000f;
        return step > 0f;
    }

    private unsafe void MoveUndergroundTo(Vector3 position)
    {
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer)
            return;

        if (!TryBeginUndergroundPositionUpdate(UndergroundMoveSpeed, out var step))
            return;

        var playerController = PlayerController.Instance();
        if (playerController != null && playerController->MoveState == 3)
            playerController->MoveState = 1;

        var current = localPlayer.Position;
        var horizontalDelta = new Vector3(position.X, current.Y, position.Z) - current;
        var distance = horizontalDelta.Length();
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
        var packetHeightDelta = targetPacketHeight - currentPacketHeight;
        var nextPacketHeight = MathF.Abs(packetHeightDelta) <= step
                                   ? targetPacketHeight
                                   : currentPacketHeight + MathF.CopySign(step, packetHeightDelta);
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
                $"[KeitaToolbox.MagicPot] Enter underground test: {player.Position.X:F2}, {player.Position.Y:F2}, {player.Position.Z:F2}");
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
        DService.Instance().Log.Warning($"[KeitaToolbox.MagicPot] {message}");
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
                DService.Instance().Log.Information("[KeitaToolbox.MagicPot] Underground test restored surface position");
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
        long deadline        = 0;
        long blockedDeadline = 0;
        long nextTry         = 0;
        string blockedBy     = string.Empty;
        return () =>
        {
            var now = Environment.TickCount64;
            if (DService.Instance().Condition[ConditionFlag.Mounted]) return true;

            if (!InOccultMapZone || DService.Instance().ObjectTable.LocalPlayer is not { IsDead: false })
                return FailAutoDigMovement("角色状态异常，无法上坐骑，已停止自动移动");

            var condition = DService.Instance().Condition;
            var currentBlock = condition[ConditionFlag.BetweenAreas]
                                   ? "区域切换"
                                   : condition[ConditionFlag.OccupiedInQuestEvent]
                                       ? "事件交互"
                                       : condition[ConditionFlag.InCombat]
                                           ? "战斗"
                                           : condition.IsCasting
                                               ? "读条"
                                               : string.Empty;
            if (!string.IsNullOrEmpty(currentBlock))
            {
                deadline = 0;
                if (!string.Equals(blockedBy, currentBlock, StringComparison.Ordinal))
                {
                    blockedBy       = currentBlock;
                    blockedDeadline = now + MountBlockedTimeoutMS;
                    DService.Instance().Log.Information(
                        $"[KeitaToolbox.MagicPot] Mount wait blocked by {currentBlock}");
                }

                if (now >= blockedDeadline)
                    return FailAutoDigMovement($"等待{currentBlock}结束超时，无法上坐骑，已停止自动移动");
                return false;
            }

            if (!string.IsNullOrEmpty(blockedBy))
            {
                DService.Instance().Log.Information(
                    $"[KeitaToolbox.MagicPot] Mount wait resumed after {blockedBy}");
                blockedBy       = string.Empty;
                blockedDeadline = 0;
            }

            if (deadline == 0) deadline = now + timeoutMs;
            if (now >= deadline)
                return FailAutoDigMovement("坐骑动作连续失败，已停止自动移动");

            if (now < nextTry) return false;

            Mount();
            nextTry = now + 1500;
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
        var retryScheduled = TryScheduleAutoDigTravelRetry();
        DService.Instance().Log.Warning($"[KeitaToolbox.MagicPot] {message}");
        AbortAutoDig();
        if (retryScheduled)
        {
            autoDigStartedFor = -1;
            DService.Instance().Log.Information(
                $"[KeitaToolbox.MagicPot] Auto-dig travel retry scheduled in {AutoDigTravelRetryDelayMS}ms");
            NotifyHelper.Instance().NotificationInfo($"{message}，5 秒后重试");
        }
        else
        {
            NotifyHelper.Instance().NotificationInfo(message);
        }
        BocchiOn();
        return true;
    }

    private bool TryScheduleAutoDigTravelRetry()
    {
        if (!autoDigActive || !autoDigStatus.StartsWith("前往") || nextSpawnTime <= 0) return false;

        var remaining = nextSpawnTime - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (remaining is < 30 or > 300) return false;

        if (autoDigRetryFor != nextSpawnTime)
        {
            autoDigRetryFor   = nextSpawnTime;
            autoDigRetryCount = 0;
        }

        if (autoDigRetryCount >= AutoDigTravelRetryLimit) return false;

        autoDigRetryCount++;
        autoDigRetryAt = Environment.TickCount64 + AutoDigTravelRetryDelayMS;
        return true;
    }

    private void ResetAutoDigTravelRetry()
    {
        autoDigRetryFor   = -1;
        autoDigRetryCount = 0;
        autoDigRetryAt    = 0;
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
                    $"[KeitaToolbox.MagicPot] Magic Pot coffer opened: 0x{treasureEntityId:X8}");
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
                "[KeitaToolbox.MagicPot] Blocked dismount while underground danger route is active");
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
            $"[KeitaToolbox.MagicPot] Magic Pot coffer read-bar interaction requested: 0x{treasureEntityId:X8}");
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
                    $"[KeitaToolbox.MagicPot] Magic Pot coffer opened; returned underground to Y={undergroundPosition.Y:F0}");
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
        StopNorthHornAggroAvoidance();
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
        StopNorthHornAggroAvoidance();
        autoDigTask?.Abort();
        pendingCofferHuntAutoDigFor = -1;
        pendingPostFateAutoDigTarget = null;
        pendingPostFateAutoDigUntil = 0;
        ClearPendingCofferHuntScan();
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
}
