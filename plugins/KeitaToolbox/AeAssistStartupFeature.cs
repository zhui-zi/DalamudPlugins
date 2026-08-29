using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace KeitaToolbox;

internal sealed class AeAssistStartupFeature : IDisposable
{
    private const string LoaderInternalName = "AEAssistV3";
    private const long UpdateIntervalMs = 250;
    private const long LoaderTimeoutMs = 60_000;
    private const long CheckTimeoutMs = 90_000;
    private const long UpdateTimeoutMs = 180_000;
    private const long LoadTimeoutMs = 30_000;
    private const long LoginTimeoutMs = 10 * 60_000;
    private const long VerifyTimeoutMs = 45_000;
    private const long VerifyRetryDelayMs = 10_000;

    private static readonly Dictionary<short, OpCode> OpCodesByValue =
        typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opCode => opCode.Value);

    private object? loader;
    private Type? loaderType;
    private FieldInfo? pendingFilesField;
    private IReadOnlyList<FieldInfo> loaderBooleanFields = [];
    private MethodInfo? checkMethod;
    private MethodInfo? updateMethod;
    private MethodInfo? loadMethod;
    private object? aeAssistPlugin;
    private object? verifier;
    private PropertyInfo? verifiedProperty;
    private FieldInfo? requestingField;
    private FieldInfo? activeKeyField;
    private MethodInfo? submitVerificationMethod;
    private List<VerificationCandidate> verificationCandidates = [];
    private int verificationCandidateIndex;
    private bool enableCommandSent;
    private bool checkRetrySent;
    private bool updateRequested;
    private bool completionReported;
    private long nextUpdateAt;
    private long phaseStartedAt;
    private long phaseDeadline;
    private StartupState state;
    private string status = string.Empty;

    public AeAssistStartupFeature()
    {
        Plugin.Framework.Update += OnUpdate;
        Restart();
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
    }

    public void DrawSettings()
    {
        if (!ImGui.CollapsingHeader("AEAssist 启动自动化"))
            return;

        var enabled = Plugin.Config.AeAssistStartup.Enabled;
        if (ImGui.Checkbox("游戏启动后自动更新、加载并验证 AEAssist", ref enabled))
        {
            Plugin.Config.AeAssistStartup.Enabled = enabled;
            Plugin.Config.Save();
            Restart();
        }

        var printChat = Plugin.Config.AeAssistStartup.PrintChatMessage;
        if (ImGui.Checkbox("在聊天栏显示最终结果", ref printChat))
        {
            Plugin.Config.AeAssistStartup.PrintChatMessage = printChat;
            Plugin.Config.Save();
        }

        Plugin.DrawWrapped($"当前状态：{status}");
        if (ImGui.Button("立即重试"))
            Restart();

        Plugin.DrawHelp(
            "依次使用 AEAssist Loader 自带的检查更新、下载更新和加载入口。"
            + "登录角色后读取 AEAssist 自己保存的码，高级码优先，失败时回退到其他已保存码；"
            + "工具箱不会保存或输出验证码。");
    }

    private void OnUpdate(IFramework _)
    {
        if (!Plugin.Config.AeAssistStartup.Enabled)
        {
            if (state != StartupState.Disabled)
            {
                state = StartupState.Disabled;
                status = "已禁用";
            }
            return;
        }

        var now = Environment.TickCount64;
        if (now < nextUpdateAt)
            return;
        nextUpdateAt = now + UpdateIntervalMs;

        try
        {
            switch (state)
            {
                case StartupState.WaitingForLoader:
                    UpdateWaitingForLoader(now);
                    break;
                case StartupState.Checking:
                    UpdateChecking(now);
                    break;
                case StartupState.Updating:
                    UpdateUpdating(now);
                    break;
                case StartupState.Loading:
                    UpdateLoading(now);
                    break;
                case StartupState.WaitingForLogin:
                    UpdateWaitingForLogin(now);
                    break;
                case StartupState.Verifying:
                    UpdateVerifying(now);
                    break;
            }
        }
        catch (Exception ex)
        {
            Fail("AEAssist 启动自动化发生异常", ex);
        }
    }

    private void UpdateWaitingForLoader(long now)
    {
        if (TryGetDalamudPlugin(LoaderInternalName, out var instance))
        {
            InitializeLoader(instance);
            if (TryFindAeAssistPlugin())
            {
                EnterWaitingForLogin();
                return;
            }

            SetState(StartupState.Checking, "正在检查 AEAssist 更新…", CheckTimeoutMs);
            return;
        }

        var installed = Plugin.PluginInterface.InstalledPlugins.FirstOrDefault(
            item => item.InternalName.Equals(LoaderInternalName, StringComparison.OrdinalIgnoreCase));
        if (installed != null && !installed.IsLoaded && !enableCommandSent)
        {
            enableCommandSent = true;
            Plugin.CommandManager.ProcessCommand($"/xlenableplugin {LoaderInternalName}");
            status = "正在启用 AEAssist Loader…";
        }

        if (now >= phaseDeadline)
            Fail("未找到已加载的 AEAssist Loader（AEAssistV3）");
    }

    private void UpdateChecking(long now)
    {
        if (TryFindAeAssistPlugin())
        {
            EnterWaitingForLogin();
            return;
        }

        var pendingCount = GetPendingFileCount();
        if (pendingCount > 0)
        {
            InvokeLoaderMethod(updateMethod, "更新");
            updateRequested = true;
            SetState(
                StartupState.Updating,
                $"发现 {pendingCount} 个待更新文件，正在调用 AEAssist 内置更新…",
                UpdateTimeoutMs);
            return;
        }

        if (IsLoaderReady())
        {
            BeginLoading();
            return;
        }

        if (!checkRetrySent && now - phaseStartedAt >= 15_000)
        {
            checkRetrySent = true;
            InvokeLoaderMethod(checkMethod, "检查更新");
            phaseDeadline = now + CheckTimeoutMs;
            status = "AEAssist 首次检查尚未返回，已重试内置检查更新…";
            return;
        }

        if (now >= phaseDeadline)
            Fail("AEAssist 内置更新检查超时");
    }

    private void UpdateUpdating(long now)
    {
        if (TryFindAeAssistPlugin())
        {
            EnterWaitingForLogin();
            return;
        }

        if (GetPendingFileCount() == 0 && IsLoaderReady())
        {
            BeginLoading();
            return;
        }

        if (now >= phaseDeadline)
            Fail("AEAssist 内置更新未能在限定时间内完成");
    }

    private void UpdateLoading(long now)
    {
        if (TryFindAeAssistPlugin())
        {
            EnterWaitingForLogin();
            return;
        }

        if (now >= phaseDeadline)
            Fail("AEAssist Loader 已执行加载，但未检测到 AEAssist 主体实例");
    }

    private void UpdateWaitingForLogin(long now)
    {
        if (!TryFindAeAssistPlugin())
        {
            Fail("AEAssist 主体实例在验证前消失");
            return;
        }

        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null)
        {
            if (now >= phaseDeadline)
                Fail("等待角色登录超时，未执行 AEAssist 验证");
            return;
        }

        if (!TryInitializeVerifier())
        {
            if (now >= phaseDeadline)
                Fail("无法定位 AEAssist 内置验证入口");
            return;
        }

        if (IsVerified())
        {
            Complete();
            return;
        }

        verificationCandidates = ReadVerificationCandidates();
        verificationCandidateIndex = 0;
        if (verificationCandidates.Count == 0)
        {
            Fail("AEAssist 没有已保存的通用码或高级码，请先在 AEAssist 中保存一次");
            return;
        }

        SubmitCurrentVerificationCandidate();
    }

    private void UpdateVerifying(long now)
    {
        if (IsVerified())
        {
            Complete();
            return;
        }

        var requesting = requestingField?.GetValue(verifier) as bool? ?? false;
        if (requesting && now < phaseDeadline)
            return;

        if (!requesting && now - phaseStartedAt >= VerifyRetryDelayMs)
        {
            verificationCandidateIndex++;
            if (verificationCandidateIndex < verificationCandidates.Count)
            {
                SubmitCurrentVerificationCandidate();
                return;
            }

            Fail("AEAssist 已保存的通用码和高级码均未通过验证");
            return;
        }

        if (now >= phaseDeadline)
            Fail("AEAssist 内置验证超时");
    }

    private void BeginLoading()
    {
        InvokeLoaderMethod(loadMethod, "加载");
        SetState(
            StartupState.Loading,
            updateRequested
                ? "更新完成，正在调用 AEAssist 内置加载…"
                : "已是最新版本，正在调用 AEAssist 内置加载…",
            LoadTimeoutMs);
    }

    private void EnterWaitingForLogin()
    {
        SetState(
            StartupState.WaitingForLogin,
            "AEAssist 已加载，等待角色登录后执行内置验证…",
            LoginTimeoutMs);
    }

    private void SubmitCurrentVerificationCandidate()
    {
        if (verifier == null ||
            activeKeyField == null ||
            submitVerificationMethod == null)
        {
            Fail("AEAssist 内置验证入口不完整");
            return;
        }

        var candidate = verificationCandidates[verificationCandidateIndex];
        activeKeyField.SetValue(verifier, candidate.Key);
        submitVerificationMethod.Invoke(verifier, null);
        SetState(
            StartupState.Verifying,
            candidate.Level > 0
                ? $"正在验证 AEAssist 高级码（候选 {verificationCandidateIndex + 1}/{verificationCandidates.Count}）…"
                : $"正在验证 AEAssist 通用码（候选 {verificationCandidateIndex + 1}/{verificationCandidates.Count}）…",
            VerifyTimeoutMs);
    }

    private void InitializeLoader(object instance)
    {
        loader = instance;
        loaderType = instance.GetType();
        var fields = GetInstanceFields(loaderType).ToArray();
        pendingFilesField = fields.FirstOrDefault(
            field => typeof(IDictionary).IsAssignableFrom(field.FieldType) &&
                     field.FieldType.IsGenericType &&
                     field.FieldType.GetGenericArguments() is [var keyType, var valueType] &&
                     keyType == typeof(string) &&
                     valueType == typeof(bool));
        loaderBooleanFields = fields
            .Where(field => field.FieldType == typeof(bool))
            .ToArray();
        checkMethod = FindAsyncStateMachineMethod(loaderType, "CheckUpdate");
        updateMethod = FindAsyncStateMachineMethod(loaderType, "UpdateAEAssist");
        loadMethod = FindLoaderLoadMethod(loaderType);

        if (pendingFilesField == null ||
            loaderBooleanFields.Count == 0 ||
            checkMethod == null ||
            updateMethod == null ||
            loadMethod == null)
        {
            throw new InvalidOperationException(
                "The installed AEAssist Loader does not expose the expected update and load workflow.");
        }
    }

    private bool TryFindAeAssistPlugin()
    {
        if (aeAssistPlugin?.GetType().FullName == "AEAssist.Plugin")
            return true;

        if (loader != null)
        {
            foreach (var field in GetInstanceFields(loader.GetType()))
            {
                var value = field.GetValue(loader);
                if (value?.GetType().FullName == "AEAssist.Plugin")
                {
                    aeAssistPlugin = value;
                    return true;
                }
            }
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
                     .Where(item => item.GetName().Name == "AEAssist"))
        {
            var type = assembly.GetType("AEAssist.Plugin", false);
            var instance = type?.GetField(
                    "P",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetValue(null);
            if (instance != null)
            {
                aeAssistPlugin = instance;
                return true;
            }
        }

        return false;
    }

    private bool TryInitializeVerifier()
    {
        if (verifier != null)
            return true;
        if (aeAssistPlugin == null)
            return false;

        var assembly = aeAssistPlugin.GetType().Assembly;
        var coreType = assembly.GetType("AEAssist.Core", false);
        var resolveMethod = coreType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(
                method => method.Name == "Resolve" &&
                          method.IsGenericMethodDefinition &&
                          method.GetParameters().Length == 0);
        var verifierType = GetLoadableTypes(assembly).FirstOrDefault(
            type => type.Namespace == "AEAssist.Verify" &&
                    type.GetProperty(
                        "Verified",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?.PropertyType == typeof(bool) &&
                    FindField(type, "ActiveKey")?.FieldType == typeof(string) &&
                    FindField(type, "Requesting")?.FieldType == typeof(bool));
        if (resolveMethod == null || verifierType == null)
            return false;

        verifier = resolveMethod.MakeGenericMethod(verifierType).Invoke(null, null);
        if (verifier == null)
            return false;

        verifiedProperty = verifierType.GetProperty(
            "Verified",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        requestingField = FindField(verifierType, "Requesting");
        activeKeyField = FindField(verifierType, "ActiveKey");
        submitVerificationMethod = verifierType
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(
                method => method.ReturnType == typeof(void) &&
                          method.GetParameters().Length == 0 &&
                          GetReferencedMembers(method).Any(
                              member => member.Name == "Start" &&
                                        member.DeclaringType?.Namespace
                                            ?.StartsWith(
                                                "AEAssist.Module.Network",
                                                StringComparison.Ordinal) == true));
        return verifiedProperty != null &&
               requestingField != null &&
               activeKeyField != null &&
               submitVerificationMethod != null;
    }

    private List<VerificationCandidate> ReadVerificationCandidates()
    {
        if (aeAssistPlugin == null)
            return [];

        var pluginType = aeAssistPlugin.GetType();
        var config = pluginType.GetField(
                "Config",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null);
        if (config == null)
            return [];

        var candidates = new Dictionary<string, VerificationCandidate>(
            StringComparer.Ordinal);
        var history = config.GetType().GetProperty(
                "PasswordHistory",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(config) as IEnumerable;
        if (history != null)
        {
            foreach (var item in history)
            {
                if (item == null)
                    continue;
                var key = item.GetType().GetProperty("Key")?.GetValue(item) as string;
                var levelValue = item.GetType().GetProperty("Level")?.GetValue(item);
                var level = levelValue == null ? -1 : Convert.ToInt32(levelValue);
                if (!string.IsNullOrWhiteSpace(key))
                    candidates[key] = new VerificationCandidate(key, level, false);
            }
        }

        var currentPassword = config.GetType().GetProperty(
                "Password",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(config) as string;
        if (!string.IsNullOrWhiteSpace(currentPassword))
        {
            if (candidates.TryGetValue(currentPassword, out var current))
                candidates[currentPassword] = current with { IsCurrent = true };
            else
                candidates[currentPassword] = new VerificationCandidate(
                    currentPassword,
                    -1,
                    true);
        }

        return candidates.Values
            .OrderByDescending(candidate => candidate.Level > 0)
            .ThenByDescending(candidate => candidate.IsCurrent)
            .ThenByDescending(candidate => candidate.Level)
            .ToList();
    }

    private int GetPendingFileCount() =>
        pendingFilesField?.GetValue(loader) is IDictionary pending ? pending.Count : 0;

    private bool IsLoaderReady() =>
        GetPendingFileCount() == 0 &&
        loaderBooleanFields.Any(field => field.GetValue(loader) as bool? == true);

    private bool IsVerified() =>
        verifier != null &&
        verifiedProperty?.GetValue(verifier) as bool? == true;

    private void InvokeLoaderMethod(MethodInfo? method, string action)
    {
        if (loader == null || method == null)
            throw new InvalidOperationException($"AEAssist Loader 的{action}入口不可用。");
        method.Invoke(loader, null);
    }

    private void Complete()
    {
        state = StartupState.Completed;
        var level = TryGetVerifiedLevel();
        status = level > 0
            ? $"已完成：AEAssist 已更新、加载并通过高级码验证（Lv{level}）"
            : "已完成：AEAssist 已更新、加载并通过通用码验证";
        Plugin.Log.Information(
            "AEAssist startup automation completed with verification level {Level}.",
            level);

        if (!completionReported && Plugin.Config.AeAssistStartup.PrintChatMessage)
            Plugin.Chat.Print($"[Keita 工具箱] {status}");
        completionReported = true;
    }

    private int TryGetVerifiedLevel()
    {
        if (aeAssistPlugin == null)
            return -1;
        try
        {
            var shareType = aeAssistPlugin.GetType().Assembly.GetType("AEAssist.Share", false);
            var vip = shareType?.GetProperty(
                    "VIP",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetValue(null);
            var level = vip?.GetType().GetProperty("Level")?.GetValue(vip);
            return level == null ? -1 : Convert.ToInt32(level);
        }
        catch
        {
            return -1;
        }
    }

    private void Restart()
    {
        loader = null;
        loaderType = null;
        pendingFilesField = null;
        loaderBooleanFields = [];
        checkMethod = null;
        updateMethod = null;
        loadMethod = null;
        aeAssistPlugin = null;
        verifier = null;
        verifiedProperty = null;
        requestingField = null;
        activeKeyField = null;
        submitVerificationMethod = null;
        verificationCandidates = [];
        verificationCandidateIndex = 0;
        enableCommandSent = false;
        checkRetrySent = false;
        updateRequested = false;
        completionReported = false;
        nextUpdateAt = 0;

        if (!Plugin.Config.AeAssistStartup.Enabled)
        {
            state = StartupState.Disabled;
            status = "已禁用";
            return;
        }

        SetState(
            StartupState.WaitingForLoader,
            "正在等待 AEAssist Loader…",
            LoaderTimeoutMs);
    }

    private void SetState(StartupState value, string message, long timeoutMs)
    {
        state = value;
        status = message;
        phaseStartedAt = Environment.TickCount64;
        phaseDeadline = phaseStartedAt + timeoutMs;
    }

    private void Fail(string message, Exception? exception = null)
    {
        state = StartupState.Failed;
        status = $"失败：{message}";
        if (exception == null)
            Plugin.Log.Warning("AEAssist startup automation failed: {Reason}.", message);
        else
            Plugin.Log.Error(exception, "AEAssist startup automation failed: {Reason}.", message);

        if (!completionReported && Plugin.Config.AeAssistStartup.PrintChatMessage)
            Plugin.Chat.PrintError($"[Keita 工具箱] {status}");
        completionReported = true;
    }

    private static MethodInfo? FindAsyncStateMachineMethod(Type type, string marker) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(
                method => method
                    .GetCustomAttribute<AsyncStateMachineAttribute>()
                    ?.StateMachineType.Name.Contains(
                        $"<{marker}>",
                        StringComparison.Ordinal) == true);

    private static MethodInfo? FindLoaderLoadMethod(Type type) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(
                method => method.ReturnType == typeof(void) &&
                          method.GetParameters().Length == 0)
            .FirstOrDefault(
                method =>
                {
                    var references = GetReferencedMembers(method).ToArray();
                    return references.Any(
                               member => member.Name == "LoadFromStream" &&
                                         member.DeclaringType?.FullName ==
                                         "System.Runtime.Loader.AssemblyLoadContext") &&
                           references.Any(
                               member => member.Name == "CreateInstance" &&
                                         member.DeclaringType == typeof(Activator));
                });

    private static bool TryGetDalamudPlugin(string internalName, out object instance)
    {
        instance = null!;
        try
        {
            var dalamudAssembly = Plugin.PluginInterface.GetType().Assembly;
            var pluginManagerType = dalamudAssembly.GetType(
                "Dalamud.Plugin.Internal.PluginManager",
                true)!;
            var serviceType = dalamudAssembly.GetType("Dalamud.Service`1", true)!
                .MakeGenericType(pluginManagerType);
            var getService = serviceType.GetMethod(
                "Get",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var pluginManager = getService?.Invoke(null, null);
            var installedPlugins = pluginManagerType.GetProperty(
                    "InstalledPlugins",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(pluginManager) as IEnumerable;
            if (installedPlugins == null)
                return false;

            foreach (var plugin in installedPlugins)
            {
                if (plugin == null)
                    continue;
                var name = plugin.GetType().GetProperty(
                        "InternalName",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(plugin) as string;
                if (!string.Equals(name, internalName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = FindField(plugin.GetType(), "instance")?.GetValue(plugin);
                if (value == null)
                    return false;
                instance = value;
                return true;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Unable to inspect the Dalamud plugin manager for AEAssist.");
        }

        return false;
    }

    private static FieldInfo? FindField(Type? type, string name)
    {
        while (type != null)
        {
            var field = type.GetField(
                name,
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly);
            if (field != null)
                return field;
            type = type.BaseType;
        }

        return null;
    }

    private static IEnumerable<FieldInfo> GetInstanceFields(Type type)
    {
        while (type != null)
        {
            foreach (var field in type.GetFields(
                         BindingFlags.Instance |
                         BindingFlags.Public |
                         BindingFlags.NonPublic |
                         BindingFlags.DeclaredOnly))
            {
                yield return field;
            }
            type = type.BaseType!;
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null)!;
        }
    }

    private static IEnumerable<MemberInfo> GetReferencedMembers(MethodInfo method)
    {
        var body = method.GetMethodBody();
        var bytes = body?.GetILAsByteArray();
        if (bytes == null)
            yield break;

        var position = 0;
        while (position < bytes.Length)
        {
            short value = bytes[position++];
            if (value == 0xFE)
                value = (short)(0xFE00 | bytes[position++]);
            if (!OpCodesByValue.TryGetValue(value, out var opCode))
                yield break;

            if (opCode.OperandType is
                OperandType.InlineField or
                OperandType.InlineMethod or
                OperandType.InlineTok or
                OperandType.InlineType)
            {
                var token = BitConverter.ToInt32(bytes, position);
                MemberInfo? member = null;
                try
                {
                    member = method.Module.ResolveMember(
                        token,
                        method.DeclaringType?.GetGenericArguments(),
                        method.GetGenericArguments());
                }
                catch
                {
                    // Ignore unresolved metadata references from protected assemblies.
                }

                if (member != null)
                    yield return member;
            }

            position += GetOperandSize(opCode.OperandType, bytes, position);
        }
    }

    private static int GetOperandSize(OperandType operandType, byte[] bytes, int position) =>
        operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or
                OperandType.ShortInlineI or
                OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or
                OperandType.InlineField or
                OperandType.InlineI or
                OperandType.InlineMethod or
                OperandType.InlineSig or
                OperandType.InlineString or
                OperandType.InlineTok or
                OperandType.InlineType or
                OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch =>
                4 + (BitConverter.ToInt32(bytes, position) * 4),
            _ => 0,
        };

    private sealed record VerificationCandidate(string Key, int Level, bool IsCurrent);

    private enum StartupState
    {
        Disabled,
        WaitingForLoader,
        Checking,
        Updating,
        Loading,
        WaitingForLogin,
        Verifying,
        Completed,
        Failed,
    }
}
