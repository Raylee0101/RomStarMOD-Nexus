using ImGuiNET;

namespace RomStar.BepInEx.UI;

internal static class RomStarMainWindow
{
    public const string Name = "RomStar BepInEx Control";

    public static void Draw()
    {
        ImGui.TextWrapped("RomStar loaded through BepInEx 6 CoreCLR.");
        ImGui.Separator();
        ImGui.TextWrapped("RomStar trainer uses F1 by default. The hotkey can be changed from the Basic page.");
        ImGui.Spacing();
        ImGui.TextWrapped("Internal tools: F10 control console, F9 Chinese debug bridge.");
    }
}
