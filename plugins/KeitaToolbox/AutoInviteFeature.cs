using System;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace KeitaToolbox;

internal sealed unsafe class AutoInviteFeature : IDisposable
{
    private const string SchedulePrefix = "AutoInvite:";

    private static readonly (XivChatType Type, string Label)[] AvailableChannels =
    [
        (XivChatType.Say, "说话"),
        (XivChatType.Yell, "呼喊"),
        (XivChatType.Shout, "喊话"),
        (XivChatType.TellIncoming, "悄悄话"),
        (XivChatType.Party, "小队"),
        (XivChatType.Alliance, "团队"),
        (XivChatType.FreeCompany, "部队"),
        (XivChatType.NoviceNetwork, "新人频道"),
        (XivChatType.Ls1, "通讯贝1"),
        (XivChatType.Ls2, "通讯贝2"),
        (XivChatType.Ls3, "通讯贝3"),
        (XivChatType.Ls4, "通讯贝4"),
        (XivChatType.Ls5, "通讯贝5"),
        (XivChatType.Ls6, "通讯贝6"),
        (XivChatType.Ls7, "通讯贝7"),
        (XivChatType.Ls8, "通讯贝8"),
        (XivChatType.CrossLinkShell1, "跨服通讯贝1"),
        (XivChatType.CrossLinkShell2, "跨服通讯贝2"),
        (XivChatType.CrossLinkShell3, "跨服通讯贝3"),
        (XivChatType.CrossLinkShell4, "跨服通讯贝4"),
        (XivChatType.CrossLinkShell5, "跨服通讯贝5"),
        (XivChatType.CrossLinkShell6, "跨服通讯贝6"),
        (XivChatType.CrossLinkShell7, "跨服通讯贝7"),
        (XivChatType.CrossLinkShell8, "跨服通讯贝8"),
    ];

    private readonly Hook<RaptureLogModule.Delegates.AddMsgSourceEntry> addMsgSourceEntryHook;

    public AutoInviteFeature()
    {
        addMsgSourceEntryHook =
            Plugin.Interop.HookFromAddress<RaptureLogModule.Delegates.AddMsgSourceEntry>(
                (nint)RaptureLogModule.MemberFunctionPointers.AddMsgSourceEntry,
                AddMsgSourceEntryDetour);
        addMsgSourceEntryHook.Enable();
    }

    public void Dispose() => addMsgSourceEntryHook.Dispose();

    private void AddMsgSourceEntryDetour(
        RaptureLogModule* module,
        ulong contentId,
        ulong accountId,
        int messageIndex,
        ushort worldId,
        ushort chatType)
    {
        addMsgSourceEntryHook.Original(
            module,
            contentId,
            accountId,
            messageIndex,
            worldId,
            chatType);

        try
        {
            QueueInvite(contentId, messageIndex, chatType);
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Ignored an invalid auto-invite chat entry.");
        }
    }

    private void QueueInvite(ulong contentId, int messageIndex, ushort chatType)
    {
        var settings = Plugin.Config.AutoInvite;
        if (!Plugin.Config.Features.AutoInviteToParty ||
            !settings.RuntimeEnabled ||
            !settings.ListenChannels.Contains((XivChatType)chatType) ||
            contentId == 0 ||
            Plugin.Condition[ConditionFlag.BetweenAreas] ||
            Plugin.Condition[ConditionFlag.BetweenAreas51])
        {
            return;
        }

        var logModule = RaptureLogModule.Instance();
        if (logModule == null ||
            !logModule->GetLogMessageDetail(
                messageIndex,
                out var senderBytes,
                out var messageBytes,
                out _,
                out _,
                out _,
                out _))
        {
            return;
        }

        var message = SeString.Parse(messageBytes).TextValue;
        bool matched;
        if (!settings.RegexMatch)
        {
            matched = message.Contains(
                settings.TextPattern,
                StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            try
            {
                matched = Regex.IsMatch(
                    message,
                    settings.TextPattern,
                    RegexOptions.IgnoreCase,
                    TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException)
            {
                return;
            }
            catch (RegexMatchTimeoutException)
            {
                return;
            }
        }

        if (!matched)
            return;

        if (!CanInviteNow(contentId))
            return;

        var sender = SeString.Parse(senderBytes);
        if (sender.Payloads.FirstOrDefault(payload => payload is PlayerPayload) is not PlayerPayload player)
            return;

        var scheduleGroup = SchedulePrefix + contentId;
        Plugin.Scheduler.Cancel(scheduleGroup);
        Plugin.Scheduler.Schedule(
            scheduleGroup,
            settings.InviteDelayMs,
            () => ExecuteInvite(
                contentId,
                player.PlayerName,
                (ushort)player.World.RowId));
    }

    private static void ExecuteInvite(
        ulong contentId,
        string playerName,
        ushort worldId)
    {
        if (!CanInviteNow(contentId))
            return;

        var proxy = InfoProxyPartyInvite.Instance();
        if (proxy == null)
            return;

        if (Plugin.Config.AutoInvite.PrintMessage)
            Plugin.Chat.Print($"[Keita 工具箱] 正在邀请 {playerName}。");

        if (InInvitableInstance())
            proxy->InviteToPartyInInstanceByContentId(contentId);
        else
            proxy->InviteToParty(contentId, playerName, worldId);
    }

    private static bool CanInviteNow(ulong contentId)
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var groupManager = GroupManager.Instance();
        var group = groupManager == null ? null : groupManager->GetGroup();
        var proxy = InfoProxyPartyInvite.Instance();
        var alreadyInParty = Plugin.PartyList.Any(member => member.ContentId == contentId);
        var hasPendingInvitation = proxy != null &&
                                   !string.IsNullOrWhiteSpace(proxy->InviterName.ToString());

        return AutoInvitePolicy.CanInvite(
            Plugin.Config.Features.AutoInviteToParty,
            Plugin.Config.AutoInvite.RuntimeEnabled,
            Plugin.Condition[ConditionFlag.BetweenAreas] ||
            Plugin.Condition[ConditionFlag.BetweenAreas51],
            localPlayer != null,
            group != null,
            group == null ? -1 : group->MemberCount,
            group != null && localPlayer != null &&
            group->IsEntityIdPartyLeader(localPlayer.EntityId),
            alreadyInParty,
            hasPendingInvitation);
    }

    private static bool InInvitableInstance()
    {
        if (!Plugin.Condition[ConditionFlag.BoundByDuty56])
            return false;

        var sheet = Plugin.Data.GetExcelSheet<TerritoryType>();
        return sheet != null &&
               sheet.TryGetRow(Plugin.ClientState.TerritoryType, out var territory) &&
               territory.TerritoryIntendedUse.RowId is 41 or 47 or 48 or 52 or 53 or 61;
    }

    public void HandleCommand(string argument)
    {
        var normalized = argument.Trim().ToLowerInvariant();
        Plugin.Config.AutoInvite.RuntimeEnabled = normalized switch
        {
            "on" => true,
            "off" => false,
            _ => !Plugin.Config.AutoInvite.RuntimeEnabled,
        };
        Plugin.Config.Save();
        Plugin.Chat.Print(
            $"[Keita 工具箱] 自动邀请已{(Plugin.Config.AutoInvite.RuntimeEnabled ? "开启" : "关闭")}。");
    }

    public void DrawSettings()
    {
        if (!ImGui.CollapsingHeader("自动邀请入队"))
            return;

        Plugin.DrawFeatureToggle(
            "自动邀请入队",
            Plugin.Config.Features.AutoInviteToParty,
            value => Plugin.Config.Features.AutoInviteToParty = value);

        var runtimeEnabled = Plugin.Config.AutoInvite.RuntimeEnabled;
        if (ImGui.Checkbox("临时运行开关", ref runtimeEnabled))
        {
            Plugin.Config.AutoInvite.RuntimeEnabled = runtimeEnabled;
            Plugin.Config.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("/ktb autoinvite on|off|toggle");

        var pattern = Plugin.Config.AutoInvite.TextPattern;
        if (ImGui.InputText("消息匹配表达式", ref pattern, 256))
        {
            Plugin.Config.AutoInvite.TextPattern = pattern;
            Plugin.Config.Save();
        }

        var regex = Plugin.Config.AutoInvite.RegexMatch;
        if (ImGui.Checkbox("使用正则表达式", ref regex))
        {
            Plugin.Config.AutoInvite.RegexMatch = regex;
            Plugin.Config.Save();
        }

        var delay = Plugin.Config.AutoInvite.InviteDelayMs;
        if (ImGui.SliderInt("邀请延迟（毫秒）", ref delay, 0, 5000))
        {
            Plugin.Config.AutoInvite.InviteDelayMs = Math.Clamp(delay, 0, 60000);
            Plugin.Config.Save();
        }

        var print = Plugin.Config.AutoInvite.PrintMessage;
        if (ImGui.Checkbox("邀请时在本地聊天栏提示", ref print))
        {
            Plugin.Config.AutoInvite.PrintMessage = print;
            Plugin.Config.Save();
        }

        ImGui.TextUnformatted("监听频道");
        for (var index = 0; index < AvailableChannels.Length; index++)
        {
            var (type, label) = AvailableChannels[index];
            var enabled = Plugin.Config.AutoInvite.ListenChannels.Contains(type);
            if (ImGui.Checkbox($"{label}##InviteChannel{(int)type}", ref enabled))
            {
                if (enabled)
                    Plugin.Config.AutoInvite.ListenChannels.Add(type);
                else
                    Plugin.Config.AutoInvite.ListenChannels.Remove(type);
                Plugin.Config.Save();
            }

            if (index % 4 != 3 && index != AvailableChannels.Length - 1)
                ImGui.SameLine();
        }

        Plugin.DrawHelp(
            "队伍已满、自己不是队长、对方已在队内或已有待处理邀请时会自动跳过。");
    }
}
