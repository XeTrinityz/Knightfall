#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace Knightfall;

// PLAYER > COMBAT, SUPER PUNCH. Your hit becomes a heavy bat hit carrying the game's own ragdoll
// push, the thug is made to go limp for it, and once the body is loose it is thrown.
static class SuperPunch
{
    const int EveryHit = 1;
    const int Knockouts = 2;

    // The push the game gets with the hit, and the speed the loose body is thrown at.
    const float Push = 3000f;
    const float Speed = 1400f;
    const float Lift = 0.35f;
    const float PushSeconds = 0.25f;

    // The throw is held for a few ticks, or the ragdoll settles back and eats it.
    const int ThrowTicks = 3;

    // A body that won't go limp is tried again this often, and given up on after the other.
    const float RetryEvery = 0.2f;
    const float GiveUpAfter = 1.5f;

    sealed class Throw
    {
        public required RPawnVillain Thug;
        public Vector3 Velocity;
        public int Ticks;
        public float Until;
        public float RetryAt;
        public int Tries;
    }

    static readonly List<Throw> Throws = new();

    // The thug being hit right now, whose ragdoll checks say yes.
    static RPawnVillain? _forcing;

    public static readonly Choice Mode = new(
        "player.combat.super_punch", "SUPER PUNCH",
        "Your hits on foot send thugs flying. EVERY HIT launches on every strike and counter. KNOCKOUTS only launches with the hit that puts one down.",
        new[] { ("off", "OFF"), ("every", "EVERY HIT"), ("knockouts", "KNOCKOUTS") })
    {
        Tick = Step,
        Restore = Throws.Clear,
    };

    public static readonly Slider Force = new(
        "player.combat.punch_force", "PUNCH FORCE",
        "How far SUPER PUNCH sends them.",
        min: 0.25, max: 3, step: 0.25, value: 1, unit: "X")
    {
        Preference = true,
        Block = static () => Mode.Index == 0 ? "SUPER PUNCH IS OFF" : null,
    };

    // Makes the hit a launch when it should be one. The way to throw them is handed back, or
    // zero when this hit is left as it was.
    public static Vector3 Shape(RPawnVillain thug, ref RPawnCombat.FDamageInfo info)
    {
        // A hit with no damage isn't a punch. Gas, smoke and stuns reach thugs this way, and
        // launching them would stop their own reaction.
        var mode = Mode.Index;
        if (mode == 0 || info.DamageAmount <= 0f || World.Player is not { } you || !ReferenceEquals(info.Attacker, you) || you.Base is Vehicle
            || !Crowd.Standing(thug) || thug.IsRagdoll() || thug.Base is Vehicle || Bodyguards.Is(thug))
        {
            return Vector3.Zero;
        }

        if (mode == Knockouts && info.DamageAmount < thug.Health)
        {
            return Vector3.Zero;
        }

        var away = Teleport.Flat(thug.Location - you.Location);
        var up = away + Vector3.UnitZ * Lift;
        var force = (float)Force.Value;
        var push = up * Push * force;

        // A harder push the hit already had is kept.
        if (info.Impulse.LengthSquared() > push.LengthSquared())
        {
            push = info.Impulse;
        }

        // The game's own launch from its big hits, put on the body once it is loose.
        info.RagdollForce = push;
        info.RagdollForceDuration = PushSeconds;
        info.ARF = RPawnCombat.AdditionalRagdollForce.ARF_HeadBackFeetUp;
        info.Impulse = push;
        info.ForceHitReactionHeading = push;
        info.DamageType = RDmgType_BaseballBat.StaticClass();
        info.bHeavyHit = true;
        info.bWillKnockOverOtherPawns = true;
        return up * Speed * force;
    }

    // Runs the game's hit with the launch in it. The thug's own ragdoll switches are set for the
    // hit only and put back after.
    public static RPawnCombat.DamageResult Hit(RPawnVillain thug, RPawnCombat.FDamageInfo info, Vector3 launch)
    {
        var never = thug.bNeverRagdollWhenHit;
        var always = thug.bAlwaysRagdollWhenHit;
        thug.bNeverRagdollWhenHit = false;
        thug.bAlwaysRagdollWhenHit = true;
        _forcing = thug;

        RPawnCombat.DamageResult result;
        try
        {
            result = thug.DamagedBy(info);
        }
        finally
        {
            _forcing = null;
            if (thug.IsValid)
            {
                thug.bNeverRagdollWhenHit = never;
                thug.bAlwaysRagdollWhenHit = always;
            }
        }

        try
        {
            Launch(thug, info, launch);
        }
        catch (Exception error)
        {
            Log.Error("combat.super_punch", "SUPER PUNCH could not launch the thug", error);
        }

        return result;
    }

    static void Launch(RPawnVillain thug, RPawnCombat.FDamageInfo info, Vector3 launch)
    {
        if (!thug.IsValid || thug.bDeleteMe)
        {
            return;
        }

        // The plain ragdoll first, then the one a glide tackle uses.
        if (!thug.IsRagdoll())
        {
            thug.GenericGoRagdoll();
        }

        if (!thug.IsRagdoll())
        {
            thug.PlayRagdoll(info, RGameInfo.RagdollStunType.RAGSTUN_GlideBoostTackle, false);
        }

        Throws.RemoveAll(waiting => ReferenceEquals(waiting.Thug, thug));
        Throws.Add(new Throw { Thug = thug, Velocity = launch, Until = World.Now + GiveUpAfter, RetryAt = World.Now + RetryEvery });
    }

    // Every tick while on. Throws each waiting body once its ragdoll is running.
    static void Step()
    {
        var now = World.Now;
        for (var i = Throws.Count - 1; i >= 0; i--)
        {
            var waiting = Throws[i];
            var thug = waiting.Thug;

            // Game time starts over with a new world.
            if (!thug.IsValid || thug.bDeleteMe || thug.Mesh is not { IsValid: true } mesh || now > waiting.Until || now < waiting.Until - GiveUpAfter - 1f)
            {
                Throws.RemoveAt(i);
                continue;
            }

            var loose = thug.IsRagdoll() || thug.Physics == Actor.EPhysics.PHYS_RigidBody
                || (thug.RagdollProcessor is { IsValid: true } ragdoll && ragdoll.IsRagdollOn());
            if (!loose)
            {
                if (now >= waiting.RetryAt && !thug.bHidden)
                {
                    waiting.RetryAt = now + RetryEvery;
                    Drop(thug, waiting.Tries++);
                }

                continue;
            }

            mesh.WakeRigidBody(FName.None);
            mesh.SetRBLinearVelocity(waiting.Velocity, false);
            if (++waiting.Ticks >= ThrowTicks)
            {
                Throws.RemoveAt(i);
            }
        }
    }

    // The game's other ways of putting a thug down, taken in turn. Any sooner than the retry
    // time and they don't take.
    static void Drop(RPawnVillain thug, int attempt)
    {
        switch (attempt % 3)
        {
            case 0 when thug.Health <= 0:
                thug.RagdollKill();
                break;
            case 0:
                thug.GenericGoRagdoll();
                break;
            case 1:
                var info = new RPawnCombat.FDamageInfo
                {
                    Attacker = World.Player!,
                    DamageAmount = 1f,
                    DamageType = RDmgType_GlideKick.StaticClass(),
                    bHeavyHit = true,
                };
                thug.PlayRagdoll(info, RGameInfo.RagdollStunType.RAGSTUN_GlideBoostTackle, false);
                break;
            default:
                thug.TargetGoRagdoll();
                break;
        }
    }

    // During the hit the game's ragdoll checks say yes, the way they do for a bat. Every other
    // check gets the game's own answer.
    [Redirect(typeof(RPawnVillain), nameof(RPawnVillain.ShouldGoRagdoll))]
    static bool ShouldGoRagdoll(RPawnVillain self, Class dmgType, float damageAmount) =>
        ReferenceEquals(_forcing, self) || Redirects.RunOriginal<bool>();

    [Redirect(typeof(RPawnVillain), nameof(RPawnVillain.ShouldGoRagdollWhenHitByBaseballBat))]
    static bool ShouldGoRagdollFromBat(RPawnVillain self) =>
        ReferenceEquals(_forcing, self) || Redirects.RunOriginal<bool>();
}
