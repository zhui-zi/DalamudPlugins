using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using BOCCHI.Enums;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;

namespace BOCCHI.Data;

public static class ZoneData
{
    public const uint SOUTHHORN = 1252;
    public const uint NORTHHORN = 1346;

    public readonly static HashSet<uint> OccultCrescentTerritories = [SOUTHHORN, NORTHHORN];

    // This can and should be filled using layout files or excel data
    public readonly static Dictionary<uint, Vector3> Aetherytes = new()
    {
        { SOUTHHORN, new Vector3(830.75f, 72.98f, -695.98f) },
        { NORTHHORN, new Vector3(881.1f, 258.5f, 882.2f) },
    };

    public readonly static Dictionary<uint, Vector3> StartingLocations = new()
    {
        { SOUTHHORN, new Vector3(850.33f, 72.99f, -704.07f) },
        { NORTHHORN, new Vector3(882.2f, 258.5f, 882.0f) },
    };

    // Zone functions
    public static bool IsInSouthHorn()
    {
        return Svc.ClientState.TerritoryType == SOUTHHORN;
    }

    public static bool IsInNorthHorn()
    {
        return Svc.ClientState.TerritoryType == NORTHHORN;
    }

    public static bool IsInOccultCrescent()
    {
        return Svc.Objects.LocalPlayer != null
               && OccultCrescentTerritories.Contains(Svc.ClientState.TerritoryType);
    }

    // Tower functions
    private static bool HasForkedTowerStatus()
    {
        var player = Svc.Objects.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        return player.StatusList.HasAny(
            PlayerStatus.DutiesAsAssigned,
            PlayerStatus.ResurrectionDenied,
            PlayerStatus.ResurrectionRestricted
        );
    }

    public static bool IsInForkedTowerBlood()
    {
        return HasForkedTowerStatus() && IsInSouthHorn();
    }

    public static bool IsInForkedTowerMagic()
    {
        return HasForkedTowerStatus() && IsInNorthHorn();
    }

    public static bool IsInForkedTower()
    {
        return HasForkedTowerStatus() && IsInOccultCrescent();
    }

    private static string GetCurrentZoneName()
    {
        if (IsInSouthHorn())
        {
            return "South Horn";
        }

        if (IsInNorthHorn())
        {
            return "North Horn";
        }

        throw new Exception("Unknown Zone");
    }

    public static string GetCurrentZoneDataDirectory()
    {
        var directory = Path.Join(Svc.PluginInterface.AssemblyLocation.DirectoryName, "Data", GetCurrentZoneName().Replace(" ", ""));
        Directory.CreateDirectory(directory);

        return directory;
    }

    public static Aethernet GetClosestAethernetShard(Vector3 position)
    {
        return AethernetData.All().OrderBy((data) => Vector3.Distance(position, data.Position)).First()!.Aethernet;
    }

    public static Aethernet GetBaseCampAethernet()
    {
        return IsInNorthHorn() ? Aethernet.NorthHornBaseCamp : Aethernet.BaseCamp;
    }

    public static IList<IGameObject> GetNearbyAethernetShards(float range = 4.3f)
    {
        var playerPos = Svc.Objects.LocalPlayer?.Position ?? Vector3.Zero;

        return Svc.Objects
            .Where(o => o.ObjectKind == ObjectKind.EventObj)
            .Where(o => AethernetData.All().Any(datum =>
                datum.BaseId != 0
                    ? o.BaseId == datum.BaseId
                    : Vector2.Distance(
                        new Vector2(o.Position.X, o.Position.Z),
                        new Vector2(datum.Position.X, datum.Position.Z)
                    ) <= 8f
            ))
            .Where(o => Vector3.Distance(o.Position, playerPos) <= range)
            .ToList();
    }

    public static bool IsNearAethernetShard(Aethernet aethernet, float range = 4.3f)
    {
        var data = aethernet.GetData();
        return GetNearbyAethernetShards(range).Any(o =>
                   data.BaseId != 0
                       ? o.BaseId == data.BaseId
                       : Vector2.Distance(
                           new Vector2(o.Position.X, o.Position.Z),
                           new Vector2(data.Position.X, data.Position.Z)
                       ) <= 8f
               )
               || Vector3.Distance(Svc.Objects.LocalPlayer?.Position ?? Vector3.Zero, data.Position) <= range;
    }

    public static bool HasPrecomputedHuntData()
    {
        return IsInSouthHorn();
    }

    public static IList<IGameObject> GetNearbyKnowledgeCrystal(float range = 4.5f)
    {
        var playerPos = Svc.Objects.LocalPlayer?.Position ?? Vector3.Zero;

        return Svc.Objects
            .Where(o => o.ObjectKind == ObjectKind.EventObj)
            .Where(o => o.BaseId == (uint)OccultObjectType.KnowledgeCrystal)
            .Where(o => Vector3.Distance(o.Position, playerPos) <= range)
            .ToList();
    }

    public static bool IsNearKnowledgeCrystal(float range = 4.5f)
    {
        return GetNearbyKnowledgeCrystal(range).Any();
    }
}
