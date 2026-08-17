using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace KeitaToolbox;

public sealed partial class Plugin
{
    private enum SettingsPage
    {
        DutyFlow,
        PartyAndTrade,
        CharacterAndInterface,
        OccultCrescent,
        IntegrationsAndAdvanced,
    }

    private SettingsPage selectedSettingsPage = SettingsPage.DutyFlow;

    private void OpenWindow() => windowOpen = true;

    private void DrawWindow()
    {
        if (!windowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(860, 680), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Keita 工具箱", ref windowOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.TextDisabled("选择左侧分类查看功能；修改后会立即保存。");
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.BeginChild(
                "ToolboxSettingsNavigation",
                new Vector2(170f, 0f),
                true))
        {
            ImGui.TextDisabled("设置分类");
            ImGui.Separator();
            ImGui.Spacing();
            DrawNavigationItem(SettingsPage.DutyFlow, "副本流程");
            DrawNavigationItem(SettingsPage.PartyAndTrade, "组队与交易");
            DrawNavigationItem(SettingsPage.CharacterAndInterface, "角色与界面");
            DrawNavigationItem(SettingsPage.OccultCrescent, "新月岛");
            DrawNavigationItem(SettingsPage.IntegrationsAndAdvanced, "插件与高级");
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
        var (title, description) = selectedSettingsPage switch
        {
            SettingsPage.DutyFlow => (
                "副本流程",
                "管理任务开始、结束退出与通关后处理。"),
            SettingsPage.PartyAndTrade => (
                "组队与交易",
                "管理邀请、招募筛选和交易处理。"),
            SettingsPage.CharacterAndInterface => (
                "角色与界面",
                "管理装备套装、即时肖像和输入界面修正。"),
            SettingsPage.OccultCrescent => (
                "新月岛",
                "管理魔法罐提醒、地图标记、自动化与战斗辅助。"),
            _ => (
                "插件与高级",
                "管理外部插件联动、验证监控与高级工具。"),
        };

        ImGui.TextUnformatted(title);
        ImGui.TextDisabled(description);
        ImGui.Separator();
        ImGui.Spacing();

        switch (selectedSettingsPage)
        {
            case SettingsPage.DutyFlow:
                basicFeatures?.DrawCommenceSettings();
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
                break;
            case SettingsPage.CharacterAndInterface:
                if (mapGearsetFeature == null)
                    DrawUnavailable("按地图自动切换套装");
                else
                    mapGearsetFeature.DrawSettings();
                if (portraitFeature == null)
                    DrawUnavailable("肖像与装备套装同步");
                else
                    portraitFeature.DrawSettings();
                basicFeatures?.DrawImeSettings();
                break;
            case SettingsPage.OccultCrescent:
                if (occultPotFeature == null)
                    DrawUnavailable("魔法罐助手");
                else
                    occultPotFeature.DrawSettings();
                break;
            case SettingsPage.IntegrationsAndAdvanced:
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
                DrawProtectedFeatureSettings();
                break;
        }
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
        if (PasswordProtectionEnabled && !Config.ProtectedFeaturesUnlocked)
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

    private static void DrawUnavailable(string name) =>
        ImGui.TextDisabled($"{name}当前不可用，请检查 Dalamud 日志。");
}
