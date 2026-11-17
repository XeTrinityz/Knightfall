#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// PLAYER > GADGETS > GRAVITY GRAB. While the batclaw or grapple gun is aimed, the fire button picks
// up what the crosshair is on with a physics handle instead of firing. Fire again to throw it.
// Letting go of the aim drops it. People are knocked into a ragdoll first and held by the pelvis.
// The editor's GRAB carries through the same handle.
static class GravityGrab
{
    const float Metre = Teleport.UnitsPerMetre;
    const float Reach = 3000f;
    const float RagdollWait = 1.5f;

    const float PressTwice = 0.1f;

    // A swing slower than this on letting go is a drop, not a throw.
    const float ThrowPace = 300f;
    const float HangPerson = 120f;
    const float HangThing = 20f;
    const float Stiffness = 8000f;
    const float Damping = 400f;

    // How fast the hold point travels from where something was grabbed to where it is held, so a
    // far pull arrives at a speed the body can take instead of being snapped across the map.
    const float PullSpeed = 2000f;

    // The stun time a person is left with when let go or thrown, so the body is clear of the
    // hand, or has landed, before the game stands it up.
    const float LeftAfterDrop = 1f;
    const float LeftAfterThrow = 2.5f;

    // A held person's ragdoll is given this long to run, again every tick.
    const float KeepDown = 5f;

    static readonly FName Pelvis = new("Bip01_Pelvis");

    // The game's own cut grab tool turns ragdoll calming off while it holds a body.
    static readonly Held<SkeletalMeshComponent, bool> NoCalming = new("gravity grab calming",
        mesh => mesh.bDebugDisableRagdollCalmingMeasures, (mesh, off) => mesh.bDebugDisableRagdollCalmingMeasures = off);

    // A ragdoll ends early once the body has been still for a second, and a body in the grip is
    // held still.
    static readonly Held<RRagdollProcessor, bool> NoFloorGetUp = new("gravity grab floor get-up",
        ragdoll => ragdoll.bGetUpWhenHitFloor, (ragdoll, on) => ragdoll.bGetUpWhenHitFloor = on);

    static Vector3 _target;
    static RRagdollProcessor? _ragdoll;
    static float _stunWas;
    static float _pressedAt = float.NegativeInfinity;
    static bool _pressTaken;

    static RB_Handle? _handle;
    static RPawnPlayer? _owner;
    static int _epoch;

    static Actor? _held;

    // The editor is carrying, and sets the hold point itself each tick.
    static bool _byEditor;
    static PrimitiveComponent? _part;
    static FName _bone = FName.None;
    static RPawnVillain? _waiting;
    static float _waitUntil;

    public static readonly Toggle Grab = new(
        "player.gadgets.gravity_grab", "GRAVITY GRAB",
        "While you aim the batclaw, fire picks up what you're aiming at and holds it in front of you instead of firing it. Fire again to throw it. Let go of the aim to drop it. People are knocked down first.")
    {
        Block = static () => World.Player is null ? "NO PLAYER" : Villains.On ? "NOT AS A VILLAIN" : World.Driving is not null ? "NOT IN THE BATMOBILE" : null,
        Tick = Tick,
        Restore = Release,
    };

    public static readonly Slider Distance = new(
        "player.gadgets.gravity_distance", "HOLD DISTANCE",
        "How far in front of you the held object floats.",
        min: 2, max: 20, step: 1, value: 6, unit: " M");

    public static readonly Slider Power = new(
        "player.gadgets.gravity_power", "THROW POWER",
        "How fast a thrown object leaves your hand.",
        min: 5, max: 80, step: 5, value: 30, unit: " M/S");

    // The batclaw's fire press goes through GadgetPressed. FireGrapple is the grapple's own path.
    [Redirect(typeof(RPlayerController), nameof(RPlayerController.GadgetPressed))]
    static void GadgetPressed(RPlayerController self)
    {
        if (!Fire(self))
        {
            Redirects.RunOriginal();
        }
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.FireGrapple))]
    static void FireGrapple(RPlayerController self)
    {
        if (!Fire(self))
        {
            Redirects.RunOriginal();
        }
    }

    // True when the press was ours, so the game's own press doesn't run.
    static bool Fire(RPlayerController self)
    {
        try
        {
            if (Grab.On && Aiming() && Press())
            {
                // What the game's own fire does first, so the aim camera isn't left waiting for a shot.
                self.GrappleAimTime = -1f;
                return true;
            }
        }
        catch (Exception error)
        {
            Log.Error("gravity_grab.fire", "Gravity grab failed on the fire button", error);
        }

        return false;
    }

    // The batclaw and the grapple gun, while the player is aiming one of them.
    static bool Aiming()
    {
        if (World.Player is not { } pawn || pawn.InvManager is not RInventoryManager inventory || inventory.CurrentGadget is not { IsValid: true } gadget)
        {
            return false;
        }

        var grapple = gadget is RHarpoonGun || gadget is RGrappleGun;
        return grapple && gadget.GetAiming();
    }

    // True when the press was ours, so the batclaw doesn't fire. The game can send one press down
    // both paths (GadgetPressed and FireGrapple), which get the same answer, since a second
    // pick-up would throw what was just picked up.
    static bool Press()
    {
        if (World.Player is null || Villains.On || World.Driving is not null || _byEditor)
        {
            return false;
        }

        if (MathF.Abs(World.Now - _pressedAt) < PressTwice)
        {
            return _pressTaken;
        }

        _pressedAt = World.Now;
        _pressTaken = Taken();
        return _pressTaken;
    }

    // Throws what's held, or picks up what the crosshair is on. With nothing to pick up the press
    // is left to the batclaw, which has puzzles of its own to use.
    static bool Taken()
    {
        if (_held is not null)
        {
            Throw();
            return true;
        }

        if (_waiting is not null)
        {
            return true;
        }

        var target = Crosshair.Pick(
            Reach,
            wanted: static kind => kind is Things.Kind.Enemy or Things.Kind.Friendly or Things.Kind.Prop or Things.Kind.Pickup,
            accept: Grabbable);
        if (target is null)
        {
            return false;
        }

        Begin(target);
        return true;
    }

    // Starts holding. A person is knocked down first and held once they are down.
    static void Begin(Actor target)
    {
        if (target is RPawnVillain person && !person.IsRagdoll())
        {
            person.GenericGoRagdoll();
            _waiting = person;
            _waitUntil = World.Now + RagdollWait;
            return;
        }

        Hold(target);
    }

    // Whether the grab can take it: a person who's alive, or anything with a physics body.
    // The batclaw's own puzzle pieces stay with the batclaw.
    public static bool Grabbable(Actor actor) =>
        actor is not (RHarpoonDragBase or RBatclawPipeCoverBase or RHarpoonTriggerBase)
        && (actor is RPawnVillain { IsAlive: true, Health: > 0 } || (Things.Body(actor) is { } part && part.Owner is { Physics: Actor.EPhysics.PHYS_RigidBody }));

    static void Tick()
    {
        if (World.Paused || _byEditor)
        {
            return;
        }

        if (!Service(Aiming() ? null : "the aim ended") || _held is null || !Crosshair.View(out var eye, out var way))
        {
            return;
        }

        Drive(eye + way * (float)Distance.Value * Metre);
    }

    // What the grab does for a held thing each tick, whoever sets the hold point. False when
    // nothing is held any more.
    static bool Service(string? stop)
    {
        if (_epoch != World.Epoch)
        {
            Forget();
        }

        if (_waiting is { } waiting)
        {
            if (!waiting.IsAlive)
            {
                _waiting = null;
            }
            else if (waiting.IsRagdoll())
            {
                _waiting = null;
                Hold(waiting);
            }
            else if (World.Now > _waitUntil)
            {
                _waiting = null;
                Toasts.Show(Grab.Plain, "COULDN'T KNOCK IT DOWN", OutcomeKind.Failed, Grab);
            }
        }

        if (_held is null)
        {
            return false;
        }

        var lost = !Things.Alive(_held) || World.Player is null || _owner is not { IsAlive: true } || _handle is not { IsValid: true } handle || handle.GrabbedComponent is not { IsValid: true }
            || stop is not null || _held is RPawnVillain { Health: <= 0 } || (_held is RPawnVillain up && !up.IsRagdoll());
        if (lost)
        {
            Drop();
            return false;
        }

        return true;
    }

    // Pulls what's held toward the point. Looking down must not put the point in the floor, a
    // person hangs well below it. A far pull travels there at a speed the body can take.
    static void Drive(Vector3 point)
    {
        var floor = World.Player!.Location.Z + (_held is RPawnVillain ? HangPerson : HangThing);
        point = point with { Z = MathF.Max(point.Z, floor) };
        var toGo = point - _target;
        var reach = PullSpeed * World.Delta;
        _target = toGo.Length() <= reach ? point : _target + Vector3.Normalize(toGo) * reach;
        _handle!.SetLocation(_target);
        if (_part is SkeletalMeshComponent held)
        {
            NoCalming.Hold(held, true);
        }

        if (_held is RPawnVillain person)
        {
            KeepLimp(person);
        }

        _part?.WakeRigidBody(_bone);
    }

    // True while something is held or being knocked down to be held.
    public static bool Active => _held is not null || _waiting is not null;

    // The editor takes hold of something and carries it to the point it gives each tick.
    public static bool Start(Actor target)
    {
        if (World.Player is null || !Grabbable(target) || Active)
        {
            return false;
        }

        _byEditor = true;
        Begin(target);
        if (!Active)
        {
            _byEditor = false;
            return false;
        }

        return true;
    }

    // One tick of the editor's carry. False once nothing is held, because it got up, died or
    // couldn't be knocked down.
    public static bool Carry(Vector3 point)
    {
        if (Service(null) && _held is not null)
        {
            Drive(point);
        }

        if (!Active)
        {
            _byEditor = false;
        }

        return Active;
    }

    // The editor lets go. A velocity above a walking pace is a throw.
    public static void Release(Vector3 velocity)
    {
        if (_epoch != World.Epoch)
        {
            Forget();
        }

        _waiting = null;
        _byEditor = false;
        if (_held is not null)
        {
            var part = _part;
            var bone = _bone;
            var thrown = velocity.Length() > ThrowPace;
            Drop(thrown ? LeftAfterThrow : LeftAfterDrop);
            if (thrown && part is { IsValid: true })
            {
                part.WakeRigidBody(bone);
                part.SetRBLinearVelocity(velocity, false);
            }
        }

        // The editor's carry needs no handle between grabs, and the row may be off.
        Detach();
    }

    // The game counts a ragdoll's stun time down and stands the person up at zero, or sooner once
    // the body is still. Held, the time is topped up and the stillness rule is off. The ragdoll's
    // own state only starts a tick or two after the knock-down, so this waits for it.
    static void KeepLimp(RPawnVillain person)
    {
        if (person.RagdollProcessor is not { IsValid: true } ragdoll || ragdoll.CurrState != RRagdollProcessor.RagdollState.RAG_Ragdoll)
        {
            return;
        }

        if (!ReferenceEquals(_ragdoll, ragdoll))
        {
            _ragdoll = ragdoll;
            _stunWas = ragdoll.StunTime;
        }

        NoFloorGetUp.Hold(ragdoll, false);
        ragdoll.StunTime = KeepDown;
    }

    static void Hold(Actor target)
    {
        if (World.Player is not { } player)
        {
            return;
        }

        var person = target as RPawnVillain;
        PrimitiveComponent? part = person is not null ? person.Mesh : Things.Body(target);
        if (part is not { IsValid: true })
        {
            Toasts.Show(Grab.Plain, "NOTHING TO GRAB", OutcomeKind.Failed, Grab);
            return;
        }

        var handle = Handle(player);
        var bone = person is not null ? Pelvis : FName.None;
        var spot = person is not null && part is SkeletalMeshComponent mesh ? mesh.GetBoneLocation(bone, 0) : target.Location;
        Loosen(part);
        part.WakeRigidBody(bone);
        handle.GrabComponent(part, bone, spot, false);
        if (handle.GrabbedComponent is not { IsValid: true })
        {
            NoCalming.Release();
            Toasts.Show(Grab.Plain, "IT WON'T BUDGE", OutcomeKind.Failed, Grab);
            return;
        }

        _held = target;
        _part = part;
        _bone = bone;
        _target = spot;
        _epoch = World.Epoch;
    }

    // Calming off for a body about to be pulled, as the game's grab tool does.
    static void Loosen(PrimitiveComponent part)
    {
        if (part is not SkeletalMeshComponent mesh)
        {
            return;
        }

        NoCalming.Hold(mesh, true);
        mesh.ResetRagdollCalmingMeasures();
    }

    // One handle on the player, kept while the row is on.
    static RB_Handle Handle(RPawnPlayer player)
    {
        if (_handle is { IsValid: true } && ReferenceEquals(_owner, player) && _epoch == World.Epoch)
        {
            return _handle;
        }

        Detach();
        _handle = Made.Tracked(new RB_Handle(player));
        player.AttachComponent(_handle);
        _handle.LinearStiffness = Stiffness;
        _handle.LinearDamping = Damping;
        _owner = player;
        _epoch = World.Epoch;
        return _handle;
    }

    static void Throw()
    {
        var part = _part;
        var bone = _bone;
        if (!Crosshair.View(out _, out var way) || part is not { IsValid: true })
        {
            Drop();
            return;
        }

        Drop(LeftAfterThrow);
        part.WakeRigidBody(bone);
        part.SetRBLinearVelocity(way * (float)Power.Value * Metre, false);
    }

    // Lets go. A person keeps at least the stun time given, or what they had when picked up if
    // that was longer, and the game stands them up from there in its own way.
    static void Drop(float left = LeftAfterDrop)
    {
        if (_handle is { IsValid: true } handle && handle.GrabbedComponent is { IsValid: true })
        {
            handle.ReleaseComponent();
        }

        NoCalming.Release();
        NoFloorGetUp.Release();
        if (_ragdoll is { IsValid: true } ragdoll && ragdoll.CurrState == RRagdollProcessor.RagdollState.RAG_Ragdoll)
        {
            ragdoll.StunTime = MathF.Max(_stunWas, left);
        }

        _ragdoll = null;
        _held = null;
        _part = null;
        _bone = FName.None;
    }

    static void Detach()
    {
        if (_handle is { IsValid: true } handle)
        {
            if (handle.GrabbedComponent is { IsValid: true })
            {
                handle.ReleaseComponent();
            }

            if (_owner is { IsAlive: true } owner)
            {
                owner.DetachComponent(handle);
            }
        }

        _handle = null;
        _owner = null;
    }

    // A new world freed everything, so nothing is called on the old objects.
    static void Forget()
    {
        _handle = null;
        _owner = null;
        _held = null;
        _part = null;
        _ragdoll = null;
        _waiting = null;
        _byEditor = false;
        _pressedAt = float.NegativeInfinity;
        _bone = FName.None;
        _epoch = World.Epoch;
    }

    // The row is off, or everything is being put back. The editor's carry isn't the row's, it lets
    // go through Release(Vector3).
    static void Release()
    {
        if (_epoch != World.Epoch)
        {
            Forget();
        }

        if (_byEditor)
        {
            return;
        }

        _waiting = null;
        Drop();
        Detach();
    }
}
