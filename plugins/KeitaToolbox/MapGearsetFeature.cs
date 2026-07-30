using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace KeitaToolbox;

internal sealed unsafe class MapGearsetFeature : IDisposable
{
    private const string ScheduleGroup = "MapGearsetSwitch";
    private const int RetryDelayMs = 1000;
    private const int MaxAttempts = 30;
    private const int MaxGearsets = 100;

    public MapGearsetFeature()
    {
        Plugin.ClientState.TerritoryChanged += OnTerritoryChanged;
    }

    public void Dispose()
    {
        Plugin.ClientState.TerritoryChanged -= OnTerritoryChanged;
        Plugin.Scheduler.Cancel(ScheduleGroup);
    }

    private void OnTerritoryChanged(uint territory) => ScheduleForTerritory(territory);

    public void ApplyCurrentTerritory()
    {
        ScheduleForTerritory(Plugin.ClientState.TerritoryType);
    }

    private static void ScheduleForTerritory(uint territory)
    {
        Plugin.Scheduler.Cancel(ScheduleGroup);
        if (!Plugin.Config.Features.MapGearsetSwitch)
            return;

        var rule = Plugin.Config.MapGearset.Rules.Find(
            item => item.TerritoryId == territory && item.GearsetIndex >= 0);
        if (rule == null)
            return;

        Plugin.Scheduler.Schedule(
            ScheduleGroup,
            Plugin.Config.MapGearset.DelayMs,
            () => TrySwitch(territory, rule.GearsetIndex, 0));
    }

    private static void TrySwitch(uint territory, int gearsetIndex, int attempt)
    {
        if (!Plugin.Config.Features.MapGearsetSwitch ||
            Plugin.ClientState.TerritoryType != territory)
        {
            return;
        }

        if (!IsReadyToSwitch())
        {
            Retry(territory, gearsetIndex, attempt, "the character remained busy");
            return;
        }

        var module = RaptureGearsetModule.Instance();
        if (module == null ||
            gearsetIndex is < 0 or >= MaxGearsets ||
            !module->IsValidGearset(gearsetIndex))
        {
            Plugin.Log.Warning(
                "Map gearset rule for territory {Territory} refers to unavailable gearset index {GearsetIndex}.",
                territory,
                gearsetIndex);
            return;
        }

        if (module->CurrentGearsetIndex == gearsetIndex)
            return;

        var gearset = module->GetGearset(gearsetIndex);
        if (gearset == null)
        {
            Plugin.Log.Warning(
                "Map gearset rule for territory {Territory} could not read gearset index {GearsetIndex}.",
                territory,
                gearsetIndex);
            return;
        }

        var result = module->EquipGearset(gearset->Id, gearset->GlamourSetLink);
        if (result != 0)
        {
            Retry(territory, gearsetIndex, attempt, $"EquipGearset returned {result}");
            return;
        }

        Plugin.Log.Information(
            "Switched to gearset {Gearset} for territory {Territory}.",
            gearset->NameString,
            territory);
        if (Plugin.Config.MapGearset.PrintChatMessage)
        {
            Plugin.Chat.Print(
                $"[Keita 工具箱] 已切换到装备套装 #{gearset->Id + 1}：{gearset->NameString}");
        }
    }

    private static bool IsReadyToSwitch()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        return player is { IsDead: false } &&
               !Plugin.Condition[ConditionFlag.InCombat] &&
               !Plugin.Condition[ConditionFlag.BetweenAreas] &&
               !Plugin.Condition[ConditionFlag.BetweenAreas51] &&
               !Plugin.Condition[ConditionFlag.OccupiedInQuestEvent] &&
               !Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] &&
               !Plugin.Condition[ConditionFlag.WatchingCutscene];
    }

    private static void Retry(uint territory, int gearsetIndex, int attempt, string reason)
    {
        if (attempt + 1 >= MaxAttempts)
        {
            Plugin.Log.Warning(
                "Map gearset switch for territory {Territory} stopped after {Attempts} attempts because {Reason}.",
                territory,
                MaxAttempts,
                reason);
            return;
        }

        Plugin.Scheduler.Schedule(
            ScheduleGroup,
            RetryDelayMs,
            () => TrySwitch(territory, gearsetIndex, attempt + 1));
    }

    public void DrawSettings()
    {
        if (!ImGui.CollapsingHeader("按地图自动切换装备套装"))
            return;

        Plugin.DrawFeatureToggle(
            "按地图自动切换装备套装",
            Plugin.Config.Features.MapGearsetSwitch,
            value =>
            {
                Plugin.Config.Features.MapGearsetSwitch = value;
                if (value)
                    ApplyCurrentTerritory();
                else
                    Plugin.Scheduler.Cancel(ScheduleGroup);
            });

        var delay = Plugin.Config.MapGearset.DelayMs;
        if (ImGui.InputInt("切换延迟（毫秒）", ref delay))
        {
            Plugin.Config.MapGearset.DelayMs = Math.Max(0, delay);
            Plugin.Config.Save();
        }

        var printMessage = Plugin.Config.MapGearset.PrintChatMessage;
        if (ImGui.Checkbox("切换后在聊天栏提示", ref printMessage))
        {
            Plugin.Config.MapGearset.PrintChatMessage = printMessage;
            Plugin.Config.Save();
        }

        ImGui.Separator();
        for (var index = 0; index < Plugin.Config.MapGearset.Rules.Count; index++)
        {
            var rule = Plugin.Config.MapGearset.Rules[index];
            ImGui.PushID(index);
            ImGui.TextUnformatted($"地图规则 {index + 1}");
            ImGui.SameLine();
            if (ImGui.SmallButton("移除规则"))
            {
                Plugin.Config.MapGearset.Rules.RemoveAt(index);
                Plugin.Config.Save();
                ImGui.PopID();
                break;
            }

            var territory = (int)rule.TerritoryId;
            if (ImGui.InputInt("地图 ID", ref territory))
            {
                rule.TerritoryId = (uint)Math.Max(0, territory);
                Plugin.Config.Save();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton($"使用当前地图（{Plugin.ClientState.TerritoryType}）"))
            {
                rule.TerritoryId = Plugin.ClientState.TerritoryType;
                Plugin.Config.Save();
            }

            DrawGearsetSelector(rule);
            ImGui.Separator();
            ImGui.PopID();
        }

        if (ImGui.Button("添加地图规则"))
        {
            Plugin.Config.MapGearset.Rules.Add(new MapGearsetRule
            {
                TerritoryId = Plugin.ClientState.TerritoryType,
            });
            Plugin.Config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("立即应用当前地图规则"))
            ApplyCurrentTerritory();

        Plugin.DrawHelp(
            "同一地图只使用第一条匹配规则；读图、战斗、剧情或任务交互结束后才会切换。");
    }

    private static void DrawGearsetSelector(MapGearsetRule rule)
    {
        var preview = GetGearsetLabel(rule.GearsetIndex);
        if (!ImGui.BeginCombo("装备套装", preview))
            return;

        var module = RaptureGearsetModule.Instance();
        if (module != null)
        {
            for (var index = 0; index < MaxGearsets; index++)
            {
                if (!module->IsValidGearset(index))
                    continue;

                var selected = rule.GearsetIndex == index;
                if (ImGui.Selectable(GetGearsetLabel(index), selected))
                {
                    rule.GearsetIndex = index;
                    Plugin.Config.Save();
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
        }

        ImGui.EndCombo();
    }

    private static string GetGearsetLabel(int index)
    {
        var module = RaptureGearsetModule.Instance();
        if (module == null ||
            index is < 0 or >= MaxGearsets ||
            !module->IsValidGearset(index))
        {
            return "请选择装备套装";
        }

        var gearset = module->GetGearset(index);
        return gearset == null
            ? "请选择装备套装"
            : $"#{gearset->Id + 1} {gearset->NameString}";
    }
}
