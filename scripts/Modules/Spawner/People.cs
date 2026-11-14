#nullable enable

using System.Text;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// SPAWNER > CHARACTERS. Anyone the area has loaded can be built again: the people around you,
// whoever its missions and streets spawn. Each comes through the game's own population spawner.
// What's loaded changes as you move, so the lists are read each time a tab that shows them
// comes up.
static class People
{
    // How close FOLLOW YOU keeps them.
    const float FollowGap = 300f;

    // Classes that only make sense in their own scene.
    static readonly string[] Refused = { "Boss", "Fake", "Tutorial", "Cinematic", "Ragdoll", "Prototype2D", "MechSuit" };

    // The folder of the game's pink stand-in for a character that's missing.
    const string StandIns = ".Debugging.";

    const int Fight = 0;
    const int Follow = 2;
    const int RunAway = 3;

    // The define folders the game files people under, as the menu names them.
    static readonly Dictionary<string, string> Folders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["redhood"] = "MILITIA",
        ["penguin"] = "PENGUIN'S GANG",
        ["twoface"] = "TWO-FACE'S GANG",
        ["harley"] = "HARLEY'S GANG",
        ["riddler"] = "RIDDLER'S ROBOTS",
        ["rioters"] = "RIOTERS",
        ["gcpd_cops"] = "POLICE",
        ["firemen"] = "FIRE CREWS",
        ["paramedics"] = "PARAMEDICS",
        ["diner_civilians"] = "CIVILIANS",
        ["villain"] = "VILLAINS",
        ["heroes"] = "HEROES",
        ["neutral"] = "OTHERS",
    };

    sealed record Recipe(string Key, string Label, string Group, Class Pawn, RCharacterDefine Define, Class Type, Class? Weapon, bool Enemy)
    {
        public bool Loaded => Pawn.IsValid && Define.IsValid && Type.IsValid;
    }

    static List<Recipe> _recipes = new();
    static RGameInfo? _world;
    static string _listedGroup = "";
    static List<Arms.Kind> _weapons = new();
    static bool _shown;

    public static readonly Choice Group = new(
        "spawner.characters.group", "GROUP",
        "Which kind of character WHO lists.",
        new[] { ("", "EVERYONE") }, saved: false);

    public static readonly Choice Who = new(
        "spawner.characters.who", "WHO",
        "Everyone this area can build: the people around you, and whoever its missions and streets spawn. Changes with where you are. Select spawns them.",
        new[] { ("", "OPEN THE MENU IN GAME") }, saved: false)
    {
        Pressed = static () => Spawn!.Press(),
        PressName = "SPAWN",
        Block = static () => Mix!.On ? "RANDOM FROM GROUP IS ON" : null,
    };

    public static readonly Toggle Mix = new(
        "spawner.characters.random", "RANDOM FROM GROUP",
        "Each one spawned is a random pick from GROUP, for a mixed crowd. WHO is left out.")
    {
        Preference = true,
    };

    public static readonly Command RefreshList = new(
        "spawner.characters.refresh", "REFRESH LIST",
        "Reads again who can be spawned here, without leaving the tab. Coming back to the tab reads it too.",
        () =>
        {
            if (World.Info is null)
            {
                return Outcome.NoPlayer;
            }

            Refresh();
            return _recipes.Count > 0 ? Outcome.Done($"{_recipes.Count} LISTED") : Outcome.Unchanged("NOBODY LOADED HERE");
        });

    public static readonly Choice Side = new(
        "spawner.characters.side", "SIDE",
        "Who they fight. HOSTILE goes for you. PASSIVE fights nobody, even when hit.",
        new[] { ("hostile", "HOSTILE"), ("passive", "PASSIVE") })
    {
        Preference = true,
    };

    public static readonly Choice Behaviour = new(
        "spawner.characters.behaviour", "BEHAVIOUR",
        "What they do once spawned: come at you, stand where they are, or follow you around. Enemies can also RUN AWAY, the way thugs run from the Batmobile. With nowhere to run, like on a roof, they cower.",
        new[] { ("fight", "FIGHT"), ("stand", "STAND"), ("follow", "FOLLOW YOU"), ("runaway", "RUN AWAY") })
    {
        Preference = true,
    };

    public static readonly Choice Weapon = new(
        "spawner.characters.weapon", "WEAPON",
        "THEIR OWN is what the game gives that character. Pick a weapon to hand them one instead, or UNARMED to take it away.",
        new[] { ("own", "THEIR OWN"), ("none", "UNARMED") }, saved: false);

    public static readonly Slider Health = new(
        "spawner.characters.health", "HEALTH",
        "How much health they get, times their normal amount.",
        min: 0.25, max: 10, step: 0.25, value: 1, unit: "X")
    {
        Preference = true,
    };

    public static readonly Slider Size = new(
        "spawner.characters.size", "SIZE",
        "How big they look. Their collision stays normal size.",
        min: 0.3, max: 3, step: 0.1, value: 1, unit: "X")
    {
        Preference = true,
    };

    public static readonly Command Spawn = new(
        "spawner.characters.spawn", "SPAWN CHARACTER",
        "Spawns the character picked in WHO, HOW MANY times, the way this tab and PLACE say.",
        SpawnPicked)
    {
        Preview = static signs => Placing.Show(signs, 0f, "SPAWN"),
    };

    public static readonly Catalog.Section[] Sections =
    {
        new("WHO", Group, Who, Mix, RefreshList),
        new("HOW", Side, Behaviour, Weapon, Health, Size, Spawn),
        Placing.Section,
    };

    static void Refresh()
    {
        if (World.Info is null)
        {
            return;
        }

        _world = World.Info;
        _recipes = Recipes();
        _weapons = Arms.Find();

        var groups = _recipes.Select(recipe => recipe.Group).Distinct().ToArray();
        var group = Group.CurrentId;
        Group.SetOptions(new[] { ("", "EVERYONE") }.Concat(groups.Select(name => (name, name))).ToArray(), Math.Max(0, Array.IndexOf(groups, group) + 1));
        ListWho();

        var weapon = Weapon.CurrentId;
        var weapons = new[] { ("own", "THEIR OWN"), ("none", "UNARMED") }.Concat(_weapons.Select(w => (w.Key, w.Label))).ToArray();
        Weapon.SetOptions(weapons, Math.Max(0, Array.FindIndex(weapons, w => w.Item1 == weapon)));
        Bodyguards.ListWho();
    }

    // Reads the lists when this world's haven't been read, or came up empty. For what spawns
    // without the menu, like a hotkey or a villain's call.
    public static void Fresh()
    {
        if (!ReferenceEquals(_world, World.Info) || _recipes.Count == 0)
        {
            Refresh();
        }
    }

    // An enemy the area has loaded, from the group named if it has one. Nothing when there is
    // nobody at all.
    public static string? EnemyKey(string group)
    {
        Fresh();
        var enemies = _recipes.Where(recipe => recipe.Enemy && recipe.Loaded).ToList();
        return (enemies.Find(recipe => recipe.Group.Equals(group, StringComparison.OrdinalIgnoreCase)) ?? enemies.FirstOrDefault())?.Key;
    }

    // Every enemy the area has loaded from one group, of one kind of body when named, the armed
    // ones first.
    public static List<string> GroupKeys(string group, Class? kind = null)
    {
        Fresh();
        return _recipes.Where(recipe => recipe.Enemy && recipe.Loaded && recipe.Group.Equals(group, StringComparison.OrdinalIgnoreCase)
                && (kind is null || Is(recipe.Pawn, kind)))
            .OrderBy(recipe => recipe.Weapon is null ? 1 : 0).Select(recipe => recipe.Key).ToList();
    }

    // Everyone listed who can fight, for the bodyguards tab. Refresh hands it the new list each
    // time the lists are read.
    public static (string Key, string Label)[] Fighters() =>
        _recipes.Where(recipe => recipe.Enemy && recipe.Loaded).Select(recipe => (recipe.Key, $"{recipe.Label}  ({recipe.Group})")).ToArray();

    // Builds the picked fighter count times in a ring round you, or round who is named, and hands
    // each to adopt with the health they had before it was scaled.
    public static Outcome Hire(string key, int count, float health, Action<RPawnVillain, string, int> adopt, Pawn? around = null)
    {
        if (_recipes.Find(recipe => recipe.Key == key) is not { } picked)
        {
            return Outcome.Failed("PICK SOMEONE FIRST");
        }

        if (!picked.Loaded)
        {
            Refresh();
            return Outcome.Failed("GONE, PICK AGAIN");
        }

        if (Teleport.Mover is not { } mover || World.Controller is not { } controller)
        {
            return Outcome.NoPlayer;
        }

        if (controller.bCinematicMode)
        {
            return Outcome.Failed("NOT IN A SCENE");
        }

        if (World.Info?.PopulationManager is not { IsValid: true } people)
        {
            return Outcome.Failed("NOT IN THIS AREA");
        }

        var room = Spawned.Room;
        if (room == 0)
        {
            return Spawned.Full;
        }

        var spots = Crowd.RingSpots(around ?? mover, Math.Min(count, room));
        if (spots.Count == 0)
        {
            return Outcome.Failed("NO ROOM AROUND YOU");
        }

        // Written before the native call, so a crash inside it names who.
        Log.Info($"Bodyguards: hiring {spots.Count} x {picked.Label} ({picked.Pawn.Name}, {picked.Define.GetPathName(null)}, {picked.Type.Name}).");
        var made = 0;
        foreach (var floor in spots)
        {
            var toYou = mover.Location - floor;
            var facing = new Rotator(0f, MathF.Atan2(toYou.Y, toYou.X) * 180f / MathF.PI, 0f);
            var body = people.SpawnPawn(picked.Pawn, picked.Define, picked.Type, floor + new System.Numerics.Vector3(0f, 0f, 100f), facing);
            if (body is not { IsValid: true, bDeleteMe: false })
            {
                continue;
            }

            if (body is not RPawnVillain thug || body.Controller is not { IsValid: true })
            {
                body.DestroyOrReturnToPool(true, false);
                continue;
            }

            Spawned.Add(thug, picked.Label, false, guard: true);
            if (thug.AIController is { IsValid: true, RiotBrain: { IsValid: true } riot })
            {
                riot.bRiotWantsToFight = false;
            }

            Arm(thug, picked);
            var normal = Toughen(thug, health);
            thug.bSpawnedInRiot = true;
            adopt(thug, picked.Label, normal);
            made++;
        }

        return made > 0 ? Outcome.Done($"{made} HIRED") : Outcome.Failed("THE GAME REFUSED");
    }

    // Scales their health, and returns what it was.
    static int Toughen(RBMPawnAI body, float scale)
    {
        var normal = body.Health > 0 ? body.Health : Math.Max(1, body.HealthMax);
        var full = Math.Max(1, (int)MathF.Round(normal * scale));
        body.Health = full;
        body.HealthMax = full;
        if (body is RPawnCombat fighter)
        {
            fighter.MaxHealth = full;
        }

        return normal;
    }

    // Runs every tick. The lists are read when a tab that shows them comes up, and WHO follows
    // GROUP as soon as it changes.
    public static void Tick()
    {
        var shown = Menu.Shows(Who) || Menu.Shows(Bodyguards.Who);
        if (shown && !_shown)
        {
            Refresh();
        }

        _shown = shown;
        if (Group.CurrentId != _listedGroup)
        {
            ListWho();
        }
    }

    static IEnumerable<Recipe> Listed() => _recipes.Where(recipe => _listedGroup.Length == 0 || recipe.Group == _listedGroup);

    static void ListWho()
    {
        var group = Group.CurrentId;
        _listedGroup = group;
        var shown = Listed().ToList();
        if (shown.Count == 0)
        {
            Who.SetOptions(new[] { ("", World.Info is null ? "OPEN THE MENU IN GAME" : "NOBODY LOADED HERE") }, 0);
            return;
        }

        var who = Who.CurrentId;
        var options = shown.Select(recipe => (recipe.Key, group.Length == 0 ? $"{recipe.Label}  ({recipe.Group})" : recipe.Label)).ToArray();
        Who.SetOptions(options, Math.Max(0, Array.FindIndex(options, option => option.Key == who)));
    }

    static Outcome SpawnPicked()
    {
        // A hotkey can ask before any tab has read the lists, or after GROUP changed.
        Fresh();
        ListWho();
        if (Mix.On)
        {
            var crowd = Listed().Where(recipe => recipe.Loaded).ToList();
            return crowd.Count > 0 ? Build(crowd) : Outcome.Failed("NOBODY LOADED HERE");
        }

        if (_recipes.Find(recipe => recipe.Key == Who.CurrentId) is not { } picked)
        {
            return Outcome.Failed("PICK SOMEONE FIRST");
        }

        if (!picked.Loaded)
        {
            Refresh();
            return Outcome.Failed("GONE, PICK AGAIN");
        }

        return Build(new List<Recipe> { picked });
    }

    // Another of someone already in the world, the way this tab and PLACE say.
    public static Outcome Copy(RBMPawnAI body)
    {
        if (!body.IsValid || body.bDeleteMe)
        {
            return Outcome.Failed("GONE");
        }

        return Make(body.Class, body.CharacterDefine, body.CharactersType, body.WeaponType) is { } recipe
            ? Build(new List<Recipe> { recipe })
            : Outcome.Failed("CAN'T COPY THEM");
    }

    // One for each spot PLACE gives. With more than one to pick from, each is a random pick.
    static Outcome Build(List<Recipe> from)
    {
        if (World.Info?.PopulationManager is not { IsValid: true } people)
        {
            return Outcome.Failed("NOT IN THIS AREA");
        }

        if (Placing.Plan(0f, out _, out var spots) is { } refused)
        {
            return refused;
        }

        var passive = Side.Index == 1;
        var behaviour = Behaviour.Index;
        var made = 0;
        foreach (var spot in spots)
        {
            var picked = from.Count == 1 ? from[0] : from[Random.Shared.Next(from.Count)];

            // Written before the native call, so a crash inside it names who.
            Log.Info($"Spawner: spawning {picked.Label} ({picked.Pawn.Name}, {picked.Define.GetPathName(null)}, {picked.Type.Name}).");
            var body = people.SpawnPawn(picked.Pawn, picked.Define, picked.Type, spot.Floor + new System.Numerics.Vector3(0f, 0f, 100f), spot.Facing);
            if (body is not { IsValid: true, bDeleteMe: false })
            {
                continue;
            }

            if (body.Controller is not { IsValid: true })
            {
                body.DestroyOrReturnToPool(true, false);
                continue;
            }

            var spawn = Spawned.Add(body, picked.Label, passive, () => Start(body, behaviour, passive));
            SetUp(body, picked, spawn, behaviour);
            made++;
        }

        return made > 0 ? Outcome.Done($"{made} SPAWNED") : Outcome.Failed("THE GAME REFUSED");
    }

    static void SetUp(RBMPawnAI body, Recipe picked, Spawned.Spawn spawn, int behaviour)
    {
        var brain = body.AIController is { IsValid: true } ai ? ai : null;
        if (brain?.RiotBrain is { IsValid: true } riot)
        {
            riot.bRiotWantsToFight = behaviour == Fight && !spawn.Passive;
        }

        Arm(body, picked);
        Toughen(body, (float)Health.Value);

        if (!Size.IsDefault)
        {
            body.SetDrawScale((float)Size.Value);
        }

        if (spawn.Passive && body is RPawnVillain calm)
        {
            calm.SetHostile(false, Spawned.Calm);
        }

        // Street people only fight and roam like the city's own when they count as from the streets.
        body.bSpawnedInRiot = true;
    }

    // Once the brain has taken over. FIGHT goes the way the game pulls a street thug into a fight.
    static void Start(RBMPawnAI body, int behaviour, bool passive)
    {
        if (!body.IsValid || body.bDeleteMe || body.AIController is not { IsValid: true } brain)
        {
            return;
        }

        switch (behaviour)
        {
            case Fight when body is RPawnVillain thug && !passive:
                thug.SetIsValidCombatant(true);
                thug.ForceIntoCombat();
                if (!thug.bIsCurrentCombatant)
                {
                    brain.EnterCombat(true, true, false);
                }

                break;
            case Follow when Teleport.Mover is { } you && brain.CreateSpawnedBehaviour(RBMBehaviour_Follow.StaticClass()) is RBMBehaviour_Follow follow:
                // The game's follow gives up at once if it has nobody to follow when it starts.
                follow.FollowActor = you;
                follow.FollowDist = FollowGap;
                follow.FollowDistSq = FollowGap * FollowGap;
                brain.ActivateSpawnedBehaviour(follow);
                break;
            case RunAway when body is RPawnVillain && brain.RiotBrain is { IsValid: true } riot && FleeClass() is { } flee:
                // The way the game breaks a street thug who has the Batmobile coming at him. He
                // runs along the roads, and the game takes him away once he's far off and unseen.
                brain.ExitCombat();
                if (brain.GetBehaviour(0) is { IsValid: true } doing)
                {
                    brain.InstantExitBehaviour(doing, true);
                }

                riot.bRiotHiding = true;
                riot.RunAwayBehaviour = flee;
                brain.DefaultFleeBehaviourClass = flee;
                brain.SpawnBehaviour(flee, null!);
                break;
        }
    }

    // The run away the city's streets give their people. Nothing indoors, where there are none.
    static Class? FleeClass()
    {
        if (World.Info?.PopulationManager is not { IsValid: true } people)
        {
            return null;
        }

        foreach (var street in people.PopulationVolumes)
        {
            if (street is { IsValid: true } && street.CityRunAwayBehaviourClass is { IsValid: true } flee)
            {
                return flee;
            }
        }

        return null;
    }

    static void Arm(RBMPawnAI body, Recipe picked)
    {
        var key = Weapon.CurrentId;
        if (key == "none")
        {
            if (body.CurrWeapon is { IsValid: true })
            {
                body.DisappearDisarm();
            }

            return;
        }

        var weapon = key == "own"
            ? (body.CurrWeapon is { IsValid: true } ? null : picked.Weapon)
            : _weapons.Find(w => w.Key == key)?.Class;
        if (weapon is not { IsValid: true })
        {
            return;
        }

        Arms.Hand(body, weapon);
    }

    static List<Recipe> Recipes()
    {
        var found = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase);
        void Offer(Class? pawn, RCharacterDefine? define, Class? type, Class? weapon)
        {
            if (Make(pawn, define, type, weapon) is not { } recipe)
            {
                return;
            }

            if (found.TryGetValue(recipe.Key, out var had))
            {
                if (had.Weapon is null && recipe.Weapon is not null)
                {
                    found[recipe.Key] = had with { Weapon = recipe.Weapon };
                }
            }
            else
            {
                found[recipe.Key] = recipe;
            }
        }

        foreach (var body in Game.FindObjects<RBMPawnAI>())
        {
            if (body.IsValid && !body.bDeleteMe && !body.IsClassDefaultObject && body is not RPawnCrowdRagdoll && !ReferenceEquals(body, World.Player))
            {
                Offer(body.Class, body.CharacterDefine, body.CharactersType, body.WeaponType);
            }
        }

        foreach (var spawner in Game.FindObjects<RBmPawnSpawner>())
        {
            if (!spawner.IsValid || spawner.IsClassDefaultObject)
            {
                continue;
            }

            Offer(spawner.PawnClass, spawner.CharacterDefine ?? spawner.CharacterDefineFallback, spawner.CharacterType, spawner.WeaponType);
            if (spawner.NewGamePlus_CharacterDefine is { IsValid: true } harder)
            {
                Offer(spawner.NewGamePlus_PawnClass ?? spawner.PawnClass, harder, spawner.NewGamePlus_CharacterType ?? spawner.CharacterType, spawner.NewGamePlus_WeaponType ?? spawner.WeaponType);
            }
        }

        var thug = RPawnVillainThug.StaticClass();
        foreach (var street in Game.FindObjects<ROverworldPopulationVolume>())
        {
            if (!street.IsValid || street.IsClassDefaultObject)
            {
                continue;
            }

            Pairs(street.MultiCharacterDefines, street.MultiCharacterTypes);
            Pairs(street.MultiCharacterDefinesForGas, street.MultiCharacterTypesForGas);
        }

        void Pairs(TArray<RCharacterDefine>? defines, TArray<Class>? types)
        {
            if (defines is null || types is null)
            {
                return;
            }

            for (var i = 0; i < Math.Min(defines.Count, types.Count); i++)
            {
                Offer(thug, defines[i], types[i], null);
            }
        }

        // Two of the same name are told apart by their kind.
        var list = found.Values.ToList();
        foreach (var same in list.GroupBy(recipe => recipe.Label).Where(group => group.Count() > 1).ToList())
        {
            foreach (var recipe in same)
            {
                var kind = Pretty((recipe.Pawn.Name.ToString() ?? "").Replace("RPawnVillain", "").Replace("RPawn", ""));
                list[list.IndexOf(recipe)] = recipe with { Label = $"{recipe.Label} ({kind})" };
            }
        }

        list.Sort((a, b) =>
        {
            var order = b.Enemy.CompareTo(a.Enemy);
            order = order != 0 ? order : string.CompareOrdinal(a.Group, b.Group);
            return order != 0 ? order : string.CompareOrdinal(a.Label, b.Label);
        });
        return list;
    }

    static Recipe? Make(Class? pawn, RCharacterDefine? define, Class? type, Class? weapon)
    {
        if (pawn is not { IsValid: true } || define is not { IsValid: true } || define.IsClassDefaultObject || type is not { IsValid: true }
            || !Is(pawn, RBMPawnAI.StaticClass()))
        {
            return null;
        }

        var pawnName = pawn.Name.ToString() ?? "";
        if (Refused.Any(word => pawnName.Contains(word, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var path = define.GetPathName(null);
        if (path.Contains(StandIns, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var enemy = Is(pawn, RPawnVillain.StaticClass());
        return new Recipe(path + "@" + pawnName, Pretty(define.Name.ToString() ?? "CHARACTER"), GroupOf(path, enemy), pawn, define, type,
            weapon is { IsValid: true } ? weapon : null, enemy);
    }

    // Defines sit at Package.Folder.Name, and the folder says which crowd they're from.
    static string GroupOf(string path, bool enemy)
    {
        var parts = path.Split('.');
        var folder = parts.Length >= 3 ? parts[1] : "";
        if (folder.Length == 0)
        {
            return enemy ? "OTHER ENEMIES" : "OTHER PEOPLE";
        }

        return Folders.TryGetValue(folder, out var name) ? name : Pretty(folder);
    }

    public static bool Is(Class type, Class parent)
    {
        for (Struct? at = type; at is { IsValid: true }; at = at.SuperStruct)
        {
            if (ReferenceEquals(at, parent))
            {
                return true;
            }
        }

        return false;
    }

    // ThugDefines_Gun becomes THUG DEFINES GUN.
    public static string Pretty(string name)
    {
        var text = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var letter = name[i];
            if (letter is '_' or '-')
            {
                if (text.Length > 0 && text[^1] != ' ')
                {
                    text.Append(' ');
                }

                continue;
            }

            var split = i > 0 && char.IsUpper(letter)
                && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])));
            if (split && text.Length > 0 && text[^1] != ' ')
            {
                text.Append(' ');
            }

            text.Append(char.ToUpperInvariant(letter));
        }

        return text.ToString().Trim();
    }
}
