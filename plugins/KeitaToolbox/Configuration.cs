using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;
using Dalamud.Game.Text;
using Dalamud.Plugin;

namespace KeitaToolbox;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 6;
    public bool ProtectedFeaturesUnlocked { get; set; }
    public string AnonymousInstallId { get; set; } = string.Empty;
    public long LastUsageUnixSeconds { get; set; }

    public FeatureSwitches Features { get; set; } = new();
    public InterfaceSettings Interface { get; set; } = new();
    public BmraiSettings Bmrai { get; set; } = new();
    public DutySettings Duty { get; set; } = new();
    public AutoInviteSettings AutoInvite { get; set; } = new();
    public TradeSettings Trade { get; set; } = new();
    public PartyFinderSettings PartyFinder { get; set; } = new();
    public PluginSwitcherSettings PluginSwitcher { get; set; } = new();
    public PortraitSettings Portrait { get; set; } = new();
    public AdvancedToolsSettings Advanced { get; set; } = new();
    public CombatUtilitySettings CombatUtilities { get; set; } = new();
    public MapGearsetSettings MapGearset { get; set; } = new();
    public OccultPotSettings OccultPot { get; set; } = new();
    public string OccultPotAssistantConfig { get; set; } = string.Empty;
    public AeAssistStartupSettings AeAssistStartup { get; set; } = new();
    public VerificationMonitorSettings VerificationMonitor { get; set; } = new();

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface value) => pluginInterface = value;

    public void Save() => pluginInterface?.SavePluginConfig(this);

    public bool Migrate()
    {
        var changed = false;

        Features = Ensure(Features, ref changed);
        Interface = Ensure(Interface, ref changed);
        Bmrai = Ensure(Bmrai, ref changed);
        Duty = Ensure(Duty, ref changed);
        AutoInvite = Ensure(AutoInvite, ref changed);
        Trade = Ensure(Trade, ref changed);
        PartyFinder = Ensure(PartyFinder, ref changed);
        PluginSwitcher = Ensure(PluginSwitcher, ref changed);
        Portrait = Ensure(Portrait, ref changed);
        Advanced = Ensure(Advanced, ref changed);
        CombatUtilities = Ensure(CombatUtilities, ref changed);
        MapGearset = Ensure(MapGearset, ref changed);
        OccultPot = Ensure(OccultPot, ref changed);
        AeAssistStartup = Ensure(AeAssistStartup, ref changed);
        VerificationMonitor = Ensure(VerificationMonitor, ref changed);

        AnonymousInstallId = EnsureString(AnonymousInstallId, ref changed);
        OccultPotAssistantConfig = EnsureString(OccultPotAssistantConfig, ref changed);
        Bmrai.CommandFormat = EnsureString(Bmrai.CommandFormat, ref changed);
        AutoInvite.TextPattern = EnsureString(AutoInvite.TextPattern, ref changed);
        Trade.ExtraCommands = EnsureString(Trade.ExtraCommands, ref changed);
        PluginSwitcher.DisableInPvp = EnsureString(PluginSwitcher.DisableInPvp, ref changed);
        PluginSwitcher.EnableInPvp = EnsureString(PluginSwitcher.EnableInPvp, ref changed);

        Duty.CommenceWhitelist = Ensure(Duty.CommenceWhitelist, ref changed);
        Duty.LeaveWhitelist = Ensure(Duty.LeaveWhitelist, ref changed);
        Duty.ImmediateLeaveWhitelist = Ensure(Duty.ImmediateLeaveWhitelist, ref changed);
        AutoInvite.ListenChannels = Ensure(AutoInvite.ListenChannels, ref changed);
        PartyFinder.BlockedKeywords = Ensure(PartyFinder.BlockedKeywords, ref changed);
        PluginSwitcher.MapRules = Ensure(PluginSwitcher.MapRules, ref changed);
        MapGearset.Rules = Ensure(MapGearset.Rules, ref changed);
        VerificationMonitor.LastNotifiedExpiryUnixSeconds = Ensure(
            VerificationMonitor.LastNotifiedExpiryUnixSeconds,
            ref changed);
        VerificationMonitor.LastKnownExpiryUnixSeconds = Ensure(
            VerificationMonitor.LastKnownExpiryUnixSeconds,
            ref changed);

        var mapRuleCount = PluginSwitcher.MapRules.Count;
        PluginSwitcher.MapRules.RemoveAll(static rule => rule == null);
        changed |= mapRuleCount != PluginSwitcher.MapRules.Count;
        foreach (var rule in PluginSwitcher.MapRules)
        {
            rule.Territories = EnsureString(rule.Territories, ref changed);
            rule.Disable = EnsureString(rule.Disable, ref changed);
            rule.Enable = EnsureString(rule.Enable, ref changed);
        }

        var gearsetRuleCount = MapGearset.Rules.Count;
        MapGearset.Rules.RemoveAll(static rule => rule == null);
        changed |= gearsetRuleCount != MapGearset.Rules.Count;
        foreach (var rule in MapGearset.Rules)
        {
            rule.TerritoryIds = Ensure(rule.TerritoryIds, ref changed);
            if (rule.TerritoryId > 0 && !rule.TerritoryIds.Contains(rule.TerritoryId))
            {
                rule.TerritoryIds.Add(rule.TerritoryId);
                changed = true;
            }

            var normalized = rule.TerritoryIds
                .Where(id => id > 0)
                .Distinct()
                .Order()
                .ToList();
            if (!rule.TerritoryIds.SequenceEqual(normalized))
            {
                rule.TerritoryIds = normalized;
                changed = true;
            }

            if (rule.TerritoryId != 0)
            {
                rule.TerritoryId = 0;
                changed = true;
            }
        }

        if (!Guid.TryParseExact(AnonymousInstallId, "N", out _))
        {
            AnonymousInstallId = Guid.NewGuid().ToString("N");
            changed = true;
        }

        if (Version < 6)
        {
            Version = 6;
            changed = true;
        }

        return changed;
    }

    private static T Ensure<T>(T? value, ref bool changed)
        where T : class, new()
    {
        if (value != null)
            return value;

        changed = true;
        return new T();
    }

    private static string EnsureString(string? value, ref bool changed)
    {
        if (value != null)
            return value;

        changed = true;
        return string.Empty;
    }
}

[Serializable]
public sealed class InterfaceSettings
{
    public bool ShowFloatingButton { get; set; } = true;
}

[Serializable]
public sealed class FeatureSwitches
{
    public bool AnnounceRecruitmentOnClear { get; set; }
    public bool AutoBmraiMaxDistance { get; set; }
    public bool AutoCommenceDuty { get; set; }
    public bool AutoInviteToParty { get; set; }
    public bool AutoLeaveDuty { get; set; }
    public bool AutoRefuseTrade { get; set; }
    public bool ImeGarbageFix { get; set; }
    public bool PartyFinderDutyFilter { get; set; }
    public bool PvpPluginSwitcher { get; set; }
    public bool PortraitGearSync { get; set; }
    public bool InstantReturn { get; set; }
    public bool IgnoreCharmAndFear { get; set; }
    public bool StatusBlock { get; set; }
    public bool FrontlineRemoteInteraction { get; set; }
    public bool MapGearsetSwitch { get; set; }
    public bool OccultPotAutoRevive { get; set; }
}

[Serializable]
public sealed class BmraiSettings
{
    public float MeleeDistance { get; set; } = 3f;
    public float RangedDistance { get; set; } = 15f;
    public string CommandFormat { get; set; } = "/bmrai maxdistancetarget {0}";
    public bool CleanupBocchiAiOnCrescentExit { get; set; }
}

[Serializable]
public sealed class DutySettings
{
    public HashSet<uint> CommenceWhitelist { get; set; } = [];
    public HashSet<uint> LeaveWhitelist { get; set; } = [];
    public HashSet<uint> ImmediateLeaveWhitelist { get; set; } = [];
    public int LeaveDelayMs { get; set; }
    public bool ForceLeave { get; set; }
    public bool SkipHighEndDuties { get; set; }
    public bool LeaveMentorRoulette { get; set; }
}

[Serializable]
public sealed class AutoInviteSettings
{
    public bool RuntimeEnabled { get; set; } = true;
    public string TextPattern { get; set; } = "111|求组队";
    public bool RegexMatch { get; set; } = true;
    public int InviteDelayMs { get; set; } = 1000;
    public bool PrintMessage { get; set; }
    public HashSet<XivChatType> ListenChannels { get; set; } = [XivChatType.Shout];
}

[Serializable]
public sealed class TradeSettings
{
    public bool AllowFriends { get; set; }
    public bool AllowPartyMembers { get; set; }
    public string ExtraCommands { get; set; } = string.Empty;
    public bool SendChat { get; set; } = true;
    public bool SendNotification { get; set; } = true;
    public uint DelayMs { get; set; } = 500;
}

[Serializable]
public sealed class PartyFinderSettings
{
    public List<string> BlockedKeywords { get; set; } = [];
}

[Serializable]
public sealed class PluginSwitcherSettings
{
    public string DisableInPvp { get; set; } = string.Empty;
    public string EnableInPvp { get; set; } = string.Empty;
    public List<MapRule> MapRules { get; set; } = [];
}

[Serializable]
public sealed class MapRule
{
    public string Territories { get; set; } = string.Empty;
    public string Disable { get; set; } = string.Empty;
    public string Enable { get; set; } = string.Empty;
}

[Serializable]
public sealed class PortraitSettings
{
    public bool ReequipLinkedGlamourPlate { get; set; } = true;
    public bool UpdatePortraitOnGearsetUpdate { get; set; } = true;
    public bool SyncHeadgearChanges { get; set; } = true;
    public bool SyncRecommendedGear { get; set; } = true;
    public bool SyncAfterGlamourPlate { get; set; } = true;
    public bool SyncSharedGearsetsAfterGlamourPlate { get; set; } = true;
    public bool UpdateSharedPortraitsAfterGlamourPlate { get; set; } = true;
}

[Serializable]
public sealed class AdvancedToolsSettings
{
    public bool SpeedHack { get; set; }
    public float SpeedValue { get; set; } = 0.17f;
    public bool MovePermission { get; set; }
    public bool SkillPostActionMove { get; set; }
    public bool ActionRange { get; set; }
    public float ActionRangeValue { get; set; } = 3f;
    public bool GapCloserRange { get; set; }
    public bool SelfResurrect { get; set; }
    public bool NoFall { get; set; }
    public bool AntiKnockback { get; set; }
    public KnockbackHandlingMode AntiKnockbackMode { get; set; } = KnockbackHandlingMode.Block;
    public float AntiKnockbackDistanceMultiplier { get; set; } = 1f;
    public bool JumpRestrictionImmunity { get; set; }
    public bool LocalFlight { get; set; }
    public bool ImmediateSprint { get; set; }
    public bool KeepHeartbeat { get; set; }
    public bool KeepHeartbeatDisableInDuty { get; set; } = true;
    public bool ZOffset { get; set; }
    public float ZOffsetValue { get; set; }
    public bool DeepDungeonZOffsetMode { get; set; }
    public bool DebugLogging { get; set; }
}

[Serializable]
public sealed class CombatUtilitySettings
{
    public float FrontlineRangeBonus { get; set; } = 40f;
}

[Serializable]
public sealed class MapGearsetSettings
{
    public int DelayMs { get; set; } = 2000;
    public bool PrintChatMessage { get; set; }
    public List<MapGearsetRule> Rules { get; set; } = [];
}

[Serializable]
public sealed class OccultPotSettings
{
    public bool AutoRevivePartyOnly { get; set; } = true;
}

[Serializable]
public sealed class AeAssistStartupSettings
{
    public bool Enabled { get; set; } = true;
    public bool PrintChatMessage { get; set; } = true;
}

[Serializable]
public sealed class VerificationMonitorSettings
{
    public bool Enabled { get; set; } = true;
    public bool ShowServerInfoBar { get; set; } = true;
    public bool NotifyWithDalamud { get; set; } = true;
    public bool NotifyWithGameToast { get; set; }
    public bool NotifyWithChat { get; set; }
    public Dictionary<string, long> LastNotifiedExpiryUnixSeconds { get; set; } = [];
    public Dictionary<string, long> LastKnownExpiryUnixSeconds { get; set; } = [];
}

[Serializable]
public sealed class MapGearsetRule
{
    public List<uint> TerritoryIds { get; set; } = [];

    // Retained only to migrate configurations saved before version 2.
    public uint TerritoryId { get; set; }

    public int GearsetIndex { get; set; } = -1;
}
