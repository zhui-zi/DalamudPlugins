using Ocelot.Modules;

namespace BOCCHI.Modules.Target;

[OcelotModule]
public class TargetModule(Plugin plugin, Config config) : Module(plugin, config)
{
    public override void Update(UpdateContext context)
    {
        TargetHelper.Update();
    }
}
