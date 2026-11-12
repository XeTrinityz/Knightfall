#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// SPAWNER > OBJECTS. Copies of any mesh in the world or in memory. Physics objects are built the
// way the game knocks a roof rack off a car, still ones like our own sky copies.
static class Objects
{
    const float Metre = Teleport.UnitsPerMetre;

    const float NearbyRange = 100f * Metre;
    const int NearbyMost = 150;
    const int LoadedMost = 500;

    // Specks too small to pick out, and whole blocks of city.
    const float Smallest = 5f;
    const float Biggest = 50f * Metre;

    // Room kept around each copy is capped, so one huge pick doesn't scatter the rest.
    const float MostRoom = 20f * Metre;

    // A new copy waits this high, hidden, until its size is known.
    const float Lift = 100f;

    // A physics copy that drops this far in its first seconds has no physics shape.
    const float FellTooFar = 25f * Metre;
    const float SettleTime = 4f;

    // How many layers the crosshair looks through, past you, people and invisible volumes.
    const float Reach = 20000f;
    const int Layers = 8;
    const float StepPast = 60f;

    // How far outside a piece's box the crosshair spot may be and still count as on it.
    const float Snug = 10f;

    static readonly (string Id, string Label, string[] Words)[] Kinds =
    {
        ("any", "ANYTHING", Array.Empty<string>()),
        ("boxes", "BOXES AND CRATES", new[] { "crate", "box", "pallet", "case", "container", "carton" }),
        ("bins", "BARRELS AND BINS", new[] { "barrel", "drum", "bin", "dumpster", "trash", "garbage" }),
        ("cars", "CARS AND WRECKS", new[] { "car", "van", "truck", "bus", "taxi", "wreck", "vehicle" }),
        ("street", "STREET THINGS", new[] { "lamp", "light", "bench", "hydrant", "cone", "sign", "bollard", "post", "meter", "mailbox", "phone", "kiosk" }),
        ("barriers", "BARRIERS AND FENCES", new[] { "barrier", "barricade", "fence", "rail", "gate", "jersey" }),
        ("plants", "PLANTS", new[] { "tree", "plant", "bush", "shrub", "planter", "flower" }),
        ("furniture", "FURNITURE", new[] { "chair", "table", "desk", "sofa", "couch", "shelf", "cabinet", "bed", "stool" }),
    };

    // Metres is how far away a NEARBY one was when listed, shown only in the list.
    sealed record Recipe(string Key, string Label, StaticMesh Mesh, MaterialInterface?[] Materials, Vector3 Scale, Rotator Tilt, float Radius, float Metres = -1f);

    static List<Recipe> _recipes = new();
    static RGameInfo? _world;
    static string _listed = "";

    public static readonly Choice From = new(
        "spawner.objects.from", "FROM",
        "NEARBY lists the pieces of the world around you, nearest first. EVERYTHING LOADED lists every mesh in memory by name, so narrow it with SHOW.",
        new[] { ("nearby", "NEARBY"), ("loaded", "EVERYTHING LOADED") })
    {
        Preference = true,
        Opening = () =>
        {
            if (!ReferenceEquals(_world, World.Info) || _recipes.Count == 0)
            {
                Refresh();
            }
        },
    };

    public static readonly Choice Show = new(
        "spawner.objects.show", "SHOW",
        "Only lists meshes whose names sound like this kind of thing.",
        Kinds.Select(kind => (kind.Id, kind.Label)).ToArray())
    {
        Preference = true,
    };

    public static readonly Choice Object = new(
        "spawner.objects.object", "OBJECT",
        "The mesh to make copies of.",
        new[] { ("", "OPEN THE MENU IN GAME") }, saved: false);

    public static readonly Command RefreshList = new(
        "spawner.objects.refresh", "REFRESH LIST",
        "Reads the meshes again, around where you are now.",
        () =>
        {
            Refresh();
            return Outcome.Done($"{_recipes.Count} LISTED");
        });

    public static readonly Toggle Physics = new(
        "spawner.objects.physics", "PHYSICS",
        "On, copies fall, tumble and can be knocked about. Off, they stay exactly where they're put.",
        on: true)
    {
        Preference = true,
    };

    public static readonly Toggle Solid = new(
        "spawner.objects.solid", "SOLID",
        "Off, you and everything else pass through them. PHYSICS needs it on.",
        on: true)
    {
        Preference = true,
    };

    public static readonly Slider Size = new(
        "spawner.objects.size", "SIZE",
        "How big copies are, times the size of the piece they came from.",
        min: 0.1, max: 10, step: 0.1, value: 1, unit: "X")
    {
        Preference = true,
    };

    public static readonly Command Spawn = new(
        "spawner.objects.spawn", "SPAWN OBJECT",
        "Spawns the object picked above, HOW MANY times, the way this tab and PLACE say.",
        SpawnPicked);

    public static readonly Command SpawnAimed = new(
        "spawner.objects.spawn_aimed", "SPAWN WHAT YOU AIM AT",
        "Copies the piece of the world under your crosshair, like a bench, a lamp post or a wreck, with its look and size.",
        CopyAimed);

    public static readonly Catalog.Section[] Sections =
    {
        new("WHICH", From, Show, Object, RefreshList),
        new("HOW", Physics, Solid, Size, Spawn, SpawnAimed),
        Placing.Section,
    };

    // Runs every tick. Once the menu has read a list, it follows FROM and SHOW as soon as they change.
    public static void Tick()
    {
        if (_listed.Length > 0 && World.Info is not null && Wanted() != _listed)
        {
            Refresh();
        }
    }

    static string Wanted() => From.CurrentId + "/" + Show.CurrentId;

    static void Refresh()
    {
        if (World.Info is null)
        {
            return;
        }

        _world = World.Info;
        _listed = Wanted();
        var words = Kinds[Math.Clamp(Show.Index, 0, Kinds.Length - 1)].Words;
        _recipes = From.Index == 1 ? Loaded(words) : Nearby(words);

        if (_recipes.Count == 0)
        {
            Object.SetOptions(new[] { ("", "NOTHING MATCHES") }, 0);
            return;
        }

        var picked = Object.CurrentId;
        var options = _recipes.Select(recipe => (recipe.Key, recipe.Metres >= 0f ? $"{recipe.Label}  {recipe.Metres:0} M" : recipe.Label)).ToArray();
        Object.SetOptions(options, Math.Max(0, Array.FindIndex(options, option => option.Key == picked)));
    }

    // Every piece of the world close by, the ones inside the big collection actors too, since
    // most of the city's props live there.
    static List<Recipe> Nearby(string[] words)
    {
        if (Teleport.Mover is not { } mover)
        {
            return new List<Recipe>();
        }

        var here = mover.Location;
        var close = new List<(float Distance, StaticMeshComponent Part, Actor Holder)>();
        foreach (var part in Game.FindObjects<StaticMeshComponent>())
        {
            if (!part.IsValid || part.IsClassDefaultObject || part.HiddenGame)
            {
                continue;
            }

            var bounds = part.Bounds;
            var distance = Vector3.DistanceSquared(bounds.Origin, here);
            if (distance > NearbyRange * NearbyRange || bounds.SphereRadius < Smallest || bounds.SphereRadius > Biggest)
            {
                continue;
            }

            // Templates live in packages, and people and vehicles carry their own meshes.
            if (HolderOf(part) is not { } holder || holder.Outer is not Level || holder is Pawn)
            {
                continue;
            }

            close.Add((distance, part, holder));
        }

        close.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var seen = new HashSet<nint>();
        var list = new List<Recipe>();
        foreach (var (distance, part, holder) in close)
        {
            if (list.Count >= NearbyMost)
            {
                break;
            }

            if (part.StaticMesh is not { IsValid: true } mesh || !seen.Add(mesh.Ptr) || !Matches(mesh, words))
            {
                continue;
            }

            var recipe = RecipeOf(part, holder, mesh);
            list.Add(recipe with { Metres = MathF.Sqrt(distance) / Metre });
        }

        return list;
    }

    static List<Recipe> Loaded(string[] words)
    {
        var list = new List<Recipe>();
        foreach (var mesh in Game.FindObjects<StaticMesh>())
        {
            if (mesh.IsValid && !mesh.IsClassDefaultObject && Matches(mesh, words))
            {
                list.Add(new Recipe(mesh.GetPathName(null), People.Pretty(mesh.Name.ToString() ?? "MESH"), mesh, Array.Empty<MaterialInterface?>(), Vector3.One, default, 0f));
            }
        }

        list.Sort((a, b) => string.CompareOrdinal(a.Label, b.Label));
        return list.Take(LoadedMost).ToList();
    }

    static bool Matches(StaticMesh mesh, string[] words)
    {
        if (words.Length == 0)
        {
            return true;
        }

        var name = mesh.Name.ToString() ?? "";
        return words.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    static Actor? HolderOf(StaticMeshComponent part) =>
        part.Owner is { IsValid: true } owner ? owner : part.Outer as Actor is { IsValid: true } outer ? outer : null;

    // A collection actor's pieces carry their own place and size, anything else's come from the actor.
    static Recipe RecipeOf(StaticMeshComponent part, Actor holder, StaticMesh mesh)
    {
        var materials = new MaterialInterface?[Math.Clamp(part.GetNumElements(), 0, 32)];
        for (var i = 0; i < materials.Length; i++)
        {
            materials[i] = part.GetMaterial(i) is { IsValid: true } material ? material : null;
        }

        var collection = holder is StaticMeshCollectionActor;
        var scale = collection ? part.Scale3D * part.Scale : holder.DrawScale3D * holder.DrawScale * part.Scale3D * part.Scale;
        var turn = collection ? part.Rotation : holder.Rotation;
        return new Recipe(mesh.GetPathName(null), People.Pretty(mesh.Name.ToString() ?? "MESH"), mesh, materials, scale,
            new Rotator(turn.Pitch, 0f, turn.Roll), part.Bounds.SphereRadius);
    }

    static Outcome SpawnPicked()
    {
        if (World.Info is null)
        {
            return Outcome.NoPlayer;
        }

        if (_recipes.Find(recipe => recipe.Key == Object.CurrentId) is not { } picked)
        {
            return Outcome.Failed("PICK AN OBJECT FIRST");
        }

        if (!picked.Mesh.IsValid)
        {
            Refresh();
            return Outcome.Failed("GONE, PICK AGAIN");
        }

        return Make(picked);
    }

    // Another of a mesh object already in the world, with its look and size.
    public static Outcome Copy(DynamicSMActor actor)
    {
        if (actor.StaticMeshComponent is not { IsValid: true } part || part.StaticMesh is not { IsValid: true } mesh)
        {
            return Outcome.Failed("NOT A MESH");
        }

        return Make(RecipeOf(part, actor, mesh));
    }

    static Outcome CopyAimed()
    {
        if (Teleport.Mover is not { } mover || World.Controller is not { } controller)
        {
            return Outcome.NoPlayer;
        }

        controller.GetPlayerViewPoint(out var eye, out var view);
        var way = view.ToDirection();
        var end = eye + way * Reach;
        var start = eye;
        for (var i = 0; i < Layers; i++)
        {
            var hit = mover.Trace(out var spot, out _, end, start, Component.ECollisionFilter.COF_TraceWorld, Vector3.Zero, out var info, 0);
            if (hit is null)
            {
                break;
            }

            if (hit is Volume or Pawn)
            {
                start = spot + way * StepPast;
                continue;
            }

            var part = info.HitComponent is StaticMeshComponent { IsValid: true } direct ? direct : PartAt(hit, spot);
            if (part?.StaticMesh is not { IsValid: true } mesh || HolderOf(part) is not { } holder)
            {
                return Outcome.Failed("NOT A MESH");
            }

            return Make(RecipeOf(part, holder, mesh));
        }

        return Outcome.Failed("AIM AT SOMETHING");
    }

    // When the hit doesn't say which piece it was: the actor's own mesh, or else the smallest
    // piece of the world whose box holds the spot.
    static StaticMeshComponent? PartAt(Actor hit, Vector3 spot)
    {
        if (hit is DynamicSMActor { StaticMeshComponent: { IsValid: true } dynamic })
        {
            return dynamic;
        }

        if (hit is StaticMeshActor { StaticMeshComponent: { IsValid: true } still })
        {
            return still;
        }

        StaticMeshComponent? best = null;
        var smallest = float.MaxValue;
        foreach (var part in Game.FindObjects<StaticMeshComponent>())
        {
            if (!part.IsValid || part.IsClassDefaultObject || part.HiddenGame)
            {
                continue;
            }

            var bounds = part.Bounds;
            var gap = Vector3.Abs(spot - bounds.Origin) - bounds.BoxExtent;
            if (gap.X > Snug || gap.Y > Snug || gap.Z > Snug || bounds.SphereRadius >= smallest || bounds.SphereRadius > Biggest)
            {
                continue;
            }

            if (HolderOf(part) is { } holder && holder.Outer is Level && holder is not Pawn)
            {
                best = part;
                smallest = bounds.SphereRadius;
            }
        }

        return best;
    }

    static Outcome Make(Recipe recipe)
    {
        if (Teleport.Mover is not { } mover || World.Controller is not { } controller)
        {
            return Outcome.NoPlayer;
        }

        if (controller.bCinematicMode)
        {
            return Outcome.Failed("NOT IN A SCENE");
        }

        var room = Spawned.Room;
        if (room == 0)
        {
            return Outcome.Failed("AT MOST AT ONCE");
        }

        var size = (float)Size.Value;
        var spots = Placing.Spots(mover, Math.Min((int)Placing.Count.Value, room), MathF.Min(recipe.Radius * 2f * size, MostRoom));
        if (spots.Count == 0)
        {
            return Outcome.Failed("NO ROOM THERE");
        }

        var physics = Physics.On && Solid.On;
        var made = 0;
        foreach (var spot in spots)
        {
            var turn = new Rotator(recipe.Tilt.Pitch, spot.Facing.Yaw, recipe.Tilt.Roll);
            var kind = physics ? KActorSpawnable.StaticClass() : DynamicSMActor_Spawnable.StaticClass();
            if (Game.SpawnActor(kind, default, spot.Floor + new Vector3(0f, 0f, Lift), turn) is not DynamicSMActor copy)
            {
                continue;
            }

            try
            {
                Dress(copy, recipe, size);
            }
            catch
            {
                copy.Destroy();
                throw;
            }

            var spawn = Spawned.Add(copy, recipe.Label, passive: false);
            var floor = spot.Floor;
            var expected = recipe.Radius * size;
            if (Sit(copy, floor, expected))
            {
                Release(copy, spawn, physics, recipe.Label);
            }
            else
            {
                // Its size isn't known yet, so it waits hidden until the next upkeep.
                copy.SetHidden(true);
                spawn.Start = () =>
                {
                    if (!Sit(copy, floor, expected))
                    {
                        Sit(copy, floor, 0f);
                    }

                    Release(copy, spawn, physics, recipe.Label);
                };
            }

            made++;
        }

        return made > 0 ? Outcome.Done($"{made} SPAWNED") : Outcome.Failed("THE GAME REFUSED");
    }

    // Kept still until it's on the floor. The size goes on before the mesh, since a new mesh is
    // fitted at once but a new size only at the end of the tick.
    static void Dress(DynamicSMActor copy, Recipe recipe, float size)
    {
        copy.SetPhysics(Actor.EPhysics.PHYS_None);
        copy.SetDrawScale3D(recipe.Scale * size);
        copy.SetStaticMesh(recipe.Mesh);
        if (copy.StaticMeshComponent is { IsValid: true } part)
        {
            for (var i = 0; i < recipe.Materials.Length; i++)
            {
                if (recipe.Materials[i] is { IsValid: true } material)
                {
                    part.SetMaterial(i, material);
                }
            }
        }

        if (Solid.On)
        {
            copy.SetCollision(true, true, false);
        }
        else
        {
            copy.SetCollision(false, false, false);
        }
    }

    // Sits it on the floor by its real size. False while its size isn't known yet, which shows as
    // empty bounds, bounds that aren't around the copy, or a size other than expected (0 when unknown).
    static bool Sit(DynamicSMActor copy, Vector3 floor, float expected)
    {
        if (!copy.IsValid || copy.bDeleteMe || copy.StaticMeshComponent is not { IsValid: true } part)
        {
            return false;
        }

        copy.ForceUpdateComponents(false, true);
        var bounds = part.Bounds;
        if (bounds.BoxExtent.Z <= 0f || Vector3.Distance(bounds.Origin, copy.Location) > bounds.SphereRadius + Lift
            || (expected > 0f && MathF.Abs(bounds.SphereRadius - expected) > expected * 0.1f))
        {
            return false;
        }

        copy.SetLocation(copy.Location + new Vector3(0f, 0f, floor.Z - (bounds.Origin.Z - bounds.BoxExtent.Z) + 2f));
        return true;
    }

    // Shown, and physics copies let go.
    static void Release(DynamicSMActor copy, Spawned.Spawn spawn, bool physics, string label)
    {
        if (!copy.IsValid || copy.bDeleteMe || copy.StaticMeshComponent is not { IsValid: true } part)
        {
            return;
        }

        copy.SetHidden(false);
        if (!physics)
        {
            return;
        }

        copy.SetPhysics(Actor.EPhysics.PHYS_RigidBody, true);
        part.WakeRigidBody();

        var from = copy.Location;
        var until = World.Now + SettleTime;
        spawn.Ready = () => !copy.IsValid || copy.bDeleteMe || copy.Location.Z < from.Z - FellTooFar || World.Now >= until;
        spawn.StartAt = World.Now;
        spawn.Start = () => Hold(copy, from, label);
    }

    // A mesh with no physics shape falls through the world, so it's put back and held still.
    static void Hold(DynamicSMActor copy, Vector3 from, string label)
    {
        if (!copy.IsValid || copy.bDeleteMe || copy.Location.Z >= from.Z - FellTooFar)
        {
            return;
        }

        copy.SetPhysics(Actor.EPhysics.PHYS_None);
        copy.SetLocation(from);
        Log.Warn("spawner.objects.fell", $"{label} has no physics shape, so it was held where it spawned.");
    }
}
