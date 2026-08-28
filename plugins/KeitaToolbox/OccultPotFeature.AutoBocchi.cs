using System;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using OmenTools;
using OmenTools.Info.Game;
using OmenTools.OmenService;
using static OmenTools.Global.Globals;

namespace KeitaToolbox;

internal sealed partial class OccultPotFeature
{
    private const string AutoBocchiScheduleGroup = "OccultAutoBocchiOnEntry";
    private const int AutoBocchiInitialDelayMs = 8_000;
    private const int AutoBocchiRetryDelayMs = 1_000;
    private const int AutoBocchiMaxAttempts = 60;

    private void ScheduleAutoBocchiIllegalOnEntry(uint territoryID)
    {
        Plugin.Scheduler.Cancel(AutoBocchiScheduleGroup);
        if (!OccultAutoBocchiPolicy.ShouldSchedule(config.EnableBocchiIllegalOnEntry, territoryID))
            return;

        Plugin.Scheduler.Schedule(
            AutoBocchiScheduleGroup,
            AutoBocchiInitialDelayMs,
            () => TryEnableBocchiIllegalOnEntry(territoryID, 0));
    }

    private void TryEnableBocchiIllegalOnEntry(uint territoryID, int attempt)
    {
        if (!OccultAutoBocchiPolicy.ShouldSchedule(config.EnableBocchiIllegalOnEntry, territoryID) ||
            GameState.TerritoryType != territoryID)
            return;

        var bocchiLoaded = Plugin.PluginInterface.InstalledPlugins.Any(plugin =>
            plugin.InternalName.Equals("BOCCHI", StringComparison.OrdinalIgnoreCase) &&
            plugin.IsLoaded);
        var playerAvailable = DService.Instance().ObjectTable.LocalPlayer is { IsDead: false };
        var condition = DService.Instance().Condition;
        var betweenAreas = condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51];

        var command = OccultAutoBocchiPolicy.BuildCommand(
            config.EnableBocchiIllegalOnEntry,
            territoryID,
            bocchiLoaded,
            playerAvailable,
            betweenAreas);
        if (command == null)
        {
            if (attempt + 1 >= AutoBocchiMaxAttempts)
            {
                DService.Instance().Log.Warning(
                    "[KeitaToolbox.MagicPot] BOCCHI illegal mode was not enabled because the game or plugin was not ready");
                return;
            }

            Plugin.Scheduler.Schedule(
                AutoBocchiScheduleGroup,
                AutoBocchiRetryDelayMs,
                () => TryEnableBocchiIllegalOnEntry(territoryID, attempt + 1));
            return;
        }

        SendCommand(command);
        DService.Instance().Log.Information(
            "[KeitaToolbox.MagicPot] BOCCHI illegal mode enabled on Crescent entry");
    }
}
