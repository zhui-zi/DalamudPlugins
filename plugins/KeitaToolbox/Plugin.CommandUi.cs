namespace KeitaToolbox;

public sealed partial class Plugin
{
    internal static void DrawCommandHelp(params string[] commands) =>
        DrawHelp($"命令：{string.Join("\n命令：", commands)}");

    private static void DrawHelpWithCommand(string description, string command)
    {
        DrawHelp(description);
        DrawCommandHelp(command);
    }
}
