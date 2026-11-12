#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using Pace = BmSDK.BmGame.RBMBehaviour_Follow.MoveSpeed;

namespace Knightfall;

// What one bodyguard does each tick. Between fights they follow you with the game's own follow.
// In a fight they pick whoever threatens you most. Gunmen are aimed and fired from here, and
// everyone else closes in and starts the game's own attack moves.
static class GuardBrain
{
    const float Metre = Teleport.UnitsPerMetre;

    const float ThinkEvery = 0.5f;

    // A new threat has to beat the current one by this much, so they don't spin between two.
    const float SwitchMargin = 400f;

    // Anyone this close to a bodyguard is fought wherever you are.
    const float SelfDefence = 1500f;

    // An enemy this close to you comes first.
    const float OnTopOfYou = 700f;

    const float WeaponReach = 180f;
    const float FistReach = 190f;
    const float FistStandoff = 110f;
    const float FaceRange = 900f;
    const float SwingGapLeast = 0.25f;
    const float SwingGapMost = 0.8f;
    const float SightGrace = 1.5f;
    const float StandEasyAfter = 1.5f;

    // A gunman only checks his line of sight to the few most urgent threats.
    const int SightChecks = 4;
    const float Unseen = 2500f;

    // How far an ordered target may be.
    const float OrderReach = 15000f;

    // Each place further back in the team follows this much further off.
    const float SlotGap = 60f;

    const float CatchUpRange = 4000f;
    const float CatchUpBehind = 350f;
    const float CatchUpEvery = 2f;
    const float ViewCone = 0.5f;

    // You count as stopped in the car below this speed.
    const float CarSlow = 600f;

    static readonly List<(float Score, RPawnVillain Who)> Threats = new();

    public static void Run(Guard guard, List<RPawnVillain> enemies, IReadOnlyList<Guard> team, Pawn you, bool scanned, float now)
    {
        Keep(guard);

        var orders = Bodyguards.Orders.Index;
        var target = orders == Bodyguards.StandDown ? null : Current(guard, enemies, you, scanned, now);
        if (target is not null && ReferenceEquals(target, guard.Ordered))
        {
            // An ordered target is kept until it's down.
        }
        else if (orders != Bodyguards.StandDown && ((scanned && Fight.Due(ref guard.ThinkAt, now, ThinkEvery)) || (target is null && guard.Target is not null)))
        {
            if (target is not null)
            {
                guard.Score = Score(guard, target, team, you, now - guard.SeenAt < SightGrace);
            }

            var best = Pick(guard, enemies, team, you, out var bestScore);
            if (best is not null && !ReferenceEquals(best, target) && (target is null || bestScore < guard.Score - SwitchMargin))
            {
                target = best;
                guard.Score = bestScore;
            }
        }

        if (target is null)
        {
            guard.Target = null;
            Idle(guard, you, now);
            return;
        }

        guard.Target = target;
        Engage(guard, target, now);
    }

    // The flags that keep the game from pointing them back at you, checked every tick because the
    // game writes them again when someone is hit or gets up.
    static void Keep(Guard guard)
    {
        var body = guard.Body;
        if (!body.bSelfManageTargetSelection)
        {
            body.bSelfManageTargetSelection = true;
            guard.SelfManaged = true;
        }

        // The game's own mark for a partner, which keeps your strikes from picking them.
        if (body.CannotBeTargettedByPlayer is not { IsValid: true } && World.Controller is { } controller)
        {
            body.CannotBeTargettedByPlayer = controller;
            guard.Marked = true;
        }
    }

    static RPawnVillain? Current(Guard guard, List<RPawnVillain> enemies, Pawn you, bool scanned, float now)
    {
        if (guard.Ordered is { } ordered)
        {
            if (Fight.Standing(ordered) && now < guard.OrderedUntil && guard.OrderedUntil - now < 120f
                && Vector3.DistanceSquared(ordered.Location, guard.Body.Location) < OrderReach * OrderReach)
            {
                return ordered;
            }

            guard.Ordered = null;
        }

        if (guard.Target is not { } target || !Fight.Standing(target) || !InReach(guard, target, you))
        {
            return null;
        }

        // A fresh list says whether they still count as an enemy.
        if (scanned && !Listed(enemies, target))
        {
            return null;
        }

        return target;
    }

    static bool Listed(List<RPawnVillain> enemies, RPawnVillain who)
    {
        foreach (var enemy in enemies)
        {
            if (ReferenceEquals(enemy, who))
            {
                return true;
            }
        }

        return false;
    }

    // Holding their ground, only what comes near them counts. Otherwise it is what comes near you.
    static bool InReach(Guard guard, RPawnVillain enemy, Pawn you)
    {
        var range = (float)Bodyguards.Range.Value * Metre;
        var toGuard = Vector3.DistanceSquared(enemy.Location, guard.Body.Location);
        if (Bodyguards.Orders.Index == Bodyguards.Hold)
        {
            return toGuard <= range * range;
        }

        return toGuard <= SelfDefence * SelfDefence || Vector3.DistanceSquared(enemy.Location, you.Location) <= range * range;
    }

    // Scored without line of sight first. A gunman then traces only the best few, since a trace
    // per enemy per bodyguard adds up.
    static RPawnVillain? Pick(Guard guard, List<RPawnVillain> enemies, IReadOnlyList<Guard> team, Pawn you, out float bestScore)
    {
        bestScore = float.MaxValue;
        RPawnVillain? best = null;
        var gun = guard.Body.CurrentGun is { IsValid: true };
        Threats.Clear();
        foreach (var enemy in enemies)
        {
            if (!Fight.Standing(enemy) || !InReach(guard, enemy, you))
            {
                continue;
            }

            var score = Score(guard, enemy, team, you, true);
            if (gun)
            {
                Threats.Add((score, enemy));
            }
            else if (score < bestScore)
            {
                bestScore = score;
                best = enemy;
            }
        }

        if (!gun)
        {
            return best;
        }

        Threats.Sort(static (a, b) => a.Score.CompareTo(b.Score));
        for (var i = 0; i < Threats.Count; i++)
        {
            var (score, enemy) = Threats[i];

            // Past the few that are checked, they're taken as hidden, so one he can see wins.
            if (i >= SightChecks || !Fight.Sees(guard.Body, enemy))
            {
                score += Unseen;
            }

            if (score < bestScore)
            {
                bestScore = score;
                best = enemy;
            }
        }

        Threats.Clear();
        return best;
    }

    // Lower is more urgent. Seen says a gunman had this one in sight a moment ago.
    static float Score(Guard guard, RPawnVillain enemy, IReadOnlyList<Guard> team, Pawn you, bool seen)
    {
        var toGuard = Vector3.Distance(enemy.Location, guard.Body.Location);
        var toYou = Vector3.Distance(enemy.Location, you.Location);
        var score = toGuard * 0.6f + toYou * 0.4f;
        if (toYou < OnTopOfYou)
        {
            score -= 1500f;
        }

        if (enemy.CurrentGun is { IsValid: true })
        {
            score -= 700f;
        }

        if (guard.Body.CurrentGun is { IsValid: true })
        {
            if (!seen && !Fight.Sees(guard.Body, enemy))
            {
                score += Unseen;
            }
        }
        else
        {
            // Fist fighters spread out over the enemies and don't all pile on one.
            foreach (var other in team)
            {
                if (!ReferenceEquals(other, guard) && ReferenceEquals(other.Target, enemy))
                {
                    score += 300f;
                }
            }
        }

        return score;
    }

    static void Engage(Guard guard, RPawnVillain target, float now)
    {
        var body = guard.Body;
        guard.Fought = true;
        guard.FoughtAt = now;
        if (!ReferenceEquals(body.OverrideTargetPawn, target))
        {
            body.OverrideTargetPawn = target;
        }

        var brain = body.AIController is { IsValid: true } found ? found : null;
        if (body.CurrentGun is { IsValid: true })
        {
            // A gun only fires for someone the game counts as hostile.
            if (guard.Benched || (Fight.Due(ref guard.HostileCheckAt, now, 0.5f) && !body.IsHostile()))
            {
                body.SetHostile(true, FName.None);
                guard.Benched = false;
            }

            Fight.Shoot(guard, target, brain, now);
            return;
        }

        Fight.StopShooting(guard);
        if (brain is not null)
        {
            Brawl(guard, brain, target, now);
        }
    }

    // In the game's own fight a thug waits for a turn against Batman and swings his way. So a
    // bodyguard is kept stood down and out of it, and is walked in, turned and made to swing from
    // here. The hit lands through Strikes.
    static void Brawl(Guard guard, RBMAIController brain, RPawnVillain target, float now)
    {
        var body = guard.Body;
        Bench(guard, now);
        if (brain.bInCombat)
        {
            brain.ExitCombat();
        }

        var gap = Vector3.Distance(body.Location, target.Location);
        if (!ReferenceEquals(guard.Facing, target) && gap < FaceRange)
        {
            body.FaceAtActor(target, FName.None);
            guard.Facing = target;
        }

        // An attack walks its last steps in a state that quits without a real fight behaviour,
        // so swings start close enough to go straight into the strike.
        var armed = body.CurrWeapon is { IsValid: true };
        var reach = armed ? WeaponReach : FistReach;
        Fight.EnsureStandIn(brain);
        if (Fight.Due(ref guard.MoveAt, now, Fight.MoveEvery))
        {
            if (gap > reach)
            {
                Fight.FollowTo(brain, guard, target, FistStandoff, gap > 800f ? Pace.MOVESPEED_Sprint : Pace.MOVESPEED_Run, now);
            }
            else
            {
                Fight.FollowTo(brain, guard, target, gap + 100f, Pace.MOVESPEED_Run, now);
            }
        }

        if (gap > reach || (now < guard.SwingAt && guard.SwingAt - now < 5f) || body.CombatMove is { IsValid: true } || body.IsStunned())
        {
            return;
        }

        guard.SwingAt = now + Fight.Random(SwingGapLeast, SwingGapMost);

        // The pawn is asked, not the weapon. From here the weapon's own answer is the base one,
        // which is nothing for a knife.
        var punch = RCombatMove_VillainCloseAttack.StaticClass();
        if (armed && body.GetAttackMoveClass() is { IsValid: true } move && !ReferenceEquals(move, punch))
        {
            body.SpawnMove(move);

            // If the game still handed it Batman, it is pointed at the enemy before it swings.
            if (body.CombatMove is RCombatMove_VillainAttack { IsValid: true } attack && !ReferenceEquals(attack.Target, target))
            {
                attack.Target = target;
            }

            return;
        }

        // Built the way the game builds a rifle butt strike, which skips the walk up.
        if (body.Spawn(punch, body, FName.None, body.Location, body.Rotation, null!, true) is RCombatMove_VillainCloseAttack { IsValid: true } swing)
        {
            swing.Villain = body;
            swing.Target = target;
            swing.bForceStraightIntoAttack = true;
            swing.Initialise();
        }
    }

    // Stood down, so the game's fight leaves them out and they never come for you. The game can
    // make them hostile again when they are hit, so it is checked twice a second.
    static void Bench(Guard guard, float now)
    {
        if ((!guard.Benched || Fight.Due(ref guard.HostileCheckAt, now, 0.5f)) && guard.Body.IsHostile())
        {
            guard.Body.SetHostile(false, Fight.Reason);
        }

        guard.Benched = true;
    }

    static void Idle(Guard guard, Pawn you, float now)
    {
        var body = guard.Body;
        Fight.StopShooting(guard);
        if (guard.Facing is not null)
        {
            guard.Facing = null;
            body.StopFaceAt();
        }

        if (guard.Fought && (now - guard.FoughtAt > StandEasyAfter || now < guard.FoughtAt) && body.CombatMove is not { IsValid: true })
        {
            guard.Fought = false;
            Fight.StandEasy(body);
        }

        if (body.OverrideTargetPawn is { IsValid: true })
        {
            body.OverrideTargetPawn = null!;
        }

        Bench(guard, now);
        if (body.AIController is not { IsValid: true } brain)
        {
            return;
        }

        if (brain.bInCombat)
        {
            brain.ExitCombat();
        }

        Fight.DropStandIn(brain);
        if (!Fight.Due(ref guard.MoveAt, now, Fight.MoveEvery))
        {
            return;
        }

        if (Bodyguards.Orders.Index == Bodyguards.Hold)
        {
            Fight.StopFollowing(body);
            return;
        }

        var away = Vector3.Distance(body.Location, you.Location);
        if (Bodyguards.CatchUp.On && away > CatchUpRange && Fight.Due(ref guard.CatchUpAt, now, CatchUpEvery) && CatchUpTo(guard, you))
        {
            return;
        }

        // Further back in the team means a little further off, so they don't crowd one spot.
        var gap = (float)Bodyguards.Gap.Value * Metre + guard.Slot % 4 * SlotGap;
        if (you is RVehicleBatmobileBase)
        {
            gap += 300f;
        }

        var pace = away > 1500f ? Pace.MOVESPEED_Sprint : away > 600f ? Pace.MOVESPEED_Run : Pace.MOVESPEED_Walk;
        Fight.FollowTo(brain, guard, you, gap, pace, now);
    }

    // Left far behind, they appear behind you once you've stopped, never in a scene and never
    // where you can see it happen.
    static bool CatchUpTo(Guard guard, Pawn you)
    {
        if (World.Controller is not { } controller || controller.bCinematicMode || Switcher.InScene(controller))
        {
            return false;
        }

        // Gliding, grappling or driving fast, they'd only be left behind again.
        var settled = you is RVehicleBatmobileBase ? you.Velocity.LengthSquared() < CarSlow * CarSlow : you.Physics == Actor.EPhysics.PHYS_Walking;
        if (!settled)
        {
            return false;
        }

        controller.GetPlayerViewPoint(out var eye, out var view);
        var body = guard.Body;
        if (InView(you, eye, view, body.Location))
        {
            return false;
        }

        var back = -Teleport.Flat(view.ToDirection());
        var side = new Vector3(-back.Y, back.X, 0f);
        var room = you is RVehicleBatmobileBase ? CatchUpBehind * 2f : CatchUpBehind;
        foreach (var lean in new[] { 0f, 0.6f, -0.6f, 1.2f, -1.2f })
        {
            var spot = you.Location + Teleport.Flat(back + side * lean) * room + side * ((guard.Slot % 3 - 1) * 90f);
            if (!Teleport.Floor(you, spot, you.Location.Z + 200f, you.Location.Z - 500f, out var floor) || !Teleport.Dry(floor)
                || !you.FastTrace(floor + Vector3.UnitZ * 60f, you.Location, Vector3.Zero, false)
                || InView(you, eye, view, floor + Vector3.UnitZ * 60f))
            {
                continue;
            }

            Place(body, floor, MathF.Atan2(-back.Y, -back.X) * 180f / MathF.PI);
            return true;
        }

        return false;
    }

    static bool InView(Pawn you, Vector3 eye, Rotator view, Vector3 at)
    {
        var toward = at - eye;
        if (toward.LengthSquared() < 1f)
        {
            return false;
        }

        return Vector3.Dot(Vector3.Normalize(toward), view.ToDirection()) >= ViewCone && you.FastTrace(at, eye, Vector3.Zero, false);
    }

    // Stands them on a floor point, facing the given way.
    public static bool Place(RPawnVillain body, Vector3 floor, float yaw)
    {
        body.StopMoveTo();
        if (!body.SetLocation(floor + new Vector3(0f, 0f, Teleport.Lift(body))))
        {
            return false;
        }

        body.Velocity = Vector3.Zero;
        body.SetRotation(new Rotator(0f, yaw, 0f));
        return true;
    }

    // Puts back what we changed. Hostile says whether they go back to being an enemy.
    public static void Release(Guard guard, bool hostile)
    {
        var body = guard.Body;
        if (!body.IsValid || body.bDeleteMe)
        {
            return;
        }

        if (body.OverrideTargetPawn is { IsValid: true })
        {
            body.OverrideTargetPawn = null!;
        }

        Fight.StopShooting(guard);
        if (guard.Facing is not null)
        {
            guard.Facing = null;
            body.StopFaceAt();
        }

        Fight.StopFollowing(body);
        if (body.AIController is { IsValid: true } brain)
        {
            Fight.DropStandIn(brain);
        }

        if (guard.SelfManaged)
        {
            guard.SelfManaged = false;
            body.bSelfManageTargetSelection = false;
        }

        if (guard.Marked)
        {
            guard.Marked = false;
            if (body.CannotBeTargettedByPlayer is { IsValid: true })
            {
                body.CannotBeTargettedByPlayer = null!;
            }
        }

        if (hostile != body.IsHostile())
        {
            body.SetHostile(hostile, hostile ? FName.None : Fight.Reason);
        }

        guard.Benched = !hostile;
    }
}
