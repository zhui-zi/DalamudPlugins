using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.ClientState.Statuses;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace KeitaToolbox;

internal sealed unsafe partial class AdvancedToolsFeature
{
    private sealed class TrackedPartyBuff
    {
        public string MemberName { get; set; } = string.Empty;
        public string BuffName { get; set; } = string.Empty;
        public DateTime ExpectedEndUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public float LastRemainingSeconds { get; set; }
        public bool WasDead { get; set; }
    }

    private static readonly string[] RemovablePartyBuffNames =
    [
        "星空构想",
        "义结金兰：攻击",
        "战斗连祷",
        "神秘环",
        "占卜",
        "太阳神之衡",
        "战争神之枪",
        "战斗之声",
        "光明神的最终乐章",
        "标准舞步结束",
        "技巧舞步结束",
        "灼热之光",
        "鼓励",
    ];

    private readonly HashSet<uint> pendingStatusRemoval = [];
    private readonly Dictionary<(ulong MemberId, uint StatusId), TrackedPartyBuff>
        trackedPartyBuffs = [];
    private DateTime nextPartyBuffScanUtc = DateTime.MinValue;
    private float lastSpeedMultiplier = 1f;
    private int removedPartyBuffsThisScan;
    private string lastRemovedPartyBuffName = string.Empty;
    private string lastPartyBuffDetectionMessage = string.Empty;

    public void UpdatePartyBuffs()
    {
        var settings = Plugin.Config.Advanced;
        if (!Plugin.ProtectedFeaturesUnlocked ||
            (!settings.RemoveOtherPlayerBuffs && !settings.DetectPartyBuffs))
        {
            ResetPartyBuffRuntime();
            return;
        }

        if (DateTime.UtcNow < nextPartyBuffScanUtc)
            return;

        nextPartyBuffScanUtc = DateTime.UtcNow.AddMilliseconds(100);
        removedPartyBuffsThisScan = 0;

        if (Plugin.ObjectTable.LocalPlayer is not IBattleChara localPlayer)
        {
            ResetPartyBuffRuntime();
            return;
        }

        if (settings.DetectPartyBuffs)
            DetectRemovedPartyBuffs(localPlayer.EntityId);
        else
            trackedPartyBuffs.Clear();

        if (!settings.RemoveOtherPlayerBuffs)
        {
            pendingStatusRemoval.Clear();
            return;
        }

        RemoveSelectedPartyBuffs(localPlayer);
    }

    private float SelectSpeedMultiplier()
    {
        var settings = Plugin.Config.Advanced;
        var territoryId = Plugin.ClientState.TerritoryType;
        var rule = settings.ZoneSpeeds.TryGetValue(territoryId, out var zoneRule)
            ? zoneRule
            : Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.ClientState.Instance != 0
                ? settings.DutySpeed
                : settings.NormalSpeed;

        return Math.Clamp(
            rule.Select(
                Plugin.Condition[ConditionFlag.InCombat],
                HasOtherPlayersAround()),
            0f,
            10f);
    }

    private static bool HasOtherPlayersAround()
    {
        var localEntityId = Plugin.ObjectTable.LocalPlayer?.EntityId ?? 0;
        foreach (var player in Plugin.ObjectTable.PlayerObjects)
        {
            if (player.EntityId != 0 && player.EntityId != localEntityId)
                return true;
        }

        return false;
    }

    private static void DrawSpeedRuleSettings(
        string label,
        SpeedRuleSettings rule,
        string id)
    {
        ImGui.TextUnformatted(label);
        var changed = false;
        var inCombat = rule.InCombat;
        var withPlayers = rule.WithPlayers;
        var normal = rule.Normal;
        changed |= DrawSpeedMultiplier("战斗中", $"##SpeedCombat{id}", ref inCombat);
        changed |= DrawSpeedMultiplier("周围有玩家", $"##SpeedPlayers{id}", ref withPlayers);
        changed |= DrawSpeedMultiplier("默认", $"##SpeedNormal{id}", ref normal);
        if (changed)
        {
            rule.InCombat = inCombat;
            rule.WithPlayers = withPlayers;
            rule.Normal = normal;
            Plugin.Config.Save();
        }
    }

    private static bool DrawSpeedMultiplier(string label, string id, ref float value)
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine(100f);
        ImGui.SetNextItemWidth(120f);
        if (!ImGui.DragFloat(id, ref value, 0.01f, -1f, 10f, "%.2f×"))
            return false;

        value = Math.Clamp(value, -1f, 10f);
        return true;
    }

    private void DrawPartyBuffSettings()
    {
        if (!ImGui.CollapsingHeader("团辅管理"))
            return;

        var settings = Plugin.Config.Advanced;
        DrawToggle(
            "自动移除团辅",
            settings.RemoveOtherPlayerBuffs,
            value => settings.RemoveOtherPlayerBuffs = value);
        DrawToggle(
            "检测队友提前移除团辅",
            settings.DetectPartyBuffs,
            value => settings.DetectPartyBuffs = value);
        Plugin.DrawHelp("自动移除只处理下方勾选且由其他玩家施加的团辅；检测排除本人、死亡和自然到期。");

        if (settings.DetectPartyBuffs)
        {
            Plugin.DrawColoredWrapped(
                new System.Numerics.Vector4(0.35f, 0.85f, 1f, 1f),
                $"正在跟踪 {trackedPartyBuffs.Count} 个队友团辅状态。");
        }

        var message = settings.PartyBuffRemovedMessage;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("队友提前移除时的小队文案", ref message, 256))
        {
            settings.PartyBuffRemovedMessage = message;
            Plugin.Config.Save();
        }
        Plugin.DrawHelp("可用占位符：{姓名}、{团辅名}");

        for (var i = 0; i < RemovablePartyBuffNames.Length; i++)
        {
            var name = RemovablePartyBuffNames[i];
            var selected = settings.SelectedStatusOffBuffs.Contains(name);
            if (ImGui.Checkbox($"{name}##PartyBuff{i}", ref selected))
            {
                if (selected)
                    settings.SelectedStatusOffBuffs.Add(name);
                else
                    settings.SelectedStatusOffBuffs.Remove(name);
                Plugin.Config.Save();
            }

            if (i % 2 == 0 && i + 1 < RemovablePartyBuffNames.Length)
                ImGui.SameLine(250f);
        }

        if (removedPartyBuffsThisScan > 0)
            Plugin.DrawWrapped($"本次已移除 {removedPartyBuffsThisScan} 个状态：{lastRemovedPartyBuffName}");
        if (!string.IsNullOrEmpty(lastPartyBuffDetectionMessage))
            Plugin.DrawWrapped(lastPartyBuffDetectionMessage);
    }

    private void RemoveSelectedPartyBuffs(IBattleChara localPlayer)
    {
        var localEntityId = localPlayer.EntityId;
        var visibleSelectedStatuses = new HashSet<uint>();
        var removals = new List<(uint StatusId, string Name, uint SourceId)>();

        foreach (var status in localPlayer.StatusList)
        {
            if (status.StatusId == 0 || status.SourceObject?.EntityId == localEntityId)
                continue;

            var selectedName = FindBuffName(
                status.GameData.Value.Name.ToString(),
                Plugin.Config.Advanced.SelectedStatusOffBuffs);
            if (selectedName == null)
                continue;

            visibleSelectedStatuses.Add(status.StatusId);
            if (!pendingStatusRemoval.Contains(status.StatusId))
            {
                var sourceId = status.SourceObject?.EntityId ?? 0xE0000000;
                removals.Add((status.StatusId, selectedName, sourceId == 0 ? 0xE0000000 : sourceId));
            }
        }

        pendingStatusRemoval.RemoveWhere(id => !visibleSelectedStatuses.Contains(id));
        foreach (var removal in removals)
        {
            pendingStatusRemoval.Add(removal.StatusId);
            if (!StatusManager.ExecuteStatusOff(removal.StatusId, removal.SourceId))
                SendGameChatCommand($"/statusoff {removal.Name}");
            removedPartyBuffsThisScan++;
            lastRemovedPartyBuffName = removal.Name;
        }
    }

    private void DetectRemovedPartyBuffs(uint localEntityId)
    {
        var now = DateTime.UtcNow;
        var visibleBuffs = new HashSet<(ulong, uint)>();
        var visibleMembers = new HashSet<ulong>();
        var deadMembers = new HashSet<ulong>();

        foreach (var member in Plugin.PartyList)
        {
            if (member == null || member.EntityId == localEntityId)
                continue;

            var memberId = member.ContentId != 0 ? member.ContentId : member.EntityId;
            if (memberId == 0)
                continue;

            visibleMembers.Add(memberId);
            var isDead = IsPartyMemberDead(member);
            if (isDead)
                deadMembers.Add(memberId);

            foreach (var status in member.Statuses)
            {
                if (status.StatusId == 0)
                    continue;

                var buffName = FindBuffName(
                    status.GameData.Value.Name.ToString(),
                    RemovablePartyBuffNames);
                if (buffName == null)
                    continue;

                var key = (memberId, status.StatusId);
                visibleBuffs.Add(key);
                if (!trackedPartyBuffs.TryGetValue(key, out var tracked))
                {
                    var remaining = status.RemainingTime > 0.5f
                        ? status.RemainingTime
                        : GetFallbackBuffDuration(buffName);
                    trackedPartyBuffs[key] = new TrackedPartyBuff
                    {
                        MemberName = member.Name.ToString(),
                        BuffName = buffName,
                        ExpectedEndUtc = now.AddSeconds(remaining),
                        LastSeenUtc = now,
                        LastRemainingSeconds = remaining,
                        WasDead = isDead,
                    };
                    continue;
                }

                tracked.LastSeenUtc = now;
                var currentRemaining = status.RemainingTime > 0.5f
                    ? status.RemainingTime
                    : Math.Max(0.1f, (float)(tracked.ExpectedEndUtc - now).TotalSeconds);
                tracked.ExpectedEndUtc = now.AddSeconds(currentRemaining);
                tracked.LastRemainingSeconds = currentRemaining;
                tracked.WasDead |= isDead;
                tracked.MemberName = member.Name.ToString();
            }
        }

        foreach (var entry in trackedPartyBuffs.ToArray())
        {
            if (visibleBuffs.Contains(entry.Key))
                continue;
            if (!visibleMembers.Contains(entry.Key.MemberId))
            {
                trackedPartyBuffs.Remove(entry.Key);
                continue;
            }

            var tracked = entry.Value;
            if ((now - tracked.LastSeenUtc).TotalMilliseconds < 350)
                continue;

            trackedPartyBuffs.Remove(entry.Key);
            if (deadMembers.Contains(entry.Key.MemberId) ||
                tracked.WasDead ||
                now >= tracked.ExpectedEndUtc.AddSeconds(-2) ||
                tracked.LastRemainingSeconds <= 2f)
            {
                continue;
            }

            var text = Plugin.Config.Advanced.PartyBuffRemovedMessage
                .Replace("{姓名}", tracked.MemberName, StringComparison.Ordinal)
                .Replace("{团辅名}", tracked.BuffName, StringComparison.Ordinal);
            if (SendGameChatCommand($"/p {text}"))
            {
                lastPartyBuffDetectionMessage = $"已发送：{text}";
                Plugin.Log.Information(
                    "Detected an early party buff removal: {Message}",
                    text);
            }
            else
            {
                lastPartyBuffDetectionMessage =
                    $"检测到 {tracked.MemberName} 提前失去{tracked.BuffName}，但小队消息发送失败。";
                Plugin.Log.Warning(
                    "Failed to report an early party buff removal: {Message}",
                    text);
            }
        }
    }

    private static bool IsPartyMemberDead(IPartyMember member)
    {
        if (member.GameObject?.IsDead == true)
            return true;
        return member.MaxHP != 0 && member.CurrentHP == 0;
    }

    private static string? FindBuffName(
        string actualName,
        IEnumerable<string> supportedNames)
    {
        foreach (var supportedName in supportedNames)
        {
            if (string.Equals(actualName, supportedName, StringComparison.Ordinal) ||
                actualName.Contains(supportedName, StringComparison.Ordinal) ||
                supportedName.Contains(actualName, StringComparison.Ordinal))
            {
                return supportedName;
            }
        }

        return null;
    }

    private static float GetFallbackBuffDuration(string buffName) => buffName switch
    {
        "神秘环" => 20f,
        "战斗连祷" or "占卜" or "太阳神之衡" or "战争神之枪" => 15f,
        "标准舞步结束" or "技巧舞步结束" => 60f,
        _ => 30f,
    };

    private static bool SendGameChatCommand(string command)
    {
        try
        {
            if (Plugin.CommandManager.ProcessCommand(command))
                return true;

            var shell = RaptureShellModule.Instance();
            var ui = UIModule.Instance();
            if (shell == null || ui == null)
                return false;

            var text = Utf8String.FromString(command);
            if (text == null)
                return false;

            try
            {
                shell->ExecuteCommandInner(text, ui);
                return true;
            }
            finally
            {
                text->Dtor(true);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to submit game chat command: {Command}.", command);
            return false;
        }
    }

    private void ResetPartyBuffRuntime()
    {
        removedPartyBuffsThisScan = 0;
        pendingStatusRemoval.Clear();
        trackedPartyBuffs.Clear();
    }
}
