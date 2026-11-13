#nullable enable

using System.Diagnostics;
using System.Numerics;
using BmSDK;
using BmSDK.Engine;

namespace Knightfall;

// SETTINGS > RADIAL. A wheel of your favourite rows on a held key, to use them without the menu.
// While it's open the game doesn't see the mouse or the right stick, so the camera stays put: the
// mouse moves a pointer and the stick points straight at a slot. A toggle flips, a command runs and
// a choice steps on. The wheel has one slot per favourite, and each theme draws it its own way.
static class Radial
{
    const float StickLean = 0.5f;

    // How quickly a slot lifts out under the pointer and settles back, per second.
    const float LiftRate = 16f;

    // The game's names for the camera's inputs, kept from it while the wheel is open.
    static readonly string[] CameraInputs = { "MouseX", "MouseY", "XboxTypeS_RightX", "XboxTypeS_RightY" };

    static readonly FName ScrollUp = new("MouseScrollUp");
    static readonly FName ScrollDown = new("MouseScrollDown");

    static Keys _key;
    static Pad.Button _button;

    static bool _open;
    static bool _wasDown;
    static Vector2 _pointer;
    static int _page;
    static int _scrolled;
    static List<Feature> _items = new();
    static readonly RadialView View = new();
    static readonly Dictionary<int, bool> Was = new();
    static long _openedAt;
    static long _pageAt;
    static long _flashedAt;
    static long _lastFrame;
    static int _flashed = -1;
    static bool _lookTaken;
    static (bool Pad, Pad.Style Style, bool Tap, bool Paged, bool Choices, int Count, Keys Key, Pad.Button Button)? _hintsFor;

    // Kept from the game while it's open: turning pages, using, closing, stepping a choice and the
    // number keys that use a slot straight away.
    static readonly Keys[] OpenKeys =
    {
        Keys.Q, Keys.E, Keys.LButton, Keys.RButton,
        Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5, Keys.D6, Keys.D7, Keys.D8, Keys.D9,
    };

    static readonly Pad.Button[] OpenButtons = { Pad.Button.LB, Pad.Button.RB, Pad.Button.A, Pad.Button.B, Pad.Button.DPadLeft, Pad.Button.DPadRight };

    static readonly HashSet<Keys> KeysTaken = new();
    static readonly HashSet<Pad.Button> ButtonsTaken = new();

    public static bool IsOpen => _open;

    public static readonly Toggle On = new(
        "settings.radial.on", "FAVOURITES RADIAL",
        "Hold the radial key for a wheel of your favourite rows. Mark favourites in the menu with the FAVOURITE key. RADIAL KEY on the CONTROLS tab sets the key.",
        on: true)
    {
        Preference = true,
    };

    public static readonly Choice Mode = new(
        "settings.radial.mode", "RADIAL MODE",
        "HOLD uses the slot you point at when you let go. TAP opens it and stays open: click (or A) uses a slot, and the radial key or right click (B) closes it.",
        new[] { ("hold", "HOLD"), ("tap", "TAP") })
    {
        Preference = true,
    };

    public static readonly Choice SlowTime = WorldTime.SlowChoice(
        "settings.radial.slow_time", "SLOW TIME",
        "Slows the game while the radial is open. It comes back up to speed when the radial closes.");

    public static readonly Slider Speed = new(
        "settings.radial.pointer_speed", "POINTER SPEED",
        "How far the pointer moves with the mouse.",
        min: 0.25, max: 3, step: 0.25, value: 1, unit: "X")
    {
        Preference = true,
    };

    public static readonly Slider Slots = new(
        "settings.radial.slots", "SLOTS PER PAGE",
        "The most favourites on one page of the wheel. The wheel has a slot for each favourite, and more go on further pages, turned with Q and E or the bumpers.",
        min: 4, max: RadialView.MostSlots, step: 1, value: 8)
    {
        Preference = true,
    };

    public static readonly Slider Size = new(
        "settings.radial.size", "RADIAL SIZE",
        "How big the wheel is on screen.",
        min: 0.6, max: 1.6, step: 0.1, value: 1, unit: "X")
    {
        Preference = true,
    };

    public static readonly Choice Order = new(
        "settings.radial.order", "RADIAL ORDER",
        "Which way round the favourites go: as you added them, A to Z, or in the menu's order.",
        new[] { ("added", "AS ADDED"), ("name", "A TO Z"), ("menu", "MENU ORDER") })
    {
        Preference = true,
        Changed = static () => Menu.Reshow(On),
    };

    public static readonly Command Clear = new(
        "settings.radial.clear", "CLEAR RADIAL",
        "Takes every row off the radial. Press it twice.",
        static () => Confirm.Second("clear radial") ? Favourites.Clear() : Outcome.Unchanged("PRESS AGAIN"));

    static readonly Catalog.Section Fixed = new("RADIAL", On, Mode, SlowTime, Speed, Slots, Size, Order);
    static readonly Catalog.Section Empty = new("NOTHING ON THE RADIAL YET");
    static readonly Catalog.Section Every = new("EVERY FAVOURITE", Clear);

    // The settings, then the favourites themselves in the order the wheel has them. A favourite
    // works here as it does on its own tab, and the FAVOURITE key takes it off.
    public static IReadOnlyList<Catalog.Section> Sections()
    {
        var marked = Favourites.Ordered(Order.Index);
        var list = marked.Count > 0 ? new Catalog.Section("ON THE RADIAL", marked.ToArray()) { Borrowed = true } : Empty;
        return new[] { Fixed, list, Every };
    }

    public static Keys Key => _key;

    public static Pad.Button Button => _button;

    // What the radial key shows as, on both devices.
    public static string KeyText => $"{(_key == Keys.None ? "off" : Hotkeys.KeyName(_key))} / {Pad.Describe(_button)}";

    // Whether the open wheel already uses this key for something.
    public static bool Uses(Keys key) => Array.IndexOf(OpenKeys, key) >= 0;

    public static bool Uses(Pad.Button buttons) => Array.IndexOf(OpenButtons, buttons) >= 0;

    public static void Bind()
    {
        var keyText = Settings.Text("keys", "radial", "XButton1").Trim();

        // The middle mouse button stuns in a fight, so it can't open the radial.
        if (keyText.Equals("MButton", StringComparison.OrdinalIgnoreCase))
        {
            keyText = "XButton1";
            Settings.Set("keys", "radial", keyText);
        }

        if (!Controls.TryKey(keyText, out var key))
        {
            Log.Warn("keys.radial", $"settings.toml: '{keyText}' is not a key name for keys.radial. Using XButton1.");
            Settings.Reset("keys", "radial", "XButton1");
            Controls.TryKey("XButton1", out key);
        }

        var padText = Settings.Text("pad", "radial", "RS");
        if (!Pad.TryParse(padText, out var button))
        {
            Log.Warn("pad.radial", $"settings.toml: '{padText}' has a button name I don't know for pad.radial. Using RS.");
            Settings.Reset("pad", "radial", "RS");
            Pad.TryParse("RS", out button);
        }

        Use(key, button);
    }

    // A new radial key, picked in the menu.
    public static void SetKey(Keys key)
    {
        Use(key, _button);
        Settings.Set("keys", "radial", key.ToString());
    }

    public static void SetButton(Pad.Button button)
    {
        Use(_key, button);
        Settings.Set("pad", "radial", Pad.Stored(button));
    }

    // A key that is no longer the radial's goes straight back to the game.
    static void Use(Keys key, Pad.Button button)
    {
        if (key != _key && KeysTaken.Remove(_key))
        {
            Swallowed.Give(_key);
        }

        if (button != _button && ButtonsTaken.Remove(_button))
        {
            Swallowed.Give(_button);
        }

        _key = key;
        _button = button;
        _hintsFor = null;
    }

    // Every frame, with the rest of the input.
    public static void Poll()
    {
        var usable = On.On && Favourites.Count > 0 && !Menu.Showing && !World.Paused && Hotkeys.Listening is null && World.Controller is not null
            && GameWindow.InFront;
        Keep(usable);

        var down = (_key != Keys.None && Keyboard.IsDown(_key)) || Pad.IsDown(_button);
        var pressed = down && !_wasDown;
        var released = !down && _wasDown;
        _wasDown = down;

        if (!usable)
        {
            _open = false;
            _scrolled = 0;
            return;
        }

        if (!_open)
        {
            if (pressed)
            {
                Open();
            }

            return;
        }

        Point();
        if (Pressed(Keys.Q, Pad.Button.LB))
        {
            Turn(-1);
        }

        if (Pressed(Keys.E, Pad.Button.RB))
        {
            Turn(1);
        }

        var step = _scrolled;
        _scrolled = 0;
        if (Pressed(Keys.None, Pad.Button.DPadRight))
        {
            step++;
        }

        if (Pressed(Keys.None, Pad.Button.DPadLeft))
        {
            step--;
        }

        if (step != 0)
        {
            StepChoice(step);
        }

        var hold = Mode.Index == 0;
        for (var i = 0; i < 9; i++)
        {
            if (Pressed(Keys.D1 + i, Pad.Button.None) && i < View.Slots.Count)
            {
                Use(i);

                // HOLD is one pick per opening, by whichever way it was picked.
                if (hold)
                {
                    _open = false;
                    return;
                }
            }
        }

        if (hold)
        {
            if (released)
            {
                Use(Hovered());
                _open = false;
            }

            return;
        }

        if (pressed || Pressed(Keys.RButton, Pad.Button.B))
        {
            _open = false;
        }
        else if (Pressed(Keys.LButton, Pad.Button.A))
        {
            Use(Hovered());
        }
    }

    static void Open()
    {
        _items = Favourites.Ordered(Order.Index);
        _page = Math.Clamp(_page, 0, Pages() - 1);
        Fill();
        _pointer = Vector2.Zero;
        _scrolled = 0;
        _openedAt = _pageAt = Stopwatch.GetTimestamp();
        _flashed = -1;
        _lastFrame = 0;
        Array.Clear(View.Lifts);
        Cursor.Centre();
        _open = true;

        // Keys already down when it opens only count once they've been let go.
        foreach (var key in OpenKeys)
        {
            Was[Id(key, Pad.Button.None)] = Keyboard.IsDown(key);
        }

        foreach (var button in OpenButtons)
        {
            Was[Id(Keys.None, button)] = Pad.IsDown(button);
        }
    }

    static void Fill()
    {
        View.Slots.Clear();
        for (var i = _page * PerPage; i < _items.Count && i < (_page + 1) * PerPage; i++)
        {
            View.Slots.Add(_items[i]);
        }
    }

    static void Turn(int by)
    {
        var pages = Pages();
        if (pages < 2)
        {
            return;
        }

        _page = (_page + by + pages) % pages;
        Fill();
        _pageAt = Stopwatch.GetTimestamp();
        _flashed = -1;
    }

    static void Use(int slot)
    {
        if (slot < 0 || slot >= View.Slots.Count)
        {
            return;
        }

        Flash(slot);
        Hotkeys.Fire(View.Slots[slot]);
    }

    // The mouse wheel or the D-pad steps the choice pointed at either way, and the wheel stays open.
    static void StepChoice(int steps)
    {
        var slot = Hovered();
        if (slot < 0 || slot >= View.Slots.Count || View.Slots[slot] is not Choice choice)
        {
            return;
        }

        if (choice.Blocked is { } why)
        {
            Toasts.Show(choice.Plain, why, OutcomeKind.Failed, choice);
            return;
        }

        for (var i = 0; i < Math.Abs(steps); i++)
        {
            choice.Step(Math.Sign(steps));
        }

        Flash(slot);
        Toasts.Used(choice);
    }

    static void Flash(int slot)
    {
        _flashed = slot;
        _flashedAt = Stopwatch.GetTimestamp();
    }

    // The mouse moves the pointer. A pushed stick points it straight out that way.
    static void Point()
    {
        var reach = Theme.Current.RadialReach;
        if (Cursor.Moved(out var dx, out var dy) && (Math.Abs(dx) > 1 || Math.Abs(dy) > 1))
        {
            // Pixels to the wheel's own units, so the pointer keeps pace with it at any size.
            var pixelsPerUnit = MathF.Max(0.3f, Draw.Scale * Look.Zoom * (float)Size.Value);
            _pointer += new Vector2(dx, dy) / pixelsPerUnit * (float)Speed.Value;
        }

        var stick = Pad.Right;
        if (stick.Length() >= StickLean)
        {
            _pointer = Vector2.Normalize(stick) * reach;
        }

        if (_pointer.Length() > reach)
        {
            _pointer = Vector2.Normalize(_pointer) * reach;
        }
    }

    static int PerPage => (int)Slots.Value;

    static int Pages() => Math.Max(1, (_items.Count + PerPage - 1) / PerPage);

    // The slot whose direction is nearest the pointer's. Inside the hub is none.
    static int Hovered()
    {
        var count = View.Slots.Count;
        var theme = Theme.Current;
        if (count == 0 || _pointer.Length() < theme.RadialHub)
        {
            return -1;
        }

        var angle = MathF.Atan2(_pointer.X, -_pointer.Y) * 180f / MathF.PI;
        var best = -1;
        var nearest = float.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var apart = MathF.Abs(Kit.Apart(angle, theme.RadialAngle(i, count)));
            if (apart < nearest)
            {
                nearest = apart;
                best = i;
            }
        }

        return best;
    }

    static int Id(Keys key, Pad.Button button) => (int)key | ((int)button << 16);

    static bool Pressed(Keys key, Pad.Button button)
    {
        var down = (key != Keys.None && Keyboard.IsDown(key)) || (button != Pad.Button.None && Pad.IsDown(button));
        var id = Id(key, button);
        Was.TryGetValue(id, out var was);
        Was[id] = down;
        return down && !was;
    }

    // The mouse wheel isn't a key, so it is caught on its way to the game. While the radial is
    // open each notch is counted here and the game doesn't see it.
    [Redirect(typeof(GameViewportClient), nameof(GameViewportClient.HandleInputKey))]
    static bool HandleInputKey(GameViewportClient self, int controllerId, FName key, GameObject.EInputEvent eventType, float amountDepressed, bool gamepad)
    {
        if (_open && (key.Index == ScrollUp.Index || key.Index == ScrollDown.Index))
        {
            if (eventType == GameObject.EInputEvent.IE_Pressed)
            {
                _scrolled += key.Index == ScrollDown.Index ? 1 : -1;
            }

            return true;
        }

        return self.HandleInputKey(controllerId, key, eventType, amountDepressed, gamepad);
    }

    // A key only changes hands while it's up, or the game would see it stuck down. The camera's
    // inputs have no up or down, so they change at once.
    static void Keep(bool usable)
    {
        Hand(_key, usable);
        Hand(_button, usable);
        foreach (var key in OpenKeys)
        {
            Hand(key, _open);
        }

        foreach (var button in OpenButtons)
        {
            Hand(button, _open);
        }

        if (_open != _lookTaken)
        {
            foreach (var name in CameraInputs)
            {
                if (_open)
                {
                    Swallowed.Take(name);
                }
                else
                {
                    Swallowed.Give(name);
                }
            }

            _lookTaken = _open;
        }
    }

    static void Hand(Keys key, bool take)
    {
        if (key == Keys.None || KeysTaken.Contains(key) == take || Keyboard.IsDown(key))
        {
            return;
        }

        if (take)
        {
            Swallowed.Take(key);
            KeysTaken.Add(key);
        }
        else
        {
            Swallowed.Give(key);
            KeysTaken.Remove(key);
        }
    }

    static void Hand(Pad.Button button, bool take)
    {
        if (button == Pad.Button.None || ButtonsTaken.Contains(button) == take || Pad.IsDown(button))
        {
            return;
        }

        if (take)
        {
            Swallowed.Take(button);
            ButtonsTaken.Add(button);
        }
        else
        {
            Swallowed.Give(button);
            ButtonsTaken.Remove(button);
        }
    }

    public static void Release()
    {
        _open = false;
        foreach (var key in KeysTaken)
        {
            Swallowed.Give(key);
        }

        foreach (var button in ButtonsTaken)
        {
            Swallowed.Give(button);
        }

        if (_lookTaken)
        {
            foreach (var name in CameraInputs)
            {
                Swallowed.Give(name);
            }
        }

        KeysTaken.Clear();
        ButtonsTaken.Clear();
        _lookTaken = false;
    }

    public static void Render(Canvas canvas)
    {
        if (!_open || View.Slots.Count == 0 || !Draw.Begin(canvas))
        {
            return;
        }

        try
        {
            var now = Stopwatch.GetTimestamp();
            var seconds = _lastFrame == 0 ? 0f : MathF.Min(Seconds(_lastFrame, now), 0.1f);
            _lastFrame = now;

            View.Hover = Hovered();
            View.Pointer = _pointer;
            View.First = _page * PerPage;
            View.Page = _page;
            View.Pages = Pages();
            View.Tap = Mode.Index == 1;
            View.Age = Seconds(_openedAt, now);
            View.PageAge = Seconds(_pageAt, now);
            View.Flashed = _flashed;
            View.FlashAge = Seconds(_flashedAt, now);
            View.Size = (float)Size.Value;
            View.Now = now;
            View.Time = (float)(now / (double)Stopwatch.Frequency % 3600.0);
            for (var i = 0; i < View.Lifts.Length; i++)
            {
                var target = i == View.Hover ? 1f : 0f;
                View.Lifts[i] += (target - View.Lifts[i]) * (1f - MathF.Exp(-LiftRate * seconds));
            }

            Hints();
            Theme.Current.RenderRadial(View);
        }
        finally
        {
            Draw.Fade(1f);
            Draw.End();
        }
    }

    // What the keys do, in the words of the device used last. Only rebuilt when that changes.
    static void Hints()
    {
        var pad = Controls.LastDevice == Device.Pad;
        var choices = false;
        foreach (var feature in View.Slots)
        {
            choices |= feature is Choice;
        }

        var count = Math.Min(9, View.Slots.Count);
        var wanted = (pad, Pad.Look, View.Tap, View.Pages > 1, choices, count, _key, _button);
        if (_hintsFor == wanted)
        {
            return;
        }

        _hintsFor = wanted;
        var lines = View.Hints;
        lines.Clear();
        if (View.Tap)
        {
            lines.Add(new GuideLine(new[] { pad ? Pad.Describe(Pad.Button.A) : "CLICK" }, "USE"));
        }
        else
        {
            lines.Add(new GuideLine(new[] { pad ? Pad.Describe(_button) : Hotkeys.KeyName(_key) }, "LET GO TO USE"));
        }

        if (choices)
        {
            lines.Add(new GuideLine(new[] { pad ? "D-PAD" : "SCROLL" }, "CHANGE"));
        }

        if (View.Pages > 1)
        {
            lines.Add(pad
                ? new GuideLine(new[] { Pad.Describe(Pad.Button.LB), Pad.Describe(Pad.Button.RB) }, "PAGE")
                : new GuideLine(new[] { "Q", "E" }, "PAGE"));
        }

        if (!pad)
        {
            lines.Add(new GuideLine(new[] { count > 1 ? $"1-{count}" : "1" }, "QUICK USE"));
        }

        if (View.Tap)
        {
            lines.Add(new GuideLine(new[] { pad ? Pad.Describe(Pad.Button.B) : "RMB" }, "CLOSE"));
        }
    }

    static float Seconds(long since, long now) => (float)((now - since) / (double)Stopwatch.Frequency);
}
