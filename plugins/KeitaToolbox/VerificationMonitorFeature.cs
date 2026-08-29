using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin;
using Dalamud.Utility;

namespace KeitaToolbox;

internal sealed class VerificationMonitorFeature : IDisposable
{
    private const string EntryName = "KeitaToolbox-VerificationMonitor";
    private const ushort HealthyColor = 504;
    private const ushort WarningColor = 506;
    private const ushort ExpiredColor = 518;
    private const ushort UnknownColor = 3;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WarningThreshold = TimeSpan.FromDays(2);

    private static readonly VerificationTarget[] Targets =
    [
        new(
            "Daily Routines",
            "DailyRoutines",
            ReaderKind.DailyRoutines,
            "OmenBot",
            "discord://-/channels/@me/1296513137297854554",
            "https://discord.com/channels/@me/1296513137297854554"),
        new(
            "MissFisher",
            "MissFisher",
            ReaderKind.NekoVerifier,
            "认证频道",
            "discord://-/channels/1416249742798618666/1418058143191273692",
            "https://discord.com/channels/1416249742798618666/1418058143191273692"),
        new(
            "CharaCradThief",
            "CharaCradThief",
            ReaderKind.NekoVerifier,
            "认证频道",
            "discord://-/channels/1090192543486054420/1297517728671993928",
            "https://discord.com/channels/1090192543486054420/1297517728671993928"),
        new(
            "PvPAuto",
            "pvpauto",
            ReaderKind.NekoVerifier,
            "认证频道",
            "discord://-/channels/1090192543486054420/1276021202636374089",
            "https://discord.com/channels/1090192543486054420/1276021202636374089"),
        new(
            "NyaDraw",
            "NyaDraw",
            ReaderKind.NekoVerifier,
            "NekoBot",
            "discord://-/channels/@me/1538405885032927263",
            "https://discord.com/channels/@me/1538405885032927263"),
        new(
            "Kodakku Assist",
            "KodakkuAssist",
            ReaderKind.NekoVerifier,
            "Kodakku_Bot",
            "discord://-/users/1294534980499804202",
            "https://discord.com/users/1294534980499804202"),
        new(
            "OmniToolbox",
            "OmniToolbox",
            ReaderKind.OmniToolbox,
            "激活频道",
            "discord://-/channels/1456729574330077206/1530517955106832414",
            "https://discord.com/channels/1456729574330077206/1530517955106832414"),
    ];

    private readonly IDtrBarEntry entry;
    private readonly HashSet<string> loggedErrors = new(StringComparer.Ordinal);
    private IReadOnlyList<VerificationState> states = [];
    private DateTimeOffset nextRefreshAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastRefreshAt = DateTimeOffset.MinValue;
    private bool discordWindowOpen;

    public VerificationMonitorFeature()
    {
        entry = Plugin.DtrBar.Get(EntryName);
        entry.OnClick = _ => OpenDiscordWindow();
        Plugin.PluginInterface.UiBuilder.Draw += DrawDiscordWindow;
        Refresh();
    }

    public void Dispose()
    {
        Plugin.PluginInterface.UiBuilder.Draw -= DrawDiscordWindow;
        entry.Remove();
    }

    public void Update()
    {
        ApplyEntryVisibility();
        if (!Plugin.Config.VerificationMonitor.Enabled || DateTimeOffset.Now < nextRefreshAt)
            return;

        Refresh();
    }

    public void DrawSettings()
    {
        var settings = Plugin.Config.VerificationMonitor;
        if (Plugin.DrawFeatureToggle(
                "插件验证监控",
                settings.Enabled,
                value => settings.Enabled = value))
        {
            ApplyEntryVisibility();
            if (settings.Enabled)
                Refresh();
        }

        Plugin.DrawHelp(
            "统一读取已加载插件的验证到期时间，不提交验证码或执行自动验证。每 30 秒刷新一次；到期前 2 天只提醒一次，同一到期时间不会重复提醒。\n" +
            "绿：正常　黄：2 天内到期　红：已过期　灰：状态不完整。\n" +
            "点击服务器信息栏状态点可打开各插件的 Discord 认证入口。");

        DrawCheckbox(
            "在服务器信息栏显示状态点",
            settings.ShowServerInfoBar,
            value => settings.ShowServerInfoBar = value,
            ApplyEntryVisibility);

        ImGui.Spacing();
        ImGui.TextUnformatted("提醒方式");
        DrawCheckbox(
            "Dalamud 通知中心",
            settings.NotifyWithDalamud,
            value => settings.NotifyWithDalamud = value);
        DrawCheckbox(
            "游戏原生 Toast",
            settings.NotifyWithGameToast,
            value => settings.NotifyWithGameToast = value);
        DrawCheckbox(
            "聊天栏本地提示",
            settings.NotifyWithChat,
            value => settings.NotifyWithChat = value);

        ImGui.Spacing();
        if (ImGui.Button("立即刷新"))
            Refresh();
        ImGui.SameLine();
        if (ImGui.Button("打开验证入口"))
            OpenDiscordWindow();
        if (lastRefreshAt != DateTimeOffset.MinValue)
        {
            ImGui.SameLine();
            Plugin.DrawDisabledWrapped($"上次刷新：{lastRefreshAt:HH:mm:ss}");
        }

        ImGui.Separator();
        foreach (var state in states.Where(state => IsTargetInstalled(state.Target)))
            DrawState(state);
    }

    private void OpenDiscordWindow()
    {
        Refresh();
        discordWindowOpen = true;
    }

    private void DrawDiscordWindow()
    {
        if (!discordWindowOpen)
            return;

        ImGui.SetNextWindowSize(
            new System.Numerics.Vector2(560f, 0f),
            ImGuiCond.Appearing);
        if (!ImGui.Begin(
                "插件验证入口##KeitaToolboxVerificationDiscord",
                ref discordWindowOpen,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        Plugin.DrawWrapped(
            "第一列打开插件设置；右侧 Discord 按钮会优先唤起客户端，客户端不可用时改用浏览器打开。");
        ImGui.Separator();

        var installedTargets = Targets
            .Where(IsTargetInstalled)
            .ToArray();
        var orderedTargets = states
            .Where(state => installedTargets.Contains(state.Target))
            .Select(state => state.Target)
            .Concat(installedTargets.Where(target => states.All(state => state.Target != target)));
        foreach (var target in orderedTargets)
        {
            ImGui.PushID(target.InternalName);
            var installedPlugin = Plugin.PluginInterface.InstalledPlugins.FirstOrDefault(
                plugin => plugin.InternalName.Equals(
                    target.InternalName,
                    StringComparison.OrdinalIgnoreCase));
            var canOpenSettings = installedPlugin is { IsLoaded: true } &&
                                  (installedPlugin.HasConfigUi || installedPlugin.HasMainUi);
            if (!canOpenSettings)
                ImGui.BeginDisabled();
            if (ImGui.Button(target.DisplayName) && installedPlugin != null)
                OpenPluginSettings(installedPlugin);
            if (!canOpenSettings)
                ImGui.EndDisabled();

            ImGui.SameLine(350f);
            if (target.DiscordName != null &&
                target.DiscordUri != null &&
                target.WebUrl != null)
            {
                if (ImGui.Button($"打开 {target.DiscordName}"))
                    OpenDiscordTarget(target, target.DiscordUri, target.WebUrl);
            }
            else
            {
                Plugin.DrawDisabledWrapped("请在插件设置内验证");
            }

            var state = states.FirstOrDefault(current => current.Target == target);
            if (state == null)
            {
                Plugin.DrawDisabledWrapped("尚未刷新");
            }
            else
            {
                Plugin.DrawColoredWrapped(
                    GetStatusColor(state),
                    FormatState(state, true));
            }

            ImGui.Separator();
            ImGui.PopID();
        }

        ImGui.End();
    }

    private static void OpenPluginSettings(IExposedPlugin plugin)
    {
        try
        {
            if (plugin.HasConfigUi)
                plugin.OpenConfigUi();
            else if (plugin.HasMainUi)
                plugin.OpenMainUi();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(
                ex,
                "Failed to open settings for {Plugin}.",
                plugin.InternalName);
        }
    }

    private static void OpenDiscordTarget(
        VerificationTarget target,
        string discordUri,
        string webUrl)
    {
        try
        {
            Process.Start(new ProcessStartInfo(discordUri)
            {
                UseShellExecute = true,
                Verb = "open",
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(
                ex,
                "Failed to open Discord client for {Plugin}; using the web link.",
                target.InternalName);
            Util.OpenLink(webUrl);
        }
    }

    private static void DrawCheckbox(
        string label,
        bool value,
        Action<bool> setter,
        Action? afterSave = null)
    {
        var changedValue = value;
        if (!ImGui.Checkbox(label, ref changedValue))
            return;

        setter(changedValue);
        Plugin.Config.Save();
        afterSave?.Invoke();
    }

    private static void DrawState(VerificationState state)
    {
        Plugin.DrawColoredWrapped(
            GetStatusColor(state),
            $"{state.Target.DisplayName,-16} {FormatState(state, true)}");
    }

    private static System.Numerics.Vector4 GetStatusColor(VerificationState state) =>
        state.Status switch
        {
            VerificationStatus.Known or VerificationStatus.Cached when
                state.ExpiresAt <= DateTimeOffset.Now =>
                new System.Numerics.Vector4(1f, 0.3f, 0.3f, 1f),
            VerificationStatus.Known or VerificationStatus.Cached when
                state.ExpiresAt - DateTimeOffset.Now <= WarningThreshold =>
                new System.Numerics.Vector4(1f, 0.72f, 0.2f, 1f),
            VerificationStatus.Known or
                VerificationStatus.Cached or
                VerificationStatus.ValidWithoutExpiry or
                VerificationStatus.Indefinite =>
                new System.Numerics.Vector4(0.45f, 0.9f, 0.55f, 1f),
            _ => new System.Numerics.Vector4(0.65f, 0.65f, 0.65f, 1f),
        };

    private void Refresh()
    {
        var now = DateTimeOffset.Now;
        nextRefreshAt = now + RefreshInterval;
        lastRefreshAt = now;

        if (!Plugin.Config.VerificationMonitor.Enabled)
        {
            states = [];
            ApplyEntryVisibility();
            return;
        }

        var refreshedStates = Targets
            .Where(IsTargetInstalled)
            .Select(ReadTarget)
            .ToArray();
        CacheKnownExpiries(refreshedStates);
        states = refreshedStates
            .OrderBy(GetStateSortGroup)
            .ThenBy(state => state.ExpiresAt ?? DateTimeOffset.MaxValue)
            .ThenBy(state => Array.IndexOf(Targets, state.Target))
            .ToArray();
        UpdateEntry(now);
        SendDueReminder(now);
    }

    private static int GetStateSortGroup(VerificationState state) => state.Status switch
    {
        VerificationStatus.Known or VerificationStatus.Cached => 0,
        VerificationStatus.ValidWithoutExpiry => 1,
        VerificationStatus.Indefinite => 2,
        _ => 3,
    };

    private VerificationState ReadTarget(VerificationTarget target)
    {
        var exposedPlugin = Plugin.PluginInterface.InstalledPlugins.FirstOrDefault(
            plugin => plugin.InternalName.Equals(
                target.InternalName,
                StringComparison.OrdinalIgnoreCase));
        if (exposedPlugin == null)
            return new VerificationState(target, VerificationStatus.NotInstalled);
        if (!exposedPlugin.IsLoaded)
            return new VerificationState(target, VerificationStatus.NotLoaded);

        try
        {
            var assemblyCandidates = AppDomain.CurrentDomain.GetAssemblies()
                .Where(candidate => candidate.GetName().Name?.Equals(
                    target.InternalName,
                    StringComparison.OrdinalIgnoreCase) == true)
                .OrderByDescending(candidate => candidate.GetName().Version)
                .ToArray();
            var assembly = assemblyCandidates.FirstOrDefault(candidate =>
                               GetPluginInstance(exposedPlugin, candidate) != null) ??
                           assemblyCandidates.FirstOrDefault();
            if (assembly == null)
                return GetCachedOrUnavailable(target, VerificationStatus.Unavailable);

            var result = target.Reader switch
            {
                ReaderKind.DailyRoutines => ReadDailyRoutines(assembly),
                ReaderKind.NekoVerifier => ReadNekoVerifier(assembly),
                ReaderKind.OmniToolbox => ReadOmniToolbox(assembly, exposedPlugin),
                _ => null,
            };

            if (result == null)
                return GetCachedOrUnavailable(target, VerificationStatus.Unavailable);
            if (result.IsServiceUnavailable)
                return GetCachedOrUnavailable(target, VerificationStatus.ServiceUnavailable);
            if (result.IsIndefinite)
                return new VerificationState(target, VerificationStatus.Indefinite);
            if (result.IsValidWithoutExpiry)
                return new VerificationState(target, VerificationStatus.ValidWithoutExpiry);
            if (result.ExpiresAt == null)
                return GetCachedOrUnavailable(target, VerificationStatus.Unavailable);

            return new VerificationState(
                target,
                VerificationStatus.Known,
                result.ExpiresAt.Value.ToLocalTime());
        }
        catch (Exception ex)
        {
            var errorKey = $"{target.InternalName}:{ex.GetType().FullName}:{ex.Message}";
            if (loggedErrors.Add(errorKey))
            {
                Plugin.Log.Warning(
                    ex,
                    "Failed to read verification expiry for {Plugin}.",
                    target.InternalName);
            }

            return GetCachedOrUnavailable(target, VerificationStatus.Unavailable);
        }
    }

    private static VerificationState GetCachedOrUnavailable(
        VerificationTarget target,
        VerificationStatus unavailableStatus)
    {
        var cache = Plugin.Config.VerificationMonitor.LastKnownExpiryUnixSeconds;
        if (!cache.TryGetValue(target.InternalName, out var unixSeconds))
            return new VerificationState(target, unavailableStatus);

        try
        {
            return new VerificationState(
                target,
                VerificationStatus.Cached,
                DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime());
        }
        catch (ArgumentOutOfRangeException)
        {
            return new VerificationState(target, unavailableStatus);
        }
    }

    private static void CacheKnownExpiries(IEnumerable<VerificationState> refreshedStates)
    {
        var cache = Plugin.Config.VerificationMonitor.LastKnownExpiryUnixSeconds;
        var changed = false;
        foreach (var state in refreshedStates.Where(state =>
                     state.Status == VerificationStatus.Known && state.ExpiresAt != null))
        {
            var unixSeconds = state.ExpiresAt!.Value.ToUnixTimeSeconds();
            if (cache.TryGetValue(state.Target.InternalName, out var cached) &&
                cached == unixSeconds)
            {
                continue;
            }

            cache[state.Target.InternalName] = unixSeconds;
            changed = true;
        }

        if (changed)
            Plugin.Config.Save();
    }

    private static ExpiryReadResult? ReadDailyRoutines(Assembly assembly)
    {
        var types = SafeGetTypes(assembly);
        var credentialType = types.FirstOrDefault(type =>
            type.GetProperty(
                "ExpiredTime",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.PropertyType == typeof(DateTime?) &&
            type.GetProperty(
                "IsActivated",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.PropertyType == typeof(bool) &&
            type.GetProperty(
                "Token",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.PropertyType == typeof(string));
        if (credentialType == null)
            return ReadDailyAuthState(assembly);

        var managerType = types.FirstOrDefault(type => GetAllFields(type).Any(
                              field => field.Name == "credentialInstances")) ??
                          types.FirstOrDefault(type => GetAllFields(type).Any(
                              field => IsDictionaryOf(field.FieldType, credentialType)));
        if (managerType == null)
            return ReadDailyAuthState(assembly);

        var manager = GetDailyManager(managerType) ?? GetSingleton(managerType);
        if (manager == null)
            return ReadDailyAuthState(assembly);

        var credentialField = GetAllFields(managerType).FirstOrDefault(
                                  field => field.Name == "credentialInstances") ??
                              GetAllFields(managerType).FirstOrDefault(
                                  field => IsDictionaryOf(field.FieldType, credentialType));
        var credentialDictionary = credentialField?.GetValue(manager);
        var values = credentialDictionary?.GetType().GetProperty("Values")
            ?.GetValue(credentialDictionary) as IEnumerable;
        if (values == null)
            return ReadDailyAuthState(assembly);

        var expiryProperty = credentialType.GetProperty(
            "ExpiredTime",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var expiries = new List<DateTimeOffset>();
        foreach (var credential in values)
        {
            if (credential != null && expiryProperty?.GetValue(credential) is DateTime expiry)
                expiries.Add(ToDateTimeOffset(expiry));
        }

        if (expiries.Count > 0)
            return new ExpiryReadResult(expiries.Min());

        return ReadDailyAuthState(assembly);
    }

    private static object? GetDailyManager(Type managerType)
    {
        var hostType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(
                "DailyRoutines.Common.Runtime.Hosts.ManagerHost",
                false))
            .FirstOrDefault(type => type != null);
        var host = hostType?.GetProperty(
                "Current",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null);
        if (host == null)
            return null;

        var getMethod = host.GetType().GetInterfaces()
            .SelectMany(type => type.GetMethods())
            .FirstOrDefault(method =>
                method.Name == "Get" &&
                method.IsGenericMethodDefinition &&
                method.GetParameters().Length == 0);
        return getMethod?.MakeGenericMethod(managerType).Invoke(host, null);
    }

    private static ExpiryReadResult? ReadDailyAuthState(Assembly assembly)
    {
        var authState = assembly.GetType("DailyRoutines.Verification.AuthState");
        var isAuthenticated = authState?.GetProperty(
                "IsAuth",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null) as bool?;
        if (isAuthenticated == true)
            return new ExpiryReadResult(null, IsValidWithoutExpiry: true);

        var isConnected = authState?.GetProperty(
                "IsConnected",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null) as bool?;
        return isConnected == false
            ? new ExpiryReadResult(null, IsServiceUnavailable: true)
            : null;
    }

    private static ExpiryReadResult? ReadNekoVerifier(Assembly assembly)
    {
        var candidates = new List<NekoCandidate>();
        foreach (var type in SafeGetTypes(assembly))
        {
            var fields = type.GetFields(
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .Where(field => field.FieldType == typeof(DateTime))
                .ToArray();
            if (fields.Length is < 3 or > 6)
                continue;

            var score = 100 - Math.Abs(fields.Length - 4) * 5;
            if (type.IsAbstract && type.IsSealed)
                score += 10;
            if (type.GetFields(
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .Any(field => field.FieldType == typeof(ushort)))
            {
                score += 10;
            }

            candidates.Add(new NekoCandidate(score, fields));
        }

        var best = candidates
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault();
        if (best == null)
            return null;

        var expiries = best.Fields
            .Select(TryGetStaticDateTime)
            .OfType<DateTime>()
            .Where(value => value.Year >= 2000)
            .Select(ToDateTimeOffset)
            .ToArray();
        if (expiries.Length == 0)
            return null;

        var expiry = expiries.Max();
        return expiry.Year >= DateTimeOffset.Now.Year + 20
            ? new ExpiryReadResult(null, true)
            : new ExpiryReadResult(expiry);
    }

    private static ExpiryReadResult? ReadOmniToolbox(
        Assembly assembly,
        object exposedPlugin)
    {
        var pluginInstance = GetPluginInstance(exposedPlugin, assembly);
        if (pluginInstance == null)
            return null;

        var verifier = FindInObjectGraph(
            pluginInstance,
            value => value.GetType().GetInterfaces().Any(
                type => type.FullName == "OmniToolbox.PublicBridge.IPrivateVerifier"),
            "OmniToolbox");
        if (verifier == null)
            return null;

        var currentProperty = verifier.GetType().GetProperties(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(property =>
                property.Name == "Current" || property.Name.EndsWith(".Current"));
        var snapshot = currentProperty?.GetValue(verifier);
        if (snapshot == null)
            return null;

        var snapshotType = snapshot.GetType();
        var displayMethod = snapshotType.GetMethod(
            "GetDisplayExpiresAt",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            [typeof(DateTimeOffset)],
            null);
        if (displayMethod?.Invoke(snapshot, [DateTimeOffset.UtcNow]) is DateTimeOffset displayExpiry)
            return new ExpiryReadResult(displayExpiry);

        var fallbackExpiry = new[]
            {
                "ExpiresAt",
                "TestExpiresAt",
                "TemporaryExpiresAt",
            }
            .Select(name => snapshotType.GetProperty(
                    name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(snapshot))
            .OfType<DateTimeOffset>()
            .DefaultIfEmpty()
            .Max();
        if (fallbackExpiry != default)
            return new ExpiryReadResult(fallbackExpiry);

        var isAuthorized = snapshotType.GetProperty(
                "IsAuthorized",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(snapshot) as bool?;
        return isAuthorized == true ? new ExpiryReadResult(null, true) : null;
    }

    private void UpdateEntry(DateTimeOffset now)
    {
        var visibleStates = states
            .Where(state => IsTargetInstalled(state.Target))
            .ToArray();
        ApplyEntryVisibility();
        if (!entry.Shown)
            return;

        var hasExpired = visibleStates.Any(state =>
            (state.Status is VerificationStatus.Known or VerificationStatus.Cached) &&
            state.ExpiresAt <= now);
        var hasWarning = visibleStates.Any(state =>
            (state.Status is VerificationStatus.Known or VerificationStatus.Cached) &&
            state.ExpiresAt > now &&
            state.ExpiresAt - now <= WarningThreshold);
        var allHealthy = visibleStates.Length > 0 && visibleStates.All(state =>
            state.Status is VerificationStatus.ValidWithoutExpiry or
                VerificationStatus.Indefinite ||
            (state.Status is VerificationStatus.Known or VerificationStatus.Cached) &&
            state.ExpiresAt - now > WarningThreshold);
        var color = hasExpired
            ? ExpiredColor
            : hasWarning
                ? WarningColor
                : allHealthy
                    ? HealthyColor
                    : UnknownColor;

        entry.Text = new SeStringBuilder()
            .AddUiForeground("●", color)
            .BuiltString;
        entry.Tooltip = string.Join(
            '\n',
            new[]
            {
                "插件验证剩余时间",
            }.Concat(
                visibleStates.Select(state => $"{state.Target.DisplayName}: {FormatState(state, false)}")));
    }

    private void ApplyEntryVisibility()
    {
        var settings = Plugin.Config.VerificationMonitor;
        entry.Shown = settings.Enabled &&
                      settings.ShowServerInfoBar &&
                      states.Any(state => IsTargetInstalled(state.Target));
    }

    private static bool IsTargetInstalled(VerificationTarget target) =>
        Plugin.PluginInterface.InstalledPlugins.Any(plugin =>
            plugin.InternalName.Equals(
                target.InternalName,
                StringComparison.OrdinalIgnoreCase));

    private void SendDueReminder(DateTimeOffset now)
    {
        if (!Plugin.ClientState.IsLoggedIn)
            return;

        var settings = Plugin.Config.VerificationMonitor;
        if (!settings.NotifyWithDalamud &&
            !settings.NotifyWithGameToast &&
            !settings.NotifyWithChat)
        {
            return;
        }

        var due = states
            .Where(state => IsTargetInstalled(state.Target))
            .Where(state =>
                (state.Status is VerificationStatus.Known or VerificationStatus.Cached) &&
                state.ExpiresAt != null &&
                state.ExpiresAt.Value - now <= WarningThreshold)
            .Where(state =>
            {
                var expiryKey = state.ExpiresAt!.Value.ToUnixTimeSeconds();
                return !settings.LastNotifiedExpiryUnixSeconds.TryGetValue(
                           state.Target.InternalName,
                           out var notifiedExpiry) ||
                       notifiedExpiry != expiryKey;
            })
            .ToArray();
        if (due.Length == 0)
            return;

        var details = string.Join(
            "；",
            due.Select(state =>
            {
                var remaining = state.ExpiresAt!.Value - now;
                return remaining <= TimeSpan.Zero
                    ? $"{state.Target.DisplayName} 已过期"
                    : $"{state.Target.DisplayName} 剩余 {FormatRemaining(remaining)}";
            }));
        var message = $"插件验证提醒：{details}";

        if (settings.NotifyWithDalamud)
        {
            Plugin.Notifications.AddNotification(new Notification
            {
                Title = "Keita 工具箱",
                Content = message,
                Type = NotificationType.Warning,
            });
        }

        if (settings.NotifyWithGameToast)
            Plugin.Toasts.ShowNormal(message);
        if (settings.NotifyWithChat)
            Plugin.Chat.Print($"[Keita 工具箱] {message}");

        foreach (var state in due)
        {
            settings.LastNotifiedExpiryUnixSeconds[state.Target.InternalName] =
                state.ExpiresAt!.Value.ToUnixTimeSeconds();
        }

        Plugin.Config.Save();
    }

    private static string FormatState(VerificationState state, bool includeDate)
    {
        return state.Status switch
        {
            VerificationStatus.NotInstalled => "未安装",
            VerificationStatus.NotLoaded => "未加载",
            VerificationStatus.Unavailable => "暂时无法读取",
            VerificationStatus.ServiceUnavailable => "认证节点不可用",
            VerificationStatus.ValidWithoutExpiry => "已验证（到期时间不可用）",
            VerificationStatus.Indefinite => "长期有效",
            VerificationStatus.Cached when state.ExpiresAt <= DateTimeOffset.Now =>
                includeDate
                    ? $"缓存：已过期 · {state.ExpiresAt:yyyy-MM-dd HH:mm}"
                    : $"缓存：已过期（{state.ExpiresAt:yyyy-MM-dd HH:mm}）",
            VerificationStatus.Cached => includeDate
                ? $"缓存：{FormatRemaining(state.ExpiresAt!.Value - DateTimeOffset.Now)} · {state.ExpiresAt:yyyy-MM-dd HH:mm}"
                : $"缓存：{FormatRemaining(state.ExpiresAt!.Value - DateTimeOffset.Now)}（{state.ExpiresAt:yyyy-MM-dd HH:mm}）",
            VerificationStatus.Known when state.ExpiresAt <= DateTimeOffset.Now =>
                includeDate
                    ? $"已过期 · {state.ExpiresAt:yyyy-MM-dd HH:mm}"
                    : $"已过期（{state.ExpiresAt:yyyy-MM-dd HH:mm}）",
            VerificationStatus.Known => includeDate
                ? $"{FormatRemaining(state.ExpiresAt!.Value - DateTimeOffset.Now)} · {state.ExpiresAt:yyyy-MM-dd HH:mm}"
                : $"{FormatRemaining(state.ExpiresAt!.Value - DateTimeOffset.Now)}（{state.ExpiresAt:yyyy-MM-dd HH:mm}）",
            _ => "未知",
        };
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
            return "已过期";
        if (remaining.TotalDays >= 1)
            return $"{(int)remaining.TotalDays}天{remaining.Hours}小时";
        if (remaining.TotalHours >= 1)
            return $"{(int)remaining.TotalHours}小时{remaining.Minutes}分";
        return $"{Math.Max(1, remaining.Minutes)}分";
    }

    private static bool IsDictionaryOf(Type type, Type valueType) =>
        type.IsGenericType &&
        type.GetGenericTypeDefinition() == typeof(Dictionary<,>) &&
        valueType.IsAssignableFrom(type.GetGenericArguments()[1]);

    private static object? GetSingleton(Type type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var instanceMethod = current.GetMethods(
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .FirstOrDefault(method =>
                    method.Name == "Instance" &&
                    method.GetParameters().Length == 0);
            if (instanceMethod?.Invoke(null, null) is { } methodInstance &&
                type.IsInstanceOfType(methodInstance))
            {
                return methodInstance;
            }

            var instanceProperty = current.GetProperties(
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .FirstOrDefault(property =>
                    property.Name == "Instance" &&
                    type.IsAssignableFrom(property.PropertyType));
            if (instanceProperty?.GetValue(null) is { } propertyInstance)
                return propertyInstance;

            var instanceField = current.GetFields(
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .FirstOrDefault(field => type.IsAssignableFrom(field.FieldType));
            if (instanceField?.GetValue(null) is { } fieldInstance)
                return fieldInstance;
        }

        return null;
    }

    private static object? GetPluginInstance(object exposedPlugin, Assembly targetAssembly)
    {
        var localPlugin = GetAllFields(exposedPlugin.GetType())
            .Select(field => TryGetValue(field, exposedPlugin))
            .FirstOrDefault(value =>
                value?.GetType().FullName?.StartsWith(
                    "Dalamud.Plugin.Internal.Types.LocalPlugin",
                    StringComparison.Ordinal) == true);
        if (localPlugin == null)
            return null;

        return GetAllFields(localPlugin.GetType())
            .Where(field => field.Name == "instance")
            .Select(field => TryGetValue(field, localPlugin))
            .FirstOrDefault(value => value?.GetType().Assembly == targetAssembly);
    }

    private static object? FindInObjectGraph(
        object root,
        Func<object, bool> match,
        string assemblyPrefix)
    {
        var queue = new Queue<(object Value, int Depth)>();
        var visited = new HashSet<object>(ReferenceComparer.Instance);
        queue.Enqueue((root, 0));

        while (queue.Count > 0 && visited.Count < 2048)
        {
            var (value, depth) = queue.Dequeue();
            if (!visited.Add(value))
                continue;
            if (match(value))
                return value;
            if (depth >= 10)
                continue;

            if (value is Delegate callback)
            {
                foreach (var invocation in callback.GetInvocationList())
                {
                    if (invocation.Target != null)
                        queue.Enqueue((invocation.Target, depth + 1));
                }
            }

            if (value is IEnumerable enumerable and not string)
            {
                var count = 0;
                foreach (var item in enumerable)
                {
                    if (item != null)
                        queue.Enqueue((item, depth + 1));
                    if (++count >= 256)
                        break;
                }
            }

            var valueAssembly = value.GetType().Assembly.GetName().Name ?? string.Empty;
            if (!valueAssembly.StartsWith(assemblyPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var field in GetAllFields(value.GetType()))
            {
                var child = TryGetValue(field, value);
                if (child == null || child is string || child.GetType().IsPrimitive)
                    continue;

                var childAssembly = child.GetType().Assembly.GetName().Name ?? string.Empty;
                if (child is Delegate ||
                    child is IEnumerable ||
                    childAssembly.StartsWith(assemblyPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }

        return null;
    }

    private static object? TryGetValue(FieldInfo field, object owner)
    {
        try
        {
            return field.GetValue(owner);
        }
        catch
        {
            return null;
        }
    }

    private static DateTime? TryGetStaticDateTime(FieldInfo field)
    {
        try
        {
            return field.GetValue(null) as DateTime?;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<FieldInfo> GetAllFields(Type type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var field in current.GetFields(
                         BindingFlags.Instance |
                         BindingFlags.Public |
                         BindingFlags.NonPublic |
                         BindingFlags.DeclaredOnly))
            {
                yield return field;
            }
        }
    }

    private static Type[] SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>().ToArray();
        }
    }

    private static DateTimeOffset ToDateTimeOffset(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => new DateTimeOffset(value),
        DateTimeKind.Local => new DateTimeOffset(value),
        _ => new DateTimeOffset(value, TimeZoneInfo.Local.GetUtcOffset(value)),
    };

    private enum ReaderKind
    {
        DailyRoutines,
        NekoVerifier,
        OmniToolbox,
    }

    private enum VerificationStatus
    {
        NotInstalled,
        NotLoaded,
        Unavailable,
        ServiceUnavailable,
        ValidWithoutExpiry,
        Known,
        Cached,
        Indefinite,
    }

    private sealed record VerificationTarget(
        string DisplayName,
        string InternalName,
        ReaderKind Reader,
        string? DiscordName,
        string? DiscordUri,
        string? WebUrl);

    private sealed record VerificationState(
        VerificationTarget Target,
        VerificationStatus Status,
        DateTimeOffset? ExpiresAt = null);

    private sealed record ExpiryReadResult(
        DateTimeOffset? ExpiresAt,
        bool IsIndefinite = false,
        bool IsValidWithoutExpiry = false,
        bool IsServiceUnavailable = false);

    private sealed record NekoCandidate(
        int Score,
        IReadOnlyList<FieldInfo> Fields);

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static ReferenceComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object value) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
    }
}
