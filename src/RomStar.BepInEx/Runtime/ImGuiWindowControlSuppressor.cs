using Candide.Toolkit;
using HarmonyLib;
using RomStar.BepInEx.Interop;

namespace RomStar.BepInEx.Runtime;

internal static class ImGuiWindowControlSuppressor
{
    public static bool SuppressVanillaWindowControl { get; set; }
}

[HarmonyPatch(typeof(ImGuiWindowControl), nameof(ImGuiWindowControl.CreateWindows))]
internal static class ImGuiWindowControlSuppressorPatch
{
    private static bool Prefix()
    {
        if (!ImGuiWindowControlSuppressor.SuppressVanillaWindowControl)
        {
            return true;
        }

        List<ImGuiWindow>? windows = ImGuiWindowRegistrar.GetWindows();
        if (windows == null)
        {
            return true;
        }

        foreach (ImGuiWindow window in windows.ToArray())
        {
            if (window.Enabled)
            {
                window.CreateWindow();
            }
        }

        return false;
    }
}
