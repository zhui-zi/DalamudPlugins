using Newtonsoft.Json;

namespace KeitaToolbox;

internal sealed partial class OccultPotFeature
{
    private struct SyncContext
    {
        public string Fingerprint;
        public ushort Datacenter;
        public uint Territory;
        public uint Server;
        public uint FateID;
        public int FateTimestamp;
        public ushort NorthFateID;
        public ushort SouthFateID;
        public PotObs North;
        public PotObs South;

        public readonly bool HasObservation =>
            (North.Observed && North.Spawn > 0) || (South.Observed && South.Spawn > 0);
    }

    private readonly struct PotObs
    {
        public bool Observed { get; init; }
        public long Spawn    { get; init; }
        public long Death    { get; init; }
        public long LastSeen { get; init; }

        public static PotObs From(Pot pot) => new()
        {
            Observed = pot.LocallyObserved,
            Spawn    = pot.SpawnTime,
            Death    = pot.DeathTime,
            LastSeen = pot.LastSeenAlive
        };
    }

    private class TrackerRow
    {
        [JsonProperty("id")]
        public long RowID { get; set; }

        [JsonProperty("territory")]
        public uint Territory { get; set; }

        [JsonProperty("tracker_id")]
        public string TrackerID = string.Empty;

        [JsonProperty("last_update")]
        public long LastUpdate;

        [JsonProperty("last_fate")]
        public string LastFateHash = string.Empty;

        [JsonProperty("server")]
        public uint Server;

        [JsonProperty("fate")]
        public uint Fate;

        [JsonProperty("fate_timestamp")]
        public int FateTimestamp;

        [JsonProperty("pot_history")]
        public string PotHistory = string.Empty;
    }

    private struct SharedPot
    {
        [JsonProperty("fate_id")]
        public uint FateID { get; set; }

        [JsonProperty("spawn_time")]
        public long SpawnTime { get; set; }

        [JsonProperty("death_time")]
        public long DeathTime { get; set; }

        [JsonProperty("last_seen")]
        public long LastSeen { get; set; }
    }

    private class UploadPot
    {
        [JsonProperty("fate_id")]
        public uint FateID;

        [JsonProperty("spawn_time")]
        public long SpawnTime;

        [JsonProperty("death_time")]
        public long DeathTime;

        [JsonProperty("last_seen")]
        public long LastSeen;

        [JsonProperty("respawn_times")]
        public long[] RespawnTimes = [];

        public static UploadPot From(uint fateID, PotObs obs) => new()
        {
            FateID    = fateID,
            SpawnTime = obs.Observed ? obs.Spawn    : -1,
            DeathTime = obs.Observed ? obs.Death    : 0,
            LastSeen  = obs.Observed ? obs.LastSeen : -1
        };
    }
}
