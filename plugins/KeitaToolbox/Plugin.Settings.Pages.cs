using System;

namespace KeitaToolbox;

public sealed partial class Plugin
{
    private static (string Title, string Description) GetPageInfo(SettingsPage page) => page switch
    {
        SettingsPage.DutyFlow => (
            "副本流程",
            "管理任务开始、结束退出与通关后的连续处理。"),
        SettingsPage.PartyAndTrade => (
            "组队与交易",
            "管理邀请、招募筛选、交易保护、部队储物与商店服务。"),
        SettingsPage.CharacterAndInterface => (
            "角色与界面",
            "管理装备套装、即时肖像、输入界面、装备维护与雇员服务。"),
        SettingsPage.MovementAndSystem => (
            "移动与系统",
            "管理移动控制、技能距离、水晶、风脉、位置探索、传送与系统状态。"),
        SettingsPage.CombatAndStatus => (
            "战斗与状态",
            "管理紧急操作、异常状态处理和 PvP 交互。"),
        SettingsPage.OccultCrescent => (
            "新月岛",
            "管理魔法罐提醒、地图标记、自动化与战斗辅助。"),
        SettingsPage.GoldSaucer => (
            "金碟游乐场",
            "管理时尚品鉴提示与候选装备数据。"),
        SettingsPage.Integrations => (
            "插件联动",
            "管理外部插件启动、自动切换、参数同步与验证监控。"),
        SettingsPage.About => (
            "关于",
            "查看项目地址、免责声明与开源许可。"),
        _ => throw new ArgumentOutOfRangeException(nameof(page), page, null),
    };
}
