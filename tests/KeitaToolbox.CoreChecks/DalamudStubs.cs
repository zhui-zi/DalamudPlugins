namespace Dalamud.Configuration
{
    public interface IPluginConfiguration
    {
        int Version { get; set; }
    }
}

namespace Dalamud.Game.Text
{
    public enum XivChatType
    {
        Shout,
    }
}

namespace Dalamud.Plugin
{
    public interface IDalamudPluginInterface
    {
        void SavePluginConfig(object configuration);
    }
}
