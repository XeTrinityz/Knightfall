#nullable enable

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using Tomlyn;

namespace Knightfall;

// EDITOR > KEEP. What the editor does to props and to the people the level places, and the objects
// you spawn, saved in edits.toml and put back each time their part of the world loads. The city
// loads in pieces as you move, so a piece's changes go on when it shows and its spawned objects go
// when it hides.
static class Kept
{
    const string FileName = "edits.toml";

    const double LookEvery = 0.5;

    // The file is written this long after the last change, once nothing kept is still rolling,
    // or anyway after the longest wait.
    const double SaveAfter = 1.0;
    const double LongestWait = 10.0;
    const float Resting = 10f;

    // How far under a spawned object the floor it belongs to may be.
    const float FloorReach = 5000f;

    // People turn up a moment after their part of the world shows, so they're looked for this long.
    const double LookForPeople = 30.0;

    // For this long after a person is put back, a post the game hands them again is covered over.
    const double SettleFor = 10.0;

    // A person moved less than this keeps doing what the level has them do.
    const float Shift = 50f;

    // The wait on the idle KEEP CHANGES stands a person in, so only that idle is ever taken off.
    const float IdleMark = 100000f;

    readonly record struct State(Vector3 At, Rotator Turn, float Size, bool Frozen, bool Removed, bool Ghost);

    // A piece of the level the editor changed. Its full name is the same every time its part of
    // the world loads, so that's how it's found again.
    sealed class Piece
    {
        public required string Path;
        public required string Level;

        // Where the game had it.
        public required Vector3 From;
        public required Rotator FromTurn;

        public State Now;
        public Actor? Actor;
    }

    // A spawned object, built again from what it's made of.
    sealed class Copy
    {
        public required string Mesh;
        public required string[] Materials;
        public required Vector3 Scale;
        public required bool Physics;
        public required bool Solid;
        public string Level = "";
        public State Now;
        public DynamicSMActor? Actor;
    }

    // A person a spawner in the level makes. Bodies are numbered as they're handed out, so the
    // spawner's full name and the character are what find them again.
    sealed class Person
    {
        public required string Spawner;
        public required string Define;
        public required string Level;
        public required Vector3 From;
        public required Rotator FromTurn;
        public State Now;
        public RBMPawnAI? Actor;
        public long LookingSince;
        public long PutAt;

        public bool Moved => Vector3.Distance(Now.At, From) > Shift;
    }

    static readonly List<Piece> Pieces = new();
    static readonly Dictionary<string, Piece> ByPath = new(StringComparer.OrdinalIgnoreCase);
    static readonly List<Copy> Copies = new();
    static readonly List<Person> Folk = new();
    static readonly Dictionary<nint, Actor> Live = new();
    static readonly Dictionary<string, (bool Shown, nint Level)> Levels = new(StringComparer.OrdinalIgnoreCase);

    static bool _read;
    static bool _started;
    static bool _applying;
    static int _epoch = -1;
    static string _main = "";
    static string _written = "";
    static bool _dirty;
    static long _dirtyAt;
    static long _firstDirty;
    static long _lookedAt;
    static int _loaded;

    // The names of the copies the last script load left in the world, in the order they're saved.
    static string[] _left = Array.Empty<string>();

    public static readonly Toggle Keep = new(
        "editor.keep.on", "KEEP CHANGES",
        "Saves what you do to props, to the people the level places and to the objects you spawn, and puts it all back each time that part of the world loads. A person you move stays where you put them. Off puts the game's own back. Your changes stay saved for when it's on again.")
    {
        Tick = Watch,
        Restore = Stop,
    };

    public static readonly Choice Here = new(
        "editor.keep.here", "KEPT",
        "How many kept changes are loaded around you, and how many are saved in all.",
        new[] { ("", "NOTHING YET") }, saved: false)
    {
        Block = Off,
    };

    public static readonly Choice Status = new(
        "editor.keep.selected", "SELECTED",
        "Whether the editor's selection is kept, or why it can't be. The city's crowds, enemies, vehicles, turrets, pickups and moving scenery are never kept, and REMOVE isn't kept on people.",
        new[] { ("", "NOTHING SELECTED") }, saved: false)
    {
        Block = Off,
    };

    public static readonly Command ForgetHere = new(
        "editor.keep.forget_here", "FORGET THIS AREA",
        "Puts everything kept around you back the way the game had it, and takes it out of edits.toml.",
        ForgetLoaded)
    {
        Block = static () => Off() ?? (_loaded == 0 ? "NOTHING KEPT HERE" : null),
    };

    public static readonly Command ForgetAll = new(
        "editor.keep.forget_all", "FORGET EVERYTHING",
        "Empties edits.toml. Everything kept around you goes back the way the game had it.",
        ForgetEverything)
    {
        Block = static () => Off() ?? (Total == 0 ? "NOTHING KEPT" : null),
    };

    public static readonly Catalog.Section[] Sections =
    {
        new("KEEP", Keep, Here, Status),
        new("FORGET", ForgetHere, ForgetAll),
    };

    static string? Off() => Keep.On ? null : "KEEP CHANGES IS OFF";

    static int Total => Pieces.Count + Copies.Count + Folk.Count;

    static int Loaded() =>
        Pieces.Count(piece => Things.Alive(piece.Actor)) + Copies.Count(copy => Things.Alive(copy.Actor)) + Folk.Count(person => Things.Alive(person.Actor));

    static string FilePath => Path.Combine(GameFiles.Mod, FileName);

    static double Since(long from) => Stopwatch.GetElapsedTime(from).TotalSeconds;

    // Called on every change the editor makes.
    public static void Touched()
    {
        if (_applying || !Keep.On)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (!_dirty)
        {
            _firstDirty = now;
        }

        _dirty = true;
        _dirtyAt = now;
    }

    // Called for every new spawn. Objects spawned while KEEP CHANGES is on are kept.
    public static bool Adopts(Actor actor)
    {
        if (!Keep.On || actor is not DynamicSMActor)
        {
            return false;
        }

        Touched();
        return true;
    }

    // Called when the editor moves something. A kept person is stood still at once, or they'd walk
    // back to their post before the change is saved.
    public static void Moved(Actor actor)
    {
        if (Keep.On && actor is RBMPawnAI body && !IsStanding(body) && WhyNot(body) is null)
        {
            Stand(body);
        }
    }

    // Whether this live object is one KEEP CHANGES put there or saved.
    public static bool Holds(Actor actor) => Keep.On && Live.TryGetValue(actor.Ptr, out var known) && ReferenceEquals(known, actor);

    // Why a change to this can't be kept, or null when it can. A spawned object of the game's own
    // gets a new name each time, so only the ones the level was built with are found again.
    public static string? Why(Actor actor)
    {
        if (actor is DynamicSMActor && Spawned.Has(actor))
        {
            return null;
        }

        return actor switch
        {
            RSentryGunBase or RTurretBase => "TURRETS AREN'T KEPT",
            Vehicle or RAbandonedVehicle or RDeadVehicle => "VEHICLES AREN'T KEPT",
            RBMPawnAI body => WhyNot(body),
            Pawn => "PEOPLE AREN'T KEPT",
            RBMCombatThrownObject or RPickupBaseBase or DroppedPickup => "PICKUPS AREN'T KEPT",
            InterpActor => "MOVING SCENERY ISN'T KEPT",
            DynamicSMActor or ApexDestructibleActor when actor.Outer is not Level => "NOT PART OF THE WORLD",
            KActorSpawnable or DynamicSMActor_Spawnable or RKActorSpawnable when IsMain(actor) => "SPAWNED BY THE GAME",
            DynamicSMActor or ApexDestructibleActor => null,
            _ => "THAT ISN'T KEPT",
        };
    }

    // Only people a spawner in the level makes come back as themselves. Enemies are left out, since
    // standing them still would stop them fighting.
    static string? WhyNot(RBMPawnAI body)
    {
        if (Spawned.Has(body))
        {
            return "SPAWNED PEOPLE AREN'T KEPT";
        }

        if (body.bPooledPawn_Inactive || body.Spawner is not { IsValid: true } spawner || body.CharacterDefine is not { IsValid: true }
            || !spawner.GetPathName(null).Contains(".TheWorld:", StringComparison.OrdinalIgnoreCase))
        {
            return "STREET PEOPLE COME AND GO";
        }

        return body.IsHostile() ? "ENEMIES AREN'T KEPT" : null;
    }

    static RBMBehaviour? Top(RBMPawnAI body) =>
        body.Controller is RBMAIController brain ? brain.BehaviourList.FirstOrDefault(behaviour => behaviour is { IsValid: true }) : null;

    // Idling already, whether the level has them do that or KEEP CHANGES does.
    static bool IsStanding(RBMPawnAI body) => Top(body) is RBMBehaviour_Idle or RBM2Behaviour_IdleConfig;

    // A plain idle on top of whatever they were doing. It takes over until it's taken off.
    static void Stand(RBMPawnAI body)
    {
        if (body.Controller is RBMAIController brain && brain.SpawnBehaviour(RBMBehaviour_Idle.StaticClass()) is RBMBehaviour_Idle idle)
        {
            idle.WaitTime = IdleMark;
        }
    }

    // Takes off the idle KEEP CHANGES put on, so they go back to their post. The controller's own
    // exit call is empty outside its state, so the idle is told to end itself.
    static void LetWalk(RBMPawnAI body)
    {
        if (body.Controller is RBMAIController brain && brain.GetBehaviourOfClass(RBMBehaviour_Idle.StaticClass(), true) is RBMBehaviour_Idle { IsValid: true, WaitTime: >= IdleMark } idle)
        {
            idle.ExitBehaviour(0);
        }
    }

    static bool IsMain(Actor actor) => _main.Length > 0 && PackageOf(actor.GetPathName(null)).Equals(_main, StringComparison.OrdinalIgnoreCase);

    static string PackageOf(string path)
    {
        var dot = path.IndexOf('.');
        return dot > 0 ? path[..dot] : path;
    }

    // Runs every tick while KEEP CHANGES is on.
    static void Watch()
    {
        if (!_read)
        {
            _read = true;
            Read();
            _left = Survive.Get(Survive.KeptCopies, Array.Empty<string>());
            Survive.Set(Survive.KeptCopies, Array.Empty<string>());
        }

        if (World.Epoch != _epoch)
        {
            // Copies left by the last script load only belong to the world they were left in.
            if (_epoch >= 0)
            {
                _left = Array.Empty<string>();
            }

            NewWorld();
        }

        if (World.Player is null || !Game.TryGetWorldInfo(out var world))
        {
            return;
        }

        if (_main.Length == 0)
        {
            _main = PackageOf(world.GetPathName(null));
        }

        // What was changed before KEEP CHANGES went on is kept too. The saved changes skip
        // anything the editor already holds, so the newer change wins.
        if (!_started)
        {
            _started = true;
            _lookedAt = Stopwatch.GetTimestamp();
            Look(world);
            foreach (var (actor, kept) in Spawned.Objects())
            {
                if (!kept)
                {
                    Spawned.Keep(actor, true);
                }
            }

            if (Capture(true))
            {
                Write();
            }
        }

        if (_dirty && Moving.Held is null && Since(_dirtyAt) >= SaveAfter && (Capture(Since(_firstDirty) >= LongestWait)))
        {
            _dirty = false;
            Write();
        }

        if (Since(_lookedAt) >= LookEvery)
        {
            _lookedAt = Stopwatch.GetTimestamp();
            Look(world);
        }
    }

    // A new world took every object with it.
    static void NewWorld()
    {
        _epoch = World.Epoch;
        _main = "";
        Levels.Clear();
        Live.Clear();
        foreach (var piece in Pieces)
        {
            piece.Actor = null;
        }

        foreach (var copy in Copies)
        {
            copy.Actor = null;
        }

        foreach (var person in Folk)
        {
            person.Actor = null;
            person.LookingSince = 0;
        }
    }

    // Each part of the world that has just shown gets its changes, and each that has just hidden
    // loses its spawned objects. The world's own level is always there.
    static void Look(WorldInfo world)
    {
        if (!Levels.ContainsKey(_main))
        {
            Levels[_main] = (true, 0);
            Show(_main);
        }

        var shown = false;
        foreach (var level in world.StreamingLevels)
        {
            if (level is not { IsValid: true })
            {
                continue;
            }

            var name = level.PackageName.ToString() ?? "";
            var loaded = level.LoadedLevel is { IsValid: true } found ? found.Ptr : 0;
            var visible = level.bIsVisible && loaded != 0;
            Levels.TryGetValue(name, out var was);
            if (visible && (!was.Shown || was.Level != loaded))
            {
                if (was.Shown)
                {
                    Hide(name);
                }

                Levels[name] = (true, loaded);
                Show(name);
                shown = true;
            }
            else if (!visible && was.Shown)
            {
                Levels[name] = (false, was.Level);
                Hide(name);
            }
        }

        // A spawned object's mesh may come from another part of the world, so the ones that
        // couldn't be built try again whenever something new shows.
        if (shown)
        {
            Retry();
        }

        FindPeople();
        KeepStanding();
        _loaded = Loaded();
        if (Menu.Showing)
        {
            Rows();
        }
    }

    static bool IsShown(string level) => Levels.TryGetValue(level, out var state) && state.Shown;

    static void Show(string level)
    {
        _applying = true;
        try
        {
            foreach (var piece in Pieces)
            {
                if (piece.Level.Equals(level, StringComparison.OrdinalIgnoreCase))
                {
                    Put(piece);
                }
            }

            foreach (var copy in Copies)
            {
                if (copy.Actor is null && copy.Level.Equals(level, StringComparison.OrdinalIgnoreCase))
                {
                    Build(copy);
                }
            }

            foreach (var person in Folk)
            {
                if (!Things.Alive(person.Actor) && person.Level.Equals(level, StringComparison.OrdinalIgnoreCase))
                {
                    person.LookingSince = Stopwatch.GetTimestamp();
                }
            }
        }
        finally
        {
            _applying = false;
        }
    }

    // People still missing from a part of the world that shows are looked for among everyone the
    // game has out, by spawner and character, until they turn up or the time's up.
    static void FindPeople()
    {
        List<(Person Person, nint Spawner, nint Define)>? wanted = null;
        foreach (var person in Folk)
        {
            if (Things.Alive(person.Actor) || person.LookingSince == 0 || Since(person.LookingSince) > LookForPeople || !IsShown(person.Level))
            {
                continue;
            }

            if (Game.FindObject<GameObject>(person.Spawner) is { IsValid: true } spawner && Game.FindObject<RCharacterDefine>(person.Define) is { IsValid: true } define)
            {
                (wanted ??= new()).Add((person, spawner.Ptr, define.Ptr));
            }
        }

        if (wanted is null)
        {
            return;
        }

        _applying = true;
        try
        {
            foreach (var body in Game.FindObjects<RBMPawnAI>())
            {
                if (!Things.Alive(body) || body.IsClassDefaultObject || body.bPooledPawn_Inactive || body.Spawner is not { IsValid: true } spawner)
                {
                    continue;
                }

                var index = wanted.FindIndex(want => want.Spawner == spawner.Ptr);
                if (index < 0 || body.CharacterDefine is not { IsValid: true } character || character.Ptr != wanted[index].Define || Edits.Find(body) is not null)
                {
                    continue;
                }

                Put(wanted[index].Person, body);
                wanted.RemoveAt(index);
                if (wanted.Count == 0)
                {
                    break;
                }
            }
        }
        finally
        {
            _applying = false;
        }
    }

    static void Put(Person person, RBMPawnAI body)
    {
        var edit = Edits.Begin(body, person.From, person.FromTurn);
        var now = person.Now;
        if (person.Moved)
        {
            Edits.Put(body, now.At, now.Turn);
            if (!IsStanding(body))
            {
                Stand(body);
            }
        }

        (edit.Size, edit.Frozen, edit.Ghost) = (now.Size, now.Frozen, now.Ghost);
        Edits.Apply(edit);
        person.Actor = body;
        person.LookingSince = 0;
        person.PutAt = Stopwatch.GetTimestamp();
        Live[body.Ptr] = body;
    }

    // The level can hand a person their post a moment after they're put back, which would walk
    // them home again.
    static void KeepStanding()
    {
        foreach (var person in Folk)
        {
            if (person.PutAt != 0 && Since(person.PutAt) < SettleFor && person.Moved && person.Actor is { } body && Things.Alive(body) && !IsStanding(body))
            {
                Stand(body);
            }
        }
    }

    static void Retry()
    {
        _applying = true;
        try
        {
            foreach (var copy in Copies)
            {
                if (copy.Actor is null && IsShown(copy.Level))
                {
                    Build(copy);
                }
            }
        }
        finally
        {
            _applying = false;
        }
    }

    // A part of the world that hid and came back as the same level still has the editor's changes
    // on it, and they may be newer than the file.
    static void Put(Piece piece)
    {
        if (Game.FindObject<Actor>(piece.Path) is not { } actor || !Things.Alive(actor) || Edits.Find(actor) is not null)
        {
            return;
        }

        var edit = Edits.Begin(actor, piece.From, piece.FromTurn);
        var now = piece.Now;
        if (Things.Fixed(actor) is null)
        {
            Edits.Put(actor, now.At, now.Turn);
        }

        (edit.Size, edit.Frozen, edit.Removed, edit.Ghost) = (now.Size, now.Frozen, now.Removed, now.Ghost);
        Edits.Apply(edit);
        piece.Actor = actor;
        Live[actor.Ptr] = actor;
    }

    // The physics goes on before the editor notes it, so unfreezing gives it back.
    static void Build(Copy copy)
    {
        if (Game.FindObject<StaticMesh>(copy.Mesh) is not { IsValid: true } mesh)
        {
            return;
        }

        var now = copy.Now;
        var made = Left(copy, mesh);
        if (made is null)
        {
            var materials = copy.Materials.Select(path => path.Length > 0 && Game.FindObject<MaterialInterface>(path) is { IsValid: true } found ? found : null).ToArray();
            try
            {
                made = Objects.Build(mesh, materials, copy.Scale, copy.Physics, copy.Solid, now.At, now.Turn);
            }
            catch (Exception error)
            {
                Log.Warn("kept.build", $"A kept {mesh.Name} could not be spawned again. {error.Message}");
                made = null;
            }

            if (made is null)
            {
                return;
            }

            if (copy.Physics)
            {
                made.SetPhysics(Actor.EPhysics.PHYS_RigidBody, true);
            }
        }

        Spawned.Add(made, Things.NameOf(made), passive: false, kept: true);
        if (now.Size != 1f || now.Frozen || now.Removed || now.Ghost)
        {
            var edit = Edits.Begin(made, now.At, now.Turn);
            (edit.Size, edit.Frozen, edit.Removed, edit.Ghost) = (now.Size, now.Frozen, now.Removed, now.Ghost);
            Edits.Apply(edit);
        }

        copy.Actor = made;
        Live[made.Ptr] = made;
    }

    // The copy the last script load left in the world for this one, if it's still there. A name
    // can be given to a new object, so its mesh has to match too.
    static DynamicSMActor? Left(Copy copy, StaticMesh mesh)
    {
        var index = Copies.IndexOf(copy);
        if (index < 0 || index >= _left.Length || _left[index].Length == 0)
        {
            return null;
        }

        var name = _left[index];
        _left[index] = "";
        return Game.FindObject<DynamicSMActor>(name) is { } left && Things.Alive(left) && left.StaticMeshComponent is { IsValid: true } part
            && part.StaticMesh is { IsValid: true } shown && shown.Ptr == mesh.Ptr && !Spawned.Has(left) ? left : null;
    }

    static void Hide(string level)
    {
        foreach (var copy in Copies)
        {
            if (copy.Actor is { } actor && copy.Level.Equals(level, StringComparison.OrdinalIgnoreCase))
            {
                copy.Actor = null;
                Live.Remove(actor.Ptr);
                if (Things.Alive(actor))
                {
                    LetGoOf(actor);
                    Spawned.Take(actor);
                }
            }
        }
    }

    static void LetGoOf(Actor actor)
    {
        if (ReferenceEquals(Moving.Held, actor))
        {
            Moving.LetGo();
        }
    }

    static bool Rolling(Actor actor) =>
        actor.Physics == Actor.EPhysics.PHYS_RigidBody && actor.Velocity.LengthSquared() > Resting * Resting;

    // Reads what the editor has done into what's saved. False while something kept is still
    // rolling or settling, unless forced.
    static bool Capture(bool force)
    {
        if (!force)
        {
            foreach (var edit in Edits.All)
            {
                if (Rolling(edit.Actor))
                {
                    return false;
                }
            }

            foreach (var (actor, kept) in Spawned.Objects())
            {
                if (kept && (Spawned.Settling(actor) || Rolling(actor)))
                {
                    return false;
                }
            }
        }

        foreach (var edit in Edits.All)
        {
            var actor = edit.Actor;
            if (actor is RBMPawnAI body)
            {
                if (WhyNot(body) is null)
                {
                    Note(body, edit);
                }

                continue;
            }

            if (Spawned.Has(actor) || Why(actor) is not null)
            {
                continue;
            }

            var path = actor.GetPathName(null);
            if (!ByPath.TryGetValue(path, out var piece))
            {
                piece = new Piece { Path = path, Level = PackageOf(path), From = edit.At, FromTurn = edit.Turn };
                Pieces.Add(piece);
                ByPath[path] = piece;
            }

            piece.Actor = actor;
            piece.Now = StateOf(actor, edit);
            Live[actor.Ptr] = actor;
        }

        // A person still out that the editor no longer holds was put back by RESTORE or UNDO, and
        // goes back to their post.
        Folk.RemoveAll(person =>
        {
            if (person.Actor is not { } body || !Things.Alive(body) || Edits.Find(body) is not null)
            {
                return false;
            }

            LetWalk(body);
            Live.Remove(body.Ptr);
            return true;
        });

        // A piece still loaded that the editor no longer holds was put back by RESTORE or UNDO.
        Pieces.RemoveAll(piece =>
        {
            if (piece.Actor is not { } actor || !Things.Alive(actor) || Edits.Find(actor) is not null)
            {
                return false;
            }

            ByPath.Remove(piece.Path);
            Live.Remove(actor.Ptr);
            return true;
        });

        foreach (var (actor, kept) in Spawned.Objects())
        {
            if (!kept)
            {
                continue;
            }

            var copy = Copies.Find(known => ReferenceEquals(known.Actor, actor));
            if (copy is null)
            {
                if (RecipeOf(actor) is not { } recipe)
                {
                    continue;
                }

                copy = recipe;
                Copies.Add(copy);
            }

            copy.Now = StateOf(actor, Edits.Find(actor));
            // A floor in no part of the world we watch would never show it again.
            copy.Level = FloorOf(actor) is { } floor && Levels.ContainsKey(floor) ? floor : copy.Level.Length > 0 ? copy.Level : _main;
            Live[actor.Ptr] = actor;
        }

        // A copy that's gone while its part of the world still shows was deleted by the player.
        Copies.RemoveAll(copy =>
        {
            if (copy.Actor is not { } actor || Things.Alive(actor))
            {
                return false;
            }

            Live.Remove(actor.Ptr);
            return true;
        });

        return true;
    }

    // REMOVE isn't kept on people, since a scene of the story may need them.
    static void Note(RBMPawnAI body, Edits.Edit edit)
    {
        var spawner = body.Spawner!.GetPathName(null);
        var define = body.CharacterDefine!.GetPathName(null);
        var person = Folk.Find(known => ReferenceEquals(known.Actor, body))
            ?? Folk.Find(known => known.Spawner.Equals(spawner, StringComparison.OrdinalIgnoreCase) && known.Define.Equals(define, StringComparison.OrdinalIgnoreCase));
        if (person is null)
        {
            person = new Person { Spawner = spawner, Define = define, Level = PackageOf(spawner), From = edit.At, FromTurn = edit.Turn };
            Folk.Add(person);
        }

        person.Actor = body;
        person.Now = StateOf(body, edit) with { Removed = false };
        Live[body.Ptr] = body;
    }

    static State StateOf(Actor actor, Edits.Edit? edit) =>
        new(actor.Location, actor.Rotation, edit?.Size ?? 1f, edit?.Frozen ?? false, edit?.Removed ?? false, edit?.Ghost ?? false);

    static Copy? RecipeOf(DynamicSMActor actor)
    {
        if (actor.StaticMeshComponent is not { IsValid: true } part || part.StaticMesh is not { IsValid: true } mesh)
        {
            return null;
        }

        var materials = new string[Math.Clamp(part.GetNumElements(), 0, 32)];
        for (var i = 0; i < materials.Length; i++)
        {
            materials[i] = part.GetMaterial(i) is { IsValid: true } material ? material.GetPathName(null) : "";
        }

        // NO COLLISION is the editor's, so the copy is saved as solid as it was made.
        var solid = Edits.Find(actor) is { Unsolid: true } edit ? edit.Collide : actor.bCollideActors;
        return new Copy
        {
            Mesh = mesh.GetPathName(null), Materials = materials, Scale = actor.DrawScale3D, Physics = actor is KActorSpawnable, Solid = solid, Actor = actor,
        };
    }

    // The part of the world whose floor it stands on, so it comes and goes with that floor.
    static string? FloorOf(Actor actor)
    {
        if (Teleport.Mover is not { } tracer)
        {
            return null;
        }

        Things.Box(actor, out var middle, out var half);
        var below = middle - Vector3.UnitZ * (half.Z + FloorReach);
        return Crosshair.Line(tracer, middle, below, Crosshair.Meets.Level, out var hit, actor) ? PackageOf(hit.Actor.GetPathName(null)) : _main;
    }

    // Off, or the mod unloading: what's not saved yet is saved first. Switched off, the game's own
    // goes back. Unloading with it still on, like for a script reload, leaves everything where it
    // is, and the next load finds its copies by name instead of spawning them again.
    static void Stop()
    {
        if (!_read)
        {
            return;
        }

        if (_dirty && Capture(true))
        {
            Write();
        }

        _dirty = false;
        if (Keep.On)
        {
            Survive.Set(Survive.KeptCopies, Copies.Select(copy => copy.Actor is { } actor && Things.Alive(actor) ? actor.GetPathName(null) : "").ToArray());
            return;
        }

        _applying = true;
        try
        {
            foreach (var piece in Pieces)
            {
                if (piece.Actor is { } actor && Things.Alive(actor))
                {
                    LetGoOf(actor);
                    Edits.Forget(actor);
                }

                piece.Actor = null;
            }

            foreach (var copy in Copies)
            {
                if (copy.Actor is { } actor && Things.Alive(actor))
                {
                    LetGoOf(actor);
                    Spawned.Take(actor);
                }

                copy.Actor = null;
            }

            foreach (var person in Folk)
            {
                if (person.Actor is { } body && Things.Alive(body))
                {
                    LetGoOf(body);
                    // Putting them back is a move, which stands them still again, so the idle comes off after.
                    Edits.Forget(body);
                    LetWalk(body);
                }

                person.Actor = null;
                person.LookingSince = 0;
            }
        }
        finally
        {
            _applying = false;
        }

        Live.Clear();
        Levels.Clear();
        _started = false;
    }

    static Outcome ForgetLoaded()
    {
        var count = Loaded();
        if (count == 0)
        {
            return Outcome.Unchanged("NOTHING KEPT HERE");
        }

        var ask = new Confirmation("CAN'T UNDO", "FORGET THIS AREA",
            count == 1
                ? "The kept change around you goes back the way the game had it and is taken out of edits.toml."
                : $"All {count} kept changes around you go back the way the game had them and are taken out of edits.toml.",
            "FORGET", "KEEP THEM");
        if (!Confirm.Asked(ForgetHere!, ask))
        {
            return Outcome.Asking;
        }

        Capture(true);
        Forget(piece => Things.Alive(piece.Actor), copy => Things.Alive(copy.Actor), person => Things.Alive(person.Actor));
        return Outcome.Done($"{count} FORGOTTEN");
    }

    static Outcome ForgetEverything()
    {
        var count = Total;
        if (count == 0)
        {
            return Outcome.Unchanged("NOTHING KEPT");
        }

        var ask = new Confirmation("CAN'T UNDO", "FORGET EVERYTHING",
            $"edits.toml is emptied of all {count} kept changes. The ones around you go back the way the game had them.",
            "FORGET", "KEEP THEM");
        if (!Confirm.Asked(ForgetAll!, ask))
        {
            return Outcome.Asking;
        }

        Capture(true);
        Forget(static _ => true, static _ => true, static _ => true);
        return Outcome.Done($"{count} FORGOTTEN");
    }

    static void Forget(Predicate<Piece> piecesTo, Predicate<Copy> copiesTo, Predicate<Person> folkTo)
    {
        _applying = true;
        try
        {
            foreach (var piece in Pieces.Where(piece => piecesTo(piece)).ToArray())
            {
                if (piece.Actor is { } actor && Things.Alive(actor))
                {
                    LetGoOf(actor);
                    Edits.Forget(actor);
                    Live.Remove(actor.Ptr);
                }

                Pieces.Remove(piece);
                ByPath.Remove(piece.Path);
            }

            foreach (var copy in Copies.Where(copy => copiesTo(copy)).ToArray())
            {
                if (copy.Actor is { } actor && Things.Alive(actor))
                {
                    LetGoOf(actor);
                    Live.Remove(actor.Ptr);
                    copy.Actor = null;
                    Spawned.Take(actor);
                }

                Copies.Remove(copy);
            }

            foreach (var person in Folk.Where(person => folkTo(person)).ToArray())
            {
                if (person.Actor is { } body && Things.Alive(body))
                {
                    LetGoOf(body);
                    Edits.Forget(body);
                    LetWalk(body);
                    Live.Remove(body.Ptr);
                }

                Folk.Remove(person);
            }
        }
        finally
        {
            _applying = false;
        }

        _dirty = false;
        Write();
        _loaded = Loaded();
        Rows();
    }

    static void Rows()
    {
        var here = Total == 0 ? "NOTHING YET" : $"{_loaded} HERE, {Total} IN ALL";
        if (Here.ValueText != here)
        {
            Here.SetOptions(new[] { ("", here) }, 0);
        }

        var selected = Editor.Selected;
        var status = selected is null ? "NOTHING SELECTED"
            : Holds(selected) ? selected is Pawn && Edits.Find(selected) is { Removed: true } ? "KEPT, BUT NOT REMOVED" : "KEPT"
            : Why(selected) is { } why ? why
            : Edits.Changed(selected) || Spawned.Has(selected) ? "SAVING"
            : "NOT CHANGED";
        if (Status.ValueText != status)
        {
            Status.SetOptions(new[] { ("", status) }, 0);
        }
    }

    static void Read()
    {
        var path = FilePath;
        if (!File.Exists(path))
        {
            return;
        }

        TomlTable file;
        try
        {
            file = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path)) ?? new TomlTable();
        }
        catch (Exception error)
        {
            try
            {
                File.Copy(path, Path.Combine(GameFiles.Mod, "edits.bad.toml"), overwrite: true);
            }
            catch (Exception)
            {
                // Only a courtesy. The changes can't be read either way.
            }

            Log.Warn("kept.read", $"edits.toml could not be read, so nothing kept was put back. It was kept as edits.bad.toml. {error.Message}");
            return;
        }

        foreach (var table in Tables(file, "changed"))
        {
            if (table.TryGetValue("name", out var name) && name is string text && text.Length > 0 && !ByPath.ContainsKey(text))
            {
                var piece = new Piece { Path = text, Level = PackageOf(text), From = Vec(table, "from"), FromTurn = Turn(table, "from_turn"), Now = StateFrom(table) };
                Pieces.Add(piece);
                ByPath[text] = piece;
            }
        }

        foreach (var table in Tables(file, "spawned"))
        {
            if (table.TryGetValue("mesh", out var mesh) && mesh is string text && text.Length > 0)
            {
                var materials = table.TryGetValue("materials", out var list) && list is System.Collections.IEnumerable items and not string
                    ? items.Cast<object?>().Select(item => item as string ?? "").ToArray()
                    : Array.Empty<string>();
                Copies.Add(new Copy
                {
                    Mesh = text,
                    Materials = materials,
                    Scale = Vec(table, "scale", Vector3.One),
                    Physics = Flag(table, "physics"),
                    Solid = !table.ContainsKey("solid") || Flag(table, "solid"),
                    Level = table.TryGetValue("level", out var level) && level is string where ? where : "",
                    Now = StateFrom(table),
                });
            }
        }

        foreach (var table in Tables(file, "person"))
        {
            if (table.TryGetValue("spawner", out var spawner) && spawner is string from && from.Length > 0 && table.TryGetValue("character", out var define) && define is string who && who.Length > 0)
            {
                Folk.Add(new Person
                {
                    Spawner = from, Define = who, Level = PackageOf(from), From = Vec(table, "from"), FromTurn = Turn(table, "from_turn"), Now = StateFrom(table) with { Removed = false },
                });
            }
        }

        Log.Info($"KEEP CHANGES: {Pieces.Count} changed props, {Folk.Count} people and {Copies.Count} spawned objects in edits.toml.");
    }

    static IEnumerable<TomlTable> Tables(TomlTable file, string key) =>
        file.TryGetValue(key, out var found) && found is System.Collections.IEnumerable items ? items.OfType<TomlTable>() : Enumerable.Empty<TomlTable>();

    static State StateFrom(TomlTable table) =>
        new(Vec(table, "at"), Turn(table, "turn"), table.TryGetValue("size", out var size) && Number(size) is { } known && known > 0f ? known : 1f,
            Flag(table, "frozen"), Flag(table, "removed"), Flag(table, "no_collision"));

    static bool Flag(TomlTable table, string key) => table.TryGetValue(key, out var value) && value is true;

    static float? Number(object? value) => value switch
    {
        long whole => whole,
        double number when double.IsFinite(number) => (float)number,
        _ => null,
    };

    static float[] Numbers(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out var value) || value is not System.Collections.IEnumerable items || value is string)
        {
            return Array.Empty<float>();
        }

        return items.Cast<object?>().Select(Number).Where(number => number is not null).Select(number => number!.Value).ToArray();
    }

    static Vector3 Vec(TomlTable table, string key, Vector3 fallback = default) =>
        Numbers(table, key) is { Length: 3 } three ? new Vector3(three[0], three[1], three[2]) : fallback;

    static Rotator Turn(TomlTable table, string key) =>
        Numbers(table, key) is { Length: 3 } three ? new Rotator(three[0], three[1], three[2]) : default;

    static void Write()
    {
        var text = new StringBuilder();
        text.AppendLine("# Props and people changed and objects spawned with KEEP CHANGES on. Knightfall puts them back each time");
        text.AppendLine("# their part of the world loads. Close the game before you edit this file.");
        foreach (var piece in Pieces)
        {
            text.AppendLine().AppendLine("[[changed]]");
            text.Append("name = ").AppendLine(Quote(piece.Path));
            text.Append("from = ").AppendLine(Triple(piece.From));
            text.Append("from_turn = ").AppendLine(Triple(piece.FromTurn));
            WriteState(text, piece.Now);
        }

        foreach (var copy in Copies)
        {
            text.AppendLine().AppendLine("[[spawned]]");
            text.Append("mesh = ").AppendLine(Quote(copy.Mesh));
            text.Append("materials = [").Append(string.Join(", ", copy.Materials.Select(Quote))).AppendLine("]");
            text.Append("scale = ").AppendLine(Triple(copy.Scale));
            text.Append("physics = ").AppendLine(copy.Physics ? "true" : "false");
            text.Append("solid = ").AppendLine(copy.Solid ? "true" : "false");
            text.Append("level = ").AppendLine(Quote(copy.Level));
            WriteState(text, copy.Now);
        }

        foreach (var person in Folk)
        {
            text.AppendLine().AppendLine("[[person]]");
            text.Append("spawner = ").AppendLine(Quote(person.Spawner));
            text.Append("character = ").AppendLine(Quote(person.Define));
            text.Append("from = ").AppendLine(Triple(person.From));
            text.Append("from_turn = ").AppendLine(Triple(person.FromTurn));
            WriteState(text, person.Now);
        }

        var written = text.ToString();
        if (written == _written)
        {
            return;
        }

        try
        {
            // Written to a second file first, so a crash halfway never leaves a cut-off file.
            var path = FilePath;
            var temp = path + ".tmp";
            File.WriteAllText(temp, written);
            File.Move(temp, path, overwrite: true);
            _written = written;
        }
        catch (Exception error)
        {
            Log.Error("kept.write", "edits.toml could not be saved", error);
        }
    }

    static void WriteState(StringBuilder text, State state)
    {
        text.Append("at = ").AppendLine(Triple(state.At));
        text.Append("turn = ").AppendLine(Triple(state.Turn));
        text.Append("size = ").AppendLine(Format(state.Size));
        text.Append("frozen = ").AppendLine(state.Frozen ? "true" : "false");
        text.Append("removed = ").AppendLine(state.Removed ? "true" : "false");
        text.Append("no_collision = ").AppendLine(state.Ghost ? "true" : "false");
    }

    static string Triple(Vector3 value) => $"[{Format(value.X)}, {Format(value.Y)}, {Format(value.Z)}]";

    static string Triple(Rotator value) => $"[{Format(value.Pitch)}, {Format(value.Yaw)}, {Format(value.Roll)}]";

    static string Format(float value)
    {
        if (!float.IsFinite(value))
        {
            return "0.0";
        }

        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') ? text : text + ".0";
    }

    static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
