using Candide.Toolkit;
using ImGuiNET;
using System.Numerics;

namespace RomStar.BepInEx.UI;

internal sealed class ChineseFontImGuiWindow(string name, ImGuiWindowCreate? windowCreate)
    : ImGuiWindow(name, windowCreate)
{
    private static readonly Vector2 InitialTrainerWindowSize = new(980f, 720f);

    public override unsafe void CreateWindow()
    {
        ImFontPtr font = ChineseFontFeature.Font;
        bool hasFont = font.NativePtr != null;
        PushRomStarStyle();
        if (hasFont)
        {
            ImGui.PushFont(font);
        }

        try
        {
            ImGui.SetNextWindowSize(InitialTrainerWindowSize, ImGuiCond.FirstUseEver);
            base.CreateWindow();
        }
        finally
        {
            if (hasFont)
            {
                ImGui.PopFont();
            }
            ImGui.PopStyleColor(16);
            ImGui.PopStyleVar(6);
        }
    }

    private static void PushRomStarStyle()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16f, 14f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(9f, 6f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(9f, 7f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 3f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 2f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.105f, 0.064f, 0.041f, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.2f, 0.12f, 0.075f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.36f, 0.22f, 0.13f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.35f, 0.21f, 0.12f, 0.88f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.47f, 0.29f, 0.16f, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.61f, 0.4f, 0.2f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Tab, new Vector4(0.34f, 0.22f, 0.13f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TabHovered, new Vector4(0.48f, 0.32f, 0.18f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TabActive, new Vector4(0.02f, 0.44f, 0.48f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TitleBg, new Vector4(0.15f, 0.095f, 0.06f, 0.96f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, new Vector4(0.27f, 0.17f, 0.1f, 0.96f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.95f, 0.88f, 0.7f, 1f));
        ImGui.PushStyleColor(ImGuiCol.CheckMark, new Vector4(0.1f, 0.95f, 1f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.22f, 0.15f, 0.09f, 1f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.36f, 0.24f, 0.14f, 1f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.02f, 0.44f, 0.48f, 1f));
    }
}
