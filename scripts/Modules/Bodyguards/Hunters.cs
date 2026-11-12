#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// ENEMIES FIGHT BODYGUARDS. An enemy nearer a bodyguard than you goes for the bodyguard, a few
// per bodyguard at most so you are never left alone. He stays in the game's own fight and is
// only pointed at his bodyguard and made to swing. Once the bodyguard is down, or you come
// nearer, he is handed back to the game as he was.
static class Hunters
{
    const float RecruitEvery = 0.5f;

    // How close to a bodyguard an enemy has to be, and how much nearer than to you.
    const float Reach = 2000f;
    const float Margin = 200f;

    // He goes back to you once you are this much nearer than his bodyguard.
    const float LetGo = 400f;

    const int PerGuard = 3;

    const float WeaponReach = 180f;
    const float FistReach = 190f;
    const float SwingGapLeast = 0.6f;
    const float SwingGapMost = 1.4f;

    // Where a waiting hunter stands off from his bodyguard.
    const float MeleeStandoff = 140f;
    const float GunStandoff = 700f;

    static readonly List<Hunter> List = new();
    static readonly List<Guard> Standing = new();
    static float _recruitAt;

    public static int Count => List.Count;

    public static Hunter? Of(RPawnVillain body)
    {
        foreach (var hunter in List)
        {
            if (ReferenceEquals(hunter.Body, body))
            {
                return hunter;
            }
        }

        return null;
    }

    public static void Tick(List<RPawnVillain> enemies, IReadOnlyList<Guard> team, Pawn you, bool scanned, float now)
    {
        if (!Bodyguards.EnemiesFight.On || team.Count == 0)
        {
            ReleaseAll();
            return;
        }

        Standing.Clear();
        foreach (var guard in team)
        {
            if (Fight.Standing(guard.Body))
            {
                Standing.Add(guard);
            }
        }

        if (scanned && Fight.Due(ref _recruitAt, now, RecruitEvery))
        {
            Recruit(enemies, you);
        }

        for (var i = List.Count - 1; i >= 0; i--)
        {
            var hunter = List[i];
            if (!hunter.Body.IsValid || hunter.Body.bDeleteMe)
            {
                List.RemoveAt(i);
            }
            else if (!Steer(hunter, you, scanned, now))
            {
                Release(hunter);
                List.RemoveAt(i);
            }
        }

        Standing.Clear();
    }

    static void Recruit(List<RPawnVillain> enemies, Pawn you)
    {
        var most = Standing.Count * PerGuard;
        if (List.Count >= most)
        {
            return;
        }

        foreach (var enemy in enemies)
        {
            if (!Fight.Standing(enemy) || Of(enemy) is not null)
            {
                continue;
            }

            if (Nearest(enemy.Location, out var away) is not { } prey || away > Reach
                || away > Vector3.Distance(enemy.Location, you.Location) - Margin)
            {
                continue;
            }

            List.Add(new Hunter { Body = enemy, Prey = prey, Target = prey.Body });
            if (List.Count >= most)
            {
                return;
            }
        }
    }

    static Guard? Nearest(Vector3 at, out float away)
    {
        Guard? best = null;
        away = float.MaxValue;
        foreach (var guard in Standing)
        {
            var distance = Vector3.Distance(guard.Body.Location, at);
            if (distance < away)
            {
                away = distance;
                best = guard;
            }
        }

        return best;
    }

    // False when he should go back to the game.
    static bool Steer(Hunter hunter, Pawn you, bool scanned, float now)
    {
        var body = hunter.Body;
        if (!Crowd.Standing(body) || body.IsIncapacitated())
        {
            return false;
        }

        // Sticks with his bodyguard until that one is down, then takes the nearest left.
        if (hunter.Prey is not { } prey || !Standing.Contains(prey))
        {
            prey = Nearest(body.Location, out _);
            hunter.Prey = prey;
        }

        if (prey is null || Vector3.Distance(body.Location, you.Location) < Vector3.Distance(body.Location, prey.Body.Location) - LetGo)
        {
            return false;
        }

        // Calmed by something else, like a scene. He is left to it.
        if (scanned && !body.IsHostile())
        {
            return false;
        }

        // The game points him back at you when he is hit or gets up, so this is kept every tick.
        var target = prey.Body;
        hunter.Target = target;
        if (!body.bSelfManageTargetSelection)
        {
            body.bSelfManageTargetSelection = true;
            hunter.SelfManaged = true;
        }

        if (!ReferenceEquals(body.OverrideTargetPawn, target))
        {
            body.OverrideTargetPawn = target;
        }

        if (body.CurrentGun is { IsValid: true })
        {
            Fight.Shoot(hunter, target, null, now);
        }
        else
        {
            Fight.StopShooting(hunter);
            Swing(hunter, target, now);
        }

        Strikes.Watch(hunter);
        Strikes.Combo(hunter);
        return true;
    }

    // The game's fight has him wait for a turn against Batman, so against a bodyguard he'd
    // mostly stand there. In reach, the attack is started for him.
    static void Swing(Hunter hunter, RPawnVillain target, float now)
    {
        var body = hunter.Body;
        if ((now < hunter.SwingAt && hunter.SwingAt - now < 5f) || body.CombatMove is { IsValid: true } || body.IsStunned()
            || body.AIController is not { IsValid: true } brain)
        {
            return;
        }

        var reach = body.CurrWeapon is { IsValid: true } ? WeaponReach : FistReach;
        if (Vector3.Distance(body.Location, target.Location) > reach)
        {
            return;
        }

        if (!brain.bInCombat)
        {
            brain.EnterCombat(false, true, false);
        }

        if (!brain.bInCombat || brain.CombatBehaviour is not { IsValid: true })
        {
            return;
        }

        hunter.SwingAt = now + Fight.Random(SwingGapLeast, SwingGapMost);
        var move = body.GetAttackMoveClass() is { IsValid: true } own ? own : RCombatMove_VillainCloseAttack.StaticClass();
        body.SpawnMove(move);
        if (body.CombatMove is RCombatMove_VillainAttack { IsValid: true } attack && !ReferenceEquals(attack.Target, target))
        {
            attack.Target = target;
        }
    }

    static void Release(Hunter hunter)
    {
        var body = hunter.Body;
        if (!body.IsValid || body.bDeleteMe)
        {
            return;
        }

        if (body.OverrideTargetPawn is { IsValid: true })
        {
            body.OverrideTargetPawn = null!;
        }

        Fight.StopShooting(hunter);
        if (hunter.SelfManaged)
        {
            hunter.SelfManaged = false;
            body.bSelfManageTargetSelection = false;
        }
    }

    // Someone taken on as a bodyguard stops being a hunter without being handed back.
    public static void Forget(RPawnVillain body)
    {
        if (Of(body) is { } hunter)
        {
            Release(hunter);
            List.Remove(hunter);
        }
    }

    public static void ReleaseAll()
    {
        foreach (var hunter in List)
        {
            Kernel.Run("spawner.bodyguards.hunters", static held => Release(held), hunter);
        }

        List.Clear();
    }

    // Enemies waiting their turn are moved by a sum of forces, and Batman's pushes them out to a
    // ring around him. A hunter gets one force toward his bodyguard instead. Everyone else gets
    // the game's answer.
    [Redirect(typeof(RBMBehaviour_CombatAI), nameof(RBMBehaviour_CombatAI.GetWaitForCombatMovementForce))]
    static Vector3 WaitForce(RBMBehaviour_CombatAI self)
    {
        var force = Redirects.RunOriginal<Vector3>();
        if (List.Count == 0)
        {
            return force;
        }

        try
        {
            return Toward(self, force);
        }
        catch (Exception error)
        {
            Log.Error("bodyguards.hunter_move", "A hunter's movement could not be worked out", error);
            return force;
        }
    }

    static Vector3 Toward(RBMBehaviour_CombatAI behaviour, Vector3 force)
    {
        if (behaviour.Villain is not { IsValid: true } body || Of(body) is not { Target: { IsValid: true } target }
            || !ReferenceEquals(body.OverrideTargetPawn, target))
        {
            return force;
        }

        var way = (target.Location - body.Location) with { Z = 0f };
        var gap = way.Length();
        if (gap < 1f)
        {
            return Vector3.Zero;
        }

        var hold = body.CurrentGun is { IsValid: true } ? GunStandoff : MeleeStandoff;
        var push = MathF.Max(force.Length(), behaviour.MaxMoveSpeedForce);
        if (gap > hold)
        {
            return way / gap * push;
        }

        return gap < hold * 0.6f ? -way / gap * push * 0.5f : Vector3.Zero;
    }
}
