using System.Runtime.InteropServices;

namespace RomStar.BepInEx.Input;

internal static class NativeInput
{
    public const int ModifierCtrl = 1;
    public const int ModifierAlt = 2;
    public const int ModifierShift = 4;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public static bool IsPressedOnce(int vKey, ref bool wasDown)
    {
        bool isDown = (GetAsyncKeyState(vKey) & 0x8000) != 0;
        bool pressed = isDown && !wasDown;
        wasDown = isDown;
        return pressed;
    }

    public static bool IsDown(int vKey)
    {
        return (GetAsyncKeyState(vKey) & 0x8000) != 0;
    }

    public static bool IsHotkeyPressedOnce(int vKey, int modifiers, ref bool wasDown)
    {
        bool isDown = IsDown(vKey) && ModifiersMatch(modifiers);
        bool pressed = isDown && !wasDown;
        wasDown = isDown;
        return pressed;
    }

    public static bool TryGetPressedKeyboardKey(out int vKey)
    {
        for (int i = 8; i <= 254; i++)
        {
            if (IsModifierKey(i))
            {
                continue;
            }

            if (IsDown(i))
            {
                vKey = i;
                return true;
            }
        }

        vKey = 0;
        return false;
    }

    public static int CurrentModifiers()
    {
        int modifiers = 0;
        if (ModifierDown(ModifierCtrl))
        {
            modifiers |= ModifierCtrl;
        }

        if (ModifierDown(ModifierAlt))
        {
            modifiers |= ModifierAlt;
        }

        if (ModifierDown(ModifierShift))
        {
            modifiers |= ModifierShift;
        }

        return modifiers;
    }

    private static bool ModifiersMatch(int modifiers)
    {
        return ModifierDown(ModifierCtrl) == ((modifiers & ModifierCtrl) != 0) &&
            ModifierDown(ModifierAlt) == ((modifiers & ModifierAlt) != 0) &&
            ModifierDown(ModifierShift) == ((modifiers & ModifierShift) != 0);
    }

    private static bool ModifierDown(int modifier)
    {
        return modifier switch
        {
            ModifierCtrl => IsDown(17) || IsDown(162) || IsDown(163),
            ModifierAlt => IsDown(18) || IsDown(164) || IsDown(165),
            ModifierShift => IsDown(16) || IsDown(160) || IsDown(161),
            _ => false
        };
    }

    private static bool IsModifierKey(int vKey)
    {
        return vKey is 16 or 17 or 18 or 160 or 161 or 162 or 163 or 164 or 165;
    }
}
