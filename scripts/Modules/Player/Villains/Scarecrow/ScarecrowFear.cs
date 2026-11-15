#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// What his toxin does to a thug. An unarmed one gets the game's fear gas panic, the behaviour
// made for its cut fear dart: he cowers from what he sees for half a minute or so. Its panic for
// a gunman was never animated, so a gunman cowers the way he does from close gunfire. A thug
// still in his fear when he is dosed again collapses, the way the fear dart drops a thug it
// catches in the head.
static partial class Scarecrow
{
    static readonly FName HeadshotFront = new("Wrist_Dart_Headshot_Front");
    static readonly FName HeadshotBack = new("Wrist_Dart_Headshot_Back");

    // A cowering gunman stays afraid this long. The panic lasts as long as the game keeps it.
    const float CowerSeconds = 6f;

    // Thugs dosed this long ago are forgotten once they are no longer afraid.
    const float ForgetAfter = 45f;

    // How often the frightened are counted, for his HUD.
    const float CountEvery = 0.25f;

    sealed record Dose(RPawnVillain Thug, float At, bool Cowering);

    static readonly Dictionary<nint, Dose> Dosed = new();
    static readonly List<nint> Gone = new();
    static float _nextCount;
    static int _afraid;
    static int _collapsed;
    static float _collapsedAt = -10f;

    // Thugs dosed since he came on, and enemies standing this close now, for his HUD.
    static int _doses;
    static int _near;
    const float NearReach = 1500f;

    // His last few effects, put out once they have shown.
    static readonly List<(Emitter Effect, ParticleSystem Look, float Until)> Bursts = new();
    const float BurstSeconds = 1.2f;
    const float NeedleSplash = 0.35f;

    static RBMBehaviour_FearGasPanic? PanicOf(RPawnVillain thug) =>
        thug.AIController is { IsValid: true } brain && brain.GetBehaviourOfClass(RBMBehaviour_FearGasPanic.StaticClass(), false) is RBMBehaviour_FearGasPanic { IsValid: true } panic
            ? panic
            : null;

    // Whether a thug is still in the fear he was dosed with.
    static bool Panicked(RPawnVillain thug)
    {
        if (!Dosed.TryGetValue(thug.Ptr, out var dose))
        {
            return false;
        }

        return dose.Cowering ? World.Now - dose.At < CowerSeconds : PanicOf(thug) is not null;
    }

    static void Panic(RPawnPlayer player, RPawnVillain thug)
    {
        if (!Crowd.Standing(thug) || thug.DrivenVehicle is { IsValid: true } || thug.Base is Vehicle)
        {
            return;
        }

        var cowering = true;
        if (thug.CurrentGun is not { IsValid: true } && thug.AIController is { IsValid: true } brain)
        {
            if (PanicOf(thug) is { } going)
            {
                going.HitAgain();
                cowering = false;
            }
            else if (brain.SpawnBehaviour(RBMBehaviour_FearGasPanic.StaticClass(), null!) is RBMBehaviour_FearGasPanic { IsValid: true } panic)
            {
                panic.Initialise(player, thug.GetHitReactionAnimset());
                cowering = false;
            }
        }

        if (cowering)
        {
            thug.TryCower(new RPawnCombat.FDamageInfo
            {
                Attacker = player,
                DamageType = RDmgType_Cower.StaticClass(),
                DamageAmount = 0,
                bDontAlignHitReactionAnim = true,
            }, player, player.Location, false);
        }

        Dosed[thug.Ptr] = new Dose(thug, World.Now, cowering);
        _doses++;
    }

    // Too much toxin. He takes the fear dart's head hit, drops and stays down.
    static void Overdose(RPawnPlayer player, RPawnVillain thug)
    {
        var to = Level(player.Location - thug.Location);
        var ahead = Level(thug.Rotation.ToDirection());
        var front = Vector3.Dot(to, ahead) >= 0f;
        var down = false;
        if (_dartHits is { IsValid: true } hits)
        {
            var result = thug.DamagedBy(new RPawnCombat.FDamageInfo
            {
                Attacker = player,
                BoneName = Head,
                Impulse = to.LengthSquared() > 1f ? -Vector3.Normalize(to) * 100f : Vector3.Zero,
                DamageAmount = 0,
                StunTime = 8f,
                DamageType = RDmgType_WristDartHeadShot.StaticClass(),
                HitReactionAnimSet = hits,
                HitReactionAnimName = front ? HeadshotFront : HeadshotBack,
                bDontAlignHitReactionAnim = true,
            });

            if (result is RPawnCombat.DamageResult.DMG_Hit or RPawnCombat.DamageResult.DMG_KO)
            {
                thug.SetGoodAsDead();
                down = true;
            }
        }

        // Without the dart's hit, the game's own takedown blow puts him down.
        if (!down && thug.IsAlive && !thug.bGoodAsDead)
        {
            thug.TakeDamage(10000, player.Controller, thug.Location, Vector3.UnitZ, RDmgType_CombatTakedown.StaticClass(), default, player);
        }

        Dosed.Remove(thug.Ptr);
        _collapsed++;
        _collapsedAt = World.Now;
    }

    // Counts the frightened now and then, and forgets the ones who are down or long over it.
    static void Watch(RPawnPlayer player)
    {
        PutOutBursts();
        if (World.Now < _nextCount)
        {
            return;
        }

        _nextCount = World.Now + CountEvery;
        Gone.Clear();
        var afraid = 0;
        foreach (var (key, dose) in Dosed)
        {
            if (!dose.Thug.IsAlive || !Crowd.Standing(dose.Thug))
            {
                Gone.Add(key);
                continue;
            }

            if (Panicked(dose.Thug))
            {
                afraid++;
            }
            else if (World.Now - dose.At > ForgetAfter)
            {
                Gone.Add(key);
            }
        }

        foreach (var key in Gone)
        {
            Dosed.Remove(key);
        }

        _afraid = afraid;
        _near = 0;
        foreach (var thug in Crowd.StandingEnemies())
        {
            if (Vector3.DistanceSquared(thug.Location, player.Location) <= NearReach * NearReach)
            {
                _near++;
            }
        }
    }

    // One of the game's pooled effects, shown once and put out after a while.
    static void Burst(ParticleSystem? look, Vector3 at, float scale, float seconds)
    {
        if (look is not { IsValid: true } || !Game.TryGetWorldInfo(out var world) || world.GRI?.ObjectPool is not { IsValid: true } pool
            || pool.SpawnEmitter(look, at, default, 0, scale) is not { IsValid: true } effect)
        {
            return;
        }

        Bursts.Add((effect, look, World.Now + seconds));
    }

    static void PutOutBursts()
    {
        for (var i = Bursts.Count - 1; i >= 0; i--)
        {
            var (effect, look, until) = Bursts[i];
            var ours = effect.IsValid && effect.ParticleSystemComponent is { IsValid: true, Template: { } shows } && shows.Ptr == look.Ptr;
            if (World.Now < until && ours)
            {
                continue;
            }

            // The pool hands a finished effect to someone else, and that one is left alone.
            if (ours)
            {
                Emitter? shown = effect;
                Villains.PutOut(ref shown);
            }

            Bursts.RemoveAt(i);
        }
    }
}
