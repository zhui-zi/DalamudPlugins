using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Utility;

namespace KeitaToolbox;

public sealed partial class Plugin
{
    private const string ProjectUrl = "https://github.com/zhui-zi/DalamudPlugins";

    private enum SettingsPage
    {
        DutyFlow,
        PartyAndTrade,
        CharacterAndInterface,
        MovementAndSystem,
        CombatAndStatus,
        OccultCrescent,
        GoldSaucer,
        Integrations,
        About,
    }

    private SettingsPage selectedSettingsPage = SettingsPage.DutyFlow;

    private void OpenWindow() => windowOpen = true;

    private void DrawWindow()
    {
        if (!Config.DisclaimerAccepted)
        {
            DrawDisclaimerWindow();
            return;
        }

        if (!windowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(900, 700), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Keita 工具箱", ref windowOpen))
        {
            ImGui.End();
            return;
        }

        DrawDisabledWrapped("选择左侧分类查看功能；修改后会立即保存。");
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.BeginChild(
                "ToolboxSettingsNavigation",
                new Vector2(190f, 0f),
                true))
        {
            DrawDisabledWrapped("设置分类");
            ImGui.Separator();
            ImGui.Spacing();
            DrawNavigationItem(SettingsPage.DutyFlow, "副本流程");
            DrawNavigationItem(SettingsPage.PartyAndTrade, "组队与交易");
            DrawNavigationItem(SettingsPage.CharacterAndInterface, "角色与界面");
            DrawNavigationItem(SettingsPage.MovementAndSystem, "移动与系统");
            DrawNavigationItem(SettingsPage.CombatAndStatus, "战斗与状态");
            DrawNavigationItem(SettingsPage.OccultCrescent, "新月岛");
            DrawNavigationItem(SettingsPage.GoldSaucer, "金碟游乐场");
            DrawNavigationItem(SettingsPage.Integrations, "插件联动");
            ImGui.Spacing();
            DrawNavigationItem(SettingsPage.About, "关于");
        }
        ImGui.EndChild();

        ImGui.SameLine();
        if (ImGui.BeginChild("ToolboxSettingsContent", Vector2.Zero, false))
            DrawSelectedSettingsPage();
        ImGui.EndChild();
        ImGui.End();
    }

    private void DrawNavigationItem(SettingsPage page, string label)
    {
        if (ImGui.Selectable(label, selectedSettingsPage == page))
            selectedSettingsPage = page;
    }

    private void DrawSelectedSettingsPage()
    {
        var (title, description) = GetPageInfo(selectedSettingsPage);
        ImGui.TextUnformatted(title);
        DrawDisabledWrapped(description);
        ImGui.Separator();
        ImGui.Spacing();

        switch (selectedSettingsPage)
        {
            case SettingsPage.DutyFlow:
                basicFeatures?.DrawCommenceSettings();
                if (autoTreasureOpenFeature == null)
                    DrawUnavailable("自动开箱");
                else
                    autoTreasureOpenFeature.DrawSettings();
                autoLeaveFeature?.DrawSettings();
                basicFeatures?.DrawAnnouncementSettings();
                break;
            case SettingsPage.PartyAndTrade:
                autoInviteFeature?.DrawSettings();
                basicFeatures?.DrawPartyFinderSettings();
                if (autoRefuseTradeFeature == null)
                    DrawUnavailable("自动拒绝交易");
                else
                    autoRefuseTradeFeature.DrawSettings();
                if (voidAetherFeature == null)
                    DrawUnavailable("储物与商店服务");
                else
                    voidAetherFeature.DrawPartyAndTradeSettings();
                break;
            case SettingsPage.CharacterAndInterface:
                DrawFloatingButtonSettings();
                if (partyAliasFeature == null)
                    DrawUnavailable("小队姓名伪装");
                else
                    partyAliasFeature.DrawSettings();
                if (mapGearsetFeature == null)
                    DrawUnavailable("按地图自动切换套装");
                else
                    mapGearsetFeature.DrawSettings();
                if (portraitFeature == null)
                    DrawUnavailable("肖像与装备套装同步");
                else
                    portraitFeature.DrawSettings();
                basicFeatures?.DrawImeSettings();
                if (voidAetherFeature == null)
                    DrawUnavailable("装备与雇员服务");
                else
                    voidAetherFeature.DrawCharacterAndInterfaceSettings();
                break;
            case SettingsPage.MovementAndSystem:
                if (voidAetherFeature == null)
                    DrawUnavailable("水晶共鸣与风脉解锁");
                else
                    voidAetherFeature.DrawMovementAndSystemSettings();
                if (!DrawProtectedFeatureGate())
                    break;
                if (advancedToolsFeature == null)
                    DrawUnavailable("移动与系统设置");
                else
                    advancedToolsFeature.DrawMovementAndSystemSettings();
                break;
            case SettingsPage.CombatAndStatus:
                if (voidAetherFeature == null)
                    DrawBattlefieldFallbackSettings();
                else
                    voidAetherFeature.DrawCombatAndStatusSettings();
                if (!DrawProtectedFeatureGate())
                    break;
                DrawInstantReturnSettings();
                if (advancedToolsFeature == null)
                    DrawUnavailable("战斗与状态设置");
                else
                    advancedToolsFeature.DrawCombatUtilitySettings();
                break;
            case SettingsPage.OccultCrescent:
                if (occultPotFeature == null)
                    DrawUnavailable("魔法罐助手");
                else
                    occultPotFeature.DrawSettings();
                break;
            case SettingsPage.GoldSaucer:
                if (outOnALimbFeature == null)
                    DrawUnavailable("自动游玩孤树无援");
                else
                    outOnALimbFeature.DrawSettings();
                if (fashionReportFeature == null)
                    DrawUnavailable("时尚品鉴助手");
                else
                    fashionReportFeature.DrawSettings();
                break;
            case SettingsPage.Integrations:
                if (aeAssistStartupFeature == null)
                    DrawUnavailable("AEAssist 启动自动化");
                else
                    aeAssistStartupFeature.DrawSettings();
                basicFeatures?.DrawBmraiSettings();
                basicFeatures?.DrawPluginSwitcherSettings();
                if (verificationMonitorFeature == null)
                {
                    DrawUnavailable("插件验证监控");
                }
                else if (ImGui.CollapsingHeader("插件验证监控"))
                {
                    verificationMonitorFeature.DrawSettings();
                }
                break;
            case SettingsPage.About:
                DrawAboutPage();
                break;
        }
    }

    private void DrawDisclaimerWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(620f, 390f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin(
                "Keita 工具箱使用须知###KeitaToolboxDisclaimer",
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.End();
            return;
        }
        ImGui.TextUnformatted("免责声明");
        ImGui.Separator();
        ImGui.Spacing();
        DrawWrapped(
            "Keita 工具箱是第三方 Dalamud 插件，包含自动化、移动、战斗及系统相关功能。使用第三方工具可能违反游戏服务条款，并可能导致账号处罚、数据损坏、游戏崩溃或其他损失。");
        ImGui.Spacing();
        DrawWrapped(
            "本项目按“原样”提供，不作任何明示或默示保证。使用者应自行了解并承担全部风险；作者及贡献者不对因使用本插件产生的任何索赔、损害或其他责任负责。");
        ImGui.Spacing();
        DrawWrapped("项目地址：");
        DrawProjectLink();
        ImGui.Spacing();
        DrawWrapped("KeitaToolbox 源代码采用 MIT License 开源，随附的第三方组件适用其各自的许可证。完整许可文本见项目仓库中的 LICENSE 文件。");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawWrapped("点击“同意并继续”即表示你已阅读、理解并接受以上内容。");
        ImGui.Spacing();
        var buttonWidth = 160f;
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), (ImGui.GetWindowWidth() - buttonWidth) / 2f));
        if (ImGui.Button("同意并继续", new Vector2(buttonWidth, 0f)))
        {
            Config.DisclaimerAccepted = true;
            Config.Save();
            InitializeRuntime();
            windowOpen = true;
        }
        ImGui.End();
    }
    private static void DrawAboutPage()
    {
        if (ImGui.CollapsingHeader("项目", ImGuiTreeNodeFlags.DefaultOpen))
            DrawProjectLink();

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("免责声明", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawWrapped(
                "本插件按“原样”提供，不作任何明示或默示保证。使用者自行承担使用第三方插件及相关功能的全部风险，作者及贡献者不对由此产生的任何索赔、损害或其他责任负责。");
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("开源许可", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawWrapped("KeitaToolbox 源代码采用 MIT License 开源，随附的第三方组件适用其各自的许可证。完整许可文本见项目仓库中的 LICENSE 文件。");
        }
    }

    private static void DrawProjectLink()
    {
        if (ImGui.Selectable(ProjectUrl, false))
            Util.OpenLink(ProjectUrl);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("点击打开 GitHub 项目主页");
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
        DrawDisabledWrapped(text);
        ImGui.Unindent();
        ImGui.Spacing();
    }

    internal static void DrawDisabledWrapped(string text)
    {
        var wrapPosition = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        ImGui.PushTextWrapPos(wrapPosition);
        ImGui.TextDisabled(PreservePhraseSpacing(text));
        ImGui.PopTextWrapPos();
    }

    internal static void DrawColoredWrapped(Vector4 color, string text)
    {
        var wrapPosition = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        ImGui.PushTextWrapPos(wrapPosition);
        ImGui.TextColored(color, PreservePhraseSpacing(text));
        ImGui.PopTextWrapPos();
    }

    internal static void DrawWrapped(string text) =>
        ImGui.TextWrapped(PreservePhraseSpacing(text));

    private static string PreservePhraseSpacing(string text) =>
        text.Contains(' ') ? text.Replace(' ', '\u00a0') : text;

    private static void DrawFloatingButtonSettings()
    {
        if (!ImGui.CollapsingHeader(
                "工具箱入口",
                ImGuiTreeNodeFlags.DefaultOpen))
            return;

        DrawFeatureToggle(
            "常驻悬浮按钮",
            Config.Interface.ShowFloatingButton,
            value => Config.Interface.ShowFloatingButton = value);
        DrawHelpWithCommand("使用插件图标显示；左键打开设置，右键拖动位置。", "/ktb");
    }

    private void DrawBattlefieldFallbackSettings()
    {
        if (!ImGui.CollapsingHeader("PVP", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (advancedToolsFeature == null)
            DrawUnavailable("远程摸点");
        else
            advancedToolsFeature.DrawFrontlineRemoteInteractionSettings();

        ImGui.Separator();
        DrawUnavailable("战场点位");
    }

    private bool DrawProtectedFeatureGate()
    {
        if (!PasswordProtectionEnabled || Config.ProtectedFeaturesUnlocked)
            return true;

        CompleteUnlockRequest();
        if (!ImGui.CollapsingHeader(
                "高级功能解锁",
                ImGuiTreeNodeFlags.DefaultOpen))
            return false;

        DrawWrapped(
            "首次输入工具箱密码即可解锁本机的受保护功能，后续无需再次输入。");
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
                DrawDisabledWrapped("正在验证……");
            else if (unlockError.Length > 0)
                DrawColoredWrapped(new Vector4(1f, 0.35f, 0.35f, 1f), unlockError);
            DrawHelp("密码通过 HTTPS 验证且不会保存，本地仅记录验证成功状态。");
        }

        return ProtectedFeaturesUnlocked;
    }

    private static void DrawInstantReturnSettings()
    {
        if (!ImGui.CollapsingHeader("即刻返回"))
            return;

        DrawFeatureToggle(
            "即刻返回",
            Config.Features.InstantReturn,
            value => Config.Features.InstantReturn = value);
        DrawHelpWithCommand("立即执行返回命令。", "/ktb return");

        if (ImGui.Button("立即返回"))
            ExecuteInstantReturn();
    }

    private static void DrawUnavailable(string name) =>
        DrawDisabledWrapped($"{name}当前不可用，请检查 Dalamud 日志。");
}
