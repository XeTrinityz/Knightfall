#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// His fear toxin. He rears up with his injector raised and thrusts it out, the lunge he makes at
// Batman on Stagg's airship, and as it lands his tanks vent a cloud of the toxin the city was
// gassed with. Everyone in it panics, and it takes anyone who walks in while it hangs there.
//
// The cloud is the toxin rolling out from the Cloudburst in the scene where it first goes off,
// made small. That scene is loaded as a package the first time he vents. Nothing in it runs.
// Only its dark gas and its shockwaves are let show. Its fireball, sparks, light and its
// lighter gas glow white up close.
static partial class Scarecrow
{
    static readonly FName VentMove = new("Scarecrow_Attack_BM");
    const string VentMark = "AttackHit";
    const float VentLands = 0.97f;
    const float VentUntil = 1.6f;

    // The gas rolls out from him this fast, as far as this and this far up or down, and hangs
    // this long. It takes this long to build up again once he has vented.
    const float CloudSpread = 600f;
    const float CloudReach = 1500f;
    const float CloudHeight = 300f;
    const float CloudSeconds = 8f;
    const float ToxinWait = 14f;

    const string CloudPackage = "CityX_09_Cine_Ch7b_Cloudburst_Activated";
    const string BurstPath = "VFX_Cinematics.Particles.CH07.FearGasExplosion_CINE";
    const float BurstScale = 0.25f;
    static readonly string[] Unwanted = { "Light", "Main", "trailingSparks", "Trail2", "Embers_2", "RollingSmoke_Delayed" };

    // The gas is thickest where it starts, and started on him it fills the camera behind him. So
    // it starts this far out ahead of him, where he thrusts his injector.
    const float CloudAhead = 350f;
    const float WallGap = 60f;
    static ParticleSystem? _burst;
    static bool _cloudTried;

    // Without that scene, the gas the Batmobile kicks up driving through the toxin is used. It
    // only comes off something moving, so each puff is swirled round its spot until the cloud
    // starts to clear.
    const int CloudPuffs = 6;
    const float SwirlRadius = 70f;
    const float SwirlSpeed = 5f;
    const float ClearSeconds = 2f;
    const float PuffRing = 260f;
    const float MiddleScale = 2.2f;
    const float RingScale = 1.5f;

    static float _nextToxin;
    static float _ventedAt = -20f;
    static Vector3 _cloud;
    static float _cloudUntil;

    sealed record Puffing(Emitter Effect, ParticleSystem Look, Vector3 Spot, float Phase);

    static readonly List<Puffing> Cloud = new();
    static readonly HashSet<nint> Caught = new();

    static Outcome Toxin()
    {
        if (!Ready(out var player, out var controller))
        {
            return Outcome.Unchanged("SCARECROW IS OFF");
        }

        if (!Free(player, controller))
        {
            return Outcome.Unchanged("NOT NOW");
        }

        if (World.Now < _nextToxin)
        {
            return Outcome.Unchanged("RECOVERING");
        }

        if (Play(VentMove, 0f, VentUntil, player.Rotation.Yaw) is null)
        {
            return Outcome.Unchanged("NOT READY");
        }

        LoadCloud();
        _aim = controller.Rotation.Yaw;
        _move = Move.Toxin;
        _hitAt = Marked(VentMove, VentMark, VentLands);
        Hold(controller);
        Speech.Speak("toxin", always: true);
        return Outcome.Done("FEAR TOXIN");
    }

    static void Vent(RPawnPlayer player)
    {
        PutOutCloud();
        _ventedAt = World.Now;
        var ahead = Vector3.Normalize(new Rotator(0f, _base, 0f).ToDirection() with { Z = 0f });
        var reach = CloudAhead;
        if (Crosshair.Line(player, player.Location, player.Location + ahead * CloudAhead, Crosshair.Meets.Level, out var wall))
        {
            reach = MathF.Max(0f, wall.Distance - WallGap);
        }

        _cloud = player.Location + ahead * reach;
        _cloudUntil = World.Now + CloudSeconds;
        Caught.Clear();

        var floor = _cloud - Vector3.UnitZ * (player.CylinderComponent is { IsValid: true } body ? body.CollisionHeight : 90f);
        if (_burst is { IsValid: true } burst)
        {
            Puff(burst, floor, BurstScale);
        }
        else
        {
            Puff(_gas, floor, MiddleScale);
            for (var i = 0; i < CloudPuffs; i++)
            {
                var (sin, cos) = MathF.SinCos(i * MathF.PI * 2f / CloudPuffs);
                Puff(_gas, floor + new Vector3(cos, sin, 0f) * PuffRing, RingScale);
            }
        }

        KeepCloud(player);
    }

    static void LoadCloud()
    {
        if (_cloudTried)
        {
            return;
        }

        _cloudTried = true;
        if (Game.FindObject<ParticleSystem>(BurstPath) is not { IsValid: true })
        {
            LoadPackage(CloudPackage);
        }

        _burst = Kept(Game.FindObject<ParticleSystem>(BurstPath));
    }

    static void Puff(ParticleSystem? look, Vector3 at, float scale)
    {
        if (look is not { IsValid: true } gas || !Game.TryGetWorldInfo(out var world) || world.GRI?.ObjectPool is not { IsValid: true } pool
            || pool.SpawnEmitter(gas, at, default, 0, scale) is not { IsValid: true } effect)
        {
            return;
        }

        if (effect.ParticleSystemComponent is { IsValid: true } shown)
        {
            var emitters = gas.Emitters;
            for (var i = 0; i < emitters.Count; i++)
            {
                if (emitters[i] is { IsValid: true } emitter && Array.IndexOf(Unwanted, emitter.EmitterName.ToString()) >= 0)
                {
                    shown.SetStopSpawning(i, true);
                    shown.KillParticlesInEmitter(emitter.EmitterName);
                }
            }
        }

        Cloud.Add(new Puffing(effect, gas, at, Cloud.Count * 1.7f));
    }

    // Each tick while the cloud hangs, anyone it has rolled over who hasn't breathed it yet
    // panics.
    static void KeepCloud(RPawnPlayer player)
    {
        if (Cloud.Count == 0 && World.Now >= _cloudUntil)
        {
            return;
        }

        if (World.Now >= _cloudUntil)
        {
            PutOutCloud();
            return;
        }

        if (World.Now < _cloudUntil - ClearSeconds)
        {
            for (var i = Cloud.Count - 1; i >= 0; i--)
            {
                var puff = Cloud[i];

                // The pool hands a finished effect to someone else, and then it isn't ours.
                if (!Ours(puff))
                {
                    Cloud.RemoveAt(i);
                    continue;
                }

                // Only the kicked up gas needs moving.
                if (_gas is null || puff.Look.Ptr != _gas.Ptr)
                {
                    continue;
                }

                var (sin, cos) = MathF.SinCos(puff.Phase + (World.Now - _ventedAt) * SwirlSpeed);
                puff.Effect.SetLocation(puff.Spot + new Vector3(cos, sin, 0f) * SwirlRadius);
            }
        }

        var reach = MathF.Min(CloudReach, CloudSpread * (World.Now - _ventedAt));
        foreach (var thug in Crowd.StandingEnemies())
        {
            if (Level(thug.Location - _cloud).Length() > reach || MathF.Abs(thug.Location.Z - _cloud.Z) > CloudHeight || !Caught.Add(thug.Ptr))
            {
                continue;
            }

            Panic(player, thug);
        }
    }

    static bool Ours(Puffing puff) =>
        puff.Effect.IsValid && puff.Effect.ParticleSystemComponent is { IsValid: true, Template: { } shows } && shows.Ptr == puff.Look.Ptr;

    static void PutOutCloud()
    {
        for (var i = 0; i < Cloud.Count; i++)
        {
            if (Ours(Cloud[i]))
            {
                Emitter? effect = Cloud[i].Effect;
                Villains.PutOut(ref effect);
            }
        }

        Cloud.Clear();
        Caught.Clear();
        _cloudUntil = 0f;
    }
}
