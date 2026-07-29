using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace BOCCHI.Pathfinding;

public interface IPathfinder
{
    PathfinderState State { get; }

    Task<List<PathfinderStep>> FindPath(Vector3 start, List<uint> nodes);
}
