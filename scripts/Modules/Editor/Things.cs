#nullable enable

using System.Numerics;
using System.Runtime.CompilerServices;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// What the editor can pick out in the world, what kind each is, what it's called and how big it
// is. Nothing here changes the game.
static class Things
{
    public enum Kind
    {
        Enemy,
        Friendly,
        Vehicle,
        Turret,
        Prop,
        Scenery,
        Pickup,
    }

    public static readonly string[] Labels = { "ENEMY", "FRIENDLY", "VEHICLE", "TURRET", "PROP", "SCENERY", "PICKUP" };
    public static readonly string[] Plurals = { "ENEMIES", "FRIENDLIES", "VEHICLES", "TURRETS", "PROPS", "MOVING SCENERY", "PICKUPS" };

    // A box with no sensible size gets this half size, and nothing measures bigger than the limit.
    const float DefaultHalf = 50f;
    const float Largest = 3000f;

    static readonly ConditionalWeakTable<GameObject, string> Names = new();
    static int _lastFound;

    public readonly record struct Found(Actor Actor, Kind Kind, float Distance);

    public static Kind? KindOf(Actor actor) => actor switch
    {
        RPawnCrowdRagdoll => null,
        RPawnVillain => Kind.Enemy,
        RBMPawnAI => Kind.Friendly,
        RPawnPlayer player when player.Controller is AIController => Kind.Friendly,
        RVehicleBatmobileBase => null,
        RVehicle or RAbandonedVehicle or RDeadVehicle => Kind.Vehicle,
        RSentryGunBase or RTurretBase => Kind.Turret,
        RBMCombatThrownObject or RPickupBaseBase or DroppedPickup => Kind.Pickup,
        InterpActor => Kind.Scenery,
        DynamicSMActor or ApexDestructibleActor => Kind.Prop,
        _ => null,
    };

    // Everything of the wanted kinds within range, nearest first. Null kind means all of them, and
    // wanted narrows it further. Each kind is asked about once, and a class is only looked through
    // when one of its kinds is wanted.
    public static List<Found> Near(Vector3 from, float range, Kind? only, Func<Kind, bool>? wanted = null)
    {
        Span<bool> wants = stackalloc bool[Labels.Length];
        for (var i = 0; i < wants.Length; i++)
        {
            var kind = (Kind)i;
            wants[i] = (only is null || only == kind) && (wanted?.Invoke(kind) ?? true);
        }

        // Sized by the last look, which is close to what this one finds.
        var found = new List<Found>(_lastFound + 16);
        var you = World.Player;
        var car = World.Driving;
        if (wants[(int)Kind.Enemy] || wants[(int)Kind.Friendly])
        {
            Take<RBMPawnAI>(found, wants, from, range, you, car);
            Take<RPawnPlayer>(found, wants, from, range, you, car);
        }

        if (wants[(int)Kind.Vehicle])
        {
            Take<RVehicle>(found, wants, from, range, you, car);
            Take<RAbandonedVehicle>(found, wants, from, range, you, car);
            Take<RDeadVehicle>(found, wants, from, range, you, car);
        }

        if (wants[(int)Kind.Turret])
        {
            Take<RSentryGunBase>(found, wants, from, range, you, car);
            Take<RTurretBase>(found, wants, from, range, you, car);
        }

        if (wants[(int)Kind.Prop] || wants[(int)Kind.Scenery])
        {
            Take<DynamicSMActor>(found, wants, from, range, you, car);
            Take<ApexDestructibleActor>(found, wants, from, range, you, car);
        }

        if (wants[(int)Kind.Pickup])
        {
            Take<RBMCombatThrownObject>(found, wants, from, range, you, car);
            Take<RPickupBaseBase>(found, wants, from, range, you, car);
            Take<DroppedPickup>(found, wants, from, range, you, car);
        }

        found.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        _lastFound = found.Count;
        return found;
    }

    // Most of a class is out of range, so that's checked first. Whose level it's in is the
    // dearest check, so it comes last.
    static void Take<T>(List<Found> found, ReadOnlySpan<bool> wants, Vector3 from, float range, Actor? you, Actor? car)
        where T : Actor
    {
        var reach = range * range;
        foreach (var actor in Game.FindObjects<T>())
        {
            if (!actor.IsValid)
            {
                continue;
            }

            var gap = Vector3.DistanceSquared(actor.Location, from);
            if (gap > reach || actor.bHidden || actor.bDeleteMe || actor.bPendingDelete || ReferenceEquals(actor, you) || ReferenceEquals(actor, car)
                || KindOf(actor) is not { } kind || !wants[(int)kind] || actor.Outer is not Level)
            {
                continue;
            }

            found.Add(new Found(actor, kind, MathF.Sqrt(gap)));
        }
    }

    public static bool Alive(Actor? actor) => actor is { IsValid: true, bDeleteMe: false, bPendingDelete: false };

    // Why it can't be moved, or null when it can. Pieces built into the level don't move even
    // when told to.
    public static string? Fixed(Actor actor)
    {
        if (actor.bStatic || !actor.bMovable)
        {
            return "PART OF THE LEVEL";
        }

        return null;
    }

    public static bool IsPerson(Actor actor) => actor is Pawn and not Vehicle;

    // A name comes from what the object is made from: a person's character, a vehicle's template,
    // a prop's mesh. So a body the game hands out again as someone else is named again, and each
    // name is only worked out once.
    public static string NameOf(Actor actor)
    {
        GameObject? from;
        try
        {
            from = NamedAfter(actor);
        }
        catch
        {
            from = null;
        }

        if (from is not null)
        {
            if (!Names.TryGetValue(from, out var known))
            {
                known = People.Pretty(Tidy(from.Name.ToString() ?? ""));
                Names.AddOrUpdate(from, known);
            }

            if (known.Length > 0)
            {
                return known;
            }
        }

        var type = actor.Class;
        if (!Names.TryGetValue(type, out var plain))
        {
            var text = type.Name.ToString() ?? "OBJECT";
            plain = People.Pretty(text.Length > 1 && text[0] == 'R' && char.IsUpper(text[1]) ? text[1..] : text);
            Names.AddOrUpdate(type, plain);
        }

        return plain;
    }

    static GameObject? NamedAfter(Actor actor) => actor switch
    {
        RBMPawnAI { CharacterDefine: { IsValid: true } define } => define,
        RVehicleNPC { ObjectArchetype: RVehicleNPC { IsValid: true } template } => template,
        DynamicSMActor { StaticMeshComponent.StaticMesh: { IsValid: true } mesh } => mesh,
        RBMCombatThrownObject { WeaponType: { IsValid: true } weapon } => weapon,
        RDestructibleProp prop => MadeFrom(prop),
        SkeletalMeshActor { SkeletalMeshComponent.SkeletalMesh: { IsValid: true } model } => model,
        _ => null,
    };

    // A breakable prop is whichever of the three kinds of model it was given.
    static GameObject? MadeFrom(RDestructibleProp prop)
    {
        var look = prop.InitialAppearance;
        return look.ApexMesh is { IsValid: true } apex ? apex
            : look.StaticMesh is { IsValid: true } still ? still
            : look.SkeletalMesh is { IsValid: true } skeletal ? skeletal
            : null;
    }

    // Weapon classes and breakable meshes carry a tag that says nothing to the player.
    static string Tidy(string name)
    {
        name = name.Replace("RBMWeapon", "", StringComparison.Ordinal);
        return name.EndsWith("_APEX", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
    }

    // A box turned with the object: its middle and the three half sides as world directions.
    public readonly record struct Shape(Vector3 Middle, Vector3 X, Vector3 Y, Vector3 Z)
    {
        // Half its size along the world's axes.
        public Vector3 Half => Vector3.Abs(X) + Vector3.Abs(Y) + Vector3.Abs(Z);
    }

    // The middle of the object and half its size along the world's axes.
    public static void Box(Actor actor, out Vector3 middle, out Vector3 half)
    {
        var shape = ShapeOf(actor);
        middle = shape.Middle;
        half = shape.Half;
    }

    // The box the model itself fills, turned and sized as it stands. People use their collision.
    // Models use their size at rest, since a vehicle's live bounds take in its physics bodies.
    public static Shape ShapeOf(Actor actor)
    {
        try
        {
            switch (actor)
            {
                case Pawn person when person is not Vehicle && person.CylinderComponent is { IsValid: true } body
                    && body.CollisionRadius > 1f && body.CollisionHeight > 1f:
                    return Body(person, body.CollisionRadius, body.CollisionHeight);
                case Vehicle vehicle when vehicle.Mesh is { IsValid: true } mesh && Model(mesh) is { } shape:
                    return shape;
                case SkeletalMeshActor holder when holder.SkeletalMeshComponent is { IsValid: true } mesh && Model(mesh) is { } shape:
                    return shape;
                case KAsset asset when asset.SkeletalMeshComponent is { IsValid: true } mesh && Model(mesh) is { } shape:
                    return shape;
                case DynamicSMActor dynamic when dynamic.StaticMeshComponent is { IsValid: true } mesh && Model(mesh) is { } shape:
                    return shape;

                // A breakable prop's model has no size at rest to read, so its box is the one the
                // game keeps round it, square to the world.
                case RDestructibleProp { MeshComp: { IsValid: true } whole } when Sane(whole.Bounds.BoxExtent):
                    var round = whole.Bounds;
                    return new Shape(round.Origin, Vector3.UnitX * round.BoxExtent.X, Vector3.UnitY * round.BoxExtent.Y, Vector3.UnitZ * round.BoxExtent.Z);
            }

            // Anything else goes by the biggest model it shows.
            Shape? best = null;
            foreach (var part in actor.Components)
            {
                if (part is MeshComponent { IsValid: true, bAttached: true, HiddenGame: false } mesh && Model(mesh) is { } shape
                    && (best is null || shape.Half.LengthSquared() > best.Value.Half.LengthSquared()))
                {
                    best = shape;
                }
            }

            if (best is { } found)
            {
                return found;
            }
        }
        catch
        {
            // Some parts can't be read on some objects. The default box stands in.
        }

        return new Shape(actor.Location, Vector3.UnitX * DefaultHalf, Vector3.UnitY * DefaultHalf, Vector3.UnitZ * DefaultHalf);
    }

    // A person's collision is a thin pole, so the box goes by the body as posed: the bounds the
    // game keeps round their limbs this frame. It turns with them and stays square, so it covers
    // them from any side, and the pole keeps a strange pose from making it huge.
    static Shape Body(Pawn person, float radius, float height)
    {
        var middle = person.Location;
        var wide = radius;
        var high = height;
        if (person.Mesh is { IsValid: true, HiddenGame: false } mesh)
        {
            var posed = mesh.Bounds;
            if (Sane(posed.BoxExtent) && Vector3.Distance(posed.Origin, middle) < height * 2f)
            {
                middle = posed.Origin;
                wide = Math.Clamp(MathF.Max(posed.BoxExtent.X, posed.BoxExtent.Y), radius, radius * 3f);
                high = Math.Clamp(posed.BoxExtent.Z, height * 0.5f, height * 1.5f);
            }
        }

        var ahead = Teleport.Flat(person.Rotation.ToDirection());
        var side = new Vector3(-ahead.Y, ahead.X, 0f);
        return new Shape(middle, ahead * wide, side * wide, Vector3.UnitZ * high);
    }

    static readonly Dictionary<nint, (Vector3 Middle, Vector3 Half)> MeshSizes = new();

    // The model's own box at rest, carried into the world by where the part is drawn.
    public static Shape? Model(MeshComponent part)
    {
        (Vector3 Middle, Vector3 Half)? rest = part switch
        {
            StaticMeshComponent { StaticMesh: { IsValid: true } mesh } => StaticSize(mesh),
            SkeletalMeshComponent { SkeletalMesh: { IsValid: true } mesh } => (mesh.Bounds.Origin, mesh.Bounds.BoxExtent),
            _ => null,
        };
        if (rest is not { } local || !Sane(local.Half))
        {
            return null;
        }

        var place = Unsafe.As<GameObject.FMatrix, Matrix4x4>(ref part.LocalToWorld);
        var middle = Vector3.Transform(local.Middle, place);
        var owner = part.Owner;
        if (!float.IsFinite(middle.X) || (owner is { IsValid: true } && Vector3.Distance(middle, owner.Location) > Largest * 2f))
        {
            return null;
        }

        var shape = new Shape(middle,
            new Vector3(place.M11, place.M12, place.M13) * local.Half.X,
            new Vector3(place.M21, place.M22, place.M23) * local.Half.Y,
            new Vector3(place.M31, place.M32, place.M33) * local.Half.Z);
        return Sane(shape.Half) ? shape : null;
    }

    // The game's own read of a static mesh's size, the one its winch and ice raft use. A mesh
    // never changes size, so each is read once.
    static (Vector3 Middle, Vector3 Half)? StaticSize(StaticMesh mesh)
    {
        if (MeshSizes.TryGetValue(mesh.Ptr, out var known))
        {
            return known;
        }

        if (World.Player is not { IsValid: true } batman)
        {
            return null;
        }

        var bounds = batman.GetStaticMeshBounds(mesh);
        if (MeshSizes.Count > 4096)
        {
            MeshSizes.Clear();
        }

        MeshSizes[mesh.Ptr] = (bounds.Origin, bounds.BoxExtent);
        return (bounds.Origin, bounds.BoxExtent);
    }

    static bool Sane(Vector3 half) =>
        half.X > 1f && half.Y > 1f && half.Z > 1f && half.X < Largest && half.Y < Largest && half.Z < Largest;

    // The part that carries its physics, for moving it without it snapping back.
    public static PrimitiveComponent? Body(Actor actor)
    {
        var part = actor switch
        {
            Vehicle vehicle => vehicle.Mesh as PrimitiveComponent,
            DynamicSMActor dynamic => dynamic.StaticMeshComponent,
            KAsset asset => asset.SkeletalMeshComponent,
            _ => actor.CollisionComponent,
        };
        return part is { IsValid: true } ? part : null;
    }
}
