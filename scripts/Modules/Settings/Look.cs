#nullable enable

namespace Knightfall;

// SETTINGS > LOOK. How the menu itself looks.
static class Look
{
    static readonly (string Id, string Label, Rgba? Color)[] Accents =
    {
        ("theme", "THEME", null),
        ("amber", "AMBER", new Rgba(242, 179, 61)),
        ("ice", "ICE", new Rgba(127, 214, 255)),
        ("red", "RED", new Rgba(240, 65, 58)),
        ("green", "GREEN", new Rgba(120, 214, 130)),
        ("violet", "VIOLET", new Rgba(168, 132, 255)),
        ("white", "WHITE", new Rgba(230, 233, 238)),
    };

    public static readonly Choice Theme = new(
        "settings.look.theme", "THEME",
        "Changes the whole look of the menu.",
        Knightfall.Theme.All.Select(theme => (theme.Id, theme.Name)).ToArray())
    {
        Preference = true,
        Changed = ShowMove,
    };

    public static readonly Choice OpenOn = new(
        "settings.look.open_on", "OPEN ON",
        "Where the menu opens. GAUNTLET shows it on Batman's gauntlet, or on the dashboard in the Batmobile. In the air, in fights or in battle mode it uses the screen.",
        new[] { ("screen", "SCREEN"), ("gauntlet", "GAUNTLET") })
    {
        Preference = true,
    };

    public static bool OnGauntlet => OpenOn.Index == 1;

    public static readonly Choice Accent = new(
        "settings.look.accent", "ACCENT",
        "The highlight colour. THEME uses the colour that belongs to the theme.",
        Accents.Select(accent => (accent.Id, accent.Label)).ToArray())
    {
        Preference = true,
    };

    public static readonly Slider Size = new(
        "settings.look.size", "MENU SIZE",
        "Makes the whole menu bigger or smaller.",
        min: 0.7, max: 1.6, step: 0.05, value: 1, unit: "X")
    {
        Preference = true,
    };

    // The order matters. Draw picks the font by this index.
    public static readonly Choice Font = new(
        "settings.look.font", "FONT",
        "The game only has two usable fonts. AUTO picks the one that is sharpest at your resolution.",
        new[] { ("auto", "AUTO"), ("slab", "SLAB"), ("sans", "SANS") })
    {
        Preference = true,
    };

    // Each theme keeps its own spot, as each sits somewhere else on the screen.
    const string PlaceSection = "settings.look.place";

    // A share of the room between the theme's own spot and the edge of the screen, so every step
    // moves the menu and it can never go off screen. They show the current theme's values, which
    // are saved under PlaceSection and not as options of their own.
    public static readonly Slider MoveX = new(
        "settings.look.move_x", "MENU X",
        "Moves the menu left or right. At -100% it sits against the left edge and at 100% against the right. THEME is where the theme puts it. Each theme keeps its own.",
        min: -100, max: 100, step: 5, value: 0, unit: "%", zero: "THEME", saved: false)
    {
        Preference = true,
        Changed = KeepMove,
    };

    public static readonly Slider MoveY = new(
        "settings.look.move_y", "MENU Y",
        "Moves the menu up or down. At -100% it sits against the top edge and at 100% against the bottom. THEME is where the theme puts it. Each theme keeps its own.",
        min: -100, max: 100, step: 5, value: 0, unit: "%", zero: "THEME", saved: false)
    {
        Preference = true,
        Changed = KeepMove,
    };

    public static readonly Command Reset = new(
        "settings.look.reset", "RESET LOOK",
        "Puts every row on this tab back to how it came. The spots you gave the other themes are left alone.",
        ResetLook);

    public static readonly Feature[] Rows = { Theme, OpenOn, Accent, Size, MoveX, MoveY, Font, Reset };

    public static float Zoom => (float)Size.Value;

    // THEME goes back first, so MENU X and MENU Y are then the default theme's.
    static Outcome ResetLook()
    {
        var count = 0;
        foreach (var row in Rows)
        {
            if (!row.IsDefault)
            {
                row.Reset();
                count++;
            }
        }

        return count == 0 ? Outcome.Unchanged("ALREADY DEFAULT") : Outcome.Done(count == 1 ? "1 ROW RESET" : $"{count} ROWS RESET");
    }

    // After settings.toml is read. Every theme's spot is kept for saving, since only the current
    // one is read.
    public static void LoadMove()
    {
        Settings.KeepAll(PlaceSection);
        ShowMove();
    }

    static void ShowMove()
    {
        var id = Knightfall.Theme.Current.Id;
        MoveX.Show(Number(Settings.Kept(PlaceSection, id + "_x")));
        MoveY.Show(Number(Settings.Kept(PlaceSection, id + "_y")));
    }

    static void KeepMove()
    {
        var id = Knightfall.Theme.Current.Id;
        Settings.Set(PlaceSection, id + "_x", (long)MoveX.Value);
        Settings.Set(PlaceSection, id + "_y", (long)MoveY.Value);
    }

    static double Number(object? stored) => stored switch
    {
        long whole => whole,
        double number when double.IsFinite(number) => number,
        _ => 0,
    };

    public static Rgba AccentOr(Rgba themeColor)
    {
        return Accents[Accent.Index].Color ?? themeColor;
    }
}
