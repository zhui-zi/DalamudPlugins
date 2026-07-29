using Ocelot.Modules;

namespace BOCCHI.Modules.EventDrop;

[OcelotModule(4)]
public class EventDropModule(Plugin plugin, Config config) : Module(plugin, config)
{
    public override EventDropConfig Config
    {
        get => PluginConfig.EventDropConfig;
    }
}
