using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Agent;
using Dalamud.Game.Command;
using Dalamud.Game.DutyState;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace KeitaToolbox;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/keitatoolbox";
    private const string ShortCommand = "/ktb";
    private const string UnlockEndpoint =
        "https://rotation-solver-release-monitor.zhuizi.workers.dev/toolbox/unlock";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IAgentLifecycle AgentLifecycle { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager Data { get; private set; } = null!;
    [PluginService] internal static IPartyFinderGui PartyFinder { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider Interop { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static INotificationManager Notifications { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    internal static Configuration Config { get; private set; } = null!;
    internal static DeferredScheduler Scheduler { get; } = new();
    internal static bool ProtectedFeaturesUnlocked => Config.ProtectedFeaturesUnlocked;

    private readonly BasicFeatures? basicFeatures;
    private readonly AutoInviteFeature? autoInviteFeature;
    private readonly AutoLeaveFeature? autoLeaveFeature;
    private readonly AutoRefuseTradeFeature? autoRefuseTradeFeature;
    private readonly PortraitGearSyncFeature? portraitFeature;
    private readonly AyanoHimituFeature? ayanoFeature;
    private readonly MapGearsetFeature? mapGearsetFeature;
    private readonly HttpClient unlockClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };
    private string protectedPassword = string.Empty;
    private Task<bool>? unlockTask;
    private string unlockError = string.Empty;
    private bool windowOpen;

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Initialize(PluginInterface);

        basicFeatures = CreateFeature("general tools", () => new BasicFeatures());
        autoInviteFeature = CreateFeature("automatic party invite", () => new AutoInviteFeature());
        autoLeaveFeature = CreateFeature("automatic duty leave", () => new AutoLeaveFeature());
        autoRefuseTradeFeature = CreateFeature(
            "automatic trade refusal",
            () => new AutoRefuseTradeFeature());
        portraitFeature = CreateFeature(
            "portrait gear synchronization",
            () => new PortraitGearSyncFeature());
        ayanoFeature = CreateFeature(
            "Ayano Himitu Box functions",
            () => new AyanoHimituFeature());
        mapGearsetFeature = CreateFeature(
            "automatic map gearset switch",
            () => new MapGearsetFeature());

        Framework.Update += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += DrawWindow;
        PluginInterface.UiBuilder.OpenConfigUi += OpenWindow;
        PluginInterface.UiBuilder.OpenMainUi += OpenWindow;
        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Keita Toolbox settings.",
        });
        CommandManager.AddHandler(ShortCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Keita Toolbox settings.",
        });

        WarnAboutLegacyPlugins();
        Log.Information("Keita Toolbox enabled.");
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(ShortCommand);
        CommandManager.RemoveHandler(Command);
        PluginInterface.UiBuilder.OpenMainUi -= OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenWindow;
        PluginInterface.UiBuilder.Draw -= DrawWindow;
        Framework.Update -= OnFrameworkUpdate;

        mapGearsetFeature?.Dispose();
        ayanoFeature?.Dispose();
        portraitFeature?.Dispose();
        autoRefuseTradeFeature?.Dispose();
        autoLeaveFeature?.Dispose();
        autoInviteFeature?.Dispose();
        basicFeatures?.Dispose();
        unlockClient.Dispose();
        Scheduler.Clear();

        Log.Information("Keita Toolbox disabled.");
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        try
        {
            Scheduler.Update();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "A deferred toolbox action failed.");
        }
    }

    private void OnCommand(string _, string arguments)
    {
        var trimmed = arguments.Trim();
        if (trimmed.StartsWith("autoinvite", StringComparison.OrdinalIgnoreCase))
        {
            if (autoInviteFeature == null)
                Chat.PrintError("[Keita Toolbox] Automatic party invite is unavailable.");
            else
                autoInviteFeature.HandleCommand(trimmed["autoinvite".Length..]);
            return;
        }

        if (trimmed.Equals("return", StringComparison.OrdinalIgnoreCase))
        {
            ExecuteInstantReturn();
            return;
        }

        windowOpen = true;
    }

    private void OpenWindow() => windowOpen = true;

    private void DrawWindow()
    {
        if (!windowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(720, 620), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Keita Toolbox", ref windowOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.TextDisabled("All features are independent. Changes are saved immediately.");
        ImGui.Separator();

        if (ImGui.BeginTabBar("ToolboxTabs"))
        {
            DrawTab("General", () =>
            {
                basicFeatures?.DrawBmraiSettings();
                basicFeatures?.DrawImeSettings();
                basicFeatures?.DrawPluginSwitcherSettings();
                if (mapGearsetFeature == null)
                    DrawUnavailable("Automatic map gearset switch");
                else
                    mapGearsetFeature.DrawSettings();
                DrawProtectedFeatureSettings();
            });
            DrawTab("Duty", () =>
            {
                basicFeatures?.DrawCommenceSettings();
                autoLeaveFeature?.DrawSettings();
            });
            DrawTab("Recruitment", () =>
            {
                basicFeatures?.DrawAnnouncementSettings();
                autoInviteFeature?.DrawSettings();
                basicFeatures?.DrawPartyFinderSettings();
            });
            DrawTab("Trade", () =>
            {
                if (autoRefuseTradeFeature == null)
                    DrawUnavailable("Automatic trade refusal");
                else
                    autoRefuseTradeFeature.DrawSettings();
            });
            DrawTab("Portrait", () =>
            {
                if (portraitFeature == null)
                    DrawUnavailable("Portrait gear synchronization");
                else
                    portraitFeature.DrawSettings();
            });
            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    private static void DrawTab(string label, Action draw)
    {
        if (!ImGui.BeginTabItem(label))
            return;

        ImGui.Spacing();
        draw();
        ImGui.EndTabItem();
    }

    internal static bool DrawFeatureToggle(string label, bool value, Action<bool> setter)
    {
        var changedValue = value;
        if (!ImGui.Checkbox($"Enable {label}", ref changedValue))
            return false;

        setter(changedValue);
        Config.Save();
        return true;
    }

    internal static void DrawHelp(string text)
    {
        ImGui.Indent();
        ImGui.TextDisabled(text);
        ImGui.Unindent();
        ImGui.Spacing();
    }

    private void DrawProtectedFeatureSettings()
    {
        if (!ProtectedFeaturesUnlocked)
        {
            if (!ImGui.CollapsingHeader("Protected Ayano and I-Ching tools"))
                return;

            ImGui.TextWrapped(
                "Enter the toolbox password once to unlock all Ayano and I-Ching derived functions on this installation.");
            CompleteUnlockRequest();
            ImGui.SetNextItemWidth(300f);
            var submitted = ImGui.InputText(
                "Password",
                ref protectedPassword,
                128,
                ImGuiInputTextFlags.Password | ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            submitted |= ImGui.Button("Unlock");

            if (submitted && unlockTask == null)
            {
                unlockError = string.Empty;
                unlockTask = VerifyProtectedPasswordAsync(protectedPassword);
                protectedPassword = string.Empty;
            }

            if (!ProtectedFeaturesUnlocked)
            {
                if (unlockTask != null)
                    ImGui.TextDisabled("Verifying...");
                else if (unlockError.Length > 0)
                    ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), unlockError);
                DrawHelp(
                    "The password is verified over HTTPS and is not stored. Only the successful unlock state is saved locally.");
                return;
            }
        }

        DrawInstantReturnSettings();
        if (ayanoFeature == null)
        {
            DrawUnavailable("I-Ching tools");
            DrawUnavailable("Ayano Himitu Box functions");
        }
        else
        {
            ayanoFeature.DrawIChingSettings();
            ayanoFeature.DrawSettings();
        }
    }

    private async Task<bool> VerifyProtectedPasswordAsync(string input)
    {
        using var response = await unlockClient.PostAsJsonAsync(
            UnlockEndpoint,
            new UnlockRequest(input));
        if (response.StatusCode == HttpStatusCode.NoContent)
            return true;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return false;

        throw new HttpRequestException(
            $"Unlock service returned HTTP {(int)response.StatusCode}.");
    }

    private void CompleteUnlockRequest()
    {
        if (unlockTask is not { IsCompleted: true })
            return;

        try
        {
            if (unlockTask.GetAwaiter().GetResult())
            {
                Config.ProtectedFeaturesUnlocked = true;
                Config.Save();
                unlockError = string.Empty;
                ayanoFeature?.RefreshProtectionState();
                Chat.Print("[Keita Toolbox] Ayano and I-Ching tools unlocked.");
            }
            else
            {
                unlockError = "Incorrect password.";
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The protected tool unlock request failed.");
            unlockError = "Unable to contact the unlock service. Try again later.";
        }
        finally
        {
            unlockTask = null;
        }
    }

    private sealed record UnlockRequest(string Password);

    private static void DrawInstantReturnSettings()
    {
        if (!ImGui.CollapsingHeader("Instant return"))
            return;

        DrawFeatureToggle(
            "instant return",
            Config.Features.InstantReturn,
            value => Config.Features.InstantReturn = value);
        DrawHelp("Runs the same internal InstantReturn command used by I-Ching. Command: /ktb return");

        if (ImGui.Button("Return immediately"))
            ExecuteInstantReturn();
    }

    private static void ExecuteInstantReturn()
    {
        if (!ProtectedFeaturesUnlocked)
        {
            Chat.PrintError("[Keita Toolbox] Unlock the Ayano and I-Ching tools in settings first.");
            return;
        }

        if (!Config.Features.InstantReturn)
        {
            Chat.PrintError("[Keita Toolbox] Enable instant return in the settings first.");
            return;
        }

        GameMain.ExecuteCommand(214);
    }

    private static T? CreateFeature<T>(string name, Func<T> factory)
        where T : class, IDisposable
    {
        try
        {
            return factory();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize toolbox feature: {Feature}.", name);
            return null;
        }
    }

    private static void DrawUnavailable(string name) =>
        ImGui.TextDisabled($"{name} is unavailable. Check the Dalamud log.");

    private static void WarnAboutLegacyPlugins()
    {
        foreach (var internalName in new[]
                 {
                     "IMEGarbageFix",
                     "PortraitGearSync",
                     "AyanoHimituBox",
                     "I-Ching-GL",
                 })
        {
            var legacy = PluginInterface.InstalledPlugins.FirstOrDefault(
                plugin => plugin.InternalName.Equals(
                    internalName,
                    StringComparison.OrdinalIgnoreCase));
            if (legacy?.IsLoaded == true)
            {
                Log.Warning(
                    "Legacy plugin {Plugin} is still loaded. Disable it to avoid duplicate behavior.",
                    internalName);
            }
        }
    }
}
