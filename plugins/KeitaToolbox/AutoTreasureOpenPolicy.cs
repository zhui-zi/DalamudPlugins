namespace KeitaToolbox;

internal static class AutoTreasureOpenPolicy
{
    internal static bool IsReady(
        bool enabled,
        bool boundByDuty,
        bool soloOnly,
        int partyMemberCount,
        bool playerReady,
        bool inCombat,
        bool occupied,
        long millisecondsSinceCombat,
        int postCombatCooldownMs) =>
        enabled &&
        boundByDuty &&
        (!soloOnly || partyMemberCount <= 1) &&
        playerReady &&
        !inCombat &&
        !occupied &&
        millisecondsSinceCombat >= postCombatCooldownMs;
}
