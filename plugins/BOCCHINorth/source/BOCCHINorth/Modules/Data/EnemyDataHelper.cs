using System.Collections.Generic;
using System.IO;
using BOCCHI.Data;
using ECommons.DalamudServices;

namespace BOCCHI.Modules.Data;

using Data = List<uint>;

public class EnemyDataHelper : DataHelper<uint>
{
    protected override Dictionary<uint, string> Paths
    {
        get => new()
        {
            { ZoneData.SOUTHHORN, Path.Join(Svc.PluginInterface.ConfigDirectory.FullName, "southhorn_enemies.json") },
        };
    }

    public bool HasSharedData(Enemy enemy)
    {
        return HasSharedData(enemy.LayoutId);
    }

    public void MarkSharedData(Enemy enemy)
    {
        MarkSharedData(enemy.LayoutId);
    }
}
