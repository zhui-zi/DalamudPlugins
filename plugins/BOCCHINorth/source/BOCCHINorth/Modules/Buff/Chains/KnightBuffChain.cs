using BOCCHI.ActionHelpers;
using BOCCHI.Data;

namespace BOCCHI.Modules.Buff.Chains;

public class KnightBuffChain(BuffModule module) : BuffChain(Job.Knight, PlayerStatus.EnduringFortitude, Actions.Knight.Pray)
{
    protected override bool ShouldRun()
    {
        return module.Config.ApplyEnduringFortitude;
    }
}
