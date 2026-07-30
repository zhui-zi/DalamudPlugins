using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using FFXIVClientStructs.FFXIV.Client.Game;
using ContentsFinder = FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsFinder;

namespace KeitaToolbox;

internal sealed unsafe class AutoLeaveFeature : IDisposable
{
    private const string ScheduleGroup = "AutoLeaveDuty";
    private const byte MentorRouletteId = 9;
    private const int LeaveDutyCommand = 819;

    private string leaveSearch = string.Empty;
    private string immediateLeaveSearch = string.Empty;

    public AutoLeaveFeature()
    {
        Plugin.DutyState.DutyCompleted += OnDutyCompleted;
        Plugin.ClientState.TerritoryChanged += OnTerritoryChanged;
    }

    public void Dispose()
    {
        Plugin.ClientState.TerritoryChanged -= OnTerritoryChanged;
        Plugin.DutyState.DutyCompleted -= OnDutyCompleted;
        Plugin.Scheduler.Cancel(ScheduleGroup);
    }

    private void OnDutyCompleted(IDutyStateEventArgs args)
    {
        if (!Plugin.Config.Features.AutoLeaveDuty)
            return;

        var contentId = args.ContentFinderCondition.RowId;
        if (Plugin.Config.Duty.ImmediateLeaveWhitelist.Contains(contentId))
        {
            ScheduleLeave(0, inactive: true);
            return;
        }

        var shouldLeave = Plugin.Config.Duty.LeaveWhitelist.Contains(contentId) ||
                          (Plugin.Config.Duty.LeaveMentorRoulette && IsMentorRoulette());
        if (!shouldLeave)
            return;

        if (Plugin.Config.Duty.SkipHighEndDuties &&
            args.ContentFinderCondition.Value.HighEndDuty)
        {
            return;
        }

        if (Plugin.Config.Duty.ForceLeave)
        {
            ScheduleLeave(Plugin.Config.Duty.LeaveDelayMs, inactive: true);
            return;
        }

        var territory = Plugin.ClientState.TerritoryType;
        Plugin.Scheduler.Cancel(ScheduleGroup);
        Plugin.Scheduler.Schedule(
            ScheduleGroup,
            Plugin.Config.Duty.LeaveDelayMs,
            () => LeaveWhenOutOfCombat(territory));
    }

    private static void ScheduleLeave(int delayMs, bool inactive)
    {
        Plugin.Scheduler.Cancel(ScheduleGroup);
        Plugin.Scheduler.Schedule(
            ScheduleGroup,
            delayMs,
            () =>
            {
                if (Plugin.Config.Features.AutoLeaveDuty)
                    GameMain.ExecuteCommand(LeaveDutyCommand, inactive ? 1 : 0);
            });
    }

    private static void LeaveWhenOutOfCombat(uint territory)
    {
        if (!Plugin.Config.Features.AutoLeaveDuty ||
            Plugin.ClientState.TerritoryType != territory)
        {
            return;
        }

        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            Plugin.Scheduler.Schedule(
                ScheduleGroup,
                500,
                () => LeaveWhenOutOfCombat(territory));
            return;
        }

        GameMain.ExecuteCommand(LeaveDutyCommand);
    }

    private void OnTerritoryChanged(uint _) => Plugin.Scheduler.Cancel(ScheduleGroup);

    private static bool IsMentorRoulette()
    {
        var contentsFinder = ContentsFinder.Instance();
        if (contentsFinder == null)
            return false;

        var queueInfo = contentsFinder->GetQueueInfo();
        return queueInfo != null && queueInfo->QueuedContentRouletteId == MentorRouletteId;
    }

    public void DrawSettings()
    {
        if (!ImGui.CollapsingHeader("Automatic duty leave"))
            return;

        Plugin.DrawFeatureToggle(
            "automatic duty leave",
            Plugin.Config.Features.AutoLeaveDuty,
            value =>
            {
                Plugin.Config.Features.AutoLeaveDuty = value;
                if (!value)
                    Plugin.Scheduler.Cancel(ScheduleGroup);
            });

        var delay = Plugin.Config.Duty.LeaveDelayMs;
        if (ImGui.InputInt("Leave delay (ms)", ref delay))
        {
            Plugin.Config.Duty.LeaveDelayMs = Math.Max(0, delay);
            Plugin.Config.Save();
        }

        var force = Plugin.Config.Duty.ForceLeave;
        if (ImGui.Checkbox("Force leave without waiting for combat to end", ref force))
        {
            Plugin.Config.Duty.ForceLeave = force;
            Plugin.Config.Save();
        }

        var skipHighEnd = Plugin.Config.Duty.SkipHighEndDuties;
        if (ImGui.Checkbox("Do not leave high-end duties automatically", ref skipHighEnd))
        {
            Plugin.Config.Duty.SkipHighEndDuties = skipHighEnd;
            Plugin.Config.Save();
        }

        var mentor = Plugin.Config.Duty.LeaveMentorRoulette;
        if (ImGui.Checkbox("Always leave Mentor Roulette duties", ref mentor))
        {
            Plugin.Config.Duty.LeaveMentorRoulette = mentor;
            Plugin.Config.Save();
        }

        ImGui.TextUnformatted("Normal leave whitelist");
        BasicFeatures.DrawDutySelector(
            "NormalLeaveDutySelector",
            "Checked duties leave after the delay and, unless forced, after combat ends.",
            ref leaveSearch,
            Plugin.Config.Duty.LeaveWhitelist);

        ImGui.TextUnformatted("Immediate leave whitelist");
        BasicFeatures.DrawDutySelector(
            "ImmediateLeaveDutySelector",
            "Checked duties use the inactive leave path immediately after completion.",
            ref immediateLeaveSearch,
            Plugin.Config.Duty.ImmediateLeaveWhitelist);
    }
}
