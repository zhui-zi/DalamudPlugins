using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Extensions;
using OmenTools.OmenService;

namespace KeitaToolbox;

internal sealed partial class OccultPotFeature
{
    private const int CurrencyStackCap = 9999;
    private const long CurrencyExchangeSessionTimeoutMS = 10_000;
    private const long CurrencyExchangeConfirmTimeoutMS = 5_000;
    private const long CurrencyExchangeRetryCooldownMS = 30_000;
    private const long CurrencyExchangeSpacingMS = 250;
    private const uint CurrencyExchangeUpdateIntervalMS = 50;

    private bool currencyExchangeBocchiSuppressed;
    private bool restoreBocchiAfterCurrencyExchange;
    private long nextCurrencyExchangeBocchiSuppressAt;
    private long currencyExchangeWindowCleanupUntil;

    private const long CurrencyExchangeWindowCleanupMS = 1_500;

    private void ScheduleCurrencyExchangeWindowCleanup(long now)
    {
        currencyExchangeWindowCleanupUntil = Math.Max(
            currencyExchangeWindowCleanupUntil,
            now + CurrencyExchangeWindowCleanupMS);
        CloseCurrencyExchangeWindows();
    }

    private void MaintainCurrencyExchangeWindowCleanup(long now)
    {
        if (now >= currencyExchangeWindowCleanupUntil)
            return;

        CloseCurrencyExchangeWindows();
    }

    private unsafe void OnCurrencyExchangeAddon(AddonEvent _, AddonArgs args)
    {
        if ((!pendingCurrencyExchange.HasValue &&
             Environment.TickCount64 >= currencyExchangeWindowCleanupUntil) ||
            args.Addon == nint.Zero)
        {
            return;
        }

        args.Addon.ToStruct()->IsVisible = false;
    }

    private unsafe void OnCurrencyExchangeDialogAddon(AddonEvent _, AddonArgs args)
    {
        if (args.Addon.IsNull)
            return;

        if (pendingCurrencyExchange is not { } pending)
        {
            if (Environment.TickCount64 < currencyExchangeWindowCleanupUntil)
            {
                var staleAddon = args.Addon.ToStruct();
                staleAddon->IsVisible = false;
                staleAddon->Close(true);
            }

            return;
        }

        if (pendingCurrencyActionAt != 0 || pendingCurrencyConfirmationClicked)
            return;

        var addon = args.Addon.ToStruct();
        if (!addon->IsReady) return;

        var exchangeButton = addon->GetComponentButtonById(17);
        if (exchangeButton == null || !exchangeButton->IsEnabled) return;

        exchangeButton->Click();
        addon->IsVisible = false;
        MarkCurrencyExchangeConfirmed(pending, "ShopExchangeCurrencyDialog");
    }

    private static unsafe void SuppressCurrencyExchangeWindow()
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("ShopExchangeCurrency");
        if (addon != null) addon->IsVisible = false;
    }

    private static unsafe void CloseCurrencyExchangeWindows()
    {
        CloseCurrencyExchangeWindow("ShopExchangeCurrencyDialog");
        CloseCurrencyExchangeWindow("ShopExchangeCurrency");
    }

    private static unsafe void CloseCurrencyExchangeWindow(string addonName)
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(addonName);
        if (addon == null) return;
        addon->IsVisible = false;
        addon->Close(true);
    }

    private void BeginCurrencyExchangeBocchiSuppression()
    {
        if (currencyExchangeBocchiSuppressed)
            return;

        currencyExchangeBocchiSuppressed = true;
        restoreBocchiAfterCurrencyExchange =
            BocchiAutomator.TryGetEnabled(out var enabled) && enabled;
        nextCurrencyExchangeBocchiSuppressAt = Environment.TickCount64 + 1_000;

        if (restoreBocchiAfterCurrencyExchange)
            SendCommand("/bocchiillegal off");

        var stopMode = EmergencyStopBocchi();
        Plugin.Log.Information(
            $"[KeitaToolbox.MagicPot] Currency exchange acquired BOCCHI hold; restore={restoreBocchiAfterCurrencyExchange}, stop={stopMode}");
    }

    private void KeepCurrencyExchangeBocchiSuppressed(long now)
    {
        if (!currencyExchangeBocchiSuppressed ||
            !restoreBocchiAfterCurrencyExchange ||
            now < nextCurrencyExchangeBocchiSuppressAt)
        {
            return;
        }

        SendCommand("/bocchiillegal off");
        nextCurrencyExchangeBocchiSuppressAt = now + 1_000;
    }

    private void EndCurrencyExchangeBocchiSuppression(bool resume)
    {
        if (!currencyExchangeBocchiSuppressed)
            return;

        var shouldResume = resume && CurrencyExchangeBocchiPolicy.ShouldResume(
            restoreBocchiAfterCurrencyExchange,
            InOccultMapZone,
            CurrencyExchangeBlockedByAutomation,
            suppressBocchiReturn);

        currencyExchangeBocchiSuppressed = false;
        restoreBocchiAfterCurrencyExchange = false;
        nextCurrencyExchangeBocchiSuppressAt = 0;

        if (shouldResume)
            BocchiOn();
    }

    private void OnCurrencyExchangeUpdate(IFramework _)
    {
        if (!InOccultMapZone)
        {
            FrameworkManager.Instance().Unreg(OnCurrencyExchangeUpdate);
            return;
        }

        DriveCurrencyExchange();
    }

    private static partial class BocchiAutomator
    {
        public static bool TryGetEnabled(out bool enabled)
        {
            enabled = false;

            try
            {
                var bocchi = ResolvePlugin();
                var config = bocchi == null ? null : GetMember(bocchi, "Config");
                var automatorConfig = config == null ? null : GetMember(config, "AutomatorConfig");
                if (automatorConfig == null)
                    return false;

                enabled = GetMember(automatorConfig, "Enabled") is true;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
