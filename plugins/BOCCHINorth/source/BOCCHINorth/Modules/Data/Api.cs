using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Types;

namespace BOCCHI.Modules.Data;

public class Api(DataModule module)
{
    public void Initialize()
    {
        _ = module;
    }

    public Task SendEnemyData(IGameObject obj)
    {
        return Task.CompletedTask;
    }

    public Task SendTrapData(IGameObject obj)
    {
        return Task.CompletedTask;
    }
}
