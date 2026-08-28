using System;
using System.Drawing;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility.Raii;
using OmenTools;
using OmenTools.Extensions;
using OmenTools.Info.Game;
using OmenTools.OmenService;

namespace KeitaToolbox;

internal sealed partial class OccultPotFeature
{
    private const string AutoDiscardScheduleGroup = "OccultAutoDiscardOnEntry";
    private const int AutoDiscardInitialDelayMs = 8_000;
    private const int AutoDiscardRetryDelayMs = 1_000;
    private const int AutoDiscardMaxAttempts = 60;
    private long autoDiscardReadySince;

    private void ConfigUIEntryActions()
    {
        ConfigSection("BOCCHI");
        using (ImRaii.PushIndent())
        {
            if (ImGui.Checkbox("进入新月岛时自动开启 BOCCHI 非法模式", ref config.EnableBocchiIllegalOnEntry))
                config.Save(this);

            ImGui.TextColored(
                KnownColor.Gray.ToVector4(),
                "下次进入南征或北征并等待角色就绪后开启一次；需要保持 BOCCHI 启用。");
        }

        ConfigSection("自动丢弃物品");
        using (ImRaii.PushIndent())
        {
            if (ImGui.Checkbox("进入新月岛时自动运行 DR 丢弃物品组", ref config.EnableAutoDiscardOnEntry))
                config.Save(this);

            var groupName = config.AutoDiscardGroupName;
            if (ImGui.InputText("物品组名称", ref groupName, 100))
            {
                config.AutoDiscardGroupName = groupName;
                config.Save(this);
            }

            ImGui.TextColored(
                KnownColor.Gray.ToVector4(),
                "填写 DailyRoutines「自动丢弃物品」中的物品组名称；下次进入南征或北征时运行一次。\n" +
                "DailyRoutines 及其自动丢弃物品模块需要保持启用。");
        }
    }

    private void ScheduleAutoDiscardOnEntry(uint territoryID)
    {
        Plugin.Scheduler.Cancel(AutoDiscardScheduleGroup);
        autoDiscardReadySince = 0;
        if (OccultAutoDiscardPolicy.BuildCommand(
                config.EnableAutoDiscardOnEntry,
                territoryID,
                config.AutoDiscardGroupName) == null)
            return;

        Plugin.Scheduler.Schedule(
            AutoDiscardScheduleGroup,
            AutoDiscardInitialDelayMs,
            () => TryRunAutoDiscardOnEntry(territoryID, 0));
    }

    private void TryRunAutoDiscardOnEntry(uint territoryID, int attempt)
    {
        var command = OccultAutoDiscardPolicy.BuildCommand(
            config.EnableAutoDiscardOnEntry,
            territoryID,
            config.AutoDiscardGroupName);
        if (command == null || GameState.TerritoryType != territoryID)
        {
            autoDiscardReadySince = 0;
            return;
        }

        var dailyRoutinesLoaded = Plugin.PluginInterface.InstalledPlugins.Any(plugin =>
            plugin.InternalName.Equals("DailyRoutines", StringComparison.OrdinalIgnoreCase) &&
            plugin.IsLoaded);
        var localPlayer = DService.Instance().ObjectTable.LocalPlayer;
        var playerAvailable = localPlayer is { IsDead: false };
        var condition = DService.Instance().Condition;
        var stateAllowsInventoryAction =
            !condition[ConditionFlag.BetweenAreas] &&
            !condition[ConditionFlag.BetweenAreas51] &&
            !condition[ConditionFlag.Occupied] &&
            !condition[ConditionFlag.Occupied30] &&
            !condition[ConditionFlag.OccupiedInEvent] &&
            !condition[ConditionFlag.OccupiedInQuestEvent] &&
            !condition[ConditionFlag.Occupied33] &&
            !condition[ConditionFlag.OccupiedInCutSceneEvent] &&
            !condition[ConditionFlag.Occupied38] &&
            !condition[ConditionFlag.Occupied39] &&
            !condition[ConditionFlag.Casting] &&
            !condition[ConditionFlag.InCombat] &&
            !condition[ConditionFlag.TradeOpen] &&
            !condition[ConditionFlag.WaitingForDuty] &&
            !condition[ConditionFlag.WatchingCutscene] &&
            !condition[ConditionFlag.WatchingCutscene78] &&
            !condition[ConditionFlag.BeingMoved];
        var now = Environment.TickCount64;
        if (!dailyRoutinesLoaded || !playerAvailable || !stateAllowsInventoryAction)
        {
            autoDiscardReadySince = 0;
            if (attempt + 1 >= AutoDiscardMaxAttempts)
            {
                DService.Instance().Log.Warning(
                    "[KeitaToolbox.MagicPot] DailyRoutines discard group was not dispatched because the game or plugin was not ready");
                return;
            }

            Plugin.Scheduler.Schedule(
                AutoDiscardScheduleGroup,
                AutoDiscardRetryDelayMs,
                () => TryRunAutoDiscardOnEntry(territoryID, attempt + 1));
            return;
        }

        if (autoDiscardReadySince == 0)
            autoDiscardReadySince = now;

        if (!OccultAutoDiscardPolicy.HasStableReadyState(
                dailyRoutinesLoaded,
                playerAvailable,
                stateAllowsInventoryAction,
                now - autoDiscardReadySince))
        {
            Plugin.Scheduler.Schedule(
                AutoDiscardScheduleGroup,
                AutoDiscardRetryDelayMs,
                () => TryRunAutoDiscardOnEntry(territoryID, attempt + 1));
            return;
        }

        SendCommand(command);
        autoDiscardReadySince = 0;
        DService.Instance().Log.Information(
            "[KeitaToolbox.MagicPot] DailyRoutines discard group dispatched on Crescent entry: {GroupName}",
            config.AutoDiscardGroupName.Trim());
    }
}
