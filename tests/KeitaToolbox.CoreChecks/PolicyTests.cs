using System.Numerics;
using KeitaToolbox;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeitaToolbox.CoreChecks;

[TestClass]
public sealed class PolicyTests
{
    [TestMethod]
    public void CrossDCRoutingForcesAnotherDataCenterWhenIslandTimeIsLow()
    {
        CrossDCCandidate[] candidates =
        [
            new(101, 600),
            new(102, 900),
            new(103, 1_200),
        ];

        Assert.IsNull(CrossDCRoutingPolicy.SelectTarget(101, candidates, false));
        Assert.AreEqual(
            new CrossDCCandidate(102, 900),
            CrossDCRoutingPolicy.SelectTarget(101, candidates, true));
        Assert.AreEqual(
            new CrossDCCandidate(101, 600),
            CrossDCRoutingPolicy.SelectTarget(103, candidates, false));
        Assert.IsTrue(CrossDCRoutingPolicy.ShouldForceTravel(5_399));
        Assert.IsFalse(CrossDCRoutingPolicy.ShouldForceTravel(5_400));
        Assert.IsFalse(CrossDCRoutingPolicy.ShouldForceTravel(null));
    }

    [TestMethod]
    public void IslandReentryRequiresLowTimeAndDisabledCrossDataCenterTravel()
    {
        Assert.IsTrue(CrossDCRoutingPolicy.ShouldReenterIsland(true, false, 5_399));
        Assert.IsFalse(CrossDCRoutingPolicy.ShouldReenterIsland(true, false, 5_400));
        Assert.IsFalse(CrossDCRoutingPolicy.ShouldReenterIsland(true, true, 5_399));
        Assert.IsFalse(CrossDCRoutingPolicy.ShouldReenterIsland(false, false, 5_399));
        Assert.IsFalse(CrossDCRoutingPolicy.ShouldReenterIsland(true, false, null));
    }

    [TestMethod]
    public void CrossDCRoutingDoesNotForceAnExpiredCandidate()
    {
        CrossDCCandidate[] candidates =
        [
            new(101, 600),
            new(102, 300),
        ];

        Assert.IsNull(CrossDCRoutingPolicy.SelectTarget(101, candidates, true));
    }

    [TestMethod]
    public void AggroAvoidanceBuildsProjectedDetourAroundCircle()
    {
        var source = new[] { new Vector3(0, 0, 0), new Vector3(20, 0, 0) };
        var zones = new[] { new AggroAvoidanceZone(new Vector3(10, 0, 0), 4) };

        Assert.IsTrue(AggroAvoidancePolicy.TryBuild(source, zones, 6, point => point, out var safePath));
        Assert.IsGreaterThan(2, safePath.Count);
        Assert.IsTrue(AggroAvoidancePolicy.IsPathClear(safePath, zones, 6));
        Assert.AreEqual(source[0], safePath[0]);
        Assert.AreEqual(source[^1], safePath[^1]);
    }

    [TestMethod]
    public void AggroAvoidanceIgnoresDifferentHeightAndDestinationZone()
    {
        var source = new[] { new Vector3(0, 0, 0), new Vector3(20, 0, 0) };
        var highZone = new[] { new AggroAvoidanceZone(new Vector3(10, 20, 0), 4) };
        var destinationZone = new[] { new AggroAvoidanceZone(new Vector3(20, 0, 0), 4) };

        Assert.IsTrue(AggroAvoidancePolicy.TryBuild(source, highZone, 6, point => point, out var highPath));
        CollectionAssert.AreEqual(source, highPath);
        Assert.IsTrue(AggroAvoidancePolicy.TryBuild(
            source,
            destinationZone,
            6,
            point => point,
            out var destinationPath));
        CollectionAssert.AreEqual(source, destinationPath);
    }

    [TestMethod]
    public void AggroAvoidanceFailsClosedWhenDetourCannotProject()
    {
        var source = new[] { new Vector3(0, 0, 0), new Vector3(20, 0, 0) };
        var zones = new[] { new AggroAvoidanceZone(new Vector3(10, 0, 0), 4) };

        Assert.IsFalse(AggroAvoidancePolicy.TryBuild(source, zones, 6, _ => null, out _));
    }

    [TestMethod]
    public void NorthHornAethernetMenuUsesZeroBasedIndices()
    {
        var expected = new Dictionary<uint, byte>
        {
            [5571] = 0,
            [5576] = 1,
            [5572] = 2,
            [5573] = 3,
            [5574] = 4,
            [5575] = 5,
        };

        foreach (var pair in expected)
        {
            Assert.IsTrue(AethernetMenuPolicy.TryGetCrescentMenuIndex(1346, pair.Key, 99, out var index));
            Assert.AreEqual(pair.Value, index);
        }

        Assert.IsTrue(AethernetMenuPolicy.TryGetCrescentMenuIndex(1252, 4930, 3, out var southIndex));
        Assert.AreEqual((byte)3, southIndex);
        Assert.IsFalse(AethernetMenuPolicy.TryGetCrescentMenuIndex(1346, 9999, 3, out _));
    }

    [TestMethod]
    public void PluginTargetsDisableConflictsAndRestoreOriginalState()
    {
        var targets = PluginSwitchingPolicy.BuildDesiredStates(
        [
            new PluginSwitchRule("AlwaysOff, Shared", "AlwaysOn"),
            new PluginSwitchRule(string.Empty, "Shared, SecondOn"),
        ]);

        Assert.HasCount(4, targets);
        Assert.IsFalse(targets["AlwaysOff"]);
        Assert.IsTrue(targets["AlwaysOn"]);
        Assert.IsFalse(targets["Shared"]);
        Assert.HasCount(2, PluginSwitchingPolicy.ParseList("One, one, Two"));

        var simulatedStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["InitiallyOff"] = false,
        };
        var originalStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var enablePlan = PluginSwitchingPolicy.PlanChanges(
            originalStates,
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["InitiallyOff"] = true },
            name => simulatedStates.TryGetValue(name, out var state) ? state : null);

        Assert.HasCount(1, enablePlan);
        Assert.AreEqual(new PluginStateChange("InitiallyOff", true), enablePlan[0]);
        Assert.IsFalse(originalStates["InitiallyOff"]);

        simulatedStates["InitiallyOff"] = true;
        var restorePlan = PluginSwitchingPolicy.PlanChanges(
            originalStates,
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
            name => simulatedStates.TryGetValue(name, out var state) ? state : null);

        Assert.HasCount(1, restorePlan);
        Assert.AreEqual(new PluginStateChange("InitiallyOff", false), restorePlan[0]);
        Assert.IsFalse(originalStates["InitiallyOff"]);

        var retryPlan = PluginSwitchingPolicy.PlanChanges(
            originalStates,
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
            name => simulatedStates.TryGetValue(name, out var state) ? state : null);
        Assert.HasCount(1, retryPlan);

        simulatedStates["InitiallyOff"] = false;
        var confirmedPlan = PluginSwitchingPolicy.PlanChanges(
            originalStates,
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
            name => simulatedStates.TryGetValue(name, out var state) ? state : null);
        Assert.IsEmpty(confirmedPlan);
        Assert.IsEmpty(originalStates);
    }

    [TestMethod]
    public void BocchiOwnedBmrAiIsCleanedAfterLeavingCrescent()
    {
        var policy = new BocchiBmrCleanupPolicy();

        Assert.IsFalse(policy.Update(true, true, false, true, true, "Idle"));
        Assert.IsFalse(policy.Update(true, true, true, true, true, "Participating"));
        Assert.IsTrue(policy.Update(true, false, true, false, true, null));
        Assert.IsTrue(policy.Update(true, false, true, false, true, null));
        Assert.IsFalse(policy.Update(true, false, false, false, true, null));
    }

    [TestMethod]
    public void PreexistingOrManualBmrAiIsPreservedAfterLeavingCrescent()
    {
        var preexisting = new BocchiBmrCleanupPolicy();
        Assert.IsFalse(preexisting.Update(true, true, true, false, true, "Idle"));
        Assert.IsFalse(preexisting.Update(true, true, true, true, true, "Participating"));
        Assert.IsFalse(preexisting.Update(true, false, true, false, true, null));

        var manual = new BocchiBmrCleanupPolicy();
        Assert.IsFalse(manual.Update(true, true, false, false, true, "Idle"));
        Assert.IsFalse(manual.Update(true, true, true, false, true, "Idle"));
        Assert.IsFalse(manual.Update(true, false, true, false, true, null));
    }

    [TestMethod]
    public void DisabledBocchiCleanupClearsPendingOwnership()
    {
        var policy = new BocchiBmrCleanupPolicy();

        Assert.IsFalse(policy.Update(true, true, false, true, true, "Idle"));
        Assert.IsFalse(policy.Update(true, true, true, true, true, "InCriticalEncounter"));
        Assert.IsFalse(policy.Update(false, false, true, false, true, null));
        Assert.IsFalse(policy.Update(true, false, true, false, true, null));
    }

    [TestMethod]
    public void AutoInviteRequiresFreshAuthorityAndPartyState()
    {
        Assert.IsTrue(AutoInvitePolicy.CanInvite(true, true, false, true, true, 0, false, false, false));
        Assert.IsFalse(AutoInvitePolicy.CanInvite(true, true, false, true, true, 2, false, false, false));
        Assert.IsFalse(AutoInvitePolicy.CanInvite(true, true, true, true, true, 0, false, false, false));
        Assert.IsFalse(AutoInvitePolicy.CanInvite(true, true, false, true, true, 0, false, true, false));
        Assert.IsFalse(AutoInvitePolicy.CanInvite(true, true, false, true, true, 0, false, false, true));
    }

    [TestMethod]
    public void AutoAcceptRaiseRequiresAnActivePlayerRaiseInsideOccultCrescent()
    {
        const string raisePrompt = "要接受Player Name的救助吗？";

        Assert.IsTrue(AutoAcceptRaisePolicy.MatchesPrompt(raisePrompt));
        Assert.IsFalse(AutoAcceptRaisePolicy.MatchesPrompt("要返回起始点吗？"));
        Assert.IsTrue(AutoAcceptRaisePolicy.CanAccept(true, true, true, false, raisePrompt));
        Assert.IsFalse(AutoAcceptRaisePolicy.CanAccept(false, true, true, false, raisePrompt));
        Assert.IsFalse(AutoAcceptRaisePolicy.CanAccept(true, false, true, false, raisePrompt));
        Assert.IsFalse(AutoAcceptRaisePolicy.CanAccept(true, true, false, false, raisePrompt));
        Assert.IsFalse(AutoAcceptRaisePolicy.CanAccept(true, true, true, true, raisePrompt));
        Assert.IsFalse(AutoAcceptRaisePolicy.CanAccept(true, true, true, false, "要返回起始点吗？"));
    }

    [TestMethod]
    public void CurrencyExchangeCatalogMapsCrescentRewardsAndCurrencies()
    {
        var northFixative = CurrencyExchangeCatalog.Get(1346, CurrencyExchangeReward.UltimateFixative);
        var northCoffer = CurrencyExchangeCatalog.Get(1346, CurrencyExchangeReward.OldCoffer);
        var southCoffer = CurrencyExchangeCatalog.Get(1252, CurrencyExchangeReward.OldCoffer);

        Assert.HasCount(2, northFixative);
        Assert.AreEqual(new CurrencyExchangeSpec("十二城邦白银币", 51975, 0x1B0614, 1200, "终极固定剂", 51978), northFixative[0]);
        Assert.AreEqual(new CurrencyExchangeSpec("十二城邦白金币", 51976, 0x1B0615, 1920, "终极固定剂", 51978), northFixative[1]);
        Assert.AreEqual(new CurrencyExchangeSpec("十二城邦白银币", 51975, 0x1B0614, 40, "辅助道具：古旧的钱箱", 47740), northCoffer[0]);
        Assert.AreEqual(new CurrencyExchangeSpec("十二城邦白金币", 51976, 0x1B0615, 50, "辅助道具：古旧的钱箱", 47740), northCoffer[1]);
        Assert.AreEqual(new CurrencyExchangeSpec("十二城邦银币", 45043, 0x1B05B0, 40, "辅助道具：古旧的钱箱", 47740), southCoffer[0]);
        Assert.AreEqual(new CurrencyExchangeSpec("十二城邦金币", 45044, 0x1B05B2, 50, "辅助道具：古旧的钱箱", 47740), southCoffer[1]);
        Assert.IsEmpty(CurrencyExchangeCatalog.Get(1252, CurrencyExchangeReward.UltimateFixative));
    }

    [TestMethod]
    public void CurrencyExchangeConfirmationRequiresCurrencyAndRewardNames()
    {
        const string prompt = "要使用十二城邦白银币兑换古旧的钱箱吗？";

        Assert.IsTrue(CurrencyExchangeConfirmationPolicy.MatchesPrompt(prompt, "十二城邦白银币", "古旧的钱箱"));
        Assert.IsFalse(CurrencyExchangeConfirmationPolicy.MatchesPrompt(prompt, "十二城邦白金币", "古旧的钱箱"));
        Assert.IsFalse(CurrencyExchangeConfirmationPolicy.MatchesPrompt(prompt, "十二城邦白银币", "终极固定剂"));
    }

    [TestMethod]
    public void CurrencyExchangeLocationRequiresInitialAetheryteProximity()
    {
        var aetheryte = new Vector3(100f, 20f, 200f);

        Assert.IsTrue(CurrencyExchangeLocationPolicy.IsNearInitialAetheryte(
            new Vector3(106f, -50f, 208f),
            aetheryte));
        Assert.IsFalse(CurrencyExchangeLocationPolicy.IsNearInitialAetheryte(
            new Vector3(106.1f, 20f, 208f),
            aetheryte));
    }

    [TestMethod]
    public void AutomaticCurrencyExchangeRetriesAtCapAfterCooldown()
    {
        Assert.IsFalse(CurrencyExchangeRetryPolicy.ShouldQueueAutomatic(9_998, 9_999, 1_000, 0));
        Assert.IsFalse(CurrencyExchangeRetryPolicy.ShouldQueueAutomatic(9_999, 9_999, 999, 1_000));
        Assert.IsTrue(CurrencyExchangeRetryPolicy.ShouldQueueAutomatic(9_999, 9_999, 1_000, 1_000));
    }

    [TestMethod]
    public void CurrencyExchangeRestoresOnlyItsOwnBocchiState()
    {
        Assert.IsTrue(CurrencyExchangeBocchiPolicy.ShouldResume(true, true, false, false));
        Assert.IsFalse(CurrencyExchangeBocchiPolicy.ShouldResume(false, true, false, false));
        Assert.IsFalse(CurrencyExchangeBocchiPolicy.ShouldResume(true, false, false, false));
        Assert.IsFalse(CurrencyExchangeBocchiPolicy.ShouldResume(true, true, true, false));
        Assert.IsFalse(CurrencyExchangeBocchiPolicy.ShouldResume(true, true, false, true));
    }

    [TestMethod]
    public void CofferHuntHandoffCanInterruptOrFinishTheCurrentHunt()
    {
        const long spawnTime = 1_000;

        Assert.IsFalse(CofferHuntHandoffPolicy.IsMagicPotDue(700, spawnTime));
        Assert.IsTrue(CofferHuntHandoffPolicy.IsMagicPotDue(701, spawnTime));
        Assert.IsFalse(CofferHuntHandoffPolicy.ShouldInterrupt(
            CofferHuntHandoffMode.InterruptForMagicPot,
            700,
            spawnTime));
        Assert.IsTrue(CofferHuntHandoffPolicy.ShouldInterrupt(
            CofferHuntHandoffMode.InterruptForMagicPot,
            701,
            spawnTime));
        Assert.IsFalse(CofferHuntHandoffPolicy.ShouldInterrupt(
            CofferHuntHandoffMode.FinishCurrentHunt,
            701,
            spawnTime));
        Assert.IsFalse(CofferHuntHandoffPolicy.ShouldInterrupt(
            CofferHuntHandoffMode.InterruptForMagicPot,
            701,
            -1));
    }

    [TestMethod]
    public void PotFateSupportJobSwitchesBeforeStartAndRequiresParticipationDuringFate()
    {
        const long spawnTime = 1_000;

        Assert.IsFalse(PotFateSupportJobPolicy.ShouldUseSupportJob(998, spawnTime, false, false));
        Assert.IsTrue(PotFateSupportJobPolicy.ShouldUseSupportJob(999, spawnTime, false, false));
        Assert.IsTrue(PotFateSupportJobPolicy.ShouldUseSupportJob(1_000, spawnTime, false, false));
        Assert.IsTrue(PotFateSupportJobPolicy.ShouldUseSupportJob(1_001, spawnTime, false, true));
        Assert.IsFalse(PotFateSupportJobPolicy.ShouldUseSupportJob(1_031, spawnTime, false, true));
        Assert.IsFalse(PotFateSupportJobPolicy.ShouldUseSupportJob(2_000, spawnTime, false, false));
        Assert.IsTrue(PotFateSupportJobPolicy.ShouldUseSupportJob(2_000, spawnTime, true, false));
    }

    [TestMethod]
    public void DrHuntRequiresPathEvidenceAndSustainedDistance()
    {
        var confirmedRoute = new DrHuntStartConfirmation(Vector2.Zero, 10, 750);
        Assert.IsFalse(confirmedRoute.Update(0, new Vector2(12, 0), false, false, false));
        Assert.IsFalse(confirmedRoute.Update(100, new Vector2(12, 0), false, true, false));
        Assert.IsFalse(confirmedRoute.Update(800, new Vector2(13, 0), false, false, false));
        Assert.IsTrue(confirmedRoute.Update(850, new Vector2(13, 0), false, false, false));

        var fallbackRoute = new DrHuntStartConfirmation(Vector2.Zero, 10, 750);
        Assert.IsFalse(fallbackRoute.Update(100, new Vector2(12, 0), false, null, false));
        Assert.IsTrue(fallbackRoute.Update(850, new Vector2(12, 0), false, null, false));

        var drMovementRoute = new DrHuntStartConfirmation(Vector2.Zero, 10, 750);
        Assert.IsFalse(drMovementRoute.Update(100, Vector2.Zero, false, false, true));
        Assert.IsTrue(drMovementRoute.Update(850, Vector2.Zero, false, false, true));
    }

    [TestMethod]
    public void RetryGateHonorsIntervalLimitAndClear()
    {
        var retry = new BoundedRetryGate(2, 100, 1_000);
        retry.Start(0);
        Assert.IsTrue(retry.TryTake(0));
        Assert.IsFalse(retry.TryTake(50));
        Assert.IsTrue(retry.TryTake(100));
        Assert.IsTrue(retry.IsExpired(101));
        retry.Clear();
        Assert.IsFalse(retry.Active);
    }

    [TestMethod]
    public void SchedulerContinuesAfterAnActionFails()
    {
        var errors = new List<Exception>();
        var scheduler = new DeferredScheduler(errors.Add);
        var laterActionExecuted = false;
        scheduler.Schedule("later", 0, () => laterActionExecuted = true);
        scheduler.Schedule("failure", 0, () => throw new InvalidOperationException("expected"));

        scheduler.Update();

        Assert.HasCount(1, errors);
        Assert.IsTrue(laterActionExecuted);

        var cancelledActionExecuted = false;
        scheduler.Schedule("cancelled", 0, () => cancelledActionExecuted = true);
        scheduler.Cancel("cancelled");
        scheduler.Update();
        Assert.IsFalse(cancelledActionExecuted);

        var reentrantCancelledActionExecuted = false;
        scheduler.Schedule("reentrant-cancelled", 0, () => reentrantCancelledActionExecuted = true);
        scheduler.Schedule("canceller", 0, () => scheduler.Cancel("reentrant-cancelled"));
        scheduler.Update();
        Assert.IsFalse(reentrantCancelledActionExecuted);

        var clearedActionExecuted = false;
        scheduler.Schedule("cleared", 0, () => clearedActionExecuted = true);
        scheduler.Schedule("clearer", 0, scheduler.Clear);
        scheduler.Update();
        Assert.IsFalse(clearedActionExecuted);
    }

    [TestMethod]
    public void FrontlineRangeMatchesSetFortyPolicy()
    {
        Assert.AreEqual(0f, CombatUtilityPolicy.GetFrontlineRangeBonus(123, false, false, 40f));
        Assert.AreEqual(0f, CombatUtilityPolicy.GetFrontlineRangeBonus(34675, true, true, 40f));
        Assert.AreEqual(0f, CombatUtilityPolicy.GetFrontlineRangeBonus(3573, true, true, 40f));
        Assert.AreEqual(0f, CombatUtilityPolicy.GetFrontlineRangeBonus(2262, true, true, 40f));
        Assert.AreEqual(0f, CombatUtilityPolicy.GetFrontlineRangeBonus(29513, true, true, 40f));
        Assert.AreEqual(40f, CombatUtilityPolicy.GetFrontlineRangeBonus(29066, true, false, 40f));
        Assert.AreEqual(40f, CombatUtilityPolicy.GetFrontlineRangeBonus(123, true, true, 40f));
        Assert.AreEqual(3f, CombatUtilityPolicy.GetFrontlineRangeBonus(123, true, false, 40f));
        Assert.AreEqual(1.5f, CombatUtilityPolicy.GetFrontlineRangeBonus(123, true, false, 1.5f));
    }

    [TestMethod]
    public void KnockbackModesApplyExpectedMovementAndLockTime()
    {
        var blocked = AdvancedUtilityPolicy.AdjustKnockback(
            KnockbackHandlingMode.Block,
            1.25f,
            20f,
            1f);
        Assert.IsTrue(blocked.Suppress);

        var reversed = AdvancedUtilityPolicy.AdjustKnockback(
            KnockbackHandlingMode.Reverse,
            1.25f,
            20f,
            1f);
        Assert.AreEqual(-1.25f, reversed.Rotation);
        Assert.AreEqual(21f, reversed.Distance);

        var scaled = AdvancedUtilityPolicy.AdjustKnockback(
            KnockbackHandlingMode.DistanceScale,
            1.25f,
            20f,
            0.25f);
        Assert.AreEqual(5f, scaled.Distance);
        Assert.AreEqual(0f, AdvancedUtilityPolicy.AdjustKnockbackLockTime(
            KnockbackHandlingMode.Instant,
            3f));
        Assert.AreEqual(0.5f, AdvancedUtilityPolicy.AdjustKnockbackLockTime(
            KnockbackHandlingMode.Fast,
            3f));
        Assert.AreEqual(0.8f, AdvancedUtilityPolicy.AdjustKnockbackLockTime(
            KnockbackHandlingMode.Normal,
            3f));
    }

    [TestMethod]
    public void SprintInterceptionMatchesBothActionRepresentations()
    {
        Assert.IsTrue(AdvancedUtilityPolicy.IsSprintRequest(5, 4));
        Assert.IsTrue(AdvancedUtilityPolicy.IsSprintRequest(1, 3));
        Assert.IsFalse(AdvancedUtilityPolicy.IsSprintRequest(1, 4));
    }

    [TestMethod]
    public void HeartbeatRefreshUsesShortDutyRecoveryInterval()
    {
        Assert.AreEqual(10_000, AdvancedUtilityPolicy.GetHeartbeatIntervalMs(true, true));
        Assert.AreEqual(140_000, AdvancedUtilityPolicy.GetHeartbeatIntervalMs(true, false));
        Assert.AreEqual(140_000, AdvancedUtilityPolicy.GetHeartbeatIntervalMs(false, true));
    }
}
