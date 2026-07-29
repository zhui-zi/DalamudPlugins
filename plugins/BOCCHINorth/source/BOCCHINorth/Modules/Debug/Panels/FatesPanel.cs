using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BOCCHI.Data;
using BOCCHI.Modules.Teleporter;
using ECommons.DalamudServices;
using Dalamud.Bindings.ImGui;
using Lumina.Data.Files;
using Lumina.Excel.Sheets;
using Ocelot.Ui;

namespace BOCCHI.Modules.Debug.Panels;

public class FatesPanel : Panel
{
    public Dictionary<uint, Vector3> FateLocations = [];

    public FatesPanel()
    {
        ProcessLgbData(Svc.ClientState.TerritoryType);
    }

    public void ProcessLgbData(uint id)
    {
        if (id == 0)
        {
            return;
        }

        FateLocations.Clear();

        var territorySheet = Svc.Data.GetExcelSheet<TerritoryType>();
        var territoryRow = territorySheet?.GetRow(id);
        if (territoryRow == null)
        {
            Svc.Log.Error($"Could not load TerritoryType for ID {id}");
            return;
        }

        Dictionary<uint, uint> locations = [];
        foreach (var fate in EventData.GetFatesForTerritory(id))
        {
            var fateRow = Svc.Data.GetExcelSheet<Fate>().FirstOrDefault(f => f.RowId == fate.Id);
            locations[fate.Id] = fateRow.Location;
        }


        var bg = territoryRow?.Bg.ExtractText();
        var lgbFileName = "bg/" + bg![..(bg!.IndexOf("/level/", StringComparison.Ordinal) + 1)] + "level/planevent.lgb";
        var lgb = Svc.Data.GetFile<LgbFile>(lgbFileName);
        foreach (var layer in lgb?.Layers ?? [])
        {
            foreach (var instanceObject in layer.InstanceObjects)
            {
                if (locations.ContainsValue(instanceObject.InstanceId))
                {
                    var fateId = locations.First(kv => kv.Value == instanceObject.InstanceId).Key;
                    var transform = instanceObject.Transform;
                    var pos = transform.Translation;
                    FateLocations[fateId] = new Vector3(pos.X, pos.Y, pos.Z);
                }
            }
        }
    }

    public override string GetName()
    {
        return "Fates";
    }

    public override void Render(DebugModule module)
    {
        OcelotUi.Title("Fates:");
        OcelotUi.Indent(() =>
        {
            var fates = EventData.GetFatesForTerritory(Svc.ClientState.TerritoryType).ToList();
            foreach (var data in fates)
            {
                ImGui.TextUnformatted(data.InternalName);

                if (module.TryGetModule<TeleporterModule>(out var teleporter) && teleporter!.IsReady())
                {
                    var start = FateLocations.GetValueOrDefault(data.Id, data.StartPosition ?? Vector3.Zero);

                    teleporter.teleporter.Button(data.Aethernet, start, data.InternalName, $"fate_{data.Id}", data);
                }

                OcelotUi.Indent(() => EventIconRenderer.Drops(data, module.PluginConfig.EventDropConfig));

                if (data.Id != fates.Max(fate => fate.Id))
                {
                    OcelotUi.VSpace();
                }
            }
        });
    }

    public override void OnTerritoryChanged(uint id, DebugModule module)
    {
        ProcessLgbData(id);
    }
}
