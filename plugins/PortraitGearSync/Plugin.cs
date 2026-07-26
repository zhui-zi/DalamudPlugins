using System;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace PortraitGearSync;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const long GearsetSettleDelayMs = 600;
    private const long RecommendedGearSettleDelayMs = 900;
    private const long GlamourSettleDelayMs = 1000;
    private const int MaxDeferredAttempts = 20;

    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;

    [PluginService]
    internal static IGameInteropProvider Interop { get; private set; } = null!;

    [PluginService]
    internal static IAgentLifecycle AgentLifecycle { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    private readonly Hook<RaptureGearsetModule.Delegates.UpdateGearset> updateGearsetHook;
    private readonly Hook<DrawDataContainer.Delegates.HideHeadgear> hideHeadgearHook;
    private readonly Hook<DrawDataContainer.Delegates.SetVisor> setVisorHook;
    private readonly Hook<RecommendEquipModule.Delegates.EquipRecommendedGear> equipRecommendedHook;

    private bool pendingSync;
    private bool saveGearsetFirst;
    private bool internalGearsetUpdate;
    private bool glamourReequipped;
    private int deferredAttempts;
    private long executeAt;
    private long blockPreviewUntil;
    private long suppressVisibilityUntil;

    public Plugin()
    {
        updateGearsetHook = Interop.HookFromAddress<RaptureGearsetModule.Delegates.UpdateGearset>(
            (nint)RaptureGearsetModule.MemberFunctionPointers.UpdateGearset,
            UpdateGearsetDetour);
        hideHeadgearHook = Interop.HookFromAddress<DrawDataContainer.Delegates.HideHeadgear>(
            (nint)DrawDataContainer.MemberFunctionPointers.HideHeadgear,
            HideHeadgearDetour);
        setVisorHook = Interop.HookFromAddress<DrawDataContainer.Delegates.SetVisor>(
            (nint)DrawDataContainer.MemberFunctionPointers.SetVisor,
            SetVisorDetour);
        equipRecommendedHook = Interop.HookFromAddress<RecommendEquipModule.Delegates.EquipRecommendedGear>(
            (nint)RecommendEquipModule.MemberFunctionPointers.EquipRecommendedGear,
            EquipRecommendedGearDetour);

        AgentLifecycle.RegisterListener(AgentEvent.PreShow, AgentId.BannerPreview, OnBannerPreviewPreShow);
        Framework.Update += OnFrameworkUpdate;

        updateGearsetHook.Enable();
        hideHeadgearHook.Enable();
        setVisorHook.Enable();
        equipRecommendedHook.Enable();

        Log.Information("Portrait gear synchronization enabled.");
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        AgentLifecycle.UnregisterListener(AgentEvent.PreShow, AgentId.BannerPreview, OnBannerPreviewPreShow);

        equipRecommendedHook.Dispose();
        setVisorHook.Dispose();
        hideHeadgearHook.Dispose();
        updateGearsetHook.Dispose();

        Log.Information("Portrait gear synchronization disabled.");
    }

    private int UpdateGearsetDetour(RaptureGearsetModule* module, int gearsetId)
    {
        var shouldSync = !internalGearsetUpdate && gearsetId == module->CurrentGearsetIndex;
        if (shouldSync)
            BlockAutomaticPreview();

        var result = updateGearsetHook.Original(module, gearsetId);

        if (shouldSync)
            ScheduleSync(saveFirst: false, GearsetSettleDelayMs);

        return result;
    }

    private void HideHeadgearDetour(DrawDataContainer* container, uint unknown, bool hide)
    {
        hideHeadgearHook.Original(container, unknown, hide);

        if (Environment.TickCount64 > suppressVisibilityUntil && IsLocalPlayer(container))
            ScheduleSync(saveFirst: true, GearsetSettleDelayMs);
    }

    private void SetVisorDetour(DrawDataContainer* container, bool enabled)
    {
        setVisorHook.Original(container, enabled);

        if (Environment.TickCount64 > suppressVisibilityUntil && IsLocalPlayer(container))
            ScheduleSync(saveFirst: true, GearsetSettleDelayMs);
    }

    private void EquipRecommendedGearDetour(RecommendEquipModule* module)
    {
        equipRecommendedHook.Original(module);
        ScheduleSync(saveFirst: true, RecommendedGearSettleDelayMs);
    }

    private void OnBannerPreviewPreShow(AgentEvent _, AgentArgs args)
    {
        if (Environment.TickCount64 <= blockPreviewUntil)
        {
            blockPreviewUntil = 0;
            args.PreventOriginal();
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!pendingSync || Environment.TickCount64 < executeAt)
            return;

        try
        {
            ProcessPendingSync();
        }
        catch (Exception ex)
        {
            pendingSync = false;
            Log.Error(ex, "Portrait synchronization failed.");
        }
    }

    private void ProcessPendingSync()
    {
        var module = RaptureGearsetModule.Instance();
        if (module == null)
        {
            Defer();
            return;
        }

        var gearsetId = module->CurrentGearsetIndex;
        if (!module->IsValidGearset(gearsetId))
        {
            Finish();
            return;
        }

        var conditions = Conditions.Instance();
        if (conditions == null || conditions->BetweenAreas)
        {
            Defer();
            return;
        }

        if (saveGearsetFirst)
        {
            var recommended = RecommendEquipModule.Instance();
            if (recommended != null && recommended->IsUpdating)
            {
                Defer();
                return;
            }

            saveGearsetFirst = false;
            internalGearsetUpdate = true;
            BlockAutomaticPreview();

            try
            {
                module->UpdateGearset(gearsetId);
            }
            finally
            {
                internalGearsetUpdate = false;
            }

            executeAt = Environment.TickCount64 + GearsetSettleDelayMs;
            return;
        }

        if (conditions->BoundByDuty56 || conditions->DutyRecorderPlayback)
        {
            executeAt = Environment.TickCount64 + 2000;
            return;
        }

        var gearset = module->GetGearset(gearsetId);
        var banner = gearset == null ? null : gearset->GetBanner();
        if (gearset == null || banner == null)
        {
            Finish();
            return;
        }

        var equippedChecksum = UIGlobals.GenerateEquippedItemsChecksum();
        if (banner->Checksum == equippedChecksum)
        {
            Finish();
            return;
        }

        if (!glamourReequipped &&
            gearset->GlamourSetLink > 0 &&
            UIGlobals.CanApplyGlamourPlates())
        {
            glamourReequipped = true;
            suppressVisibilityUntil = Environment.TickCount64 + 3000;
            module->EquipGearset(gearset->Id, gearset->GlamourSetLink);
            executeAt = Environment.TickCount64 + GlamourSettleDelayMs;
            return;
        }

        if (!SendPortraitUpdate(banner))
            Log.Warning("The instant portrait could not be updated.");

        Finish();
    }

    private static bool SendPortraitUpdate(BannerModuleEntry* banner)
    {
        var localPlayer = (Character*)Control.GetLocalPlayer();
        var uiModule = UIModule.Instance();
        var bannerModule = BannerModule.Instance();
        if (localPlayer == null || uiModule == null || bannerModule == null)
            return false;

        var helpers = uiModule->GetUIModuleHelpers();
        if (helpers == null)
            return false;

        var helper = helpers->BannerHelper;
        if (helper == null ||
            !helper->BannerModuleEntry_IsCurrentCharaCardBannerOutdated(banner, true) ||
            !helper->BannerModuleEntry_IsCharacterDataOutdated(banner, true))
        {
            return false;
        }

        banner->LastUpdated = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        banner->Checksum = UIGlobals.GenerateEquippedItemsChecksum();
        helper->BannerModuleEntry_ApplyRaceGenderHeightTribe(banner, localPlayer);
        bannerModule->UserFileEvent.HasChanges = true;

        var data = new BannerData();
        helper->BannerData_ApplyBannerModuleEntry(&data, banner);
        return helper->SendBannerData(&data);
    }

    private static bool IsLocalPlayer(DrawDataContainer* container)
    {
        var localPlayer = Control.GetLocalPlayer();
        return container != null && localPlayer != null && container->OwnerObject == localPlayer;
    }

    private void ScheduleSync(bool saveFirst, long delayMs)
    {
        pendingSync = true;
        saveGearsetFirst |= saveFirst;
        glamourReequipped = false;
        deferredAttempts = 0;
        executeAt = Environment.TickCount64 + delayMs;
    }

    private void Defer()
    {
        if (++deferredAttempts > MaxDeferredAttempts)
        {
            Finish();
            return;
        }

        executeAt = Environment.TickCount64 + GearsetSettleDelayMs;
    }

    private void Finish()
    {
        pendingSync = false;
        saveGearsetFirst = false;
        glamourReequipped = false;
        deferredAttempts = 0;
    }

    private void BlockAutomaticPreview()
    {
        blockPreviewUntil = Environment.TickCount64 + 3000;
    }
}
