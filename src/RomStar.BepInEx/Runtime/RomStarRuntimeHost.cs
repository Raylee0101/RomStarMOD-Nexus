using System.Threading;
using BepInEx.Logging;
using Candide;
using Candide.Toolkit;
using HarmonyLib;
using RomStar.BepInEx.Input;
using RomStar.BepInEx.Interop;
using RomStar.BepInEx.Trainer;
using RomStar.BepInEx.UI;

namespace RomStar.BepInEx.Runtime;

internal sealed class RomStarRuntimeHost
{
    private const int AttachRetryDelayMs = 25;
    private const int AttachAttempts = 12000;
    private const int Escape = 27;

    private static RomStarRuntimeHost? activeHost;

    private readonly ManualLogSource log;
    private readonly ImGuiWindowRegistrar registrar = new();
    private readonly TrainerModule trainerModule = new();
    private readonly Thread worker;
    private readonly DateTime startedAt = DateTime.UtcNow;
    private volatile bool running;
    private ImGuiWindow? controlWindow;
    private bool f10WasDown;
    private bool trainerHotkeyWasDown;
    private bool trainerToggleQueuedBeforeRegistration;
    private bool directTrainerHookRegistered;
    private bool firstTrainerDirectDrawLogged;
    private bool escapeWasDown;

    public RomStarRuntimeHost(ManualLogSource log)
    {
        this.log = log;
        activeHost = this;
        worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "RomStar BepInEx runtime"
        };
    }

    public void Start()
    {
        running = true;
        worker.Start();
    }

    public void Stop()
    {
        running = false;
        if (activeHost == this)
        {
            activeHost = null;
        }
    }

    public static void DrawTrainerDirectFromGameHook()
    {
        activeHost?.DrawTrainerDirectHook();
    }

    private void WorkerLoop()
    {
        log.LogInfo("RomStar runtime host waiting for Romestead ImGui.");

        for (int i = 0; running && i < AttachAttempts; i++)
        {
            try
            {
                QueueTrainerToggleBeforeRegistration();
                TrainerWindow.TickAlways();
                if (TrainerWindow.ConsumeTrainerCloseRequest())
                {
                    trainerModule.Close();
                    UpdateGlobalImGuiEnabled();
                    log.LogInfo("Close RomStar trainer shell by trainer request while waiting for auxiliary windows.");
                }

                if (Globals.Game != null && Globals.ImGuiRenderer != null && TryAttachToRomesteadImGui())
                {
                    log.LogInfo($"RomStar runtime attached after {ElapsedMilliseconds()} ms.");
                    FlushQueuedTrainerToggle();
                    break;
                }
            }
            catch (Exception ex)
            {
                log.LogWarning($"Attach attempt failed: {ex.Message}");
            }

            Thread.Sleep(AttachRetryDelayMs);
        }

        while (running)
        {
            try
            {
                if (controlWindow != null && NativeInput.IsHotkeyPressedOnce(HotkeyConfig.ControlHotkey, HotkeyConfig.ControlHotkeyModifiers, ref f10WasDown))
                {
                    controlWindow.Enabled = !controlWindow.Enabled;
                    UpdateGlobalImGuiEnabled();
                    log.LogInfo($"Toggle RomStar BepInEx control window: {controlWindow.Enabled}");
                }

                TrainerWindow.TickAlways();
                if (TrainerWindow.ConsumeTrainerCloseRequest())
                {
                    trainerModule.Close();
                    UpdateGlobalImGuiEnabled();
                    log.LogInfo("Close RomStar trainer shell by trainer request.");
                }

            }
            catch (Exception ex)
            {
                log.LogWarning($"Runtime tick failed: {ex.Message}");
            }

            Thread.Sleep(50);
        }
    }

    private bool TryAttachToRomesteadImGui()
    {
        if (!TryRegisterAuxiliaryWindows())
        {
            return false;
        }

        if (!directTrainerHookRegistered)
        {
            directTrainerHookRegistered = true;
            log.LogInfo($"RomStar direct trainer BeforeLayout patch ready after {ElapsedMilliseconds()} ms.");
        }

        return true;
    }

    private bool TryRegisterAuxiliaryWindows()
    {
        controlWindow = new ImGuiWindow(RomStarMainWindow.Name, RomStarMainWindow.Draw)
        {
            Enabled = false
        };

        return registrar.TryRegister(controlWindow);
    }

    private void DrawTrainerDirectHook()
    {
        try
        {
            if (NativeInput.IsHotkeyPressedOnce(HotkeyConfig.TrainerHotkey, HotkeyConfig.TrainerHotkeyModifiers, ref trainerHotkeyWasDown))
            {
                trainerModule.Toggle();
                UpdateGlobalImGuiEnabled();
                log.LogInfo($"Toggle RomStar trainer shell from direct hook after {ElapsedMilliseconds()} ms: {trainerModule.IsVisible}");
            }

            if (NativeInput.IsPressedOnce(Escape, ref escapeWasDown) && trainerModule.IsVisible)
            {
                trainerModule.Close();
                UpdateGlobalImGuiEnabled();
                log.LogInfo("Close RomStar trainer shell with Escape from direct hook.");
            }

            if (trainerModule.IsVisible)
            {
                trainerModule.DrawDirect();
                RecordFirstTrainerDirectDraw();
            }
        }
        catch (Exception ex)
        {
            log.LogWarning($"RomStar direct trainer hook failed: {ex.Message}");
        }
    }

    private void QueueTrainerToggleBeforeRegistration()
    {
        if (NativeInput.IsHotkeyPressedOnce(HotkeyConfig.TrainerHotkey, HotkeyConfig.TrainerHotkeyModifiers, ref trainerHotkeyWasDown))
        {
            trainerToggleQueuedBeforeRegistration = true;
            log.LogInfo("F1 pressed before RomStar windows were registered; queued trainer open.");
        }
    }

    private void FlushQueuedTrainerToggle()
    {
        if (!trainerToggleQueuedBeforeRegistration)
        {
            return;
        }

        trainerToggleQueuedBeforeRegistration = false;
        trainerModule.Toggle();
        UpdateGlobalImGuiEnabled();
        log.LogInfo($"Flush queued RomStar trainer shell toggle: {trainerModule.IsVisible}");
    }

    private void RecordFirstTrainerDirectDraw()
    {
        if (firstTrainerDirectDrawLogged)
        {
            return;
        }

        firstTrainerDirectDrawLogged = true;
        log.LogInfo($"RomStar trainer first direct draw after {ElapsedMilliseconds()} ms.");
    }

    private long ElapsedMilliseconds()
    {
        return (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
    }

    private static bool IsLegacyChineseDebugLoaded()
    {
        return AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
            string.Equals(assembly.GetName().Name, "RomesteadChineseDebugMod", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLegacyTrainerLoaded()
    {
        return AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
            string.Equals(assembly.GetName().Name, "RomesteadTrainerMod", StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateGlobalImGuiEnabled()
    {
        bool controlVisible = controlWindow?.Enabled ?? false;
        ImGuiWindowControlSuppressor.SuppressVanillaWindowControl = !controlVisible &&
            trainerModule.IsVisible;
        Globals.ImGuiEnabled = controlVisible || trainerModule.IsVisible;
    }
}

[HarmonyPatch(typeof(ImGuiRenderer), nameof(ImGuiRenderer.BeforeLayout))]
internal static class RomStarDirectTrainerBeforeLayoutPatch
{
    private static void Postfix()
    {
        RomStarRuntimeHost.DrawTrainerDirectFromGameHook();
    }
}
