using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace IMEGarbageFix;

public sealed class Plugin : IDalamudPlugin
{
    private const long CleanupIntervalMs = 100;
    private const uint NI_COMPOSITIONSTR = 0x0015;
    private const uint CPS_CANCEL        = 0x0004;

    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    private nint gameWindow;
    private long nextCleanupAt;

    public Plugin()
    {
        gameWindow = ResolveGameWindow();
        Framework.Update += OnFrameworkUpdate;
        Log.Information("IME garbage cleanup enabled.");
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        Log.Information("IME garbage cleanup disabled.");
    }

    private unsafe void OnFrameworkUpdate(IFramework _)
    {
        var now = Environment.TickCount64;
        if (now < nextCleanupAt) return;
        nextCleanupAt = now + CleanupIntervalMs;

        var module = RaptureAtkModule.Instance();
        if (module == null || module->IsTextInputActive()) return;
        if (ImGui.GetIO().WantTextInput) return;

        if (gameWindow == nint.Zero)
        {
            gameWindow = ResolveGameWindow();
            if (gameWindow == nint.Zero) return;
        }

        var inputContext = ImmGetContext(gameWindow);
        if (inputContext == nint.Zero) return;

        try
        {
            ImmNotifyIME(inputContext, NI_COMPOSITIONSTR, CPS_CANCEL, 0);
        }
        finally
        {
            ImmReleaseContext(gameWindow, inputContext);
        }
    }

    private static nint ResolveGameWindow()
    {
        var handle = Process.GetCurrentProcess().MainWindowHandle;
        return handle != nint.Zero ? handle : FindWindow("FFXIVGAME", null);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("imm32.dll")]
    private static extern nint ImmGetContext(nint hWnd);

    [DllImport("imm32.dll")]
    private static extern bool ImmReleaseContext(nint hWnd, nint hIMC);

    [DllImport("imm32.dll")]
    private static extern bool ImmNotifyIME(nint hIMC, uint dwAction, uint dwIndex, uint dwValue);
}
