#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.BmScript;
using BmSDK.Engine;

namespace Knightfall;

// The Joker's shotgun. A shot is the game's own Joker gun shot: its pellets, spread, range,
// damage, push and lift are read from the gun's defaults, thugs are hit the way it hits them,
// and its own flash, tracer, impact, sound and rumble are used. It holds six shells, pumps after
// each shot and reloads when empty.
static partial class JokerVillain
{
    const string GunDefaultsPath = "BmScript.Default__RJokerGunGadget";
    const string PumpSoundPath = "Ww_WPN_JokerShotGun.Play_SEQ_JokerBoss_Shotgun_Reload_A";
    const string LoadSoundPath = "Ww_WPN_JokerShotGun.Play_SEQ_JokerBoss_Shotgun_Reload_B";

    public const int Shells = 6;
    const float ShotEvery = 0.8f;
    const float PumpAfter = 0.35f;
    const float ReloadSeconds = 1.6f;
    const float KickDegrees = 9f;

    // Used when the gun's defaults give none.
    const float FallbackRange = 5000f;

    // Thugs this near hear the shot and come for him, as they do for the game's own gun.
    const float Hearing = 4000f;

    // How near a pellet's end breaks things and chips vehicles, and how much of a vehicle's
    // health a shot takes.
    const float Splash = 60f;
    const float CarShare = 0.03f;

    static RJokerGunGadget? _gunDefaults;
    static AkEvent? _pumpSound;
    static AkEvent? _loadSound;
    static int _shells = Shells;
    static float _nextShot;
    static float _pumpAt = -1f;
    static float _loadAt = -1f;
    static float _reloadUntil = -1f;
    static readonly Random Scatter = new();

    // Set when a shot puts someone down, which he laughs at.
    static bool _dropped;
    static readonly HashSet<nint> Struck = new();
    static readonly List<(float Ahead, RPawnVillain Thug, Vector3 At)> InLine = new();

    // For the HUD: when he last fired, whether he is pumping, and how far a reload has got.
    static float _firedAt = -100f;

    static bool Pumping => _pumpAt >= 0f && _reloadUntil < 0f;

    static bool Reloading => _reloadUntil >= 0f;

    static float ReloadDone => Reloading ? Math.Clamp(1f - (_reloadUntil - World.Now) / (ShotEvery + ReloadSeconds), 0f, 1f) : 1f;

    static void LoadGun()
    {
        _gunDefaults = Kept(Game.FindObject<RJokerGunGadget>(GunDefaultsPath));
        _pumpSound = Kept(Game.FindObject<AkEvent>(PumpSoundPath));
        _loadSound = Kept(Game.FindObject<AkEvent>(LoadSoundPath));
        if (_gunDefaults is null)
        {
            Log.Warn("joker.gun_defaults", "The Joker's gun settings weren't found, so his shotgun can't fire.");
        }
    }

    static bool GunLoaded() => Game.FindObject<RJokerGunGadget>(GunDefaultsPath) is { IsValid: true };

    static void Shoot(RPawnPlayer player, RPlayerController controller)
    {
        var now = World.Now;
        if (_pumpAt >= 0f && now >= _pumpAt)
        {
            _pumpAt = -1f;
            Play(player, _pumpSound);
        }

        if (_loadAt >= 0f && now >= _loadAt)
        {
            _loadAt = -1f;
            Play(player, _loadSound);
        }

        if (_reloadUntil >= 0f && now >= _reloadUntil)
        {
            _reloadUntil = -1f;
            _shells = Shells;
        }

        if (!Aim.Fire || _reloadUntil >= 0f || now < _nextShot || Arms.Raised < 0.9f || Barrel() is not { } barrel
            || _gunDefaults is not { IsValid: true } gun)
        {
            return;
        }

        _shells--;
        _nextShot = now + ShotEvery;
        if (_shells > 0)
        {
            _pumpAt = now + PumpAfter;
        }
        else
        {
            _reloadUntil = now + ShotEvery + ReloadSeconds;
            _pumpAt = now + ShotEvery;
            _loadAt = _reloadUntil - PumpAfter;
        }

        Arms.Kick(KickDegrees);
        KickCrosshair(controller);

        // Pellets go out from the muzzle to the crosshair, not the way the barrel happens to point.
        var from = barrel.From;
        var aim = Aim.PointFor(from) - from;
        aim = aim.LengthSquared() > 1f ? Vector3.Normalize(aim) : barrel.Way;
        var range = gun.FireRange > 0f ? gun.FireRange : FallbackRange;
        var pellets = Math.Max(1, gun.BlunderbusShots);
        var spread = gun.BlunderbusRandom * 360f / 65536f;
        Struck.Clear();
        _dropped = false;
        for (var i = 0; i < pellets; i++)
        {
            Pellet(player, gun, from, i == 0 ? aim : Spread(aim, spread), range);
        }

        var pool = Game.TryGetWorldInfo(out var world) ? world.GRI?.ObjectPool : null;
        if (pool is { IsValid: true } && gun.MuzzleFlash is { IsValid: true, Template: { IsValid: true } flash })
        {
            pool.SpawnEmitter(flash, from, Rotator.FromDirection(barrel.Way), 0, 1f);
        }

        Play(player, gun.LoadedGunFireSound);
        if (gun.GunFireRumble is { IsValid: true } rumble)
        {
            controller.ClientPlayForceFeedbackWaveform(rumble, player);
        }

        Alert(player, from);
        _firedAt = now;
        if (_dropped)
        {
            Say("laugh");
        }
    }

    // Each pellet stops at the first wall. Thugs along it before then are hit, the nearest first,
    // as many as one pellet of the game's gun goes through, each pushed a little less.
    static void Pellet(RPawnPlayer player, RJokerGunGadget gun, Vector3 from, Vector3 way, float range)
    {
        var end = from + way * range;
        var walled = Crosshair.Line(player, from, end, Crosshair.Meets.Level, out var wall);
        var reach = walled ? wall.Distance : range;

        InLine.Clear();
        foreach (var thug in Crowd.StandingEnemies())
        {
            if (thug.IsIncapacitated())
            {
                continue;
            }

            if (Villains.Crosses(thug, from, way, 0f, out var enter, out var leave) && enter <= reach)
            {
                var ahead = MathF.Min((enter + leave) * 0.5f, reach);
                InLine.Add((ahead, thug, from + way * ahead));
            }
        }

        InLine.Sort((a, b) => a.Ahead.CompareTo(b.Ahead));
        var through = Math.Max(1, gun.MaxNumVillainsWhichCanBeShotWithSingleBullet);
        var hit = Math.Min(through, InLine.Count);
        for (var i = 0; i < hit; i++)
        {
            var (_, thug, at) = InLine[i];
            Strike(player, gun, thug, at, way, i);
        }

        var stop = hit > 0 ? InLine[hit - 1].At : walled ? wall.Spot : end;
        Tracer(gun, from, stop);
        if (hit == 0 && walled)
        {
            Impact(gun, wall.Spot, wall.Normal);
            Villains.Break(player, wall.Spot, Splash, Struck);
            Villains.Wreck(player, wall.Spot, Splash, CarShare, 0, Struck);
        }
    }

    // The game's own shotgun hit, with the push it works out for the gun: straight along the shot,
    // lifted, and less for each thug the pellet has already gone through.
    static void Strike(RPawnPlayer player, RJokerGunGadget gun, RPawnVillain thug, Vector3 at, Vector3 way, int behind)
    {
        var mesh = thug.Mesh is { IsValid: true } skin ? skin : null;
        var bone = mesh is not null ? mesh.FindClosestBone(at, out _, 0f) : FName.None;
        var push = way * gun.DefaultHitImpulse;
        push.Z += gun.DefaultHitLift;
        push *= MathF.Pow(gun.BulletImpulseReductionProportionPerVillain, behind);

        thug.DamagedBy(new RPawnCombat.FDamageInfo
        {
            Attacker = player,
            BoneName = bone,
            Impulse = push,
            DamageAmount = gun.DefaultHitDamage,
            DamageType = RDmgType_JokerShotgun.StaticClass(),
        });

        if (thug.IsValid && (!Crowd.Standing(thug) || thug.IsIncapacitated()))
        {
            _dropped = true;
            Downed.Add(thug.Ptr);
        }

        if (Game.TryGetWorldInfo(out var world) && world.GRI?.ObjectPool is { IsValid: true } pool && gun.HitEffectParticleSystem is { IsValid: true } look)
        {
            pool.SpawnEmitter(look, at, Rotator.FromDirection(-way), 0, 1f);
        }
    }

    static void Impact(RJokerGunGadget gun, Vector3 at, Vector3 normal)
    {
        if (Game.TryGetWorldInfo(out var world) && world.GRI?.ObjectPool is { IsValid: true } pool && gun.HitEffectParticleSystem is { IsValid: true } look)
        {
            pool.SpawnEmitter(look, at, Rotator.FromDirection(normal.LengthSquared() > 0f ? normal : Vector3.UnitZ), 0, 1f);
        }
    }

    // The gun's tracer is a beam told how long to be, as the gun itself tells it.
    static void Tracer(RJokerGunGadget gun, Vector3 from, Vector3 to)
    {
        var way = to - from;
        if (way.LengthSquared() < 1f || gun.GunTracerParticleSystem is not { IsValid: true } look
            || !Game.TryGetWorldInfo(out var world) || world.GRI?.ObjectPool is not { IsValid: true } pool)
        {
            return;
        }

        if (pool.SpawnEmitter(look, from, Rotator.FromDirection(way), 0, 1f) is { IsValid: true, ParticleSystemComponent: { IsValid: true } beam })
        {
            beam.SetFloatParameter(new FName("BeamLength"), way.Length());
        }
    }

    // A random turn within the gun's spread, up to that many degrees each way.
    static Vector3 Spread(Vector3 aim, float degrees)
    {
        var turn = Rotator.FromDirection(aim);
        var yaw = turn.Yaw + ((float)Scatter.NextDouble() * 2f - 1f) * degrees;
        var pitch = turn.Pitch + ((float)Scatter.NextDouble() * 2f - 1f) * degrees;
        return new Rotator(pitch, yaw, 0f).ToDirection();
    }

    static void Alert(RPawnPlayer player, Vector3 from) => Villains.Alert(player, from, Hearing);

    static void Play(RPawnPlayer player, AkEvent? sound)
    {
        if (sound is { IsValid: true })
        {
            player.StartAudioEvent(sound);
        }
    }

    static void ResetGun()
    {
        _shells = Shells;
        _nextShot = 0f;
        _pumpAt = -1f;
        _loadAt = -1f;
        _reloadUntil = -1f;
    }

    static void UnloadGun()
    {
        _gunDefaults = null;
        _pumpSound = null;
        _loadSound = null;
        Struck.Clear();
        InLine.Clear();
    }
}
