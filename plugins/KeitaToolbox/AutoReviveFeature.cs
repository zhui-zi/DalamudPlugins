using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Lumina.Excel.Sheets;
using ClientGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace KeitaToolbox;

internal sealed unsafe class AutoReviveFeature : IDisposable
{
    private const uint SouthHornTerritoryId = 1252;
    private const uint NorthHornTerritoryId = 1346;
    private const uint PhantomChemistReviveActionId = 41634;
    private const uint PhantomWhiteMageReviveActionId = 49070;
    private const byte PhantomChemistJobId = 10;
    private const byte PhantomWhiteMageJobId = 17;
    private const uint PhantomChemistStatusId = 4367;
    private const uint PhantomWhiteMageStatusId = 5329;
    private const uint RaiseStatusId = 148;
    private const uint AlternateRaiseStatusId = 1140;
    private const float ReviveRange = 30f;
    private const long UpdateIntervalMs = 200;
    private const long ReviveDelayMs = 1000;
    private const long ConfirmationTimeoutMs = 3000;
    private const long RetryDelayMs = 30000;
    private const long FailureLogIntervalMs = 10000;

    private readonly Dictionary<uint, long> retryAfter = [];
    private string runtimeStatus = "等待检测。";
    private uint pendingTargetEntityId;
    private long reviveAt;
    private uint confirmingTargetEntityId;
    private string confirmingTargetName = string.Empty;
    private long confirmUntil;
    private long nextUpdateAt;
    private long nextFailureLogAt;

    public AutoReviveFeature()
    {
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        Reset();
    }

    public void DrawSettings()
    {
        if (!ImGui.CollapsingHeader("自动复活"))
            return;

        Plugin.DrawFeatureToggle(
            "辅助职业自动复活",
            Plugin.Config.Features.OccultPotAutoRevive,
            value =>
            {
                Plugin.Config.Features.OccultPotAutoRevive = value;
                Reset();
            });

        using var disabled = ImRaii.Disabled(!Plugin.Config.Features.OccultPotAutoRevive);
        var partyOnly = Plugin.Config.OccultPot.AutoRevivePartyOnly;
        if (ImGui.RadioButton("仅同小队成员", partyOnly))
        {
            Plugin.Config.OccultPot.AutoRevivePartyOnly = true;
            Plugin.Config.Save();
            Reset();
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("周围所有玩家", !partyOnly))
        {
            Plugin.Config.OccultPot.AutoRevivePartyOnly = false;
            Plugin.Config.Save();
            Reset();
        }

        Plugin.DrawHelp(
            "仅在新月岛南征之章或北征之章生效。辅助药剂师使用“复活”，辅助白魔法师使用“魔复活”；范围 30 yalms，锁定后延迟 1 秒施放。");
        Plugin.DrawHelp($"当前状态：{runtimeStatus}");
    }

    private void OnUpdate(IFramework _)
    {
        var now = Environment.TickCount64;
        if (now < nextUpdateAt)
            return;
        nextUpdateAt = now + UpdateIntervalMs;

        if (!Plugin.Config.Features.OccultPotAutoRevive || !InOccultFieldZone())
        {
            runtimeStatus = Plugin.Config.Features.OccultPotAutoRevive
                ? "等待进入新月岛南征之章或北征之章。"
                : "功能已关闭。";
            ResetTransientState();
            retryAfter.Clear();
            return;
        }

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var reviveActionRowId = ResolveReviveAction(localPlayer);
        if (localPlayer is not { IsDead: false })
        {
            runtimeStatus = "玩家不可用或已倒地。";
            ResetTransientState();
            return;
        }

        if (reviveActionRowId == 0)
        {
            runtimeStatus = "当前辅助职业不是辅助药剂师或辅助白魔法师。";
            ResetTransientState();
            return;
        }

        if (!IsReviveActionUnlocked(reviveActionRowId))
        {
            runtimeStatus = "当前辅助职业的复活技能尚未解锁。";
            ResetTransientState();
            return;
        }

        if (Plugin.Condition[ConditionFlag.BetweenAreas] ||
            Plugin.Condition[ConditionFlag.BetweenAreas51])
        {
            runtimeStatus = "区域切换中。";
            ResetTransientState();
            return;
        }

        ConfirmPreviousRevive(localPlayer, now);
        if (confirmingTargetEntityId != 0)
        {
            runtimeStatus = $"正在确认对 {confirmingTargetName} 的复活。";
            return;
        }

        var pending = FindPlayer(pendingTargetEntityId);
        if (!IsValidTarget(pending, localPlayer))
        {
            ResetPendingTarget();
            pending = null;
        }

        if (pending != null)
        {
            if (now < reviveAt)
            {
                runtimeStatus = $"已锁定 {pending.Name}，准备施放。";
                return;
            }

            var actionManager = ActionManager.Instance();
            var target = (ClientGameObject*)pending.Address;
            if (actionManager != null &&
                target != null &&
                actionManager->UseAction(
                    ActionType.Action,
                    reviveActionRowId,
                    pending.EntityId))
            {
                runtimeStatus = $"客户端已接受对 {pending.Name} 的复活请求，等待服务端效果。";
                confirmingTargetEntityId = pending.EntityId;
                confirmingTargetName = pending.Name.ToString();
                confirmUntil = now + ConfirmationTimeoutMs;
                ResetPendingTarget();
                return;
            }

            runtimeStatus = $"复活 {pending.Name} 的请求被游戏拒绝，将重试。";
            if (now >= nextFailureLogAt)
            {
                Plugin.Log.Warning(
                    "Auto revive request rejected. Target={Target}, Action={ActionId}",
                    pending.Name,
                    reviveActionRowId);
                nextFailureLogAt = now + FailureLogIntervalMs;
            }
            reviveAt = now + ReviveDelayMs;
            return;
        }

        var nearest = FindNearestTarget(localPlayer, now);
        if (nearest == null)
        {
            runtimeStatus = "30 yalms 内没有可复活目标。";
            return;
        }

        pendingTargetEntityId = nearest.EntityId;
        reviveAt = now + ReviveDelayMs;
        runtimeStatus = $"已锁定 {nearest.Name}，准备施放。";
    }

    private void ConfirmPreviousRevive(IPlayerCharacter localPlayer, long now)
    {
        if (confirmingTargetEntityId == 0)
            return;

        var confirming = FindPlayer(confirmingTargetEntityId);
        if (confirming != null &&
            (!confirming.IsDead || HasOwnRaise(confirming, localPlayer.EntityId)))
        {
            retryAfter[confirmingTargetEntityId] = now + RetryDelayMs;
            Plugin.Chat.Print($"[Keita 工具箱] 已自动复活 {confirmingTargetName}。");
            runtimeStatus = $"已自动复活 {confirmingTargetName}。";
            ResetConfirmation();
            return;
        }

        if (now >= confirmUntil)
        {
            runtimeStatus = "未确认到复活效果，重新检测。";
            ResetConfirmation();
        }
    }

    private IPlayerCharacter? FindNearestTarget(IPlayerCharacter localPlayer, long now)
    {
        IPlayerCharacter? nearest = null;
        var bestDistanceSquared = ReviveRange * ReviveRange;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter player || !IsValidTarget(player, localPlayer))
                continue;

            if (retryAfter.TryGetValue(player.EntityId, out var retryTime))
            {
                if (now < retryTime)
                    continue;
                retryAfter.Remove(player.EntityId);
            }

            var distanceSquared = Vector3.DistanceSquared(localPlayer.Position, player.Position);
            if (distanceSquared > bestDistanceSquared)
                continue;

            nearest = player;
            bestDistanceSquared = distanceSquared;
        }

        return nearest;
    }

    private bool IsValidTarget(IPlayerCharacter? target, IPlayerCharacter localPlayer)
    {
        if (target == null ||
            !target.IsDead ||
            !target.IsTargetable ||
            target.EntityId == localPlayer.EntityId ||
            HasOwnRaise(target, localPlayer.EntityId) ||
            Vector3.DistanceSquared(localPlayer.Position, target.Position) >
            ReviveRange * ReviveRange)
        {
            return false;
        }

        if (!Plugin.Config.OccultPot.AutoRevivePartyOnly)
            return true;

        foreach (var member in Plugin.PartyList)
        {
            if (member.EntityId == target.EntityId)
                return true;
        }

        return false;
    }

    private static uint ResolveReviveAction(IPlayerCharacter? localPlayer)
    {
        var state = PublicContentOccultCrescent.GetState();
        if (state != null)
        {
            var stateAction = state->CurrentSupportJob switch
            {
                PhantomChemistJobId => PhantomChemistReviveActionId,
                PhantomWhiteMageJobId => PhantomWhiteMageReviveActionId,
                _ => 0u,
            };
            if (stateAction != 0)
                return stateAction;
        }

        if (localPlayer != null)
        {
            foreach (var status in localPlayer.StatusList)
            {
                switch (status.StatusId)
                {
                    case PhantomChemistStatusId:
                        return PhantomChemistReviveActionId;
                    case PhantomWhiteMageStatusId:
                        return PhantomWhiteMageReviveActionId;
                }
            }
        }

        return 0;
    }

    private static bool IsReviveActionUnlocked(uint actionId)
    {
        var state = PublicContentOccultCrescent.GetState();
        if (state == null)
            return false;

        var jobId = actionId switch
        {
            PhantomChemistReviveActionId => PhantomChemistJobId,
            PhantomWhiteMageReviveActionId => PhantomWhiteMageJobId,
            _ => byte.MaxValue,
        };
        if (jobId == byte.MaxValue)
            return false;

        var sheet = Plugin.Data.GetExcelSheet<MKDSupportJob>();
        if (sheet == null || !sheet.TryGetRow(jobId, out var job))
            return false;

        foreach (var entry in job.Actions)
        {
            if (entry.Action.RowId != actionId)
                continue;

            return state->SupportJobLevels[jobId] >= entry.LevelUnlock;
        }

        return false;
    }

    private static bool HasOwnRaise(IPlayerCharacter target, uint localPlayerEntityId)
    {
        foreach (var status in target.StatusList)
        {
            if (status.SourceId == localPlayerEntityId &&
                status.StatusId is RaiseStatusId or AlternateRaiseStatusId)
            {
                return true;
            }
        }

        return false;
    }

    private static IPlayerCharacter? FindPlayer(uint entityId)
    {
        if (entityId == 0)
            return null;

        return Plugin.ObjectTable.SearchByEntityId(entityId) as IPlayerCharacter;
    }

    private static bool InOccultFieldZone() =>
        Plugin.ClientState.TerritoryType is SouthHornTerritoryId or NorthHornTerritoryId;

    private void Reset()
    {
        ResetTransientState();
        retryAfter.Clear();
        nextUpdateAt = 0;
        nextFailureLogAt = 0;
        runtimeStatus = "等待检测。";
    }

    private void ResetTransientState()
    {
        ResetPendingTarget();
        ResetConfirmation();
    }

    private void ResetPendingTarget()
    {
        pendingTargetEntityId = 0;
        reviveAt = 0;
    }

    private void ResetConfirmation()
    {
        confirmingTargetEntityId = 0;
        confirmingTargetName = string.Empty;
        confirmUntil = 0;
    }
}
