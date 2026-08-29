using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;

namespace KeitaToolbox;

public sealed partial class Plugin
{
    private const string FloatingButtonIconResource = "KeitaToolbox.icon.png";

    private ISharedImmediateTexture? floatingButtonIcon;
    private bool floatingButtonDragging;
    private bool floatingButtonTextureErrorLogged;

    private void DrawFloatingButton()
    {
        if (!Config.DisclaimerAccepted || !Config.Interface.ShowFloatingButton)
        {
            floatingButtonDragging = false;
            return;
        }

        var scale = Math.Clamp(ImGui.GetFontSize() / 17f, 0.85f, 1.65f);
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(
            viewport.WorkPos + new Vector2(viewport.WorkSize.X - 84f * scale, 160f * scale),
            ImGuiCond.FirstUseEver);

        var visible = ImGui.Begin(
            "Keita 工具箱悬浮按钮###KeitaToolboxFloatingButton",
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoFocusOnAppearing);
        try
        {
            if (!visible)
                return;

            var buttonSize = new Vector2(32.2f, 32.2f) * scale;
            var clicked = DrawFloatingButtonIcon(buttonSize);
            if (clicked)
                windowOpen = !windowOpen;

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    windowOpen
                        ? "左键关闭设置 · 右键拖动位置"
                        : "左键打开设置 · 右键拖动位置");
            }

            if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                floatingButtonDragging = true;
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Right))
                floatingButtonDragging = false;

            if (floatingButtonDragging)
            {
                ImGui.SetWindowPos(
                    ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta,
                    ImGuiCond.Always);
            }
        }
        finally
        {
            ImGui.End();
        }
    }

    private bool DrawFloatingButtonIcon(Vector2 size)
    {
        try
        {
            floatingButtonIcon ??= TextureProvider.GetFromManifestResource(
                typeof(Plugin).Assembly,
                FloatingButtonIconResource);
            if (floatingButtonIcon.TryGetWrap(out var texture, out var error))
            {
                ImGui.Image(texture.Handle, size);
                return ImGui.IsItemClicked(ImGuiMouseButton.Left);
            }

            if (error != null && !floatingButtonTextureErrorLogged)
            {
                floatingButtonTextureErrorLogged = true;
                Log.Warning(error, "Failed to load the floating button icon.");
            }
        }
        catch (Exception ex)
        {
            if (!floatingButtonTextureErrorLogged)
            {
                floatingButtonTextureErrorLogged = true;
                Log.Warning(ex, "Failed to initialize the floating button icon.");
            }
        }

        return ImGui.Button("K##KeitaToolboxFloatingButtonFallback", size);
    }
}
