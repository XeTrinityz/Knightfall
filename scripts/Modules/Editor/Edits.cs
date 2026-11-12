#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// What the editor changed on each object, kept from the first touch so RESTORE puts back exactly
// what the game had. A script reload forgets the journal but leaves the objects as they are.
static class Edits
{
    public sealed class Edit
    {
        public required Actor Actor;
        public required Vector3 At;
        public required Rotator Turn;
        public required Actor.EPhysics Physics;
        public required float DrawScale;
        public required bool Hidden;
        public required bool Collide;
        public required bool Block;
        public required bool Ignore;
        public required bool TickOff;

        // How far the MOVE and TURN rows have taken it since it was first touched.
        public Vector3 Moved;
        public Vector3 Turned;
        public bool Changed;

        // What the CHANGE rows have done to it.
        public float Size = 1f;
        public bool Frozen;
        public bool Removed;
        public bool Ghost;

        // Held still for FREEZE or REMOVE, and where. People are paused the way FREEZE ENEMIES does.
        public bool Still;
        public Vector3 StillAt;
        public object? Paused;
    }

    static readonly Dictionary<nint, Edit> Journal = new();

    public static int Count => Journal.Values.Count(edit => edit.Changed && Things.Alive(edit.Actor));

    public static Edit? Find(Actor actor) => Journal.TryGetValue(actor.Ptr, out var edit) && ReferenceEquals(edit.Actor, actor) ? edit : null;

    public static bool Changed(Actor actor) => Find(actor) is { Changed: true };

    // The first touch notes how it was.
    public static Edit Begin(Actor actor)
    {
        if (Find(actor) is { } known)
        {
            return known;
        }

        var edit = new Edit
        {
            Actor = actor,
            At = actor.Location,
            Turn = actor.Rotation,
            Physics = actor.Physics,
            DrawScale = actor.DrawScale,
            Hidden = actor.bHidden,
            Collide = actor.bCollideActors,
            Block = actor.bBlockActors,
            Ignore = actor.bIgnoreEncroachers,
            TickOff = actor.bTickIsDisabled,
        };
        Journal[actor.Ptr] = edit;
        return edit;
    }

    // Moves it and turns it, physics body and all, so it doesn't snap back. A spot it would overlap
    // is refused by the game, so it goes with its collision off for the move.
    public static void Put(Actor actor, Vector3 at, Rotator turn)
    {
        actor.SetRotation(turn);
        if (!actor.SetLocation(at))
        {
            var (collide, block, ignore) = (actor.bCollideActors, actor.bBlockActors, actor.bIgnoreEncroachers);
            actor.SetCollision(false, false, ignore);
            actor.SetLocation(at);
            actor.SetCollision(collide, block, ignore);
        }

        actor.Velocity = Vector3.Zero;
        if (actor.Physics == Actor.EPhysics.PHYS_RigidBody && Things.Body(actor) is { } body)
        {
            body.SetRBPosition(at);
            body.SetRBRotation(turn);
            body.SetRBLinearVelocity(Vector3.Zero, false);
            body.SetRBAngularVelocity(Vector3.Zero, false);
            body.WakeRigidBody();
        }

        if (Find(actor) is { Still: true } edit)
        {
            edit.StillAt = at;
        }
    }

    // Puts the CHANGE rows' state on the object. Anything removed, frozen, or a physics object
    // with no collision is held still, since it would fall through the world otherwise.
    public static void Apply(Edit edit)
    {
        var actor = edit.Actor;
        if (!Things.Alive(actor))
        {
            return;
        }

        Hold(edit, edit.Frozen || edit.Removed || (edit.Ghost && edit.Physics == Actor.EPhysics.PHYS_RigidBody));
        if (actor.bHidden != (edit.Removed || edit.Hidden))
        {
            actor.SetHidden(edit.Removed || edit.Hidden);
        }

        var solid = !edit.Removed && !edit.Ghost;
        actor.SetCollision(solid && edit.Collide, solid && edit.Block, edit.Ignore);
        var scale = edit.DrawScale * edit.Size;
        if (MathF.Abs(actor.DrawScale - scale) > 0.0001f)
        {
            actor.SetDrawScale(scale);
        }
    }

    static void Hold(Edit edit, bool still)
    {
        if (still == edit.Still)
        {
            return;
        }

        var actor = edit.Actor;
        edit.Still = still;
        if (still)
        {
            edit.StillAt = actor.Location;
            if (actor is RBMPawnAI body)
            {
                edit.Paused = Freezer.Pause(body);
            }
            else
            {
                actor.SetTickIsDisabled(true);
            }

            if (actor.Physics == Actor.EPhysics.PHYS_RigidBody)
            {
                actor.SetPhysics(Actor.EPhysics.PHYS_None);
            }

            actor.Velocity = Vector3.Zero;
            return;
        }

        if (edit.Paused is { } paused)
        {
            Freezer.Unpause(paused);
            edit.Paused = null;
        }
        else if (actor.bTickIsDisabled != edit.TickOff)
        {
            actor.SetTickIsDisabled(edit.TickOff);
        }

        if (actor.Physics != edit.Physics)
        {
            actor.SetPhysics(edit.Physics, true);
            Things.Body(actor)?.WakeRigidBody();
        }
    }

    // Runs every tick. Something in this game still carries a paused person along, so anyone held
    // still is put back where they were held, unless the editor is carrying them.
    public static void Keep(Actor? carried)
    {
        foreach (var edit in Journal.Values)
        {
            if (edit.Still && edit.Paused is not null && Things.Alive(edit.Actor) && !ReferenceEquals(edit.Actor, carried)
                && Vector3.DistanceSquared(edit.Actor.Location, edit.StillAt) > 16f)
            {
                edit.Actor.SetLocation(edit.StillAt);
                edit.Actor.Velocity = Vector3.Zero;
            }
        }
    }

    // Sets it going, for a throw. People fall, physics objects tumble, the rest stays put.
    public static void Throw(Actor actor, Vector3 velocity)
    {
        if (Things.IsPerson(actor))
        {
            actor.SetPhysics(Actor.EPhysics.PHYS_Falling, true);
            actor.Velocity = velocity;
        }
        else if (actor.Physics == Actor.EPhysics.PHYS_RigidBody && Things.Body(actor) is { } body)
        {
            body.WakeRigidBody();
            body.SetRBLinearVelocity(velocity, false);
        }
    }

    public static Outcome Restore(Actor actor)
    {
        if (Find(actor) is not { } edit)
        {
            return Outcome.Unchanged("NOTHING CHANGED");
        }

        Journal.Remove(actor.Ptr);
        Undo(edit);
        return Outcome.Done("RESTORED");
    }

    public static int RestoreAll()
    {
        var count = 0;
        foreach (var edit in Journal.Values.ToArray())
        {
            if (Things.Alive(edit.Actor) && edit.Changed)
            {
                Undo(edit);
                count++;
            }
        }

        Journal.Clear();
        return count;
    }

    static void Undo(Edit edit)
    {
        var actor = edit.Actor;
        if (!Things.Alive(actor))
        {
            return;
        }

        edit.Size = 1f;
        edit.Frozen = false;
        edit.Removed = false;
        edit.Ghost = false;
        Apply(edit);
        if (actor.Physics != edit.Physics)
        {
            actor.SetPhysics(edit.Physics, true);
        }

        Put(actor, edit.At, edit.Turn);
    }

    // Objects that are gone are dropped from the journal.
    public static void Prune()
    {
        foreach (var (key, edit) in Journal.ToArray())
        {
            if (!Things.Alive(edit.Actor))
            {
                Journal.Remove(key);
            }
        }
    }

    // Every object the journal holds that's still in the world.
    public static IEnumerable<Edit> All => Journal.Values.Where(edit => Things.Alive(edit.Actor));

    // A script reload loses the journal, so what CHANGE did is put back first. Moved objects
    // stay where they are, like spawns do.
    public static void Release()
    {
        foreach (var edit in Journal.Values)
        {
            edit.Size = 1f;
            edit.Frozen = false;
            edit.Removed = false;
            edit.Ghost = false;
            Apply(edit);
        }

        Journal.Clear();
    }
}
