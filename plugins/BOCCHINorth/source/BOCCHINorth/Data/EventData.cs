using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BOCCHI.Enums;

namespace BOCCHI.Data;

public struct EventData
{
    public uint Id;

    public EventType Type;

    public string InternalName;

    public Demiatma? Demiatma;

    public SoulShard? Soulshard;

    public MonsterNote? Note;

    public bool IsPot;

    public Aethernet? Aethernet;

    public Vector3? StartPosition;

    public float? Radius;

    public readonly static Dictionary<uint, EventData> Fates = new()
    {
        {
            1962,
            new EventData
            {
                Id = 1962,
                Type = EventType.Fate,
                InternalName = "Rough Waters",
                Demiatma = Enums.Demiatma.Azurite,
                StartPosition = new Vector3(162.00f, 56.00f, 676.00f),
            }
        },
        {
            1963,
            new EventData
            {
                Id = 1963,
                Type = EventType.Fate,
                InternalName = "The Golden Guardian",
                Demiatma = Enums.Demiatma.Azurite,
                StartPosition = new Vector3(373.20f, 70.00f, 486.00f),
            }
        },
        {
            1964,
            new EventData
            {
                Id = 1964,
                Type = EventType.Fate,
                InternalName = "King of the Crescent",
                Demiatma = Enums.Demiatma.Orpiment,
                StartPosition = new Vector3(-226.10f, 116.38f, 254.00f),
            }
        },
        {
            1965,
            new EventData
            {
                Id = 1965,
                Type = EventType.Fate,
                InternalName = "The Winged Terror",
                Demiatma = Enums.Demiatma.Realgar,
                Aethernet = Enums.Aethernet.TheWanderersHaven,
                StartPosition = new Vector3(-548.50f, 3.00f, -595.00f),
            }
        },
        {
            1966,
            new EventData
            {
                Id = 1966,
                Type = EventType.Fate,
                InternalName = "An Unending Duty",
                Demiatma = Enums.Demiatma.Malachite,
                StartPosition = new Vector3(-223.10f, 107.00f, 36.00f),
            }
        },
        {
            1967,
            new EventData
            {
                Id = 1967,
                Type = EventType.Fate,
                InternalName = "Brain Drain",
                Demiatma = Enums.Demiatma.Realgar,
                Aethernet = Enums.Aethernet.CrystallizedCaverns,
                StartPosition = new Vector3(-48.10f, 111.76f, -320.00f),
            }
        },
        {
            1968,
            new EventData
            {
                Id = 1968,
                Type = EventType.Fate,
                InternalName = "A Delicate Balance",
                Demiatma = Enums.Demiatma.Verdigris,
                StartPosition = new Vector3(-370.00f, 75.00f, 650.00f),
            }
        },
        {
            1969,
            new EventData
            {
                Id = 1969,
                Type = EventType.Fate,
                InternalName = "Sworn to Soil",
                Demiatma = Enums.Demiatma.Verdigris,
                StartPosition = new Vector3(-589.10f, 96.50f, 333.00f),
            }
        },
        {
            1970,
            new EventData
            {
                Id = 1970,
                Type = EventType.Fate,
                InternalName = "A Prying Eye",
                Demiatma = Enums.Demiatma.Azurite,
                StartPosition = new Vector3(-71.00f, 71.31f, 557.00f),
            }
        },
        {
            1971,
            new EventData
            {
                Id = 1971,
                Type = EventType.Fate,
                InternalName = "Fatal Allure",
                Demiatma = Enums.Demiatma.Orpiment,
                StartPosition = new Vector3(79.00f, 97.86f, 278.00f),
            }
        },
        {
            1972,
            new EventData
            {
                Id = 1972,
                Type = EventType.Fate,
                InternalName = "Serving Darkness",
                Demiatma = Enums.Demiatma.CaputMortuum,
                StartPosition = new Vector3(413.00f, 96.00f, -13.00f),
            }
        },
        {
            1976,
            new EventData
            {
                Id = 1976,
                Type = EventType.Fate,
                InternalName = "Persistent Pots",
                Demiatma = Enums.Demiatma.Orpiment,
                Note = MonsterNote.PersistentPots,
                StartPosition = new Vector3(200.00f, 111.73f, -215.00f),
            }
        },
        {
            1977,
            new EventData
            {
                Id = 1977,
                Type = EventType.Fate,
                InternalName = "Pleading Pots",
                Demiatma = Enums.Demiatma.Verdigris,
                Note = MonsterNote.PersistentPots,
                StartPosition = new Vector3(-481.00f, 75.00f, 528.00f),
            }
        },
        {
            2072,
            new EventData
            {
                Id = 2072,
                Type = EventType.Fate,
                InternalName = "Daylight Pottery",
                IsPot = true,
                StartPosition = new Vector3(233f, 7.729229f, -470f),
            }
        },
        {
            2073,
            new EventData
            {
                Id = 2073,
                Type = EventType.Fate,
                InternalName = "In a Pot of Bother",
                IsPot = true,
                StartPosition = new Vector3(-505.2822f, 53.14409f, 244.041f),
            }
        },
        {
            2074,
            new EventData
            {
                Id = 2074,
                Type = EventType.Fate,
                InternalName = "Raging Thrall",
                StartPosition = new Vector3(724f, 70f, 220f),
            }
        },
        {
            2075,
            new EventData
            {
                Id = 2075,
                Type = EventType.Fate,
                InternalName = "Eye to Eye",
                StartPosition = new Vector3(510f, 16.76658f, -29.99999f),
            }
        },
        {
            2076,
            new EventData
            {
                Id = 2076,
                Type = EventType.Fate,
                InternalName = "Shoreline Showdown",
                StartPosition = new Vector3(95f, 10f, 470f),
            }
        },
        {
            2077,
            new EventData
            {
                Id = 2077,
                Type = EventType.Fate,
                InternalName = "Waved Away",
                StartPosition = new Vector3(330f, 0f, -250f),
            }
        },
        {
            2078,
            new EventData
            {
                Id = 2078,
                Type = EventType.Fate,
                InternalName = "Allure of the Occult",
                StartPosition = new Vector3(-402.0002f, 29.76808f, -252.9997f),
            }
        },
        {
            2079,
            new EventData
            {
                Id = 2079,
                Type = EventType.Fate,
                InternalName = "Inconstant Gardener",
                StartPosition = new Vector3(-170f, 30f, -500f),
            }
        },
        {
            2080,
            new EventData
            {
                Id = 2080,
                Type = EventType.Fate,
                InternalName = "Territorial Dispute",
                StartPosition = new Vector3(-90f, 67.47852f, 865.9999f),
            }
        },
        {
            2081,
            new EventData
            {
                Id = 2081,
                Type = EventType.Fate,
                InternalName = "A Rotten Affair",
                StartPosition = new Vector3(-440f, 47.02659f, -790f),
            }
        },
        {
            2082,
            new EventData
            {
                Id = 2082,
                Type = EventType.Fate,
                InternalName = "Gale-force Encounter",
                StartPosition = new Vector3(-855.7433f, 70.67716f, 482.1518f),
            }
        },
        {
            2083,
            new EventData
            {
                Id = 2083,
                Type = EventType.Fate,
                InternalName = "Scale Model",
                StartPosition = new Vector3(-661.0049f, 87f, -54.00021f),
            }
        },
        {
            2084,
            new EventData
            {
                Id = 2084,
                Type = EventType.Fate,
                InternalName = "Thunderregnum",
                StartPosition = new Vector3(140f, 37f, -708f),
            }
        },
    };

    public readonly static Dictionary<uint, EventData> CriticalEncounters = new()
    {
        {
            48,
            new EventData
            {
                Id = 48,
                Type = EventType.CriticalEncounter,
                InternalName = "The Forked Tower: Blood",
            }
        },
        {
            33,
            new EventData
            {
                Id = 33,
                Type = EventType.CriticalEncounter,
                InternalName = "Scourge of the Mind",
                Demiatma = Enums.Demiatma.Azurite,
                Aethernet = Enums.Aethernet.Eldergrowth,
            }
        },
        {
            34,
            new EventData
            {
                Id = 34,
                Type = EventType.CriticalEncounter,
                InternalName = "The Black Regiment",
                Demiatma = Enums.Demiatma.Orpiment,
                Soulshard = SoulShard.Ranger,
                Note = MonsterNote.BlackChocobos,
                Aethernet = Enums.Aethernet.Eldergrowth,
            }
        },
        {
            35,
            new EventData
            {
                Id = 35,
                Type = EventType.CriticalEncounter,
                InternalName = "The Unbridled",
                Demiatma = Enums.Demiatma.Azurite,
                Soulshard = SoulShard.Berserker,
                Note = MonsterNote.CrescentBerserker,
                Aethernet = Enums.Aethernet.Eldergrowth,
            }
        },
        {
            36,
            new EventData
            {
                Id = 36,
                Type = EventType.CriticalEncounter,
                InternalName = "Crawling Death",
                Demiatma = Enums.Demiatma.Azurite,
                Aethernet = Enums.Aethernet.Eldergrowth,
            }
        },
        {
            37,
            new EventData
            {
                Id = 37,
                Type = EventType.CriticalEncounter,
                InternalName = "Calamity Bound",
                Demiatma = Enums.Demiatma.Verdigris,
                Note = MonsterNote.CloisterDemon,
                Aethernet = Enums.Aethernet.Stonemarsh,
            }
        },
        {
            38,
            new EventData
            {
                Id = 38,
                Type = EventType.CriticalEncounter,
                InternalName = "Trial by Claw",
                Demiatma = Enums.Demiatma.Malachite,
                Aethernet = Enums.Aethernet.CrystallizedCaverns,
            }
        },
        {
            39,
            new EventData
            {
                Id = 39,
                Type = EventType.CriticalEncounter,
                InternalName = "From Times Bygone",
                Demiatma = Enums.Demiatma.Malachite,
                Note = MonsterNote.MythicIdol,
                Aethernet = Enums.Aethernet.Stonemarsh,
            }
        },
        {
            40,
            new EventData
            {
                Id = 40,
                Type = EventType.CriticalEncounter,
                InternalName = "Company of Stone",
                Demiatma = Enums.Demiatma.CaputMortuum,
                Aethernet = Enums.Aethernet.BaseCamp,
            }
        },
        {
            41,
            new EventData
            {
                Id = 41,
                Type = EventType.CriticalEncounter,
                InternalName = "Shark Attack",
                Demiatma = Enums.Demiatma.Realgar,
                Note = MonsterNote.NymianPotaladus,
                Aethernet = Enums.Aethernet.TheWanderersHaven,
            }
        },
        {
            42,
            new EventData
            {
                Id = 42,
                Type = EventType.CriticalEncounter,
                InternalName = "On the Hunt",
                Demiatma = Enums.Demiatma.CaputMortuum,
                Soulshard = SoulShard.Oracle,
                Aethernet = Enums.Aethernet.Eldergrowth,
            }
        },
        {
            43,
            new EventData
            {
                Id = 43,
                Type = EventType.CriticalEncounter,
                InternalName = "With Extreme Prejudice",
                Demiatma = Enums.Demiatma.Realgar,
                Aethernet = Enums.Aethernet.TheWanderersHaven,
            }
        },
        {
            44,
            new EventData
            {
                Id = 44,
                Type = EventType.CriticalEncounter,
                InternalName = "Noise Complaint",
                Demiatma = Enums.Demiatma.Orpiment,
                Aethernet = Enums.Aethernet.BaseCamp,
            }
        },
        {
            45,
            new EventData
            {
                Id = 45,
                Type = EventType.CriticalEncounter,
                InternalName = "Cursed Concern",
                Demiatma = Enums.Demiatma.Realgar,
                Note = MonsterNote.TradeTortoise,
                Aethernet = Enums.Aethernet.TheWanderersHaven,
            }
        },
        {
            46,
            new EventData
            {
                Id = 46,
                Type = EventType.CriticalEncounter,
                InternalName = "Eternal Watch",
                Demiatma = Enums.Demiatma.CaputMortuum,
                Aethernet = Enums.Aethernet.Eldergrowth,
            }
        },
        {
            47,
            new EventData
            {
                Id = 47,
                Type = EventType.CriticalEncounter,
                InternalName = "Flame of Dusk",
                Demiatma = Enums.Demiatma.Malachite,
                Aethernet = Enums.Aethernet.CrystallizedCaverns,
            }
        },
        {
            49,
            new EventData
            {
                Id = 49,
                Type = EventType.CriticalEncounter,
                InternalName = "Many Mouths to Feed",
            }
        },
        {
            50,
            new EventData
            {
                Id = 50,
                Type = EventType.CriticalEncounter,
                InternalName = "Doubled Trouble",
                Note = MonsterNote.ConjuredCalofisteri,
            }
        },
        {
            51,
            new EventData
            {
                Id = 51,
                Type = EventType.CriticalEncounter,
                InternalName = "Quarried Away",
                Note = MonsterNote.AlabasterBlade,
            }
        },
        {
            52,
            new EventData
            {
                Id = 52,
                Type = EventType.CriticalEncounter,
                InternalName = "Forbidden Folios",
                Note = MonsterNote.Arbatel,
            }
        },
        {
            53,
            new EventData
            {
                Id = 53,
                Type = EventType.CriticalEncounter,
                InternalName = "Cursed Resurgence",
                Note = MonsterNote.ClaretDragon,
            }
        },
        {
            54,
            new EventData
            {
                Id = 54,
                Type = EventType.CriticalEncounter,
                InternalName = "Imbalanced Diet",
                Note = MonsterNote.Algol,
            }
        },
        {
            55,
            new EventData
            {
                Id = 55,
                Type = EventType.CriticalEncounter,
                InternalName = "Web of Terror",
            }
        },
        {
            56,
            new EventData
            {
                Id = 56,
                Type = EventType.CriticalEncounter,
                InternalName = "A Beast Unleashed",
            }
        },
        {
            57,
            new EventData
            {
                Id = 57,
                Type = EventType.CriticalEncounter,
                InternalName = "Dark Artistry",
                Soulshard = SoulShard.Necromancer,
                Note = MonsterNote.PhantomNecromancer,
            }
        },
        {
            58,
            new EventData
            {
                Id = 58,
                Type = EventType.CriticalEncounter,
                InternalName = "Familiar Tactics",
            }
        },
        {
            59,
            new EventData
            {
                Id = 59,
                Type = EventType.CriticalEncounter,
                InternalName = "Appalling Behavior",
                Soulshard = SoulShard.BlueMage,
                Note = MonsterNote.Pallmagia,
            }
        },
        {
            60,
            new EventData
            {
                Id = 60,
                Type = EventType.CriticalEncounter,
                InternalName = "Tiny Terror",
                Note = MonsterNote.TinyMage,
            }
        },
        {
            61,
            new EventData
            {
                Id = 61,
                Type = EventType.CriticalEncounter,
                InternalName = "Lost on the Wind",
                Note = MonsterNote.Abductor,
            }
        },
        {
            62,
            new EventData
            {
                Id = 62,
                Type = EventType.CriticalEncounter,
                InternalName = "Ahead of the Competition",
            }
        },
        {
            63,
            new EventData
            {
                Id = 63,
                Type = EventType.CriticalEncounter,
                InternalName = "Accept No Imitators",
                Note = MonsterNote.Metamorph,
            }
        },
        {
            64,
            new EventData
            {
                Id = 64,
                Type = EventType.CriticalEncounter,
                InternalName = "The Forked Tower: Magic",
            }
        },
        {
            65,
            new EventData
            {
                Id = 65,
                Type = EventType.CriticalEncounter,
                InternalName = "The Forked Tower: Magic (Extreme)",
            }
        },
    };

    public static IEnumerable<EventData> GetFatesForTerritory(uint territoryId)
    {
        return Fates.Values.Where(data =>
            territoryId == ZoneData.SOUTHHORN ? data.Id < 2000 : data.Id >= 2000
        );
    }

    public static IEnumerable<EventData> GetCriticalEncountersForTerritory(uint territoryId)
    {
        return CriticalEncounters.Values.Where(data =>
            territoryId == ZoneData.SOUTHHORN ? data.Id <= 48 : data.Id >= 49
        );
    }
}
