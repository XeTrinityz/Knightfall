#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using AimTurn = BmSDK.BmGame.RAnimUtil_PosePlayer.EAllowTurningToAim;
using Pace = BmSDK.BmGame.RBMBehaviour_Follow.MoveSpeed;

namespace Knightfall;

// Someone the bodyguard code steers. A bodyguard, or an enemy who has gone after one.
abstract class Fighter
{
    public required RPawnVillain Body;

    // Who they are fighting now.
    public RPawnVillain? Target;

    // What we changed on them, so it can be put back.
    public bool SelfManaged;
    public bool Benched;

    public float MoveAt;
    public float SwingAt;
    public float FollowMadeAt;

    // Gun handling.
    public bool Shooting;
    public bool Raised;
    public RPawnVillain? ShotAt;
    public float AimedSince;
    public float BurstAt;
    public float SightAt;
    public float SeenAt = -100f;
    public bool Sight;
    public float StanceAt;

    // The last strike we landed for them, and the health we watch while they swing.
    public RCombatMove? LandedMove;
    public float LandedAt = -100f;
    public RPawnVillain? WatchedBody;
    public int WatchedHealth;
    public RCombatMove? Combo;
    public int ComboHits;
}

sealed class Guard : Fighter
{
    public required string Label;

    // Someone the game placed, who goes back to how they were when dismissed.
    public bool Recruited;

    // Whether a recruit was against you when taken on.
    public bool WasHostile = true;

    // Their place in the team, which spreads them out behind you.
    public int Slot;

    // Health before BODYGUARD HEALTH scaled it, which sizes what an enemy's hit takes off.
    public int BaseHealth = 1;

    public bool Marked;
    public float WakeAt;
    public float Score;
    public float ThinkAt;
    public float CatchUpAt;
    public float HostileCheckAt;
    public RPawnVillain? Facing;
    public bool Fought;
    public float FoughtAt;
    public float DownSince = -1f;
    public float HiddenSince = -1f;

    // ATTACK WHO I AIM AT.
    public RPawnVillain? Ordered;
    public float OrderedUntil;

    // HOLD HERE. The spot they keep, and the hidden marker they walk back to.
    public bool Posted;
    public bool Returning;
    public Vector3 Post;
    public Actor? PostMark;

    // Where they last moved from and when, which tells when they can't get to you.
    public Vector3 MovedFrom;
    public float MovedAt;
    public float StuckCheckedAt = -100f;

    // BODYGUARDS RECOVER.
    public int SeenHealth;
    public float HurtAt;
    public float RecoverAt;
}

sealed class Hunter : Fighter
{
    public Guard? Prey;
}

// Moves bodyguards and their hunters share: following, aiming and shooting. The game's fights are
// built around Batman, so anyone fighting someone else is steered from here.
static class Fight
{
    // Our reason for standing someone down, which the game reads back.
    public static readonly FName Reason = new("KnightfallGuard");

    static readonly FName Aiming = new("Aiming");
    static readonly FName Relaxed = new("Relaxed");
    static readonly FName CombatStance = new("Combat");
    static readonly FName StandingStance = new("Standing");
    static readonly FName AimName = new("KnightfallAim");

    public const float MoveEvery = 0.5f;
    const float MakeFollowEvery = 1.5f;

    // A swing started inside this goes straight into its strike.
    const float WeaponReach = 180f;
    const float FistReach = 190f;

    const float ChestHeight = 40f;
    const float AimSettle = 0.35f;
    const float SightEvery = 0.2f;
    const float BurstGapLeast = 0.15f;
    const float BurstGapMost = 0.6f;
    const float ReloadSeconds = 1.6f;
    const float GunFar = 1800f;
    const float GunNoSight = 350f;
    const float GunMost = 4500f;

    public static bool Standing(RPawnVillain? body) => body is { IsValid: true } && Crowd.Standing(body);

    public static Vector3 Chest(Pawn body) => body.Location + Vector3.UnitZ * ChestHeight;

    public static Vector3 Eye(Pawn body) => body.Location + Vector3.UnitZ * MathF.Max(40f, body.BaseEyeHeight);

    // A clear line from their eyes to the other's chest. Other people don't block it.
    public static bool Sees(Pawn body, Pawn other) => body.FastTrace(Chest(other), Eye(body), Vector3.Zero, true);

    public static float Random(float least, float most) => least + System.Random.Shared.NextSingle() * (most - least);

    public static float Reach(RPawnVillain body) => body.CurrWeapon is { IsValid: true } ? WeaponReach : FistReach;

    // A body sitting in the game's pool, waiting to come out again as somebody else.
    public static bool Pooled(RPawnVillain body) => body.bPooledPawn_Inactive;

    // The game's own follow, which paths over the ground and stops inside the distance. It gives
    // up at once with nobody to follow, so it is made, aimed and only then started.
    public static void FollowTo(RBMAIController brain, Fighter fighter, Actor who, float distance, Pace pace, float now)
    {
        if (brain.GetBehaviourOfClass(RBMBehaviour_Follow.StaticClass(), true) is RBMBehaviour_Follow { IsValid: true } follow)
        {
            if (!ReferenceEquals(follow.FollowActor, who))
            {
                follow.FollowActor = who;
            }

            if (MathF.Abs(follow.FollowDist - distance) > 1f)
            {
                follow.FollowDist = distance;
                follow.FollowDistSq = distance * distance;
            }

            if (follow.Speed != pace)
            {
                follow.Speed = pace;
                follow.SetMoveToSpeed();
            }

            return;
        }

        if (Waiting(fighter.FollowMadeAt, now, MakeFollowEvery * 2f))
        {
            return;
        }

        fighter.FollowMadeAt = now + MakeFollowEvery;
        if (brain.CreateSpawnedBehaviour(RBMBehaviour_Follow.StaticClass()) is not RBMBehaviour_Follow { IsValid: true } made)
        {
            return;
        }

        made.FollowActor = who;
        made.FollowDist = distance;
        made.FollowDistSq = distance * distance;
        made.Speed = pace;
        made.bNeverAvoid = true;
        brain.ActivateSpawnedBehaviour(made);
    }

    static RBMBehaviour_Follow? FollowOf(RPawnVillain body) =>
        body.AIController is { IsValid: true } brain
        && brain.GetBehaviourOfClass(RBMBehaviour_Follow.StaticClass(), true) is RBMBehaviour_Follow { IsValid: true } follow
            ? follow
            : null;

    // The follow is asked to end itself. The brain's own call for ending a behaviour only works
    // from inside the game's script, and does nothing from here.
    public static void StopFollowing(RPawnVillain body)
    {
        FollowOf(body)?.ExitBehaviour(0);
    }

    // For someone who is an enemy again. With nothing of their own left to do they'd only stand
    // there, so they are pulled into a fight the way the game pulls a street thug in.
    public static void Rejoin(RPawnVillain body)
    {
        if (!Standing(body) || body.AIController is not { IsValid: true } brain || brain.GetBehaviour(0) is { IsValid: true })
        {
            return;
        }

        body.SetIsValidCombatant(true);
        body.ForceIntoCombat();
        if (!body.bIsCurrentCombatant)
        {
            brain.EnterCombat(true, true, false);
        }
    }

    // Before someone they may be following is taken out of the world. The follow waits for the
    // next one it is given.
    public static void Unfollow(RPawnVillain body, Actor who)
    {
        if (FollowOf(body) is { } follow && ReferenceEquals(follow.FollowActor, who))
        {
            follow.FollowActor = null!;
        }
    }

    // After they are moved by hand. The follow's path still starts from where they were.
    public static void Repath(RPawnVillain body)
    {
        if (FollowOf(body) is { FollowActor: { IsValid: true, bDeleteMe: false } } follow)
        {
            follow.SetPathToFollowActor();
        }
    }

    // What the game's own shoot-at-someone action does each tick: aim, name who gets hurt, line
    // the gun up, fire in bursts. The track target stays empty. That path is the one built for
    // Batman, and it aims to miss.
    public static void Shoot(Fighter fighter, RPawnVillain target, RBMAIController? brain, float now)
    {
        var body = fighter.Body;
        var gun = body.CurrentGun;
        var gap = Vector3.Distance(body.Location, target.Location);
        if (!ReferenceEquals(fighter.ShotAt, target))
        {
            if (body.bFiring)
            {
                body.WeaponStop();
            }

            fighter.ShotAt = target;
            fighter.AimedSince = now;
            fighter.BurstAt = now + AimSettle;
            fighter.SightAt = -1f;
        }

        if (!Waiting(fighter.SightAt, now, SightEvery * 2f))
        {
            fighter.Sight = Sees(body, target);
            fighter.SightAt = now + SightEvery;
            if (fighter.Sight)
            {
                fighter.SeenAt = now;
            }
        }

        if (!ReferenceEquals(body.DamageTarget, target))
        {
            body.SetDamageTarget(target);
        }

        if (body.TrackTarget is { IsValid: true })
        {
            body.TrackTarget = null!;
        }

        Raise(fighter, now);
        body.AimAt(Chest(target), AimName, AimTurn.ALLOWTURNINGTOAIM_Yes, 1f, default);
        body.AdjustGunToHitTarget(target);
        fighter.Shooting = true;

        if (brain is not null && Due(ref fighter.MoveAt, now, MoveEvery))
        {
            // Closes in until there's a shot. A follow distance just past where they stand
            // leaves them standing.
            var wanted = !fighter.Sight ? GunNoSight : gap > GunFar ? GunFar * 0.7f : gap + 150f;
            FollowTo(brain, fighter, target, wanted, !fighter.Sight || gap > GunFar * 1.5f ? Pace.MOVESPEED_Run : Pace.MOVESPEED_Walk, now);
        }

        if (!fighter.Sight || gap > GunMost || now - fighter.AimedSince < AimSettle)
        {
            if (body.bFiring)
            {
                body.WeaponStop();
            }

            return;
        }

        if (body.bFiring || Waiting(fighter.BurstAt, now, 5f))
        {
            return;
        }

        body.bFireOnceOnly = false;
        body.WeaponFire(true, false);
        body.bStopAfterBurst = true;
        var pause = gun is { IsValid: true } && gun.BurstPause > 0.05f ? gun.BurstPause : 0.8f;
        fighter.BurstAt = now + pause + Random(BurstGapLeast, BurstGapMost);

        // An empty clip refuses to fire. The game's own quick reload fills it after a pause.
        if (!body.bFiring)
        {
            body.ReloadGun(true);
            fighter.BurstAt = now + ReloadSeconds;
        }
    }

    // Other behaviours lower the gun, so it is checked twice a second. A rifle can only be aimed
    // on the move in the combat stance.
    static void Raise(Fighter fighter, float now)
    {
        if (!Due(ref fighter.StanceAt, now, 0.5f))
        {
            return;
        }

        var body = fighter.Body;
        if (body.GetMovementStance().Index != CombatStance.Index)
        {
            body.ChangeMovementStance(CombatStance);
        }

        if (body.GetWeaponStance().Index != Aiming.Index)
        {
            body.ChangeWeaponStance(Aiming);
        }

        fighter.Raised = true;
    }

    // The damage target is always cleared. The gun keeps aiming at it otherwise, and they go on
    // shooting someone already on the floor.
    public static void StopShooting(Fighter fighter)
    {
        var body = fighter.Body;
        if (body.bFiring)
        {
            body.WeaponStop();
        }

        if (body.DamageTarget is RPawnVillain)
        {
            body.SetDamageTarget(null!);
        }

        if (body.TrackTarget is RPawnVillain)
        {
            body.TrackTarget = null!;
        }

        if (!fighter.Shooting)
        {
            return;
        }

        fighter.Shooting = false;
        fighter.ShotAt = null;
        body.StopAimAt();
        if (fighter.Raised)
        {
            fighter.Raised = false;
            body.ChangeWeaponStance(Relaxed);
        }
    }

    // Out of the fight stance, so they don't walk after you still squared up to the last fight.
    public static void StandEasy(RPawnVillain body)
    {
        body.StopAimAt();
        body.StopFaceAt();
        body.StopLookAt();
        if (body.GetMovementStance().Index != StandingStance.Index)
        {
            body.ChangeMovementStance(StandingStance);
        }

        if (body.GetWeaponStance().Index != Relaxed.Index)
        {
            body.ChangeWeaponStance(Relaxed);
        }
    }

    // A punch asks the fight behaviour for its stance and whether its target is in the fight, and
    // gives up with none. Bodyguards stay out of the game's fight, so they carry one that is
    // never started, only there to answer.
    public static void EnsureStandIn(RBMAIController brain)
    {
        if (brain.CombatBehaviour is { IsValid: true })
        {
            return;
        }

        var stand = GameObject.ConstructObject<RBMBehaviour_CombatAI>(brain);
        if (stand.CombatMoveStance.Index == FName.None.Index)
        {
            stand.CombatMoveStance = CombatStance;
        }

        // Told whose it is, the way the brain tells any behaviour it takes on. The game asks a
        // fighter's behaviour about its own pawn, like for the push it gives an enemy waiting to
        // attack them, and one with no pawn crashes the game there.
        stand.RegisterController(brain);
        if (brain.Pawn is RPawnCombat { IsValid: true } body)
        {
            stand.CombatManager = body.CombatManager;
        }

        brain.CombatBehaviour = stand;
    }

    public static void DropStandIn(RBMAIController brain)
    {
        if (!brain.bInCombat && brain.CombatBehaviour is { IsValid: true } stand && ReferenceEquals(stand.Outer, brain)
            && !brain.HasBehaviourOfClass(stand.Class, true))
        {
            brain.CombatBehaviour = null!;
        }
    }

    // True when the time has come, and sets the next one.
    public static bool Due(ref float at, float now, float every)
    {
        if (Waiting(at, now, every * 2f))
        {
            return false;
        }

        at = now + every;
        return true;
    }

    // True while a set time is still ahead. Game time starts over with a new world, so a time
    // further ahead than it could have been set counts as passed.
    public static bool Waiting(float until, float now, float most) => now < until && until - now <= most;

    // Seconds since a time was noted. One noted in a world before this one counts as long ago.
    public static float Since(float then, float now) => now >= then ? now - then : float.MaxValue;
}
