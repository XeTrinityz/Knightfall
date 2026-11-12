#nullable enable

using System.Diagnostics;

namespace Knightfall;

enum Act
{
    Open,
    Up,
    Down,
    Left,
    Right,
    Accept,
    Back,
    TabPrev,
    TabNext,
    PagePrev,
    PageNext,
    Bind,
    Favourite,
}

enum Device
{
    Keyboard,
    Pad,
}

// Keyboard and pad both end up as the same actions, so the menu never asks which one was used.
static class Controls
{
    const double FirstRepeatSeconds = 0.35;
    const double NextRepeatSeconds = 0.07;

    static readonly int Count = Enum.GetValues<Act>().Length;

    static readonly Keys[] KeyOf = new Keys[Count];

    static readonly Pad.Button[] PadOf = Enum.GetValues<Act>().Select(PadFor).ToArray();

    static readonly bool[] Held = new bool[Count];
    static readonly bool[] Fired = new bool[Count];
    static readonly long[] RepeatAt = new long[Count];
    static readonly bool[] KeySwallowed = new bool[Count];
    static readonly bool[] PadSwallowed = new bool[Count];

    static Pad.Button _padOpen;
    static bool _menuOpen;

    public static Device LastDevice { get; private set; }

    public static bool Pressed(Act act) => Fired[(int)act];

    public static string KeyName(Act act) => KeyOf[(int)act].ToString();

    public static Keys KeyFor(Act act) => KeyOf[(int)act];

    public static Pad.Button ButtonFor(Act act) => act == Act.Open ? _padOpen : PadOf[(int)act];

    public static void Bind()
    {
        Settings.Describe("keys", "Keyboard keys for the menu. Use key names like F1, Insert, Q, Up, Enter, Escape, Backspace, PageUp or NumPad5.\nradial is held for the favourites radial. XButton1 is mouse button 4 and XButton2 is mouse button 5.\nSome options also have their own keys, next to the option as <name>_key and <name>_pad, like jump_key under [batmobile.driving].");
        KeyOf[(int)Act.Open] = Key("open", "F1");
        KeyOf[(int)Act.Up] = Key("up", "Up");
        KeyOf[(int)Act.Down] = Key("down", "Down");
        KeyOf[(int)Act.Left] = Key("left", "Left");
        KeyOf[(int)Act.Right] = Key("right", "Right");
        KeyOf[(int)Act.Accept] = Key("accept", "Enter");
        KeyOf[(int)Act.Back] = Key("back", "Escape");
        KeyOf[(int)Act.TabPrev] = Key("tab_prev", "Q");
        KeyOf[(int)Act.TabNext] = Key("tab_next", "E");
        KeyOf[(int)Act.PagePrev] = Key("page_prev", "Z");
        KeyOf[(int)Act.PageNext] = Key("page_next", "C");
        KeyOf[(int)Act.Bind] = Key("bind", "B");
        KeyOf[(int)Act.Favourite] = Key("favourite", "F");

        Settings.Describe("pad", "open is the controller buttons that open the menu when held together. Leave it empty to turn that off.\nradial is held for the favourites radial.\nXbox names: A, B, X, Y, LB, RB, LT, RT, LS, RS, Back, Start. PlayStation names: Cross, Circle, Square, Triangle, L1, R1, L2, R2, L3, R3, Share, Options.\nAlso DPadUp, DPadDown, DPadLeft, DPadRight.\nbuttons picks the names shown in hints: auto, xbox or playstation.\nIn the menu the D-pad moves, A or Cross accepts, B or Circle goes back, the bumpers switch tabs and the triggers switch pages.");
        _padOpen = Chord("open", "LS+RS");
        Pad.Configure();

        Log.Info($"Menu key {KeyName(Act.Open)}, controller {Pad.Describe(_padOpen)}.");
    }

    // Reads the keys again after settings.toml changed under us. A swallowed key that changes is
    // handed back now, and the new one is taken once it is up.
    public static void Rebind()
    {
        var before = (Keys[])KeyOf.Clone();
        Bind();
        for (var i = 0; i < Count; i++)
        {
            if (KeySwallowed[i] && KeyOf[i] != before[i])
            {
                Swallowed.Give(before[i]);
                KeySwallowed[i] = false;
            }
        }
    }

    // Short names for the hints at the bottom of the menu.
    public static string KeyLabel(Act act)
    {
        var key = KeyOf[(int)act];
        return key switch
        {
            Keys.Return => "ENTER",
            Keys.Escape => "ESC",
            Keys.Back => "BKSP",
            Keys.Prior => "PGUP",
            Keys.Next => "PGDN",
            >= Keys.D0 and <= Keys.D9 => ((int)key - (int)Keys.D0).ToString(),
            _ => key.ToString().ToUpperInvariant(),
        };
    }

    public static string PadLabel(Act act)
    {
        return Pad.Describe(act == Act.Open ? _padOpen : PadOf[(int)act]);
    }

    public static void Poll()
    {
        Pad.Poll();

        var now = Stopwatch.GetTimestamp();
        for (var i = 0; i < Count; i++)
        {
            var onPad = (Act)i == Act.Open ? Pad.IsDown(_padOpen) : Pad.IsDown(PadOf[i]);
            var down = onPad || Keyboard.IsDown(KeyOf[i]);

            Fired[i] = false;
            if (down && !Held[i])
            {
                Fired[i] = true;
                RepeatAt[i] = now + Ticks(FirstRepeatSeconds);
                LastDevice = onPad ? Device.Pad : Device.Keyboard;
            }
            else if (down && Repeats((Act)i) && now >= RepeatAt[i])
            {
                Fired[i] = true;
                RepeatAt[i] = now + Ticks(NextRepeatSeconds);
            }

            Held[i] = down;
        }

        Reconcile();
    }

    // Tells the game to stop or start seeing the menu's keys. It takes effect in Reconcile.
    public static void Capture(bool menuOpen)
    {
        _menuOpen = menuOpen;
    }

    public static void Release()
    {
        for (var i = 0; i < Count; i++)
        {
            if (KeySwallowed[i])
            {
                Swallowed.Give(KeyOf[i]);
                KeySwallowed[i] = false;
            }

            if (PadSwallowed[i])
            {
                Swallowed.Give(PadOf[i]);
                PadSwallowed[i] = false;
            }
        }

        Pad.Stop();
    }

    // A key only changes hands while it is up. Windows repeats the key-down message for a held
    // key, so handing Escape back while it is still down would open the pause menu, and taking
    // a held key away would leave it stuck down in the game.
    static void Reconcile()
    {
        for (var i = 0; i < Count; i++)
        {
            var wantKey = _menuOpen || (Act)i == Act.Open;
            if (KeySwallowed[i] != wantKey && KeyOf[i] != Keys.None && !Keyboard.IsDown(KeyOf[i]))
            {
                if (wantKey)
                {
                    Swallowed.Take(KeyOf[i]);
                }
                else
                {
                    Swallowed.Give(KeyOf[i]);
                }

                KeySwallowed[i] = wantKey;
            }

            var button = PadOf[i];
            var wantPad = _menuOpen && Pad.Connected;
            if (PadSwallowed[i] != wantPad && button != Pad.Button.None && !Pad.IsDown(button))
            {
                if (wantPad)
                {
                    Swallowed.Take(button);
                }
                else
                {
                    Swallowed.Give(button);
                }
                PadSwallowed[i] = wantPad;
            }
        }
    }

    // Open is left out. Its buttons come from settings.toml.
    static Pad.Button PadFor(Act act)
    {
        return act switch
        {
            Act.Up => Pad.Button.DPadUp,
            Act.Down => Pad.Button.DPadDown,
            Act.Left => Pad.Button.DPadLeft,
            Act.Right => Pad.Button.DPadRight,
            Act.Accept => Pad.Button.A,
            Act.Back => Pad.Button.B,
            Act.TabPrev => Pad.Button.LB,
            Act.TabNext => Pad.Button.RB,
            Act.PagePrev => Pad.Button.LT,
            Act.PageNext => Pad.Button.RT,
            Act.Bind => Pad.Button.Y,
            Act.Favourite => Pad.Button.X,
            _ => Pad.Button.None,
        };
    }

    static bool Repeats(Act act)
    {
        return act is Act.Up or Act.Down or Act.Left or Act.Right;
    }

    static long Ticks(double seconds)
    {
        return (long)(seconds * Stopwatch.Frequency);
    }

    static Keys Key(string name, string fallback)
    {
        var text = Settings.Text("keys", name, fallback).Trim();
        if (TryKey(text, out var key))
        {
            return key;
        }

        Log.Warn("keys." + name, $"settings.toml: '{text}' is not a key name for keys.{name}. Using {fallback}.");
        Settings.Reset("keys", name, fallback);
        TryKey(fallback, out key);
        return key;
    }

    public static bool TryKey(string text, out Keys key)
    {
        text = text.ToLowerInvariant() switch
        {
            "esc" => "Escape",
            "backspace" => "Back",
            "spacebar" => "Space",
            _ => text,
        };

        // Enum parsing also accepts plain numbers, which would let a typo through.
        return Enum.TryParse(text, ignoreCase: true, out key)
            && !int.TryParse(text, out _)
            && (int)key is > 0 and < 256;
    }

    static Pad.Button Chord(string name, string fallback)
    {
        var text = Settings.Text("pad", name, fallback);
        if (Pad.TryParse(text, out var chord))
        {
            return chord;
        }

        Log.Warn("pad." + name, $"settings.toml: '{text}' has a button name I don't know for pad.{name}. Using {fallback}.");
        Settings.Reset("pad", name, fallback);
        Pad.TryParse(fallback, out chord);
        return chord;
    }
}
