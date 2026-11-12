#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// The mech's power surge, its move from the fight where it slams power into the floor to wake
// its robots. Here the slam sends a shockwave out that knocks down everyone close, hits the
// vehicles and breaks what is around.
//
// The move's own sparks and the robots' revival burst play but never show outside the fight, so
// its sparks are the beam's: the charge glows on its claw as it winds up, and the slam bursts
// a ring of the beam's impacts around it.
static partial class RiddlerMech
{
    static readonly FName SurgeMove = new("Resurrect_In");
    static readonly FName SurgeRecover = new("Resurrect_Out");

    const float SurgeReach = 800f;
    const float SurgeBreakReach = 600f;
    const float SurgeHeight = 300f;
    const int SurgeDamage = 30;
    const float SurgeForce = 1800f;
    const float SurgeWreck = 0.25f;
    const int SurgeParked = 40;
    const float SurgeWait = 12f;
    const float SurgeDust = 1.6f;

    // The ring of impacts, how far out it bursts, and how long each burst lasts.
    const int RingCount = 8;
    const float RingReach = 350f;
    const float RingSeconds = 0.7f;
    static readonly List<Emitter> Ring = new();
    static float _ringUntil;

    static float _nextSurge;
    static bool _surgeOut;

    static float SurgeReady => Math.Clamp(1f - (_nextSurge - World.Now) / SurgeWait, 0f, 1f);

    // The slam, then the move that rises out of it.
    static void Surging(RPawnPlayer player, AnimNodeSequence played)
    {
        if (!_surgeOut && !_hit)
        {
            Glow();
        }

        if (!_surgeOut && !_hit && played.CurrentTime >= _hitAt)
        {
            _hit = true;
            Villains.PutOut(ref _glow);
            Shockwave(player);
        }

        if (Ring.Count > 0 && World.Now >= _ringUntil)
        {
            PutOutRing();
        }

        if (played.bPlaying && played.CurrentTime < played.GetAnimPlaybackLength() - (_surgeOut ? EndEarly : 0.01f))
        {
            return;
        }

        if (_surgeOut)
        {
            Unact();
            return;
        }

        _surgeOut = true;
        if (Play(SurgeRecover, loop: false) is null)
        {
            Unact();
        }
    }

    static void Shockwave(RPawnPlayer player)
    {
        var feet = player.Location - Vector3.UnitZ * player.CylinderComponent.CollisionHeight;
        if (_dustLook is { IsValid: true } dust && Game.TryGetWorldInfo(out var world) && world.GRI?.ObjectPool is { IsValid: true } pool)
        {
            pool.SpawnEmitter(dust, feet, new Rotator(0f, player.Rotation.Yaw, 0f), 0, SurgeDust);
        }

        if (_thump is { IsValid: true } thump)
        {
            player.StartAudioEvent(thump);
        }

        Burst(feet);

        var flattened = 0;
        foreach (var thug in Crowd.StandingEnemies())
        {
            var off = thug.Location - feet;
            if (off.X * off.X + off.Y * off.Y > SurgeReach * SurgeReach || MathF.Abs(off.Z) > SurgeHeight)
            {
                continue;
            }

            var away = Teleport.Flat(off);
            var push = Vector3.Normalize((away.LengthSquared() > 1f ? Vector3.Normalize(away) : Vector3.UnitX) + Vector3.UnitZ * 0.7f);
            if (Villains.Knock(player, thug, SurgeDamage, push, SurgeForce, fall: true))
            {
                flattened++;
            }
        }

        flattened += Villains.Wreck(player, feet, SurgeBreakReach, SurgeWreck, SurgeParked);
        Villains.Break(player, feet, SurgeBreakReach);
        _solved += flattened;
        Speak(SurgeVoice, flattened > 0 ? SurgeLines : SurgeQuiet, always: true);
    }

    // The ring of impacts, each on the ground facing up.
    static void Burst(Vector3 feet)
    {
        PutOutRing();
        if (_impactLook is not { IsValid: true } look || !Game.TryGetWorldInfo(out var world) || world.GRI?.ObjectPool is not { IsValid: true } pool)
        {
            return;
        }

        var up = Rotator.FromDirection(Vector3.UnitZ);
        for (var i = 0; i < RingCount; i++)
        {
            var (sin, cos) = MathF.SinCos(i * MathF.Tau / RingCount);
            if (pool.SpawnEmitter(look, feet + new Vector3(cos, sin, 0f) * RingReach, up, 0, 1f) is { IsValid: true } spark)
            {
                Ring.Add(spark);
            }
        }

        _ringUntil = World.Now + RingSeconds;
    }

    static void PutOutRing()
    {
        foreach (var spark in Ring)
        {
            if (spark.IsValid && spark.ParticleSystemComponent is { IsValid: true } shown && shown.Template is { } showing && showing.Ptr == _impactLook?.Ptr)
            {
                shown.DeactivateSystem();
            }
        }

        Ring.Clear();
    }

    static void PutOutSurge()
    {
        PutOutRing();
        Villains.PutOut(ref _glow);
    }
}
