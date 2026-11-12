#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// Lands the hits of bodyguards and of the enemies fighting them. A thug's strike only hurts
// through a path built for hitting Batman, so against anyone else it passes straight through.
// Each attack's animation opens its hit window with a call, and that is where the hit is landed.
// Every other attack in the game runs as it always did.
static class Strikes
{
    // A strike only lands on someone this close.
    const float Reach = 320f;

    // The same swing can open its window more than once.
    const float OncePer = 0.25f;

    // An enemy's hit takes this share of a bodyguard's normal health when the game's own does nothing.
    const float EnemyHits = 4f;

    const string SparkName = "fx_FaceHit5_p";
    const float SparkLookEvery = 120f;

    static ParticleSystem? _spark;
    static string _sparkPath = "";
    static float _sparkLookedAt = -1000f;

    [Redirect(typeof(RCombatMove_VillainCloseAttack), nameof(RCombatMove_VillainCloseAttack.CombatAnimHit))]
    static void PunchHit(RCombatMove_VillainCloseAttack self)
    {
        Redirects.RunOriginal();
        Landed(self, false);
    }

    // A punch can also land a frame or two late through this call. The game's damage may land
    // there by itself, and then only the sound and spark are missing.
    [Redirect(typeof(RCombatMove_VillainCloseAttack), nameof(RCombatMove_VillainCloseAttack.ForceHitTarget))]
    static void PunchForced(RCombatMove_VillainCloseAttack self)
    {
        var before = -1;
        try
        {
            // It waits until the fist's damage proxy has a position, and only strikes then.
            if (Bodyguards.Busy && self.bForceHitTarget && self.Target is { IsValid: true } target
                && self.Villain?.DamageProxy is { IsValid: true } proxy && proxy.LocationHistory.Count >= 1)
            {
                before = target.Health;
            }
        }
        catch (Exception error)
        {
            Log.Error("bodyguards.strike", "A bodyguard strike could not be checked", error);
        }

        Redirects.RunOriginal();
        if (before >= 0)
        {
            Guarded(self, false, before);
        }
    }

    // Knife slashes and shield bashes are called left and right by the animation and never reach
    // the plain call.
    [Redirect(typeof(RCombatMove_VillainKnifeAttack), nameof(RCombatMove_VillainKnifeAttack.CombatAnimHit_Left))]
    static void KnifeLeft(RCombatMove_VillainKnifeAttack self)
    {
        Redirects.RunOriginal();
        Landed(self, true);
    }

    [Redirect(typeof(RCombatMove_VillainKnifeAttack), nameof(RCombatMove_VillainKnifeAttack.CombatAnimHit_Right))]
    static void KnifeRight(RCombatMove_VillainKnifeAttack self)
    {
        Redirects.RunOriginal();
        Landed(self, true);
    }

    [Redirect(typeof(RCombatMove_VillainKnifeAttack), nameof(RCombatMove_VillainKnifeAttack.CombatAnimHit))]
    static void KnifeHit(RCombatMove_VillainKnifeAttack self)
    {
        Redirects.RunOriginal();
        Landed(self, false);
    }

    [Redirect(typeof(RCombatMove_VillainShieldAttack), nameof(RCombatMove_VillainShieldAttack.CombatAnimHit_Left))]
    static void ShieldLeft(RCombatMove_VillainShieldAttack self)
    {
        Redirects.RunOriginal();
        Landed(self, true);
    }

    [Redirect(typeof(RCombatMove_VillainShieldAttack), nameof(RCombatMove_VillainShieldAttack.CombatAnimHit_Right))]
    static void ShieldRight(RCombatMove_VillainShieldAttack self)
    {
        Redirects.RunOriginal();
        Landed(self, true);
    }

    [Redirect(typeof(RCombatMove_VillainShieldAttack), nameof(RCombatMove_VillainShieldAttack.CombatAnimHit))]
    static void ShieldHit(RCombatMove_VillainShieldAttack self)
    {
        Redirects.RunOriginal();
        Landed(self, false);
    }

    [Redirect(typeof(RCombatMove_VillainPipeAttack), nameof(RCombatMove_VillainPipeAttack.CombatAnimHit))]
    static void PipeHit(RCombatMove_VillainPipeAttack self)
    {
        Redirects.RunOriginal();
        Landed(self, false);
    }

    [Redirect(typeof(RCombatMove_VillainHeavySmashAttack), nameof(RCombatMove_VillainHeavySmashAttack.CombatAnimHit))]
    static void SmashHit(RCombatMove_VillainHeavySmashAttack self)
    {
        Redirects.RunOriginal();
        Landed(self, false);
    }

    static void Landed(RCombatMove_VillainAttack move, bool slash)
    {
        if (Bodyguards.Busy)
        {
            Guarded(move, slash, -1);
        }
    }

    // Nothing may get out of a redirect into the game's own call.
    static void Guarded(RCombatMove_VillainAttack move, bool slash, int healthBefore)
    {
        try
        {
            // A slash that landed has its damage collision switched off, so the game's own can't
            // land it a second time.
            if (Land(move, healthBefore, false) && slash && move.Villain?.DamageProxy is { IsValid: true } proxy)
            {
                proxy.Deactivate();
            }
        }
        catch (Exception error)
        {
            Log.Error("bodyguards.strike", "A bodyguard strike could not be landed", error);
        }
    }

    static Class DamageOf(RCombatMove_VillainAttack move) => move switch
    {
        RCombatMove_VillainKnifeAttack or RCombatMove_VillainMultiAttackAndCounter => RDmgType_Knife.StaticClass(),
        RCombatMove_VillainShieldAttack => RDmgType_ShieldBash.StaticClass(),
        RCombatMove_VillainPipeAttack => RDmgType_Pipe.StaticClass(),
        RCombatMove_VillainHeavySmashAttack => RDmgType_SledgeHammer.StaticClass(),
        _ => RDmgType_Strike.StaticClass(),
    };

    // A punch goes through the game's own damage first, for the real reaction. A weapon, or a
    // punch the game's damage does nothing with, takes health off directly. Counted says the
    // caller already made sure this is a new hit.
    static bool Land(RCombatMove_VillainAttack move, int healthBefore, bool counted)
    {
        if (move.Villain is not { IsValid: true } attacker || Bodyguards.FighterOf(attacker) is not { Target: { } target } fighter)
        {
            return false;
        }

        if (!ReferenceEquals(attacker.OverrideTargetPawn, target) || !Fight.Standing(target)
            || Vector3.DistanceSquared(target.Location, attacker.Location) > Reach * Reach)
        {
            return false;
        }

        var now = World.Now;
        if (!counted && ReferenceEquals(fighter.LandedMove, move) && now >= fighter.LandedAt && now - fighter.LandedAt < OncePer)
        {
            return false;
        }

        fighter.LandedMove = move;
        fighter.LandedAt = now;

        // The game's hit already hurt them.
        if (healthBefore >= 0 && target.Health < healthBefore)
        {
            Effects(attacker, target, move);
            return true;
        }

        var before = target.Health;
        if (move is RCombatMove_VillainCloseAttack close)
        {
            close.DamagePawn(target);
        }

        if (target.Health >= before && Fight.Standing(target))
        {
            var push = Teleport.Flat(target.Location - attacker.Location) * 400f;
            target.TakeDamage(Amount(fighter, target, before), attacker.Controller, target.Location, push, DamageOf(move), default, attacker);
        }

        Effects(attacker, target, move);
        return true;
    }

    // A bodyguard's hit is sized by HITS TO KNOCK OUT. An enemy's hit on a bodyguard is sized by
    // the bodyguard's normal health, so BODYGUARD HEALTH makes them last longer.
    static int Amount(Fighter fighter, RPawnVillain target, int health)
    {
        if (fighter is Hunter && Bodyguards.GuardOf(target) is { } guard)
        {
            return Math.Max(1, (int)MathF.Ceiling(guard.BaseHealth / EnemyHits));
        }

        var full = Math.Max(Math.Max(target.HealthMax, health), 1);
        return Math.Max(1, (int)MathF.Ceiling(full / MathF.Max(1f, (float)Bodyguards.HitsToKnockOut.Value)));
    }

    // Runs every tick for someone with a target. Fists and blades can also land through the
    // game's own collision, which reaches none of the calls above. So the target's health is
    // watched while they swing, and a drop gets its sound and spark.
    public static void Watch(Fighter fighter)
    {
        var body = fighter.Body;
        if (fighter.Target is not { } target || body.CurrentGun is { IsValid: true } || !target.IsValid)
        {
            fighter.WatchedBody = null;
            return;
        }

        var health = target.Health;
        if (!ReferenceEquals(fighter.WatchedBody, target))
        {
            fighter.WatchedBody = target;
            fighter.WatchedHealth = health;
            return;
        }

        var before = fighter.WatchedHealth;
        fighter.WatchedHealth = health;
        if (health >= before || body.CombatMove is not RCombatMove_VillainAttack { IsValid: true } move
            || Vector3.DistanceSquared(target.Location, body.Location) > Reach * Reach)
        {
            return;
        }

        var now = World.Now;
        if (ReferenceEquals(fighter.LandedMove, move) && now >= fighter.LandedAt && now - fighter.LandedAt < 0.6f)
        {
            return;
        }

        fighter.LandedMove = move;
        fighter.LandedAt = now;
        Effects(body, target, move);
    }

    // Runs every tick. A knife thug's attack is the knife combo, which only ever hurts Batman and
    // whose hit is a state function no redirect can reach. It counts its slashes, so each new
    // count is landed here.
    public static void Combo(Fighter fighter)
    {
        if (fighter.Body.CombatMove is not RCombatMove_VillainMultiAttackAndCounter { IsValid: true } combo)
        {
            fighter.Combo = null;
            return;
        }

        var hits = combo.NumCombatAnimHits;
        if (!ReferenceEquals(fighter.Combo, combo))
        {
            fighter.Combo = combo;
            fighter.ComboHits = 0;
        }

        if (hits > fighter.ComboHits)
        {
            fighter.ComboHits = hits;
            Land(combo, -1, true);
        }
    }

    // A thug's swing is built to hit Batman, whose side plays the impact. Thug on thug it is
    // silent, so the one struck plays the impact and the game's thug-hit spark is put on them.
    static void Effects(RPawnVillain attacker, RPawnVillain target, RCombatMove_VillainAttack move)
    {
        try
        {
            var punch = move is RCombatMove_VillainCloseAttack && attacker.CurrWeapon is not { IsValid: true };
            var head = System.Random.Shared.Next(3) == 0;
            var finishing = !Fight.Standing(target);
            target.PlaySound_ImpactAI(target, true, finishing, head, punch, !punch, false, true, false, false, false);

            if (Spark() is not { } spark || !Game.TryGetWorldInfo(out var world) || world.GRI?.ObjectPool is not { IsValid: true } pool)
            {
                return;
            }

            var at = head ? Fight.Eye(target) : Fight.Chest(target);
            var way = at - Fight.Eye(attacker);
            var spot = at - (way.LengthSquared() > 1f ? Vector3.Normalize(way) : Vector3.Zero) * 20f;
            pool.SpawnEmitter(spark, spot, Rotator.FromDirection(-way), 0, 1f);
        }
        catch (Exception error)
        {
            Log.Error("bodyguards.strike_effects", "A bodyguard hit could not play its sound and spark", error);
        }
    }

    // Found by name once, then by its path, which is a single lookup.
    static ParticleSystem? Spark()
    {
        if (_spark is { IsValid: true })
        {
            return _spark;
        }

        if (_sparkPath.Length > 0)
        {
            _spark = Game.FindObject<ParticleSystem>(_sparkPath);
            return _spark is { IsValid: true } ? _spark : null;
        }

        var now = World.Now;
        if (now >= _sparkLookedAt && now - _sparkLookedAt < SparkLookEvery)
        {
            return null;
        }

        _sparkLookedAt = now;
        foreach (var system in Game.FindObjects<ParticleSystem>())
        {
            if (system.IsValid && system.Name.ToString() == SparkName)
            {
                _spark = system;
                _sparkPath = system.GetPathName(null);
                return system;
            }
        }

        Log.Info("Bodyguards: the hit spark isn't loaded here, so hits only play their sound.");
        return null;
    }
}
