using BOCCHI.ActionHelpers;
using BOCCHI.Data;

namespace BOCCHI.Modules.Buff.Chains;

public class FreelancerBuffChain(BuffModule module) : BuffChain(Job.Freelancer, PlayerStatus.QuickerStep, Actions.Freelancer.InquiringMind)
{
    protected override bool ShouldRun()
    {
        return module.Config.UseInquiringMind;
    }
}
