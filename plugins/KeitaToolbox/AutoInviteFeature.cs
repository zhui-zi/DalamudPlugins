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
        (XivChatType.Say, "Say"),
        (XivChatType.Yell, "Yell"),
        (XivChatType.Shout, "Shout"),
        (XivChatType.TellIncoming, "Tell"),
        (XivChatType.Party, "Party"),
        (XivChatType.Alliance, "Alliance"),
        (XivChatType.FreeCompany, "Free Company"),
        (XivChatType.NoviceNetwork, "Novice Network"),
        (XivChatType.Ls1, "LS1"),
        (XivChatType.Ls2, "LS2"),
        (XivChatType.Ls3, "LS3"),
        (XivChatType.Ls4, "LS4"),
        (XivChatType.Ls5, "LS5"),
        (XivChatType.Ls6, "LS6"),
        (XivChatType.Ls7, "LS7"),
        (XivChatType.Ls8, "LS8"),
        (XivChatType.CrossLinkShell1, "CWLS1"),
        (XivChatType.CrossLinkShell2, "CWLS2"),
        (XivChatType.CrossLinkShell3, "CWLS3"),
        (XivChatType.CrossLinkShell4, "CWLS4"),
        (XivChatType.CrossLinkShell5, "CWLS5"),
        (XivChatType.CrossLinkShell6, "CWLS6"),
        (XivChatType.CrossLinkShell7, "CWLS7"),
        (XivChatType.CrossLinkShell8, "CWLS8"),
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

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null)
            return;

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

        var groupManager = GroupManager.Instance();
        var group = groupManager == null ? null : groupManager->GetGroup();
        if (group == null || group->MemberCount >= 8)
            return;
        if (group->MemberCount > 0 && !group->IsEntityIdPartyLeader(localPlayer.EntityId))
            return;

        foreach (var member in Plugin.PartyList)
        {
            if (member.ContentId == contentId)
                return;
        }

        var sender = SeString.Parse(senderBytes);
        if (sender.Payloads.FirstOrDefault(payload => payload is PlayerPayload) is not PlayerPayload player)
            return;

        var scheduleGroup = SchedulePrefix + contentId;
        var inInstance = InInvitableInstance();
        Plugin.Scheduler.Cancel(scheduleGroup);
        Plugin.Scheduler.Schedule(
            scheduleGroup,
            settings.InviteDelayMs,
            () => ExecuteInvite(
                contentId,
                player.PlayerName,
                (ushort)player.World.RowId,
                inInstance));
    }

    private static void ExecuteInvite(
        ulong contentId,
        string playerName,
        ushort worldId,
        bool inInstance)
    {
        if (!Plugin.Config.Features.AutoInviteToParty ||
            !Plugin.Config.AutoInvite.RuntimeEnabled)
        {
            return;
        }

        var groupManager = GroupManager.Instance();
        var group = groupManager == null ? null : groupManager->GetGroup();
        if (group == null || group->MemberCount >= 8)
            return;

        var proxy = InfoProxyPartyInvite.Instance();
        if (proxy == null)
            return;

        if (Plugin.Config.AutoInvite.PrintMessage)
            Plugin.Chat.Print($"[Keita Toolbox] Inviting {playerName}.");

        if (inInstance)
            proxy->InviteToPartyInInstanceByContentId(contentId);
        else
            proxy->InviteToParty(contentId, playerName, worldId);
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
            $"[Keita Toolbox] Auto invite {(Plugin.Config.AutoInvite.RuntimeEnabled ? "enabled" : "disabled")}.");
    }

    public void DrawSettings()
    {
        if (!ImGui.CollapsingHeader("Automatic party invite"))
            return;

        Plugin.DrawFeatureToggle(
            "automatic party invite",
            Plugin.Config.Features.AutoInviteToParty,
            value => Plugin.Config.Features.AutoInviteToParty = value);

        var runtimeEnabled = Plugin.Config.AutoInvite.RuntimeEnabled;
        if (ImGui.Checkbox("Runtime invite switch", ref runtimeEnabled))
        {
            Plugin.Config.AutoInvite.RuntimeEnabled = runtimeEnabled;
            Plugin.Config.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("/ktb autoinvite on|off|toggle");

        var pattern = Plugin.Config.AutoInvite.TextPattern;
        if (ImGui.InputText("Message pattern", ref pattern, 256))
        {
            Plugin.Config.AutoInvite.TextPattern = pattern;
            Plugin.Config.Save();
        }

        var regex = Plugin.Config.AutoInvite.RegexMatch;
        if (ImGui.Checkbox("Use regular expression", ref regex))
        {
            Plugin.Config.AutoInvite.RegexMatch = regex;
            Plugin.Config.Save();
        }

        var delay = Plugin.Config.AutoInvite.InviteDelayMs;
        if (ImGui.SliderInt("Invite delay (ms)", ref delay, 0, 5000))
        {
            Plugin.Config.AutoInvite.InviteDelayMs = Math.Clamp(delay, 0, 60000);
            Plugin.Config.Save();
        }

        var print = Plugin.Config.AutoInvite.PrintMessage;
        if (ImGui.Checkbox("Print a local message when inviting", ref print))
        {
            Plugin.Config.AutoInvite.PrintMessage = print;
            Plugin.Config.Save();
        }

        ImGui.TextUnformatted("Listen channels");
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
            "Full parties, non-leaders, existing party members, and duplicate pending invites are skipped.");
    }
}
