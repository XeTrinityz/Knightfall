#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// The Arkham Knight's sniper rifle. While it is up its red laser runs from the barrel to whatever
// it lands on, as the militia snipers' lasers do. A shot is one heavy round that puts down the
// first thug it reaches and the one behind him, hurts drones and tanks, and breaks what it hits.
// The bolt is worked after each shot.
//
// The laser is his own, from the cutscene his rifle comes from. The flash, the round's trail and
// the sounds are the militia sniper's, from the stance package.
static partial class ArkhamKnight
{
    const string LaserPath = "VFX_Cinematics.Particles.SniperBeam_Cine";
    const string FlashPath = "VFX_Weapon.Particles.Muzzle_Flash_MASTER";
    const string TrailPath = "VFX_Weapon.Particles.SniperRifle_Beam";
    const string FireSoundPath = "Ww_WPN_SniperRifle.Play_SniperRifleFire";
    const string LaserSoundPath = "Ww_WPN_SniperRifle.Play_SniperRifleLaser";
    const string BoltSoundPath = "Ww_WPN_SniperRifle.Play_SniperRifleReload";

    static readonly FName BeamLength = new("BeamLength");

    // How far the laser and a round reach.
    const float Range = 12000f;

    // A thug counts as under the crosshair, and as struck, this much bigger than his body. He is
    // looked for from this far out from the camera, past the Knight's own shoulders.
    const float AimSlack = 12f;
    const float NearestThug = 120f;

    // Whether the crosshair is on a thug this tick, and when a round last struck one.
    static bool _onThug;
    static float _hitAt = -10f;

    const float ShotEvery = 1.5f;

    // The flash and the trail are made to run while a gun keeps firing, so each is put out after
    // a moment.
    const float FlashSeconds = 0.08f;
    const float TrailSeconds = 0.15f;
    const float BoltAfter = 0.45f;
    const float KickDegrees = 6f;

    // A round goes through this many thugs, and each is thrown this hard along it, a little up.
    const int Through = 2;
    const int ThugDamage = 1000;
    const float ThugThrow = 1100f;
    const float ThugLift = 0.25f;

    // How wide a vehicle is to a round, and the share of its full health a round takes.
    const float VehicleRadius = 180f;
    const float VehicleShare = 0.25f;

    // How near the round's end breaks things.
    const float Splash = 40f;

    // Thugs this near hear the shot.
    const float Hearing = 5000f;

    static ParticleSystem? _sightLook;
    static ParticleSystem? _flashLook;
    static ParticleSystem? _trailLook;
    static AkEvent? _fireSound;
    static AkEvent? _laserSound;
    static AkEvent? _boltSound;
    static ParticleSystemComponent? _laser;
    static RPawnPlayer? _laserOn;
    static RPawnPlayer? _humming;
    static float _nextShot;
    static float _boltAt = -1f;

    static readonly List<(Emitter Effect, float Until)> Fading = new();
    static readonly HashSet<nint> Struck = new();
    static readonly List<(float Ahead, RPawnVillain Thug)> InLine = new();

    static bool KitLoaded() =>
        Game.FindObject<ParticleSystem>(FlashPath) is { IsValid: true } && Game.FindObject<ParticleSystem>(TrailPath) is { IsValid: true }
        && Game.FindObject<AkEvent>(LaserSoundPath) is { IsValid: true } && Game.FindObject<AkEvent>(BoltSoundPath) is { IsValid: true };

    static void LoadRifleKit()
    {
        _sightLook = Kept(Game.FindObject<ParticleSystem>(LaserPath));
        LearnLaser();
        _flashLook = Kept(Game.FindObject<ParticleSystem>(FlashPath));
        _trailLook = Kept(Game.FindObject<ParticleSystem>(TrailPath));
        _fireSound = Kept(Game.FindObject<AkEvent>(FireSoundPath));
        _laserSound = Kept(Game.FindObject<AkEvent>(LaserSoundPath));
        _boltSound = Kept(Game.FindObject<AkEvent>(BoltSoundPath));
        if (_sightLook is null)
        {
            Log.Warn("arkham_knight.laser", "The sniper's laser wasn't found, so the Arkham Knight's rifle has no laser.");
        }

    }


    static void Shoot(RPawnPlayer player, RPlayerController controller)
    {
        var now = World.Now;
        PutOutFaded(false);
        if (_boltAt >= 0f && now >= _boltAt)
        {
            _boltAt = -1f;
            Play(player, _boltSound);
        }

        // The laser and the strike it marks for are the rifle's alone.
        if (_pistols)
        {
            Laser(player, null);
            ShootPistols(player);
            return;
        }

        var barrel = Arms.Raised >= 0.9f ? Barrel() : null;
        Laser(player, barrel);
        if (!Aim.Fire || now < _nextShot || barrel is not { } muzzle)
        {
            return;
        }

        _nextShot = now + ShotEvery;
        _boltAt = now + BoltAfter;
        Arms.Kick(KickDegrees);

        var from = muzzle.From;
        var way = Way(from, muzzle.Way);
        var reach = Round(player, from, way);
        Brief(_flashLook, from, muzzle.Way, FlashSeconds, null);
        Brief(_trailLook, from, way, TrailSeconds, from + way * reach);

        Play(player, _fireSound);
        Villains.Alert(player, from, Hearing);
    }

    // Rounds and the laser go from the muzzle to the crosshair, not the way the barrel happens to
    // point.
    static Vector3 Way(Vector3 from, Vector3 barrel)
    {
        var aim = AimPoint(from) - from;
        return aim.LengthSquared() > 1f ? Vector3.Normalize(aim) : barrel;
    }

    // The crosshair's own spot. The camera's line only stops on walls and the ground, so a thug
    // under the crosshair is looked for along it, nearest first. Aimed at the wall behind him, a
    // round from the gun off to one side of the camera would pass him by up close.
    static Vector3 AimPoint(Vector3 from)
    {
        var point = Aim.PointFor(from);
        var eye = Aim.Eye;
        var look = Aim.Look;
        var nearest = Vector3.Dot(point - eye, look);
        _onThug = false;
        foreach (var thug in Crowd.StandingEnemies())
        {
            if (thug.IsIncapacitated())
            {
                continue;
            }

            if (!Villains.Crosses(thug, eye, look, AimSlack, out var enter, out var leave) || enter < NearestThug || enter >= nearest)
            {
                continue;
            }

            // The middle of the stretch inside him, so the round from the gun goes through him
            // too.
            nearest = enter;
            point = eye + look * ((enter + leave) * 0.5f);
            _onThug = true;
        }

        return point;
    }

    // The thugs along a line before the first wall, nearest first, and how far the line runs.
    static float Line(RPawnPlayer player, Vector3 from, Vector3 way)
    {
        var walled = Blink.Aim(player, from, from + way * Range, out var wall, out _);
        var reach = walled ? Vector3.Dot(wall - from, way) : Range;

        InLine.Clear();
        foreach (var thug in Crowd.StandingEnemies())
        {
            if (thug.IsIncapacitated())
            {
                continue;
            }

            // He is in the way if the line enters him before the wall. How far is to the middle
            // of him, where a round stops and the laser lands.
            if (Villains.Crosses(thug, from, way, AimSlack, out var enter, out var leave) && enter <= reach)
            {
                InLine.Add((MathF.Min((enter + leave) * 0.5f, reach), thug));
            }
        }

        InLine.Sort((a, b) => a.Ahead.CompareTo(b.Ahead));
        return reach;
    }

    // How far the round got.
    static float Round(RPawnPlayer player, Vector3 from, Vector3 way)
    {
        var reach = Line(player, from, way);
        Struck.Clear();

        // A drone or tank in the way takes the round before anything behind it.
        var hit = VehicleInLine(from, way, reach);
        if (hit is { } blocking)
        {
            reach = blocking.Ahead;
            InLine.RemoveAll(each => each.Ahead > reach);
        }

        var throwWay = Vector3.Normalize(way + new Vector3(0f, 0f, ThugLift));
        var count = Math.Min(Through, InLine.Count);
        for (var i = 0; i < count; i++)
        {
            Villains.Knock(player, InLine[i].Thug, ThugDamage, throwWay, ThugThrow, true);
        }

        if (count > 0)
        {
            _hitAt = World.Now;
        }

        if (count > 0)
        {
            return InLine[count - 1].Ahead;
        }

        if (hit is { } struck)
        {
            var damage = Math.Max(1, (int)MathF.Round(Math.Max(struck.Car.Health, struck.Car.HealthMax) * VehicleShare));
            struck.Car.TakeDamage(damage, player.Controller, from + way * reach, way * 100f, RDmgType_Explosion.StaticClass(), default, player);
            return reach;
        }

        if (reach < Range)
        {
            Villains.Break(player, from + way * reach, Splash, Struck);
        }

        return reach;
    }

    // An effect from the game's pool, put out after a moment. A beam is told where it ends.
    static void Brief(ParticleSystem? look, Vector3 at, Vector3 way, float seconds, Vector3? end)
    {
        if (look is not { IsValid: true } || !Game.TryGetWorldInfo(out var world) || world.GRI?.ObjectPool is not { IsValid: true } pool
            || pool.SpawnEmitter(look, at, Rotator.FromDirection(way), 0, 1f) is not { IsValid: true } effect)
        {
            return;
        }

        if (end is { } point && effect.ParticleSystemComponent is { IsValid: true } beam)
        {
            beam.SetFloatParameter(BeamLength, MathF.Max(Vector3.Distance(at, point), 0.1f));
            EndAt(beam, look, at, point);
        }

        Fading.Add((effect, World.Now + seconds));
    }

    // A beam is told where it starts and ends. One made to start from a point set by hand would
    // otherwise start wherever the pooled effect was last left. One that isn't told runs out
    // along its turn.
    static void EndAt(ParticleSystemComponent beam, ParticleSystem look, Vector3 from, Vector3 point)
    {
        var emitters = look.Emitters.Count;
        for (var i = 0; i < emitters; i++)
        {
            beam.SetBeamSourcePoint(i, from, 0);
            beam.SetBeamEndPoint(i, point);
            beam.SetBeamTargetPoint(i, point, 0);
        }
    }

    static void PutOutFaded(bool all)
    {
        for (var i = Fading.Count - 1; i >= 0; i--)
        {
            var (effect, until) = Fading[i];
            if (!all && World.Now < until)
            {
                continue;
            }

            if (effect.IsValid && effect.ParticleSystemComponent is { IsValid: true } shown)
            {
                shown.DeactivateSystem();
            }

            Fading.RemoveAt(i);
        }
    }

    // The nearest hostile drone, tank or car the line passes through.
    static (float Ahead, RVehicleNPC Car)? VehicleInLine(Vector3 from, Vector3 way, float reach)
    {
        (float Ahead, RVehicleNPC Car)? best = null;
        foreach (var car in Game.FindObjects<RVehicleNPC>())
        {
            if (car is not { IsValid: true, IsClassDefaultObject: false, bDeleteMe: false, bHidden: false, DroneHacked: false, IsFriendly: false, Health: > 0 })
            {
                continue;
            }

            var ahead = Vector3.Dot(car.Location - from, way);
            if (ahead < 0f || ahead > reach + VehicleRadius || (best is { } near && ahead >= near.Ahead))
            {
                continue;
            }

            if (Vector3.DistanceSquared(from + way * ahead, car.Location) <= VehicleRadius * VehicleRadius)
            {
                best = (Math.Max(0f, ahead - VehicleRadius * 0.5f), car);
            }
        }

        return best;
    }

    // The laser runs to the first thug or wall along the aim, and hums while it is on.
    static void Laser(RPawnPlayer player, (Vector3 From, Vector3 Way)? barrel)
    {
        var on = barrel is not null;
        if (on && (_laserOn is not { IsValid: true } || _laserOn.Ptr != player.Ptr))
        {
            Unlaser();
            _laser = Effect(player, _sightLook);
            _laserOn = player;
        }

        Hum(on ? player : null);
        if (!on)
        {
            Off(_laser);
            _markAt = null;
            return;
        }

        var (from, barrelWay) = barrel!.Value;
        var way = Way(from, barrelWay);
        var reach = Line(player, from, way);
        if (InLine.Count > 0)
        {
            reach = InLine[0].Ahead;
        }

        if (VehicleInLine(from, way, reach) is { } car)
        {
            reach = car.Ahead;
        }

        // The beam is drawn out from where it is placed, so it sits at the muzzle.
        var length = MathF.Max(reach, 1f);
        _markAt = reach < Range ? from + way * reach : null;
        if (On(_laser, from, Rotator.FromDirection(way)) is { } beam)
        {
            Stretch(beam, length);
        }
    }

    // An effect that stays on the player, placed in the world by hand.
    static ParticleSystemComponent? Effect(RPawnPlayer player, ParticleSystem? look)
    {
        if (look is not { IsValid: true })
        {
            return null;
        }

        var effect = Made.Tracked(new ParticleSystemComponent(player));
        effect.SetTemplate(look);
        effect.SetAbsolute(true, true, false);
        player.AttachComponent(effect);
        return effect;
    }

    static ParticleSystemComponent? On(ParticleSystemComponent? effect, Vector3 at, Rotator turn)
    {
        if (effect is not { IsValid: true })
        {
            return null;
        }

        effect.SetTranslation(at);
        effect.SetRotation(turn);
        if (!effect.bIsActive)
        {
            effect.ActivateSystem(false, false);
        }

        return effect;
    }

    static void Off(ParticleSystemComponent? effect)
    {
        if (effect is { IsValid: true, bIsActive: true })
        {
            effect.DeactivateSystem();
        }
    }

    static void Hum(RPawnPlayer? player)
    {
        var wanted = player is not null && _laserSound is { IsValid: true } ? player : null;
        if (ReferenceEquals(wanted, _humming))
        {
            return;
        }

        if (_humming is { IsValid: true } was && _laserSound is { IsValid: true } sound)
        {
            was.RemoveLoopingAudioEvent(sound);
        }

        _humming = wanted;
        _humming?.AddLoopingAudioEvent(_laserSound!);
    }

    static void Unlaser()
    {
        if (_laser is { IsValid: true } shown && _laserOn is { IsValid: true } owner)
        {
            shown.DeactivateSystem();
            owner.DetachComponent(shown);
        }

        _laser = null;
        _laserOn = null;
    }

    static void Play(RPawnPlayer player, AkEvent? sound)
    {
        if (sound is { IsValid: true })
        {
            player.StartAudioEvent(sound);
        }
    }

    static void StopRifle()
    {
        PutOutFaded(true);
        Hum(null);
        Unlaser();
        _boltAt = -1f;
    }

    static void UnloadRifleKit()
    {
        StopRifle();
        _nextShot = 0f;
        ForgetLaser();
        _sightLook = null;
        _flashLook = null;
        _trailLook = null;
        _fireSound = null;
        _laserSound = null;
        _boltSound = null;
        Struck.Clear();
        InLine.Clear();
    }
}
