#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// SPAWNER > OBJECTS. Copies of any mesh in the world or in memory. Physics objects are built the
// way the game knocks a roof rack off a car, still ones like our own sky copies. What's around
// you and what's loaded both change as you move, so the list is read each time the tab comes up.
static class Objects
{
    const float Metre = Teleport.UnitsPerMetre;

    const float NearbyRange = 100f * Metre;
    const int NearbyMost = 150;

    // The most meshes one group of EVERYTHING LOADED lists. A bigger set is split by the words
    // of its names.
    const int GroupMost = 100;

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

    // How far SPAWN WHAT YOU AIM AT looks.
    const float Reach = 20000f;

    // How far outside a piece's box the crosshair spot may be and still count as on it.
    const float Snug = 10f;

    const int Loaded = 1;

    // A mesh is of a kind when a whole word of its name is one of these, so CAR doesn't take
    // every carpet and scarecrow with it.
    static readonly (string Id, string Label, string[] Words)[] Kinds =
    {
        ("any", "ANYTHING", Array.Empty<string>()),
        ("boxes", "BOXES AND CRATES", new[] { "CRATE", "WOODCRATE", "BOX", "PALLET", "CASE", "SUITCASE", "CONTAINER", "CARTON", "TRUNK" }),
        ("bins", "BARRELS AND BINS", new[] { "BARREL", "DRUM", "OILDRUM", "BIN", "DUMPSTER", "TRASH", "TRASHBAG", "GARBAGE" }),
        ("cars", "CARS AND WRECKS", new[] { "CAR", "VAN", "TRUCK", "BUS", "TAXI", "WRECK", "VEHICLE", "BOAT", "BIKE", "TRAILER", "CARRIAGE" }),
        ("street", "STREET THINGS", new[] { "LAMP", "STREETLAMP", "LIGHT", "STREETLIGHT", "BENCH", "HYDRANT", "CONE", "SIGN", "BOLLARD", "POST", "METER", "MAILBOX", "PHONE", "KIOSK" }),
        ("barriers", "BARRIERS AND FENCES", new[] { "BARRIER", "BARRICADE", "FENCE", "RAIL", "RAILING", "HANDRAIL", "GATE", "JERSEY" }),
        ("plants", "PLANTS", new[] { "TREE", "PLANT", "PLANTPOT", "BUSH", "SHRUB", "PLANTER", "FLOWER", "VEGETATION", "VEG", "GRASS", "VINE" }),
        ("furniture", "FURNITURE", new[] { "CHAIR", "TABLE", "DESK", "SOFA", "COUCH", "SHELF", "BOOKCASE", "CABINET", "CUPBOARD", "BED", "STOOL" }),
    };

    // Stand-ins for far off city blocks and invisible blockers, never something to place.
    static readonly string[] Shells = { "LOD", "VISTA", "COLLISION", "COLL", "BLOCKING" };

    const string Digits = "0123456789";

    // Metres is how far away a NEARBY one was when listed, shown only in the list.
    sealed record Recipe(string Key, string Label, string[] Words, StaticMesh Mesh, MaterialInterface?[] Materials, Vector3 Scale, Rotator Tilt, float Radius, float Metres = -1f);

    static List<Recipe> _recipes = new();
    static List<WordGroups.Bunch> _groups = new();
    static RGameInfo? _world;
    static bool _shown;
    static int _readFrom = -1;
    static int _readShow = -1;
    static int _listedGroup = -1;

    public static readonly Choice From = new(
        "spawner.objects.from", "FROM",
        "NEARBY lists the pieces of the world around you, nearest first. EVERYTHING LOADED lists every mesh in memory by name, in GROUPs.",
        new[] { ("nearby", "NEARBY"), ("loaded", "EVERYTHING LOADED") })
    {
        Preference = true,
    };

    public static readonly Choice Show = new(
        "spawner.objects.show", "SHOW",
        "Only lists meshes whose names say they're this kind of thing.",
        Kinds.Select(kind => (kind.Id, kind.Label)).ToArray())
    {
        Preference = true,
    };

    public static readonly Choice Group = new(
        "spawner.objects.group", "GROUP",
        "Meshes with names that start the same way, when FROM is EVERYTHING LOADED. The number is how many it has.",
        new[] { ("", "OPEN THE MENU IN GAME") }, saved: false)
    {
        Block = static () => From.Index == Loaded ? null : "FROM IS NEARBY",
    };

    public static readonly Choice Object = new(
        "spawner.objects.object", "OBJECT",
        "The mesh to make copies of. Select spawns it.",
        new[] { ("", "OPEN THE MENU IN GAME") }, saved: false)
    {
        Pressed = static () => Spawn!.Press(),
        PressName = "SPAWN",
    };

    public static readonly Command RefreshList = new(
        "spawner.objects.refresh", "REFRESH LIST",
        "Reads the meshes again, around where you are now, without leaving the tab. Coming back to the tab reads them too.",
        () =>
        {
            if (World.Info is null)
            {
                return Outcome.NoPlayer;
            }

            Refresh();
            return _recipes.Count > 0 ? Outcome.Done($"{_recipes.Count} LISTED") : Outcome.Unchanged("NOTHING MATCHES");
        });

    public static readonly Toggle Solid = new(
        "spawner.objects.solid", "SOLID",
        "Off, you and everything else pass through them.",
        on: true)
    {
        Preference = true,
    };

    public static readonly Toggle Physics = new(
        "spawner.objects.physics", "PHYSICS",
        "On, copies fall, tumble and can be knocked about. Off, they stay exactly where they're put. Only SOLID copies can have it.",
        on: true)
    {
        Preference = true,
        Block = static () => Solid.On ? null : "NEEDS SOLID ON",
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
        "Spawns the object picked in OBJECT, HOW MANY times, the way this tab and PLACE say.",
        SpawnPicked);

    public static readonly Command SpawnAimed = new(
        "spawner.objects.spawn_aimed", "SPAWN WHAT YOU AIM AT",
        "Copies the piece of the world under your crosshair, like a bench, a lamp post or a wreck, with its look and size.",
        CopyAimed);

    public static readonly Catalog.Section[] Sections =
    {
        new("WHICH", From, Show, Group, Object, RefreshList),
        new("HOW", Solid, Physics, Size, Spawn, SpawnAimed),
        Placing.Section,
    };

    static bool Read => _readFrom >= 0 && ReferenceEquals(_world, World.Info);

    // Runs every tick. The list is read when the tab comes up, and from then on follows FROM,
    // SHOW and GROUP as soon as they change.
    public static void Tick()
    {
        var shown = Menu.Shows(Object);
        if (shown && !_shown)
        {
            Refresh();
        }

        _shown = shown;
        if (!Read)
        {
            return;
        }

        if (From.Index != _readFrom || Show.Index != _readShow)
        {
            Refresh();
        }
        else if (Group.Index != _listedGroup)
        {
            ListObjects(null);
        }
    }

    static void Refresh()
    {
        if (World.Info is null)
        {
            return;
        }

        var picked = Object.CurrentId;
        _world = World.Info;
        _readFrom = From.Index;
        _readShow = Show.Index;
        var kind = Kinds[Math.Clamp(Show.Index, 0, Kinds.Length - 1)].Words;
        if (From.Index == Loaded)
        {
            _recipes = InMemory(kind);
            _groups = WordGroups.Split(_recipes.ConvertAll(static recipe => recipe.Words), GroupMost);
        }
        else
        {
            _recipes = Nearby(kind);
            _groups = new List<WordGroups.Bunch>();
        }

        // The group the pick is in, when it's still listed.
        var at = picked.Length == 0 ? -1 : _recipes.FindIndex(recipe => recipe.Key == picked);
        var group = at < 0 ? 0 : Math.Max(0, _groups.FindIndex(bunch => at >= bunch.Start && at < bunch.Start + bunch.Count));
        if (_groups.Count == 0)
        {
            Group.SetOptions(new[] { ("", _recipes.Count == 0 ? "NOTHING MATCHES" : "NEAREST FIRST") }, 0);
        }
        else
        {
            Group.SetOptions(_groups.Select((bunch, i) => (i.ToString(), $"{bunch.Label}  ({bunch.Count})")).ToArray(), group);
        }

        ListObjects(picked);
    }

    // The picked group's meshes, or all of them when the list has no groups.
    static void ListObjects(string? keep)
    {
        _listedGroup = Group.Index;
        var bunch = _groups.Count == 0
            ? new WordGroups.Bunch("", 0, _recipes.Count, 0)
            : _groups[Math.Clamp(_listedGroup, 0, _groups.Count - 1)];
        if (bunch.Count == 0)
        {
            Object.SetOptions(new[] { ("", "NOTHING MATCHES") }, 0);
            return;
        }

        var options = new (string, string)[bunch.Count];
        var index = 0;
        for (var i = 0; i < bunch.Count; i++)
        {
            var recipe = _recipes[bunch.Start + i];
            options[i] = (recipe.Key, recipe.Metres >= 0f ? $"{recipe.Label}  {recipe.Metres:0} M" : WordGroups.Label(recipe.Words, bunch.Depth));
            if (recipe.Key == keep)
            {
                index = i;
            }
        }

        Object.SetOptions(options, index);
    }

    // Every piece of the world close by, the ones inside the big collection actors too, since
    // most of the city's props live there.
    static List<Recipe> Nearby(string[] kind)
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

            if (part.StaticMesh is not { IsValid: true } mesh || !seen.Add(mesh.Ptr))
            {
                continue;
            }

            var label = Named(mesh, out var words);
            if (Placeable(words, kind))
            {
                list.Add(RecipeOf(part, holder, mesh, label, words) with { Metres = MathF.Sqrt(distance) / Metre });
            }
        }

        return list;
    }

    // Every mesh in memory of the kind, sorted by the words of its name.
    static List<Recipe> InMemory(string[] kind)
    {
        var list = new List<Recipe>();
        foreach (var mesh in Game.FindObjects<StaticMesh>())
        {
            if (!mesh.IsValid || mesh.IsClassDefaultObject)
            {
                continue;
            }

            var label = Named(mesh, out var words);
            if (Placeable(words, kind))
            {
                list.Add(new Recipe(mesh.GetPathName(null), label, words, mesh, Array.Empty<MaterialInterface?>(), Vector3.One, default, 0f));
            }
        }

        list.Sort(static (a, b) => WordGroups.Compare(a.Words, b.Words));
        return list;
    }

    // A mesh's name as the list shows it, and the same as words.
    static string Named(StaticMesh mesh, out string[] words)
    {
        var label = People.Pretty(mesh.Name.ToString() ?? "MESH");
        words = label.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return label;
    }

    static bool Placeable(string[] words, string[] kind) => !Says(words, Shells) && (kind.Length == 0 || Says(words, kind));

    // Whether one of the name's words is one of these, or its plural. A number on the end of a
    // word doesn't count, so BIN01 is a BIN.
    static bool Says(string[] words, string[] wanted)
    {
        foreach (var word in words)
        {
            var bare = word.AsSpan().TrimEnd(Digits);
            foreach (var want in wanted)
            {
                if (!bare.StartsWith(want, StringComparison.Ordinal))
                {
                    continue;
                }

                var rest = bare[want.Length..];
                if (rest.Length == 0 || rest is "S" || rest is "ES")
                {
                    return true;
                }
            }
        }

        return false;
    }

    static Actor? HolderOf(StaticMeshComponent part) =>
        part.Owner is { IsValid: true } owner ? owner : part.Outer as Actor is { IsValid: true } outer ? outer : null;

    static Recipe RecipeOf(StaticMeshComponent part, Actor holder, StaticMesh mesh)
    {
        var label = Named(mesh, out var words);
        return RecipeOf(part, holder, mesh, label, words);
    }

    // A collection actor's pieces carry their own place and size, anything else's come from the actor.
    static Recipe RecipeOf(StaticMeshComponent part, Actor holder, StaticMesh mesh, string label, string[] words)
    {
        var materials = new MaterialInterface?[Math.Clamp(part.GetNumElements(), 0, 32)];
        for (var i = 0; i < materials.Length; i++)
        {
            materials[i] = part.GetMaterial(i) is { IsValid: true } material ? material : null;
        }

        var collection = holder is StaticMeshCollectionActor;
        var scale = collection ? part.Scale3D * part.Scale : holder.DrawScale3D * holder.DrawScale * part.Scale3D * part.Scale;
        var turn = collection ? part.Rotation : holder.Rotation;
        return new Recipe(mesh.GetPathName(null), label, words, mesh, materials, scale, new Rotator(turn.Pitch, 0f, turn.Roll), part.Bounds.SphereRadius);
    }

    static Outcome SpawnPicked()
    {
        if (World.Info is null)
        {
            return Outcome.NoPlayer;
        }

        // A hotkey can ask before the tab has read the list.
        if (!Read)
        {
            Refresh();
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
        if (Teleport.Mover is null)
        {
            return Outcome.NoPlayer;
        }

        if (!Crosshair.Surface(Reach, out var hit))
        {
            return Outcome.Failed("AIM AT SOMETHING");
        }

        var part = hit.Part is StaticMeshComponent { IsValid: true } direct ? direct : PartAt(hit.Actor, hit.Spot);
        if (part?.StaticMesh is not { IsValid: true } mesh || HolderOf(part) is not { } holder)
        {
            return Outcome.Failed(hit.Actor is Vehicle ? "THAT'S A VEHICLE" : "NOT A MESH");
        }

        return Make(RecipeOf(part, holder, mesh));
    }

    // When the hit doesn't say which piece it was: the actor's own mesh, or else the smallest
    // piece of the world whose box holds the spot. A breakable prop or a vehicle is never taken
    // for the piece of world under it.
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

        if (hit is Pawn)
        {
            return null;
        }

        if (hit is ApexDestructibleActor)
        {
            foreach (var own in hit.Components)
            {
                if (own is StaticMeshComponent { IsValid: true, HiddenGame: false, StaticMesh: { IsValid: true } } shown)
                {
                    return shown;
                }
            }

            return null;
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
        var size = (float)Size.Value;
        if (Placing.Plan(MathF.Min(recipe.Radius * 2f * size, MostRoom), out _, out var spots) is { } refused)
        {
            return refused;
        }

        Log.Info($"Spawner: spawning {spots.Count} x {recipe.Label} ({recipe.Key}).");
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
