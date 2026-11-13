#nullable enable

namespace Knightfall;

// One pad as far as the rest of the mod can tell. Buttons from every connected controller are
// merged, and the last one pressed decides whether hints use Xbox or PlayStation names.
static class Pad
{
    [Flags]
    public enum Button : ushort
    {
        None = 0,
        DPadUp = 0x0001,
        DPadDown = 0x0002,
        DPadLeft = 0x0004,
        DPadRight = 0x0008,
        Start = 0x0010,
        Back = 0x0020,
        LeftStick = 0x0040,
        RightStick = 0x0080,
        LB = 0x0100,
        RB = 0x0200,

        // XInput leaves these two bits unused. The triggers are axes, so they are read as
        // buttons once they are pressed past halfway.
        LT = 0x0400,
        RT = 0x0800,

        A = 0x1000,
        B = 0x2000,
        X = 0x4000,
        Y = 0x8000,
    }

    public enum Style
    {
        Xbox,
        PlayStation,
    }

    static readonly Button[] Singles =
    {
        Button.DPadUp, Button.DPadDown, Button.DPadLeft, Button.DPadRight, Button.Start, Button.Back,
        Button.LeftStick, Button.RightStick, Button.LB, Button.RB, Button.LT, Button.RT,
        Button.A, Button.B, Button.X, Button.Y,
    };

    static readonly Dictionary<Button, string[]> Engine = new();

    static readonly Dictionary<string, Button> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = Button.A, ["B"] = Button.B, ["X"] = Button.X, ["Y"] = Button.Y,
        ["Cross"] = Button.A, ["Circle"] = Button.B, ["Square"] = Button.X, ["Triangle"] = Button.Y,
        ["LB"] = Button.LB, ["RB"] = Button.RB, ["L1"] = Button.LB, ["R1"] = Button.RB,
        ["LT"] = Button.LT, ["RT"] = Button.RT, ["L2"] = Button.LT, ["R2"] = Button.RT,
        ["LeftStick"] = Button.LeftStick, ["RightStick"] = Button.RightStick,
        ["LS"] = Button.LeftStick, ["RS"] = Button.RightStick, ["L3"] = Button.LeftStick, ["R3"] = Button.RightStick,
        ["Back"] = Button.Back, ["Start"] = Button.Start,
        ["Share"] = Button.Back, ["Create"] = Button.Back, ["Options"] = Button.Start,
        ["DPadUp"] = Button.DPadUp, ["DPadDown"] = Button.DPadDown, ["DPadLeft"] = Button.DPadLeft, ["DPadRight"] = Button.DPadRight,
    };

    static Button _held;
    static Style _lastUsed;
    static bool _xinputOff;
    static bool _sonyOff;

    // Kept as buttons under [pad] in settings.toml, next to the pad's other settings.
    public static readonly Choice Prompts = new(
        "pad.buttons", "BUTTON PROMPTS",
        "The controller button names shown in hints. AUTO follows the controller you used last.",
        new[] { ("auto", "AUTO"), ("xbox", "XBOX"), ("playstation", "PLAYSTATION") })
    {
        Preference = true,
        Changed = Hotkeys.Relabel,
    };

    public static bool Connected => XInputPad.Connected || SonyPad.Connected;

    public static Style Look => Prompts.Index switch
    {
        1 => Style.Xbox,
        2 => Style.PlayStation,
        _ => _lastUsed,
    };

    // Every button down right now.
    public static Button Held => _held;

    // The right stick, -1 to 1 with right and down positive, while the game is in front.
    public static System.Numerics.Vector2 Right => GameWindow.InFront ? Clamp(XInputPad.Right.LengthSquared() > SonyPad.Right.LengthSquared() ? XInputPad.Right : SonyPad.Right) : default;

    static System.Numerics.Vector2 Clamp(System.Numerics.Vector2 stick) =>
        stick.LengthSquared() > 1f ? System.Numerics.Vector2.Normalize(stick) : stick;

    public static bool IsDown(Button buttons)
    {
        return buttons != Button.None && (_held & buttons) == buttons;
    }

    public static void Poll()
    {
        var xbox = ReadXInput();
        var sony = ReadSony();

        if (sony != Button.None)
        {
            _lastUsed = Style.PlayStation;
        }
        else if (xbox != Button.None)
        {
            _lastUsed = Style.Xbox;
        }
        else if (SonyPad.Connected != XInputPad.Connected)
        {
            _lastUsed = SonyPad.Connected ? Style.PlayStation : Style.Xbox;
        }

        // Both readers report buttons even when another window is in front. They count there
        // only when the game is taking the controller there too.
        var held = xbox | sony;
        _held = held != Button.None && (GameWindow.InFront || GameTweaks.PadInBackground.On) ? held : Button.None;
    }

    public static void Stop()
    {
        _held = Button.None;
        XInputPad.Stop();
        SonyPad.Stop();
    }

    // A reader that fails is switched off for the session. Keyboard input must not go down with it.
    static Button ReadXInput()
    {
        if (_xinputOff)
        {
            return Button.None;
        }

        try
        {
            return XInputPad.Read();
        }
        catch (Exception error)
        {
            _xinputOff = true;
            XInputPad.Stop();
            Log.Error("pad.xinput", "Xbox controller support is off", error);
            return Button.None;
        }
    }

    static Button ReadSony()
    {
        if (_sonyOff)
        {
            return Button.None;
        }

        try
        {
            return SonyPad.Read();
        }
        catch (Exception error)
        {
            _sonyOff = true;
            SonyPad.Stop();
            Log.Error("pad.sony", "Direct PlayStation controller support is off", error);
            return Button.None;
        }
    }

    public static bool TryParse(string text, out Button buttons)
    {
        buttons = Button.None;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // The names the menu shows are read too, like "D-PAD UP".
            if (!Names.TryGetValue(part, out var button) && !Names.TryGetValue(part.Replace("-", "").Replace(" ", ""), out button))
            {
                return false;
            }

            buttons |= button;
        }

        return true;
    }

    // The names settings.toml keeps, which TryParse reads back whatever pad is in use.
    public static string Stored(Button buttons)
    {
        return string.Join("+", Singles.Where(single => (buttons & single) != 0).Select(StoredName));
    }

    static string StoredName(Button button)
    {
        return button switch
        {
            Button.A => "A",
            Button.B => "B",
            Button.X => "X",
            Button.Y => "Y",
            Button.LB => "LB",
            Button.RB => "RB",
            Button.LT => "LT",
            Button.RT => "RT",
            Button.LeftStick => "LS",
            Button.RightStick => "RS",
            Button.Back => "Back",
            Button.Start => "Start",
            Button.DPadUp => "DPadUp",
            Button.DPadDown => "DPadDown",
            Button.DPadLeft => "DPadLeft",
            Button.DPadRight => "DPadRight",
            _ => "",
        };
    }

    static readonly Dictionary<(Button, Style), string> Described = new();
    static readonly Dictionary<(string, Button, Button, Style), string> Hints = new();

    // "L3+R3" or "LS+RS", depending on the pad in use. HUDs ask every frame, so each answer is
    // made once.
    public static string Describe(Button buttons)
    {
        if (buttons == Button.None)
        {
            return "off";
        }

        var key = (buttons, Look);
        if (!Described.TryGetValue(key, out var text))
        {
            text = string.Join("+", Singles.Where(single => (buttons & single) != 0).Select(Name));
            Described[key] = text;
        }

        return text;
    }

    // A line of text with button names in it, like "{0} SHOOT · {1} GAS", made once per pad style.
    public static string Hint(string format, Button first, Button second = Button.None)
    {
        var key = (format, first, second, Look);
        if (!Hints.TryGetValue(key, out var text))
        {
            text = string.Format(format, Describe(first), Describe(second));
            Hints[key] = text;
        }

        return text;
    }

    static string Name(Button button)
    {
        var playStation = Look == Style.PlayStation;
        return button switch
        {
            Button.A => playStation ? "CROSS" : "A",
            Button.B => playStation ? "CIRCLE" : "B",
            Button.X => playStation ? "SQUARE" : "X",
            Button.Y => playStation ? "TRIANGLE" : "Y",
            Button.LB => playStation ? "L1" : "LB",
            Button.RB => playStation ? "R1" : "RB",
            Button.LT => playStation ? "L2" : "LT",
            Button.RT => playStation ? "R2" : "RT",
            Button.LeftStick => playStation ? "L3" : "LS",
            Button.RightStick => playStation ? "R3" : "RS",
            Button.Back => playStation ? "SHARE" : "BACK",
            Button.Start => playStation ? "OPTIONS" : "START",
            Button.DPadUp => "D-PAD UP",
            Button.DPadDown => "D-PAD DOWN",
            Button.DPadLeft => "D-PAD LEFT",
            Button.DPadRight => "D-PAD RIGHT",
            _ => "",
        };
    }

    // Everything the game knows this button by. A trigger is both a button and an axis, and the
    // game reads both, so both have to be kept from it.
    public static string[] EngineNames(Button button)
    {
        if (!Engine.TryGetValue(button, out var names))
        {
            names = button switch
            {
                Button.LT => new[] { "XboxTypeS_LeftTrigger", "XboxTypeS_LeftTriggerAxis" },
                Button.RT => new[] { "XboxTypeS_RightTrigger", "XboxTypeS_RightTriggerAxis" },
                _ => EngineName(button) is { Length: > 0 } name ? new[] { name } : Array.Empty<string>(),
            };

            Engine[button] = names;
        }

        return names;
    }

    // The names the game's key bindings use, which is what Keyboard.Swallow(string) takes. The
    // game uses the Xbox names for every kind of pad.
    static string EngineName(Button button)
    {
        return button switch
        {
            Button.DPadUp => "XboxTypeS_DPad_Up",
            Button.DPadDown => "XboxTypeS_DPad_Down",
            Button.DPadLeft => "XboxTypeS_DPad_Left",
            Button.DPadRight => "XboxTypeS_DPad_Right",
            Button.Start => "XboxTypeS_Start",
            Button.Back => "XboxTypeS_Back",
            Button.LeftStick => "XboxTypeS_LeftThumbstick",
            Button.RightStick => "XboxTypeS_RightThumbstick",
            Button.LB => "XboxTypeS_LeftShoulder",
            Button.RB => "XboxTypeS_RightShoulder",
            Button.A => "XboxTypeS_A",
            Button.B => "XboxTypeS_B",
            Button.X => "XboxTypeS_X",
            Button.Y => "XboxTypeS_Y",
            _ => "",
        };
    }
}
