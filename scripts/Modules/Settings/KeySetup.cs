#nullable enable

namespace Knightfall;

// SETTINGS > CONTROLS. The keys that open the menu and the radial, then every option with a
// hotkey under the page and tab it lives on, so they can all be changed in one place.
static class KeySetup
{
    public static readonly Command MenuKey = new(
        "settings.controls.menu_key", "MENU KEY",
        "What opens and closes the menu. Press this, then the key you want. On a controller, hold the buttons together and let go.",
        static () => Listen(MenuKey!, TakeMenuKey, TakeMenuButtons))
    {
        Passing = true,
        Cap = "SET",
        PressName = "SET",
    };

    public static readonly Command RadialKey = new(
        "settings.controls.radial_key", "RADIAL KEY",
        "What you hold for the favourites radial. Press this, then the key, mouse side button or controller button you want.",
        static () => Listen(RadialKey!, TakeRadialKey, TakeRadialButtons))
    {
        Passing = true,
        Cap = "SET",
        PressName = "SET",
    };

    public static readonly Command ResetHotkeys = new(
        "settings.hotkeys.reset", "RESET HOTKEYS",
        "Takes off every hotkey you added and puts back the ones Knightfall comes with. Press it twice.",
        static () => Twice("reset hotkeys", Hotkeys.ResetAll));

    public static readonly Command ClearHotkeys = new(
        "settings.hotkeys.clear", "CLEAR ALL HOTKEYS",
        "Takes every hotkey off, the ones Knightfall comes with too. Press it twice. To add one, highlight a row anywhere in the menu, press the HOTKEY key shown at the bottom, then the keys you want.",
        static () => Twice("clear hotkeys", Hotkeys.ClearAll));

    static readonly Catalog.Section MenuRows = new("MENU", MenuKey, RadialKey, Pad.Prompts);
    static readonly Catalog.Section NoneYet = new("NO HOTKEYS YET");
    static readonly Catalog.Section AllRows = new("EVERY HOTKEY", ResetHotkeys, ClearHotkeys);

    static KeySetup()
    {
        Hotkeys.Shows(MenuKey, static () => $"{Hotkeys.KeyName(Controls.KeyFor(Act.Open))} / {Controls.PadLabel(Act.Open)}");
        Hotkeys.Shows(RadialKey, static () => Radial.KeyText);
    }

    // This tab's own rows show their key after their name already, so they aren't listed again.
    public static IReadOnlyList<Catalog.Section> Sections()
    {
        var sections = new List<Catalog.Section> { MenuRows };
        var listed = new HashSet<Feature>(MenuRows.Rows.Concat(AllRows.Rows));
        foreach (var page in Catalog.Pages)
        {
            foreach (var tab in page.Tabs)
            {
                var bound = tab.Own.Where(row => Hotkeys.Has(row) && listed.Add(row)).ToArray();
                if (bound.Length > 0)
                {
                    sections.Add(new Catalog.Section($"{page.Name} > {tab.Name}", bound) { Borrowed = true });
                }
            }
        }

        if (sections.Count == 1)
        {
            sections.Add(NoneYet);
        }

        sections.Add(AllRows);
        return sections;
    }

    static Outcome Listen(Command row, Func<Keys, Outcome> key, Func<Pad.Button, Outcome> buttons)
    {
        Hotkeys.Listen(row, key, buttons);
        return Outcome.Unchanged("WAITING");
    }

    // The list is built again after, since most of it has just lost its key.
    static Outcome Twice(string what, Func<Outcome> run)
    {
        if (!Confirm.Second(what))
        {
            return Outcome.Unchanged("PRESS AGAIN");
        }

        var outcome = run();
        Menu.Reshow(ClearHotkeys);
        return outcome;
    }

    // A key the menu moves or picks with would shut it on the next press.
    static Outcome TakeMenuKey(Keys key)
    {
        if (key == Controls.KeyFor(Act.Open))
        {
            return Outcome.Unchanged("ALREADY SET");
        }

        if (Controls.Uses(key))
        {
            return Outcome.Failed("USED BY THE MENU");
        }

        if (key == Radial.Key || Radial.Uses(key))
        {
            return Outcome.Failed("USED BY THE RADIAL");
        }

        Hotkeys.Free(key, MenuKey);
        Controls.SetOpen(key);
        return Outcome.Done("SET");
    }

    static Outcome TakeMenuButtons(Pad.Button buttons)
    {
        if (buttons == Controls.ButtonFor(Act.Open))
        {
            return Outcome.Unchanged("ALREADY SET");
        }

        if (Controls.Uses(buttons))
        {
            return Outcome.Failed("USED BY THE MENU");
        }

        if (buttons == Radial.Button || Radial.Uses(buttons))
        {
            return Outcome.Failed("USED BY THE RADIAL");
        }

        Hotkeys.Free(buttons, MenuKey);
        Controls.SetOpen(buttons);
        return Outcome.Done("SET");
    }

    static Outcome TakeRadialKey(Keys key)
    {
        if (key == Radial.Key)
        {
            return Outcome.Unchanged("ALREADY SET");
        }

        if (key == Controls.KeyFor(Act.Open))
        {
            return Outcome.Failed("USED BY THE MENU");
        }

        if (Radial.Uses(key))
        {
            return Outcome.Failed("USED BY THE RADIAL");
        }

        Hotkeys.Free(key, RadialKey);
        Radial.SetKey(key);
        return Outcome.Done("SET");
    }

    static Outcome TakeRadialButtons(Pad.Button buttons)
    {
        if (buttons == Radial.Button)
        {
            return Outcome.Unchanged("ALREADY SET");
        }

        if (buttons == Controls.ButtonFor(Act.Open))
        {
            return Outcome.Failed("USED BY THE MENU");
        }

        if (Radial.Uses(buttons))
        {
            return Outcome.Failed("USED BY THE RADIAL");
        }

        Hotkeys.Free(buttons, RadialKey);
        Radial.SetButton(buttons);
        return Outcome.Done("SET");
    }
}
