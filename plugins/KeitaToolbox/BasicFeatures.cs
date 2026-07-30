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

namespace KeitaToolbox;

internal sealed unsafe class BasicFeatures : IDisposable
{
    private const string RecruitmentMarker = "招募信息为：";
    private const long ImeCleanupIntervalMs = 100;
    private const uint NiCompositionStr = 0x0015;
    private const uint CpsCancel = 0x0004;

    private readonly Dictionary<MapRule, string> territoryInputs = [];
    private string pendingRecruitment = string.Empty;
    private string commenceSearch = string.Empty;
    private string partyFinderInput = string.Empty;
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
                pendingRecruitment = value;
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

        if (Plugin.PartyList.Length < 2)
        {
            pendingRecruitment = string.Empty;
            return;
        }

        foreach (var line in pendingRecruitment.Split(
                     '\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Plugin.CommandManager.ProcessCommand($"/e {line}");
        }

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
        if (!ImGui.CollapsingHeader("Recruitment text after duty clear"))
            return;

        Plugin.DrawFeatureToggle(
            "recruitment text announcement",
            Plugin.Config.Features.AnnounceRecruitmentOnClear,
            value => Plugin.Config.Features.AnnounceRecruitmentOnClear = value);
        Plugin.DrawHelp(
            "Captures the Party Finder recruitment text when joining a party and prints it to /echo after duty completion.");
    }

    public void DrawBmraiSettings()
    {
        if (!ImGui.CollapsingHeader("BossMod Reborn distance"))
            return;

        Plugin.DrawFeatureToggle(
            "automatic bmrai distance",
            Plugin.Config.Features.AutoBmraiMaxDistance,
            value =>
            {
                Plugin.Config.Features.AutoBmraiMaxDistance = value;
                lastJobId = 0;
            });

        var melee = Plugin.Config.Bmrai.MeleeDistance;
        if (ImGui.InputFloat("Tank / melee distance", ref melee, 0.1f, 1f, "%.1f"))
        {
            Plugin.Config.Bmrai.MeleeDistance = Math.Max(0, melee);
            Plugin.Config.Save();
            lastJobId = 0;
        }

        var ranged = Plugin.Config.Bmrai.RangedDistance;
        if (ImGui.InputFloat("Healer / ranged distance", ref ranged, 0.1f, 1f, "%.1f"))
        {
            Plugin.Config.Bmrai.RangedDistance = Math.Max(0, ranged);
            Plugin.Config.Save();
            lastJobId = 0;
        }

        var format = Plugin.Config.Bmrai.CommandFormat;
        if (ImGui.InputText("Command format", ref format, 256))
        {
            Plugin.Config.Bmrai.CommandFormat = format;
            Plugin.Config.Save();
            lastJobId = 0;
        }
        Plugin.DrawHelp("Use {0} as the distance placeholder.");
    }

    public void DrawImeSettings()
    {
        if (!ImGui.CollapsingHeader("Chinese IME cleanup"))
            return;

        Plugin.DrawFeatureToggle(
            "IME garbage cleanup",
            Plugin.Config.Features.ImeGarbageFix,
            value => Plugin.Config.Features.ImeGarbageFix = value);
        Plugin.DrawHelp(
            "Clears pending Windows IME composition only when neither the game nor ImGui is accepting text input.");
    }

    public void DrawCommenceSettings()
    {
        if (!ImGui.CollapsingHeader("Automatic duty commence"))
            return;

        Plugin.DrawFeatureToggle(
            "automatic duty commence",
            Plugin.Config.Features.AutoCommenceDuty,
            value => Plugin.Config.Features.AutoCommenceDuty = value);
        DrawDutySelector(
            "CommenceDutySelector",
            "Only checked duties are commenced automatically.",
            ref commenceSearch,
            Plugin.Config.Duty.CommenceWhitelist);
    }

    public void DrawPartyFinderSettings()
    {
        if (!ImGui.CollapsingHeader("Party Finder duty filter"))
            return;

        Plugin.DrawFeatureToggle(
            "Party Finder duty filter",
            Plugin.Config.Features.PartyFinderDutyFilter,
            value => Plugin.Config.Features.PartyFinderDutyFilter = value);

        ImGui.SetNextItemWidth(260);
        ImGui.InputText("Keyword", ref partyFinderInput, 128);
        ImGui.SameLine();
        if (ImGui.Button("Add") && !string.IsNullOrWhiteSpace(partyFinderInput))
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
            if (ImGui.SmallButton("Remove"))
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
        Plugin.DrawHelp("A listing is hidden when its duty name contains any configured keyword.");
    }

    public void DrawPluginSwitcherSettings()
    {
        if (!ImGui.CollapsingHeader("PvP and territory plugin switcher"))
            return;

        Plugin.DrawFeatureToggle(
            "plugin switcher",
            Plugin.Config.Features.PvpPluginSwitcher,
            value => Plugin.Config.Features.PvpPluginSwitcher = value);

        DrawPluginListInput(
            "Disable in PvP",
            Plugin.Config.PluginSwitcher.DisableInPvp,
            value => Plugin.Config.PluginSwitcher.DisableInPvp = value);
        DrawPluginListInput(
            "Enable in PvP",
            Plugin.Config.PluginSwitcher.EnableInPvp,
            value => Plugin.Config.PluginSwitcher.EnableInPvp = value);

        ImGui.Separator();
        for (var index = 0; index < Plugin.Config.PluginSwitcher.MapRules.Count; index++)
        {
            var rule = Plugin.Config.PluginSwitcher.MapRules[index];
            ImGui.PushID(index);
            ImGui.TextUnformatted($"Territory rule {index + 1}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove rule"))
            {
                territoryInputs.Remove(rule);
                Plugin.Config.PluginSwitcher.MapRules.RemoveAt(index);
                Plugin.Config.Save();
                ImGui.PopID();
                break;
            }

            var territories = rule.Territories;
            if (ImGui.InputText("Territory IDs", ref territories, 512))
            {
                rule.Territories = territories;
                Plugin.Config.Save();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton($"Add current ({Plugin.ClientState.TerritoryType})"))
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

            DrawPluginListInput(
                "Disable on entry",
                rule.Disable,
                value => rule.Disable = value);
            DrawPluginListInput(
                "Enable on entry",
                rule.Enable,
                value => rule.Enable = value);
            ImGui.Separator();
            ImGui.PopID();
        }

        if (ImGui.Button("Add territory rule"))
        {
            Plugin.Config.PluginSwitcher.MapRules.Add(new MapRule());
            Plugin.Config.Save();
        }

        Plugin.DrawHelp(
            $"Use comma-separated plugin InternalNames. Current territory: {Plugin.ClientState.TerritoryType}; PvP: {Plugin.ClientState.IsPvP}.");
    }

    private static void DrawPluginListInput(string label, string value, Action<string> setter)
    {
        ImGui.SetNextItemWidth(420);
        var buffer = value;
        if (!ImGui.InputText(label, ref buffer, 2048))
            return;

        setter(buffer);
        Plugin.Config.Save();
    }

    internal static void DrawDutySelector(
        string id,
        string help,
        ref string search,
        HashSet<uint> selected)
    {
        ImGui.SetNextItemWidth(320);
        ImGui.InputText($"Search##{id}", ref search, 128);
        ImGui.SameLine();
        ImGui.TextDisabled($"{selected.Count} selected");

        var sheet = Plugin.Data.GetExcelSheet<ContentFinderCondition>();
        if (sheet == null)
            return;

        if (ImGui.BeginChild(
                id,
                new System.Numerics.Vector2(0, 220),
                true))
        {
            var normalizedSearch = search.Trim();
            foreach (var row in sheet)
            {
                var name = row.Name.ToString();
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
                Plugin.Config.Save();
            }
        }
        ImGui.EndChild();
        Plugin.DrawHelp(help);
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
