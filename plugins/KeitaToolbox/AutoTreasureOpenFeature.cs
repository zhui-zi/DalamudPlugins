using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools.Info.Game.Packets.Upstream;

namespace KeitaToolbox;

internal sealed unsafe class AutoTreasureOpenFeature : IDisposable
{
    private enum OpenPhase
    {
        Idle,
        MoveDelay,
        ReturnDelay,
        RetryDelay,
    }

    private OpenPhase phase;
    private ulong treasureId;
    private Vector3 treasurePosition;
    private Vector3 originalPosition;
    private float originalRotation;
    private uint operationTerritory;
    private int attempts;
    private long phaseDeadline;
    private long nextScanAt;
    private long lastCombatAt;
    private long nextReturnReinforcementAt;

    public AutoTreasureOpenFeature()
    {
        var now = Environment.TickCount64;
        lastCombatAt = now;
        nextScanAt = now;
        Plugin.Framework.Update += OnFrameworkUpdate;
        Plugin.ClientState.TerritoryChanged += OnTerritoryChanged;
        Plugin.Log.Information("[AutoTreasureOpen] Initialized.");
    }

    public void Dispose()
    {
        Plugin.ClientState.TerritoryChanged -= OnTerritoryChanged;
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Abort(restorePosition: true);
        Plugin.Log.Information("[AutoTreasureOpen] Disposed.");
    }

    private void OnTerritoryChanged(uint _)
    {
        Abort(restorePosition: false);
        lastCombatAt = Environment.TickCount64;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        try
        {
            Update();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[AutoTreasureOpen] Update failed.");
            Abort(restorePosition: true);
        }
    }

    private void Update()
    {
        var now = Environment.TickCount64;
        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            lastCombatAt = now;
            Abort(restorePosition: true);
            return;
        }

        if (!IsContextReady(now))
        {
            Abort(restorePosition: true);
            return;
        }

        if (phase != OpenPhase.Idle)
        {
            UpdateOperation(now);
            return;
        }

        if (now < nextScanAt)
            return;

        nextScanAt = now + Math.Max(50, Plugin.Config.Duty.TreasureOpenCheckIntervalMs);
        if (TryFindNearestTreasure(Plugin.Config.Duty.TreasureOpenMaxDistance, out var candidate))
            BeginOpen(candidate, now);
    }

    private bool IsContextReady(long now)
    {
        var condition = Plugin.Condition;
        var occupied =
            condition[ConditionFlag.BetweenAreas] ||
            condition[ConditionFlag.BetweenAreas51] ||
            condition[ConditionFlag.Occupied] ||
            condition[ConditionFlag.Occupied30] ||
            condition[ConditionFlag.OccupiedInEvent] ||
            condition[ConditionFlag.OccupiedInQuestEvent] ||
            condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            condition[ConditionFlag.Casting] ||
            condition[ConditionFlag.WatchingCutscene] ||
            condition[ConditionFlag.WatchingCutscene78];
        var boundByDuty = condition[ConditionFlag.BoundByDuty] ||
                          condition[ConditionFlag.BoundByDuty56];
        var player = Plugin.ObjectTable.LocalPlayer;
        return AutoTreasureOpenPolicy.IsReady(
            Plugin.Config.Features.AutoTreasureOpen,
            boundByDuty,
            Plugin.Config.Duty.TreasureOpenSoloModeOnly,
            Plugin.PartyList.Length,
            Plugin.ClientState.IsLoggedIn && player is { IsDead: false },
            condition[ConditionFlag.InCombat],
            occupied,
            now - lastCombatAt,
            Math.Max(0, Plugin.Config.Duty.TreasureOpenPostCombatCooldownMs));
    }

    private void BeginOpen(TreasureCandidate candidate, long now)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return;

        treasureId = candidate.GameObjectId;
        treasurePosition = candidate.Position;
        originalPosition = player.Position;
        originalRotation = player.Rotation;
        operationTerritory = Plugin.ClientState.TerritoryType;
        attempts = 0;
        SendMove(treasurePosition);
        phase = OpenPhase.MoveDelay;
        phaseDeadline = now + Math.Max(0, Plugin.Config.Duty.TreasureOpenMoveDelayMs);
        DebugLog($"Moving to treasure 0x{treasureId:X16} at {treasurePosition:F2}.");
    }

    private void UpdateOperation(long now)
    {
        if (Plugin.ClientState.TerritoryType != operationTerritory)
        {
            Abort(restorePosition: false);
            return;
        }

        if (phase == OpenPhase.RetryDelay &&
            Plugin.Config.Duty.TreasureOpenPreventPullback &&
            now >= nextReturnReinforcementAt &&
            now < phaseDeadline)
        {
            SendMove(originalPosition);
            nextReturnReinforcementAt = now + 100;
        }

        if (now < phaseDeadline)
            return;

        switch (phase)
        {
            case OpenPhase.MoveDelay:
                attempts++;
                new TreasureOpenPacket(treasureId).Send();
                phase = OpenPhase.ReturnDelay;
                phaseDeadline = now + Math.Max(0, Plugin.Config.Duty.TreasureOpenReturnDelayMs);
                DebugLog($"Opening treasure 0x{treasureId:X16}, attempt {attempts}.");
                break;
            case OpenPhase.ReturnDelay:
                SendMove(originalPosition);
                phase = OpenPhase.RetryDelay;
                phaseDeadline = now + Math.Max(50, Plugin.Config.Duty.TreasureOpenRetryDelayMs);
                nextReturnReinforcementAt = now + 100;
                break;
            case OpenPhase.RetryDelay:
                if (!IsTreasureUnopened(treasureId))
                {
                    Complete();
                    break;
                }

                if (attempts > Math.Max(0, Plugin.Config.Duty.TreasureOpenRetryCount))
                {
                    DebugLog($"Treasure 0x{treasureId:X16} did not open after {attempts} attempts.");
                    ResetOperation();
                    break;
                }

                SendMove(treasurePosition);
                phase = OpenPhase.MoveDelay;
                phaseDeadline = now + Math.Max(0, Plugin.Config.Duty.TreasureOpenMoveDelayMs);
                break;
        }
    }

    private void Complete()
    {
        Plugin.Config.Duty.TotalTreasureOpenCount++;
        Plugin.Config.Save();
        DebugLog($"Opened treasure 0x{treasureId:X16}.");
        if (Plugin.Config.Duty.ShowTreasureOpenNotification)
        {
            Plugin.Notifications.AddNotification(new Notification
            {
                Title = "Keita 工具箱",
                Content = "已自动开启宝箱。",
                Type = NotificationType.Success,
            });
        }
        ResetOperation();
    }

    private void Abort(bool restorePosition)
    {
        if (phase != OpenPhase.Idle &&
            restorePosition &&
            Plugin.ClientState.IsLoggedIn &&
            Plugin.ClientState.TerritoryType == operationTerritory &&
            !Plugin.Condition[ConditionFlag.BetweenAreas] &&
            !Plugin.Condition[ConditionFlag.BetweenAreas51])
        {
            SendMove(originalPosition);
        }
        ResetOperation();
    }

    private void ResetOperation()
    {
        phase = OpenPhase.Idle;
        treasureId = 0;
        treasurePosition = Vector3.Zero;
        originalPosition = Vector3.Zero;
        originalRotation = 0;
        operationTerritory = 0;
        attempts = 0;
        phaseDeadline = 0;
        nextReturnReinforcementAt = 0;
    }

    private void SendMove(Vector3 position) =>
        new PositionUpdateInstancePacket(
            originalRotation,
            position,
            PositionUpdateInstancePacket.MoveType.NormalMove0).Send();

    private static bool TryFindNearestTreasure(float maxDistance, out TreasureCandidate candidate)
    {
        candidate = default;
        var player = Plugin.ObjectTable.LocalPlayer;
        var manager = EventObjectManager.Instance();
        if (player == null || manager == null)
            return false;

        var maximumDistanceSquared = MathF.Max(0f, maxDistance) * MathF.Max(0f, maxDistance);
        var nearestDistanceSquared = maximumDistanceSquared;
        foreach (var pointer in manager->EventObjects)
        {
            var gameObject = pointer.Value;
            if (gameObject == null ||
                gameObject->ObjectKind != ObjectKind.Treasure ||
                !gameObject->GetIsTargetable())
            {
                continue;
            }

            var treasure = (Treasure*)gameObject;
            if (treasure->State != Treasure.TreasureState.Unopened ||
                (treasure->Flags & Treasure.TreasureFlags.Opened) != 0)
            {
                continue;
            }

            var position = new Vector3(
                gameObject->Position.X,
                gameObject->Position.Y,
                gameObject->Position.Z);
            var distanceSquared = Vector3.DistanceSquared(player.Position, position);
            if (distanceSquared > nearestDistanceSquared)
                continue;

            nearestDistanceSquared = distanceSquared;
            candidate = new TreasureCandidate(gameObject->GetGameObjectId(), position);
        }

        return candidate.GameObjectId != 0;
    }

    private static bool IsTreasureUnopened(ulong gameObjectId)
    {
        var manager = EventObjectManager.Instance();
        if (manager == null)
            return false;

        foreach (var pointer in manager->EventObjects)
        {
            var gameObject = pointer.Value;
            if (gameObject == null ||
                gameObject->ObjectKind != ObjectKind.Treasure ||
                (ulong)gameObject->GetGameObjectId() != gameObjectId)
            {
                continue;
            }

            var treasure = (Treasure*)gameObject;
            return treasure->State == Treasure.TreasureState.Unopened &&
                   (treasure->Flags & Treasure.TreasureFlags.Opened) == 0;
        }

        return false;
    }

    private static void DebugLog(string message)
    {
        if (Plugin.Config.Duty.ShowTreasureOpenDebugLog)
            Plugin.Log.Information("[AutoTreasureOpen] {Message}", message);
    }

    public void DrawSettings()
    {
        if (!ImGui.CollapsingHeader("自动开箱"))
            return;

        Plugin.DrawFeatureToggle(
            "自动开箱",
            Plugin.Config.Features.AutoTreasureOpen,
            value => Plugin.Config.Features.AutoTreasureOpen = value);
        Plugin.DrawHelp("仅在副本内脱战后，自动开启范围内尚未开启的宝箱并返回原位。");

        var soloOnly = Plugin.Config.Duty.TreasureOpenSoloModeOnly;
        if (ImGui.Checkbox("仅单人副本", ref soloOnly))
        {
            Plugin.Config.Duty.TreasureOpenSoloModeOnly = soloOnly;
            Plugin.Config.Save();
        }

        var maxDistance = Plugin.Config.Duty.TreasureOpenMaxDistance;
        if (ImGui.SliderFloat("最大距离（yalms）", ref maxDistance, 1f, 100f, "%.0f"))
        {
            Plugin.Config.Duty.TreasureOpenMaxDistance = maxDistance;
            Plugin.Config.Save();
        }

        DrawNonNegativeInput("扫描间隔（毫秒）", Plugin.Config.Duty.TreasureOpenCheckIntervalMs,
            value => Plugin.Config.Duty.TreasureOpenCheckIntervalMs = value);
        DrawNonNegativeInput("脱战等待（毫秒）", Plugin.Config.Duty.TreasureOpenPostCombatCooldownMs,
            value => Plugin.Config.Duty.TreasureOpenPostCombatCooldownMs = value);
        DrawNonNegativeInput("靠近后延迟（毫秒）", Plugin.Config.Duty.TreasureOpenMoveDelayMs,
            value => Plugin.Config.Duty.TreasureOpenMoveDelayMs = value);
        DrawNonNegativeInput("开箱后返回延迟（毫秒）", Plugin.Config.Duty.TreasureOpenReturnDelayMs,
            value => Plugin.Config.Duty.TreasureOpenReturnDelayMs = value);
        DrawNonNegativeInput("失败重试次数", Plugin.Config.Duty.TreasureOpenRetryCount,
            value => Plugin.Config.Duty.TreasureOpenRetryCount = value);
        DrawNonNegativeInput("重试间隔（毫秒）", Plugin.Config.Duty.TreasureOpenRetryDelayMs,
            value => Plugin.Config.Duty.TreasureOpenRetryDelayMs = value);

        var preventPullback = Plugin.Config.Duty.TreasureOpenPreventPullback;
        if (ImGui.Checkbox("抑制位置回拉", ref preventPullback))
        {
            Plugin.Config.Duty.TreasureOpenPreventPullback = preventPullback;
            Plugin.Config.Save();
        }

        var showNotification = Plugin.Config.Duty.ShowTreasureOpenNotification;
        if (ImGui.Checkbox("显示开箱通知", ref showNotification))
        {
            Plugin.Config.Duty.ShowTreasureOpenNotification = showNotification;
            Plugin.Config.Save();
        }

        var debugLog = Plugin.Config.Duty.ShowTreasureOpenDebugLog;
        if (ImGui.Checkbox("记录调试日志", ref debugLog))
        {
            Plugin.Config.Duty.ShowTreasureOpenDebugLog = debugLog;
            Plugin.Config.Save();
        }

        Plugin.DrawDisabledWrapped($"累计自动开箱：{Plugin.Config.Duty.TotalTreasureOpenCount}");
    }

    private static void DrawNonNegativeInput(string label, int current, Action<int> setter)
    {
        var value = current;
        if (!ImGui.InputInt(label, ref value))
            return;

        setter(Math.Max(0, value));
        Plugin.Config.Save();
    }

    private readonly record struct TreasureCandidate(ulong GameObjectId, Vector3 Position);
}
