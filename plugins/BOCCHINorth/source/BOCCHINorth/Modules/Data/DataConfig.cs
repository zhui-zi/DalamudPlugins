using Ocelot.Config.Attributes;
using Ocelot.Modules;

namespace BOCCHI.Modules.Data;

[Text("config.explanation")]
public class DataConfig : ModuleConfig
{
    [Checkbox]
    [Label("generic.label.enabled")]
    public bool Enabled { get; set; } = false;
}
