using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace KeitaToolbox;

internal sealed unsafe class AyanoHimituFeature : IDisposable
{
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

    private delegate float SpeedDelegate(nint arg1);
    private delegate nint MovePermissionDelegate(nint arg1, uint actionId, int arg3, int arg4);
    private delegate long SkillPostActionMoveDelegate(long arg1);
    private delegate float ActionRangeDelegate(uint actionId);
    private delegate long FallCheckDelegate(long arg1, uint flags);
    private delegate long KnockbackDelegate(
        long gameObject,
        float rotation,
        float distance,
        long duration,
        char arg5,
        int arg6);
    private delegate byte DiveTeleportDelegate(nint context, nint data1, nint data2, byte arg4);
    private delegate long SelfResurrectDelegate(GameObject* player, float x, float y, float z);
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
    private nint diveTeleportContext;

    public AyanoHimituFeature()
    {
        foreach (var action in Plugin.Data.GetExcelSheet<LuminaAction>())
        {
            if (action.AffectsPosition && action.CanTargetHostile && action.IsPlayerAction)
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
        UpdateHookStates();
    }

    public void Dispose()
    {
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

    public void DrawSettings()
    {
        if (!ImGui.CollapsingHeader("Ayano Himitu Box"))
            return;

        Plugin.DrawFeatureToggle(
            "Ayano Himitu Box functions",
            Plugin.Config.Features.AyanoHimituBox,
            value =>
            {
                Plugin.Config.Features.AyanoHimituBox = value;
                UpdateHookStates();
            });
        Plugin.DrawHelp(
            "These functions alter client behavior. Disable the original AyanoHimituBox plugin before enabling them.");

        DrawToggle(
            "Movement speed",
            Plugin.Config.Ayano.SpeedHack,
            value => Plugin.Config.Ayano.SpeedHack = value);
        if (Plugin.Config.Ayano.SpeedHack)
        {
            var value = Plugin.Config.Ayano.SpeedValue;
            if (ImGui.DragFloat("Speed bonus", ref value, 0.01f, 0f, 1f, "%.2f"))
            {
                Plugin.Config.Ayano.SpeedValue = Math.Clamp(value, 0f, 1f);
                Plugin.Config.Save();
            }
        }

        DrawToggle(
            "Move during restricted actions",
            Plugin.Config.Ayano.MovePermission,
            value => Plugin.Config.Ayano.MovePermission = value);
        DrawToggle(
            "Move immediately after actions",
            Plugin.Config.Ayano.SkillPostActionMove,
            value => Plugin.Config.Ayano.SkillPostActionMove = value);
        DrawToggle(
            "Extended action range",
            Plugin.Config.Ayano.ActionRange,
            value => Plugin.Config.Ayano.ActionRange = value);
        if (Plugin.Config.Ayano.ActionRange)
        {
            var value = Plugin.Config.Ayano.ActionRangeValue;
            if (ImGui.DragFloat("Action range bonus", ref value, 0.1f, 0f, 3f, "%.1f"))
            {
                Plugin.Config.Ayano.ActionRangeValue = Math.Clamp(value, 0f, 3f);
                Plugin.Config.Save();
            }
        }

        DrawToggle(
            "Gap-closer range bypass",
            Plugin.Config.Ayano.GapCloserRange,
            value => Plugin.Config.Ayano.GapCloserRange = value);
        DrawToggle(
            "Self-resurrect suppression",
            Plugin.Config.Ayano.SelfResurrect,
            value => Plugin.Config.Ayano.SelfResurrect = value);
        DrawToggle(
            "Fall protection",
            Plugin.Config.Ayano.NoFall,
            value => Plugin.Config.Ayano.NoFall = value);
        DrawToggle(
            "Anti-knockback",
            Plugin.Config.Ayano.AntiKnockback,
            value => Plugin.Config.Ayano.AntiKnockback = value);

        var zOffset = Plugin.Config.Ayano.ZOffset;
        if (ImGui.Checkbox("Z-axis offset", ref zOffset))
        {
            if (zOffset && !Plugin.Config.Ayano.ZOffset)
                Plugin.Config.Ayano.ZOffsetValue = 0f;
            Plugin.Config.Ayano.ZOffset = zOffset;
            Plugin.Config.Save();
        }

        if (Plugin.Config.Ayano.ZOffset)
        {
            var previous = Plugin.Config.Ayano.ZOffsetValue;
            var value = previous;
            if (ImGui.DragFloat("Z offset", ref value, 0.1f, -10f, 10f, "%.1f"))
            {
                value = Math.Clamp(value, -10f, 10f);
                Plugin.Config.Ayano.ZOffsetValue = value;
                ApplyVerticalOffset(value - previous);
                Plugin.Config.Save();
            }
        }

        var debug = Plugin.Config.Ayano.DebugLogging;
        if (ImGui.Checkbox("Debug logging", ref debug))
        {
            Plugin.Config.Ayano.DebugLogging = debug;
            Plugin.Config.Save();
        }

        ImGui.Separator();
        if (ImGui.Button("Teleport to mouse", new Vector2(-1f, 0f)))
            TeleportToMouse();

        if (ImGui.Button("Teleport to map flag"))
            TeleportToFlag();
        ImGui.SameLine();
        if (ImGui.Button("Trigger invincibility"))
            TriggerInvincibility();

        if (diveTeleportHook == null)
            ImGui.TextDisabled("Dive teleport is unavailable for this game build.");
        else if (diveTeleportContext == nint.Zero)
            ImGui.TextDisabled("Dive teleport is waiting for the game context to initialize.");
    }

    public void DrawIChingSettings()
    {
        if (!ImGui.CollapsingHeader("I-Ching tools"))
            return;

        var ignoreCharm = Plugin.Config.Features.IgnoreCharmAndFear;
        if (Plugin.DrawFeatureToggle(
                "ignore charm and fear",
                ignoreCharm,
                value => Plugin.Config.Features.IgnoreCharmAndFear = value))
        {
            UpdateHookStates();
        }
        Plugin.DrawHelp("Blocks the forced-action handler used by I-Ching's charm and fear bypass.");

        var statusBlock = Plugin.Config.Features.StatusBlock;
        if (Plugin.DrawFeatureToggle(
                "status block (sliding)",
                statusBlock,
                value => Plugin.Config.Features.StatusBlock = value))
        {
            UpdateHookStates();
        }
        Plugin.DrawHelp("Filters I-Ching's fixed status list from both local and network status updates.");

        var remoteInteraction = Plugin.Config.Features.FrontlineRemoteInteraction;
        if (Plugin.DrawFeatureToggle(
                "Frontline remote interaction",
                remoteInteraction,
                value => Plugin.Config.Features.FrontlineRemoteInteraction = value))
        {
            UpdateHookStates();
        }
        Plugin.DrawHelp("Mirrors I-Ching's Set 40 action-range preset and only applies while in PvP.");

        var range = Plugin.Config.IChing.FrontlineRangeBonus;
        if (ImGui.DragFloat("Frontline range bonus", ref range, 1f, 0f, 40f, "%.0f"))
        {
            Plugin.Config.IChing.FrontlineRangeBonus = Math.Clamp(range, 0f, 40f);
            Plugin.Config.Save();
        }
    }

    private static bool Enabled(Func<AyanoSettings, bool> selector) =>
        Plugin.ProtectedFeaturesUnlocked &&
        Plugin.Config.Features.AyanoHimituBox &&
        selector(Plugin.Config.Ayano);

    private float SpeedDetour(nint arg1)
    {
        var original = speedHook!.Original(arg1);
        return Enabled(settings => settings.SpeedHack)
            ? original + Plugin.Config.Ayano.SpeedValue
            : original;
    }

    private nint MovePermissionDetour(nint arg1, uint actionId, int arg3, int arg4)
    {
        if (Enabled(settings => settings.MovePermission) &&
            actionId is 96 or 97 or 98 or 99 or 1001 or 1006 or 1007 or 1008)
        {
            return 1;
        }

        return movePermissionHook!.Original(arg1, actionId, arg3, arg4);
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
            return original + Plugin.Config.IChing.FrontlineRangeBonus;

        if (Plugin.ProtectedFeaturesUnlocked &&
            Plugin.Config.Features.AyanoHimituBox &&
            Plugin.Config.Ayano.GapCloserRange &&
            gapCloserActions.Contains(actionId))
        {
            return original + 25f;
        }

        return Plugin.ProtectedFeaturesUnlocked &&
               Plugin.Config.Features.AyanoHimituBox &&
               Plugin.Config.Ayano.ActionRange
            ? original + Plugin.Config.Ayano.ActionRangeValue
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

    private long SelfResurrectDetour(GameObject* player, float x, float y, float z)
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (Enabled(settings => settings.SelfResurrect) &&
            !Plugin.ClientState.IsPvP &&
            localPlayer != null &&
            localPlayer.IsDead &&
            (nint)player == localPlayer.Address)
        {
            return 0;
        }

        return selfResurrectHook!.Original(player, x, y, z);
    }

    private long NoFallDetour(long arg1, uint flags)
    {
        if (Enabled(settings => settings.NoFall) && (flags & 0x700) != 0)
            flags = (flags & ~0x700u) | 2u;

        return noFallHook!.Original(arg1, flags);
    }

    private long AntiKnockbackDetour(
        long gameObject,
        float rotation,
        float distance,
        long duration,
        char arg5,
        int arg6)
    {
        if (Enabled(settings => settings.AntiKnockback))
            distance = 0f;

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

    private void TeleportToMouse()
    {
        if (!Plugin.ProtectedFeaturesUnlocked ||
            !Plugin.Config.Features.AyanoHimituBox)
            return;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null)
            return;

        var position = Vector3.Zero;
        if (!Plugin.GameGui.ScreenToWorld(ImGui.GetIO().MousePos, out position, 100000f))
            return;

        ((GameObject*)localPlayer.Address)->SetPosition(position.X, position.Y, position.Z);
        Debug($"Teleported to mouse position {position}.");
    }

    private void TeleportToFlag()
    {
        if (!Plugin.ProtectedFeaturesUnlocked ||
            !Plugin.Config.Features.AyanoHimituBox)
            return;

        var agentMap = AgentMap.Instance();
        if (agentMap == null || agentMap->FlagMarkerCount <= 0)
        {
            Plugin.Chat.PrintError("[Keita Toolbox] No map flag is available.");
            return;
        }

        var marker = agentMap->FlagMapMarkers[0];
        SendDiveTeleport(new Vector3(marker.XFloat, 0f, marker.YFloat));
    }

    private void TriggerInvincibility()
    {
        if (!Plugin.ProtectedFeaturesUnlocked ||
            !Plugin.Config.Features.AyanoHimituBox)
            return;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer != null)
            SendDiveTeleport(localPlayer.Position);
    }

    private void SendDiveTeleport(Vector3 position)
    {
        if (diveTeleportHook == null || diveTeleportContext == nint.Zero)
        {
            Plugin.Chat.PrintError("[Keita Toolbox] Dive teleport is not ready.");
            return;
        }

        const int packetSize = 58;
        var packet = Marshal.AllocHGlobal(packetSize);
        try
        {
            new Span<byte>((void*)packet, packetSize).Clear();
            *(int*)(packet + 0) = 554;
            *(int*)(packet + 8) = 56;
            *(float*)(packet + 32) = Plugin.ObjectTable.LocalPlayer?.Rotation ?? 0f;
            *(Vector3*)(packet + 36) = position;
            diveTeleportHook.Original(diveTeleportContext, packet, packet, 1);
            Debug($"Sent dive teleport to {position}.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Dive teleport failed.");
            Plugin.Chat.PrintError("[Keita Toolbox] Dive teleport failed. Check the Dalamud log.");
        }
        finally
        {
            Marshal.FreeHGlobal(packet);
        }
    }

    private static void ApplyVerticalOffset(float delta)
    {
        if (!Plugin.ProtectedFeaturesUnlocked ||
            !Plugin.Config.Features.AyanoHimituBox ||
            Math.Abs(delta) < 0.001f)
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

    private static void DrawToggle(string label, bool value, Action<bool> setter)
    {
        var changed = value;
        if (!ImGui.Checkbox(label, ref changed))
            return;

        setter(changed);
        Plugin.Config.Save();
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
            Plugin.Log.Error(ex, "Failed to initialize Ayano function: {Feature}.", name);
            return null;
        }
    }

    private void UpdateHookStates()
    {
        var protectionUnlocked = Plugin.ProtectedFeaturesUnlocked;
        var ayanoEnabled = protectionUnlocked && Plugin.Config.Features.AyanoHimituBox;
        SetHookEnabled(speedHook, ayanoEnabled);
        SetHookEnabled(movePermissionHook, ayanoEnabled);
        SetHookEnabled(skillPostActionMoveHook, ayanoEnabled);
        SetHookEnabled(
            actionRangeHook,
            ayanoEnabled ||
            (protectionUnlocked && Plugin.Config.Features.FrontlineRemoteInteraction));
        SetHookEnabled(selfResurrectHook, ayanoEnabled);
        SetHookEnabled(noFallHook, ayanoEnabled);
        SetHookEnabled(antiKnockbackHook, ayanoEnabled);
        SetHookEnabled(diveTeleportHook, ayanoEnabled);
        SetHookEnabled(
            forcedActionHook,
            protectionUnlocked && Plugin.Config.Features.IgnoreCharmAndFear);
        SetHookEnabled(
            statusManagerHook,
            protectionUnlocked && Plugin.Config.Features.StatusBlock);
        SetHookEnabled(
            statusPacketHook,
            protectionUnlocked && Plugin.Config.Features.StatusBlock);
        if (!ayanoEnabled)
            diveTeleportContext = nint.Zero;
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
        if (Plugin.Config.Ayano.DebugLogging)
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
