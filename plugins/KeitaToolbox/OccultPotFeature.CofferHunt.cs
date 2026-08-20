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
    #region Coffer hunt between Magic Pot cycles

    private void CaptureCofferHuntScan(int bronzeChests, int silverChests)
    {
        if (!config.EnableCofferHunt || !InOccultMapZone || InForkedTower) return;

        cofferHuntScanPending  = true;
        cofferHuntScanBronze   = bronzeChests;
        cofferHuntScanSilver   = silverChests;
        cofferHuntScanExpireAt = Environment.TickCount64 + CofferHuntScanTimeoutMS;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var remaining = nextSpawnTime > 0 ? $"{nextSpawnTime - now}s" : "unknown";
        DService.Instance().Log.Information(
            $"[KeitaToolbox.MagicPot] Treasure Sight scan received: " +
            $"silver={silverChests}, bronze={bronzeChests}, Magic Pot remaining={remaining}");
    }

    private void DriveCofferHuntFromTreasureScan(long nowUnix)
    {
        if (!config.EnableCofferHunt || !InOccultMapZone || InForkedTower)
        {
            ClearPendingCofferHuntScan();
            return;
        }

        if (!cofferHuntScanPending) return;

        var nowTick = Environment.TickCount64;
        if (nowTick >= cofferHuntScanExpireAt)
        {
            ClearPendingCofferHuntScan();
            return;
        }

        if (cofferHuntScanSilver < CofferHuntSilverCap && cofferHuntScanBronze < CofferHuntBronzeCap)
        {
            ClearPendingCofferHuntScan();
            return;
        }

        if (nextSpawnTime <= 0) return;
        if (nextSpawnTime - nowUnix <= CofferHuntRequiredLeadSeconds)
        {
            ClearPendingCofferHuntScan();
            return;
        }

        if (autoDigActive || cofferHuntActive || crossingDC || standbyDeathReturning) return;

        var bronzeChests = cofferHuntScanBronze;
        var silverChests = cofferHuntScanSilver;
        ClearPendingCofferHuntScan();

        autoDigTask ??= new();
        autoDigTask.Abort();
        autoDigActive = true;
        autoDigStatus = $"宝箱数量满足：青铜 {bronzeChests} / 白银 {silverChests}";
        SendCommand("/bocchiillegal off");
        NotifyHelper.Instance().NotificationInfo(
            $"宝箱达到上限，开始自动寻宝：白银 {silverChests}、青铜 {bronzeChests}");
        StartCofferHunt();
    }

    private void ClearPendingCofferHuntScan()
    {
        cofferHuntScanPending  = false;
        cofferHuntScanBronze   = 0;
        cofferHuntScanSilver   = 0;
        cofferHuntScanExpireAt = 0;
    }

    private void ManualStartCofferHunt()
    {
        if (!InOccultMapZone || autoDigActive || cofferHuntActive || undergroundTestActive) return;
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return;

        autoDigTask ??= new();
        autoDigTask.Abort();
        autoDigActive                 = true;
        cofferHuntActive              = true;
        cofferHuntTerritory           = GameState.TerritoryType;
        drHuntStarted                 = false;
        SelectRandomDrCofferHuntRoute();
        cofferHuntStartedAt           = Environment.TickCount64;
        var routeName                 = drOuterRouteActive ? "外环" : "内环";
        autoDigStatus                 = $"DR {routeName}等待启动确认";
        pendingCofferHuntAutoDigFor    = -1;

        EndBocchiReturnSuppression();
        VnavStop();
        var bocchiStopMode = EmergencyStopBocchi();
        var routeStartPosition = localPlayer.Position.ToVector2();
        SendDrCofferHuntStartCommand();
        autoDigTask.Enqueue(WaitDrCofferHuntStarted(
            () => routeStartPosition,
            10_000,
            () => true));
        autoDigTask.Enqueue(() =>
        {
            if (drHuntStarted) return true;

            SendCommand("/pdr ptreasure abort");
            ClearCofferHuntState();
            BocchiOn();
            FinishAutoDig();
            autoDigStatus = $"DR {routeName}寻宝未能从当前位置启动";
            NotifyHelper.Instance().NotificationWarning(autoDigStatus);
            return true;
        });
        DService.Instance().Log.Information(
            $"[KeitaToolbox.MagicPot] Manual DailyRoutines {routeName} treasure hunt dispatched at {localPlayer.Position:F2}; " +
            $"BOCCHI stop={bocchiStopMode}");
    }

    private void StopAutoDigManually()
    {
        AbortAutoDig();
        ResetAutoDigTravelRetry();
        if (nextSpawnTime > 0) autoDigStartedFor = nextSpawnTime;
    }

    private void StartCofferHunt()
    {
        if (autoDigTask == null) return;

        cofferHuntActive    = true;
        cofferHuntTerritory = GameState.TerritoryType;
        drHuntStarted       = false;
        SelectRandomDrCofferHuntRoute();
        EndBocchiReturnSuppression();
        StartDrCofferHunt();
    }

    private void SelectRandomDrCofferHuntRoute()
    {
        drOuterRouteActive = Random.Shared.Next(2) == 1;
        DService.Instance().Log.Information(
            $"[KeitaToolbox.MagicPot] DailyRoutines treasure route selected: " +
            $"{(drOuterRouteActive ? "outer" : "inner")}");
    }

    private void StartDrCofferHunt()
    {
        if (autoDigTask == null) return;

        var routeName = drOuterRouteActive ? "外环" : "内环";
        var preferredAetheryteDataID = GetPreferredDrAetheryteDataID(cofferHuntTerritory);
        var candidates = GetShuffledDrAetherytes(cofferHuntTerritory, preferredAetheryteDataID);
        if (candidates.Count == 0)
        {
            ClearCofferHuntState();
            NotifyHelper.Instance().NotificationWarning($"当前区域没有可用于 DR {routeName}寻宝的非初始点魔路水晶");
            EnqueueReturnStandby();
            return;
        }

        var basePosition = GetCofferHuntBasePosition(cofferHuntTerritory);
        autoDigStatus = $"DR {routeName}准备：返回初始点";
        autoDigTask.Enqueue(() =>
        {
            var bocchiStopMode = EmergencyStopBocchi();
            DService.Instance().Log.Information(
                $"[KeitaToolbox.MagicPot] DR treasure hunt preparation; BOCCHI stop={bocchiStopMode}");
            return true;
        });
        autoDigTask.Enqueue(() => { UseReturn(); return true; });
        autoDigTask.Enqueue(WaitDrReturnToBase(basePosition, 15000));

        autoDigTask.Enqueue(() =>
        {
            autoDigStatus = $"DR {routeName}准备：前往初始点水晶";
            VnavMoveTo(basePosition);
            return true;
        });
        autoDigTask.Enqueue(WaitArrive(basePosition, 3f, 20000));
        autoDigTask.Enqueue(() => { VnavStop(); return true; });
        autoDigTask.DelayNext(800);

        for (var i = 0; i < candidates.Count; i++)
            EnqueueDrCofferHuntAttempt(candidates[i], i + 1, candidates.Count);

        autoDigTask.Enqueue(() =>
        {
            if (drHuntStarted) return true;

            SendCommand("/pdr ptreasure abort");
            ClearCofferHuntState();
            NotifyHelper.Instance().NotificationWarning($"DR {routeName}寻宝未能启动：已尝试所有非初始点魔路水晶");
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

    private void EnqueueDrCofferHuntAttempt(CrescentAetheryte aetheryte, int attempt, int attemptCount)
    {
        if (autoDigTask == null) return;
        var commandIssued             = false;
        var roadFound                 = false;
        var roadPosition              = Vector3.Zero;
        var teleportRequestAccepted   = false;
        var teleportStarted           = false;
        var routeStartPosition        = Vector2.Zero;

        // CrescentAetheryte.Position is the teleport landing point, not the crystal object.
        // Re-acquire the current crystal before every hop because a landing point can sit outside interaction range.
        autoDigTask.Enqueue(WaitFindNearbyAethernetWhen(
            () => !drHuntStarted,
            position =>
            {
                roadFound    = true;
                roadPosition = position;
                autoDigStatus =
                    $"DR {(drOuterRouteActive ? "外环" : "内环")}准备 {attempt}/{attemptCount}：靠近当前魔路水晶";
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
                autoDigStatus =
                    $"DR {(drOuterRouteActive ? "外环" : "内环")}跳过 {attempt}/{attemptCount}：未走到当前魔路水晶交互范围";
                LogDrCofferHuntCandidateSkipped(aetheryte, attempt, attemptCount, "current crystal unavailable");
                return true;
            }

            autoDigStatus =
                $"DR {(drOuterRouteActive ? "外环" : "内环")}准备 {attempt}/{attemptCount}：打开当前魔路水晶菜单";
            return true;
        });
        autoDigTask.Enqueue(WaitAethernetMenuOpenWhen(
            () => !drHuntStarted && roadFound && InAethernetInteractionRange(roadPosition),
            5000));
        autoDigTask.Enqueue(() =>
        {
            if (drHuntStarted) return true;

            autoDigStatus =
                $"DR {(drOuterRouteActive ? "外环" : "内环")}准备 {attempt}/{attemptCount}：请求传送至{aetheryte.Name}";
            teleportRequestAccepted = TryAethernetTeleportFromOpenMenu(aetheryte);
            if (!teleportRequestAccepted && GetLifestreamActiveCustomAetheryte() != 0)
                teleportRequestAccepted = TryLifestreamAethernetTeleport(aetheryte.DataID);

            if (!teleportRequestAccepted)
            {
                autoDigStatus =
                    $"DR {(drOuterRouteActive ? "外环" : "内环")}跳过 {attempt}/{attemptCount}：未能传送至{aetheryte.Name}";
                LogDrCofferHuntCandidateSkipped(aetheryte, attempt, attemptCount, "teleport request failed");
            }
            else
            {
                autoDigStatus =
                    $"DR {(drOuterRouteActive ? "外环" : "内环")}准备 {attempt}/{attemptCount}：等待{aetheryte.Name}传送响应";
            }
            return true;
        });
        autoDigTask.Enqueue(WaitAethernetTeleportStartedWhen(
            () => !drHuntStarted && teleportRequestAccepted,
            () => aetheryte.Position,
            50f,
            AethernetTeleportStartTimeoutMS,
            () =>
            {
                teleportStarted = true;
                autoDigStatus = Arrived(aetheryte.Position, 50f)
                                    ? $"DR {(drOuterRouteActive ? "外环" : "内环")}准备 {attempt}/{attemptCount}：已抵达{aetheryte.Name}"
                                    : $"DR {(drOuterRouteActive ? "外环" : "内环")}准备 {attempt}/{attemptCount}：正在传送至{aetheryte.Name}";
            },
            () =>
            {
                VnavStop();
                autoDigStatus =
                    $"DR {(drOuterRouteActive ? "外环" : "内环")}跳过 {attempt}/{attemptCount}：传送请求未生效";
                LogDrCofferHuntCandidateSkipped(aetheryte, attempt, attemptCount, "teleport did not start");
            }));
        autoDigTask.Enqueue(WaitArriveUnlessDrStarted(
            aetheryte.Position,
            50f,
            20000,
            () => teleportStarted));
        autoDigTask.DelayNext(800);
        autoDigTask.Enqueue(() =>
        {
            if (drHuntStarted) return true;
            if (!teleportRequestAccepted || !teleportStarted) return true;
            if (!Arrived(aetheryte.Position, 50f))
            {
                autoDigStatus =
                    $"DR {(drOuterRouteActive ? "外环" : "内环")}跳过 {attempt}/{attemptCount}：未到达{aetheryte.Name}";
                LogDrCofferHuntCandidateSkipped(aetheryte, attempt, attemptCount, "arrival timeout");
                return true;
            }

            var nearbyPlayers = CountNearbyOtherPlayers(CofferHuntPlayerAvoidanceRadius);
            if (nearbyPlayers != 0)
            {
                var nearbyStatus = nearbyPlayers < 0 ? "角色状态不可用" : $"周围 {nearbyPlayers} 人";
                var nearbyReason = nearbyPlayers < 0 ? "local player unavailable" : $"{nearbyPlayers} nearby player(s)";
                autoDigStatus =
                    $"DR {(drOuterRouteActive ? "外环" : "内环")}跳过 {attempt}/{attemptCount}：{aetheryte.Name}{nearbyStatus}";
                LogDrCofferHuntCandidateSkipped(
                    aetheryte,
                    attempt,
                    attemptCount,
                    nearbyReason);
                return true;
            }

            autoDigStatus = $"DR {(drOuterRouteActive ? "外环" : "内环")}启动：{aetheryte.Name}";
            routeStartPosition = DService.Instance().ObjectTable.LocalPlayer?.Position.ToVector2() ??
                                 aetheryte.Position.ToVector2();
            SendDrCofferHuntStartCommand();
            commandIssued = true;
            return true;
        });
        autoDigTask.Enqueue(WaitDrCofferHuntStarted(
            () => routeStartPosition,
            10_000,
            () => commandIssued,
            () => RememberSuccessfulDrAetheryte(aetheryte)));
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
        var routeAliases = drOuterRouteActive
                               ? DrOuterLoopRouteAliases
                               : DrInnerLoopRouteAliases;
        foreach (var routeAlias in routeAliases)
            SendCommand($"/pdr ptreasure {routeAlias}");

        DService.Instance().Log.Information(
            $"[KeitaToolbox.MagicPot] DailyRoutines treasure route dispatched: {(drOuterRouteActive ? "outer" : "inner")}");
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

    private Func<bool?> WaitDrCofferHuntStarted(
        Func<Vector2> getOrigin,
        int timeoutMs,
        Func<bool> commandIssued,
        Action? onStarted = null)
    {
        long deadline = 0;
        DrHuntStartConfirmation? confirmation = null;
        var vnavPath = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        var vnavUnavailable = false;
        return () =>
        {
            if (drHuntStarted) return true;
            if (!commandIssued()) return true;
            var now = Environment.TickCount64;
            if (deadline == 0)
            {
                deadline = now + timeoutMs;
                confirmation = new DrHuntStartConfirmation(
                    getOrigin(),
                    CofferHuntStartMinimumDistance,
                    CofferHuntStartHoldMS);
            }

            var player = DService.Instance().ObjectTable.LocalPlayer;
            bool? vnavPathRunning = null;
            if (!vnavUnavailable)
            {
                try
                {
                    vnavPathRunning = vnavPath.InvokeFunc();
                }
                catch
                {
                    vnavUnavailable = true;
                }
            }

            var betweenAreas = DService.Instance().Condition[ConditionFlag.BetweenAreas] ||
                               DService.Instance().Condition[ConditionFlag.BetweenAreas51];
            if (player is not null && confirmation!.Update(
                    now,
                    player.Position.ToVector2(),
                    betweenAreas,
                    vnavPathRunning,
                    IsDrTreasureMovementLocked()))
            {
                drHuntStarted       = true;
                cofferHuntStartedAt = now;
                autoDigStatus       = $"DR {(drOuterRouteActive ? "外环" : "内环")}寻宝中";
                onStarted?.Invoke();
                NotifyHelper.Instance().NotificationInfo($"DR {(drOuterRouteActive ? "外环" : "内环")}寻宝已启动");
                return true;
            }

            return now >= deadline;
        };
    }

    private static unsafe bool IsDrTreasureMovementLocked()
    {
        var playerController = PlayerController.Instance();
        return playerController != null && playerController->MoveControllerWalk.IsMovementInputLocked;
    }

    private static int CountNearbyOtherPlayers(float radius)
    {
        if (DService.Instance().ObjectTable.LocalPlayer is not { } localPlayer) return -1;
        var radiusSquared = radius * radius;
        var count = 0;

        foreach (var obj in DService.Instance().ObjectTable)
        {
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc ||
                obj.GameObjectID == localPlayer.GameObjectID)
                continue;

            var deltaX = obj.Position.X - localPlayer.Position.X;
            var deltaZ = obj.Position.Z - localPlayer.Position.Z;
            if ((deltaX * deltaX) + (deltaZ * deltaZ) <= radiusSquared)
                count++;
        }

        return count;
    }

    private static List<CrescentAetheryte> GetShuffledDrAetherytes(uint territory, uint preferredDataID)
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

        var preferredIndex = result.FindIndex(x => x.DataID == preferredDataID);
        if (preferredIndex > 0)
        {
            var preferred = result[preferredIndex];
            result.RemoveAt(preferredIndex);
            result.Insert(0, preferred);
        }

        return result;
    }

    private uint GetPreferredDrAetheryteDataID(uint territory) =>
        territory == OccultTerritory
            ? config.CofferHuntSouthPreferredAetheryteDataID
            : config.CofferHuntNorthPreferredAetheryteDataID;

    private void RememberSuccessfulDrAetheryte(CrescentAetheryte aetheryte)
    {
        ref var preferredDataID = ref cofferHuntTerritory == OccultTerritory
                                      ? ref config.CofferHuntSouthPreferredAetheryteDataID
                                      : ref config.CofferHuntNorthPreferredAetheryteDataID;
        if (preferredDataID == aetheryte.DataID) return;

        preferredDataID = aetheryte.DataID;
        config.Save(this);
        DService.Instance().Log.Information(
            $"[KeitaToolbox.MagicPot] Remembered DR treasure start aetheryte: " +
            $"territory={cofferHuntTerritory}, name={aetheryte.Name}, dataID={aetheryte.DataID}");
    }

    private void LogDrCofferHuntCandidateSkipped(
        CrescentAetheryte aetheryte,
        int attempt,
        int attemptCount,
        string reason) =>
        DService.Instance().Log.Information(
            $"[KeitaToolbox.MagicPot] DR treasure start candidate skipped: " +
            $"route={(drOuterRouteActive ? "outer" : "inner")}, attempt={attempt}/{attemptCount}, " +
            $"name={aetheryte.Name}, dataID={aetheryte.DataID}, reason={reason}");

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

        var completedRoute = drOuterRouteActive ? "outer" : "inner";
        var now       = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var remaining = nextSpawnTime - now;
        pendingCofferHuntAutoDigFor = displayPot != null && nextSpawnTime > 0 &&
                                      remaining <= CofferHuntStopLeadSeconds
                                          ? nextSpawnTime
                                          : -1;

        DService.Instance().Log.Information(
            $"[KeitaToolbox.MagicPot] DailyRoutines {completedRoute} treasure route completed");
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
        drOuterRouteActive  = false;
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
}
