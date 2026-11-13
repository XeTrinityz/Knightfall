#nullable enable

namespace Knightfall;

// WORLD > WEATHER, the PRESET rows. A preset is only a set of values for the rows under it, so
// each of them can still be changed afterwards. It leaves RAIN SPREAD, the two rain switches and
// WIND DIRECTION as they are, since those are a matter of taste.
static class Weather
{
    sealed record Preset(string Id, string Label, double Rain, double Wet, double Snow, double Strikes, double Flash, double Wind, double Gusts);

    // Snow is how thickly it falls, 0 for none. While it snows the rain rows don't count.
    static readonly Preset[] Presets =
    {
        new("clear", "CLEAR NIGHT", Rain: 0, Wet: 1, Snow: 0, Strikes: 0, Flash: 1, Wind: 0.5, Gusts: 1),
        new("drizzle", "DRIZZLE", Rain: 0.5, Wet: 0.8, Snow: 0, Strikes: 0, Flash: 1, Wind: 0.75, Gusts: 1),
        new("downpour", "DOWNPOUR", Rain: 2.5, Wet: 1.5, Snow: 0, Strikes: 1, Flash: 1, Wind: 1.5, Gusts: 1),
        new("storm", "THUNDERSTORM", Rain: 3, Wet: 1.6, Snow: 0, Strikes: 5, Flash: 1.5, Wind: 3, Gusts: 1.5),
        new("snow", "SNOWFALL", Rain: 1, Wet: 1, Snow: 1, Strikes: 0, Flash: 1, Wind: 0.75, Gusts: 1),
        new("blizzard", "BLIZZARD", Rain: 1, Wet: 1, Snow: 3, Strikes: 0, Flash: 1, Wind: 4, Gusts: 2),
    };

    static readonly Feature[] Rows =
    {
        Rain.Amount, Rain.Wetness, Rain.Spread, Rain.NoMist, Rain.NoFarRain,
        Snow.Snowing, Snow.Amount,
        Lightning.Frequency, Lightning.Bright,
        Wind.Strength, Wind.Direction, Wind.Gusts,
    };

    public static readonly Choice Pick = new(
        "world.weather.preset", "PRESET",
        "A whole night's weather to start from. APPLY PRESET puts it on.",
        Presets.Select(preset => (preset.Id, preset.Label)).ToArray(),
        saved: false)
    {
        Pressed = static () => Apply!.Press(),
        PressName = "APPLY",
    };

    public static readonly Command Apply = new(
        "world.weather.apply_preset", "APPLY PRESET",
        "Sets the weather rows to the picked PRESET. Change any of them after. RESET WEATHER puts them all back.",
        Set);

    public static readonly Command Reset = new(
        "world.weather.reset", "RESET WEATHER",
        "Puts every row on this tab back to the game's weather.",
        static () => Back(Rows));

    public static readonly Catalog.Section Section = new("PRESET", Pick, Apply, Reset);

    // Puts a tab's rows back to the game's, and says how many it took.
    public static Outcome Back(Feature[] rows)
    {
        var count = 0;
        foreach (var row in rows)
        {
            if (!row.IsDefault)
            {
                row.Reset();
                count++;
            }
        }

        return count == 0 ? Outcome.Unchanged("ALREADY THE GAME'S") : Outcome.Done(count == 1 ? "1 ROW RESET" : $"{count} ROWS RESET");
    }

    static Outcome Set()
    {
        var preset = Presets[Math.Clamp(Pick.Index, 0, Presets.Length - 1)];
        if (preset.Snow > 0 && Snow.Snowing.Blocked is { } why)
        {
            return Outcome.Failed(why);
        }

        Rain.Amount.SetTo(preset.Rain);
        Rain.Wetness.SetTo(preset.Wet);
        Snow.Snowing.SetTo(preset.Snow > 0);
        Snow.Amount.SetTo(preset.Snow > 0 ? preset.Snow : 1);
        Lightning.Frequency.SetTo(preset.Strikes);
        Lightning.Bright.SetTo(preset.Flash);
        Wind.Strength.SetTo(preset.Wind);
        Wind.Gusts.SetTo(preset.Gusts);
        return Outcome.Done(preset.Label);
    }
}
