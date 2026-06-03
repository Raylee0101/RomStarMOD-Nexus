using System.Reflection;
using Candide.Toolkit;

namespace RomStar.BepInEx.Interop;

internal sealed class ImGuiWindowRegistrar
{
    private const string WindowListFieldName = "ImGuiWindows";

    public static List<ImGuiWindow>? GetWindows()
    {
        return typeof(ImGuiWindowControl).GetField(WindowListFieldName, BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetValue(null) as List<ImGuiWindow>;
    }

    public bool TryRegister(ImGuiWindow window)
    {
        if (GetWindows() is not { } windows)
        {
            return false;
        }

        windows.RemoveAll(existing => existing.Name == window.Name);
        windows.Add(window);
        return true;
    }
}
