using Ocelot.Config.Handlers;

namespace BOCCHI.Modules.Automator;

public class AiTypeProvider : EnumProvider<AiType>
{
    public override string GetLabel(AiType item)
    {
        return item.ToLabel();
    }
}
