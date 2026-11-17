#nullable enable

using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// The colour of the headlights, a part of its own in the car's PAINT list. The lamp rings glow in
// the colour the game keeps for normal driving and for battle mode, and it writes the one in use
// to the lamp material whenever the mode changes. The glow and streaks around them are lens
// flares in their own colour, and the light thrown on the road is each light's own colour.
// Brightness scales the lamp glow, since the game fades the lights themselves.
static class Beams
{
    static readonly FName GlowParam = new("CodeHeadlightColor");

    static readonly Dictionary<nint, (byte R, byte G, byte B)> Originals = new();

    // Normal driving's white is too faint to show a colour in the rings, so a colour gets this much.
    const float DrivingLevel = 3f;

    static readonly Dictionary<nint, GameObject.FLinearColor> Flares = new();

    static nint _car;
    static GameObject.FLinearColor _pursuit;
    static GameObject.FLinearColor _battle;

    public static SuitPart Part(RVehicleBatmobileBase car) => new("car_headlights", "HEADLIGHTS") { Custom = look => Apply(car, look) };

    static void Apply(RVehicleBatmobileBase car, SuitLook look)
    {
        if (!car.IsAlive)
        {
            Originals.Clear();
            Flares.Clear();
            _car = 0;
            return;
        }

        Glow(car, look);

        var swatch = SuitLook.Palette[Math.Clamp(look.Colour, 0, SuitLook.Palette.Length - 1)];
        var strength = (float)(look.Strength / 100);
        var lights = car.VehicleLights;
        for (var i = 0; i < lights.Count; i++)
        {
            var slot = lights[i];
            if (slot.Type is not (RVehicle.VehicleLightType.VEHLT_HeadLight or RVehicle.VehicleLightType.VEHLT_BattleModeLight)
                || slot.LightSource is not { IsValid: true } source)
            {
                continue;
            }

            ref var colour = ref source.LightColor;
            if (!Originals.TryGetValue(source.Ptr, out var was))
            {
                was = (colour.R, colour.G, colour.B);
                Originals[source.Ptr] = was;
            }

            var own = look.IsPlain || look.Colour == 0;
            var (r, g, b) = own
                ? was
                : (Lerp(was.R, swatch.R, strength), Lerp(was.G, swatch.G, strength), Lerp(was.B, swatch.B, strength));
            if (colour.R != r || colour.G != g || colour.B != b)
            {
                colour.R = r;
                colour.G = g;
                colour.B = b;
                source.OnUpdatePropertyLightColor();
            }
        }

        if (look.IsPlain)
        {
            Originals.Clear();
        }
    }

    // Sets the car's two lamp colours and shows the one in use.
    static void Glow(RVehicleBatmobileBase car, SuitLook look)
    {
        if (_car != car.Ptr)
        {
            if (look.IsPlain)
            {
                return;
            }

            (_car, _pursuit, _battle) = (car.Ptr, car.HeadLightsColourPursuit, car.HeadLightsColourBattle);
        }

        var custom = !look.IsPlain && look.Colour != 0;
        var pursuit = look.IsPlain ? _pursuit : Tinted(_pursuit, look, custom ? DrivingLevel : 0);
        var battle = look.IsPlain ? _battle : Tinted(_battle, look, 0);
        car.HeadLightsColourPursuit = pursuit;
        car.HeadLightsColourBattle = battle;
        if (car.HeadLightsMaterial is { IsValid: true } material)
        {
            var shown = car.IsInBattleMode() ? battle : pursuit;
            material.SetVectorParameterValue(GlowParam, out shown);
        }

        TintFlares(car, custom ? look : null);
        if (look.IsPlain)
        {
            _car = 0;
        }
    }

    // The lamps' glow and streaks are lens flares, drawn in the colour the flare is given, so they
    // take the colour too and go back as they were.
    static void TintFlares(RVehicleBatmobileBase car, SuitLook? look)
    {
        if (look is null && Flares.Count == 0)
        {
            return;
        }

        GameObject.FLinearColor target = default;
        if (look is { } custom)
        {
            var swatch = SuitLook.Palette[Math.Clamp(custom.Colour, 0, SuitLook.Palette.Length - 1)];
            var (r, g, b) = (Linear(swatch.R), Linear(swatch.G), Linear(swatch.B));
            var peak = Math.Max(r, Math.Max(g, b));
            var scale = peak > 0 ? (float)Math.Min(custom.Brightness, 2) / peak : 0;
            target = new GameObject.FLinearColor { R = r * scale, G = g * scale, B = b * scale, A = 1 };
        }

        var lights = car.VehicleLights;
        for (var i = 0; i < lights.Count; i++)
        {
            var slot = lights[i];
            if (slot.Type != RVehicle.VehicleLightType.VEHLT_HeadLight || slot.LensFlare is not { IsValid: true } flare)
            {
                continue;
            }

            var now = flare.SourceColor;
            if (look is not null)
            {
                Flares.TryAdd(flare.Ptr, now);
                if (now.R != target.R || now.G != target.G || now.B != target.B)
                {
                    flare.SetSourceColor(target);
                }
            }
            else if (Flares.TryGetValue(flare.Ptr, out var was))
            {
                flare.SetSourceColor(was);
            }
        }

        if (look is null)
        {
            Flares.Clear();
        }
    }

    // The colour at the strength the game gave the original, or at a strength of its own, so battle
    // mode stays far brighter than driving.
    static GameObject.FLinearColor Tinted(GameObject.FLinearColor was, SuitLook look, float strongest)
    {
        var level = strongest > 0 ? strongest : Math.Max(was.R, Math.Max(was.G, was.B));
        var bright = (float)look.Brightness;
        if (look.Colour == 0)
        {
            return new GameObject.FLinearColor { R = was.R * bright, G = was.G * bright, B = was.B * bright, A = was.A };
        }

        var swatch = SuitLook.Palette[Math.Clamp(look.Colour, 0, SuitLook.Palette.Length - 1)];
        var (cr, cg, cb) = (Linear(swatch.R), Linear(swatch.G), Linear(swatch.B));
        var peak = Math.Max(cr, Math.Max(cg, cb));
        var scale = peak > 0 ? level / peak : 0;
        var strength = (float)(look.Strength / 100);
        return new GameObject.FLinearColor
        {
            R = (was.R + (cr * scale - was.R) * strength) * bright,
            G = (was.G + (cg * scale - was.G) * strength) * bright,
            B = (was.B + (cb * scale - was.B) * strength) * bright,
            A = was.A,
        };
    }

    static float Linear(byte channel) => MathF.Pow(channel / 255f, 2.2f);

    static byte Lerp(byte from, byte to, float amount) => (byte)Math.Clamp(MathF.Round(from + (to - from) * amount), 0, 255);
}
