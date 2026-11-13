#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// EDITOR > CHANGE. The state rows show the selected object and change it. UNDO and RESTORE take
// them back. The action rows use the game's own damage, healing and disarming, so they can't be
// undone.
static class Change
{
    const float LaunchUp = 1800f;
    const float LaunchAhead = 500f;
    const float FlipLift = 150f;
    const int Lethal = 100000;

    // A destructible breaks every piece within this of its middle.
    const float BreakRadius = 1000f;

    static double _size = 1;
    static bool _frozen;
    static bool _removed;
    static bool _ghost;

    public static readonly Slider Size = new(
        "editor.change.size", "SIZE",
        "Shrinks or grows the selected object. On people it's only how they look.",
        min: 0.1, max: 5, step: 0.1, value: 1, unit: "X", saved: false)
    {
        Preference = true,
        Block = static () => Refusal(Size!, Editor.Selected),
    };

    public static readonly Toggle Freeze = new(
        "editor.change.freeze", "FREEZE",
        "Holds the selected object still. People stop thinking and moving, vehicles and props hang where they are.",
        saved: false)
    {
        Preference = true,
        Block = static () => Refusal(Freeze!, Editor.Selected),
    };

    public static readonly Toggle Remove = new(
        "editor.change.remove", "REMOVE",
        "Takes the selected object out of sight, with no collision, held still. Nothing is deleted, so missions can't break. Pieces built into the level can be removed too. SELECTED still lists it so it can come back.",
        saved: false)
    {
        Preference = true,
        Block = static () => Refusal(Remove!, Editor.Selected),
    };

    public static readonly Toggle Ghost = new(
        "editor.change.no_collision", "NO COLLISION",
        "You and everything else pass straight through the selected object. A physics object is held still, or it would fall through the world.",
        saved: false)
    {
        Preference = true,
        Block = static () => Refusal(Ghost!, Editor.Selected),
    };

    public static readonly Command Launch = new(
        "editor.change.launch", "LAUNCH",
        "Throws the selected person, vehicle or physics object into the air.",
        LaunchSelected)
    {
        Block = static () => CantLaunch(Editor.Selected),
    };

    public static readonly Command Flip = new(
        "editor.change.flip", "FLIP OVER",
        "Turns the selected vehicle or prop upside down.",
        FlipSelected)
    {
        Block = static () => CantFlip(Editor.Selected),
    };

    public static readonly Command Heal = new(
        "editor.change.heal", "HEAL OR REPAIR",
        "Gives the selected person or vehicle full health, and puts a broken prop back together.",
        HealSelected)
    {
        Block = static () => CantHeal(Editor.Selected),
    };

    public static readonly Command Destroy = new(
        "editor.change.destroy", "DESTROY OR KNOCK OUT",
        "The game's own damage. Knocks people out, destroys vehicles and sentry guns, breaks props and switches turrets off like a disruptor. UNDO can't take it back, but HEAL OR REPAIR rebuilds a prop.",
        DestroySelected)
    {
        Block = static () => CantBreak(Editor.Selected),
    };

    public static readonly Command Disarm = new(
        "editor.change.disarm", "DISARM",
        "The selected person drops what they're holding.",
        DisarmSelected)
    {
        Block = static () => NotPerson(Editor.Selected),
    };

    public static readonly Command Revive = new(
        "editor.change.revive", "REVIVE",
        "The selected person gets back up, the way the game wakes people.",
        ReviveSelected)
    {
        Block = static () => NotPerson(Editor.Selected),
    };

    public static readonly Catalog.Section[] Sections =
    {
        new("STATE", Size, Freeze, Remove, Ghost),
        new("ACT", Launch, Flip, Heal, Destroy, Disarm, Revive),
        new("UNDO", Editor.Undo, Editor.RestoreSelected),
    };

    // A new selection, or none. The state rows show what's been done to it.
    public static void Selected(Actor? actor)
    {
        var edit = actor is null ? null : Edits.Find(actor);
        Size.Show(edit?.Size ?? 1f);
        Freeze.Show(edit?.Frozen ?? false);
        Remove.Show(edit?.Removed ?? false);
        Ghost.Show(edit?.Ghost ?? false);
        (_size, _frozen, _removed, _ghost) = (Size.Value, Freeze.On, Remove.On, Ghost.On);
    }

    // Why a state row can't act on the selection, or null when it can. A piece of the level can
    // only be removed, which the game does to them itself. Its size and collision don't follow.
    public static string? Refusal(Feature row, Actor? actor)
    {
        if (actor is null)
        {
            return "NOTHING SELECTED";
        }

        return ReferenceEquals(row, Remove) ? null : Things.Fixed(actor);
    }

    public static void Tick()
    {
        if (Size.Value != _size || Freeze.On != _frozen || Remove.On != _removed || Ghost.On != _ghost)
        {
            var actor = Editor.Selected;
            Feature row = Size.Value != _size ? Size : Freeze.On != _frozen ? Freeze : Remove.On != _removed ? Remove : Ghost;
            if (Refusal(row, actor) is { } why)
            {
                // The row goes back to what it showed, and says why.
                Selected(actor);
                Toasts.Show(row.Plain, why, OutcomeKind.Failed, row);
            }
            else
            {
                var edit = Edits.Touch(actor!, row.Plain);
                edit.Size = (float)Size.Value;
                edit.Frozen = Freeze.On;
                edit.Removed = Remove.On;
                edit.Ghost = Ghost.On;
                if (edit.Removed)
                {
                    Moving.LetGo();
                }

                Edits.Apply(edit);
                (_size, _frozen, _removed, _ghost) = (Size.Value, Freeze.On, Remove.On, Ghost.On);
            }
        }

        Edits.Keep(Moving.Held);
    }

    // Why each ACT row can't act on the selection, or null when it can. The rows show these, and
    // the commands ask again when pressed.

    // Something to act on that's still in sight. Acting on what REMOVE took away would play out
    // unseen.
    static string? Absent(Actor? actor) =>
        actor is null ? "NOTHING SELECTED" : Edits.Find(actor) is { Removed: true } ? "IT'S REMOVED" : null;

    // Moving it needs it loose: not part of the level, removed or frozen.
    static string? Pinned(Actor? actor) =>
        Moving.Stuck(actor) ?? (Edits.Find(actor!) is { Frozen: true } ? "IT'S FROZEN" : null);

    static string? CantLaunch(Actor? actor) =>
        Pinned(actor) ?? (!Things.IsPerson(actor!) && actor!.Physics != Actor.EPhysics.PHYS_RigidBody ? "IT DOESN'T MOVE FREELY" : null);

    static string? CantFlip(Actor? actor) =>
        actor is null ? "NOTHING SELECTED" : Things.IsPerson(actor) ? "NOT PEOPLE" : Pinned(actor);

    static string? CantHeal(Actor? actor) => Absent(actor) ?? (actor is Pawn or RDestructibleProp ? null : "NO HEALTH");

    // A prop with no mesh of its own crashes the game when first damaged, since the game sets its
    // mesh falling without checking it has one.
    static string? CantBreak(Actor? actor) => Absent(actor) ?? actor switch
    {
        RDestructibleProp { MeshComp: not { IsValid: true } } => "CAN'T BREAK THAT",
        RBMPawnAI or RVehicleNPC or RSentryGunBase or RTurretBase or ApexDestructibleActor => null,
        _ => "CAN'T BREAK THAT",
    };

    static string? NotPerson(Actor? actor) => Absent(actor) ?? (actor is RBMPawnAI ? null : "NOT A PERSON");

    // Frozen things can't play out a fall or a hit, so they're let go first.
    static void Thaw(Actor actor)
    {
        if (Edits.Find(actor) is { Frozen: true } edit)
        {
            edit.Frozen = false;
            Edits.Apply(edit);
            Selected(actor);
        }
    }

    static Outcome LaunchSelected()
    {
        if (CantLaunch(Editor.Selected) is { } why)
        {
            return Outcome.Failed(why);
        }

        var actor = Editor.Selected!;
        var ahead = World.Controller is { } controller ? Ahead(controller) : Vector3.Zero;
        Edits.Touch(actor, "LAUNCH");
        Edits.Throw(actor, Vector3.UnitZ * LaunchUp + ahead * LaunchAhead);
        return Outcome.Done("LAUNCHED");
    }

    static Vector3 Ahead(PlayerController controller)
    {
        controller.GetPlayerViewPoint(out _, out var view);
        return Teleport.Flat(view.ToDirection());
    }

    static Outcome FlipSelected()
    {
        if (CantFlip(Editor.Selected) is { } why)
        {
            return Outcome.Failed(why);
        }

        Moving.Flip(Editor.Selected!, FlipLift);
        return Outcome.Done("FLIPPED");
    }

    static Outcome HealSelected()
    {
        if (CantHeal(Editor.Selected) is { } why)
        {
            return Outcome.Failed(why);
        }

        var actor = Editor.Selected!;
        if (actor is RDestructibleProp prop)
        {
            return Rebuild(prop);
        }

        if (actor is not Pawn pawn)
        {
            return Outcome.Failed("NO HEALTH");
        }

        if (pawn.Health <= 0 || pawn is RPawnVillain { bGoodAsDead: true })
        {
            return Outcome.Failed(Things.IsPerson(pawn) ? "DOWN, USE REVIVE" : "ALREADY DESTROYED");
        }

        var full = pawn.HealthMax;
        if (pawn is RPawnCombat fighter && fighter.MaxHealth > full)
        {
            full = fighter.MaxHealth;
        }

        if (full <= 0 || pawn.Health >= full)
        {
            return Outcome.Unchanged("ALREADY FULL");
        }

        pawn.Health = full;
        return Outcome.Done(Things.IsPerson(pawn) ? "HEALED" : "REPAIRED");
    }

    // The game's own rebuild, the one a challenge restart gives every broken prop. It leaves the
    // health at nothing, so that comes from the prop's class.
    static Outcome Rebuild(RDestructibleProp prop)
    {
        if (prop.MeshComp is not { IsValid: true })
        {
            return Outcome.Failed("CAN'T REPAIR THAT");
        }

        var full = prop.ObjectArchetype is RDestructibleProp { IsValid: true } made ? made.Health : 0f;
        if (full > 0f && prop.Health >= full)
        {
            return Outcome.Unchanged("NOT BROKEN");
        }

        prop.ScriptReformToInitialState();
        if (full > 0f)
        {
            prop.Health = full;
        }

        return Outcome.Done("REPAIRED");
    }

    static Outcome DestroySelected()
    {
        if (CantBreak(Editor.Selected) is { } why)
        {
            return Outcome.Failed(why);
        }

        var actor = Editor.Selected!;
        if (World.Controller is not { } controller)
        {
            return Outcome.NoPlayer;
        }

        switch (actor)
        {
            case RBMPawnAI body:
                if (!Crowd.Standing(body))
                {
                    return Outcome.Unchanged("ALREADY DOWN");
                }

                if (body is RPawnVillain thug && (!thug.CanDebugKill() || Game.GetCheatManager().IsPawnImmuneToDebugKIll(thug)))
                {
                    return Outcome.Failed("CAN'T BE KNOCKED OUT");
                }

                Thaw(body);
                if (body.CurrWeapon is { IsValid: true })
                {
                    body.ForceDisarm();
                }

                body.TakeDamage(body.Health, controller, body.Location, Vector3.Zero, RDmgType_DebugKill.StaticClass());
                return Outcome.Done("KNOCKED OUT");

            // A parked or sleeping vehicle never plays its death and stays stuck, so only a
            // running one is destroyed, the way the game's own debug kill does it.
            case RVehicleNPC vehicle:
                if (vehicle.HasDied || vehicle.Health <= 0)
                {
                    return Outcome.Unchanged("ALREADY DESTROYED");
                }

                if (vehicle.CurrentBehaviour is not { IsValid: true })
                {
                    return Outcome.Failed("IT'S ASLEEP");
                }

                Thaw(vehicle);
                vehicle.TakeDamage(Lethal, null, vehicle.Location, Vector3.Zero, RDmgType_DebugKill.StaticClass());
                return Outcome.Done("DESTROYED");

            case RSentryGunBase gun:
                if (gun.bGoneRagdoll)
                {
                    return Outcome.Unchanged("ALREADY DESTROYED");
                }

                Thaw(gun);
                gun.Explode(true);
                if (!gun.bGoneRagdoll)
                {
                    gun.GoPhysicsAndTriggerEvent();
                }

                return Outcome.Done("DESTROYED");

            case RTurretBase turret:
                if (turret.bDisabled)
                {
                    return Outcome.Unchanged("ALREADY OFF");
                }

                turret.Disrupted();
                return Outcome.Done("SWITCHED OFF");

            // A prop with no mesh of its own crashes the game when first damaged, since the game
            // sets its mesh falling without checking it has one.
            case RDestructibleProp { MeshComp: not { IsValid: true } }:
                return Outcome.Failed("CAN'T BREAK THAT");

            // Plain damage on a destructible reads which piece was hit, and given none it crashes
            // the game. Radius damage picks the pieces itself, the way an explosion does.
            case ApexDestructibleActor breakable:
                breakable.TakeRadiusDamage(controller, Lethal, BreakRadius, RDmgType_Explosion.StaticClass(), 0f, breakable.Location, true, null!, 1f);
                return Outcome.Done("BROKEN");

            default:
                return Outcome.Failed("CAN'T BREAK THAT");
        }
    }

    static Outcome DisarmSelected()
    {
        if (NotPerson(Editor.Selected) is { } why)
        {
            return Outcome.Failed(why);
        }

        var body = (RBMPawnAI)Editor.Selected!;

        if (!Crowd.Standing(body) || body.CurrWeapon is not { IsValid: true })
        {
            return Outcome.Unchanged("NOT ARMED");
        }

        body.ForceDisarm();
        return Outcome.Done("DISARMED");
    }

    static Outcome ReviveSelected()
    {
        if (NotPerson(Editor.Selected) is { } why)
        {
            return Outcome.Failed(why);
        }

        var body = (RBMPawnAI)Editor.Selected!;

        if (body.bHidden || Crowd.Standing(body))
        {
            return Outcome.Unchanged("NOT DOWN");
        }

        Thaw(body);
        body.WakeFromDeadWithDefaults();
        return Outcome.Done("REVIVED");
    }
}
