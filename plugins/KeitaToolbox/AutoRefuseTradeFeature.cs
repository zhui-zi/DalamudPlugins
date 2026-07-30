using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using Dalamud.Hooking;
using Dalamud.Interface.ImGuiNotification;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace KeitaToolbox;

internal sealed unsafe class AutoRefuseTradeFeature : IDisposable
{
    private const string ScheduleGroup = "AutoRefuseTrade";
    private const string TradeStatusUpdateSignature =
        "E9 ?? ?? ?? ?? CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC 4C 8B C2 8B D1 48 8D 0D ?? ?? ?? ?? E9 ?? ?? ?? ?? CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC 48 8D 0D";

    private delegate nint TradeStatusUpdateDelegate(
        InventoryManager* manager,
        nint entityId,
        nint data);

    private readonly Hook<TradeStatusUpdateDelegate> tradeStatusUpdateHook;
    private readonly Hook<InventoryManager.Delegates.SendTradeRequest> sendTradeRequestHook;
    private uint tradePartnerEntityId;
    private long suppressIncomingUntil;

    public AutoRefuseTradeFeature()
    {
        tradeStatusUpdateHook =
            Plugin.Interop.HookFromSignature<TradeStatusUpdateDelegate>(
                TradeStatusUpdateSignature,
                TradeStatusUpdateDetour);
        sendTradeRequestHook =
            Plugin.Interop.HookFromAddress<InventoryManager.Delegates.SendTradeRequest>(
                (nint)InventoryManager.MemberFunctionPointers.SendTradeRequest,
                SendTradeRequestDetour);

        Plugin.AgentLifecycle.RegisterListener(
            AgentEvent.PreShow,
            AgentId.Trade,
            OnTradePreShow);
        tradeStatusUpdateHook.Enable();
        sendTradeRequestHook.Enable();
    }

    public void Dispose()
    {
        Plugin.Scheduler.Cancel(ScheduleGroup);
        Plugin.AgentLifecycle.UnregisterListener(
            AgentEvent.PreShow,
            AgentId.Trade,
            OnTradePreShow);
        sendTradeRequestHook.Dispose();
        tradeStatusUpdateHook.Dispose();
    }

    private nint TradeStatusUpdateDetour(
        InventoryManager* manager,
        nint entityId,
        nint data)
    {
        if (data != nint.Zero)
        {
            var eventType = Marshal.ReadByte(data + 4);
            if (eventType == 1)
                tradePartnerEntityId = (uint)Marshal.ReadInt32(data + 40);
            else if (eventType == 7)
                tradePartnerEntityId = 0;
        }

        return tradeStatusUpdateHook.Original(manager, entityId, data);
    }

    private void SendTradeRequestDetour(InventoryManager* manager, uint entityId)
    {
        suppressIncomingUntil = Environment.TickCount64 + 3000;
        sendTradeRequestHook.Original(manager, entityId);
    }

    private void OnTradePreShow(AgentEvent _, AgentArgs args)
    {
        if (!Plugin.Config.Features.AutoRefuseTrade ||
            Environment.TickCount64 <= suppressIncomingUntil)
        {
            tradePartnerEntityId = 0;
            return;
        }

        var partnerEntityId = tradePartnerEntityId;
        tradePartnerEntityId = 0;
        if (ShouldAllowTrade(partnerEntityId))
            return;

        args.PreventOriginal();
        Plugin.Scheduler.Cancel(ScheduleGroup);
        Plugin.Scheduler.Schedule(
            ScheduleGroup,
            (int)Math.Min(Plugin.Config.Trade.DelayMs, int.MaxValue),
            RefuseTrade);
    }

    private static bool ShouldAllowTrade(uint entityId)
    {
        if (entityId == 0)
            return false;

        if (Plugin.Config.Trade.AllowPartyMembers)
        {
            foreach (var member in Plugin.PartyList)
            {
                if (member.EntityId == entityId)
                    return true;
            }
        }

        if (!Plugin.Config.Trade.AllowFriends)
            return false;

        var gameObject = Plugin.ObjectTable.SearchByEntityId(entityId);
        if (gameObject == null)
            return false;

        var character = (Character*)gameObject.Address;
        return character != null && character->IsFriend;
    }

    private static void RefuseTrade()
    {
        if (!Plugin.Config.Features.AutoRefuseTrade)
            return;

        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return;

        inventory->RefuseTrade();
        NotifyTradeCancel();
    }

    private static void NotifyTradeCancel()
    {
        const string message = "已拒绝收到的交易请求。";
        var settings = Plugin.Config.Trade;

        if (settings.SendNotification)
        {
            Plugin.Notifications.AddNotification(new Notification
            {
                Title = "Keita 工具箱",
                Content = message,
                Type = NotificationType.Info,
            });
        }

        if (settings.SendChat)
            Plugin.Chat.Print($"[Keita 工具箱] {message}（{DateTime.Now:t}）");

        if (string.IsNullOrWhiteSpace(settings.ExtraCommands))
            return;

        var accumulatedDelay = 0;
        foreach (var rawLine in settings.ExtraCommands.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            if (line.StartsWith("/wait ", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line[6..].Trim(), out var standaloneWait) &&
                standaloneWait > 0)
            {
                accumulatedDelay += standaloneWait;
                continue;
            }

            var waitMatch = WaitParamRegex.Match(line);
            var command = waitMatch.Success ? waitMatch.Groups[1].Value.Trim() : line;
            if (!string.IsNullOrEmpty(command))
            {
                accumulatedDelay += 100;
                var captured = command;
                Plugin.Scheduler.Schedule(
                    ScheduleGroup,
                    accumulatedDelay,
                    () => Plugin.CommandManager.ProcessCommand(captured));
            }

            if (waitMatch.Success &&
                int.TryParse(waitMatch.Groups[2].Value, out var wait) &&
                wait > 0)
            {
                accumulatedDelay += wait;
            }
        }
    }

    public void DrawSettings()
    {
        if (!ImGui.CollapsingHeader("按条件自动拒绝交易"))
            return;

        Plugin.DrawFeatureToggle(
            "自动拒绝交易",
            Plugin.Config.Features.AutoRefuseTrade,
            value =>
            {
                Plugin.Config.Features.AutoRefuseTrade = value;
                if (!value)
                    Plugin.Scheduler.Cancel(ScheduleGroup);
            });

        var allowFriends = Plugin.Config.Trade.AllowFriends;
        if (ImGui.Checkbox("允许好友交易", ref allowFriends))
        {
            Plugin.Config.Trade.AllowFriends = allowFriends;
            Plugin.Config.Save();
        }

        var allowParty = Plugin.Config.Trade.AllowPartyMembers;
        if (ImGui.Checkbox("允许小队成员交易", ref allowParty))
        {
            Plugin.Config.Trade.AllowPartyMembers = allowParty;
            Plugin.Config.Save();
        }

        var delay = Plugin.Config.Trade.DelayMs;
        if (ImGui.InputUInt("拒绝延迟（毫秒）", ref delay))
        {
            Plugin.Config.Trade.DelayMs = delay;
            Plugin.Config.Save();
        }

        var sendChat = Plugin.Config.Trade.SendChat;
        if (ImGui.Checkbox("在本地聊天栏提示", ref sendChat))
        {
            Plugin.Config.Trade.SendChat = sendChat;
            Plugin.Config.Save();
        }

        var sendNotification = Plugin.Config.Trade.SendNotification;
        if (ImGui.Checkbox("显示通知", ref sendNotification))
        {
            Plugin.Config.Trade.SendNotification = sendNotification;
            Plugin.Config.Save();
        }

        var commands = Plugin.Config.Trade.ExtraCommands;
        if (ImGui.InputTextMultiline(
                "附加命令",
                ref commands,
                4096,
                new Vector2(0, 140)))
        {
            Plugin.Config.Trade.ExtraCommands = commands;
            Plugin.Config.Save();
        }

        Plugin.DrawHelp(
            "每行填写一条命令。/wait 1000 和末尾的 <wait.1000> 均使用毫秒延迟。");
    }

    private static readonly Regex WaitParamRegex = new(
        @"^(.*?)<wait\.(\d+)>\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
