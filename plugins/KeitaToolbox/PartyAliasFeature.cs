using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

namespace KeitaToolbox;

internal sealed unsafe class PartyAliasFeature : IDisposable
{
    private readonly string[] currentNames = new string[PartyAliasSettings.SlotCount];
    private readonly uint[] currentEntityIds = new uint[PartyAliasSettings.SlotCount];
    private readonly string[] lastAppliedAliases = new string[PartyAliasSettings.SlotCount];
    private bool failureLogged;

    private PartyAliasSettings Settings => Plugin.Config.PartyAlias;

    public int MemberCount { get; private set; }

    public PartyAliasFeature()
    {
        Plugin.Framework.Update += OnFrameworkUpdate;
        Plugin.NamePlateGui.OnDataUpdate += OnNamePlateDataUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Plugin.NamePlateGui.OnDataUpdate -= OnNamePlateDataUpdate;

        try
        {
            UpdatePartySnapshot();
            RestoreAliases();
            Plugin.NamePlateGui.RequestRedraw();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to restore party aliases during disposal.");
        }
    }

    public void DrawSettings()
    {
        if (!ImGui.CollapsingHeader("小队姓名伪装", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var enabled = Settings.Enabled;
        if (ImGui.Checkbox("启用小队列表与头顶姓名伪装", ref enabled))
        {
            Settings.Enabled = enabled;
            Plugin.Config.Save();
            Plugin.NamePlateGui.RequestRedraw();
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"当前成员 {MemberCount}/8");
        Plugin.DrawHelp("按小队列表实际显示顺序设置别名，仅改写本机小队列表和对应成员的头顶姓名。");

        var tableHeight = MathF.Max(260f, ImGui.GetTextLineHeightWithSpacing() * 10f);
        var tableFlags = ImGuiTableFlags.Borders |
                         ImGuiTableFlags.RowBg |
                         ImGuiTableFlags.ScrollY |
                         ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("##PartyAliases", 4, tableFlags, new Vector2(0f, tableHeight)))
        {
            ImGui.TableSetupColumn("位置", ImGuiTableColumnFlags.WidthFixed, 54f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupColumn("当前姓名", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("显示别名", ImGuiTableColumnFlags.WidthStretch, 1.4f);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 60f * ImGuiHelpers.GlobalScale);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            for (var slot = 0; slot < PartyAliasSettings.SlotCount; slot++)
                DrawAliasRow(slot);

            ImGui.EndTable();
        }

        ImGui.Spacing();
        if (ImGui.Button("全部清除"))
        {
            Settings.ClearAliases();
            Plugin.Config.Save();
            Plugin.NamePlateGui.RequestRedraw();
        }

        ImGui.Spacing();
        ImGui.Separator();
    }

    private void DrawAliasRow(int slot)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted((slot + 1).ToString());

        ImGui.TableSetColumnIndex(1);
        var memberName = currentNames[slot];
        ImGui.TextUnformatted(string.IsNullOrEmpty(memberName) ? "空位" : memberName);

        ImGui.TableSetColumnIndex(2);
        ImGui.SetNextItemWidth(-1f);
        var alias = Settings.GetAlias(slot);
        if (ImGui.InputText($"##PartyAlias{slot}", ref alias, 96))
        {
            Settings.SetAlias(slot, alias);
            Plugin.Config.Save();
            Plugin.NamePlateGui.RequestRedraw();
        }

        ImGui.TableSetColumnIndex(3);
        if (ImGui.SmallButton($"清除##PartyAlias{slot}"))
        {
            Settings.SetAlias(slot, string.Empty);
            Plugin.Config.Save();
            Plugin.NamePlateGui.RequestRedraw();
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        try
        {
            UpdatePartySnapshot();
            UpdatePartyListAddon();
            failureLogged = false;
        }
        catch (Exception ex)
        {
            if (failureLogged)
                return;

            Plugin.Log.Error(ex, "Failed to update party aliases.");
            failureLogged = true;
        }
    }

    private void UpdatePartySnapshot()
    {
        Array.Fill(currentNames, string.Empty);
        Array.Fill(currentEntityIds, 0u);
        MemberCount = 0;

        var hud = AgentHUD.Instance();
        if (hud == null || hud->PartyMemberCount == 0)
            return;

        for (var i = 0; i < PartyAliasSettings.SlotCount; i++)
        {
            var member = hud->PartyMembers.GetPointer(i);
            if (member == null || member->ContentId == 0 || member->Index >= PartyAliasSettings.SlotCount)
                continue;

            var slot = member->Index;
            currentNames[slot] = member->Name.ToString();
            currentEntityIds[slot] = member->EntityId;
            MemberCount++;
        }
    }

    private void OnNamePlateDataUpdate(
        INamePlateUpdateContext _,
        IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        if (!Settings.Enabled)
            return;

        foreach (var handler in handlers)
        {
            var entityId = handler.GameObject?.EntityId ?? 0u;
            var slot = Array.IndexOf(currentEntityIds, entityId);
            if (entityId == 0 || slot < 0)
                continue;

            var alias = Settings.GetAlias(slot);
            if (!string.IsNullOrEmpty(alias) &&
                !handler.GetFieldAsString(NamePlateStringField.Name).Equals(alias, StringComparison.Ordinal))
            {
                handler.SetField(NamePlateStringField.Name, alias);
            }
        }
    }

    private void UpdatePartyListAddon()
    {
        var addon = (AddonPartyList*)Plugin.GameGui.GetAddonByName("_PartyList", 1).Address;
        if (addon == null)
            return;

        for (var slot = 0; slot < PartyAliasSettings.SlotCount; slot++)
        {
            var nameNode = addon->PartyMembers[slot].Name;
            if (nameNode == null)
            {
                lastAppliedAliases[slot] = string.Empty;
                continue;
            }

            var realName = currentNames[slot];
            var alias = Settings.Enabled ? Settings.GetAlias(slot) : string.Empty;
            if (!string.IsNullOrEmpty(realName) && !string.IsNullOrEmpty(alias))
            {
                if (!nameNode->NodeText.ToString().Equals(alias, StringComparison.Ordinal))
                    nameNode->SetText(alias);
                lastAppliedAliases[slot] = alias;
            }
            else
            {
                RestoreSlot(nameNode, slot, realName);
            }
        }
    }

    private void RestoreAliases()
    {
        var addon = (AddonPartyList*)Plugin.GameGui.GetAddonByName("_PartyList", 1).Address;
        if (addon == null)
            return;

        for (var slot = 0; slot < PartyAliasSettings.SlotCount; slot++)
            RestoreSlot(addon->PartyMembers[slot].Name, slot, currentNames[slot]);
    }

    private void RestoreSlot(AtkTextNode* nameNode, int slot, string realName)
    {
        var appliedAlias = lastAppliedAliases[slot];
        if (nameNode != null &&
            !string.IsNullOrEmpty(appliedAlias) &&
            !string.IsNullOrEmpty(realName) &&
            nameNode->NodeText.ToString().Equals(appliedAlias, StringComparison.Ordinal))
        {
            nameNode->SetText(realName);
        }

        lastAppliedAliases[slot] = string.Empty;
    }
}
