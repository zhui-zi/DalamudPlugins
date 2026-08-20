using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using OmenTools;
using OmenTools.Info.Game.Packets.Upstream;
using OmenTools.OmenService;

namespace KeitaToolbox;

internal sealed unsafe class VoidAetherFeature : IDisposable
{
    private const uint RepairEventId = 720915;
    private const uint CompanyChestEventId = 720995;
    private const uint SummoningBellEventId = 721440;
    private const uint RivalWingsTerritoryId = 888;
    private const uint ShatterTerritoryId = 431;
    private const uint RivalWingsFirstEventId = 983864;

    private static readonly uint[] BicolorShopEventIds =
    [
        1770746,
        1770736,
        1770726,
        1770716,
    ];

    private static readonly KeyValuePair<string, uint>[] ShatterPoints =
    [
        new("A1", 983500),
        new("A2", 983501),
        new("A3", 983502),
        new("A4", 983503),
        new("B1", 983504),
        new("B2", 983506),
        new("B3", 983505),
        new("B4", 983507),
        new("C1", 983513),
        new("C2", 983508),
        new("C3", 983512),
        new("C4", 983511),
        new("D1", 983514),
        new("D2", 983509),
        new("D3", 983510),
    ];

    private readonly List<AetheryteEntry> aetherytes = [];
    private readonly List<AetherCurrentEntry> aetherCurrents = [];
    private uint cachedTerritory;
    private bool refreshUnlockState = true;
    private long lastUnlockRefreshAt;
    private bool battlefieldWindowOpen;

    public VoidAetherFeature()
    {
        Plugin.PluginInterface.UiBuilder.Draw += DrawBattlefieldWindow;
        RefreshTerritoryData(Plugin.ClientState.TerritoryType);
        Plugin.Log.Information("Void aether tools initialized.");
    }

    public void Dispose()
    {
        Plugin.PluginInterface.UiBuilder.Draw -= DrawBattlefieldWindow;
        aetherytes.Clear();
        aetherCurrents.Clear();
        Plugin.Log.Information("Void aether tools disposed.");
    }

    public void DrawCharacterAndInterfaceSettings()
    {
        if (!ImGui.CollapsingHeader("装备与雇员服务", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (ImGui.Button("虚空修理工"))
            OpenRepair();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("打开虚空修理工（需周围存在NPC）");

        ImGui.SameLine();
        if (ImGui.Button("虚空传唤铃"))
            OpenSummoningBell();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("打开虚空传唤铃");

        Plugin.DrawHelp("通过游戏事件打开装备维护与雇员服务。");
    }

    public void DrawPartyAndTradeSettings()
    {
        if (!ImGui.CollapsingHeader("储物与商店服务", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (ImGui.Button("部队储物柜"))
            OpenCompanyChest();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("打开部队储物柜");

        ImGui.SameLine();
        if (ImGui.Button("双色宝石4级商店（图莱尤拉）"))
            OpenBicolorShop();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("无视等级限制打开当前地图的4级双色宝石商店");

        Plugin.DrawHelp("通过游戏事件打开储物与商店服务。");
    }

    public void DrawMovementAndSystemSettings()
    {
        EnsureTerritoryData();
        RefreshUnlockStates();

        DrawAetheryteUnlocks();
        DrawAetherCurrentUnlocks();
    }

    public void DrawCombatAndStatusSettings()
    {
        DrawBattlefieldPoints();
    }

    public bool HandleCommand(string arguments)
    {
        var parts = arguments.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 ||
            (!parts[0].Equals("bf", StringComparison.OrdinalIgnoreCase) &&
             !parts[0].Equals("battlefield", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (parts.Length == 1)
        {
            battlefieldWindowOpen = true;
            return true;
        }

        TouchBattlefieldPoint(parts[1]);
        return true;
    }

    private void DrawAetheryteUnlocks()
    {
        if (!ImGui.CollapsingHeader("虚空水晶共鸣", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (aetherytes.Count == 0)
        {
            ImGui.TextDisabled("当前地形没有可供解锁的水晶!");
            return;
        }

        var aethernetEntries = aetherytes.Where(entry => entry.IsAethernet).ToList();
        if (aethernetEntries.Count > 0)
        {
            ImGui.TextUnformatted("城内以太之光:");
            foreach (var entry in aethernetEntries)
            {
                if (ImGui.SmallButton($"{entry.AethernetName}##Aethernet-{entry.Id}"))
                    UnlockAetheryte(entry.Id);
                ImGui.SameLine();
            }
            ImGui.NewLine();
            ImGui.Separator();
        }

        ImGui.TextUnformatted("水晶解锁:");
        foreach (var entry in aetherytes)
        {
            var displayName = entry.IsAethernet
                ? $"({entry.AethernetName})"
                : entry.Name;
            var status = entry.IsUnlocked
                ? "[已解锁]"
                : entry.PrerequisiteComplete
                    ? string.Empty
                    : "[前置任务未完成]";

            ImGui.TextUnformatted($"{entry.Id} | {displayName} {status}");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(entry.IsUnlocked
                    ? "此水晶已解锁"
                    : !entry.PrerequisiteComplete
                        ? "需要完成前置任务"
                        : "点击解锁按钮进行虚空共鸣");
            }

            ImGui.SameLine();
            ImGui.BeginDisabled(!entry.PrerequisiteComplete || entry.IsUnlocked);
            if (ImGui.SmallButton($"解锁##AetheryteUnlock-{entry.Id}"))
                UnlockAetheryte(entry.Id);
            ImGui.EndDisabled();
        }
    }

    private void DrawAetherCurrentUnlocks()
    {
        if (!ImGui.CollapsingHeader("风脉解锁", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (aetherCurrents.Count == 0)
        {
            ImGui.TextDisabled("当前地形没有可供解锁的风脉泉!");
            return;
        }

        foreach (var entry in aetherCurrents)
        {
            var status = entry.IsUnlocked ? " [已解锁]" : string.Empty;
            ImGui.TextUnformatted($"{entry.Id} | 风脉泉{status}");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(entry.IsUnlocked
                    ? "此风脉泉已解锁"
                    : "点击解锁按钮进行虚空共鸣");
            }

            ImGui.SameLine();
            ImGui.BeginDisabled(entry.IsUnlocked);
            if (ImGui.SmallButton($"解锁##AetherCurrentUnlock-{entry.Id}"))
                UnlockAetherCurrent(entry.Id);
            ImGui.EndDisabled();
        }
    }

    private void DrawBattlefieldPoints()
    {
        if (!ImGui.CollapsingHeader("战场虚空摸点", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (ImGui.Button("独立窗口##OpenBattlefieldWindow"))
            battlefieldWindowOpen = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("打开独立的战场摸点窗口 命令: /ktb bf");

        ImGui.Spacing();
        DrawBattlefieldPointGrid();
    }

    private void DrawBattlefieldWindow()
    {
        if (!battlefieldWindowOpen)
            return;

        ImGui.SetNextWindowSize(new System.Numerics.Vector2(420f, 0f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("战场虚空摸点##BattlefieldTouchWindow", ref battlefieldWindowOpen))
        {
            ImGui.End();
            return;
        }

        var territory = Plugin.ClientState.TerritoryType;
        var mapName = territory switch
        {
            RivalWingsTerritoryId => "大草原",
            ShatterTerritoryId => "尘封秘岩",
            _ => "非战场地图",
        };
        ImGui.TextUnformatted($"当前地图: {mapName} ({territory})");
        ImGui.Separator();
        DrawBattlefieldPointGrid();
        ImGui.End();
    }

    private static void DrawBattlefieldPointGrid()
    {
        switch (Plugin.ClientState.TerritoryType)
        {
            case RivalWingsTerritoryId:
                ImGui.TextUnformatted("大草原");
                ImGui.TextDisabled("点击按钮快速触摸对应点位");
                for (var index = 1; index <= 13; index++)
                {
                    if (ImGui.Button($"{index}##RivalWingsPoint-{index}"))
                        TouchRivalWingsPoint(index);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"点击按钮可快速触摸对应点位 {index}");
                    if (index % 4 != 0 && index != 13)
                        ImGui.SameLine();
                }

                if (ImGui.Button("触摸全部##RivalWingsAll"))
                    TouchAllBattlefieldPoints();
                break;
            case ShatterTerritoryId:
                ImGui.TextUnformatted("尘封秘岩");
                ImGui.TextDisabled("点击按钮快速触摸对应点位");
                for (var index = 0; index < ShatterPoints.Length; index++)
                {
                    var point = ShatterPoints[index];
                    if (ImGui.Button($"{point.Key}##ShatterPoint-{point.Key}"))
                        TouchShatterPoint(point.Key);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"点击按钮可快速触摸对应点位 {point.Key}");
                    if ((index + 1) % 4 != 0 && index != ShatterPoints.Length - 1)
                        ImGui.SameLine();
                }

                if (ImGui.Button("触摸全部##ShatterAll"))
                    TouchAllBattlefieldPoints();
                break;
            default:
                ImGui.TextDisabled("战场虚空摸点仅在以下地图可用:");
                ImGui.TextDisabled("- 尘封秘岩 (431)");
                ImGui.TextDisabled("- 大草原 (888)");
                break;
        }
    }

    private void EnsureTerritoryData()
    {
        var territory = Plugin.ClientState.TerritoryType;
        if (territory != cachedTerritory)
            RefreshTerritoryData(territory);
    }

    private void RefreshTerritoryData(uint territory)
    {
        cachedTerritory = territory;
        aetherytes.Clear();
        aetherCurrents.Clear();

        var aetheryteSheet = Plugin.Data.GetExcelSheet<Aetheryte>();
        if (aetheryteSheet != null)
        {
            foreach (var row in aetheryteSheet)
            {
                if (row.Territory.RowId != territory || row.RowId > ushort.MaxValue)
                    continue;

                var name = row.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty;
                var aethernetName = row.AethernetName.ValueNullable?.Name.ToString() ?? string.Empty;
                var requiredQuestId = row.RequiredQuest.RowId;
                aetherytes.Add(new AetheryteEntry(
                    (ushort)row.RowId,
                    name,
                    aethernetName,
                    !string.IsNullOrEmpty(aethernetName),
                    requiredQuestId == 0 || QuestManager.IsQuestComplete(requiredQuestId)));
            }
        }

        var currentSheet = Plugin.Data.GetExcelSheet<AetherCurrentCompFlgSet>();
        if (currentSheet != null)
        {
            foreach (var row in currentSheet)
            {
                if (row.Territory.RowId != territory)
                    continue;

                for (var index = 0; index <= 14; index++)
                {
                    try
                    {
                        var current = row.AetherCurrents[index].ValueNullable;
                        if (current is { RowId: not 0 } && current.Value.Quest.RowId == 0)
                            aetherCurrents.Add(new AetherCurrentEntry(current.Value.RowId));
                    }
                    catch
                    {
                    }
                }

                break;
            }
        }

        refreshUnlockState = true;
        RefreshUnlockStates();
        Plugin.Log.Debug(
            "Loaded {AetheryteCount} aetherytes and {CurrentCount} field currents for territory {Territory}.",
            aetherytes.Count,
            aetherCurrents.Count,
            territory);
    }

    private void RefreshUnlockStates()
    {
        var now = Environment.TickCount64;
        if (!refreshUnlockState && now - lastUnlockRefreshAt < 1_000)
            return;

        try
        {
            var unlockedAetherytes = new HashSet<ushort>();
            var aetheryteList = DService.Instance().AetheryteList;
            if (aetheryteList != null)
            {
                for (var index = 0; index < aetheryteList.Length; index++)
                {
                    var entry = aetheryteList[index];
                    if (entry != null)
                        unlockedAetherytes.Add((ushort)entry.AetheryteID);
                }
            }

            foreach (var entry in aetherytes)
                entry.IsUnlocked = unlockedAetherytes.Contains(entry.Id);

            var playerState = PlayerState.Instance();
            if (playerState != null)
            {
                foreach (var entry in aetherCurrents)
                    entry.IsUnlocked = playerState->IsAetherCurrentUnlocked(entry.Id);
            }

            refreshUnlockState = false;
            lastUnlockRefreshAt = now;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to refresh void aether unlock states.");
        }
    }

    private static bool CanSend()
    {
        if (!Plugin.ProtectedFeaturesUnlocked)
        {
            Plugin.Chat.PrintError("[Keita 工具箱] 请先解锁受保护的高级工具。");
            return false;
        }

        if (Plugin.ObjectTable.LocalPlayer == null || LocalPlayerState.EntityID == 0)
        {
            Plugin.Chat.PrintError("[Keita 工具箱] 玩家对象未找到。");
            return false;
        }

        return true;
    }

    private static bool SendPlayerEvent(uint eventId)
    {
        if (!CanSend())
            return false;

        new EventStartPackt(LocalPlayerState.EntityID, eventId).Send();
        return true;
    }

    private static void OpenRepair()
    {
        if (SendPlayerEvent(RepairEventId))
            Plugin.Chat.Print("[Keita 工具箱] 已打开虚空修理工。");
    }

    private static void OpenCompanyChest()
    {
        if (SendPlayerEvent(CompanyChestEventId))
            Plugin.Chat.Print("[Keita 工具箱] 已打开部队储物柜。");
    }

    private static void OpenSummoningBell()
    {
        if (SendPlayerEvent(SummoningBellEventId))
            Plugin.Chat.Print("[Keita 工具箱] 已打开传唤铃。");
    }

    private static void OpenBicolorShop()
    {
        if (!CanSend())
            return;

        foreach (var eventId in BicolorShopEventIds)
            new EventStartPackt(LocalPlayerState.EntityID, eventId).Send();
        Plugin.Chat.Print("[Keita 工具箱] 已尝试打开4级双色宝石商店。");
    }

    private void UnlockAetheryte(ushort aetheryteId)
    {
        var eventId = 0x00050000u | aetheryteId;
        if (!SendPlayerEvent(eventId))
            return;

        refreshUnlockState = true;
        Plugin.Log.Information(
            "Unlocked aetheryte {AetheryteId} through event {EventId}.",
            aetheryteId,
            eventId);
    }

    private void UnlockAetherCurrent(uint currentId)
    {
        if (!SendPlayerEvent(currentId))
            return;

        refreshUnlockState = true;
        Plugin.Log.Information("Unlocked aether current {CurrentId}.", currentId);
    }

    private static void TouchRivalWingsPoint(int point)
    {
        if (Plugin.ClientState.TerritoryType != RivalWingsTerritoryId)
        {
            Plugin.Chat.PrintError("[Keita 工具箱] 大草原摸点只能在地图 888 使用。");
            return;
        }

        if (point is < 1 or > 13)
        {
            Plugin.Chat.PrintError("[Keita 工具箱] 大草原点位必须为 1-13。");
            return;
        }

        var eventId = RivalWingsFirstEventId + (uint)(point - 1);
        if (SendPlayerEvent(eventId))
            Plugin.Log.Information("Touched Rival Wings point {Point} through event {EventId}.", point, eventId);
    }

    private static void TouchShatterPoint(string point)
    {
        if (Plugin.ClientState.TerritoryType != ShatterTerritoryId)
        {
            Plugin.Chat.PrintError("[Keita 工具箱] 尘封秘岩摸点只能在地图 431 使用。");
            return;
        }

        var eventId = ShatterPoints
            .FirstOrDefault(item => item.Key.Equals(point, StringComparison.OrdinalIgnoreCase))
            .Value;
        if (eventId == 0)
        {
            Plugin.Chat.PrintError($"[Keita 工具箱] 未知的尘封秘岩点位: {point}");
            return;
        }

        if (SendPlayerEvent(eventId))
            Plugin.Log.Information("Touched Shatter point {Point} through event {EventId}.", point, eventId);
    }

    private static void TouchAllBattlefieldPoints()
    {
        switch (Plugin.ClientState.TerritoryType)
        {
            case RivalWingsTerritoryId:
                for (var point = 1; point <= 13; point++)
                    TouchRivalWingsPoint(point);
                Plugin.Chat.Print("[Keita 工具箱] 已摸大草原全部13个点。");
                break;
            case ShatterTerritoryId:
                foreach (var point in ShatterPoints)
                    TouchShatterPoint(point.Key);
                Plugin.Chat.Print("[Keita 工具箱] 已摸尘封秘岩全部15个点。");
                break;
            default:
                Plugin.Chat.PrintError("[Keita 工具箱] 战场虚空摸点仅支持尘封秘岩或大草原。");
                break;
        }
    }

    private static void TouchBattlefieldPoint(string argument)
    {
        var normalized = argument.Trim().ToUpperInvariant();
        if (normalized == "ALL")
        {
            TouchAllBattlefieldPoints();
            return;
        }

        switch (Plugin.ClientState.TerritoryType)
        {
            case RivalWingsTerritoryId when int.TryParse(normalized, out var point):
                TouchRivalWingsPoint(point);
                break;
            case ShatterTerritoryId:
                if (int.TryParse(normalized, out var numericPoint) &&
                    numericPoint is >= 1 and <= 15)
                {
                    var group = (char)('A' + ((numericPoint - 1) / 4));
                    var index = ((numericPoint - 1) % 4) + 1;
                    normalized = $"{group}{index}";
                }
                TouchShatterPoint(normalized);
                break;
            default:
                Plugin.Chat.PrintError("[Keita 工具箱] 战场虚空摸点仅支持尘封秘岩或大草原。");
                break;
        }
    }

    private sealed record AetheryteEntry(
        ushort Id,
        string Name,
        string AethernetName,
        bool IsAethernet,
        bool PrerequisiteComplete)
    {
        public bool IsUnlocked { get; set; }
    }

    private sealed record AetherCurrentEntry(uint Id)
    {
        public bool IsUnlocked { get; set; }
    }
}
