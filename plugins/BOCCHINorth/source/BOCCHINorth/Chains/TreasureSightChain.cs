using BOCCHI.ActionHelpers;
using BOCCHI.Data;
using BOCCHI.Modules.Treasure;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;

namespace BOCCHI.Chains;

public class TreasureSightChain(TreasureModule module) : ChainFactory
{
    private readonly Job StartingJob = Job.Current;

    protected override Chain Create(Chain chain)
    {
        chain.RunIf(() => module.Config.CastTreasureSightUponReturn);

        chain.Then(Job.Freelancer.ChangeToChain);
        chain.Then(Actions.Freelancer.Treasuresight.GetCastChain()).Wait(1000);
        chain.Then(StartingJob.ChangeToChain);

        return chain;
    }
}
