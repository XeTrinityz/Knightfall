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

    static readonly Dictionary<nint, string> Names = new();

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
    // wanted narrows it further. A class is only looked through when one of its kinds is wanted.
    public static List<Found> Near(Vector3 from, float range, Kind? only, Func<Kind, bool>? wanted = null)
    {
        bool Wants(params Kind[] kinds) => kinds.Any(kind => (only is null || only == kind) && (wanted?.Invoke(kind) ?? true));

        var found = new List<Found>();
        var you = World.Player;
        var car = World.Driving;
        void Take<T>() where T : Actor
        {
            foreach (var actor in Game.FindObjects<T>())
            {
                if (!actor.IsValid || actor.bDeleteMe || actor.bPendingDelete || actor.bHidden || actor.Outer is not Level
                    || ReferenceEquals(actor, you) || ReferenceEquals(actor, car))
                {
                    continue;
                }

                var distance = Vector3.Distance(actor.Location, from);
                if (distance > range || KindOf(actor) is not { } kind || !Wants(kind))
                {
                    continue;
                }

                found.Add(new Found(actor, kind, distance));
            }
        }

        if (Wants(Kind.Enemy, Kind.Friendly))
        {
            Take<RBMPawnAI>();
            Take<RPawnPlayer>();
        }

        if (Wants(Kind.Vehicle))
        {
            Take<RVehicle>();
            Take<RAbandonedVehicle>();
            Take<RDeadVehicle>();
        }

        if (Wants(Kind.Turret))
        {
            Take<RSentryGunBase>();
            Take<RTurretBase>();
        }

        if (Wants(Kind.Prop, Kind.Scenery))
        {
            Take<DynamicSMActor>();
            Take<ApexDestructibleActor>();
        }

        if (Wants(Kind.Pickup))
        {
            Take<RBMCombatThrownObject>();
            Take<RPickupBaseBase>();
            Take<DroppedPickup>();
        }

        found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return found;
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

    public static string NameOf(Actor actor)
    {
        if (Names.TryGetValue(actor.Ptr, out var known))
        {
            return known;
        }

        string name;
        try
        {
            name = actor switch
            {
                RBMPawnAI { CharacterDefine: { IsValid: true } define } => People.Pretty(define.Name.ToString() ?? ""),
                RVehicleNPC { ObjectArchetype: RVehicleNPC { IsValid: true } template } => People.Pretty(template.Name.ToString() ?? ""),
                DynamicSMActor { StaticMeshComponent.StaticMesh: { IsValid: true } mesh } => People.Pretty(mesh.Name.ToString() ?? ""),
                RBMCombatThrownObject { WeaponType: { IsValid: true } weapon } => People.Pretty((weapon.Name.ToString() ?? "").Replace("RBMWeapon", "")),
                _ => "",
            };
        }
        catch
        {
            name = "";
        }

        if (name.Length == 0)
        {
            var type = actor.Class.Name.ToString() ?? "OBJECT";
            name = People.Pretty(type.Length > 1 && type[0] == 'R' && char.IsUpper(type[1]) ? type[1..] : type);
        }

        // Ids come back once an actor is gone, so the list is emptied now and then.
        if (Names.Count > 2048)
        {
            Names.Clear();
        }

        Names[actor.Ptr] = name;
        return name;
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
    static Shape? Model(MeshComponent part)
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
