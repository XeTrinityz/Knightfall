#nullable enable

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// Firefly's HUD, a hazard placard for a man who is the hazard. His badge is a fire diamond with
// live numbers: flame heat on top, enemies near on the left, the grenade on the right and
// whether he is up in the air at the bottom. Under it the nozzle heat climbs while he burns and
// cools after, each thug he torches strikes a match, and his keys for the moment sit along the
// bottom. Embers drift off the top edge, brighter the hotter he runs.
static partial class Firefly
{
    static readonly Rgba Orange = new(255, 122, 26);
    static readonly Rgba Yellow = new(255, 210, 122);
    static readonly Rgba Blaze = new(255, 106, 58);
    static readonly Rgba Red = new(226, 65, 43);
    static readonly Rgba Ember = new(107, 36, 20);
    static readonly Rgba Blue = new(63, 134, 184);
    static readonly Rgba Brass = new(107, 85, 48);
    static readonly Rgba Cream = new(246, 233, 216);
    static readonly Rgba White = new(255, 244, 220);
    static readonly Rgba Ink = new(14, 11, 9, 237);
    static readonly Rgba Solid = new(18, 12, 8);
    static readonly Rgba Rule = new(74, 46, 26);
    static readonly Rgba Text = new(246, 233, 216);
    static readonly Rgba Dim = new(168, 153, 138);
    static readonly Rgba Chip = new(122, 104, 88);
    static readonly Rgba Dark = new(42, 26, 16);
    static readonly Rgba Charred = new(58, 36, 22);
    static readonly Rgba MatchHead = new(122, 42, 28);
    static readonly Rgba MatchStick = new(138, 122, 104);
    static readonly Rgba Unlit = new(92, 66, 48);

    // Laid out on the 1080-high screen.
    const float HudLeft = 36f;
    const float HudBottom = 36f;
    const float HudWidth = 350f;
    const float Inset = 9f;
    const float HeaderHeight = 66f;
    const float NozzleHeight = 38f;
    const float BurnHeight = 42f;
    const float KeysHeight = 40f;
    const float SideWidth = 122f;

    // The badge's four diamonds, each this far from its middle and this big from middle to corner.
    const float BadgeStep = 13f;
    const float BadgeHalf = 12f;
    const float BadgeWidth = (BadgeStep + BadgeHalf) * 2f;

    // The nozzle heats to full over this long of burning and cools over this long after. It
    // reads the first temperature cold and the second at full.
    const float HeatUp = 2.5f;
    const float CoolDown = 4f;
    const int ColdTemp = 20;
    const int HotTemp = 1100;

    // A new mode flashes its tag, and a thug just torched flares his match for this long.
    const float FlashSeconds = 0.35f;
    const float FlareSeconds = 0.6f;

    // Right after a throw the tag says so for this long.
    const float ThrowShown = 0.6f;

    // A climb or sink faster than this shows as an arrow.
    const float ClimbShown = 50f;

    // Embers take this long to rise off the top edge, and rise this high.
    const float EmberSeconds = 2.4f;
    const float EmberRise = 40f;
    static readonly float[] EmberAt = { 0.2f, 0.46f, 0.69f, 0.88f };

    // Enemies are counted this close, this often. Height is looked for below him this far at
    // most, and this often.
    const float NearReach = 1500f;
    const float CountEvery = 0.5f;
    const float HeightReach = 50000f;
    const float HeightEvery = 0.1f;

    static float _nextCount;
    static int _near;
    static float _nextHeight;
    static float _height;
    static float _speed;
    static float _climb;
    static float _heat;
    static bool _airborne;
    static int _fires;
    static string _mode = "";
    static long _modeAt;

    // Thugs he has knocked down and vehicles he has set alight while he has been on.
    static int _torched;
    static float _torchedAt = -100f;
    static int _carsLit;

    static readonly string[] Digits = Enumerable.Range(0, 10).Select(n => n.ToString()).ToArray();
    static readonly string[] Counts = Enumerable.Range(0, 1000).Select(n => n.ToString("00")).ToArray();
    static readonly string[] Speeds = Enumerable.Range(0, 1000).Select(n => n + " M/S").ToArray();
    static readonly string[] Metres = Enumerable.Range(0, 1000).Select(n => n + " M").ToArray();
    static readonly string[] Waits = Enumerable.Range(0, 100).Select(n => (n / 10f).ToString("0.0", CultureInfo.InvariantCulture) + " S").ToArray();
    static readonly string[] Temps = Enumerable.Range(0, (HotTemp - ColdTemp) / 10 + 1).Select(n => (ColdTemp + n * 10).ToString()).ToArray();
    static readonly string[] FlameLegend = Enumerable.Range(0, 5).Select(n => $"FLAME {n}").ToArray();
    static readonly string[] NearLegend = Enumerable.Range(0, 11).Select(n => n < 10 ? $"NEAR {n}" : "NEAR 9+").ToArray();
    static readonly string[] BlastLegend = Enumerable.Range(0, 4).Select(n => $"BLAST {n}").ToArray();
    static readonly string[] AirLegend = Enumerable.Range(0, 1000).Select(n => $"AIR {n} M").ToArray();

    static void ResetHud()
    {
        _torched = 0;
        _torchedAt = -100f;
        _carsLit = 0;
        _heat = 0f;
        _mode = "";
    }

    static void Torched()
    {
        _torched++;
        _torchedAt = World.Now;
    }

    static void ShowHud(RPawnPlayer player, Mode mode)
    {
        _heat = Math.Clamp(_heat + (_flaming ? World.Delta / HeatUp : -World.Delta / CoolDown), 0f, 1f);
        if (!Hud.On)
        {
            VillainHud.Show(null);
            return;
        }

        _airborne = mode != Mode.Grounded;
        _speed = player.Velocity.Length() / Teleport.UnitsPerMetre;
        _climb = _airborne ? player.Velocity.Z : 0f;
        if (World.Now >= _nextHeight)
        {
            _nextHeight = World.Now + HeightEvery;
            _height = !_airborne ? 0f
                : Teleport.Floor(player, player.Location, player.Location.Z, player.Location.Z - HeightReach, out var floor)
                    ? MathF.Max(0f, player.Location.Z - Teleport.Lift(player) - floor.Z) / Teleport.UnitsPerMetre
                    : 999f;
        }

        if (World.Now >= _nextCount)
        {
            _nextCount = World.Now + CountEvery;
            Look();
            _near = 0;
            foreach (var thug in Thugs)
            {
                if (Vector3.DistanceSquared(thug.Location, player.Location) <= NearReach * NearReach)
                {
                    _near++;
                }
            }
        }

        _fires = 0;
        foreach (var thrown in Grenades)
        {
            if (thrown.Burst)
            {
                _fires++;
            }
        }

        _mode = mode switch
        {
            _ when _flaming => "TORCHING",
            _ when World.Now < _nextThrow - ThrowWait + ThrowShown => "GRENADE",
            _ when Aiming => "AIMING",
            Mode.Flying => "FLYING",
            Mode.Hovering => "HOVER",
            _ => "GROUND",
        };

        VillainHud.Show(DrawHud);
    }

    static float Reload => Math.Clamp(1f - (_nextThrow - World.Now) / ThrowWait, 0f, 1f);

    // Triangles are always solid, so they are faded by mixing toward the panel.
    static Rgba Faded(Rgba colour) => Rgba.Mix(Solid, colour, Shapes.Opacity);

    static string _shownMode = "";

    static void DrawHud(float rise, float seconds)
    {
        var now = Stopwatch.GetTimestamp();
        var time = (float)(now / (double)Stopwatch.Frequency);
        var height = HeaderHeight + NozzleHeight + BurnHeight + KeysHeight;
        var x = HudLeft;
        var y = Draw.ScreenHeight - HudBottom - height + rise;
        var right = x + HudWidth - Inset;
        Shapes.Under = Solid;

        Embers(x, y, time);
        Draw.Rect(x, y, HudWidth, height, Ink);
        Draw.Frame(x, y, HudWidth, height, 1f, Rule);
        Draw.Rect(x, y - 2f, HudWidth, 2f, Orange);

        Badge(x + Inset + BadgeWidth / 2f, y + HeaderHeight / 2f, time);
        Header(x + Inset + BadgeWidth + 12f, y, right, now);

        var row = y + HeaderHeight;
        Draw.Rect(x, row, HudWidth, 1f, Rule);
        Nozzle(x + Inset, row + 8f, right, time);

        row += NozzleHeight;
        Burns(x + Inset, row + 2f, right, time);

        row += BurnHeight;
        Draw.Rect(x, row, HudWidth, 1f, Rule);
        Keys(x + Inset, row + 7f, right);
    }

    // Sparks lift off the top edge and fade as they rise. Cold they are faint.
    static void Embers(float x, float y, float time)
    {
        var glow = 0.3f + 0.7f * _heat;
        for (var i = 0; i < EmberAt.Length; i++)
        {
            var phase = (time / EmberSeconds + i * 0.37f) % 1f;
            var size = i % 2 == 0 ? 3f : 2f;
            var ex = x + HudWidth * EmberAt[i] + MathF.Sin(time * 1.7f + i * 2f) * 4f * phase;
            var ey = y - 2f - phase * EmberRise;
            var colour = i % 2 == 0 ? Yellow : Orange;
            Draw.Rect(ex, ey, size, size, colour with { A = (byte)(255f * glow * (1f - phase)) });
        }
    }

    // The fire diamond. Its top burns brighter while he fires, and its right lights up once the
    // grenade is ready.
    static void Badge(float cx, float cy, float time)
    {
        var level = Math.Clamp((int)MathF.Ceiling(_heat * 4f - 0.01f), 0, 4);
        var ready = Reload >= 1f;
        var blast = ready ? 3 : Math.Clamp((int)(Reload * 3f), 0, 2);
        var top = _flaming ? Rgba.Mix(Red, Blaze, 0.5f + 0.5f * MathF.Sin(time * 9f)) : Red;

        Diamond(cx, cy - BadgeStep, top);
        Diamond(cx - BadgeStep, cy, Blue);
        Diamond(cx + BadgeStep, cy, ready ? Yellow : Brass);
        Diamond(cx, cy + BadgeStep, Cream);

        Centred(Digits[level], cx, cy - BadgeStep - 7f, 13f, Solid);
        Centred(Digits[Math.Min(_near, 9)], cx - BadgeStep, cy - 7f, 13f, Solid);
        Centred(Digits[blast], cx + BadgeStep, cy - 7f, 13f, ready ? Solid : Dim);
        var where = _airborne ? "AIR" : "GND";
        var width = Draw.TrackedWidth(where, 7.5f, 0.5f);
        Draw.Tracked(where, cx - width / 2f, cy + BadgeStep - 4f, 7.5f, 0.5f, Solid);
    }

    static void Diamond(float cx, float cy, Rgba colour)
    {
        Draw.Quad(cx, cy - BadgeHalf, cx + BadgeHalf, cy, cx, cy + BadgeHalf, cx - BadgeHalf, cy, Faded(colour));
    }

    static void Centred(string text, float cx, float y, float size, Rgba colour)
    {
        Draw.Text(text, cx - Draw.TextWidth(text, size) / 2f, y, size, colour);
    }

    static void Header(float x, float y, float right, long now)
    {
        Draw.Tracked("FIREFLY", x, y + 7f, 15f, 3f, Text);
        Draw.Tracked("PYRO RIG · G. LYNNS · HANDLE WITH CARE", x, y + 25f, 7.5f, 1.2f, Dim);

        if (_mode != _shownMode)
        {
            _shownMode = _mode;
            _modeAt = now;
        }

        var flash = 1f - Math.Clamp((float)((now - _modeAt) / (double)Stopwatch.Frequency) / FlashSeconds, 0f, 1f);
        var tagWidth = Draw.TrackedWidth(_mode, 11f, 2.5f) + 14f;
        var tagX = right - tagWidth;
        if (flash > 0f || _flaming)
        {
            var fill = _flaming ? 50f + 40f * MathF.Sin((float)(now / (double)Stopwatch.Frequency) * 8f) : 0f;
            Draw.Rect(tagX, y + 6f, tagWidth, 17f, Orange with { A = (byte)Math.Clamp(MathF.Max(flash * 120f, fill), 0f, 255f) });
        }

        Draw.Frame(tagX, y + 6f, tagWidth, 17f, 1f, Yellow);
        Draw.Tracked(_mode, tagX + 7f, y + 9f, 11f, 2.5f, Yellow);

        // What each corner of the badge means, with its number.
        var reload = Reload;
        var blast = reload >= 1f ? 3 : Math.Clamp((int)(reload * 3f), 0, 2);
        var level = Math.Clamp((int)MathF.Ceiling(_heat * 4f - 0.01f), 0, 4);
        var step = (right - x) / 4f;
        var ly = y + 45f;
        Legend(x, ly, Red, FlameLegend[level]);
        Legend(x + step, ly, Blue, NearLegend[Math.Min(_near, 10)]);
        Legend(x + step * 2f, ly, reload >= 1f ? Yellow : Brass, BlastLegend[blast]);
        Legend(x + step * 3f, ly, Cream, _airborne ? AirLegend[Math.Clamp((int)MathF.Round(_height), 0, AirLegend.Length - 1)] : "ON FOOT");
    }

    static void Legend(float x, float y, Rgba colour, string text)
    {
        Draw.Rect(x, y + 1f, 5f, 5f, colour);
        Draw.Tracked(text, x + 9f, y, 7f, 1f, Dim);
    }

    // The nozzle's heat, a bar that runs from scorched red to white hot. A flame licks up off
    // its end while he fires.
    static void Nozzle(float x, float y, float right, float time)
    {
        Draw.Tracked("NOZZLE", x, y + 5f, 7.5f, 1.5f, Dim);
        var unit = Draw.TextRight("°C", right, y + 1f, 11f, Dim);
        var temp = Temps[(int)MathF.Round(_heat * (Temps.Length - 1))];
        var start = Draw.TextRight(temp, unit - 3f, y, 15f, _heat >= 0.5f ? Yellow : Text);
        if (_flaming)
        {
            Draw.Triangle(start - 12f, y + 11f, start - 4f, y + 11f, start - 8f, y + 4f, Faded(Orange));
        }
        else if (_heat > 0f)
        {
            Draw.Triangle(start - 12f, y + 5f, start - 4f, y + 5f, start - 8f, y + 12f, Faded(Dim));
        }

        var barY = y + 20f;
        var barWidth = right - x;
        const float BarHeight = 8f;
        Draw.Rect(x, barY, barWidth, BarHeight, Dark);
        Ramp(x, barY, barWidth, BarHeight, _heat);
        for (var i = 1; i < 4; i++)
        {
            Draw.Rect(x + barWidth * i / 4f - 1f, barY, 2f, BarHeight, Solid);
        }

        if (_flaming && _heat > 0.02f)
        {
            var tip = x + barWidth * _heat;
            var lick = 1f + 0.22f * MathF.Sin(time * 21f) + 0.12f * MathF.Sin(time * 13f);
            Draw.Triangle(tip - 6f, barY + BarHeight, tip + 6f, barY + BarHeight, tip, barY - 9f * lick, Faded(Yellow));
            Draw.Triangle(tip - 3f, barY + BarHeight, tip + 3f, barY + BarHeight, tip, barY - 2f * lick, Faded(White));
        }
    }

    // The heat colours along the bar, filled up to an amount.
    static readonly (float At, Rgba Colour)[] HeatStops =
    {
        (0f, Ember), (0.45f, Red), (0.75f, Orange), (1f, Yellow),
    };

    static void Ramp(float x, float y, float width, float height, float amount)
    {
        for (var i = 1; i < HeatStops.Length && HeatStops[i - 1].At < amount; i++)
        {
            var (from, fromColour) = HeatStops[i - 1];
            var (to, toColour) = HeatStops[i];
            var end = MathF.Min(to, amount);
            var endColour = Rgba.Mix(fromColour, toColour, (end - from) / (to - from));
            Draw.GradientAcross(x + width * from, y, width * (end - from), height, fromColour, endColour);
        }
    }

    // Thugs torched as a row of matches, then cars lit, his fires still burning, and how he flies.
    static void Burns(float x, float y, float right, float time)
    {
        var side = right - SideWidth;
        var columnRight = side - 12f;
        Draw.Tracked("TORCHED", x, y + 1f, 7.5f, 1.5f, Dim);
        Draw.TextRight(Counts[Math.Clamp(_torched, 0, Counts.Length - 1)], columnRight, y - 1f, 10f, Text);

        var lit = Math.Min(_torched, 10);
        var flare = 1f - Math.Clamp((World.Now - _torchedAt) / FlareSeconds, 0f, 1f);
        var step = (columnRight - x) / 10f;
        for (var i = 0; i < 10; i++)
        {
            var mx = x + step * i + step / 2f;
            var burnt = i < lit;
            Draw.Rect(mx - 1f, y + 22f, 2.5f, 13f, burnt ? Charred : MatchStick);
            Draw.Place(mx + 0.25f, y + 20f, 1f);
            if (burnt && i == lit - 1 && flare > 0f)
            {
                Shapes.Disc(3.5f + 4f * flare, Yellow, flare * 0.7f);
            }

            var head = burnt ? Rgba.Mix(Orange, Yellow, 0.5f + 0.5f * MathF.Sin(time * 9f + i * 1.7f)) : MatchHead;
            Shapes.Disc(3.5f, head);
            Draw.Place(0f, 0f, 1f);
        }

        Draw.Rect(side - 6f, y, 1f, BurnHeight - 6f, Rule);

        // Up to three little flames for the vehicles he has lit, then the count.
        Draw.Tracked("CARS LIT", side, y + 1f, 7.5f, 1.5f, Dim);
        var count = Draw.TextRight(Counts[Math.Clamp(_carsLit, 0, Counts.Length - 1)], right, y - 1f, 10f, Text);
        for (var i = 0; i < 3; i++)
        {
            var fx = count - 8f - (2 - i) * 9f;
            var on = i >= 3 - Math.Min(_carsLit, 3);
            var tall = on ? 9f * (1f + 0.2f * MathF.Sin(time * 17f + i * 2.3f)) : 7f;
            Draw.Triangle(fx - 3.5f, y + 9f, fx + 3.5f, y + 9f, fx, y + 9f - tall, Faded(on ? Orange : Unlit));
        }

        Draw.Tracked("FIRES", side, y + 14f, 7.5f, 1.5f, Dim);
        Draw.TextRight(Digits[Math.Min(_fires, 9)], right, y + 12f, 10f, _fires > 0 ? Yellow : Text);

        // How fast and how high, with an arrow while he climbs or sinks.
        Draw.Tracked(_airborne ? "AIR" : "FOOT", side, y + 27f, 7.5f, 1.5f, Dim);
        var ax = right;
        if (_climb > ClimbShown)
        {
            Draw.Triangle(right - 7f, y + 33f, right, y + 33f, right - 3.5f, y + 27f, Faded(Yellow));
            ax -= 10f;
        }
        else if (_climb < -ClimbShown)
        {
            Draw.Triangle(right - 7f, y + 27f, right, y + 27f, right - 3.5f, y + 33f, Faded(Dim));
            ax -= 10f;
        }

        var shown = _airborne ? Draw.TextRight(Metres[Math.Clamp((int)MathF.Round(_height), 0, Metres.Length - 1)], ax, y + 25f, 9.5f, Text) - 4f : ax;
        if (_airborne)
        {
            shown = Draw.TextRight("·", shown, y + 25f, 9.5f, Dim) - 4f;
        }

        Draw.TextRight(Speeds[Math.Clamp((int)MathF.Round(_speed), 0, Speeds.Length - 1)], shown, y + 25f, 9.5f, Text);
    }

    // His weapons on the first row, and his moves for the moment on the second.
    static void Keys(float x, float y, float right)
    {
        var pad = Controls.LastDevice == Device.Pad;
        var aim = pad ? Pad.Describe(Pad.Button.LT) : "RMB";
        var fire = pad ? Pad.Describe(Pad.Button.RT) : "LMB";
        var secondary = pad ? Pad.Describe(Pad.Button.RB) : "MMB";

        var at = Key(x, y, fire, "FLAME", _flaming, !Aiming);

        // The grenade's reload goes round a ring before its key.
        var reload = Reload;
        var ready = reload >= 1f;
        Draw.Place(at + 6f, y + 6f, 1f);
        Shapes.Ring(4.5f, 6f, Dark);
        Shapes.Arc(4.5f, 6f, 0f, 360f * reload, ready ? Yellow : Dim);
        if (ready)
        {
            Shapes.Disc(2f, Yellow);
        }

        Draw.Place(0f, 0f, 1f);
        at = Key(at + 16f, y, secondary, "GRENADE", false, !Aiming || !ready);
        if (!ready)
        {
            Draw.Text(Waits[Math.Clamp((int)MathF.Ceiling((_nextThrow - World.Now) * 10f), 0, Waits.Length - 1)], at - 6f, y + 1f, 9f, Dim);
        }

        Key(right - AimWidth(aim), y, aim, "AIM", Aiming, false);

        var row = y + 15f;
        if (!_airborne)
        {
            var end = Key(x, row, Hotkeys.KeyText(TakeOffKey), "TAKE OFF", false, false);
            Draw.Tracked("TAP TWICE", end - 4f, row + 2f, 7.5f, 1f, Dim);
            return;
        }

        at = Key(x, row, Hotkeys.KeyText(FlyKey), "FLY", _flying, false);
        at = Key(at, row, Hotkeys.KeyText(TakeOffKey), "UP", Hotkeys.IsHeld(TakeOffKey), false);
        Key(at, row, Hotkeys.KeyText(DescendKey), "DOWN", Hotkeys.IsHeld(DescendKey), false);
    }

    static float AimWidth(string key) => Draw.TextWidth(key, 9f) + 8f + 5f + Draw.TrackedWidth("AIM", 8.5f, 1f);

    // A key chip and what it does. Lit it fills in, waiting it dims. Returns where the next one goes.
    static float Key(float x, float y, string key, string action, bool lit, bool waiting)
    {
        if (key.Length == 0)
        {
            return x;
        }

        var chip = Draw.TextWidth(key, 9f) + 8f;
        if (lit)
        {
            Draw.Rect(x, y, chip, 12f, Orange);
        }
        else
        {
            Draw.Frame(x, y, chip, 12f, 1f, waiting ? Chip with { A = 110 } : Chip);
        }

        Draw.Text(key, x + 4f, y + 1f, 9f, lit ? Solid : waiting ? Dim : Text);
        var end = Draw.Tracked(action, x + chip + 5f, y + 2f, 8.5f, 1f, lit ? Yellow : waiting ? Dim : Text);
        return end + 12f;
    }
}
