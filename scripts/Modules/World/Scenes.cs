#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.BmScript;
using BmSDK.Engine;

namespace Knightfall;

// WORLD > SCENES. Story moments put back in the city once the story is past them. The story's
// package is only loaded as data and never streamed in, so its Kismet never runs. Each person
// is spawned the way their Kismet spawner would. Whatever the scene's behaviour hands on to the
// cutscene goes nowhere, so nothing plays and the story doesn't move.
static class Scenes
{
    // They are spawned this close to their spot and taken away past the far distance, so the
    // floor they stand on is streamed in.
    const float Near = 12000f;
    const float Far = 20000f;
    const float CheckEvery = 0.5f;
    const float RetryAfter = 10f;

    // A new body's brain starts on a short timer after it begins play.
    const float StartAfter = 0.5f;

    // A prop dropped into the world settles on the ground first, so the person is put on the
    // floor once it has.
    const float SettleAfter = 2f;

    // Most one scene brings, so a package full of guards stays small.
    const int MostPeople = 12;

    // How far in front of the first person GO TO SCENE puts you.
    const float Beside = 250f;

    // Used when a scene's behaviour reads no turning distance.
    const float TurnFallback = 800f;

    static readonly FName Turning = new("Turning");

    sealed class Member
    {
        public required string Spawner;
        public Vector3 Spot;
        public Rotator Facing;

        // The Kismet behaviour that takes over the spawned pawn, found by the variable they share.
        public string? Behaviour;
        public RBMPawnAI? Body;
        public RBMBehaviour? Made;

        // Stands in for the idle's prop, which Kismet links from the package we only read.
        public Actor? Prop;
        public int Epoch;
        public float StartAt;
        public float SettleAt;
    }

    sealed class Scene
    {
        public required string Key;
        public required string Name;
        public required string Help;
        public required string Package;

        // Parts of a character define's path, split by |. A spawner counts when its define has one.
        public required string Defines;

        // The scene's place in the story, as the chapter check reads it. Sub chapter a is 1, and
        // 99 means the whole chapter has to be over.
        public required byte Chapter;
        public required byte SubChapter;

        // A room that has to be showing before anyone is put in it.
        public string? Level;

        // The Kismet behaviour the scene gave its person, in the main sequence, when the shared
        // variable doesn't lead to it.
        public string? Behaviour;
        public Action<RBMBehaviour, RBMPawnAI, RPawnPlayer>? Watch;

        // Where the room was streamed in. Kismet spots are in the room's own coordinates.
        public Vector3 Offset;
        public string? LevelState;

        public List<Member>? Members;
        public float CheckedAt = -1f;
        public float RetryAt = -1f;
        public bool Reported;
        public Toggle Row = null!;

        public string BehaviourPath => $"{Package}.TheWorld.PersistentLevel.Main_Sequence.{Behaviour}";
    }

    static readonly Scene[] All =
    {
        new()
        {
            Key = "gordon_roof", Name = "GORDON ON THE GCPD ROOF", Package = "CityZ_17_Ch1b", Defines = "Heroes.Gordon", Chapter = 1, SubChapter = 2,
            Help = "Gordon waits on the phone by the Bat-Signal on the GCPD roof, as he does before your first meeting. He turns to you when you get close.",
            Behaviour = "RBMBehaviour_GordonFaceAwayOnRoof_0",
            Watch = WatchGordon,
        },
        new()
        {
            Key = "gordon_chase", Name = "GORDON ON THE STREET", Package = "CityZ_12_Ch3a", Defines = "Heroes.Gordon", Chapter = 3, SubChapter = 1,
            Help = "Gordon where the story has him on the street in chapter three.",
        },
        new()
        {
            Key = "gordon_garage", Name = "GORDON AT THE GCPD GARAGE", Package = "CityZ_T17_Ch3a", Defines = "Heroes.Gordon", Chapter = 3, SubChapter = 1,
            Help = "Gordon in the GCPD garage, as in chapter three.",
        },
        new()
        {
            Key = "gordon_ambush", Name = "GORDON AT THE AMBUSH", Package = "CityY_05_Ch8", Defines = "Heroes.Gordon", Chapter = 8, SubChapter = 99,
            Help = "Gordon where Red Hood's people hold him in chapter eight.",
        },
        new()
        {
            Key = "gordon_hideout", Name = "GORDON IN THE HIDEOUT", Package = "Hideout_A3_Ch8", Defines = "Heroes.Gordon", Chapter = 8, SubChapter = 99,
            Level = "Hideout_A3",
            Help = "Gordon where the story holds him in the hideout. Only while the hideout is showing.",
        },
        new()
        {
            Key = "gordon_briefing", Name = "GORDON AT THE BRIEFING", Package = "Lockup_B1_Ch1bcd", Defines = "Heroes.Gordon|Briefing_Leader", Chapter = 1, SubChapter = 99,
            Level = "Lockup_B1",
            Help = "Gordon and the briefing leader in the GCPD, as in the first chapter. Only while the GCPD is showing.",
        },
        new()
        {
            Key = "gordon_beaten", Name = "GORDON BEATEN", Package = "CityY_U09_Ch9a", Defines = "Heroes.Gordon", Chapter = 9, SubChapter = 1,
            Help = "Gordon after the beating, from the rooftop confrontation.",
        },
        new()
        {
            Key = "oracle_roof", Name = "ORACLE ON THE ROOF", Package = "CityY_09_Ch9a", Defines = "Heroes.Oracle", Chapter = 9, SubChapter = 1,
            Help = "Oracle where the story leaves her in chapter nine.",
        },
        new()
        {
            Key = "oracle_garage", Name = "ORACLE AT THE GCPD GARAGE", Package = "CityZ_T17_Ch9", Defines = "Heroes.Oracle", Chapter = 8, SubChapter = 99,
            Help = "Oracle in the GCPD garage in chapter nine.",
        },
        new()
        {
            Key = "oracle_clocktower", Name = "ORACLE IN THE CLOCKTOWER", Package = "Clocktower_C1_Ch1d2_ChV0", Defines = "Heroes.Oracle", Chapter = 2, SubChapter = 99,
            Level = "Clocktower_C1",
            Help = "Oracle in the Clocktower. Only while the Clocktower is showing.",
        },
        new()
        {
            Key = "lockup_chapter_nine", Name = "THE GCPD IN CHAPTER NINE", Package = "Lockup_B1_Ch9", Defines = "AaronCash|Oracle_DS2_GCPDChair", Chapter = 8, SubChapter = 99,
            Level = "Lockup_B1",
            Help = "Aaron Cash and Oracle in the GCPD as it is in chapter nine. Only while the GCPD is showing.",
        },
        new()
        {
            Key = "lockup_injured", Name = "THE GCPD'S INJURED", Package = "Lockup_C1_Ch1bcd", Defines = "Injured|Paramedic", Chapter = 1, SubChapter = 99,
            Level = "Lockup_C1",
            Help = "Hurt officers and paramedics in the GCPD, as after the first chapter. Only while that part of the GCPD is showing.",
        },
        new()
        {
            Key = "aaron_cash", Name = "AARON CASH OUTSIDE THE GCPD", Package = "CityZ_17_ChF478", Defines = "AaronCash", Chapter = 8, SubChapter = 99,
            Help = "Aaron Cash outside the GCPD, as in the Firefly chapters.",
        },
        new()
        {
            Key = "lucius_fox", Name = "LUCIUS FOX IN WAYNE TOWER", Package = "WayneTower_B1_Ch3", Defines = "Heroes.Lucius", Chapter = 3, SubChapter = 99,
            Level = "WayneTower_B1",
            Help = "Lucius Fox in Wayne Tower. Only while Wayne Tower is showing.",
        },
    };

    public static readonly Choice Which = new(
        "world.scenes.which", "SCENE",
        "Which scene GO TO SCENE takes you to.",
        All.Select(scene => (scene.Key, scene.Name)).ToArray(), saved: false);

    public static readonly Command GoThere = new(
        "world.scenes.go_there", "GO TO SCENE",
        "Puts you in front of the scene picked above and turns it on. For a scene indoors, go inside first.",
        GoToPicked)
    {
        Block = () => Locked(Picked()),
    };

    public static readonly Catalog.Section Section;

    static Scenes()
    {
        foreach (var scene in All)
        {
            var one = scene;
            scene.Row = new Toggle($"world.scenes.{scene.Key}", scene.Name,
                $"{scene.Help} No cutscene plays and the story doesn't move. Only once the story is past it.")
            {
                Block = () => Locked(one),
                Tick = () => Keep(one),
                Restore = () => RemoveAll(one),
            };
        }

        Section = new Catalog.Section("SCENES", All.Select(scene => (Feature)scene.Row).Prepend(GoThere).Prepend(Which).ToArray());
    }

    static Scene Picked() => Array.Find(All, scene => scene.Key == Which.CurrentId) ?? All[0];

    // A behaviour made outside Kismet doesn't get its own Tick, so what it would do there is
    // done here: face away, then turn once the player is close.
    static void WatchGordon(RBMBehaviour made, RBMPawnAI body, RPawnPlayer player)
    {
        if (made is not RBMBehaviour_GordonFaceAwayOnRoof gordon || body is not RPawnCharacter character)
        {
            return;
        }

        // OnActivate takes the player once. A character swap gives the player a new body.
        if (gordon.Bats?.Ptr != player.Ptr)
        {
            gordon.Bats = player;
        }

        var reach = gordon.DistanceToTurn > 0f ? gordon.DistanceToTurn : TurnFallback;
        var turned = gordon.GetStateName() == Turning;
        if (!turned && Vector3.Distance(body.Location, player.Location) < reach)
        {
            gordon.GotoState(Turning);
            character.AimAtActor(player);
        }
        else if (!turned)
        {
            character.AimInDirection(body.Location - player.Location);
        }
    }

    static string? Locked(Scene scene)
    {
        if (!GameFiles.HasPackage(scene.Package))
        {
            return "NOT INSTALLED";
        }

        if (PlayerLocks.InChallenge() is { } challenge)
        {
            return challenge;
        }

        if (!World.InCity)
        {
            return "ONLY IN THE CITY";
        }

        return Passed(scene) ? null : "NOT THAT FAR IN THE STORY";
    }

    // The main story's chapter, read the way the game's own chapter check reads it.
    static bool Passed(Scene scene)
    {
        if (World.Info?.PersistentData is not { IsValid: true } data)
        {
            return false;
        }

        var chapter = data.CurrentChapter;
        return chapter > scene.Chapter || (chapter == scene.Chapter && data.CurrentSubChapter > scene.SubChapter);
    }

    static void Keep(Scene scene)
    {
        // A new world took the bodies with it, and a wrapper from the old one must not be read.
        foreach (var member in scene.Members ?? new List<Member>())
        {
            if (member.Body is not null && (member.Epoch != World.Epoch || !member.Body.IsAlive))
            {
                Remove(member);
            }
        }

        if (Locked(scene) is not null || World.Player is not { IsAlive: true } player)
        {
            RemoveAll(scene);
            return;
        }

        if (scene.Level is not null && !RoomShows(scene))
        {
            RemoveAll(scene);
            return;
        }

        if (scene.Members is { } members)
        {
            foreach (var member in members)
            {
                if (member.Body is not { } body)
                {
                    continue;
                }

                if (member.StartAt > 0f && World.Now >= member.StartAt)
                {
                    member.StartAt = 0f;
                    Start(scene, member, body);
                }

                if (member.SettleAt > 0f && World.Now >= member.SettleAt)
                {
                    member.SettleAt = 0f;
                    Settle(body, player);
                }

                if (member.Made is { IsValid: true } made)
                {
                    scene.Watch?.Invoke(made, body, player);
                }
            }
        }

        if (MathF.Abs(World.Now - scene.CheckedAt) < CheckEvery)
        {
            return;
        }

        scene.CheckedAt = World.Now;
        if (scene.Members is null)
        {
            if (World.Now >= scene.RetryAt)
            {
                Find(scene);
            }

            return;
        }

        foreach (var member in scene.Members)
        {
            var away = Vector3.Distance(player.Location, member.Spot + scene.Offset);
            if (member.Body is null && away < Near && World.Now >= scene.RetryAt)
            {
                Spawn(scene, member, player);
            }
            else if (member.Body is not null && away > Far)
            {
                Log.Info($"Scenes: one of {scene.Name} is {away / 100f:0} m from you, taken away.");
                Remove(member);
            }
        }
    }

    // Loads the scene's package as data. Its objects can be collected again later, so the spots
    // are kept and the package is asked for again at each spawn.
    static bool Load(Scene scene, params string[] wanted)
    {
        if (Packages.TryLoad(scene.Package, $"the scene {scene.Name}", "world.scenes.load", wanted))
        {
            return true;
        }

        scene.RetryAt = World.Now + RetryAfter;
        return false;
    }

    // Kismet usually hands a spawner its spot through a variable link rather than the field.
    // A link named for spawning wins over any other actor linked to it.
    static Actor? PointOf(RBmPawnSpawner spawner)
    {
        if (spawner.SpawnPoint is { IsValid: true } set)
        {
            return set;
        }

        Actor? any = null;
        foreach (var link in spawner.VariableLinks)
        {
            if (link.LinkedVariables is not { } linked)
            {
                continue;
            }

            foreach (var variable in linked)
            {
                if (variable is not SeqVar_Object { ObjValue: Actor { IsValid: true } actor } || actor is Pawn)
                {
                    continue;
                }

                var desc = link.LinkDesc.ToString() ?? "";
                if (desc.Contains("Spawn", StringComparison.OrdinalIgnoreCase) || desc.Contains("Point", StringComparison.OrdinalIgnoreCase))
                {
                    return actor;
                }

                any ??= actor;
            }
        }

        return any;
    }

    // Whether the scene's room is in and showing, with where it was put. Logged when it changes,
    // since a room the game moved puts its people somewhere else.
    static bool RoomShows(Scene scene)
    {
        var shows = false;
        var offset = Vector3.Zero;
        if (scene.Level is { } level && Game.TryGetWorldInfo(out var world))
        {
            foreach (var streamed in world.StreamingLevels)
            {
                if (streamed is { IsValid: true } && streamed.bIsVisible && streamed.LoadedLevel is { IsValid: true }
                    && string.Equals(streamed.PackageName.ToString(), level, StringComparison.OrdinalIgnoreCase))
                {
                    shows = true;
                    offset = streamed.Offset;
                    break;
                }
            }
        }

        scene.Offset = offset;
        var state = shows ? $"in at {offset.X:0} {offset.Y:0} {offset.Z:0}" : "not in";
        if (state != scene.LevelState)
        {
            scene.LevelState = state;
            Log.Info($"Scenes: {scene.Level} is {state} for {scene.Name}.");
        }

        return shows;
    }

    // The behaviour Kismet hands the spawned pawn to. Both are linked to the same variable, and
    // an idle config, which holds the scene's own poses, wins over any other.
    static string? BehaviourOf(RBmPawnSpawner spawner, RCharacterDefine define, List<RBMBehaviour> behaviours)
    {
        var shared = new HashSet<nint>();
        foreach (var link in spawner.VariableLinks)
        {
            if (link.LinkedVariables is not { } linked)
            {
                continue;
            }

            foreach (var variable in linked)
            {
                if (variable is { IsValid: true } && variable is not SeqVar_Object { ObjValue: Actor and not Pawn })
                {
                    shared.Add(variable.Ptr);
                }
            }
        }

        RBMBehaviour? found = null;
        foreach (var behaviour in behaviours)
        {
            var linked = false;
            foreach (var link in behaviour.VariableLinks)
            {
                if (link.LinkedVariables is { } variables && variables.Any(variable => variable is { IsValid: true } && shared.Contains(variable.Ptr)))
                {
                    linked = true;
                    break;
                }
            }

            if (!linked)
            {
                continue;
            }

            if (behaviour is RBM2Behaviour_IdleConfig)
            {
                return behaviour.GetPathName(null);
            }

            found ??= behaviour;
        }

        if (found is not null)
        {
            return found.GetPathName(null);
        }

        // Some scenes reach the pawn some other way. Idle configs sit at Config_<area>.<who>.<pose>,
        // so one made for this character is taken instead.
        var who = (define.Name.ToString() ?? "").Split('_')[0];
        foreach (var behaviour in behaviours)
        {
            if (behaviour is RBM2Behaviour_IdleConfig { IdleConfig: { IsValid: true } config } && who.Length > 0)
            {
                var parts = config.GetPathName(null).Split('.');
                if (parts.Length >= 3 && parts[1].StartsWith(who, StringComparison.OrdinalIgnoreCase))
                {
                    return behaviour.GetPathName(null);
                }
            }
        }

        return null;
    }

    static RCharacterDefine? DefineOf(RBmPawnSpawner spawner) =>
        spawner.CharacterDefine is { IsValid: true } main ? main : spawner.CharacterDefineFallback is { IsValid: true } fallback ? fallback : null;

    // Reads every spawner in the package that makes one of the scene's people.
    static void Find(Scene scene)
    {
        if (!Load(scene))
        {
            return;
        }

        var parts = scene.Defines.Split('|');
        var members = new List<Member>();
        var prefix = scene.Package + ".";
        var behaviours = Game.FindObjects<RBMBehaviour>()
            .Where(behaviour => behaviour.IsValid && !behaviour.IsClassDefaultObject && behaviour.GetPathName(null).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var spawner in Game.FindObjects<RBmPawnSpawner>())
        {
            if (members.Count >= MostPeople)
            {
                break;
            }

            if (!spawner.IsValid || spawner.IsClassDefaultObject)
            {
                continue;
            }

            var path = spawner.GetPathName(null);
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || DefineOf(spawner) is not { } define)
            {
                continue;
            }

            var defined = define.GetPathName(null);
            if (!Array.Exists(parts, part => defined.Contains(part, StringComparison.OrdinalIgnoreCase)) || PointOf(spawner) is not { } point)
            {
                continue;
            }

            var behaviour = scene.Behaviour is null ? BehaviourOf(spawner, define, behaviours) : $"{scene.Package}.TheWorld.PersistentLevel.Main_Sequence.{scene.Behaviour}";
            members.Add(new Member { Spawner = path, Spot = point.Location, Facing = point.Rotation, Behaviour = behaviour });
            Log.Info($"Scenes: {define.Name} for {scene.Name} takes {behaviour ?? "no behaviour"}.");
        }

        if (members.Count == 0)
        {
            if (!scene.Reported)
            {
                scene.Reported = true;
                Log.Info($"Scenes: found nobody for {scene.Name} in {scene.Package}.");
            }

            scene.RetryAt = World.Now + RetryAfter;
            return;
        }

        Log.Info($"Scenes: {scene.Name} has {members.Count} to put in.");
        scene.Members = members;
    }

    static void Spawn(Scene scene, Member member, RPawnPlayer player)
    {
        if (!Load(scene, member.Spawner) || Game.FindObject<RBmPawnSpawner>(member.Spawner) is not { IsValid: true } spawner
            || World.Info?.PopulationManager is not { IsValid: true } people)
        {
            return;
        }

        if (DefineOf(spawner) is not { } define || spawner.PawnClass is not { IsValid: true } pawn || spawner.CharacterType is not { IsValid: true } type)
        {
            scene.RetryAt = World.Now + RetryAfter;
            return;
        }

        // Written before the native call, so a crash inside it names who.
        Log.Info($"Scenes: spawning {define.Name} for {scene.Name} ({pawn.Name}, {type.Name}).");
        // A Kismet spawn point can sit under the pavement, so the person is stood on the floor found there.
        var spot = member.Spot + scene.Offset;
        // Searched from just above first, so a desk or console over the spot isn't taken for the floor.
        // Someone leaning on a car is placed where the story put them, the car stands there too.
        var withCar = member.Behaviour is { } path && Game.FindObject<RBMBehaviour>(path) is { IsValid: true } kismet && PropOf(kismet) is RVehicleNPC;
        var floor = Vector3.Zero;
        var grounded = !withCar && (Teleport.Floor(player, spot, spot.Z + 60f, spot.Z - 400f, out floor)
            || Teleport.Floor(player, spot, spot.Z + 200f, spot.Z - 400f, out floor));
        var body = people.SpawnPawn(pawn, define, type, grounded ? floor + new Vector3(0f, 0f, 100f) : spot + new Vector3(0f, 0f, 50f), member.Facing);
        if (body is not { IsAlive: true } || body.Controller is not { IsValid: true })
        {
            if (body is { IsAlive: true })
            {
                Spawned.Take(body);
            }

            Log.Info($"Scenes: the game refused {define.Name} for {scene.Name}.");
            scene.RetryAt = World.Now + RetryAfter;
            return;
        }

        if (grounded && !body.SetLocation(floor + new Vector3(0f, 0f, Teleport.Lift(body))))
        {
            Log.Info($"Scenes: couldn't stand {define.Name} on the floor for {scene.Name}.");
        }

        member.Body = body;
        member.Epoch = World.Epoch;
        member.StartAt = member.Behaviour is null ? 0f : World.Now + StartAfter;
    }

    static void Start(Scene scene, Member member, RBMPawnAI body)
    {
        if (!body.IsAlive || body.AIController is not { IsValid: true } brain || member.Behaviour is not { } path)
        {
            return;
        }

        if (!Load(scene, path) || Game.FindObject<RBMBehaviour>(path) is not { IsValid: true } kismet)
        {
            Log.Info($"Scenes: {scene.Name} stands without the scene's behaviour, it didn't load.");
            return;
        }

        // Made from the Kismet node, so it carries the scene's settings, like its idle config.
        // With no parent sequence its output links reach nothing. The rest is what the brain's
        // own CreateSpawnedBehaviour does.
        if (GameObject.ConstructObject(kismet.Class, brain, null, 0, kismet) is not RBMBehaviour { IsValid: true } made)
        {
            Log.Info($"Scenes: {scene.Name} refused the scene's behaviour.");
            return;
        }

        made.ParentSequence = null!;

        // Kismet links an idle's prop from the package we only read, so a hidden stand-in takes
        // its place in the world. An idle laid out against a prop gives up without one. The walk
        // to the prop is left out, the pawn already stands there.
        if (made is RBM2Behaviour_IdleConfig idle)
        {
            idle.MoveOptions.bUseMoveTo = false;
            if (idle.Props.Count > 0)
            {
                idle.Props.Clear();
            }

            idle.Prop = null!;

            // With no prop linked, like a car the story drives in, the stand-in goes where the
            // pawn stands.
            var linked = PropOf(kismet);
            var at = linked is null ? body.Location : linked.Location + scene.Offset;
            var turned = linked is null ? member.Facing : linked.Rotation;
            // The story's car is a level actor in the package we only read. As a spawn template
            // it crashed the game on the second copy, so a loaded vehicle template with the same
            // body is spawned instead, the way SPAWNER does it.
            if (linked is RVehicleNPC car && TemplateLike(car) is { } template
                && body.Spawn(template.Class, null, default, at + new Vector3(0f, 0f, 10f), turned, template, false) is RVehicleNPC { IsAlive: true } parked)
            {
                // Parked, so it neither drives off nor fights. The idle leans on it.
                parked.GotoBehaviourClass(RVehicleBehaviour_Idle.StaticClass());
                parked.RoofLightsOff();
                parked.BootLightsOff();
                parked.InteriorLightsOff();
                idle.Prop = parked;
                member.Prop = parked;
            }
            else if (Game.SpawnActor(DynamicSMActor_Spawnable.StaticClass(), default, at, turned) is { IsValid: true } stand)
            {
                stand.SetHidden(true);
                stand.SetCollision(false, false, false);
                idle.Prop = stand;
                member.Prop = stand;
            }

            Log.Info($"Scenes: {scene.Name} idles as {idle.IdleConfig?.Name.ToString() ?? "none"}, {(linked is null ? "prop at their feet" : "with the scene's prop")}.");
        }

        brain.RegisterSpawnedBehaviour(made);
        made.OnSpawn();
        brain.ActivateSpawnedBehaviour(made);
        member.Made = made;
        member.SettleAt = World.Now + SettleAfter;
    }

    // A vehicle template in memory with the same body as the story's car. A template lives in
    // its package, a vehicle in the world lives in a level.
    static RVehicleNPC? TemplateLike(RVehicleNPC car)
    {
        if (car.Mesh?.SkeletalMesh is not { IsValid: true } wanted)
        {
            return null;
        }

        foreach (var vehicle in Game.FindObjects<RVehicleNPC>())
        {
            if (vehicle.IsValid && !vehicle.IsClassDefaultObject && vehicle.Outer is { IsValid: true } outer && outer is not Level
                && vehicle.Mesh?.SkeletalMesh?.Ptr == wanted.Ptr)
            {
                return vehicle;
            }
        }

        return null;
    }

    // The actor linked to a Kismet behaviour as its prop.
    static Actor? PropOf(RBMBehaviour kismet)
    {
        foreach (var link in kismet.VariableLinks)
        {
            if (link.LinkedVariables is not { } linked || !(link.LinkDesc.ToString() ?? "").Contains("Prop", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var variable in linked)
            {
                if (variable is SeqVar_Object { ObjValue: Actor { IsValid: true } actor } && actor is Vehicle or not Pawn)
                {
                    return actor;
                }
            }
        }

        return null;
    }

    static void Settle(RBMPawnAI body, RPawnPlayer player)
    {
        var at = body.Location;
        if (Teleport.Floor(player, at, at.Z + 40f, at.Z - 300f, out var floor))
        {
            var want = floor.Z + Teleport.Lift(body);
            if (MathF.Abs(want - at.Z) > 8f)
            {
                body.SetLocation(at with { Z = want });
            }
        }
    }

    static void Remove(Member member)
    {
        if (member.Prop is { IsValid: true } prop && member.Epoch == World.Epoch)
        {
            if (prop is RVehicleNPC)
            {
                Spawned.Take(prop);
            }
            else
            {
                prop.Destroy();
            }
        }

        member.Prop = null;
        var body = member.Body;
        member.Body = null;
        member.Made = null;
        member.StartAt = 0f;
        if (body is null || member.Epoch != World.Epoch || !body.IsAlive)
        {
            return;
        }

        Spawned.Take(body);
    }

    static void RemoveAll(Scene scene)
    {
        foreach (var member in scene.Members ?? new List<Member>())
        {
            Remove(member);
        }
    }

    static Outcome GoToPicked()
    {
        var scene = Picked();
        if (Locked(scene) is { } reason)
        {
            return Outcome.Failed(reason);
        }

        if (scene.Members is null)
        {
            Find(scene);
        }

        if (scene.Members is not { Count: > 0 } members)
        {
            return Outcome.Failed("CAN'T FIND IT");
        }

        // Sending the player into a room that isn't in leaves them in the empty space it uses.
        if (scene.Level is not null && !RoomShows(scene))
        {
            return Outcome.Failed("GO INSIDE FIRST");
        }

        var first = members[0];
        var inFront = first.Spot + scene.Offset + Teleport.Flat(first.Facing.ToDirection()) * Beside;
        var going = Teleport.Start(GoThere, inFront, true);
        if (going.Kind != OutcomeKind.Failed)
        {
            scene.Row.SetTo(true);
        }

        return going;
    }
}
