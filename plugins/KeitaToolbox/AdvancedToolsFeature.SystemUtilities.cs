using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using OmenTools.Extensions;
using OmenTools.Info.Game.Packets.Upstream;
using OmenTools.Interop.Game;
using OmenTools.OmenService;

namespace KeitaToolbox;

internal sealed unsafe partial class AdvancedToolsFeature
{
    private const string KnockbackSpeedSignature =
        "48 8B C4 48 89 58 ?? 48 89 70 ?? 57 48 81 EC ?? ?? ?? ?? 48 8B B9";
    private const string JumpRestrictionSignature =
        "B8 ?? ?? ?? ?? D3 E0 84 D2";
    private const string LocalFlightSignature =
        "40 53 48 83 EC ?? 48 8B 1D ?? ?? ?? ?? 48 85 DB 0F 84 ?? ?? ?? ?? 80 3D";
    private const string HeartbeatPatchSignature =
        "48 3D ?? ?? ?? ?? 0F 82 ?? ?? ?? ?? 48 8D 4C 24 ?? FF 15 ?? ?? ?? ?? 85 C0 49 8B DD";

    private delegate byte KnockbackSpeedDelegate(nint arg1, nint arg2, nint arg3, float lockTime);
    private delegate nint JumpRestrictionDelegate(byte flag, byte isSet);
    private delegate Control.FlightAllowedStatus LocalFlightDelegate();

    private Hook<KnockbackSpeedDelegate>? knockbackSpeedHook;
    private Hook<JumpRestrictionDelegate>? jumpRestrictionHook;
    private Hook<LocalFlightDelegate>? localFlightHook;
    private MemoryPatch? heartbeatPatch;
    private bool immediateSprintRegistered;
    private bool lastHeartbeatInDuty;
    private long nextHeartbeatAt;

    private void InitializeSystemUtilities()
    {
        knockbackSpeedHook = CreateHook<KnockbackSpeedDelegate>(
            "knockback timing",
            KnockbackSpeedSignature,
            KnockbackSpeedDetour);
        jumpRestrictionHook = CreateHook<JumpRestrictionDelegate>(
            "jump restriction immunity",
            JumpRestrictionSignature,
            JumpRestrictionDetour);
        localFlightHook = CreateHook<LocalFlightDelegate>(
            "local flight",
            LocalFlightSignature,
            LocalFlightDetour);

        try
        {
            heartbeatPatch = new MemoryPatch(
                HeartbeatPatchSignature,
                new byte?[] { 0x48, 0x83, 0xF8, 0xFF, 0x90, 0x90 });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to initialize the heartbeat state patch.");
        }

        try
        {
            immediateSprintRegistered =
                UseActionManager.Instance().RegPreUseAction(ImmediateSprintPreUseAction);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to initialize immediate sprint.");
        }

        Plugin.ClientState.TerritoryChanged += OnSystemTerritoryChanged;
    }

    private void DisposeSystemUtilities()
    {
        Plugin.ClientState.TerritoryChanged -= OnSystemTerritoryChanged;

        if (immediateSprintRegistered)
        {
            try
            {
                UseActionManager.Instance().Unreg(ImmediateSprintPreUseAction);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Failed to unregister immediate sprint.");
            }
        }

        var restoreHeartbeat = heartbeatPatch?.IsEnabled == true;
        heartbeatPatch?.Dispose();
        heartbeatPatch = null;
        if (restoreHeartbeat)
            TrySendHeartbeat();

        localFlightHook?.Dispose();
        jumpRestrictionHook?.Dispose();
        knockbackSpeedHook?.Dispose();
    }

    private void DrawKnockbackSettings()
    {
        var selectedMode = Plugin.Config.Advanced.AntiKnockbackMode;
        if (ImGui.BeginCombo("处理方式", GetKnockbackModeLabel(selectedMode)))
        {
            foreach (var mode in Enum.GetValues<KnockbackHandlingMode>())
            {
                if (ImGui.Selectable(GetKnockbackModeLabel(mode), selectedMode == mode))
                {
                    Plugin.Config.Advanced.AntiKnockbackMode = mode;
                    Plugin.Config.Save();
                }
            }

            ImGui.EndCombo();
        }

        if (Plugin.Config.Advanced.AntiKnockbackMode == KnockbackHandlingMode.DistanceScale)
        {
            var multiplier = Plugin.Config.Advanced.AntiKnockbackDistanceMultiplier;
            if (ImGui.DragFloat("距离倍率", ref multiplier, 0.05f, 0f, 2f, "%.2f"))
            {
                Plugin.Config.Advanced.AntiKnockbackDistanceMultiplier = Math.Max(0f, multiplier);
                Plugin.Config.Save();
            }
        }

        Plugin.DrawHelp(GetKnockbackModeDescription(Plugin.Config.Advanced.AntiKnockbackMode));
        if (antiKnockbackHook == null)
            ImGui.TextDisabled("当前游戏版本无法处理强制位移。");
        else if (knockbackSpeedHook == null &&
                 Plugin.Config.Advanced.AntiKnockbackMode is
                     KnockbackHandlingMode.Fast or KnockbackHandlingMode.Instant)
            ImGui.TextDisabled("当前游戏版本无法调整强制位移完成时间。");
    }

    private void DrawSystemUtilitiesSettings()
    {
        ImGui.Separator();
        ImGui.TextUnformatted("系统增强");

        if (DrawToggle(
                "自动免疫禁止跳跃限制",
                Plugin.Config.Advanced.JumpRestrictionImmunity,
                value => Plugin.Config.Advanced.JumpRestrictionImmunity = value))
        {
            UpdateHookStates();
        }
        Plugin.DrawHelp("阻止状态或场地设置本地禁止跳跃标记。");
        if (jumpRestrictionHook == null)
            ImGui.TextDisabled("当前游戏版本无法使用跳跃限制免疫。");

        if (DrawToggle(
                "本地飞行模式",
                Plugin.Config.Advanced.LocalFlight,
                value => Plugin.Config.Advanced.LocalFlight = value))
        {
            UpdateHookStates();
        }
        Plugin.DrawHelp("移除当前区域的本地飞行判定限制。");
        if (localFlightHook == null)
            ImGui.TextDisabled("当前游戏版本无法使用本地飞行模式。");

        DrawToggle(
            "即刻冲刺",
            Plugin.Config.Advanced.ImmediateSprint,
            value => Plugin.Config.Advanced.ImmediateSprint = value);
        Plugin.DrawHelp("直接发送冲刺动作并跳过普通冷却限制。");
        if (!immediateSprintRegistered)
            ImGui.TextDisabled("即刻冲刺服务当前不可用。");

        if (DrawToggle(
                "保持心电图",
                Plugin.Config.Advanced.KeepHeartbeat,
                value => Plugin.Config.Advanced.KeepHeartbeat = value))
        {
            UpdateHookStates();
        }
        Plugin.DrawHelp("持续保持指定的在线状态显示，不影响其他操作。");
        if (Plugin.Config.Advanced.KeepHeartbeat)
        {
            var disableInDuty = Plugin.Config.Advanced.KeepHeartbeatDisableInDuty;
            if (ImGui.Checkbox("副本内暂停保持心电图", ref disableInDuty))
            {
                Plugin.Config.Advanced.KeepHeartbeatDisableInDuty = disableInDuty;
                Plugin.Config.Save();
                nextHeartbeatAt = 0;
            }
        }

        if (heartbeatPatch is not { IsValid: true })
            ImGui.TextDisabled("当前游戏版本无法使用保持心电图。");
    }

    private void UpdateSystemUtilityStates(bool advancedEnabled)
    {
        var antiKnockbackEnabled = advancedEnabled && Plugin.Config.Advanced.AntiKnockback;
        SetHookEnabled(knockbackSpeedHook, antiKnockbackEnabled);

        var enableJumpRestriction =
            advancedEnabled && Plugin.Config.Advanced.JumpRestrictionImmunity;
        var jumpRestrictionWasEnabled = jumpRestrictionHook?.IsEnabled == true;
        SetHookEnabled(jumpRestrictionHook, enableJumpRestriction);
        if (enableJumpRestriction &&
            !jumpRestrictionWasEnabled &&
            jumpRestrictionHook?.IsEnabled == true)
        {
            ClearJumpRestrictions();
        }

        SetHookEnabled(
            localFlightHook,
            advancedEnabled && Plugin.Config.Advanced.LocalFlight);
        SetHeartbeatEnabled(
            advancedEnabled && Plugin.Config.Advanced.KeepHeartbeat);
    }

    private byte KnockbackSpeedDetour(nint arg1, nint arg2, nint arg3, float lockTime)
    {
        if (Enabled(settings => settings.AntiKnockback))
        {
            lockTime = AdvancedUtilityPolicy.AdjustKnockbackLockTime(
                Plugin.Config.Advanced.AntiKnockbackMode,
                lockTime);
        }

        return knockbackSpeedHook!.Original(arg1, arg2, arg3, lockTime);
    }

    private nint JumpRestrictionDetour(byte flag, byte isSet) =>
        jumpRestrictionHook!.Original(flag, 0);

    private static Control.FlightAllowedStatus LocalFlightDetour() =>
        Control.FlightAllowedStatus.CanFly;

    private void ClearJumpRestrictions()
    {
        if (jumpRestrictionHook == null)
            return;

        for (byte flag = 0; flag < 5; flag++)
            jumpRestrictionHook.Original(flag, 0);
    }

    private static void ImmediateSprintPreUseAction(
        ref bool isPrevented,
        ref ActionType actionType,
        ref uint actionId,
        ref ulong targetId,
        ref uint extraParam,
        ref ActionManager.UseActionMode queueState,
        ref uint comboRouteId)
    {
        if (!Enabled(settings => settings.ImmediateSprint) ||
            !AdvancedUtilityPolicy.IsSprintRequest((int)actionType, actionId))
            return;

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null)
            return;

        try
        {
            var adjustedActionId = ActionManagerExtension.GetAdjustSprintActionID();
            if (adjustedActionId == 3)
            {
                new UseActionPacket(
                    ActionType.GeneralAction,
                    4,
                    localPlayer->EntityId,
                    localPlayer->Rotation).Send();
                ActionManager.Instance()->StartCooldown(ActionType.Action, adjustedActionId);
            }
            else
            {
                new UseActionPacket(
                    ActionType.Action,
                    adjustedActionId,
                    localPlayer->EntityId,
                    localPlayer->Rotation).Send();
            }

            isPrevented = true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Immediate sprint failed.");
        }
    }

    private void SetHeartbeatEnabled(bool enabled)
    {
        if (heartbeatPatch is not { IsValid: true } || heartbeatPatch.IsEnabled == enabled)
            return;

        if (enabled)
        {
            TrySendHeartbeat();
            heartbeatPatch.Enable();
        }
        else
        {
            heartbeatPatch.Disable();
            TrySendHeartbeat();
        }

        lastHeartbeatInDuty = IsInDuty();
        nextHeartbeatAt = Environment.TickCount64 + 10_000;
    }

    private void UpdateHeartbeat()
    {
        if (!Enabled(settings => settings.KeepHeartbeat) ||
            heartbeatPatch is not { IsEnabled: true })
            return;

        var now = Environment.TickCount64;
        var inDuty = IsInDuty();
        if (inDuty != lastHeartbeatInDuty)
        {
            lastHeartbeatInDuty = inDuty;
            nextHeartbeatAt = 0;
        }

        if (now < nextHeartbeatAt)
            return;

        TrySendHeartbeat();
        nextHeartbeatAt = now + AdvancedUtilityPolicy.GetHeartbeatIntervalMs(
            Plugin.Config.Advanced.KeepHeartbeatDisableInDuty,
            inDuty);
    }

    private void OnSystemTerritoryChanged(uint _)
    {
        if (!Enabled(settings => settings.KeepHeartbeat) ||
            heartbeatPatch is not { IsEnabled: true })
            return;

        TrySendHeartbeat();
        lastHeartbeatInDuty = IsInDuty();
        nextHeartbeatAt = Environment.TickCount64 +
                          AdvancedUtilityPolicy.GetHeartbeatIntervalMs(
                              Plugin.Config.Advanced.KeepHeartbeatDisableInDuty,
                              lastHeartbeatInDuty);
    }

    private static bool IsInDuty()
    {
        var gameMain = GameMain.Instance();
        return gameMain != null && gameMain->CurrentContentFinderConditionId != 0;
    }

    private static void TrySendHeartbeat()
    {
        try
        {
            new HeartbeatPacket().Send();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to refresh the heartbeat state.");
        }
    }

    private static string GetKnockbackModeLabel(KnockbackHandlingMode mode) =>
        mode switch
        {
            KnockbackHandlingMode.Block => "不位移",
            KnockbackHandlingMode.Fast => "快速就位",
            KnockbackHandlingMode.Instant => "即刻就位",
            KnockbackHandlingMode.Reverse => "反转位移",
            KnockbackHandlingMode.DistanceScale => "调整距离",
            _ => "保持原效果",
        };

    private static string GetKnockbackModeDescription(KnockbackHandlingMode mode) =>
        mode switch
        {
            KnockbackHandlingMode.Block => "完全阻止本地强制位移。",
            KnockbackHandlingMode.Fast => "保留位移目标，将完成时间缩短至 0.5 秒。",
            KnockbackHandlingMode.Instant => "保留位移目标并立即完成位移。",
            KnockbackHandlingMode.Reverse => "反转位移方向并补偿目标距离。",
            KnockbackHandlingMode.DistanceScale => "按倍率调整强制位移距离。",
            _ => "保留强制位移，并缩短过长的锁定时间。",
        };
}
