using System;
using System.Numerics;

namespace KeitaToolbox;

internal static class AethernetMenuPolicy
{
    private const uint OccultNorthTerritory = 1346;

    internal static bool TryGetCrescentMenuIndex(
        uint territoryID,
        uint dataID,
        byte libraryIndex,
        out byte index)
    {
        if (territoryID != OccultNorthTerritory)
        {
            index = libraryIndex;
            return true;
        }

        index = dataID switch
        {
            5571 => 0,
            5576 => 1,
            5572 => 2,
            5573 => 3,
            5574 => 4,
            5575 => 5,
            _    => byte.MaxValue
        };
        return index != byte.MaxValue;
    }
}

internal static class AutoInvitePolicy
{
    internal static bool CanInvite(
        bool featureEnabled,
        bool runtimeEnabled,
        bool betweenAreas,
        bool hasLocalPlayer,
        bool hasGroup,
        int memberCount,
        bool isPartyLeader,
        bool targetAlreadyInParty,
        bool hasPendingInvitation) =>
        featureEnabled &&
        runtimeEnabled &&
        !betweenAreas &&
        hasLocalPlayer &&
        hasGroup &&
        memberCount is >= 0 and < 8 &&
        (memberCount == 0 || isPartyLeader) &&
        !targetAlreadyInParty &&
        !hasPendingInvitation;
}

internal static class PotFateSupportJobPolicy
{
    internal const long SwitchLeadSeconds = 1;
    private const long StartDetectionGraceSeconds = 30;

    internal static bool ShouldUseNinja(
        long now,
        long nextSpawnTime,
        bool potFateActive,
        bool participating,
        bool switchActive)
    {
        if (potFateActive || participating)
            return true;
        if (nextSpawnTime <= 0)
            return false;

        var remaining = nextSpawnTime - now;
        if (remaining is >= 0 and <= SwitchLeadSeconds)
            return true;

        return switchActive && remaining is >= -StartDetectionGraceSeconds and < 0;
    }
}

internal sealed class DrHuntStartConfirmation(Vector2 origin, float minimumDistance, long holdDurationMs)
{
    private long? candidateSince;
    private bool pathObserved;

    internal bool Update(
        long now,
        Vector2 position,
        bool betweenAreas,
        bool? pathRunning,
        bool routeMovementLocked)
    {
        if (pathRunning == true)
            pathObserved = true;

        if (routeMovementLocked)
        {
            candidateSince ??= now;
            return now - candidateSince.Value >= holdDurationMs;
        }

        var hasMovementEvidence = pathRunning is null || pathObserved;
        if (betweenAreas ||
            !hasMovementEvidence ||
            Vector2.Distance(position, origin) <= minimumDistance)
        {
            candidateSince = null;
            return false;
        }

        candidateSince ??= now;
        return now - candidateSince.Value >= holdDurationMs;
    }
}

internal sealed class BoundedRetryGate(int maxAttempts, long intervalMs, long timeoutMs)
{
    private int attempts;
    private long deadline;
    private long nextAttemptAt;

    internal bool Active => deadline != 0;

    internal void Start(long now)
    {
        attempts = 0;
        deadline = now + timeoutMs;
        nextAttemptAt = now;
    }

    internal bool TryTake(long now)
    {
        if (!Active || attempts >= maxAttempts || now >= deadline || now < nextAttemptAt)
            return false;

        attempts++;
        nextAttemptAt = now + intervalMs;
        return true;
    }

    internal bool IsExpired(long now) =>
        Active && (attempts >= maxAttempts || now >= deadline);

    internal void Clear()
    {
        attempts = 0;
        deadline = 0;
        nextAttemptAt = 0;
    }
}

internal static class CombatUtilityPolicy
{
    internal static float GetFrontlineRangeBonus(
        uint actionId,
        bool actionExists,
        bool isHostileMovementAction,
        float configuredBonus)
    {
        if (!actionExists || actionId is 34675 or 3573 or 2262 or 29513)
            return 0f;

        if (actionId == 29066 || isHostileMovementAction)
            return configuredBonus;

        return configuredBonus >= 2f ? 3f : configuredBonus;
    }
}
