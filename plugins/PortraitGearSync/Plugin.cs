using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using Dalamud.Game.Command;
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
    private const string Command = "/portraitgearsync";
    private const long GearsetSettleDelayMs = 600;
    private const long RecommendedGearSettleDelayMs = 900;
    private const long GlamourSettleDelayMs = 800;
    private const int MaxDeferredAttempts = 20;

    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;

    [PluginService]
    internal static IGameInteropProvider Interop { get; private set; } = null!;

    [PluginService]
    internal static IAgentLifecycle AgentLifecycle { get; private set; } = null!;

    [PluginService]
    internal static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    private readonly Hook<RaptureGearsetModule.Delegates.UpdateGearset> updateGearsetHook;
    private readonly Hook<DrawDataContainer.Delegates.HideHeadgear> hideHeadgearHook;
    private readonly Hook<DrawDataContainer.Delegates.SetVisor> setVisorHook;
    private readonly Hook<RecommendEquipModule.Delegates.EquipRecommendedGear> equipRecommendedHook;
    private readonly Configuration configuration;

    private bool configWindowOpen;
    private bool pendingSync;
    private bool saveGearsetFirst;
    private bool internalGearsetUpdate;
    private bool glamourReequipped;
    private bool glamourApplicationPending;
    private bool wasApplyingGlamourPlate;
    private int deferredAttempts;
    private int observedGlamourGearsetId = -1;
    private int pendingGlamourGearsetId = -1;
    private long executeAt;
    private long blockPreviewUntil;
    private long suppressVisibilityUntil;

    public Plugin()
    {
        configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        configuration.Initialize(PluginInterface);

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
        PluginInterface.UiBuilder.Draw += DrawConfigWindow;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigWindow;
        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Portrait Gear Sync settings.",
        });

        updateGearsetHook.Enable();
        hideHeadgearHook.Enable();
        setVisorHook.Enable();
        equipRecommendedHook.Enable();

        Log.Information("Portrait gear synchronization enabled.");
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(Command);
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigWindow;
        PluginInterface.UiBuilder.Draw -= DrawConfigWindow;
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
        var shouldSync =
            !internalGearsetUpdate &&
            gearsetId == module->CurrentGearsetIndex &&
            (configuration.ReequipLinkedGlamourPlate || configuration.UpdatePortraitOnGearsetUpdate);

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

        if (configuration.SyncHeadgearChanges &&
            Environment.TickCount64 > suppressVisibilityUntil &&
            IsLocalPlayer(container))
        {
            ScheduleSync(saveFirst: true, GearsetSettleDelayMs);
        }
    }

    private void SetVisorDetour(DrawDataContainer* container, bool enabled)
    {
        setVisorHook.Original(container, enabled);

        if (configuration.SyncHeadgearChanges &&
            Environment.TickCount64 > suppressVisibilityUntil &&
            IsLocalPlayer(container))
        {
            ScheduleSync(saveFirst: true, GearsetSettleDelayMs);
        }
    }

    private void EquipRecommendedGearDetour(RecommendEquipModule* module)
    {
        equipRecommendedHook.Original(module);

        if (configuration.SyncRecommendedGear)
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
        ObserveGlamourPlateApplication();

        if (!pendingSync || Environment.TickCount64 < executeAt)
            return;

        try
        {
            ProcessPendingSync();
        }
        catch (Exception ex)
        {
            Finish();
            Log.Error(ex, "Portrait synchronization failed.");
        }
    }

    private void ObserveGlamourPlateApplication()
    {
        var manager = MirageManager.Instance();
        var isApplying = manager != null && manager->IsApplyingGlamourPlate;

        if (!wasApplyingGlamourPlate && isApplying)
        {
            var module = RaptureGearsetModule.Instance();
            observedGlamourGearsetId = module == null ? -1 : module->CurrentGearsetIndex;
        }

        if (wasApplyingGlamourPlate &&
            !isApplying &&
            (configuration.SyncAfterGlamourPlate ||
             configuration.SyncSharedGearsetsAfterGlamourPlate ||
             configuration.UpdateSharedPortraitsAfterGlamourPlate))
        {
            ScheduleGlamourApplicationSync(observedGlamourGearsetId);
            observedGlamourGearsetId = -1;
        }

        wasApplyingGlamourPlate = isApplying;
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

        if (glamourApplicationPending)
        {
            var manager = MirageManager.Instance();
            if (manager != null && manager->IsApplyingGlamourPlate)
            {
                Defer();
                return;
            }

            if (pendingGlamourGearsetId < 0 || pendingGlamourGearsetId != gearsetId)
            {
                Log.Warning("Skipped glamour plate synchronization because the active gearset changed.");
                Finish();
                return;
            }

            ProcessGlamourPlateApplication(module, pendingGlamourGearsetId);
            Finish();
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
            SaveCurrentGearset(module, gearsetId);
            executeAt = Environment.TickCount64 + GearsetSettleDelayMs;
            return;
        }

        if (conditions->BoundByDuty56 || conditions->DutyRecorderPlayback)
        {
            executeAt = Environment.TickCount64 + 2000;
            return;
        }

        if (SyncCurrentPortrait(module, gearsetId))
            Finish();
    }

    private void ProcessGlamourPlateApplication(RaptureGearsetModule* module, int currentGearsetId)
    {
        var sharedSlots = configuration.SyncSharedGearsetsAfterGlamourPlate
            ? FindSharedGearsetSlots(module, currentGearsetId)
            : [];

        if (configuration.SyncAfterGlamourPlate || configuration.SyncSharedGearsetsAfterGlamourPlate)
            SaveCurrentGearset(module, currentGearsetId);

        var changedGearsets = configuration.SyncSharedGearsetsAfterGlamourPlate
            ? ApplySharedGearsetAppearances(module, currentGearsetId, sharedSlots)
            : [];

        if (configuration.UpdateSharedPortraitsAfterGlamourPlate)
        {
            foreach (var gearsetId in GetDistinctGearsetIds(sharedSlots))
                UpdateStoredPortraitChecksum(module, gearsetId);
        }

        if (configuration.SyncAfterGlamourPlate)
        {
            var gearset = module->GetGearset(currentGearsetId);
            var banner = gearset == null ? null : gearset->GetBanner();
            if (banner != null && !SendPortraitUpdate(banner))
                Log.Warning("The current instant portrait could not be updated after applying a glamour plate.");
        }

        Log.Information(
            "Glamour plate synchronization completed. Shared gearsets updated: {Count}.",
            changedGearsets.Count);
    }

    private bool SyncCurrentPortrait(RaptureGearsetModule* module, int gearsetId)
    {
        var gearset = module->GetGearset(gearsetId);
        var banner = gearset == null ? null : gearset->GetBanner();
        if (gearset == null || banner == null)
            return true;

        var equippedChecksum = UIGlobals.GenerateEquippedItemsChecksum();
        if (banner->Checksum == equippedChecksum)
            return true;

        if (configuration.ReequipLinkedGlamourPlate &&
            !glamourReequipped &&
            gearset->GlamourSetLink > 0 &&
            UIGlobals.CanApplyGlamourPlates())
        {
            glamourReequipped = true;
            suppressVisibilityUntil = Environment.TickCount64 + 3000;
            module->EquipGearset(gearset->Id, gearset->GlamourSetLink);
            executeAt = Environment.TickCount64 + GlamourSettleDelayMs;
            pendingSync = true;
            return false;
        }

        if (configuration.UpdatePortraitOnGearsetUpdate && !SendPortraitUpdate(banner))
            Log.Warning("The instant portrait could not be updated.");

        return true;
    }

    private void SaveCurrentGearset(RaptureGearsetModule* module, int gearsetId)
    {
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
    }

    private static List<SharedGearsetSlot> FindSharedGearsetSlots(
        RaptureGearsetModule* module,
        int currentGearsetId)
    {
        var result = new List<SharedGearsetSlot>();
        var source = module->GetGearset(currentGearsetId);
        if (source == null)
            return result;

        for (var gearsetId = 0; gearsetId < 100; gearsetId++)
        {
            if (gearsetId == currentGearsetId || !module->IsValidGearset(gearsetId))
                continue;

            var target = module->GetGearset(gearsetId);
            if (target == null)
                continue;

            for (var targetSlot = (int)RaptureGearsetModule.GearsetItemIndex.Head;
                 targetSlot <= (int)RaptureGearsetModule.GearsetItemIndex.RingLeft;
                 targetSlot++)
            {
                var sourceSlot = FindMatchingSourceSlot(source, target, targetSlot);
                if (sourceSlot >= 0)
                    result.Add(new SharedGearsetSlot(gearsetId, targetSlot, sourceSlot));
            }
        }

        return result;
    }

    private static int FindMatchingSourceSlot(
        RaptureGearsetModule.GearsetEntry* source,
        RaptureGearsetModule.GearsetEntry* target,
        int targetSlot)
    {
        var targetItem = target->GetItem((RaptureGearsetModule.GearsetItemIndex)targetSlot);

        if (targetSlot is (int)RaptureGearsetModule.GearsetItemIndex.RingRight
            or (int)RaptureGearsetModule.GearsetItemIndex.RingLeft)
        {
            var sameSide = source->GetItem((RaptureGearsetModule.GearsetItemIndex)targetSlot);
            if (IsSamePhysicalItem(sameSide, targetItem))
                return targetSlot;

            var otherSlot = targetSlot == (int)RaptureGearsetModule.GearsetItemIndex.RingRight
                ? (int)RaptureGearsetModule.GearsetItemIndex.RingLeft
                : (int)RaptureGearsetModule.GearsetItemIndex.RingRight;
            var otherSide = source->GetItem((RaptureGearsetModule.GearsetItemIndex)otherSlot);
            return IsSamePhysicalItem(otherSide, targetItem) ? otherSlot : -1;
        }

        var sourceItem = source->GetItem((RaptureGearsetModule.GearsetItemIndex)targetSlot);
        return IsSamePhysicalItem(sourceItem, targetItem) ? targetSlot : -1;
    }

    private static bool IsSamePhysicalItem(
        RaptureGearsetModule.GearsetItem left,
        RaptureGearsetModule.GearsetItem right)
    {
        if (left.ItemId == 0 || left.ItemId != right.ItemId)
            return false;

        for (var index = 0; index < 5; index++)
        {
            if (left.Materia[index] != right.Materia[index] ||
                left.MateriaGrades[index] != right.MateriaGrades[index])
            {
                return false;
            }
        }

        return true;
    }

    private static List<int> ApplySharedGearsetAppearances(
        RaptureGearsetModule* module,
        int currentGearsetId,
        IReadOnlyList<SharedGearsetSlot> sharedSlots)
    {
        var changedGearsets = new HashSet<int>();
        var source = module->GetGearset(currentGearsetId);
        if (source == null)
            return [];

        foreach (var match in sharedSlots)
        {
            var target = module->GetGearset(match.GearsetId);
            if (target == null)
                continue;

            var sourceItem = source->GetItem((RaptureGearsetModule.GearsetItemIndex)match.SourceSlot);
            ref var targetItem = ref target->GetItem((RaptureGearsetModule.GearsetItemIndex)match.TargetSlot);

            if (targetItem.GlamourId == sourceItem.GlamourId &&
                targetItem.Stain0Id == sourceItem.Stain0Id &&
                targetItem.Stain1Id == sourceItem.Stain1Id)
            {
                continue;
            }

            targetItem.GlamourId = sourceItem.GlamourId;
            targetItem.Stain0Id = sourceItem.Stain0Id;
            targetItem.Stain1Id = sourceItem.Stain1Id;
            targetItem.Flags &= ~(
                RaptureGearsetModule.GearsetItemFlag.ColorDiffers |
                RaptureGearsetModule.GearsetItemFlag.AppearanceDiffers);
            changedGearsets.Add(match.GearsetId);
        }

        if (changedGearsets.Count > 0)
            module->UserFileEvent.HasChanges = true;

        return [.. changedGearsets];
    }

    private static List<int> GetDistinctGearsetIds(IReadOnlyList<SharedGearsetSlot> sharedSlots)
    {
        var result = new HashSet<int>();
        foreach (var match in sharedSlots)
            result.Add(match.GearsetId);
        return [.. result];
    }

    private static bool UpdateStoredPortraitChecksum(RaptureGearsetModule* module, int gearsetId)
    {
        var gearset = module->GetGearset(gearsetId);
        var banner = gearset == null ? null : gearset->GetBanner();
        var uiModule = UIModule.Instance();
        var bannerModule = BannerModule.Instance();
        var localPlayer = (Character*)Control.GetLocalPlayer();
        if (gearset == null || banner == null || uiModule == null || bannerModule == null || localPlayer == null)
            return false;

        if (!TryGetEnabledGearsetIndex(module, gearsetId, out var enabledIndex))
            return false;

        var helpers = uiModule->GetUIModuleHelpers();
        if (helpers == null || helpers->BannerHelper == null)
            return false;

        var helper = helpers->BannerHelper;
        var gearData = new BannerGearData
        {
            EnabledGearsetIndex = enabledIndex,
        };

        helper->BannerGearData_ApplyClassJobIdAndGearVisibilityFromGearset(&gearData);
        if (!helper->BannerGearData_ApplyGearFromGearset(&gearData))
            return false;

        helper->BannerGearData_UpdateGearsetChecksum(&gearData);
        banner->LastUpdated = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        banner->Checksum = gearData.Checksum;
        helper->BannerModuleEntry_ApplyRaceGenderHeightTribe(banner, localPlayer);
        bannerModule->UserFileEvent.HasChanges = true;
        return true;
    }

    private static bool TryGetEnabledGearsetIndex(
        RaptureGearsetModule* module,
        int gearsetId,
        out byte enabledIndex)
    {
        for (byte index = 0; index < module->NumGearsets; index++)
        {
            if (module->EnabledGearsetIndex2EntryIndex[index] == gearsetId)
            {
                enabledIndex = index;
                return true;
            }
        }

        enabledIndex = 0;
        return false;
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
        glamourApplicationPending = false;
        glamourReequipped = false;
        deferredAttempts = 0;
        executeAt = Environment.TickCount64 + delayMs;
    }

    private void ScheduleGlamourApplicationSync(int gearsetId)
    {
        if (gearsetId < 0)
            return;

        pendingSync = true;
        saveGearsetFirst = false;
        glamourApplicationPending = true;
        glamourReequipped = false;
        deferredAttempts = 0;
        pendingGlamourGearsetId = gearsetId;
        executeAt = Environment.TickCount64 + GlamourSettleDelayMs;
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
        glamourApplicationPending = false;
        glamourReequipped = false;
        deferredAttempts = 0;
        pendingGlamourGearsetId = -1;
    }

    private void BlockAutomaticPreview()
    {
        blockPreviewUntil = Environment.TickCount64 + 3000;
    }

    private void OnCommand(string _, string __)
    {
        configWindowOpen = true;
    }

    private void OpenConfigWindow()
    {
        configWindowOpen = true;
    }

    private void DrawConfigWindow()
    {
        if (!configWindowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(560, 0), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("肖像装备同步设置##PortraitGearSync", ref configWindowOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.TextWrapped("所有功能均可独立开关。后台同步共用装备套装时不会切换职业，也不会装备其他套装。");
        ImGui.Spacing();

        DrawSection("装备套装更新");
        DrawOption(
            "重新应用已关联的投影模板",
            ref configuration.ReequipLinkedGlamourPlate,
            "更新当前装备套装后，在允许使用投影模板的区域重新装备该套装。");
        DrawOption(
            "自动更新当前即时肖像",
            ref configuration.UpdatePortraitOnGearsetUpdate,
            "当前装备套装更新后，自动更新与其关联的即时肖像。");

        DrawSection("装备变化");
        DrawOption(
            "同步头部装备显示与面罩调整",
            ref configuration.SyncHeadgearChanges,
            "切换头部装备显示或调整面罩后，保存当前套装并更新肖像。");
        DrawOption(
            "同步最强装备",
            ref configuration.SyncRecommendedGear,
            "应用最强装备完成后，保存当前套装并更新肖像。");

        DrawSection("投影模板应用");
        DrawOption(
            "同步当前套装与肖像",
            ref configuration.SyncAfterGlamourPlate,
            "投影模板应用完成后，自动保存当前套装并更新当前即时肖像。");
        DrawOption(
            "同步所有共用装备的套装",
            ref configuration.SyncSharedGearsetsAfterGlamourPlate,
            "把新投影和染色信息写入所有引用同一件装备的套装，不切换职业或装备套装。");
        DrawOption(
            "同步共用装备套装的肖像",
            ref configuration.UpdateSharedPortraitsAfterGlamourPlate,
            "为上一选项实际更新的套装重新计算并保存即时肖像校验和。下次切换到该职业时不会因本次投影变化恢复默认肖像。");

        ImGui.End();
    }

    private void DrawSection(string title)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted(title);
        ImGui.Separator();
    }

    private void DrawOption(string label, ref bool value, string description)
    {
        if (ImGui.Checkbox(label, ref value))
            configuration.Save();

        ImGui.Indent();
        ImGui.TextWrapped(description);
        ImGui.Unindent();
        ImGui.Spacing();
    }

    private readonly record struct SharedGearsetSlot(int GearsetId, int TargetSlot, int SourceSlot);
}
