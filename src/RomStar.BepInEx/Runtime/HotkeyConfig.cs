using BepInEx.Configuration;
using RomStar.BepInEx.Input;

namespace RomStar.BepInEx.Runtime;

internal readonly record struct HotkeyBinding(int Key, int Modifiers);

internal static class HotkeyConfig
{
    private static readonly int[] FunctionKeys = { 112, 113, 114, 115, 116, 117, 118, 119, 120, 121, 122, 123 };
    private static ConfigEntry<int>? trainerHotkeyConfig;
    private static ConfigEntry<int>? trainerHotkeyModifiersConfig;
    private static ConfigEntry<bool>? trainerF1MigrationConfig;
    private static ConfigEntry<int>? debugHotkeyConfig;
    private static ConfigEntry<int>? debugHotkeyModifiersConfig;
    private static ConfigEntry<int>? controlHotkeyConfig;
    private static ConfigEntry<int>? controlHotkeyModifiersConfig;

    public static int TrainerHotkey => TrainerBinding.Key;
    public static int TrainerHotkeyModifiers => TrainerBinding.Modifiers;
    public static int DebugHotkey => DebugBinding.Key;
    public static int DebugHotkeyModifiers => DebugBinding.Modifiers;
    public static int ControlHotkey => ControlBinding.Key;
    public static int ControlHotkeyModifiers => ControlBinding.Modifiers;
    public static HotkeyBinding TrainerBinding => GetBinding(trainerHotkeyConfig, trainerHotkeyModifiersConfig, 112);
    public static HotkeyBinding DebugBinding => GetBinding(debugHotkeyConfig, debugHotkeyModifiersConfig, 120);
    public static HotkeyBinding ControlBinding => GetBinding(controlHotkeyConfig, controlHotkeyModifiersConfig, 121);

    public static void Configure(ConfigFile config)
    {
        trainerHotkeyConfig = config.Bind("Hotkeys", "Trainer", 112, "Trainer window hotkey virtual-key code. F1=112, F2=113 ... F12=123.");
        trainerHotkeyModifiersConfig = config.Bind("Hotkeys", "TrainerModifiers", 0, "Trainer hotkey modifiers. Ctrl=1, Alt=2, Shift=4; combine by adding values.");
        trainerF1MigrationConfig = config.Bind("Hotkeys", "TrainerDefaultF1MigrationDone", false, "Internal migration flag for switching the default trainer hotkey from F2 back to F1.");
        debugHotkeyConfig = config.Bind("Hotkeys", "ChineseDebug", 120, "Chinese debug bridge hotkey virtual-key code. F1=112, F2=113 ... F12=123.");
        debugHotkeyModifiersConfig = config.Bind("Hotkeys", "ChineseDebugModifiers", 0, "Chinese debug hotkey modifiers. Ctrl=1, Alt=2, Shift=4; combine by adding values.");
        controlHotkeyConfig = config.Bind("Hotkeys", "ControlConsole", 121, "RomStar control console hotkey virtual-key code. F1=112, F2=113 ... F12=123.");
        controlHotkeyModifiersConfig = config.Bind("Hotkeys", "ControlConsoleModifiers", 0, "Control console hotkey modifiers. Ctrl=1, Alt=2, Shift=4; combine by adding values.");
        MigrateTrainerDefaultToF1();
        SetBinding(trainerHotkeyConfig, trainerHotkeyModifiersConfig, TrainerBinding, 112);
        SetBinding(debugHotkeyConfig, debugHotkeyModifiersConfig, DebugBinding, 120);
        SetBinding(controlHotkeyConfig, controlHotkeyModifiersConfig, ControlBinding, 121);
    }

    public static int GetFunctionKeyIndex(int virtualKey)
    {
        int normalized = NormalizeFunctionKey(virtualKey);
        for (int i = 0; i < FunctionKeys.Length; i++)
        {
            if (FunctionKeys[i] == normalized)
            {
                return i;
            }
        }

        return 0;
    }

    public static string KeyName(int virtualKey)
    {
        return virtualKey switch
        {
            >= 112 and <= 123 => "F" + (virtualKey - 111),
            8 => "Backspace",
            9 => "Tab",
            13 => "Enter",
            20 => "CapsLock",
            27 => "Esc",
            32 => "Space",
            33 => "PageUp",
            34 => "PageDown",
            35 => "End",
            36 => "Home",
            37 => "Left",
            38 => "Up",
            39 => "Right",
            40 => "Down",
            45 => "Insert",
            46 => "Delete",
            >= 48 and <= 57 => ((char)virtualKey).ToString(),
            >= 65 and <= 90 => ((char)virtualKey).ToString(),
            _ => "VK" + virtualKey
        };
    }

    public static string HotkeyName(HotkeyBinding binding)
    {
        List<string> parts = new();
        if ((binding.Modifiers & NativeInput.ModifierCtrl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((binding.Modifiers & NativeInput.ModifierAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((binding.Modifiers & NativeInput.ModifierShift) != 0)
        {
            parts.Add("Shift");
        }

        parts.Add(KeyName(binding.Key));
        return string.Join(" + ", parts);
    }

    public static int FunctionKeyFromIndex(int index)
    {
        return FunctionKeys[Math.Clamp(index, 0, FunctionKeys.Length - 1)];
    }

    public static void SetTrainerHotkey(int virtualKey)
    {
        SetTrainerHotkey(new HotkeyBinding(virtualKey, TrainerHotkeyModifiers));
    }

    public static void SetTrainerHotkey(HotkeyBinding binding)
    {
        SetBinding(trainerHotkeyConfig, trainerHotkeyModifiersConfig, binding, 112);
    }

    public static void SetDebugHotkey(int virtualKey)
    {
        SetDebugHotkey(new HotkeyBinding(virtualKey, DebugHotkeyModifiers));
    }

    public static void SetDebugHotkey(HotkeyBinding binding)
    {
        SetBinding(debugHotkeyConfig, debugHotkeyModifiersConfig, binding, 120);
    }

    public static void SetControlHotkey(int virtualKey)
    {
        SetControlHotkey(new HotkeyBinding(virtualKey, ControlHotkeyModifiers));
    }

    public static void SetControlHotkey(HotkeyBinding binding)
    {
        SetBinding(controlHotkeyConfig, controlHotkeyModifiersConfig, binding, 121);
    }

    private static HotkeyBinding GetBinding(ConfigEntry<int>? keyConfig, ConfigEntry<int>? modifiersConfig, int defaultKey)
    {
        return new HotkeyBinding(
            NormalizeVirtualKey(keyConfig?.Value ?? defaultKey, defaultKey),
            NormalizeModifiers(modifiersConfig?.Value ?? 0));
    }

    private static void SetBinding(ConfigEntry<int>? keyConfig, ConfigEntry<int>? modifiersConfig, HotkeyBinding binding, int fallback)
    {
        if (keyConfig != null)
        {
            keyConfig.Value = NormalizeVirtualKey(binding.Key, fallback);
        }

        if (modifiersConfig != null)
        {
            modifiersConfig.Value = NormalizeModifiers(binding.Modifiers);
        }
    }

    private static void MigrateTrainerDefaultToF1()
    {
        if (trainerHotkeyConfig == null || trainerHotkeyModifiersConfig == null || trainerF1MigrationConfig == null)
        {
            return;
        }

        if (!trainerF1MigrationConfig.Value && trainerHotkeyConfig.Value == 113 && trainerHotkeyModifiersConfig.Value == 0)
        {
            trainerHotkeyConfig.Value = 112;
        }

        trainerF1MigrationConfig.Value = true;
    }

    private static int NormalizeVirtualKey(int virtualKey, int fallback)
    {
        return virtualKey is >= 8 and <= 254 && !IsModifierKey(virtualKey) ? virtualKey : fallback;
    }

    private static int NormalizeModifiers(int modifiers)
    {
        return modifiers & (NativeInput.ModifierCtrl | NativeInput.ModifierAlt | NativeInput.ModifierShift);
    }

    private static bool IsModifierKey(int virtualKey)
    {
        return virtualKey is 16 or 17 or 18 or 160 or 161 or 162 or 163 or 164 or 165;
    }

    private static int NormalizeFunctionKey(int virtualKey)
    {
        return virtualKey is >= 112 and <= 123 ? virtualKey : 113;
    }
}
