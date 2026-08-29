using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace KeitaToolbox;

internal sealed unsafe partial class AdvancedToolsFeature : IDisposable
{
    private const int DiveTeleportOpcode = 991;

    private const string SpeedSignature =
        "40 ?? 48 ?? ?? ?? 48 ?? ?? 48 ?? ?? ?? 48 ?? ?? FF 90 ?? ?? ?? ?? 48 ?? ?? 75 ?? F3 ?? ?? ?? ?? ?? ?? ??";
    private const string MovePermissionSignature =
        "E8 ?? ?? ?? ?? 84 ?? 74 ?? 48 C7 05";
    private const string SkillPostActionMoveSignature =
        "48 ?? ?? ?? 48 ?? ?? ?? 45 ?? ?? 33 ?? E8 ?? ?? ?? ?? 84 ?? 74 ??";
    private const string ActionRangeSignature =
        "48 89 5C 24 ?? 57 48 ?? ?? ?? 48 ?? ?? ?? ?? ?? ?? 8B ?? 0F 29 74 24 20";
    private const string SelfResurrectSignature =
        "E8 ?? ?? ?? ?? 83 4B 70 01";
    private const string NoFallSignature =
        "E8 ?? ?? ?? ?? 85 ?? 78 ?? 48 ?? ?? ?? ?? ?? ?? 4C ?? ?? ?? ?? ?? ?? 44";
    private const string AntiKnockbackSignature =
        "E8 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? FF C6";
    private const string DiveTeleportSignature =
        "48 89 5C 24 ?? 48 89 74 24 ?? 4C 89 64 24 ?? 55 41 ?? 41 ?? 48 ?? ?? 48 ?? ?? ?? 48";
    private const string ForcedActionSignature =
        "E8 ?? ?? ?? ?? B0 ?? C7 43 ?? ?? ?? ?? ?? EB ??";
    private const string StatusManagerSignature =
        "4C 8B DC 55 49 8D AB ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 ?? ?? ?? ?? 49 89 73";
    private const string StatusPacketSignature =
        "48 8B C4 44 88 48 ?? 55 57";
    private const string NormalMovementSignature =
        "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B F9 41 8B D8";
    private const string CombatMovementSignature =
        "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B F9 41 8B F0";

    private delegate float SpeedDelegate(nint arg1);
    private delegate bool MovePermissionDelegate(
        Conditions* conditions,
        uint actionId,
        int arg3,
        int arg4);
    private delegate long SkillPostActionMoveDelegate(long arg1);
    private delegate float ActionRangeDelegate(uint actionId);
    private delegate long FallCheckDelegate(long arg1, uint flags);
    private delegate byte KnockbackDelegate(
        nint gameObject,
        float rotation,
        float distance,
        float duration,
        byte arg5,
        int arg6);
    private delegate byte DiveTeleportDelegate(nint context, nint data1, nint data2, byte arg4);
    private delegate void SelfResurrectDelegate(GameObject* player, float x, float y, float z);
    private delegate nint ForcedActionDelegate(
        GameObject* gameObject,
        float x,
        float y,
        float z,
        int arg5,
        nint arg6);
    private delegate void StatusManagerDelegate(StatusManager* manager);
    private delegate void StatusPacketDelegate(
        uint entityId,
        StatusEffectList* packet,
        bool isReplayGroup,
        bool isFirstHalf);
    private delegate nint MovementPacketDelegate(nuint context, nint data, uint length);

    private readonly HashSet<uint> knownActionIds = [];
    private readonly HashSet<uint> frontlineFullRangeActions = [];
    private readonly HashSet<uint> gapCloserActions = [];
    private static readonly HashSet<uint> BlockedStatusIds =
    [
        142, 149, 604, 905, 911, 1257, 1293, 1294, 1295, 1296,
        1422, 1579, 1580, 1681, 1958, 1959, 1960, 1961, 2161, 2162,
        2163, 2164, 2381, 2382, 2383, 2384, 2538, 2539, 2540, 2541,
        2936, 3629, 3694, 3698, 3699, 3700, 3701, 3715, 3716, 3717,
        3718, 3719, 3737, 3909,
    ];
    private readonly Hook<SpeedDelegate>? speedHook;
    private readonly Hook<MovePermissionDelegate>? movePermissionHook;
    private readonly Hook<SkillPostActionMoveDelegate>? skillPostActionMoveHook;
    private readonly Hook<ActionRangeDelegate>? actionRangeHook;
    private readonly Hook<SelfResurrectDelegate>? selfResurrectHook;
    private readonly Hook<FallCheckDelegate>? noFallHook;
    private readonly Hook<KnockbackDelegate>? antiKnockbackHook;
    private readonly Hook<DiveTeleportDelegate>? diveTeleportHook;
    private readonly Hook<ForcedActionDelegate>? forcedActionHook;
    private readonly Hook<StatusManagerDelegate>? statusManagerHook;
    private readonly Hook<StatusPacketDelegate>? statusPacketHook;
    private readonly Hook<MovementPacketDelegate>? normalMovementHook;
    private readonly Hook<MovementPacketDelegate>? combatMovementHook;
    private nint diveTeleportContext;
    private bool mouseTeleportArmed;
    private bool mouseTeleportClickReleased;
    private long suppressInvincibilityDiveUntil;
    private CharacterModes invincibilityOriginalMode;
    private byte invincibilityOriginalModeParam;

    public AdvancedToolsFeature()
    {
        foreach (var action in Plugin.Data.GetExcelSheet<LuminaAction>())
        {
            knownActionIds.Add(action.RowId);
            if (!action.AffectsPosition || !action.CanTargetHostile)
                continue;

            frontlineFullRangeActions.Add(action.RowId);
            if (action.IsPlayerAction)
                gapCloserActions.Add(action.RowId);
        }

        speedHook = CreateHook<SpeedDelegate>("movement speed", SpeedSignature, SpeedDetour);
        movePermissionHook = CreateHook<MovePermissionDelegate>(
            "movement permission",
            MovePermissionSignature,
            MovePermissionDetour);
        skillPostActionMoveHook = CreateHook<SkillPostActionMoveDelegate>(
            "post-action movement",
            SkillPostActionMoveSignature,
            SkillPostActionMoveDetour);
        actionRangeHook = CreateHook<ActionRangeDelegate>(
            "action range",
            ActionRangeSignature,
            ActionRangeDetour);
        selfResurrectHook = CreateHook<SelfResurrectDelegate>(
            "self-resurrect",
            SelfResurrectSignature,
            SelfResurrectDetour);
        noFallHook = CreateHook<FallCheckDelegate>(
            "fall protection",
            NoFallSignature,
            NoFallDetour);
        antiKnockbackHook = CreateHook<KnockbackDelegate>(
            "anti-knockback",
            AntiKnockbackSignature,
            AntiKnockbackDetour);
        diveTeleportHook = CreateHook<DiveTeleportDelegate>(
            "dive teleport",
            DiveTeleportSignature,
            DiveTeleportDetour);
        forcedActionHook = CreateHook<ForcedActionDelegate>(
            "ignore charm and fear",
            ForcedActionSignature,
            ForcedActionDetour);
        statusManagerHook = CreateHook<StatusManagerDelegate>(
            "status block manager",
            StatusManagerSignature,
            StatusManagerDetour);
        statusPacketHook = CreateHook<StatusPacketDelegate>(
            "status block packet",
            StatusPacketSignature,
            StatusPacketDetour);
        normalMovementHook = CreateHook<MovementPacketDelegate>(
            "normal movement Z offset",
            NormalMovementSignature,
            NormalMovementDetour);
        combatMovementHook = CreateHook<MovementPacketDelegate>(
            "combat movement Z offset",
            CombatMovementSignature,
            CombatMovementDetour);
        InitializeSystemUtilities();
        UpdateHookStates();
    }

    public void Dispose()
    {
        DisposeSystemUtilities();
        combatMovementHook?.Dispose();
        normalMovementHook?.Dispose();
        statusPacketHook?.Dispose();
        statusManagerHook?.Dispose();
        forcedActionHook?.Dispose();
        diveTeleportHook?.Dispose();
        antiKnockbackHook?.Dispose();
        noFallHook?.Dispose();
        selfResurrectHook?.Dispose();
        actionRangeHook?.Dispose();
        skillPostActionMoveHook?.Dispose();
        movePermissionHook?.Dispose();
        speedHook?.Dispose();
    }

    public void RefreshProtectionState() => UpdateHookStates();

    public void DrawMovementAndSystemSettings()
    {
        DrawMovementControlSettings();
        DrawActionAndDisplacementSettings();
        DrawPositionAndExplorationSettings();
        DrawSystemStateSettings();
        DrawTeleportSettings();
        DrawDiagnosticsSettings();
    }

    private void DrawMovementControlSettings()
    {
        if (!ImGui.CollapsingHeader("移动控制", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        DrawToggle(
            "移动速度",
            Plugin.Config.Advanced.SpeedHack,
            value => Plugin.Config.Advanced.SpeedHack = value);
        if (Plugin.Config.Advanced.SpeedHack)
        {
            var value = Plugin.Config.Advanced.SpeedValue;
            if (ImGui.DragFloat("速度加成", ref value, 0.01f, 0f, 1f, "%.2f"))
            {
                Plugin.Config.Advanced.SpeedValue = Math.Clamp(value, 0f, 1f);
                Plugin.Config.Save();
            }
        }

        DrawToggle(
            "受限动作期间允许移动",
            Plugin.Config.Advanced.MovePermission,
            value => Plugin.Config.Advanced.MovePermission = value);
        DrawToggle(
            "动作结束后立即移动",
            Plugin.Config.Advanced.SkillPostActionMove,
            value => Plugin.Config.Advanced.SkillPostActionMove = value);
        DrawToggle(
            "防坠落",
            Plugin.Config.Advanced.NoFall,
            value => Plugin.Config.Advanced.NoFall = value);
        DrawJumpRestrictionSettings();
        DrawImmediateSprintSettings();
    }

    private void DrawActionAndDisplacementSettings()
    {
        if (!ImGui.CollapsingHeader("技能距离与强制位移"))
            return;

        DrawToggle(
            "延长技能距离",
            Plugin.Config.Advanced.ActionRange,
            value => Plugin.Config.Advanced.ActionRange = value);
        if (Plugin.Config.Advanced.ActionRange)
        {
            var value = Plugin.Config.Advanced.ActionRangeValue;
            if (ImGui.DragFloat("技能距离加成", ref value, 0.1f, 0f, 3f, "%.1f"))
            {
                Plugin.Config.Advanced.ActionRangeValue = Math.Clamp(value, 0f, 3f);
                Plugin.Config.Save();
            }
        }

        DrawToggle(
            "扩展突进技能距离",
            Plugin.Config.Advanced.GapCloserRange,
            value => Plugin.Config.Advanced.GapCloserRange = value);
        DrawToggle(
            "自动防击退",
            Plugin.Config.Advanced.AntiKnockback,
            value => Plugin.Config.Advanced.AntiKnockback = value);
        if (Plugin.Config.Advanced.AntiKnockback)
            DrawKnockbackSettings();
    }

    private void DrawPositionAndExplorationSettings()
    {
        if (!ImGui.CollapsingHeader("位置与探索"))
            return;

        DrawLocalFlightSettings();

        var zOffset = Plugin.Config.Advanced.ZOffset;
        if (ImGui.Checkbox("Z 轴偏移", ref zOffset))
        {
            if (zOffset && !Plugin.Config.Advanced.ZOffset)
                Plugin.Config.Advanced.ZOffsetValue = 0f;
            Plugin.Config.Advanced.ZOffset = zOffset;
            Plugin.Config.Save();
            UpdateHookStates();
        }

        if (!Plugin.Config.Advanced.ZOffset)
            return;

        var deepDungeonMode = Plugin.Config.Advanced.DeepDungeonZOffsetMode;
        if (ImGui.Checkbox("死宫特供模式", ref deepDungeonMode))
        {
            Plugin.Config.Advanced.DeepDungeonZOffsetMode = deepDungeonMode;
            Plugin.Config.Save();
            UpdateHookStates();
        }
        Plugin.DrawHelp(
            "通过移动数据应用偏移；进入深层迷宫后，整十层以及特定第 99 层自动恢复正常高度。");

        var previous = Plugin.Config.Advanced.ZOffsetValue;
        var value = previous;
        if (ImGui.DragFloat("Z 轴偏移量", ref value, 0.1f, -10f, 10f, "%.1f"))
        {
            value = Math.Clamp(value, -10f, 10f);
            Plugin.Config.Advanced.ZOffsetValue = value;
            if (!Plugin.Config.Advanced.DeepDungeonZOffsetMode)
                ApplyVerticalOffset(value - previous);
            Plugin.Config.Save();
        }

        if (Plugin.Config.Advanced.DeepDungeonZOffsetMode &&
            (normalMovementHook == null || combatMovementHook == null))
        {
            Plugin.DrawDisabledWrapped("当前游戏版本无法使用死宫特供模式。");
        }
    }

    private void DrawSystemStateSettings()
    {
        if (!ImGui.CollapsingHeader("系统状态"))
            return;

        DrawHeartbeatSettings();
    }

    private void DrawTeleportSettings()
    {
        if (!ImGui.CollapsingHeader("位置传送"))
            return;

        if (ImGui.Button("传送到鼠标位置", new Vector2(-1f, 0f)))
            ArmMouseTeleport();
        if (mouseTeleportArmed)
            Plugin.DrawColoredWrapped(new Vector4(0.35f, 0.85f, 1f, 1f), "选点中：左键传送，右键取消。");
        else
            Plugin.DrawHelp("点击后左键选点；也可用 /ktb mouse 传送到当前鼠标位置。");

        if (ImGui.Button("传送到地图旗标", new Vector2(-1f, 0f)))
            TeleportToFlag();

        DrawDiveServiceStatus("位置传送");
    }

    private void DrawDiagnosticsSettings()
    {
        if (!ImGui.CollapsingHeader("诊断"))
            return;

        var debug = Plugin.Config.Advanced.DebugLogging;
        if (ImGui.Checkbox("调试日志", ref debug))
        {
            Plugin.Config.Advanced.DebugLogging = debug;
            Plugin.Config.Save();
        }
        Plugin.DrawHelp("仅在排查功能初始化或执行问题时启用。");
    }

    public void DrawCombatUtilitySettings()
    {
        DrawSurvivalSettings();
        DrawStatusResistanceSettings();
    }

    private void DrawSurvivalSettings()
    {
        if (!ImGui.CollapsingHeader("生存与紧急操作", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        DrawToggle(
            "原地复活",
            Plugin.Config.Advanced.SelfResurrect,
            value => Plugin.Config.Advanced.SelfResurrect = value);
        Plugin.DrawHelp("野外不可用；副本内死亡后需手动点击“返回”。");

        if (ImGui.Button("触发无敌", new Vector2(-1f, 0f)))
            TriggerInvincibility();
        Plugin.DrawHelp("也可使用 /ktb invincible。");
        DrawDiveServiceStatus("触发无敌");
    }

    private void DrawStatusResistanceSettings()
    {
        if (!ImGui.CollapsingHeader("异常状态与强制移动"))
            return;

        var ignoreCharm = Plugin.Config.Features.IgnoreCharmAndFear;
        if (Plugin.DrawFeatureToggle(
                "无视魅惑与恐惧",
                ignoreCharm,
                value => Plugin.Config.Features.IgnoreCharmAndFear = value))
        {
            UpdateHookStates();
        }
        Plugin.DrawHelp("阻止魅惑和恐惧状态造成的强制移动。");

        var statusBlock = Plugin.Config.Features.StatusBlock;
        if (Plugin.DrawFeatureToggle(
                "状态屏蔽（滑冰）",
                statusBlock,
                value => Plugin.Config.Features.StatusBlock = value))
        {
            UpdateHookStates();
        }
        Plugin.DrawHelp("从本地和网络状态更新中过滤指定的移动状态。");
    }

    public void DrawFrontlineRemoteInteractionSettings()
    {
        var remoteInteraction = Plugin.Config.Features.FrontlineRemoteInteraction;
        if (Plugin.DrawFeatureToggle(
                "远程摸点",
                remoteInteraction,
                value => Plugin.Config.Features.FrontlineRemoteInteraction = value))
        {
            UpdateHookStates();
        }
        Plugin.DrawHelp("仅在 PvP 区域内延长交互距离。");

        var range = Plugin.Config.CombatUtilities.FrontlineRangeBonus;
        if (ImGui.DragFloat("战场距离加成", ref range, 1f, 0f, 40f, "%.0f"))
        {
            Plugin.Config.CombatUtilities.FrontlineRangeBonus = Math.Clamp(range, 0f, 40f);
            Plugin.Config.Save();
        }
    }

    private void DrawDiveServiceStatus(string featureName)
    {
        if (diveTeleportHook == null)
            Plugin.DrawDisabledWrapped($"{featureName}当前不可用：游戏版本不受支持。");
        else if (diveTeleportContext == nint.Zero)
            Plugin.DrawDisabledWrapped($"{featureName}正在等待游戏环境初始化。");
    }

    private static bool Enabled(Func<AdvancedToolsSettings, bool> selector) =>
        Plugin.ProtectedFeaturesUnlocked &&
        selector(Plugin.Config.Advanced);

    private float SpeedDetour(nint arg1)
    {
        var original = speedHook!.Original(arg1);
        return Enabled(settings => settings.SpeedHack)
            ? original + Plugin.Config.Advanced.SpeedValue
            : original;
    }

    private bool MovePermissionDetour(
        Conditions* conditions,
        uint actionId,
        int arg3,
        int arg4)
    {
        if (Enabled(settings => settings.MovePermission) &&
            actionId is 96 or 97 or 98 or 99 or 1001 or 1006 or 1007 or 1008)
        {
            return true;
        }

        return movePermissionHook!.Original(conditions, actionId, arg3, arg4);
    }

    private long SkillPostActionMoveDetour(long arg1) =>
        Enabled(settings => settings.SkillPostActionMove)
            ? arg1
            : skillPostActionMoveHook!.Original(arg1);

    private float ActionRangeDetour(uint actionId)
    {
        var original = actionRangeHook!.Original(actionId);
        if (Plugin.ProtectedFeaturesUnlocked &&
            Plugin.Config.Features.FrontlineRemoteInteraction &&
            Plugin.ClientState.IsPvP)
            return original + CombatUtilityPolicy.GetFrontlineRangeBonus(
                actionId,
                knownActionIds.Contains(actionId),
                frontlineFullRangeActions.Contains(actionId),
                Plugin.Config.CombatUtilities.FrontlineRangeBonus);

        if (Enabled(settings => settings.GapCloserRange) &&
            gapCloserActions.Contains(actionId))
        {
            return original + 25f;
        }

        return Enabled(settings => settings.ActionRange)
            ? original + Plugin.Config.Advanced.ActionRangeValue
            : original;
    }

    private nint ForcedActionDetour(
        GameObject* gameObject,
        float x,
        float y,
        float z,
        int arg5,
        nint arg6)
    {
        if (Plugin.ProtectedFeaturesUnlocked &&
            Plugin.Config.Features.IgnoreCharmAndFear &&
            Plugin.ObjectTable.LocalPlayer != null)
        {
            return nint.Zero;
        }

        return forcedActionHook!.Original(gameObject, x, y, z, arg5, arg6);
    }

    private void StatusManagerDetour(StatusManager* manager)
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (!Plugin.ProtectedFeaturesUnlocked ||
            !Plugin.Config.Features.StatusBlock ||
            manager == null ||
            manager->Owner == null ||
            localPlayer == null ||
            manager->Owner->EntityId != localPlayer.EntityId)
        {
            statusManagerHook!.Original(manager);
            return;
        }

        foreach (ref var status in manager->Status)
        {
            if (status.StatusId != 0 && BlockedStatusIds.Contains(status.StatusId))
                status = default;
        }

        statusManagerHook!.Original(manager);
    }

    private void StatusPacketDetour(
        uint entityId,
        StatusEffectList* packet,
        bool isReplayGroup,
        bool isFirstHalf)
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (Plugin.ProtectedFeaturesUnlocked &&
            Plugin.Config.Features.StatusBlock &&
            packet != null &&
            localPlayer != null &&
            entityId == localPlayer.EntityId)
        {
            foreach (ref var entry in packet->Entries)
            {
                if (entry.StatusID != 0 && BlockedStatusIds.Contains(entry.StatusID))
                    entry = default;
            }
        }

        statusPacketHook!.Original(entityId, packet, isReplayGroup, isFirstHalf);
    }

    private nint NormalMovementDetour(nuint context, nint data, uint length)
    {
        if (data != nint.Zero && ShouldApplyMovementZOffset())
            ((float*)data)[3] += Plugin.Config.Advanced.ZOffsetValue;

        return normalMovementHook!.Original(context, data, length);
    }

    private nint CombatMovementDetour(nuint context, nint data, uint length)
    {
        if (data != nint.Zero && ShouldApplyMovementZOffset())
        {
            ((float*)data)[4] += Plugin.Config.Advanced.ZOffsetValue;
            ((float*)data)[7] += Plugin.Config.Advanced.ZOffsetValue;
        }

        return combatMovementHook!.Original(context, data, length);
    }

    private static bool ShouldApplyMovementZOffset()
    {
        if (!Enabled(settings =>
                settings.ZOffset &&
                settings.DeepDungeonZOffsetMode &&
                Math.Abs(settings.ZOffsetValue) >= 0.001f))
        {
            return false;
        }

        if (!Plugin.Condition[ConditionFlag.InDeepDungeon])
            return true;

        var territorySheet = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
        if (territorySheet == null ||
            !territorySheet.TryGetRow(Plugin.ClientState.TerritoryType, out var territory))
        {
            return false;
        }

        if (territory.TerritoryIntendedUse.RowId != 31)
            return true;

        var eventFramework = EventFramework.Instance();
        var deepDungeon = eventFramework == null
            ? null
            : eventFramework->GetInstanceContentDeepDungeon();
        if (deepDungeon == null)
            return false;

        var floor = deepDungeon->Floor;
        if (floor == 0 || floor % 10 == 0)
            return false;

        return Plugin.ClientState.TerritoryType is not 1108 and not 1290 || floor != 99;
    }

    private void SelfResurrectDetour(GameObject* player, float x, float y, float z)
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (Plugin.ProtectedFeaturesUnlocked &&
            Plugin.Config.Advanced.SelfResurrect &&
            !Plugin.ClientState.IsPvP &&
            localPlayer != null &&
            localPlayer.IsDead &&
            (nint)player == localPlayer.Address)
        {
            return;
        }

        selfResurrectHook!.Original(player, x, y, z);
    }

    private long NoFallDetour(long arg1, uint flags)
    {
        if (Enabled(settings => settings.NoFall) && (flags & 0x700) != 0)
            flags = (flags & ~0x700u) | 2u;

        return noFallHook!.Original(arg1, flags);
    }

    private byte AntiKnockbackDetour(
        nint gameObject,
        float rotation,
        float distance,
        float duration,
        byte arg5,
        int arg6)
    {
        if (Enabled(settings => settings.AntiKnockback))
        {
            var adjustment = AdvancedUtilityPolicy.AdjustKnockback(
                Plugin.Config.Advanced.AntiKnockbackMode,
                rotation,
                distance,
                Plugin.Config.Advanced.AntiKnockbackDistanceMultiplier);
            if (adjustment.Suppress)
                return 0;

            rotation = adjustment.Rotation;
            distance = adjustment.Distance;
        }

        return antiKnockbackHook!.Original(
            gameObject,
            rotation,
            distance,
            duration,
            arg5,
            arg6);
    }

    private byte DiveTeleportDetour(nint context, nint data1, nint data2, byte arg4)
    {
        diveTeleportContext = context;
        return diveTeleportHook!.Original(context, data1, data2, arg4);
    }

    public void ArmMouseTeleport()
    {
        if (!Plugin.ProtectedFeaturesUnlocked)
        {
            Plugin.Chat.PrintError("[Keita 工具箱] 请先解锁受保护的高级工具。");
            return;
        }

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null)
        {
            Plugin.Chat.PrintError("[Keita 工具箱] 当前无法读取角色位置。");
            return;
        }

        mouseTeleportArmed = true;
        mouseTeleportClickReleased = false;
        Plugin.Chat.Print("[Keita 工具箱] 请在游戏地面左键选择传送位置，右键取消。");
    }

    public void TeleportToMouse()
    {
        if (!Plugin.ProtectedFeaturesUnlocked)
        {
            Plugin.Chat.PrintError("[Keita 工具箱] 请先解锁受保护的高级工具。");
            return;
        }

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null)
        {
            Plugin.Chat.PrintError("[Keita 工具箱] 当前无法读取角色位置。");
            return;
        }

        var position = Vector3.Zero;
        if (!Plugin.GameGui.ScreenToWorld(ImGui.GetIO().MousePos, out position, 100000f))
        {
            Plugin.Chat.PrintError("[Keita 工具箱] 当前鼠标位置没有可传送的地面。");
            return;
        }

        ((GameObject*)localPlayer.Address)->SetPosition(position.X, position.Y, position.Z);
        Plugin.Chat.Print($"[Keita 工具箱] 已传送到 {position.X:F1}, {position.Y:F1}, {position.Z:F1}。");
        Debug($"Teleported directly to mouse position {position}.");
    }

    public void UpdateMouseTeleport()
    {
        SuppressInvincibilityDiveAnimation();
        UpdateHeartbeat();

        if (!mouseTeleportArmed)
            return;

        if (!Plugin.ProtectedFeaturesUnlocked || Plugin.ObjectTable.LocalPlayer == null)
        {
            CancelMouseTeleport();
            return;
        }

        if (!mouseTeleportClickReleased)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left) &&
                !ImGui.IsMouseDown(ImGuiMouseButton.Right))
            {
                mouseTeleportClickReleased = true;
            }
            return;
        }

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            CancelMouseTeleport();
            return;
        }

        var io = ImGui.GetIO();
        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left) || io.WantCaptureMouse)
            return;

        var position = Vector3.Zero;
        if (!Plugin.GameGui.ScreenToWorld(io.MousePos, out position, 100000f))
        {
            Plugin.Chat.PrintError("[Keita 工具箱] 该位置没有可传送的地面，请重新选择。");
            return;
        }

        var localPlayer = Plugin.ObjectTable.LocalPlayer!;
        ((GameObject*)localPlayer.Address)->SetPosition(position.X, position.Y, position.Z);
        mouseTeleportArmed = false;
        mouseTeleportClickReleased = false;
        Plugin.Chat.Print($"[Keita 工具箱] 已传送到 {position.X:F1}, {position.Y:F1}, {position.Z:F1}。");
        Debug($"Teleported to mouse position {position}.");
    }

    private void CancelMouseTeleport()
    {
        if (!mouseTeleportArmed)
            return;

        mouseTeleportArmed = false;
        mouseTeleportClickReleased = false;
        Plugin.Chat.Print("[Keita 工具箱] 已取消鼠标位置传送。");
    }

    private void TeleportToFlag()
    {
        if (!Plugin.ProtectedFeaturesUnlocked)
            return;

        var agentMap = AgentMap.Instance();
        if (agentMap == null || agentMap->FlagMarkerCount <= 0)
        {
            Plugin.Chat.PrintError("[Keita 工具箱] 当前没有可用的地图旗标。");
            return;
        }

        var marker = agentMap->FlagMapMarkers[0];
        SendDiveTeleport(new Vector3(marker.XFloat, 0f, marker.YFloat));
    }

    public void TriggerInvincibility()
    {
        if (!Plugin.ProtectedFeaturesUnlocked)
        {
            Plugin.Chat.PrintError("[Keita 工具箱] 请先解锁受保护的高级工具。");
            return;
        }

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null)
        {
            Plugin.Chat.PrintError("[Keita 工具箱] 当前无法读取角色位置。");
            return;
        }

        var character = (Character*)localPlayer.Address;
        if (character != null &&
            character->Mode != CharacterModes.Mounted &&
            !Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted] &&
            !Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InFlight] &&
            !Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Swimming] &&
            !Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Diving])
        {
            invincibilityOriginalMode = character->Mode;
            invincibilityOriginalModeParam = character->ModeParam;
            suppressInvincibilityDiveUntil = Environment.TickCount64 + 1000;
        }

        SendDiveTeleport(localPlayer.Position);
        SuppressInvincibilityDiveAnimation();
    }

    private void SuppressInvincibilityDiveAnimation()
    {
        if (suppressInvincibilityDiveUntil == 0)
            return;

        if (Environment.TickCount64 > suppressInvincibilityDiveUntil)
        {
            suppressInvincibilityDiveUntil = 0;
            return;
        }

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null)
        {
            suppressInvincibilityDiveUntil = 0;
            return;
        }

        var character = (Character*)localPlayer.Address;
        if (character != null && character->Mode == CharacterModes.Mounted)
            character->SetMode(invincibilityOriginalMode, invincibilityOriginalModeParam);
    }

    private void SendDiveTeleport(Vector3 position)
    {
        if (diveTeleportHook == null || diveTeleportContext == nint.Zero)
        {
            Plugin.Chat.PrintError("[Keita 工具箱] 潜水传送尚未就绪。");
            return;
        }

        const int packetSize = 58;
        var packet = Marshal.AllocHGlobal(packetSize);
        try
        {
            new Span<byte>((void*)packet, packetSize).Clear();
            *(int*)(packet + 0) = DiveTeleportOpcode;
            *(int*)(packet + 8) = 56;
            *(float*)(packet + 32) = Plugin.ObjectTable.LocalPlayer?.Rotation ?? 0f;
            *(Vector3*)(packet + 36) = position;
            diveTeleportHook.Original(diveTeleportContext, packet, packet, 1);
            Debug($"Sent dive teleport to {position}.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Dive teleport failed.");
            Plugin.Chat.PrintError("[Keita 工具箱] 潜水传送失败，请检查 Dalamud 日志。");
        }
        finally
        {
            Marshal.FreeHGlobal(packet);
        }
    }

    private static void ApplyVerticalOffset(float delta)
    {
        if (!Plugin.ProtectedFeaturesUnlocked || Math.Abs(delta) < 0.001f)
            return;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null)
            return;

        var position = localPlayer.Position;
        ((GameObject*)localPlayer.Address)->SetPosition(
            position.X,
            position.Y + delta,
            position.Z);
    }

    private bool DrawToggle(string label, bool value, Action<bool> setter)
    {
        var changed = value;
        if (!ImGui.Checkbox(label, ref changed))
            return false;

        setter(changed);
        Plugin.Config.Save();
        UpdateHookStates();
        return true;
    }

    private static Hook<T>? CreateHook<T>(string name, string signature, T detour)
        where T : Delegate
    {
        try
        {
            return Plugin.Interop.HookFromSignature(signature, detour);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to initialize advanced function: {Feature}.", name);
            return null;
        }
    }

    private void UpdateHookStates()
    {
        var protectionUnlocked = Plugin.ProtectedFeaturesUnlocked;
        var settings = Plugin.Config.Advanced;
        SetHookEnabled(speedHook, protectionUnlocked && settings.SpeedHack);
        SetHookEnabled(movePermissionHook, protectionUnlocked && settings.MovePermission);
        SetHookEnabled(
            skillPostActionMoveHook,
            protectionUnlocked && settings.SkillPostActionMove);
        SetHookEnabled(
            actionRangeHook,
            protectionUnlocked &&
            (settings.ActionRange ||
             settings.GapCloserRange ||
             Plugin.Config.Features.FrontlineRemoteInteraction));
        SetHookEnabled(
            selfResurrectHook,
            protectionUnlocked && settings.SelfResurrect);
        SetHookEnabled(noFallHook, protectionUnlocked && settings.NoFall);
        SetHookEnabled(
            antiKnockbackHook,
            protectionUnlocked && settings.AntiKnockback);
        SetHookEnabled(diveTeleportHook, protectionUnlocked);
        SetHookEnabled(
            forcedActionHook,
            protectionUnlocked && Plugin.Config.Features.IgnoreCharmAndFear);
        SetHookEnabled(
            statusManagerHook,
            protectionUnlocked && Plugin.Config.Features.StatusBlock);
        SetHookEnabled(
            statusPacketHook,
            protectionUnlocked && Plugin.Config.Features.StatusBlock);
        var deepDungeonZOffsetEnabled =
            protectionUnlocked &&
            settings.ZOffset &&
            settings.DeepDungeonZOffsetMode;
        SetHookEnabled(normalMovementHook, deepDungeonZOffsetEnabled);
        SetHookEnabled(combatMovementHook, deepDungeonZOffsetEnabled);
        UpdateSystemUtilityStates(protectionUnlocked);
        if (!protectionUnlocked)
        {
            diveTeleportContext = nint.Zero;
            if (mouseTeleportArmed)
                CancelMouseTeleport();
        }
    }

    private static void SetHookEnabled<T>(Hook<T>? hook, bool enabled)
        where T : Delegate
    {
        if (hook == null)
            return;

        try
        {
            if (enabled && !hook.IsEnabled)
                hook.Enable();
            else if (!enabled && hook.IsEnabled)
                hook.Disable();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to change an advanced tool hook state.");
        }
    }

    private static void Debug(string message)
    {
        if (Plugin.Config.Advanced.DebugLogging)
            Plugin.Log.Debug(message);
    }
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct StatusEffectList
{
    [FieldOffset(20)]
    public fixed byte EntryData[240];

    public Span<StatusEffectListEntry> Entries
    {
        get
        {
            fixed (byte* data = EntryData)
                return new Span<StatusEffectListEntry>(data, 30);
        }
    }
}

[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 12)]
internal struct StatusEffectListEntry
{
    [FieldOffset(0)]
    public ushort StatusID;

    [FieldOffset(2)]
    public ushort StackCount;

    [FieldOffset(4)]
    public float RemainingTime;

    [FieldOffset(8)]
    public uint SourceID;
}
