#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// BATMOBILE > DRIVING, the DASHBOARD rows. The speedometer is drawn by the theme, from what Fill
// puts in its view.
static class Dashboard
{
    // A unit is a centimetre.
    const float KmhPerUnit = 0.036f;
    const float MphPerUnit = 0.0223694f;
    const float FeetPerUnit = 0.0328084f;
    const float MetresPerUnit = 0.01f;

    // The hologram. It stands off the right side of the car about roof high, faces back towards
    // the chase camera, turned in a little, and leans back to face up at it. It is projected
    // from the front right wheel arch.
    const float AnchorSide = 165f;
    const float AnchorUp = 125f;
    const float AnchorBack = 30f;
    const float PanelTurn = 0.35f;
    const float PanelLean = 0.3f;
    const float WorldPerUnit = 0.85f;
    static readonly Vector3 Arch = new(170f, 120f, 45f);

    // How quickly the hologram swings round after the car turns, so it feels carried.
    const float Swing = 5f;

    // The speed bar is full at this.
    const float FullScaleKmh = 300f;

    // Shorter hops are bumps. Longer leaps are a rescue or a teleport, not a jump.
    const float JumpSeconds = 0.4f;
    const float JumpMost = 30000f;

    static readonly Held<RPlayerController, bool> AlwaysRadar = new("vehicle radar", controller => controller.bAlwaysShowVehicleRadar, (controller, on) => controller.bAlwaysShowVehicleRadar = on);

    static readonly Dictionary<int, string> Numbers = new();
    static readonly Dictionary<int, string> Kilometres = new();
    static readonly string[] Percents = Enumerable.Range(0, 101).Select(percent => percent + "%").ToArray();

    static float _best;
    static float _longest;
    static bool _flying;
    static float _flyingFor;
    static Vector3 _takeoff;
    static RPlayerController? _lentTo;
    static Vector3 _heading;

    public static readonly Toggle Speedometer = new(
        "batmobile.driving.speedometer", "SPEEDOMETER",
        "Shows your speed while you drive, with the car's armour and weapon power, and whether you are boosting or on autopilot. It takes the look of the menu theme.");

    public static readonly Choice Units = new(
        "batmobile.driving.speed_units", "SPEED UNITS",
        "Miles or kilometres an hour on the speedometer.",
        new[] { ("mph", "MPH"), ("kmh", "KM/H") })
    {
        Preference = true,
    };

    // In the same order as DashSpot.
    public static readonly Choice Position = new(
        "batmobile.driving.speedometer_position", "SPEEDOMETER POSITION",
        "ON THE CAR floats it beside the Batmobile. The others keep it in one spot on screen. The game's radar takes the bottom right in battle mode.",
        new[] { ("on_car", "ON THE CAR"), ("middle_right", "MIDDLE RIGHT"), ("middle_left", "MIDDLE LEFT"), ("bottom_left", "BOTTOM LEFT"), ("bottom_right", "BOTTOM RIGHT") })
    {
        Preference = true,
    };

    public static readonly Toggle Stats = new(
        "batmobile.driving.driving_stats", "DRIVING STATS",
        "Adds your best speed and longest jump since the game started to the speedometer.")
    {
        Preference = true,
    };

    public static readonly Command ResetStats = new(
        "batmobile.driving.reset_stats", "RESET DRIVING STATS",
        "Starts the best speed and longest jump again from nothing.",
        Reset);

    public static readonly Toggle RadarOnFoot = new(
        "batmobile.driving.radar_on_foot", "VEHICLE RADAR ON FOOT",
        "The minimap keeps showing militia drones and tanks after you get out of the Batmobile, not only the heavy tanks.")
    {
        Tick = HoldRadar,
        Restore = ReleaseRadar,
    };

    public static readonly Catalog.Section Section = new("DASHBOARD", Speedometer, Units, Position, Stats, ResetStats, RadarOnFoot);

    // The car to show the speedometer for, or null when it shouldn't show.
    public static RVehicleBatmobileBase? Showing()
    {
        if (!Speedometer.On || World.Driving is not { IsValid: true } car || World.Controller is not { } controller || controller.bCinematicMode)
        {
            return null;
        }

        // Hidden whenever the game hides its own HUD.
        return controller.myHUD is { IsValid: true, bShowHUD: false } ? null : car;
    }

    public static void Fill(DashView view, RVehicleBatmobileBase car, float seconds)
    {
        var kmh = Units.Index == 1;
        view.Spot = (DashSpot)Position.Index;

        // Upright whatever the car is doing, like a projection that steadies itself. Its heading
        // trails the car's a little, and snaps when the car has turned right round, like after a
        // teleport.
        var facing = car.Rotation.ToDirection();
        facing = Vector3.Normalize(new Vector3(facing.X, facing.Y, 0f));
        _heading = Vector3.Dot(_heading, facing) < 0.5f ? facing : Vector3.Normalize(Vector3.Lerp(_heading, facing, 1f - MathF.Exp(-Swing * seconds)));
        var ahead = _heading;
        var right = new Vector3(-ahead.Y, ahead.X, 0f);
        view.Anchor = car.Location + right * AnchorSide + Vector3.UnitZ * AnchorUp - ahead * AnchorBack;
        view.Across = (right * MathF.Cos(PanelTurn) - ahead * MathF.Sin(PanelTurn)) * WorldPerUnit;
        view.Down = (-Vector3.UnitZ * MathF.Cos(PanelLean) - ahead * MathF.Sin(PanelLean)) * WorldPerUnit;
        var nose = car.Rotation.ToDirection();
        nose = Vector3.Normalize(new Vector3(nose.X, nose.Y, 0f));
        view.Emitter = car.Location + nose * Arch.X + new Vector3(-nose.Y, nose.X, 0f) * Arch.Y + Vector3.UnitZ * Arch.Z;
        var speed = car.Velocity.Length();
        view.Speed = Number((int)MathF.Round(speed * (kmh ? KmhPerUnit : MphPerUnit)));
        view.Units = kmh ? "KM/H" : "MPH";
        view.SpeedFill = Math.Clamp(speed * KmhPerUnit / FullScaleKmh, 0f, 1f);

        view.Hot = false;
        view.Note = "";
        if (Autopilot.Active)
        {
            view.Mode = "AUTOPILOT";
            view.Note = Distance(Autopilot.Remaining(car.Location), kmh);
        }
        else if (car.IsInBattleMode())
        {
            view.Mode = "BATTLE";
        }
        else if (car.IsBoosting())
        {
            view.Mode = "BOOST";
            view.Hot = true;
        }
        else
        {
            view.Mode = "";
        }

        var levels = car.ArmourLevels;
        var most = car.HealthMax + (levels.Count == 0 ? 0 : levels[Math.Clamp(car.CurrentArmourLevel, 0, levels.Count - 1)]);
        view.Armour = most > 0 ? Math.Clamp(car.Health / (float)most, 0f, 1f) : 0f;
        view.ArmourText = Percents[(int)MathF.Round(view.Armour * 100f)];

        var power = car.MaxWeaponGeneratorPower;
        var level = car.CurrentGeneratorLevel;
        var full = level >= 0 && level < power.Count ? power[level] : 0f;
        view.Power = full > 0f ? Math.Clamp(car.WeaponGeneratorPower / full, 0f, 1f) : 0f;
        view.PowerText = Percents[(int)MathF.Round(view.Power * 100f)];

        view.Stats = Stats.On;
        if (view.Stats)
        {
            view.Best = Number((int)MathF.Round(_best * (kmh ? KmhPerUnit : MphPerUnit)));
            view.Jump = Number((int)MathF.Round(_longest * (kmh ? MetresPerUnit : FeetPerUnit))) + (kmh ? " M" : " FT");
        }
    }

    // Runs every game tick, while driving. Speed and jumps are kept whether or not the
    // speedometer is on, so switching it on shows the session so far.
    public static void Track()
    {
        if (World.Driving is not { IsValid: true } car || Car.Sim(car) is { Abseiling: true })
        {
            _flying = false;
            return;
        }

        _best = MathF.Max(_best, car.Velocity.Length());

        if (!car.HasWheelsOnGround())
        {
            if (!_flying)
            {
                _flying = true;
                _flyingFor = 0f;
                _takeoff = car.Location;
            }

            _flyingFor += World.Delta;
            return;
        }

        if (_flying)
        {
            _flying = false;
            var across = car.Location - _takeoff;
            var length = new Vector2(across.X, across.Y).Length();
            if (_flyingFor >= JumpSeconds && length < JumpMost)
            {
                _longest = MathF.Max(_longest, length);
            }
        }
    }

    static Outcome Reset()
    {
        _best = 0f;
        _longest = 0f;
        _flying = false;
        return Outcome.Done("RESET");
    }

    static string Number(int value)
    {
        value = Math.Max(0, value);
        if (!Numbers.TryGetValue(value, out var text))
        {
            text = value.ToString();
            Numbers[value] = text;
        }

        return text;
    }

    // Tenths of a mile or kilometre, or yards or metres under one.
    static string Distance(float units, bool kmh)
    {
        var tenths = (int)MathF.Round(units * (kmh ? MetresPerUnit / 100f : FeetPerUnit / 528f));
        if (tenths >= 10)
        {
            var key = tenths * 2 + (kmh ? 1 : 0);
            if (!Kilometres.TryGetValue(key, out var text))
            {
                text = (tenths / 10f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + (kmh ? " KM" : " MI");
                Kilometres[key] = text;
            }

            return text;
        }

        // Rounded to tens, so there are only a few of these.
        var near = (int)MathF.Round(units * (kmh ? MetresPerUnit : FeetPerUnit / 3f) / 10f) * 10;
        return Number(near) + (kmh ? " M" : " YD");
    }

    // The HUD only builds the drone radar while the player's car pointer is set, which it never
    // is on foot. The parked car is lent to it for just the part of the frame that builds the
    // radar, between the waypoint update and the forensics device drawing, and nothing else in
    // that part reads it.
    static void HoldRadar()
    {
        if (World.Controller is { } controller)
        {
            AlwaysRadar.Hold(controller, true);
        }
    }

    static void ReleaseRadar()
    {
        GiveBack();
        AlwaysRadar.Release();
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.UpdatePlayerWaypoint))]
    static void UpdatePlayerWaypoint(RPlayerController self)
    {
        Redirects.RunOriginal();
        if (!RadarOnFoot.On || World.Driving is not null || self.BatmobilePawn is { IsValid: true } || Car.Find() is not { } car)
        {
            return;
        }

        self.BatmobilePawn = car;
        _lentTo = self;
    }

    [Redirect(typeof(RForensicsDevice), nameof(RForensicsDevice.PreRender))]
    static void PreRender(RForensicsDevice self)
    {
        GiveBack();
        Redirects.RunOriginal();
    }

    static void GiveBack()
    {
        if (_lentTo is { IsValid: true } controller && World.Driving is null)
        {
            controller.BatmobilePawn = null!;
        }

        _lentTo = null;
    }
}
