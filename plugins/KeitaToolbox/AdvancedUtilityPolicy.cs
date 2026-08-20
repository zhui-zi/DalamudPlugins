using System;

namespace KeitaToolbox;

public enum KnockbackHandlingMode
{
    Block,
    Fast,
    Instant,
    Reverse,
    DistanceScale,
    Normal,
}

internal readonly record struct KnockbackAdjustment(
    bool Suppress,
    float Rotation,
    float Distance);

internal static class AdvancedUtilityPolicy
{
    internal static KnockbackAdjustment AdjustKnockback(
        KnockbackHandlingMode mode,
        float rotation,
        float distance,
        float distanceMultiplier) =>
        mode switch
        {
            KnockbackHandlingMode.Block => new(true, rotation, distance),
            KnockbackHandlingMode.Reverse => new(false, -rotation, distance + 1f),
            KnockbackHandlingMode.DistanceScale =>
                new(false, rotation, distance * Math.Max(0f, distanceMultiplier)),
            _ => new(false, rotation, distance),
        };

    internal static float AdjustKnockbackLockTime(
        KnockbackHandlingMode mode,
        float lockTime) =>
        mode switch
        {
            KnockbackHandlingMode.Fast => 0.5f,
            KnockbackHandlingMode.Instant => 0f,
            _ when lockTime >= 2f => 0.8f,
            _ => lockTime,
        };

    internal static bool IsSprintRequest(int actionType, uint actionId) =>
        actionType == 5 && actionId == 4 ||
        actionType == 1 && actionId == 3;

    internal static long GetHeartbeatIntervalMs(bool disableInDuty, bool inDuty) =>
        disableInDuty && inDuty ? 10_000 : 140_000;
}
