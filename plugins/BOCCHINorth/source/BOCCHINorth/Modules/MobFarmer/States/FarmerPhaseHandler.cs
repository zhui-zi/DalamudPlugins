using Ocelot.States;

namespace BOCCHI.Modules.MobFarmer.States;

public abstract class FarmerPhaseHandler(MobFarmerModule module) : StateHandler<FarmerPhase, MobFarmerModule>(module);
