using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Numerics;
using System.Threading;
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
using OmenTools;

namespace KeitaToolbox;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/keitatoolbox";
    private const string ShortCommand = "/ktb";
    private const string UnlockEndpoint =
        "https://dalamudunlock.ff14.cafe/toolbox/unlock";
    private const string UsageEndpoint =
        "https://pluginping.keita.cc/v1/heartbeat";

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
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] internal static IToastGui Toasts { get; private set; } = null!;
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
    private readonly AdvancedToolsFeature? advancedToolsFeature;
    private readonly MapGearsetFeature? mapGearsetFeature;
    private readonly OccultPotFeature? occultPotFeature;
    private readonly AeAssistStartupFeature? aeAssistStartupFeature;
    private readonly VerificationMonitorFeature? verificationMonitorFeature;
    private readonly bool omenServicesInitialized;
    private readonly HttpClient unlockClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };
    private readonly HttpClient usageClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };
    private readonly CancellationTokenSource usageCancellation = new();
    private string protectedPassword = string.Empty;
    private Task<bool>? unlockTask;
    private Task? usageTask;
    private string unlockError = string.Empty;
    private long completedUsageTimestamp;
    private bool windowOpen;

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Initialize(PluginInterface);
        if (Config.Migrate())
            Config.Save();

        try
        {
            DService.Init(PluginInterface, () => new DServiceInitOptions());
            omenServicesInitialized = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize the Magic Pot Assistant runtime.");
        }

        basicFeatures = CreateFeature("general tools", () => new BasicFeatures());
        autoInviteFeature = CreateFeature("automatic party invite", () => new AutoInviteFeature());
        autoLeaveFeature = CreateFeature("automatic duty leave", () => new AutoLeaveFeature());
        autoRefuseTradeFeature = CreateFeature(
            "automatic trade refusal",
            () => new AutoRefuseTradeFeature());
        portraitFeature = CreateFeature(
            "portrait gear synchronization",
            () => new PortraitGearSyncFeature());
        advancedToolsFeature = CreateFeature(
            "advanced tools",
            () => new AdvancedToolsFeature());
        mapGearsetFeature = CreateFeature(
            "automatic map gearset switch",
            () => new MapGearsetFeature());
        if (omenServicesInitialized)
        {
            occultPotFeature = CreateFeature(
                "Magic Pot Assistant",
                () => new OccultPotFeature());
        }
        aeAssistStartupFeature = CreateFeature(
            "AEAssist startup automation",
            () => new AeAssistStartupFeature());
        verificationMonitorFeature = CreateFeature(
            "plugin verification monitor",
            () => new VerificationMonitorFeature());

        Framework.Update += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += DrawWindow;
        PluginInterface.UiBuilder.OpenConfigUi += OpenWindow;
        PluginInterface.UiBuilder.OpenMainUi += OpenWindow;
        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开 Keita 工具箱设置。",
        });
        CommandManager.AddHandler(ShortCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开 Keita 工具箱设置。",
        });

        WarnAboutLegacyPlugins();
        usageTask = SendUsageAsync(usageCancellation.Token);
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

        verificationMonitorFeature?.Dispose();
        aeAssistStartupFeature?.Dispose();
        occultPotFeature?.Dispose();
        mapGearsetFeature?.Dispose();
        advancedToolsFeature?.Dispose();
        portraitFeature?.Dispose();
        autoRefuseTradeFeature?.Dispose();
        autoLeaveFeature?.Dispose();
        autoInviteFeature?.Dispose();
        basicFeatures?.Dispose();
        usageCancellation.Cancel();
        usageClient.Dispose();
        usageCancellation.Dispose();
        unlockClient.Dispose();
        Scheduler.Clear();
        if (omenServicesInitialized)
        {
            try
            {
                DService.Uninit();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to dispose the Magic Pot Assistant runtime.");
            }
        }

        Log.Information("Keita Toolbox disabled.");
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        try
        {
            Scheduler.Update();
            advancedToolsFeature?.UpdateMouseTeleport();
            verificationMonitorFeature?.Update();
            CompleteUsageRequest();
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
                Chat.PrintError("[Keita 工具箱] 自动邀请功能当前不可用。");
            else
                autoInviteFeature.HandleCommand(trimmed["autoinvite".Length..]);
            return;
        }

        if (trimmed.Equals("return", StringComparison.OrdinalIgnoreCase))
        {
            ExecuteInstantReturn();
            return;
        }

        if (trimmed.Equals("mouse", StringComparison.OrdinalIgnoreCase))
        {
            if (advancedToolsFeature == null)
                Chat.PrintError("[Keita 工具箱] 鼠标位置传送当前不可用。");
            else
                advancedToolsFeature.TeleportToMouse();
            return;
        }

        if (trimmed.Equals("invincible", StringComparison.OrdinalIgnoreCase))
        {
            if (advancedToolsFeature == null)
                Chat.PrintError("[Keita 工具箱] 触发无敌当前不可用。");
            else
                advancedToolsFeature.TriggerInvincibility();
            return;
        }

        if (occultPotFeature?.HandleCommand(trimmed) == true)
            return;

        windowOpen = true;
    }

    private void OpenWindow() => windowOpen = true;

    private void DrawWindow()
    {
        if (!windowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(720, 620), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Keita 工具箱", ref windowOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.TextDisabled("各项功能相互独立，修改后立即保存。");
        ImGui.Separator();

        if (ImGui.BeginTabBar("ToolboxTabs"))
        {
            DrawTab("常规", () =>
            {
                basicFeatures?.DrawBmraiSettings();
                basicFeatures?.DrawImeSettings();
                if (aeAssistStartupFeature == null)
                    DrawUnavailable("AEAssist 启动自动化");
                else
                    aeAssistStartupFeature.DrawSettings();
                basicFeatures?.DrawPluginSwitcherSettings();
                if (mapGearsetFeature == null)
                    DrawUnavailable("按地图自动切换套装");
                else
                    mapGearsetFeature.DrawSettings();
                DrawProtectedFeatureSettings();
            });
            DrawTab("副本", () =>
            {
                basicFeatures?.DrawCommenceSettings();
                autoLeaveFeature?.DrawSettings();
            });
            DrawTab("招募", () =>
            {
                basicFeatures?.DrawAnnouncementSettings();
                autoInviteFeature?.DrawSettings();
                basicFeatures?.DrawPartyFinderSettings();
            });
            DrawTab("交易", () =>
            {
                if (autoRefuseTradeFeature == null)
                    DrawUnavailable("自动拒绝交易");
                else
                    autoRefuseTradeFeature.DrawSettings();
            });
            DrawTab("肖像", () =>
            {
                if (portraitFeature == null)
                    DrawUnavailable("肖像与装备套装同步");
                else
                    portraitFeature.DrawSettings();
            });
            DrawTab("新月岛", () =>
            {
                if (occultPotFeature == null)
                    DrawUnavailable("魔法罐助手");
                else
                    occultPotFeature.DrawSettings();
            });
            DrawTab("验证", () =>
            {
                if (verificationMonitorFeature == null)
                    DrawUnavailable("插件验证监控");
                else
                    verificationMonitorFeature.DrawSettings();
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
        if (!ImGui.Checkbox($"启用{label}", ref changedValue))
            return false;

        setter(changedValue);
        Config.Save();
        return true;
    }

    internal static void DrawHelp(string text)
    {
        ImGui.Indent();
        ImGui.PushTextWrapPos();
        ImGui.TextDisabled(text);
        ImGui.PopTextWrapPos();
        ImGui.Unindent();
        ImGui.Spacing();
    }

    private void DrawProtectedFeatureSettings()
    {
        if (!ProtectedFeaturesUnlocked)
        {
            if (!ImGui.CollapsingHeader("受保护的高级工具"))
                return;

            ImGui.TextWrapped(
                "首次输入工具箱密码即可解锁本机的受保护高级功能，后续无需再次输入。");
            CompleteUnlockRequest();
            ImGui.SetNextItemWidth(300f);
            var submitted = ImGui.InputText(
                "密码",
                ref protectedPassword,
                128,
                ImGuiInputTextFlags.Password | ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            submitted |= ImGui.Button("解锁");

            if (submitted && unlockTask == null)
            {
                unlockError = string.Empty;
                unlockTask = VerifyProtectedPasswordAsync(protectedPassword);
                protectedPassword = string.Empty;
            }

            if (!ProtectedFeaturesUnlocked)
            {
                if (unlockTask != null)
                    ImGui.TextDisabled("正在验证……");
                else if (unlockError.Length > 0)
                    ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), unlockError);
                DrawHelp(
                    "密码通过 HTTPS 验证且不会保存，本地仅记录验证成功状态。");
                return;
            }
        }

        DrawInstantReturnSettings();
        if (advancedToolsFeature == null)
        {
            DrawUnavailable("战斗辅助");
            DrawUnavailable("高级移动工具");
        }
        else
        {
            advancedToolsFeature.DrawCombatUtilitySettings();
            advancedToolsFeature.DrawSettings();
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
                advancedToolsFeature?.RefreshProtectionState();
                Chat.Print("[Keita 工具箱] 受保护的高级工具已解锁。");
            }
            else
            {
                unlockError = "密码错误。";
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The protected tool unlock request failed.");
            unlockError = "无法连接验证服务，请稍后重试。";
        }
        finally
        {
            unlockTask = null;
        }
    }

    private sealed record UnlockRequest(string Password);

    private async Task SendUsageAsync(CancellationToken cancellationToken)
    {
        var lastSuccess = Config.LastUsageUnixSeconds;
        var firstAttempt = true;
        try
        {
            while (true)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var delaySeconds = Math.Clamp(
                    lastSuccess + (long)TimeSpan.FromDays(1).TotalSeconds - now,
                    0,
                    (long)TimeSpan.FromDays(1).TotalSeconds);
                if (firstAttempt && delaySeconds == 0)
                    delaySeconds = Random.Shared.Next(30, 121);
                if (delaySeconds > 0)
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(delaySeconds),
                        cancellationToken);
                }

                try
                {
                    using var response = await usageClient.PostAsJsonAsync(
                        UsageEndpoint,
                        new UsageRequest(
                            Config.AnonymousInstallId,
                            typeof(Plugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"),
                        cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        lastSuccess = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        Interlocked.Exchange(ref completedUsageTimestamp, lastSuccess);
                    }
                    else
                    {
                        await Task.Delay(TimeSpan.FromHours(6), cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "A background service request failed.");
                    await Task.Delay(TimeSpan.FromHours(6), cancellationToken);
                }

                firstAttempt = false;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void CompleteUsageRequest()
    {
        var timestamp = Interlocked.Exchange(ref completedUsageTimestamp, 0);
        if (timestamp == 0)
            return;

        Config.LastUsageUnixSeconds = timestamp;
        Config.Save();
    }

    private sealed record UsageRequest(string InstallId, string Version);

    private static void DrawInstantReturnSettings()
    {
        if (!ImGui.CollapsingHeader("即刻返回"))
            return;

        DrawFeatureToggle(
            "即刻返回",
            Config.Features.InstantReturn,
            value => Config.Features.InstantReturn = value);
        DrawHelp("立即执行返回命令。命令：/ktb return");

        if (ImGui.Button("立即返回"))
            ExecuteInstantReturn();
    }

    private static void ExecuteInstantReturn()
    {
        if (!ProtectedFeaturesUnlocked)
        {
            Chat.PrintError("[Keita 工具箱] 请先在设置中解锁受保护的高级工具。");
            return;
        }

        if (!Config.Features.InstantReturn)
        {
            Chat.PrintError("[Keita 工具箱] 请先在设置中启用即刻返回。");
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
        ImGui.TextDisabled($"{name}当前不可用，请检查 Dalamud 日志。");

    private static void WarnAboutLegacyPlugins()
    {
        foreach (var internalName in new[]
                 {
                     "IMEGarbageFix",
                     "PortraitGearSync",
                     "AyanoHimituBox",
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
