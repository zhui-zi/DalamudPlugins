using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using OmenTools.OmenService;

namespace KeitaToolbox;

internal sealed unsafe class BasicFeatures : IDisposable
{
    private const string RecruitmentMarker = "招募信息为：";
    private const long ImeCleanupIntervalMs = 100;
    private const uint NiCompositionStr = 0x0015;
    private const uint CpsCancel = 0x0004;

    private static readonly Dictionary<string, bool> dutySelectedOnly = [];
    private readonly Dictionary<MapRule, MapRuleEditorState> mapRuleEditors = [];
    private string pendingRecruitment = string.Empty;
    private string commenceSearch = string.Empty;
    private string partyFinderInput = string.Empty;
    private string pvpDisablePluginSearch = string.Empty;
    private string pvpEnablePluginSearch = string.Empty;
    private uint lastJobId;
    private long nextImeCleanupAt;
    private nint gameWindow;
    private bool? lastInPvp;
    private MapRule? appliedMapRule;

    public BasicFeatures()
    {
        gameWindow = ResolveGameWindow();
        Plugin.Chat.ChatMessage += OnChatMessage;
        Plugin.DutyState.DutyCompleted += OnDutyCompleted;
        Plugin.Framework.Update += OnUpdate;
        Plugin.AddonLifecycle.RegisterListener(
            AddonEvent.PostSetup,
            "ContentsFinderConfirm",
            OnContentsFinderConfirm);
        Plugin.AddonLifecycle.RegisterListener(
            AddonEvent.PreDraw,
            "ContentsFinderConfirm",
            OnContentsFinderConfirm);
        Plugin.PartyFinder.ReceiveListing += OnReceiveListing;
    }

    public void Dispose()
    {
        RestorePluginSwitching();
        Plugin.PartyFinder.ReceiveListing -= OnReceiveListing;
        Plugin.AddonLifecycle.UnregisterListener(OnContentsFinderConfirm);
        Plugin.Framework.Update -= OnUpdate;
        Plugin.DutyState.DutyCompleted -= OnDutyCompleted;
        Plugin.Chat.ChatMessage -= OnChatMessage;
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (!Plugin.Config.Features.AnnounceRecruitmentOnClear)
            return;

        var text = message.Message.TextValue;
        var markerIndex = text.IndexOf(RecruitmentMarker, StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            var value = text[(markerIndex + RecruitmentMarker.Length)..].Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                pendingRecruitment = value;
                Plugin.Log.Information("Captured a party finder recruitment message for duty completion.");
            }
            return;
        }

        if (text.Contains("小队", StringComparison.Ordinal) &&
            text.Contains("解散", StringComparison.Ordinal))
        {
            pendingRecruitment = string.Empty;
        }
    }

    private void OnDutyCompleted(IDutyStateEventArgs _)
    {
        if (!Plugin.Config.Features.AnnounceRecruitmentOnClear ||
            string.IsNullOrWhiteSpace(pendingRecruitment))
        {
            return;
        }

        foreach (var line in pendingRecruitment.Split(
                     '\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ChatManager.Instance().SendMessage($"/e {line}");
        }

        Plugin.Log.Information("Sent the captured party finder recruitment message after duty completion.");
        pendingRecruitment = string.Empty;
    }

    private void OnUpdate(IFramework _)
    {
        UpdateBmraiDistance();
        UpdateImeCleanup();
        UpdatePluginSwitcher();
    }

    private void UpdateBmraiDistance()
    {
        if (!Plugin.Config.Features.AutoBmraiMaxDistance ||
            Plugin.Condition[ConditionFlag.BetweenAreas] ||
            Plugin.Condition[ConditionFlag.BetweenAreas51])
        {
            return;
        }

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null)
            return;

        var classJob = localPlayer.ClassJob.Value;
        if (classJob.RowId == 0 || classJob.RowId == lastJobId)
            return;

        lastJobId = classJob.RowId;
        var distance = classJob.Role is 1 or 2
            ? Plugin.Config.Bmrai.MeleeDistance
            : classJob.Role is 3 or 4
                ? Plugin.Config.Bmrai.RangedDistance
                : (float?)null;
        if (distance == null)
            return;

        try
        {
            var command = string.Format(
                CultureInfo.InvariantCulture,
                Plugin.Config.Bmrai.CommandFormat,
                distance.Value);
            Plugin.CommandManager.ProcessCommand(command);
        }
        catch (FormatException ex)
        {
            Plugin.Log.Warning(ex, "Invalid bmrai command format.");
        }
    }

    private void UpdateImeCleanup()
    {
        if (!Plugin.Config.Features.ImeGarbageFix)
            return;

        var now = Environment.TickCount64;
        if (now < nextImeCleanupAt)
            return;
        nextImeCleanupAt = now + ImeCleanupIntervalMs;

        var module = RaptureAtkModule.Instance();
        if (module == null || module->IsTextInputActive() || ImGui.GetIO().WantTextInput)
            return;

        if (gameWindow == nint.Zero)
            gameWindow = ResolveGameWindow();
        if (gameWindow == nint.Zero)
            return;

        var inputContext = ImmGetContext(gameWindow);
        if (inputContext == nint.Zero)
            return;

        try
        {
            ImmNotifyIME(inputContext, NiCompositionStr, CpsCancel, 0);
        }
        finally
        {
            ImmReleaseContext(gameWindow, inputContext);
        }
    }

    private void OnContentsFinderConfirm(AddonEvent _, AddonArgs args)
    {
        if (!Plugin.Config.Features.AutoCommenceDuty ||
            Plugin.Config.Duty.CommenceWhitelist.Count == 0 ||
            args.Addon.IsNull)
        {
            return;
        }

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon->AtkValues == null || addon->AtkValues[7].UInt != 0)
            return;

        var dutyName = addon->AtkValues[1].String.ToString();
        if (string.IsNullOrWhiteSpace(dutyName) || !IsDutyWhitelisted(dutyName))
            return;

        var confirm = (AddonContentsFinderConfirm*)addon;
        if (confirm->CommenceButton != null && confirm->CommenceButton->IsEnabled)
            ClickButton(addon, confirm->CommenceButton);
    }

    private static bool IsDutyWhitelisted(string dutyName)
    {
        var sheet = Plugin.Data.GetExcelSheet<ContentFinderCondition>();
        if (sheet == null)
            return false;

        foreach (var id in Plugin.Config.Duty.CommenceWhitelist)
        {
            if (!sheet.TryGetRow(id, out var row))
                continue;

            if (string.Equals(
                    row.Name.ToString().Trim(),
                    dutyName.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void OnReceiveListing(IPartyFinderListing listing, IPartyFinderListingEventArgs args)
    {
        if (!Plugin.Config.Features.PartyFinderDutyFilter ||
            Plugin.Config.PartyFinder.BlockedKeywords.Count == 0 ||
            listing.RawDuty == 0)
        {
            return;
        }

        var sheet = Plugin.Data.GetExcelSheet<ContentFinderCondition>();
        if (sheet == null || !sheet.TryGetRow(listing.RawDuty, out var row))
            return;

        var name = row.Name.ToString();
        if (Plugin.Config.PartyFinder.BlockedKeywords.Any(
                keyword => !string.IsNullOrWhiteSpace(keyword) &&
                           name.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            args.Visible = false;
        }
    }

    private void UpdatePluginSwitcher()
    {
        if (!Plugin.Config.Features.PvpPluginSwitcher)
        {
            RestorePluginSwitching();
            return;
        }

        var inPvp = Plugin.ClientState.IsPvP;
        if (lastInPvp != inPvp)
        {
            var previous = lastInPvp;
            lastInPvp = inPvp;
            if (inPvp)
                ApplyPluginRules(
                    Plugin.Config.PluginSwitcher.DisableInPvp,
                    Plugin.Config.PluginSwitcher.EnableInPvp,
                    true);
            else if (previous == true)
                ApplyPluginRules(
                    Plugin.Config.PluginSwitcher.DisableInPvp,
                    Plugin.Config.PluginSwitcher.EnableInPvp,
                    false);
        }

        var territory = Plugin.ClientState.TerritoryType;
        var rule = Plugin.Config.PluginSwitcher.MapRules.FirstOrDefault(
            item => ParseList(item.Territories)
                .Any(value => uint.TryParse(value, out var id) && id == territory));
        if (ReferenceEquals(rule, appliedMapRule))
            return;

        if (appliedMapRule != null)
            ApplyPluginRules(appliedMapRule.Disable, appliedMapRule.Enable, false);
        if (rule != null)
            ApplyPluginRules(rule.Disable, rule.Enable, true);
        appliedMapRule = rule;
    }

    private void RestorePluginSwitching()
    {
        if (appliedMapRule != null)
        {
            ApplyPluginRules(appliedMapRule.Disable, appliedMapRule.Enable, false);
            appliedMapRule = null;
        }

        if (lastInPvp == true)
        {
            ApplyPluginRules(
                Plugin.Config.PluginSwitcher.DisableInPvp,
                Plugin.Config.PluginSwitcher.EnableInPvp,
                false);
        }

        lastInPvp = null;
    }

    private static void ApplyPluginRules(string disableList, string enableList, bool entering)
    {
        foreach (var name in ParseList(disableList))
        {
            if (entering)
                DisablePlugin(name);
            else
                EnablePlugin(name);
        }

        foreach (var name in ParseList(enableList))
        {
            if (entering)
                EnablePlugin(name);
            else
                DisablePlugin(name);
        }
    }

    private static void EnablePlugin(string internalName)
    {
        var plugin = Plugin.PluginInterface.InstalledPlugins.FirstOrDefault(
            item => item.InternalName.Equals(internalName, StringComparison.OrdinalIgnoreCase));
        if (plugin == null || plugin.IsLoaded)
            return;

        Plugin.CommandManager.ProcessCommand($"/xlenableplugin {plugin.InternalName}");
    }

    private static void DisablePlugin(string internalName)
    {
        if (internalName.Equals(
                Plugin.PluginInterface.InternalName,
                StringComparison.OrdinalIgnoreCase))
        {
            Plugin.Log.Warning("Ignored a rule that attempted to disable Keita Toolbox itself.");
            return;
        }

        var plugin = Plugin.PluginInterface.InstalledPlugins.FirstOrDefault(
            item => item.InternalName.Equals(internalName, StringComparison.OrdinalIgnoreCase));
        if (plugin == null || !plugin.IsLoaded)
            return;

        Plugin.CommandManager.ProcessCommand($"/xldisableplugin {plugin.InternalName}");
    }

    public void DrawAnnouncementSettings()
    {
        if (!ImGui.CollapsingHeader("副本结束后发送招募信息"))
            return;

        Plugin.DrawFeatureToggle(
            "招募信息回显",
            Plugin.Config.Features.AnnounceRecruitmentOnClear,
            value => Plugin.Config.Features.AnnounceRecruitmentOnClear = value);
        Plugin.DrawHelp(
            "加入小队时记录招募信息，副本完成后通过 /echo 输出。");
    }

    public void DrawBmraiSettings()
    {
        if (!ImGui.CollapsingHeader("BossMod Reborn 距离"))
            return;

        Plugin.DrawFeatureToggle(
            "自动设置 bmrai 距离",
            Plugin.Config.Features.AutoBmraiMaxDistance,
            value =>
            {
                Plugin.Config.Features.AutoBmraiMaxDistance = value;
                lastJobId = 0;
            });

        var melee = Plugin.Config.Bmrai.MeleeDistance;
        if (ImGui.InputFloat("防护 / 近战距离", ref melee, 0.1f, 1f, "%.1f"))
        {
            Plugin.Config.Bmrai.MeleeDistance = Math.Max(0, melee);
            Plugin.Config.Save();
            lastJobId = 0;
        }

        var ranged = Plugin.Config.Bmrai.RangedDistance;
        if (ImGui.InputFloat("治疗 / 远程距离", ref ranged, 0.1f, 1f, "%.1f"))
        {
            Plugin.Config.Bmrai.RangedDistance = Math.Max(0, ranged);
            Plugin.Config.Save();
            lastJobId = 0;
        }

        var format = Plugin.Config.Bmrai.CommandFormat;
        if (ImGui.InputText("命令格式", ref format, 256))
        {
            Plugin.Config.Bmrai.CommandFormat = format;
            Plugin.Config.Save();
            lastJobId = 0;
        }
        Plugin.DrawHelp("使用 {0} 作为距离占位符。");
    }

    public void DrawImeSettings()
    {
        if (!ImGui.CollapsingHeader("中文输入法残留清理"))
            return;

        Plugin.DrawFeatureToggle(
            "输入法残留清理",
            Plugin.Config.Features.ImeGarbageFix,
            value => Plugin.Config.Features.ImeGarbageFix = value);
        Plugin.DrawHelp(
            "仅在游戏和插件界面均未接收文字输入时，清除 Windows 输入法残留的组字内容。");
    }

    public void DrawCommenceSettings()
    {
        if (!ImGui.CollapsingHeader("自动开始任务"))
            return;

        Plugin.DrawFeatureToggle(
            "自动开始任务",
            Plugin.Config.Features.AutoCommenceDuty,
            value => Plugin.Config.Features.AutoCommenceDuty = value);
        DrawDutySelector(
            "CommenceDutySelector",
            "仅自动确认已勾选的任务。",
            ref commenceSearch,
            Plugin.Config.Duty.CommenceWhitelist);
    }

    public void DrawPartyFinderSettings()
    {
        if (!ImGui.CollapsingHeader("招募板任务过滤"))
            return;

        Plugin.DrawFeatureToggle(
            "招募板任务过滤",
            Plugin.Config.Features.PartyFinderDutyFilter,
            value => Plugin.Config.Features.PartyFinderDutyFilter = value);

        ImGui.SetNextItemWidth(260);
        ImGui.InputText("关键词", ref partyFinderInput, 128);
        ImGui.SameLine();
        if (ImGui.Button("添加") && !string.IsNullOrWhiteSpace(partyFinderInput))
        {
            var keyword = partyFinderInput.Trim();
            if (!Plugin.Config.PartyFinder.BlockedKeywords.Contains(
                    keyword,
                    StringComparer.OrdinalIgnoreCase))
            {
                Plugin.Config.PartyFinder.BlockedKeywords.Add(keyword);
                Plugin.Config.Save();
            }
            partyFinderInput = string.Empty;
        }

        for (var index = 0; index < Plugin.Config.PartyFinder.BlockedKeywords.Count; index++)
        {
            ImGui.PushID(index);
            if (ImGui.SmallButton("移除"))
            {
                Plugin.Config.PartyFinder.BlockedKeywords.RemoveAt(index);
                Plugin.Config.Save();
                ImGui.PopID();
                break;
            }
            ImGui.SameLine();
            ImGui.TextUnformatted(Plugin.Config.PartyFinder.BlockedKeywords[index]);
            ImGui.PopID();
        }
        Plugin.DrawHelp("任务名称包含任一已设置关键词时隐藏该招募。");
    }

    public void DrawPluginSwitcherSettings()
    {
        if (!ImGui.CollapsingHeader("PvP 与地图插件切换"))
            return;

        Plugin.DrawFeatureToggle(
            "插件自动切换",
            Plugin.Config.Features.PvpPluginSwitcher,
            value => Plugin.Config.Features.PvpPluginSwitcher = value);

        DrawPluginSelector(
            "PvpDisablePlugins",
            "进入 PvP 时禁用",
            Plugin.Config.PluginSwitcher.DisableInPvp,
            value => Plugin.Config.PluginSwitcher.DisableInPvp = value,
            ref pvpDisablePluginSearch);
        DrawPluginSelector(
            "PvpEnablePlugins",
            "进入 PvP 时启用",
            Plugin.Config.PluginSwitcher.EnableInPvp,
            value => Plugin.Config.PluginSwitcher.EnableInPvp = value,
            ref pvpEnablePluginSearch);

        ImGui.Separator();
        for (var index = 0; index < Plugin.Config.PluginSwitcher.MapRules.Count; index++)
        {
            var rule = Plugin.Config.PluginSwitcher.MapRules[index];
            if (!mapRuleEditors.TryGetValue(rule, out var editor))
            {
                editor = new MapRuleEditorState();
                mapRuleEditors[rule] = editor;
            }

            ImGui.PushID(index);
            ImGui.TextUnformatted($"地图规则 {index + 1}");
            ImGui.SameLine();
            if (ImGui.SmallButton("移除规则"))
            {
                mapRuleEditors.Remove(rule);
                Plugin.Config.PluginSwitcher.MapRules.RemoveAt(index);
                Plugin.Config.Save();
                ImGui.PopID();
                break;
            }

            if (ImGui.SmallButton($"添加当前地图（{Plugin.ClientState.TerritoryType}）"))
            {
                var ids = ParseList(rule.Territories);
                var current = Plugin.ClientState.TerritoryType.ToString();
                if (!ids.Contains(current))
                {
                    ids.Add(current);
                    rule.Territories = string.Join(", ", ids);
                    Plugin.Config.Save();
                }
            }

            DrawTerritorySelector(
                "Territories",
                "生效地图",
                rule.Territories,
                value => rule.Territories = value,
                ref editor.TerritorySearch);
            DrawPluginSelector(
                "DisablePlugins",
                "进入时禁用",
                rule.Disable,
                value => rule.Disable = value,
                ref editor.DisablePluginSearch);
            DrawPluginSelector(
                "EnablePlugins",
                "进入时启用",
                rule.Enable,
                value => rule.Enable = value,
                ref editor.EnablePluginSearch);
            ImGui.Separator();
            ImGui.PopID();
        }

        if (ImGui.Button("添加地图规则"))
        {
            Plugin.Config.PluginSwitcher.MapRules.Add(new MapRule());
            Plugin.Config.Save();
        }

        Plugin.DrawHelp(
            $"直接从列表勾选插件和地图；列表中没有的项目可在“手动编辑”中填写。当前地图：{Plugin.ClientState.TerritoryType}；PvP：{Plugin.ClientState.IsPvP}。");
    }

    private static void DrawPluginSelector(
        string id,
        string label,
        string value,
        Action<string> setter,
        ref string search)
    {
        var selected = ParseList(value);
        if (!ImGui.BeginCombo($"{label}##{id}", $"已选择 {selected.Count} 项"))
            return;

        ImGui.SetNextItemWidth(320);
        ImGui.InputText($"搜索插件##{id}", ref search, 128);

        if (ImGui.BeginChild(
                $"PluginSelector##{id}",
                new System.Numerics.Vector2(0, 160),
                true))
        {
            var normalizedSearch = search.Trim();
            foreach (var plugin in Plugin.PluginInterface.InstalledPlugins
                         .Where(item => !item.InternalName.Equals(
                             Plugin.PluginInterface.InternalName,
                             StringComparison.OrdinalIgnoreCase))
                         .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(normalizedSearch) &&
                    !plugin.Name.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase) &&
                    !plugin.InternalName.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var enabled = selected.Contains(
                    plugin.InternalName,
                    StringComparer.OrdinalIgnoreCase);
                if (!ImGui.Checkbox(
                        $"{plugin.Name} ({plugin.InternalName})##{id}-{plugin.InternalName}",
                        ref enabled))
                {
                    continue;
                }

                if (enabled)
                    selected.Add(plugin.InternalName);
                else
                    selected.RemoveAll(item => item.Equals(
                        plugin.InternalName,
                        StringComparison.OrdinalIgnoreCase));
                setter(string.Join(", ", selected));
                Plugin.Config.Save();
            }
        }
        ImGui.EndChild();

        if (ImGui.TreeNode($"手动编辑内部名称##{id}"))
        {
            ImGui.SetNextItemWidth(420);
            var buffer = value;
            if (ImGui.InputText($"英文逗号分隔##{id}", ref buffer, 2048))
            {
                setter(buffer);
                Plugin.Config.Save();
            }
            ImGui.TreePop();
        }
        ImGui.EndCombo();
    }

    private static void DrawTerritorySelector(
        string id,
        string label,
        string value,
        Action<string> setter,
        ref string search)
    {
        var selected = ParseList(value)
            .Where(item => uint.TryParse(item, out _))
            .Select(uint.Parse)
            .ToHashSet();
        if (!ImGui.BeginCombo($"{label}##{id}", $"已选择 {selected.Count} 项"))
            return;

        ImGui.SetNextItemWidth(320);
        ImGui.InputText($"搜索地图##{id}", ref search, 128);

        var sheet = Plugin.Data.GetExcelSheet<TerritoryType>();
        if (sheet != null)
        {
            if (ImGui.BeginChild(
                    $"TerritorySelector##{id}",
                    new System.Numerics.Vector2(480, 260),
                    true))
            {
                var normalizedSearch = search.Trim();
                foreach (var row in sheet)
                {
                    var name = GetTerritoryName(row);
                    if (string.IsNullOrWhiteSpace(name) ||
                        (!string.IsNullOrWhiteSpace(normalizedSearch) &&
                         !name.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase) &&
                         !row.RowId.ToString().Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var enabled = selected.Contains(row.RowId);
                    if (!ImGui.Checkbox($"{name} ({row.RowId})##{id}-{row.RowId}", ref enabled))
                        continue;

                    if (enabled)
                        selected.Add(row.RowId);
                    else
                        selected.Remove(row.RowId);
                    setter(string.Join(", ", selected.Order()));
                    Plugin.Config.Save();
                }
            }
            ImGui.EndChild();
        }

        if (ImGui.TreeNode($"手动编辑地图 ID##{id}"))
        {
            ImGui.SetNextItemWidth(420);
            var buffer = value;
            if (ImGui.InputText($"英文逗号分隔##{id}", ref buffer, 2048))
            {
                setter(buffer);
                Plugin.Config.Save();
            }
            ImGui.TreePop();
        }
        ImGui.EndCombo();
    }

    internal static void DrawDutySelector(
        string id,
        string help,
        ref string search,
        HashSet<uint> selected)
    {
        ImGui.SetNextItemWidth(320);
        ImGui.InputText($"搜索##{id}", ref search, 128);
        ImGui.SameLine();
        ImGui.TextDisabled($"已选择 {selected.Count} 项");
        ImGui.SameLine();
        var selectedOnly = dutySelectedOnly.GetValueOrDefault(id);
        if (ImGui.Checkbox($"仅显示已选##{id}", ref selectedOnly))
            dutySelectedOnly[id] = selectedOnly;

        var sheet = Plugin.Data.GetExcelSheet<ContentFinderCondition>();
        if (sheet == null)
            return;

        if (ImGui.BeginChild(
                id,
                new System.Numerics.Vector2(0, 280),
                true))
        {
            var normalizedSearch = search.Trim();
            foreach (var row in sheet)
            {
                var name = row.Name.ToString();
                var category = row.ContentType.RowId == 0
                    ? string.Empty
                    : row.ContentType.Value.Name.ToString();
                if (string.IsNullOrWhiteSpace(name) ||
                    (selectedOnly && !selected.Contains(row.RowId)) ||
                    (!string.IsNullOrWhiteSpace(normalizedSearch) &&
                     !name.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase) &&
                     !category.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase) &&
                     !row.RowId.ToString().Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var enabled = selected.Contains(row.RowId);
                var prefix = string.IsNullOrWhiteSpace(category) ? string.Empty : $"[{category}] ";
                if (!ImGui.Checkbox($"{prefix}{name} ({row.RowId})##{id}-{row.RowId}", ref enabled))
                    continue;

                if (enabled)
                    selected.Add(row.RowId);
                else
                    selected.Remove(row.RowId);
                Plugin.Config.Save();
            }
        }
        ImGui.EndChild();
        Plugin.DrawHelp(help);
    }

    internal static string GetTerritoryLabel(uint territoryId)
    {
        var sheet = Plugin.Data.GetExcelSheet<TerritoryType>();
        if (sheet == null || !sheet.TryGetRow(territoryId, out var row))
            return territoryId == 0 ? "请选择地图" : $"未知地图 ({territoryId})";

        var name = GetTerritoryName(row);
        return string.IsNullOrWhiteSpace(name)
            ? $"未知地图 ({territoryId})"
            : $"{name} ({territoryId})";
    }

    internal static string GetTerritoryName(TerritoryType row)
    {
        if (row.PlaceName.RowId != 0)
        {
            var name = row.PlaceName.Value.Name.ToString();
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        if (row.ContentFinderCondition.RowId != 0)
            return row.ContentFinderCondition.Value.Name.ToString();
        return string.Empty;
    }

    private sealed class MapRuleEditorState
    {
        public string TerritorySearch = string.Empty;
        public string DisablePluginSearch = string.Empty;
        public string EnablePluginSearch = string.Empty;
    }

    private static List<string> ParseList(string value) =>
        value.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void ClickButton(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (addon == null || button == null || button->OwnerNode == null)
            return;

        var ownerNode = button->OwnerNode;
        var atkEvent = ownerNode->AtkResNode.AtkEventManager.Event;
        if (atkEvent == null)
            return;

        addon->ReceiveEvent(
            atkEvent->State.EventType,
            (int)atkEvent->Param,
            atkEvent);
    }

    private static nint ResolveGameWindow()
    {
        var handle = Process.GetCurrentProcess().MainWindowHandle;
        return handle != nint.Zero ? handle : FindWindow("FFXIVGAME", null);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("imm32.dll")]
    private static extern nint ImmGetContext(nint hWnd);

    [DllImport("imm32.dll")]
    private static extern bool ImmReleaseContext(nint hWnd, nint hImc);

    [DllImport("imm32.dll")]
    private static extern bool ImmNotifyIME(nint hImc, uint action, uint index, uint value);
}
