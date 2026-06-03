using Candide.Toolkit;
using RomStar.BepInEx.Interop;
using RomStar.BepInEx.UI;

namespace RomStar.BepInEx.Trainer;

internal sealed class TrainerModule
{
    public const string WindowName = "RomStar Trainer";

    private readonly ChineseFontImGuiWindow trainerWindow = new(WindowName, TrainerWindow.Draw)
    {
        Enabled = false
    };

    public bool IsVisible => trainerWindow.Enabled;

    public bool TryRegister(ImGuiWindowRegistrar registrar)
    {
        return registrar.TryRegister(trainerWindow);
    }

    public void Toggle()
    {
        trainerWindow.Enabled = !trainerWindow.Enabled;
    }

    public void Close()
    {
        trainerWindow.Enabled = false;
    }

    public void DrawDirect()
    {
        if (trainerWindow.Enabled)
        {
            trainerWindow.CreateWindow();
        }
    }
}
