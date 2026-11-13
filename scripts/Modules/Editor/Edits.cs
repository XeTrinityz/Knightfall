#nullable enable

using System.Diagnostics;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// What the editor changed on each object, kept from the first touch so RESTORE puts back exactly
// what the game had, and the last few changes one by one, for UNDO. A script reload forgets both
// but leaves the objects as they are.
static class Edits
{
    public sealed class Edit
    {
        public required Actor Actor;
        public required Vector3 At;
        public required Rotator Turn;
        public required Actor.EPhysics Physics;
        public required float DrawScale;
        public required bool TickOff;

        // How far the MOVE and TURN rows have taken it since it was first touched.
        public Vector3 Moved;
        public Vector3 Turned;

        // What the CHANGE rows have done to it.
        public float Size = 1f;
        public bool Frozen;
        public bool Removed;
        public bool Ghost;

        // Held still for FREEZE or REMOVE, and where. People are paused the way FREEZE ENEMIES does.
        public bool Still;
        public Vector3 StillAt;
        public object? Paused;

        // Taken out of sight, and whether the game had it hidden then.
        public bool Unseen;
        public bool Hidden;

        // Its collision switched off, and what it had then.
        public bool Unsolid;
        public bool Collide;
        public bool Block;
        public bool Ignore;

        // When a person was first seen hidden or shown against what the editor set.
        public float OddSince = -1f;
    }

    // How an object was just before one change. Fresh means nothing had been done to it yet.
    readonly record struct Step(
        Actor Actor, string What, bool Fresh, Vector3 At, Rotator Turn, Vector3 Moved, Vector3 Turned,
        float Size, bool Frozen, bool Removed, bool Ghost, long Noted);

    const int MostSteps = 40;

    // The same change again this soon, like a held key, is part of the same step.
    const double SameStep = 0.75;

    // A person hidden or shown against the editor for this long is back in the game's pool, where
    // they may come out again as somebody else.
    const float GoneAfter = 3f;

    static readonly Dictionary<nint, Edit> Journal = new();
    static readonly List<Step> History = new();

    public static Edit? Find(Actor actor) => Journal.TryGetValue(actor.Ptr, out var edit) && ReferenceEquals(edit.Actor, actor) ? edit : null;

    public static bool Changed(Actor actor) => Find(actor) is not null;

    // Whether there is anything for RESTORE EVERYTHING and for UNDO.
    public static bool Any => Journal.Count > 0;

    public static bool CanStepBack => History.Count > 0;

    // Every object the journal holds that's still in the world.
    public static IEnumerable<Edit> All => Journal.Values.Where(edit => Things.Alive(edit.Actor));

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
            TickOff = actor.bTickIsDisabled,
        };
        Journal[actor.Ptr] = edit;
        return edit;
    }

    // Called before every change the player makes, with the word UNDO shows for it. It notes how
    // the object is now, so UNDO can take that one change back.
    public static Edit Touch(Actor actor, string what)
    {
        var known = Find(actor);
        var now = Stopwatch.GetTimestamp();
        if (History.Count > 0 && History[^1] is var last && ReferenceEquals(last.Actor, actor) && last.What == what
            && Stopwatch.GetElapsedTime(last.Noted, now).TotalSeconds < SameStep)
        {
            History[^1] = last with { Noted = now };
            return known ?? Begin(actor);
        }

        if (History.Count >= MostSteps)
        {
            History.RemoveAt(0);
        }

        History.Add(new Step(actor, what, known is null, actor.Location, actor.Rotation, known?.Moved ?? default, known?.Turned ?? default,
            known?.Size ?? 1f, known?.Frozen ?? false, known?.Removed ?? false, known?.Ghost ?? false, now));
        return known ?? Begin(actor);
    }

    // Takes the newest change back. Steps on objects that are gone are skipped.
    public static Outcome StepBack()
    {
        while (History.Count > 0)
        {
            var step = History[^1];
            History.RemoveAt(History.Count - 1);
            var actor = step.Actor;
            if (!Things.Alive(actor))
            {
                continue;
            }

            if (step.Fresh)
            {
                // Nothing had been done to it before, so it goes back exactly as the game had it.
                if (Find(actor) is { } whole)
                {
                    Journal.Remove(actor.Ptr);
                    PutBack(whole);
                }
            }
            else
            {
                var edit = Begin(actor);
                (edit.Moved, edit.Turned) = (step.Moved, step.Turned);
                (edit.Size, edit.Frozen, edit.Removed, edit.Ghost) = (step.Size, step.Frozen, step.Removed, step.Ghost);
                Apply(edit);
                if (Things.Fixed(actor) is null)
                {
                    Put(actor, step.At, step.Turn);
                }
            }

            return Outcome.Done("UNDID " + step.What);
        }

        return Outcome.Unchanged("NOTHING TO UNDO");
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
    // with no collision is held still, since it would fall through the world otherwise. A piece
    // of the level never moves, so it isn't held. Sight and collision are only touched when the
    // editor's own switch changes, so what the game does to them in between is left alone.
    public static void Apply(Edit edit)
    {
        var actor = edit.Actor;
        if (!Things.Alive(actor))
        {
            return;
        }

        Hold(edit, Things.Fixed(actor) is null && (edit.Frozen || edit.Removed || (edit.Ghost && edit.Physics == Actor.EPhysics.PHYS_RigidBody)));
        if (edit.Removed != edit.Unseen)
        {
            if (edit.Removed)
            {
                edit.Hidden = actor.bHidden;
            }

            edit.Unseen = edit.Removed;
            actor.SetHidden(edit.Removed || edit.Hidden);
        }

        var unsolid = edit.Removed || edit.Ghost;
        if (unsolid != edit.Unsolid)
        {
            if (unsolid)
            {
                (edit.Collide, edit.Block, edit.Ignore) = (actor.bCollideActors, actor.bBlockActors, actor.bIgnoreEncroachers);
            }

            edit.Unsolid = unsolid;
            actor.SetCollision(!unsolid && edit.Collide, !unsolid && edit.Block, edit.Ignore);
        }

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

        // Noted first, so UNDO can bring the changes back.
        Touch(actor, "RESTORE");
        Journal.Remove(actor.Ptr);
        PutBack(edit);
        return Outcome.Done("RESTORED");
    }

    public static int RestoreAll()
    {
        var count = 0;
        foreach (var edit in Journal.Values.ToArray())
        {
            if (Things.Alive(edit.Actor))
            {
                PutBack(edit);
                count++;
            }
        }

        Journal.Clear();
        History.Clear();
        return count;
    }

    static void PutBack(Edit edit)
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
        if (Things.Fixed(actor) is not null)
        {
            return;
        }

        if (actor.Physics != edit.Physics)
        {
            actor.SetPhysics(edit.Physics, true);
        }

        Put(actor, edit.At, edit.Turn);
    }

    // Runs now and then. Objects that are gone are dropped from the journal, and so is a person
    // the game has taken back into its pool.
    public static void Prune()
    {
        foreach (var (key, edit) in Journal.ToArray())
        {
            var actor = edit.Actor;
            if (!Things.Alive(actor))
            {
                Journal.Remove(key);
                continue;
            }

            if (actor is not RBMPawnAI || actor.bHidden == edit.Unseen)
            {
                edit.OddSince = -1f;
            }
            else if (edit.OddSince < 0f || World.Now < edit.OddSince)
            {
                edit.OddSince = World.Now;
            }
            else if (World.Now - edit.OddSince >= GoneAfter)
            {
                Journal.Remove(key);
                Lose(edit);
            }
        }

        History.RemoveAll(static step => !Things.Alive(step.Actor));
    }

    // The body is the game's again. Only what would follow it into its next life is taken off.
    // Putting it back where it stood would bring it out of the pool as a ghost.
    static void Lose(Edit edit)
    {
        var actor = edit.Actor;
        History.RemoveAll(step => ReferenceEquals(step.Actor, actor));
        if (edit.Paused is { } paused)
        {
            Freezer.Unpause(paused);
        }

        if (MathF.Abs(actor.DrawScale - edit.DrawScale) > 0.0001f)
        {
            actor.SetDrawScale(edit.DrawScale);
        }

        // In the pool it has no collision anyway. Out of it again, it needs its own back.
        if (edit.Unsolid && !actor.bHidden)
        {
            actor.SetCollision(edit.Collide, edit.Block, edit.Ignore);
        }
    }

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
        History.Clear();
    }
}
