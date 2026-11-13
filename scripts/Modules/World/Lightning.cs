#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// WORLD > WEATHER, the LIGHTNING rows, and PRE-SCARECROW ERA's still sky. Both decide whether the
// city's lightning strikes by itself, so this one place holds that switch for both.
static class Lightning
{
    // The story can change the lightning, so it is checked this often.
    const float CheckEvery = 1f;

    // How far off a strike in front of you lands when the game has no distance of its own.
    const float InFront = 12288f;

    // The crosshair looks this far for something to strike.
    const float Reach = 50000f;

    // Never closer than this, so a strike at a near wall isn't inside the camera.
    const float Nearest = 500f;

    // A shorter wait than this between strikes is just a flicker.
    const float Shortest = 0.5f;

    static readonly Held<RLightningActor, bool> Still = new(
        "lightning off", sky => sky.bRandomFlashesDisabled, (sky, off) => sky.bRandomFlashesDisabled = off);

    static readonly Held<RLightningActor, float> Wait = new(
        "lightning wait", sky => sky.MaxTimeBetweenStrikes, (sky, seconds) => sky.MaxTimeBetweenStrikes = seconds);

    static readonly Held<RLightningActor, float> Brightness = new(
        "lightning brightness", sky => sky.MaxLightningBrightness, (sky, bright) => sky.MaxLightningBrightness = bright);

    static readonly Held<RLightningActor, float> Strength = new(
        "lightning strength", sky => sky.MaxLightningStrength, (sky, strong) => sky.MaxLightningStrength = strong);

    static float _checkedAt = -1f;
    static int? _applied;

    public static readonly Command Strike = new(
        "world.weather.lightning_strike", "LIGHTNING STRIKE",
        "A bolt of lightning lights up the sky in front of you, with thunder.",
        () => StrikeAhead(null));

    public static readonly Command StrikeAim = new(
        "world.weather.strike_at_crosshair", "STRIKE AT CROSSHAIR",
        "Lightning strikes whatever the crosshair is on: an enemy, a car, a street or a roof.",
        StrikeAimed);

    public static readonly Slider Frequency = new(
        "world.weather.lightning_frequency", "LIGHTNING FREQUENCY",
        "How often lightning strikes by itself. 1X is the story's storm. OFF stops it, and the strike rows still work.",
        min: 0, max: 10, step: 0.5, value: 1, unit: "X", zero: "OFF")
    {
        Block = static () => Atmosphere.Era.On ? "NONE IN THE ERA" : null,
    };

    public static readonly Slider Bright = new(
        "world.weather.lightning_brightness", "LIGHTNING BRIGHTNESS",
        "How brightly each flash lights the city.",
        min: 0.25, max: 3, step: 0.25, value: 1, unit: "X");

    public static readonly Catalog.Section Section = new("LIGHTNING", Strike, StrikeAim, Frequency, Bright);

    public static void Tick()
    {
        var era = Atmosphere.Era.On;
        if (!era && Frequency.IsDefault && Bright.IsDefault)
        {
            if (_applied is not null)
            {
                Release();
            }

            return;
        }

        var wanted = HashCode.Combine(era, Frequency.Value, Bright.Value);
        if (wanted == _applied && MathF.Abs(World.Now - _checkedAt) < CheckEvery)
        {
            return;
        }

        _checkedAt = World.Now;
        _applied = wanted;
        if (Sky() is not { } sky)
        {
            return;
        }

        // Off for the era or OFF, on for a busier storm, and otherwise the story's own choice, so
        // brightness alone leaves a quiet night quiet.
        var times = (float)Frequency.Value;
        if (era || times <= 0f)
        {
            Still.Hold(sky, true);
        }
        else if (times > 1f)
        {
            Still.Hold(sky, false);
        }
        else
        {
            Still.Release();
        }

        if (!era && times > 0f && !Frequency.IsDefault)
        {
            Wait.Hold(sky, was => Math.Max(Shortest, was / times));

            // A shorter wait only counts from the next strike, so a far off one is brought in.
            if (sky.TimeToNextStrike > sky.MaxTimeBetweenStrikes)
            {
                sky.TimeToNextStrike = sky.MaxTimeBetweenStrikes * Random.Shared.NextSingle();
            }
        }
        else
        {
            Wait.Release();
        }

        var bright = (float)Bright.Value;
        Brightness.Hold(sky, was => was * bright);
        Strength.Hold(sky, was => was * bright);
    }

    public static void Release()
    {
        Still.Release();
        Wait.Release();
        Brightness.Release();
        Strength.Release();
        _applied = null;
    }

    static RLightningActor? Sky()
    {
        if (World.Info?.LightningEventListener is { IsValid: true } listening)
        {
            return listening;
        }

        foreach (var sky in Game.FindObjects<RLightningActor>())
        {
            if (sky.IsValid && !sky.IsClassDefaultObject)
            {
                return sky;
            }
        }

        return null;
    }

    static Outcome StrikeAhead(float? distance)
    {
        if (Sky() is not { } sky)
        {
            return Outcome.Failed("NO LIGHTNING HERE");
        }

        var far = distance ?? (World.Info is { onScreenJokerfy_LightningDist: > 0f } game ? game.onScreenJokerfy_LightningDist : InFront);
        sky.ManualTriggerLightingInFrontOfCamera(far);
        return Outcome.Done("STRIKE");
    }

    static Outcome StrikeAimed()
    {
        if (Sky() is not { } sky)
        {
            return Outcome.Failed("NO LIGHTNING HERE");
        }

        if (Teleport.Mover is not { } mover || World.Controller is not { } controller)
        {
            return Outcome.NoPlayer;
        }

        controller.GetPlayerViewPoint(out var eye, out var view);
        var hit = mover.Trace(out var spot, out _, eye + view.ToDirection() * Reach, eye, Component.ECollisionFilter.COF_TraceAll, Vector3.Zero, out _, 0);

        // A person or a car gets the bolt right on them. Anything else gets one at that distance.
        if (hit is Pawn { IsValid: true, bDeleteMe: false } target && !ReferenceEquals(target, mover) && !ReferenceEquals(target, World.Player))
        {
            sky.NextStrikeIntensity = 1f;
            sky.ManualTriggerLightningTargetActor(target);
            return Outcome.Done("STRIKE");
        }

        return StrikeAhead(hit is not null ? Math.Max(Nearest, Vector3.Distance(eye, spot)) : null);
    }
}
