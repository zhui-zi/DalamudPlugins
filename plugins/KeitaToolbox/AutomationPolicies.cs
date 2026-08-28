using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;

namespace KeitaToolbox;

internal readonly record struct CrossDCCandidate(ushort DataCenter, long RemainingSeconds);

internal enum AutoDigRunMode
{
    ReturnToBase,
    ReenterCurrentIslandWhenTimeLow,
    AlternateIslands,
    CrossDataCenter
}

internal static class AutoDigRunModePolicy
{
    internal const uint SouthTerritory = 1252;
    internal const uint NorthTerritory = 1346;

    internal static uint? SelectIslandDestination(
        AutoDigRunMode mode,
        uint currentTerritory,
        float? islandTimeLeftSeconds) => mode switch
    {
        AutoDigRunMode.ReenterCurrentIslandWhenTimeLow
            when currentTerritory is SouthTerritory or NorthTerritory &&
                 CrossDCRoutingPolicy.ShouldForceTravel(islandTimeLeftSeconds) => currentTerritory,
        AutoDigRunMode.AlternateIslands when currentTerritory == SouthTerritory => NorthTerritory,
        AutoDigRunMode.AlternateIslands when currentTerritory == NorthTerritory => SouthTerritory,
        _ => null
    };

    internal static AutoDigRunMode ResolveLegacyMode(
        AutoDigRunMode configuredMode,
        bool? legacyCrossDataCenter,
        bool? legacyReenterCurrentIsland) =>
        legacyCrossDataCenter == true
            ? AutoDigRunMode.CrossDataCenter
            : legacyReenterCurrentIsland == true
                ? AutoDigRunMode.ReenterCurrentIslandWhenTimeLow
                : configuredMode;
}

internal static class CrossDCRoutingPolicy
{
    private const float ForcedTravelThresholdSeconds = 90 * 60;

    internal static bool ShouldForceTravel(float? islandTimeLeftSeconds) =>
        islandTimeLeftSeconds is > 0 and < ForcedTravelThresholdSeconds;

    internal static CrossDCCandidate? SelectTarget(
        ushort currentDataCenter,
        IReadOnlyList<CrossDCCandidate> candidates,
        bool forceTravel)
    {
        CrossDCCandidate? best = null;
        foreach (var candidate in candidates)
        {
            if (candidate.RemainingSeconds is <= 300 or long.MaxValue ||
                forceTravel && candidate.DataCenter == currentDataCenter ||
                best is { } currentBest && candidate.RemainingSeconds >= currentBest.RemainingSeconds)
                continue;

            best = candidate;
        }

        if (!forceTravel && best?.DataCenter == currentDataCenter)
            return null;

        return best;
    }
}

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

internal static class AutoAcceptRaisePolicy
{
    private static readonly Regex RaisePromptRegex = new(
        @"要接受.*的救助吗？",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    internal static bool MatchesPrompt(string promptText) => RaisePromptRegex.IsMatch(promptText);

    internal static bool CanAccept(
        bool featureEnabled,
        bool inOccultCrescent,
        bool localPlayerDead,
        bool betweenAreas,
        string promptText) =>
        featureEnabled &&
        inOccultCrescent &&
        localPlayerDead &&
        !betweenAreas &&
        MatchesPrompt(promptText);
}

internal static class OccultAutoDiscardPolicy
{
    internal const long ReadyGraceMs = 2_000;

    internal static string? BuildCommand(bool featureEnabled, uint territoryID, string groupName)
    {
        var normalizedGroupName = groupName.Trim();
        if (!featureEnabled ||
            territoryID is not 1252 and not 1346 ||
            normalizedGroupName.Length == 0 ||
            normalizedGroupName.IndexOfAny(['\r', '\n']) >= 0)
            return null;

        return $"/pdrdiscard {normalizedGroupName}";
    }

    internal static bool HasStableReadyState(
        bool dailyRoutinesLoaded,
        bool playerAvailable,
        bool stateAllowsInventoryAction,
        long readyDurationMs) =>
        dailyRoutinesLoaded &&
        playerAvailable &&
        stateAllowsInventoryAction &&
        readyDurationMs >= ReadyGraceMs;
}

internal static class OccultAutoBocchiPolicy
{
    internal static bool ShouldSchedule(bool featureEnabled, uint territoryID) =>
        featureEnabled && territoryID is 1252 or 1346;

    internal static string? BuildCommand(
        bool featureEnabled,
        uint territoryID,
        bool bocchiLoaded,
        bool playerAvailable,
        bool betweenAreas) =>
        ShouldSchedule(featureEnabled, territoryID) &&
        bocchiLoaded &&
        playerAvailable &&
        !betweenAreas
            ? "/bocchiillegal on"
            : null;
}

internal static class CurrencyExchangeConfirmationPolicy
{
    internal static bool MatchesPrompt(string promptText, string currencyName, string rewardName) =>
        !string.IsNullOrWhiteSpace(promptText) &&
        promptText.Contains(currencyName, StringComparison.OrdinalIgnoreCase) &&
        promptText.Contains(rewardName, StringComparison.OrdinalIgnoreCase);
}

internal static class CurrencyExchangeLocationPolicy
{
    internal const float InitialAetheryteRadius = 10f;

    internal static bool IsNearInitialAetheryte(Vector3 playerPosition, Vector3 aetherytePosition)
    {
        var deltaX = playerPosition.X - aetherytePosition.X;
        var deltaZ = playerPosition.Z - aetherytePosition.Z;
        return (deltaX * deltaX) + (deltaZ * deltaZ) <= InitialAetheryteRadius * InitialAetheryteRadius;
    }
}

internal static class CurrencyExchangeRetryPolicy
{
    internal static bool ShouldQueueAutomatic(int count, int stackCap, long now, long retryAfter) =>
        count >= stackCap && now >= retryAfter;
}

internal static class MagicPotStandbyPolicy
{
    internal const float DesiredOffsetRadius = 15f;
    internal const float ArrivalTolerance = 4f;

    internal static float GetOffsetRadius(
        Vector3 targetPosition,
        Vector3 fateCenter,
        float fateRadius)
    {
        var deltaX = targetPosition.X - fateCenter.X;
        var deltaZ = targetPosition.Z - fateCenter.Z;
        var centerOffset = MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
        var boundaryLimitedRadius = Math.Max(0f, fateRadius - centerOffset - ArrivalTolerance);
        return Math.Min(DesiredOffsetRadius, boundaryLimitedRadius);
    }
}

internal static class CurrencyExchangeBocchiPolicy
{
    internal static bool ShouldResume(
        bool wasEnabled,
        bool inOccultMap,
        bool automationBlocked,
        bool returnSuppressed) =>
        wasEnabled && inOccultMap && !automationBlocked && !returnSuppressed;
}

internal enum CofferHuntExecutor
{
    DailyRoutines,
    Bocchi,
}

internal enum CofferHuntHandoffMode
{
    InterruptForMagicPot = 0,
    FinishCurrentHunt    = 1,
}

internal static class CofferHuntHandoffPolicy
{
    internal const long LeadSeconds = 300;

    internal static bool IsMagicPotDue(long now, long nextSpawnTime) =>
        nextSpawnTime > 0 && nextSpawnTime - now < LeadSeconds;

    internal static bool ShouldInterrupt(
        CofferHuntHandoffMode mode,
        long now,
        long nextSpawnTime) =>
        mode == CofferHuntHandoffMode.InterruptForMagicPot &&
        IsMagicPotDue(now, nextSpawnTime);
}

internal enum CurrencyExchangeReward
{
    UltimateFixative,
    OldCoffer,
}

internal readonly record struct CurrencyExchangeSpec(
    string CurrencyName,
    uint CurrencyItemID,
    uint EventID,
    int Cost,
    string RewardName,
    uint RewardItemID);

internal static class CurrencyExchangeCatalog
{
    private const uint UltimateFixativeItemID = 51978;
    private const uint OldCofferItemID = 47740;

    private static readonly CurrencyExchangeSpec[] NorthFixativeExchanges =
    [
        new("十二城邦白银币", 51975, 0x1B0614, 1200, "终极固定剂", UltimateFixativeItemID),
        new("十二城邦白金币", 51976, 0x1B0615, 1920, "终极固定剂", UltimateFixativeItemID),
    ];

    private static readonly CurrencyExchangeSpec[] NorthCofferExchanges =
    [
        new("十二城邦白银币", 51975, 0x1B0614, 40, "辅助道具：古旧的钱箱", OldCofferItemID),
        new("十二城邦白金币", 51976, 0x1B0615, 50, "辅助道具：古旧的钱箱", OldCofferItemID),
    ];

    private static readonly CurrencyExchangeSpec[] SouthCofferExchanges =
    [
        new("十二城邦银币", 45043, 0x1B05B0, 40, "辅助道具：古旧的钱箱", OldCofferItemID),
        new("十二城邦金币", 45044, 0x1B05B2, 50, "辅助道具：古旧的钱箱", OldCofferItemID),
    ];

    internal static IReadOnlyList<CurrencyExchangeSpec> Get(uint territoryID, CurrencyExchangeReward reward) =>
        (territoryID, reward) switch
        {
            (1252, CurrencyExchangeReward.OldCoffer) => SouthCofferExchanges,
            (1346, CurrencyExchangeReward.OldCoffer) => NorthCofferExchanges,
            (1346, CurrencyExchangeReward.UltimateFixative) => NorthFixativeExchanges,
            _ => Array.Empty<CurrencyExchangeSpec>(),
        };
}

internal enum PotFateSupportJobTarget
{
    Ninja,
    Samurai,
}

internal static class PotFateSupportJobPolicy
{
    internal const long SwitchLeadSeconds = 1;
    private const long StartDetectionGraceSeconds = 30;

    internal static bool ShouldUseSupportJob(
        long now,
        long nextSpawnTime,
        bool participating,
        bool switchActive)
    {
        if (participating)
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
