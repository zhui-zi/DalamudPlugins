using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace KeitaToolbox;

internal sealed class FashionReportFeature : IDisposable
{
    private const string PrimaryDataUrl =
        "https://raw.githubusercontent.com/Infiziert90/FFXIVGachaSpreadsheet/refs/heads/master/website/static/data/FashionReport.json";
    private const string FallbackDataUrl = "https://xivstats.com/data/FashionReport.json";
    private const string GarlandItemUrl = "https://garlandtools.cn/db/#item/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
    private readonly CancellationTokenSource cancellation = new();
    private readonly Dictionary<uint, ushort> dyeIcons;

    private IReadOnlyDictionary<uint, IReadOnlyList<ItemCandidate>> categories =
        new Dictionary<uint, IReadOnlyList<ItemCandidate>>();
    private IReadOnlyDictionary<uint, IReadOnlyList<DyeCandidate>> weeklyDyes =
        new Dictionary<uint, IReadOnlyList<DyeCandidate>>();
    private Task? loadTask;
    private nint addonAddress;
    private FashionSlot? selectedItemSlot;
    private FashionSlot? selectedDyeSlot;
    private Vector2 itemWindowPosition;
    private Vector2 dyeWindowPosition;
    private List<ResolvedItem> selectedItems = [];
    private List<ResolvedDye> selectedDyes = [];
    private string dataStatus = "正在加载数据……";
    private int disposeState;

    public FashionReportFeature()
    {
        dyeIcons = BuildDyeIconMap();
        Plugin.AddonLifecycle.RegisterListener(
            AddonEvent.PostSetup,
            "FashionCheck",
            OnFashionCheckSetup);
        Plugin.AddonLifecycle.RegisterListener(
            AddonEvent.PreClose,
            "FashionCheck",
            OnFashionCheckClose);
        Plugin.PluginInterface.UiBuilder.Draw += Draw;
        StartDataLoad();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
            return;

        Plugin.PluginInterface.UiBuilder.Draw -= Draw;
        Plugin.AddonLifecycle.UnregisterListener(
            AddonEvent.PostSetup,
            "FashionCheck",
            OnFashionCheckSetup);
        Plugin.AddonLifecycle.UnregisterListener(
            AddonEvent.PreClose,
            "FashionCheck",
            OnFashionCheckClose);
        cancellation.Cancel();
        cancellation.Dispose();
        httpClient.Dispose();
    }

    public void DrawSettings()
    {
        if (!ImGui.CollapsingHeader("时尚品鉴助手", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        Plugin.DrawFeatureToggle(
            "时尚品鉴助手",
            Plugin.Config.Features.FashionReportAssistant,
            value => Plugin.Config.Features.FashionReportAssistant = value);
        Plugin.DrawHelp("打开时尚品鉴界面时，在各装备栏旁显示候选装备与染剂统计入口。");

        ImGui.TextDisabled(dataStatus);
        if (loadTask is { IsCompleted: false })
            ImGui.BeginDisabled();
        if (ImGui.Button("重新加载数据"))
            StartDataLoad();
        if (loadTask is { IsCompleted: false })
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("打开数据源"))
            Util.OpenLink(PrimaryDataUrl);
    }

    private unsafe void OnFashionCheckSetup(AddonEvent _, AddonArgs args)
    {
        addonAddress = args.Addon.Address;
        selectedItemSlot = null;
        selectedDyeSlot = null;
    }

    private void OnFashionCheckClose(AddonEvent _, AddonArgs __)
    {
        addonAddress = 0;
        selectedItemSlot = null;
        selectedDyeSlot = null;
        selectedItems.Clear();
        selectedDyes.Clear();
    }

    private unsafe void Draw()
    {
        if (!Plugin.Config.Features.FashionReportAssistant || addonAddress == 0)
            return;

        var addon = (AtkUnitBase*)addonAddress;
        if (addon == null || !addon->IsVisible || addon->AtkValues == null)
            return;

        try
        {
            DrawSlotButtons(addon);
            DrawItemWindow();
            DrawDyeWindow();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to draw the Fashion Report assistant.");
            addonAddress = 0;
        }
    }

    private unsafe void DrawSlotButtons(AtkUnitBase* addon)
    {
        var addonPosition = new Vector2(addon->X, addon->Y);
        var scale = addon->Scale;

        foreach (var slot in Enum.GetValues<FashionSlot>())
        {
            var slotIndex = (uint)slot;
            var valueIndex = 2u + (slotIndex * 11u);
            if (valueIndex >= addon->AtkValuesCount)
                continue;

            var slotNode = addon->GetNodeById(8u + slotIndex);
            if (slotNode == null)
                continue;

            var categoryName = addon->AtkValues[valueIndex].String.ToString();
            var itemButtonSize = Math.Max(22f, slotNode->Height * 0.8f * scale);

            if (!string.IsNullOrWhiteSpace(categoryName))
            {
                var itemPosition = addonPosition + GetItemButtonPosition(slotNode, scale);
                if (DrawOverlayButton($"装备候选##KtbFashionItem{slot}", "装", itemPosition, itemButtonSize))
                    ToggleItemWindow(slot, categoryName, itemPosition, itemButtonSize);
            }

            if (slot > FashionSlot.Feet)
                continue;

            var dyeButtonSize = Math.Max(18f, slotNode->Height * 0.5f * scale);
            var dyePosition = addonPosition + GetDyeButtonPosition(slotNode, scale);
            if (DrawOverlayButton($"染剂统计##KtbFashionDye{slot}", "染", dyePosition, dyeButtonSize))
                ToggleDyeWindow(slot, dyePosition);
        }
    }

    private static bool DrawOverlayButton(
        string id,
        string label,
        Vector2 position,
        float size)
    {
        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(position);
        ImGui.SetNextWindowSize(new Vector2(size));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var visible = ImGui.Begin(
            id,
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoNav);
        var clicked = visible && ImGui.Button(label, new Vector2(size));
        if (visible && ImGui.IsItemHovered())
            ImGui.SetTooltip(id.StartsWith("装备", StringComparison.Ordinal) ? "显示候选装备" : "显示染剂统计");
        ImGui.End();
        ImGui.PopStyleVar();
        return clicked;
    }

    private void ToggleItemWindow(
        FashionSlot slot,
        string categoryName,
        Vector2 buttonPosition,
        float buttonSize)
    {
        if (selectedItemSlot == slot)
        {
            selectedItemSlot = null;
            selectedItems.Clear();
            return;
        }

        selectedItemSlot = slot;
        selectedDyeSlot = null;
        itemWindowPosition = buttonPosition;
        itemWindowPosition.X += slot >= FashionSlot.Ears ? -390f : buttonSize;

        var categoryId = FindCategoryId(categoryName);
        selectedItems = ResolveItems(slot, categoryId);
    }

    private void ToggleDyeWindow(
        FashionSlot slot,
        Vector2 buttonPosition)
    {
        if (selectedDyeSlot == slot)
        {
            selectedDyeSlot = null;
            selectedDyes.Clear();
            return;
        }

        selectedDyeSlot = slot;
        selectedItemSlot = null;
        dyeWindowPosition = buttonPosition - new Vector2(390f, 0f);
        selectedDyes = ResolveDyes(slot);
    }

    private void DrawItemWindow()
    {
        if (selectedItemSlot is not FashionSlot slot)
            return;

        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(itemWindowPosition, ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new Vector2(390f, 430f), ImGuiCond.FirstUseEver);
        var open = true;
        if (!ImGui.Begin(
                $"时尚品鉴：{GetSlotName(slot)}###KtbFashionItemList",
                ref open,
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            if (!open)
                selectedItemSlot = null;
            return;
        }

        if (selectedItems.Count == 0)
        {
            ImGui.TextDisabled(categories.Count == 0
                ? "候选数据尚未加载。"
                : "该提示暂无已收录的候选装备。");
        }
        else
        {
            ImGui.TextDisabled($"已收录 {selectedItems.Count} 件候选装备");
            ImGui.Separator();
            if (ImGui.BeginChild("KtbFashionItems", Vector2.Zero, false))
            {
                foreach (var entry in selectedItems)
                    DrawItem(entry);
            }
            ImGui.EndChild();
        }

        ImGui.End();
        if (!open)
            selectedItemSlot = null;
    }

    private static unsafe void DrawItem(ResolvedItem entry)
    {
        const float iconSize = 36f;
        var item = entry.Item;
        if (Plugin.TextureProvider
            .GetFromGameIcon(new GameIconLookup(item.Icon))
            .TryGetWrap(out var texture, out _) && texture != null)
        {
            ImGui.Image(texture.Handle, new Vector2(iconSize));
            ImGui.SameLine();
        }

        var itemName = item.Name.ExtractText();
        if (ImGui.Selectable(
                $"{itemName}\n采用记录：{entry.Count}##KtbFashionItem{item.RowId}",
                false,
                ImGuiSelectableFlags.None,
                new Vector2(0f, iconSize)))
        {
            ImGui.OpenPopup($"KtbFashionItemActions{item.RowId}");
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"可装备职业：{item.ClassJobCategory.Value.Name.ExtractText()}");

        if (!ImGui.BeginPopup($"KtbFashionItemActions{item.RowId}"))
            return;

        ImGui.TextUnformatted(itemName);
        ImGui.Separator();
        DrawItemAction("试穿", () => AgentTryon.TryOn(0, item.RowId));
        DrawItemAction(
            "搜索物品",
            () => ItemFinderModule.Instance()->SearchForItem(item.RowId, true));
        DrawItemAction("复制名称", () => ImGui.SetClipboardText(itemName));
        DrawItemAction(
            "打开 Garland Tools 国服站",
            () => Util.OpenLink(GarlandItemUrl + item.RowId));
        ImGui.EndPopup();
    }

    private static void DrawItemAction(string label, System.Action action)
    {
        if (!ImGui.Selectable(label))
            return;

        try
        {
            action();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "A Fashion Report item action failed.");
        }
    }

    private void DrawDyeWindow()
    {
        if (selectedDyeSlot is not FashionSlot slot)
            return;

        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(dyeWindowPosition, ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new Vector2(390f, 430f), ImGuiCond.FirstUseEver);
        var open = true;
        if (!ImGui.Begin(
                $"时尚品鉴染剂：{GetSlotName(slot)}###KtbFashionDyeList",
                ref open,
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            if (!open)
                selectedDyeSlot = null;
            return;
        }

        if (selectedDyes.Count == 0)
        {
            ImGui.TextDisabled(weeklyDyes.Count == 0
                ? "本周染剂数据尚未收录。"
                : "该装备栏暂无染剂统计。");
        }
        else
        {
            var total = selectedDyes.Aggregate(0UL, (sum, dye) => sum + dye.Count);
            ImGui.TextDisabled($"统计样本：{total}");
            ImGui.Separator();
            if (ImGui.BeginChild("KtbFashionDyes", Vector2.Zero, false))
            {
                foreach (var dye in selectedDyes)
                    DrawDye(dye, total);
            }
            ImGui.EndChild();
        }

        ImGui.End();
        if (!open)
            selectedDyeSlot = null;
    }

    private static void DrawDye(ResolvedDye dye, ulong total)
    {
        var color = dye.Stain.Color;
        var colorVector = new Vector4(
            ((color >> 16) & 0xff) / 255f,
            ((color >> 8) & 0xff) / 255f,
            (color & 0xff) / 255f,
            1f);
        ImGui.ColorButton(
            $"##KtbFashionDyeColor{dye.Stain.RowId}",
            colorVector,
            ImGuiColorEditFlags.NoTooltip,
            new Vector2(32f));
        ImGui.SameLine();
        ImGui.TextUnformatted(
            $"{dye.Stain.Name.ExtractText()}（{dye.Shade}）\n置信度：{dye.Confidence:P1}（{dye.Count}/{total}）");
        ImGui.Spacing();
    }

    private uint FindCategoryId(string categoryName)
    {
        var sheet = Plugin.Data.GetExcelSheet<FashionCheckThemeCategory>(Plugin.ClientState.ClientLanguage);
        if (sheet == null)
            return 0;

        foreach (var category in sheet)
        {
            if (category.Name.ExtractText().Equals(categoryName, StringComparison.Ordinal))
                return category.RowId;
        }

        return 0;
    }

    private List<ResolvedItem> ResolveItems(FashionSlot slot, uint categoryId)
    {
        if (categoryId == 0 || !categories.TryGetValue(categoryId, out var candidates))
            return [];

        var itemSheet = Plugin.Data.GetExcelSheet<Item>();
        if (itemSheet == null)
            return [];

        var result = new List<ResolvedItem>();
        foreach (var candidate in candidates)
        {
            if (!itemSheet.TryGetRow(candidate.ItemId, out var item) || !MatchesSlot(slot, item))
                continue;

            result.Add(new ResolvedItem(item, candidate.Count));
        }

        return result;
    }

    private List<ResolvedDye> ResolveDyes(FashionSlot slot)
    {
        if (!weeklyDyes.TryGetValue((uint)slot, out var candidates))
            return [];

        var stainSheet = Plugin.Data.GetExcelSheet<Stain>();
        if (stainSheet == null)
            return [];

        var result = new List<ResolvedDye>();
        foreach (var candidate in candidates)
        {
            if (!stainSheet.TryGetRow(candidate.StainId, out var stain))
                continue;

            var icon = dyeIcons.GetValueOrDefault(stain.RowId);
            result.Add(new ResolvedDye(
                stain,
                candidate.Count,
                candidate.Confidence,
                GetScoringShade(icon)));
        }

        return result;
    }

    private Dictionary<uint, ushort> BuildDyeIconMap()
    {
        var result = new Dictionary<uint, ushort>();
        var itemSheet = Plugin.Data.GetExcelSheet<Item>();
        if (itemSheet == null)
            return result;

        foreach (var itemId in EnumerateDyeItemIds())
        {
            if (!itemSheet.TryGetRow(itemId, out var item) || item.AdditionalData.RowId == 0)
                continue;

            result[item.AdditionalData.RowId] = item.Icon;
        }

        return result;
    }

    private static IEnumerable<uint> EnumerateDyeItemIds()
    {
        foreach (var range in new[]
        {
            (Start: 5729u, End: 5813u),
            (Start: 13114u, End: 13117u),
            (Start: 13708u, End: 13723u),
            (Start: 30116u, End: 30124u),
            (Start: 48163u, End: 48172u),
            (Start: 48227u, End: 48227u),
        })
        {
            for (var id = range.Start; id <= range.End; id++)
                yield return id;
        }
    }

    private static bool MatchesSlot(FashionSlot slot, Item item)
    {
        var equipSlot = item.EquipSlotCategory.Value;
        return slot switch
        {
            FashionSlot.Weapon => equipSlot.MainHand > 0,
            FashionSlot.Head => equipSlot.Head > 0,
            FashionSlot.Body => equipSlot.Body > 0,
            FashionSlot.Hands => equipSlot.Gloves > 0,
            FashionSlot.Legs => equipSlot.Legs > 0,
            FashionSlot.Feet => equipSlot.Feet > 0,
            FashionSlot.Ears => equipSlot.Ears > 0,
            FashionSlot.Neck => equipSlot.Neck > 0,
            FashionSlot.Wrists => equipSlot.Wrists > 0,
            FashionSlot.RightRing => equipSlot.FingerR > 0,
            FashionSlot.LeftRing => equipSlot.FingerL > 0,
            _ => false,
        };
    }

    private static unsafe Vector2 GetItemButtonPosition(AtkResNode* node, float scale)
    {
        var component = node->GetComponent();
        var button = component == null ? null : component->GetNodeById(4);
        if (button == null)
            return Vector2.Zero;

        return new Vector2(
            256f + node->X + button->X,
            80f + node->Y + button->Y) * scale;
    }

    private static unsafe Vector2 GetDyeButtonPosition(AtkResNode* node, float scale)
    {
        var component = node->GetComponent();
        var image = component == null ? null : component->GetNodeById(3);
        if (image == null)
            return Vector2.Zero;

        return new Vector2(
            251f + node->X + image->X,
            75f + node->Y) * scale;
    }

    private void StartDataLoad()
    {
        if (disposeState != 0 || loadTask is { IsCompleted: false })
            return;

        dataStatus = "正在加载数据……";
        loadTask = LoadDataAsync(cancellation.Token);
    }

    private async Task LoadDataAsync(CancellationToken token)
    {
        Exception? lastError = null;
        foreach (var url in new[] { PrimaryDataUrl, FallbackDataUrl })
        {
            try
            {
                using var response = await httpClient.GetAsync(url, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var data = await JsonSerializer.DeserializeAsync<FashionData>(
                    stream,
                    JsonOptions,
                    token).ConfigureAwait(false);
                if (data == null)
                    throw new JsonException("Fashion Report data was empty.");

                categories = data.Categories.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<ItemCandidate>)pair.Value
                        .Select(item => new ItemCandidate(item.Key, item.Value))
                        .ToList());
                weeklyDyes = BuildWeeklyDyes(data);
                dataStatus = $"数据已加载：{categories.Count} 个提示分类";
                Plugin.Log.Information("Fashion Report data loaded from {Url}.", url);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Plugin.Log.Warning(ex, "Failed to load Fashion Report data from {Url}.", url);
            }
        }

        dataStatus = "数据加载失败，请稍后重试。";
        if (lastError != null)
            Plugin.Log.Error(lastError, "All Fashion Report data sources failed.");
    }

    private static IReadOnlyDictionary<uint, IReadOnlyList<DyeCandidate>> BuildWeeklyDyes(
        FashionData data)
    {
        if (!data.WeeklyDyes.TryGetValue(GetCurrentWeek(), out var slots))
            return new Dictionary<uint, IReadOnlyList<DyeCandidate>>();

        var result = new Dictionary<uint, IReadOnlyList<DyeCandidate>>();
        foreach (var slot in slots)
        {
            var normalizedSlot = NormalizeDyeSlot(slot.Id);
            if (normalizedSlot == null)
                continue;

            result[(uint)normalizedSlot.Value] = slot.Dyes
                .Select(dye => new DyeCandidate(dye.Key, dye.Value.Count, dye.Value.Pct))
                .OrderByDescending(dye => dye.Confidence)
                .ToList();
        }

        return result;
    }

    private static uint GetCurrentWeek()
    {
        var firstWeek = new DateTimeOffset(2018, 1, 30, 8, 0, 0, TimeSpan.Zero);
        var elapsed = DateTimeOffset.UtcNow - firstWeek;
        return elapsed < TimeSpan.Zero ? 0u : (uint)(elapsed.TotalDays / 7d) + 1u;
    }

    private static FashionSlot? NormalizeDyeSlot(uint slotId) => slotId switch
    {
        1 => FashionSlot.Weapon,
        34 => FashionSlot.Head,
        35 => FashionSlot.Body,
        37 => FashionSlot.Hands,
        36 => FashionSlot.Legs,
        38 => FashionSlot.Feet,
        _ => null,
    };

    private static string GetScoringShade(ushort icon) => icon switch
    {
        22811 or 22820 or 22817 => "白色系",
        22808 => "灰色系",
        22807 or 22816 => "黑色系",
        22805 or 22814 => "红色系",
        22809 or 22818 => "棕色系",
        22806 or 22815 => "黄色系",
        22810 or 22819 => "绿色系",
        22804 or 22813 => "蓝色系",
        22812 or 22821 => "紫色系",
        _ => "未知色系",
    };

    private static string GetSlotName(FashionSlot slot) => slot switch
    {
        FashionSlot.Weapon => "武器",
        FashionSlot.Head => "头部",
        FashionSlot.Body => "身体",
        FashionSlot.Hands => "手部",
        FashionSlot.Legs => "腿部",
        FashionSlot.Feet => "脚部",
        FashionSlot.Ears => "耳饰",
        FashionSlot.Neck => "项链",
        FashionSlot.Wrists => "手镯",
        FashionSlot.RightRing => "右戒指",
        FashionSlot.LeftRing => "左戒指",
        _ => slot.ToString(),
    };

    private enum FashionSlot : uint
    {
        Weapon,
        Head,
        Body,
        Hands,
        Legs,
        Feet,
        Ears,
        Neck,
        Wrists,
        RightRing,
        LeftRing,
    }

    private sealed class FashionData
    {
        public Dictionary<uint, Dictionary<uint, uint>> Categories { get; set; } = [];
        public Dictionary<uint, List<DyeSlotData>> WeeklyDyes { get; set; } = [];
    }

    private sealed class DyeSlotData
    {
        public uint Id { get; set; }
        public Dictionary<uint, DyeData> Dyes { get; set; } = [];
    }

    private sealed class DyeData
    {
        public ulong Count { get; set; }
        public float Pct { get; set; }
    }

    private readonly record struct ItemCandidate(uint ItemId, uint Count);
    private readonly record struct DyeCandidate(uint StainId, ulong Count, float Confidence);
    private readonly record struct ResolvedItem(Item Item, uint Count);
    private readonly record struct ResolvedDye(
        Stain Stain,
        ulong Count,
        float Confidence,
        string Shade);
}
