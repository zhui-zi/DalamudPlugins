using BOCCHI.Data;
using Ocelot.Config.Handlers;

namespace BOCCHI.Modules.MobFarmer;

public class MobProvider : EnumProvider<Mob>
{
    public override string GetLabel(Mob mob)
    {
        return MobData.GetName(mob);
    }
}
