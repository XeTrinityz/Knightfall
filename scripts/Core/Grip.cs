#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// One physics handle on a pawn. It takes hold of a physics body, or of one bone of a ragdoll, and
// pulls it toward the point it is given each tick. The handle is ours, and comes off again.
sealed class Grip
{
    // The engine's own spring is far too soft to carry a ragdoll.
    const float Stiffness = 8000f;
    const float Damping = 400f;

    RB_Handle? _handle;
    Actor? _owner;
    int _epoch;

    public bool Holding => _epoch == World.Epoch && _owner is { IsAlive: true } && _handle is { IsValid: true } handle && handle.GrabbedComponent is { IsValid: true };

    // Takes hold of the part by the bone, or the whole of it with no bone, at the spot given.
    public bool Take(Actor owner, PrimitiveComponent part, FName bone, Vector3 spot)
    {
        var handle = Handle(owner);
        if (handle.GrabbedComponent is { IsValid: true })
        {
            handle.ReleaseComponent();
        }

        part.WakeRigidBody(bone);
        handle.GrabComponent(part, bone, spot, false);
        return Holding;
    }

    public void Pull(Vector3 point)
    {
        if (Holding)
        {
            _handle!.SetLocation(point);
        }
    }

    // Lets go and takes the handle off its pawn. A new world freed both, so then nothing is
    // called on them.
    public void Remove()
    {
        if (_epoch == World.Epoch && _handle is { IsValid: true } handle)
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

    RB_Handle Handle(Actor owner)
    {
        if (_epoch == World.Epoch && _handle is { IsValid: true } kept && ReferenceEquals(_owner, owner))
        {
            return kept;
        }

        Remove();
        var handle = Made.Tracked(new RB_Handle(owner));
        owner.AttachComponent(handle);
        handle.LinearStiffness = Stiffness;
        handle.LinearDamping = Damping;
        _handle = handle;
        _owner = owner;
        _epoch = World.Epoch;
        return handle;
    }
}

// Keeps a ragdolled person down while they are carried. The game counts a ragdoll's stun time down
// and stands the person up at zero, or sooner once the body has been still for a second, and a
// carried body is held still. So the time is topped up each tick and the stillness rule is off.
sealed class Limp
{
    // The stun time a carried person is given, again every tick.
    const float KeepDown = 5f;

    // The game's own cut grab tool turns ragdoll calming off while it holds a body.
    static readonly Held<SkeletalMeshComponent, bool> NoCalming = new("carried calming",
        mesh => mesh.bDebugDisableRagdollCalmingMeasures, (mesh, off) => mesh.bDebugDisableRagdollCalmingMeasures = off);

    static readonly Held<RRagdollProcessor, bool> NoFloorGetUp = new("carried floor get-up",
        ragdoll => ragdoll.bGetUpWhenHitFloor, (ragdoll, on) => ragdoll.bGetUpWhenHitFloor = on);

    SkeletalMeshComponent? _mesh;
    RRagdollProcessor? _ragdoll;
    float _stunWas;
    int _epoch;

    // Calming off for a body about to be pulled. This much is all a loose body that is no person
    // needs, every tick it is carried.
    public void Loosen(SkeletalMeshComponent mesh)
    {
        Renew();
        NoCalming.Hold(mesh, true);
        if (!ReferenceEquals(_mesh, mesh))
        {
            _mesh = mesh;
            mesh.ResetRagdollCalmingMeasures();
        }
    }

    // Every tick while carried. The ragdoll's own state only starts a tick or two after the
    // knock-down, so its part waits for that.
    public void Keep(RPawnVillain person)
    {
        Renew();
        if (person.Mesh is { IsValid: true } mesh)
        {
            Loosen(mesh);
        }

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

    // A new world freed what was kept, so it is only forgotten.
    void Renew()
    {
        if (_epoch != World.Epoch)
        {
            _mesh = null;
            _ragdoll = null;
            _epoch = World.Epoch;
        }
    }

    // Let go. They keep at least the stun time given, or what they had when picked up if that
    // was longer, and the game stands them up from there in its own way.
    public void Free(float left)
    {
        if (_epoch == World.Epoch)
        {
            if (_mesh is { } mesh)
            {
                NoCalming.Release(mesh);
            }

            if (_ragdoll is { } ragdoll)
            {
                NoFloorGetUp.Release(ragdoll);
                if (ragdoll.IsValid && ragdoll.CurrState == RRagdollProcessor.RagdollState.RAG_Ragdoll)
                {
                    ragdoll.StunTime = MathF.Max(_stunWas, left);
                }
            }
        }

        _mesh = null;
        _ragdoll = null;
    }
}
