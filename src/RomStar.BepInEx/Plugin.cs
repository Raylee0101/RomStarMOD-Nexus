using BepInEx;
using BepInEx.NET.Common;
using HarmonyLib;
using RomStar.BepInEx.Runtime;
using RomStar.BepInEx.Trainer;

namespace RomStar.BepInEx;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "romstar.core";
    public const string PluginName = "RomStar Core";
    public const string PluginVersion = "0.2.66";

    private RomStarRuntimeHost? runtimeHost;
    private Harmony? harmony;

    public override void Load()
    {
        Log.LogInfo("RomStar Core loaded through BepInEx 6 CoreCLR.");

        try
        {
            HotkeyConfig.Configure(Config);
            TrainerWindow.Configure(Config);
            harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            runtimeHost = new RomStarRuntimeHost(Log);
            runtimeHost.Start();
        }
        catch (Exception ex)
        {
            Log.LogError("RomStar startup failed. The plugin was disabled so the game can continue.");
            Log.LogError(ex);
            runtimeHost = null;
        }
    }

    public override bool Unload()
    {
        try
        {
            runtimeHost?.Stop();
            harmony?.UnpatchSelf();
        }
        catch (Exception ex)
        {
            Log.LogWarning($"RomStar unload failed: {ex}");
        }

        return true;
    }
}
