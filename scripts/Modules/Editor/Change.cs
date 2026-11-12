#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// EDITOR > CHANGE. The state rows show the selected object and change it. RESTORE undoes them.
// The action rows use the game's own damage, healing and disarming, so they can't be undone.
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
    };

    public static readonly Toggle Freeze = new(
        "editor.change.freeze", "FREEZE",
        "Holds the selected object still. People stop thinking and moving, vehicles and props hang where they are.",
        saved: false)
    {
        Preference = true,
    };

    public static readonly Toggle Remove = new(
        "editor.change.remove", "REMOVE",
        "Takes the selected object out of sight, with no collision, held still. Nothing is deleted, so missions can't break. SELECTED still lists it so it can come back.",
        saved: false)
    {
        Preference = true,
    };

    public static readonly Toggle Ghost = new(
        "editor.change.no_collision", "NO COLLISION",
        "You and everything else pass straight through the selected object. A physics object is held still, or it would fall through the world.",
        saved: false)
    {
        Preference = true,
    };

    public static readonly Command Launch = new(
        "editor.change.launch", "LAUNCH",
        "Throws the selected person, vehicle or physics object into the air.",
        LaunchSelected);

    public static readonly Command Flip = new(
        "editor.change.flip", "FLIP OVER",
        "Turns the selected vehicle or prop upside down.",
        FlipSelected);

    public static readonly Command Heal = new(
        "editor.change.heal", "HEAL OR REPAIR",
        "Gives the selected person or vehicle full health.",
        HealSelected);

    public static readonly Command Destroy = new(
        "editor.change.destroy", "DESTROY OR KNOCK OUT",
        "The game's own damage. Knocks people out, destroys vehicles and sentry guns, and switches turrets off like a disruptor. RESTORE can't undo it.",
        DestroySelected);

    public static readonly Command Disarm = new(
        "editor.change.disarm", "DISARM",
        "The selected person drops what they're holding.",
        DisarmSelected);

    public static readonly Command Revive = new(
        "editor.change.revive", "REVIVE",
        "The selected person gets back up, the way the game wakes people.",
        ReviveSelected);

    public static readonly Catalog.Section[] Sections =
    {
        new("STATE", Size, Freeze, Remove, Ghost),
        new("ACT", Launch, Flip, Heal, Destroy, Disarm, Revive),
        new("RESTORE", Editor.RestoreSelected),
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

    public static void Tick()
    {
        if (Size.Value != _size || Freeze.On != _frozen || Remove.On != _removed || Ghost.On != _ghost)
        {
            var actor = Editor.Selected;
            if (actor is null || Things.Fixed(actor) is not null)
            {
                Selected(actor);
            }
            else
            {
                var edit = Edits.Begin(actor);
                edit.Size = (float)Size.Value;
                edit.Frozen = Freeze.On;
                edit.Removed = Remove.On;
                edit.Ghost = Ghost.On;
                edit.Changed = true;
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

    static Outcome Selection(out Actor actor)
    {
        actor = null!;
        if (Editor.Selected is not { } selected)
        {
            return Outcome.Failed("NOTHING SELECTED");
        }

        actor = selected;
        return Outcome.Done();
    }

    // Moving it needs it loose: not part of the level, removed or frozen.
    static Outcome Loose(Actor actor)
    {
        if (Things.Fixed(actor) is { } why)
        {
            return Outcome.Failed(why);
        }

        return Edits.Find(actor) switch
        {
            { Removed: true } => Outcome.Failed("IT'S REMOVED"),
            { Frozen: true } => Outcome.Failed("IT'S FROZEN"),
            _ => Outcome.Done(),
        };
    }

    static Outcome LaunchSelected()
    {
        if (Selection(out var actor) is { Kind: OutcomeKind.Failed } none)
        {
            return none;
        }

        if (Loose(actor) is { Kind: OutcomeKind.Failed } stuck)
        {
            return stuck;
        }

        if (!Things.IsPerson(actor) && actor.Physics != Actor.EPhysics.PHYS_RigidBody)
        {
            return Outcome.Failed("IT DOESN'T MOVE FREELY");
        }

        var ahead = World.Controller is { } controller ? Ahead(controller) : Vector3.Zero;
        Edits.Begin(actor).Changed = true;
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
        if (Selection(out var actor) is { Kind: OutcomeKind.Failed } none)
        {
            return none;
        }

        if (Things.IsPerson(actor))
        {
            return Outcome.Failed("NOT PEOPLE");
        }

        if (Loose(actor) is { Kind: OutcomeKind.Failed } stuck)
        {
            return stuck;
        }

        var turn = actor.Rotation;
        Edits.Begin(actor).Changed = true;
        Edits.Put(actor, actor.Location + Vector3.UnitZ * FlipLift, new Rotator(turn.Pitch, turn.Yaw, turn.Roll + 180f));
        return Outcome.Done("FLIPPED");
    }

    static Outcome HealSelected()
    {
        if (Selection(out var actor) is { Kind: OutcomeKind.Failed } none)
        {
            return none;
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

    static Outcome DestroySelected()
    {
        if (Selection(out var actor) is { Kind: OutcomeKind.Failed } none)
        {
            return none;
        }

        if (World.Controller is not { } controller)
        {
            return Outcome.NoPlayer;
        }

        // Frozen things can't play out their fall, so they're let go first.
        if (Edits.Find(actor) is { Frozen: true } edit)
        {
            edit.Frozen = false;
            Edits.Apply(edit);
            Selected(actor);
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

                vehicle.TakeDamage(Lethal, null, vehicle.Location, Vector3.Zero, RDmgType_DebugKill.StaticClass());
                return Outcome.Done("DESTROYED");

            case RSentryGunBase gun:
                if (gun.bGoneRagdoll)
                {
                    return Outcome.Unchanged("ALREADY DESTROYED");
                }

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
        if (Selection(out var actor) is { Kind: OutcomeKind.Failed } none)
        {
            return none;
        }

        if (actor is not RBMPawnAI body)
        {
            return Outcome.Failed("NOT A PERSON");
        }

        if (!Crowd.Standing(body) || body.CurrWeapon is not { IsValid: true })
        {
            return Outcome.Unchanged("NOT ARMED");
        }

        body.ForceDisarm();
        return Outcome.Done("DISARMED");
    }

    static Outcome ReviveSelected()
    {
        if (Selection(out var actor) is { Kind: OutcomeKind.Failed } none)
        {
            return none;
        }

        if (actor is not RBMPawnAI body)
        {
            return Outcome.Failed("NOT A PERSON");
        }

        if (body.bHidden || Crowd.Standing(body))
        {
            return Outcome.Unchanged("NOT DOWN");
        }

        body.WakeFromDeadWithDefaults();
        return Outcome.Done("REVIVED");
    }
}
