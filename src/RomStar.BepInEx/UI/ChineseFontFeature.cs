using BepInEx.Logging;
using Candide;
using Candide.GameModels.Systems;
using ImGuiNET;

namespace RomStar.BepInEx.UI;

internal static class ChineseFontFeature
{
    private static bool fontLoaded;
    private static bool fontLoadQueued;
    private static ImFontPtr chineseFont;

    public static ImFontPtr Font => chineseFont;
    public static bool IsLoaded => fontLoaded;

    public static void EnsureLoaded(ManualLogSource log)
    {
        if (fontLoaded || fontLoadQueued || Globals.ImGuiRenderer == null)
        {
            return;
        }

        fontLoadQueued = true;
        DebugSystem.Queue((Action)(() => LoadOnGameThread(log)));
    }

    private static unsafe void LoadOnGameThread(ManualLogSource log)
    {
        if (fontLoaded)
        {
            return;
        }

        try
        {
            string fontPath = Path.Combine(AppContext.BaseDirectory, "Content", "fonts", "LXGWWenKaiGB-Regular.ttf");
            if (!File.Exists(fontPath))
            {
                fontPath = Path.Combine(AppContext.BaseDirectory, "Content", "fonts", "SIMSUN.ttf");
            }

            if (!File.Exists(fontPath))
            {
                log.LogWarning("RomStar Chinese font not found.");
                return;
            }

            ImGuiIOPtr io = ImGui.GetIO();
            ImFontAtlasPtr fonts = io.Fonts;
            ImFontConfigPtr config = (ImFontConfigPtr)(ImFontConfig*)null;
            ImFontPtr font = fonts.AddFontFromFileTTF(fontPath, 18f, config, fonts.GetGlyphRangesChineseFull());
            if (font.NativePtr == null)
            {
                log.LogWarning("RomStar failed to add Chinese font.");
                return;
            }

            Globals.ImGuiRenderer.RebuildFontAtlas();
            chineseFont = font;
            fontLoaded = true;
            log.LogInfo("RomStar Chinese font loaded: " + fontPath);
        }
        catch (Exception ex)
        {
            log.LogWarning("RomStar Chinese font load failed: " + ex);
        }
    }
}
