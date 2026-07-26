using System;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace PortraitGearSync;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool ReequipLinkedGlamourPlate = true;
    public bool UpdatePortraitOnGearsetUpdate = true;
    public bool SyncHeadgearChanges = true;
    public bool SyncRecommendedGear = true;
    public bool SyncAfterGlamourPlate = true;
    public bool SyncSharedGearsetsAfterGlamourPlate = true;
    public bool UpdateSharedPortraitsAfterGlamourPlate = true;

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface dalamudPluginInterface)
    {
        pluginInterface = dalamudPluginInterface;
    }

    public void Save()
    {
        pluginInterface?.SavePluginConfig(this);
    }
}
