#nullable enable

using System.Diagnostics;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using Pace = BmSDK.Engine.RAutomaticTransitions.EMovementSpeed;

namespace Knightfall;

// PLAYER > SIDEKICK. A sidekick is a partner made the way the story makes Robin and Nightwing.
// The game loads the character into a spare character slot in the background and says when it
// is in. Then a body is spawned beside the player, given the story's partner AI and named as the
// game's switch partner. From there the game's own swap, dual takedowns and team moves work
// with it.
static class Sidekick
{
    const string Lender = "sidekick";
    const float Metre = Teleport.UnitsPerMetre;
    const double LoadGiveUpSeconds = 20;

    // The most one tick adds to the load wait, so time spent paused doesn't count.
    const double LoadTickCap = 0.5;

    const float UpkeepSeconds = 0.25f;
    const float LookEvery = 0.25f;

    // 40 m. Left further behind than this and out of sight, the sidekick rejoins behind you.
    const float CatchUpRange = 4000f;
    const float CatchUpBehind = 600f;
    const float CatchUpWaitSeconds = 1.5f;

    // Still that far behind after this long, the sidekick rejoins even on screen.
    const float CatchUpAnywaySeconds = 8f;

    // Standing still this long, this much further off than they follow at, they have no way
    // to you.
    const float StuckAfter = 5f;
    const float StuckShort = 450f;
    const float StuckStep = 60f;

    // How much further off than they stop the partner AI walks, and then runs flat out. The
    // game's own gaps.
    const float WalkPast = 250f;
    const float RunPast = 1050f;

    const float ArriveBeside = 180f;
    const float LeaveRun = 2500f;
    const float GoneRange = 1500f;
    const float LeaveGiveUpSeconds = 8f;
    const float FloorReach = 200f;
    const float ViewCone = 0.4f;

    // The story's partner versions, which the partner AI was made for.
    static readonly (string Name, string Label)[] Known =
    {
        ("Playable_Robin", "ROBIN"),
        ("Playable_Nightwing", "NIGHTWING"),
        ("Playable_Catwoman", "CATWOMAN"),
        ("Playable_DLCBatgirl", "BATGIRL"),
    };

    static readonly FName StealthMove = new("StealthMoveMode");
    static readonly FName[] FollowStates = { new("WalkingMode"), new("RunningMode"), StealthMove };
    static readonly FName Crouching = new("Crouching");
    static readonly FName StandingBot = new("StandingBot");
    static readonly FName Relaxed = new("Relaxed");
    static readonly float[] CatchUpTurns = { 180f, 150f, 210f, 120f, 240f };

    public static readonly Choice Who = new(
        "player.sidekick.who", "SIDEKICK",
        "Who CALL SIDEKICK brings in. They arrive in the suit you have picked for them. Select calls them.",
        Installed())
    {
        Preference = true,
        Pressed = static () => Call!.Press(),
        PressName = "CALL",
    };

    public static readonly Command Call = new(
        "player.sidekick.call", "CALL SIDEKICK",
        "Brings your sidekick in beside you. They follow you, join fights and do dual takedowns. Called while you are busy, they arrive once you are free.",
        Summon)
    {
        Block = static () => Car.InCar() ?? CallLock(),
    };

    public static readonly Command Bring = new(
        "player.sidekick.bring", "BRING SIDEKICK HERE",
        "Puts your sidekick beside you at once, for when they are stuck or you left them waiting somewhere.",
        Fetch)
    {
        Block = static () => Car.InCar() ?? Missing(),
    };

    public static readonly Command Dismiss = new(
        "player.sidekick.dismiss", "DISMISS SIDEKICK",
        "Your sidekick heads off and is gone once out of sight. If you are playing as them you are put back first. One still on the way is called off.",
        SendAway)
    {
        Block = static () => OnTheWay ? null : Missing(),
    };

    public static readonly Command Swap = new(
        "player.sidekick.swap", "SWAP WITH SIDEKICK",
        "Takes control of your sidekick, or hands it back. Your save still records the story's character.",
        Trade)
    {
        Block = static () => Car.InCar() ?? Missing() ?? _swapLock,
    };

    public static readonly Toggle ButtonSwaps = new(
        "player.sidekick.button_swaps", "SWAP ON CALL BUTTON",
        "With a sidekick out, the call Batmobile button swaps you with them, as in the story. Off, the button calls the Batmobile as usual and SWAP WITH SIDEKICK does the swap.",
        on: true)
    {
        Preference = true,
    };

    public static readonly Toggle Follows = new(
        "player.sidekick.follows", "SIDEKICK FOLLOWS YOU",
        "Off makes your sidekick wait where they are until you turn it back on.", on: true)
    {
        Preference = true,
    };

    public static readonly Slider Distance = new(
        "player.sidekick.distance", "SIDEKICK FOLLOW DISTANCE",
        "How far behind you your sidekick stops.",
        min: 2, max: 10, step: 0.5, value: 3.5, unit: " M")
    {
        Preference = true,
        Block = static () => Follows.On ? null : "FOLLOWING IS OFF",
    };

    public static readonly Toggle CatchesUp = new(
        "player.sidekick.catches_up", "SIDEKICK CATCHES UP",
        "A sidekick left more than 40 m behind, or with no way up to you, rejoins you out of sight, as the story's partners do.", on: true)
    {
        Preference = true,
        Block = static () => Follows.On ? null : "FOLLOWING IS OFF",
    };

    public static readonly Toggle Sneaks = new(
        "player.sidekick.sneaks", "SIDEKICK SNEAKS WITH YOU",
        "Your sidekick crouches when you do, and stays out of fights while crouched.", on: true)
    {
        Preference = true,
        Block = static () => Follows.On ? null : "FOLLOWING IS OFF",
    };

    public static readonly Toggle Fights = new(
        "player.sidekick.fights", "SIDEKICK FIGHTS",
        "Your sidekick joins your fights and dual takedowns. Off, they stand back and leave the fight to you.", on: true)
    {
        Preference = true,
    };

    public static readonly Catalog.Section[] Sections =
    {
        new("CALL", Who, Call, Bring, Dismiss),
        new("SWAP", Swap, ButtonSwaps),
        new("FOLLOWING", Follows, Distance, CatchesUp, Sneaks),
        new("FIGHTING", Fights),
        new("DUAL TAKEDOWNS", Combat.DualTakedowns) { Borrowed = true },
    };

    static RSeqAct_SwitchDualPlayerCharacter? _loader;
    static bool _loaded;
    static bool _arriving;
    static bool _standardSuit;
    static double _loadWaited;
    static long _loadTickAt;

    static RPawnPlayer? _body;
    static RPawnPlayer? _caller;
    static RRobinBotController? _bot;
    static RCombatBotVolume? _arena;
    static string _name = "";
    static int _slot = -1;
    static bool _ownsSlot;

    static bool _leaving;
    static bool _trading;
    static float _leaveSince;
    static float _farSince = -1f;
    static float _upkeepAt;
    static float _stoodSince;
    static float _stuckSeenAt = -100f;
    static Vector3 _stoodAt;
    static float _poseSince;
    static bool _adopted;

    // What the rows' locks show. Looked at a few times a second, since a lock is asked every frame.
    static float _lookedAt = -100f;
    static string? _busy;
    static string? _swapLock;
    static string _askedFor = "";
    static FName _asked;

    // Loading, or loaded and waiting for the player to be free.
    static bool OnTheWay => _loader is not null || _arriving;

    static bool Present => _body is { IsValid: true, bDeleteMe: false } && _bot is { IsValid: true, bDeleteMe: false };

    // The body that called the sidekick, while the player is in the sidekick's. Nobody otherwise.
    public static RPawnPlayer? Waiting
    {
        get
        {
            Adopt();
            return Present && _caller is { IsValid: true, bDeleteMe: false } caller && Game.TryGetPlayerController(out var controller)
                && controller.Pawn is { } driven && driven.Ptr == _body!.Ptr ? caller : null;
        }
    }

    static (string Id, string Label)[] Installed()
    {
        var list = new List<(string Id, string Label)>();
        foreach (var (name, label) in Known)
        {
            if (GameFiles.HasPackage(name))
            {
                list.Add((name, label));
            }
        }

        return list.Count > 0 ? list.ToArray() : new[] { ("", "NONE INSTALLED") };
    }

    static string? CallLock() =>
        Who.CurrentId.Length == 0 ? "NONE INSTALLED" : OnTheWay ? "ON THE WAY" : _body is not null ? _leaving ? "STILL LEAVING" : "ALREADY HERE" : _busy;

    // Why a row that needs a sidekick out can't be used, or null when one is.
    static string? Missing() => Present ? _leaving ? "LEAVING" : null : OnTheWay ? "ON THE WAY" : "NO SIDEKICK";

    static void Look()
    {
        if (MathF.Abs(World.Now - _lookedAt) < LookEvery)
        {
            return;
        }

        _lookedAt = World.Now;
        _busy = _body is null && !OnTheWay ? Busy(Who.CurrentId) : null;
        _swapLock = _body is not null && World.Info is { bDualPlayerSwitchingDisabledBySeq: true } ? "LOCKED BY THE STORY" : null;
    }

    // Why nobody can be called in right now, or null.
    static string? Busy(string name)
    {
        if (World.Player is not { } pawn || World.Controller is not { } controller || World.Info is not { } info || !Game.TryGetGameRI(out var game))
        {
            return "NO PLAYER";
        }

        if (info.IsChallengeMode())
        {
            return "NOT IN CHALLENGES";
        }

        if (Switcher.InScene(controller))
        {
            return "IN A SCENE";
        }

        if (controller.IsInCombat(false))
        {
            return "FINISH THE FIGHT";
        }

        // The story's own partner owns the switch. Taking it would break the mission.
        if (info.PlayerSwitchPawn is { IsValid: true, bDeleteMe: false })
        {
            return "PARTNER IS HERE";
        }

        if (name != _askedFor)
        {
            _askedFor = name;
            _asked = name.Length > 0 ? new FName(name) : default;
        }

        var index = Loaded.IndexOf(game, _asked);
        return index >= 0 && (index == 0 || index == Loaded.IndexOf(game, pawn)) ? "THAT IS YOU" : null;
    }

    static Outcome Summon()
    {
        Adopt();
        var name = Who.CurrentId;
        if (name.Length == 0)
        {
            return Outcome.Failed("NONE INSTALLED");
        }

        if (OnTheWay)
        {
            return Outcome.Unchanged("ON THE WAY");
        }

        if (_body is not null)
        {
            return Outcome.Unchanged(_leaving ? "STILL LEAVING" : "ALREADY HERE");
        }

        if (World.Player is not { } pawn || World.Controller is not { } controller || !Game.TryGetGameRI(out var game) || !Game.TryGetWorldInfo(out var world))
        {
            return Outcome.NoPlayer;
        }

        if (controller.Pawn is not { } driven || driven.Ptr != pawn.Ptr)
        {
            return Outcome.Failed(World.Driving is not null ? "GET OUT FIRST" : "NOT NOW");
        }

        if (pawn.bDeleteMe || pawn.Health <= 0 || Remesh.Busy)
        {
            return Outcome.Failed("NOT NOW");
        }

        if (Busy(name) is { } busy)
        {
            return Outcome.Failed(busy);
        }

        if (world.GetGameSequence() is not { IsValid: true } sequence)
        {
            return Outcome.Failed("NOT HERE");
        }

        var character = new FName(name);
        var index = Loaded.IndexOf(game, character);
        var already = index >= 0;
        _loaded = false;
        _arriving = false;
        _standardSuit = false;
        _caller = pawn;
        _name = name;
        _ownsSlot = !already;
        _leaving = false;
        _farSince = -1f;

        // The story keeps some partners loaded. One that is fully in is called as they are, so
        // the story's own copy is never reloaded.
        if (already && Ready(game, index))
        {
            _slot = index;
            return Spawn();
        }

        // With every slot taken the game's loader writes past the last one.
        if (!already && Loaded.Free(game) < 0)
        {
            Forget();
            return Outcome.Failed("TOO MANY LOADED");
        }

        var cost = already ? 0 : GameFiles.Exports(name) + GameFiles.Exports(Suits.Owner(name) + "_Std");
        var room = ObjectTable.Headroom;
        if (cost > 0 && room < cost + Packages.Spare)
        {
            world.ForceGarbageCollection(true);
            Forget();
            Log.Info($"No room to call {name}. It needs {cost} objects and {room} are free.");
            return Outcome.Failed("NOT ENOUGH ROOM");
        }

        // The game's own loader for a second character. It is a script action, and the game
        // calls its OnDataLoaded when the character is in.
        var loader = Made.Tracked(new RSeqAct_SwitchDualPlayerCharacter(sequence) { ParentSequence = sequence });
        Borrowed.Keep(loader, Lender);
        _loader = loader;
        _loadWaited = 0;
        _loadTickAt = Stopwatch.GetTimestamp();

        try
        {
            loader.SetCharacterData(out var slot, character, null!, null!);
            _slot = slot;
        }
        catch
        {
            DropLoader();
            Forget();
            throw;
        }

        return Outcome.Done("ON THE WAY");
    }

    static Outcome Trade()
    {
        Adopt();
        if (Missing() is { } missing)
        {
            return Outcome.Failed(missing);
        }

        if (World.Controller is not { } controller || controller.Pawn is not RPawnPlayer player)
        {
            return Outcome.Failed(World.Driving is not null ? "GET OUT FIRST" : "NOT NOW");
        }

        // The game's swap tells the mission when it is tried while locked, so it isn't tried.
        if (World.Info is { bDualPlayerSwitchingDisabledBySeq: true })
        {
            return Outcome.Failed("LOCKED BY THE STORY");
        }

        // Not forced, so the game's own rules apply. It won't swap mid-move, while aiming or
        // when the story has locked it.
        bool swapped;
        _trading = true;
        try
        {
            swapped = controller.SwitchPlayer(false, false, false, false, true, false);
        }
        finally
        {
            _trading = false;
        }

        if (!swapped)
        {
            var aiming = controller.GetCurrentGadget() is { IsValid: true, bAiming: true };
            return Outcome.Failed(aiming ? "STOP AIMING FIRST" : player.CurrentSpecialMove is not null || player.CombatMove is not null ? "FINISH THE MOVE" : "NOT NOW");
        }

        World.Tick();
        Catalog.MenuOpening();
        return Outcome.Done("SWAPPED");
    }

    static Outcome Fetch()
    {
        Adopt();
        if (Missing() is { } missing)
        {
            return Outcome.Failed(missing);
        }

        if (World.Controller is not { } controller || controller.Pawn is not RPawnPlayer player)
        {
            return Outcome.Failed(World.Driving is not null ? "GET OUT FIRST" : "NOT NOW");
        }

        if (_bot!.BotPawn is not { IsValid: true, bDeleteMe: false } follower || follower.CurrentSpecialMove is not null || follower.CombatMove is not null)
        {
            return Outcome.Failed("THEY ARE BUSY");
        }

        if (!Beside(player, out var floor))
        {
            return Outcome.Failed("NO ROOM HERE");
        }

        Place(follower, floor, player.Rotation.Yaw);
        return Outcome.Done("HERE");
    }

    static Outcome SendAway()
    {
        Adopt();
        if (OnTheWay)
        {
            CancelLoad();
            return Outcome.Done("CALLED OFF");
        }

        if (!Present)
        {
            return Outcome.Unchanged("NO SIDEKICK");
        }

        if (_leaving)
        {
            return Outcome.Unchanged("LEAVING");
        }

        if (Waiting is not null)
        {
            if (World.Controller is not { } controller || !controller.SwitchPlayer(true, false, false, false, false, false) || Waiting is not null)
            {
                return Outcome.Failed("TRY AGAIN");
            }

            World.Tick();
            Catalog.MenuOpening();
        }

        _leaving = true;
        _leaveSince = World.Now;
        _upkeepAt = 0f;
        Arena(false);
        Keep();
        return Outcome.Done("HEADING OFF");
    }

    public static void Tick()
    {
        Adopt();
        Look();
        if (OnTheWay)
        {
            TickLoad();
            return;
        }

        if (_body is null)
        {
            return;
        }

        // A new world took every actor with it. There is nothing left to tidy.
        if (!_body.IsValid)
        {
            Forget();
            return;
        }

        // A scene or a mission took the sidekick or the caller away.
        if (!Present || _caller is not { IsValid: true, bDeleteMe: false })
        {
            Log.Info($"{_name} or the body that called them was taken away, so the sidekick is gone.");
            Remove();
            return;
        }

        if (World.Now >= _upkeepAt - UpkeepSeconds && World.Now < _upkeepAt)
        {
            return;
        }

        _upkeepAt = World.Now + UpkeepSeconds;
        if (World.Controller is not { } controller || World.Info is not { } info || _bot!.BotPawn is not { IsValid: true, bDeleteMe: false } follower)
        {
            return;
        }

        // The story named a partner of its own, or took the switch away. Ours steps aside, and
        // the story's link is left as it is.
        if (info.PlayerSwitchPawn is not { } linked || (linked.Ptr != _body.Ptr && linked.Ptr != _caller.Ptr))
        {
            Log.Info($"The story took the switch partner over, so {_name} is gone.");
            Remove();
            Toasts.Show(Who.Plain, "THE STORY TOOK OVER", OutcomeKind.Unchanged);
            return;
        }

        if (_leaving)
        {
            Leave(controller, follower);
            return;
        }

        Tune(_bot);
        Arena(Fights.On);
        Sneak(controller, follower);
        if (Follows.On && CatchesUp.On)
        {
            CatchUp(controller, follower);
        }
    }

    // A load that outlives this copy of the class would keep its slot for good.
    public static void Release()
    {
        if (OnTheWay)
        {
            CancelLoad();
        }

        Keep();
    }

    static void TickLoad()
    {
        // The player went to another world or into another body. Nobody is left to arrive beside.
        if (_caller is not { IsValid: true, bDeleteMe: false })
        {
            CancelLoad();
            Call.Report(Outcome.Unchanged("CALLED OFF"));
            return;
        }

        if (_arriving)
        {
            Arrive();
            return;
        }

        // The game's call to say it is done doesn't reach us when it comes from inside our own call
        // into the loader, as it does for packages already in memory. So the slot is checked too.
        if (_loaded || (_slot >= 0 && Game.TryGetGameRI(out var game) && Ready(game, _slot)))
        {
            _loaded = false;
            Unhook();
            DropLoader();
            _arriving = true;
            Arrive();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        _loadWaited += Math.Min((now - _loadTickAt) / (double)Stopwatch.Frequency, LoadTickCap);
        _loadTickAt = now;

        // The game takes the character out of its slot when a package it needs is not installed,
        // usually the suit picked for them, and then never says it is done.
        var dropped = Dropped();
        if (dropped && _standardSuit)
        {
            Log.Info($"{_name} could not load as a sidekick, even in the standard suit.");
            CancelLoad();
            Call.Report(Outcome.Failed("CAN'T LOAD"));
            return;
        }

        // Only a slot we loaded is retried. A slot the story loaded keeps the story's suit.
        var stuck = _loadWaited > LoadGiveUpSeconds;
        if ((dropped || stuck) && !_standardSuit && _ownsSlot && LoadStandardSuit())
        {
            return;
        }

        if (dropped && !_ownsSlot)
        {
            LogSlot($"{_name} was taken out of the story's slot while loading as a sidekick.");
            CancelLoad();
            Call.Report(Outcome.Failed("CAN'T LOAD"));
            return;
        }

        if (stuck)
        {
            LogSlot($"{_name} took too long to load as a sidekick.");
            CancelLoad();
            Call.Report(Outcome.Failed("TIMED OUT"));
        }
    }

    // The character is in. They come in once the player is on foot and out of any scene.
    static void Arrive()
    {
        if (World.Player is not { } pawn || World.Controller is not { } controller)
        {
            return;
        }

        if (controller.Pawn is not { } driven || driven.Ptr != pawn.Ptr || pawn.Health <= 0 || Remesh.Busy || Switcher.InScene(controller))
        {
            return;
        }

        Call.Report(Spawn());
    }

    // The slot holds this character with their data and suit both in.
    static bool Ready(RGameRI game, int index)
    {
        ref var slot = ref Loaded.Slot(game, index);
        return slot.CharacterName.Index != 0 && slot.CharacterName.ToString().Equals(_name, StringComparison.OrdinalIgnoreCase)
            && slot.CharacterData is { IsValid: true } && slot.MeshData is { IsValid: true };
    }

    static bool Dropped() => _slot >= 0 && Game.TryGetGameRI(out var game) && Loaded.Slot(game, _slot).CharacterName.Index == 0;

    // The game's loader with its raw name switch on loads the character's standard suit instead of
    // the picked one. It reloads every slot still waiting for data, so it waits until only ours is.
    static bool LoadStandardSuit()
    {
        if (_loader is not { IsValid: true } loader || _slot < 0 || !Game.TryGetGameRI(out var game))
        {
            return false;
        }

        for (var i = 0; i < Loaded.Slots; i++)
        {
            ref var other = ref Loaded.Slot(game, i);
            if (i != _slot && other.CharacterName.Index != 0 && other.CharacterData is not { IsValid: true })
            {
                return false;
            }
        }

        ref var slot = ref Loaded.Slot(game, _slot);
        if (slot.CharacterName.Index != 0 && !slot.CharacterName.ToString().Equals(_name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        LogSlot($"{_name} did not load in their picked suit. Trying the standard suit.");
        slot.CharacterName = new FName(_name);
        slot.DamageLevel = 0;
        slot.CharacterData = null!;
        slot.MeshData = null!;
        slot.LoadingObject = loader;
        _standardSuit = true;
        _loadWaited = 0;
        game.ReallyLoadPlayableCharacters(false, true);
        return true;
    }

    static void LogSlot(string what)
    {
        if (_slot < 0 || !Game.TryGetGameRI(out var game))
        {
            Log.Info(what);
            return;
        }

        ref var slot = ref Loaded.Slot(game, _slot);
        var name = slot.CharacterName.Index != 0 ? slot.CharacterName.ToString() : "nobody";
        var suit = slot.MeshName.ToString() is { Length: > 0 } mesh ? mesh : "none";
        var data = slot.CharacterData is { IsValid: true } ? "in" : "missing";
        var look = slot.MeshData is { IsValid: true } ? "in" : "missing";
        Log.Info($"{what} Slot {_slot} has {name} in suit {suit}. Character {data}, suit {look}.");
    }

    static void CancelLoad()
    {
        Unhook();
        DropLoader();
        ClearSlot();
        Forget();
    }

    // A slot can still name our loader after the game gave up on it, and the loader is about to go.
    static void Unhook()
    {
        if (_loader is { IsValid: true } loader && _slot >= 0 && Game.TryGetGameRI(out var game))
        {
            ref var slot = ref Loaded.Slot(game, _slot);
            if (slot.LoadingObject is { } waiting && waiting.Ptr == loader.Ptr)
            {
                slot.LoadingObject = null!;
            }
        }
    }

    static void DropLoader()
    {
        _loader = null;
        Borrowed.Let(Lender);
    }

    // What the story's own spawn-partner action does once its character has loaded.
    static Outcome Spawn()
    {
        if (World.Player is not { } pawn || World.Controller is not { } controller || !Game.TryGetGameInfo(out var info) || !Game.TryGetGameRI(out var game)
            || _caller is not { IsValid: true } caller || pawn.Ptr != caller.Ptr || controller.Pawn is not { } driven || driven.Ptr != pawn.Ptr)
        {
            return GiveUp("YOU WERE BUSY");
        }

        if (info.PlayerSwitchPawn is { IsValid: true, bDeleteMe: false })
        {
            return GiveUp("PARTNER IS HERE");
        }

        var data = _slot >= 0 ? Loaded.Slot(game, _slot).CharacterData : null;
        if (data is not { IsValid: true } || data.PlayerClass is not { IsValid: true } kind)
        {
            Log.Info($"{_name} did not load as a sidekick.");
            return GiveUp("CAN'T LOAD");
        }

        var right = Flat(pawn.Rotation.Yaw + 90f);
        var at = (Beside(pawn, out var floor) ? floor : pawn.Location + right * (ArriveBeside * 0.66f) - Vector3.UnitZ * pawn.GetCollisionHeight())
            + new Vector3(0f, 0f, pawn.GetCollisionHeight() + 5f);
        var facing = new Rotator(0f, pawn.Rotation.Yaw, 0f);
        if (Game.SpawnActor(kind, default, at, facing) is not RPawnPlayer body)
        {
            return GiveUp("NO ROOM HERE");
        }

        if (Game.SpawnActor(RRobinBotController.StaticClass(), default, at, facing, null, body) is not RRobinBotController bot)
        {
            body.Destroy();
            return GiveUp("FAILED");
        }

        body.RiddlerCharData = data;
        info.PlayerSwitchPawn = body;
        body.SharedBotController = bot;
        pawn.SharedBotController = bot;
        bot.Possess(body, false);
        body.AddDefaultInventory();

        _body = body;
        _bot = bot;
        _arriving = false;
        _upkeepAt = 0f;
        _stuckSeenAt = -100f;
        _poseSince = World.Now;
        Tune(bot);
        Keep();
        Log.Info($"{_name} joined as a sidekick from slot {_slot}. {ObjectTable.Headroom} objects are free.");
        return Outcome.Done(_standardSuit ? "ARRIVED IN STANDARD SUIT" : "ARRIVED");
    }

    static Outcome GiveUp(string why)
    {
        ClearSlot();
        Forget();
        return Outcome.Failed(why);
    }

    // The first open floor at the player's side, then behind and in front.
    static bool Beside(RPawnPlayer pawn, out Vector3 floor)
    {
        foreach (var turn in new[] { 90f, -90f, 180f, 0f })
        {
            if (Standable(pawn, pawn.Location + Flat(pawn.Rotation.Yaw + turn) * ArriveBeside, out floor))
            {
                return true;
            }
        }

        floor = default;
        return false;
    }

    static Vector3 Flat(float yaw) => new Rotator(0f, yaw, 0f).ToDirection();

    // Dry ground the pawn could stand on, about level with it and with nothing in the way.
    static bool Standable(RPawnPlayer from, Vector3 spot, out Vector3 floor)
    {
        floor = default;
        if (!from.FastTrace(spot, from.Location, Vector3.Zero, false) || !Teleport.Floor(from, spot, spot.Z + 150f, spot.Z - 600f, out floor) || !Teleport.Dry(floor))
        {
            return false;
        }

        var feet = from.Location.Z - from.GetCollisionHeight();
        return MathF.Abs(floor.Z - feet) < FloorReach;
    }

    // Stands them on a floor point with nowhere left to walk to.
    static void Place(RPawnPlayer follower, Vector3 floor, float yaw)
    {
        follower.SetLocationIgnoringCollision(floor + new Vector3(0f, 0f, follower.GetCollisionHeight() + 5f));
        follower.SetRotation(new Rotator(0f, yaw, 0f));
        follower.Velocity = Vector3.Zero;
        follower.StopMoveTo();
        _bot?.ClearNavigation();
        _farSince = -1f;
        _stuckSeenAt = -100f;
    }

    // How far back the partner AI stops, and from how far it walks and then runs.
    static void Tune(RRobinBotController bot)
    {
        var stand = (float)Distance.Value * Metre;
        if (bot.StandBehindDist == stand)
        {
            return;
        }

        bot.StandBehindDist = stand;
        bot.WalkBehindDist = stand + WalkPast;
        bot.RunBehindDist = stand + RunPast;
    }

    // The partner AI only attacks inside one of the arenas the story's missions place, and the
    // game only counts towards a dual takedown for a player standing in one. Ours has no size
    // and is simply handed to both bodies. An arena the story placed is left on.
    static void Arena(bool wanted)
    {
        Hand(_body);
        Hand(_caller);

        void Hand(RPawnPlayer? pawn)
        {
            if (pawn is not { IsValid: true, bDeleteMe: false })
            {
                return;
            }

            var has = pawn.CombatBotVolume is { IsValid: true, bDeleteMe: false } volume ? volume : null;
            if (!wanted)
            {
                if (has is not null && _arena is { } mine && has.Ptr == mine.Ptr)
                {
                    pawn.CombatBotVolume = null!;
                }

                return;
            }

            if (has is not null)
            {
                return;
            }

            if (_arena is not { IsValid: true, bDeleteMe: false })
            {
                _arena = Game.SpawnActor(RCombatBotVolume.StaticClass(), default, pawn.Location) as RCombatBotVolume;
                if (_arena is null)
                {
                    return;
                }

                _arena.SetHidden(true);
                _arena.SetCollision(false, false, false);
                Keep();
            }

            pawn.CombatBotVolume = _arena;
        }
    }

    // Crouches and stands with the player, after the game's own short wait. The partner AI
    // won't attack while crouched.
    static void Sneak(RPlayerController controller, RPawnPlayer follower)
    {
        var bot = _bot!;
        var low = Sneaks.On && Follows.On && Same(controller.GetStateName(), StealthMove);
        if (low == bot.bCrouched || World.Now < _poseSince)
        {
            _poseSince = World.Now;
            return;
        }

        if (World.Now - _poseSince < bot.TimeNeededForCrouchChange || follower.CurrentSpecialMove is not null || follower.CombatMove is not null)
        {
            return;
        }

        Stance(bot, follower, low);
        _poseSince = World.Now;
    }

    static void Stance(RRobinBotController bot, RPawnPlayer follower, bool low)
    {
        if (low)
        {
            bot.StopAllLookAtPlayer();
        }

        bot.bCrouched = low;
        bot.bInMovementStand = !low;
        follower.ChangePose(low ? Crouching : StandingBot, Relaxed, default, false, default, default, default, 1f);
    }

    static void CatchUp(RPlayerController controller, RPawnPlayer follower)
    {
        if (controller.Pawn is not RPawnPlayer player || player.Physics != Actor.EPhysics.PHYS_Walking || follower.CurrentSpecialMove is not null
            || Switcher.InScene(controller))
        {
            _farSince = -1f;
            return;
        }

        // The story keeps its partners out of some rooms. Ours waits outside them too.
        if (_bot!.BotExclusionVolume is { IsValid: true, bDeleteMe: false } keptOut && keptOut.EncompassesPoint(player.Location))
        {
            _farSince = -1f;
            return;
        }

        var gap = Vector3.DistanceSquared(follower.Location, player.Location);
        var stuck = Stuck(controller, follower, gap);
        if (gap < CatchUpRange * CatchUpRange && !stuck)
        {
            _farSince = -1f;
            return;
        }

        // Game time starts over with a new world.
        if (_farSince < 0f || World.Now < _farSince)
        {
            _farSince = World.Now;
        }

        controller.GetPlayerViewPoint(out var eye, out var view);
        var waited = World.Now - _farSince;
        if (waited < CatchUpWaitSeconds || (InView(player, eye, view, follower.Location) && waited < CatchUpAnywaySeconds))
        {
            return;
        }

        foreach (var turn in CatchUpTurns)
        {
            if (Standable(player, player.Location + Flat(view.Yaw + turn) * CatchUpBehind, out var floor) && !InView(player, eye, view, floor + Vector3.UnitZ * 60f))
            {
                Place(follower, floor, player.Rotation.Yaw);
                return;
            }
        }
    }

    // True once they have stood still for a while with the player out of reach. The partner AI
    // has no grapple and climbs nothing, so a roof is enough. Asked every upkeep while they
    // should be following, and a gap in the asking starts the count again.
    static bool Stuck(RPlayerController controller, RPawnPlayer follower, float gapSquared)
    {
        var bot = _bot!;
        var reach = bot.StandBehindDist + StuckShort;
        var asked = World.Now >= _stuckSeenAt && World.Now - _stuckSeenAt <= UpkeepSeconds * 3f;
        _stuckSeenAt = World.Now;
        if (!asked || gapSquared <= reach * reach || bot.bWasInCombat || !Following(controller)
            || Vector3.DistanceSquared(follower.Location, _stoodAt) > StuckStep * StuckStep)
        {
            _stoodAt = follower.Location;
            _stoodSince = World.Now;
            return false;
        }

        return World.Now - _stoodSince >= StuckAfter;
    }

    static bool InView(RPawnPlayer player, Vector3 eye, Rotator view, Vector3 at)
    {
        var to = at - eye;
        if (to.LengthSquared() < 1f)
        {
            return false;
        }

        return Vector3.Dot(Vector3.Normalize(to), view.ToDirection()) >= ViewCone && player.FastTrace(at, eye, Vector3.Zero, false);
    }

    // Runs off, and is gone once out of sight or far enough away.
    static void Leave(RPlayerController controller, RPawnPlayer follower)
    {
        var caller = _caller!;
        if (World.Now < _leaveSince)
        {
            _leaveSince = World.Now;
        }

        controller.GetPlayerViewPoint(out var eye, out var view);
        var far = Vector3.DistanceSquared(follower.Location, caller.Location) > GoneRange * GoneRange;
        if (far || World.Now - _leaveSince > LeaveGiveUpSeconds || !InView(caller, eye, view, follower.Location + Vector3.UnitZ * 60f))
        {
            Remove();
            Dismiss.Report(Outcome.Done("GONE"));
            return;
        }

        if (follower.CurrentSpecialMove is not null || follower.Physics != Actor.EPhysics.PHYS_Walking)
        {
            return;
        }

        var away = follower.Location - caller.Location;
        away.Z = 0f;
        away = away.LengthSquared() > 1f ? Vector3.Normalize(away) : Flat(follower.Rotation.Yaw);
        follower.SetMovementSpeed(Pace.MS_Run, 1f, default);
        follower.MoveTo(follower.Location + away * LeaveRun);
    }

    // The game's own clean-up for a spawned partner, in its order. The switch link goes first,
    // then the AI, the body, and last the slot the character was loaded into.
    static void Remove()
    {
        var body = _body is { IsValid: true, bDeleteMe: false } ? _body : null;
        var bot = _bot is { IsValid: true, bDeleteMe: false } ? _bot : null;
        var caller = _caller is { IsValid: true, bDeleteMe: false } ? _caller : null;
        var arena = _arena is { IsValid: true, bDeleteMe: false } ? _arena : null;
        var played = Game.TryGetPlayerController(out var controller) ? controller.Pawn?.Ptr ?? 0 : 0;

        if (Game.TryGetGameInfo(out var info) && info.PlayerSwitchPawn is { } linked && (linked.Ptr == body?.Ptr || linked.Ptr == caller?.Ptr))
        {
            info.PlayerSwitchPawn = null!;
        }

        foreach (var pawn in new[] { body, caller })
        {
            if (pawn is null)
            {
                continue;
            }

            if (bot is not null && pawn.SharedBotController is { } shared && shared.Ptr == bot.Ptr)
            {
                pawn.SharedBotController = null!;
            }

            if (arena is not null && pawn.CombatBotVolume is { } volume && volume.Ptr == arena.Ptr)
            {
                pawn.CombatBotVolume = null!;
            }
        }

        bot?.Destroy();
        if (body is not null && body.Ptr != played)
        {
            body.Destroy();
        }

        arena?.Destroy();
        ClearSlot();
        Forget();
    }

    // Empties the slot the sidekick was loaded into, unless the story had them loaded already
    // or someone still in the world is that character.
    static void ClearSlot()
    {
        if (!_ownsSlot || _slot <= 0 || !Game.TryGetGameRI(out var game))
        {
            return;
        }

        ref var slot = ref Loaded.Slot(game, _slot);
        if (!slot.CharacterName.ToString().Equals(_name, StringComparison.OrdinalIgnoreCase) || Worn(game, World.Info?.PlayerSwitchPawn)
            || Worn(game, World.Controller?.Pawn) || Worn(game, World.Controller?.OriginalPawn))
        {
            return;
        }

        slot.CharacterName = default;
        slot.CharacterData = null!;
        slot.MeshData = null!;
        slot.DamageLevel = 0;
        slot.LoadingObject = null!;
        game.ReallyLoadPlayableCharacters(false, false);
    }

    static bool Worn(RGameRI game, Pawn? pawn) =>
        pawn is RPawnPlayer { IsValid: true, bDeleteMe: false } player && Loaded.IndexOf(game, player) == _slot;

    static void Forget()
    {
        _body = null;
        _caller = null;
        _bot = null;
        _arena = null;
        _name = "";
        _slot = -1;
        _ownsSlot = false;
        _arriving = false;
        _leaving = false;
        _farSince = -1f;
        _lookedAt = -100f;
        Keep();
        Catalog.MenuOpening();
    }

    // A hot reload starts this class over while the sidekick is still in the world.
    static void Keep()
    {
        Survive.Set(Survive.SidekickName, _name);
        Survive.Set(Survive.SidekickSlot, _slot);
        Survive.Set(Survive.SidekickOwnsSlot, _ownsSlot);
        Survive.Set(Survive.SidekickLeaving, _leaving);
        Survive.Set(Survive.SidekickBody, (long)(_body?.Ptr ?? 0));
        Survive.Set(Survive.SidekickBot, (long)(_bot?.Ptr ?? 0));
        Survive.Set(Survive.SidekickArena, (long)(_arena?.Ptr ?? 0));
    }

    static void Adopt()
    {
        if (_adopted)
        {
            return;
        }

        _adopted = true;
        var botAt = (nint)Survive.Get(Survive.SidekickBot, 0L);
        var bodyAt = (nint)Survive.Get(Survive.SidekickBody, 0L);
        var arenaAt = (nint)Survive.Get(Survive.SidekickArena, 0L);
        if (botAt == 0 || bodyAt == 0 || !Game.TryGetPlayerController(out var controller) || !Game.TryGetGameInfo(out var info))
        {
            return;
        }

        // The two bodies are the one being played and the game's switch partner.
        var bot = Game.FindObjects<RRobinBotController>().FirstOrDefault(found => found.IsValid && found.Ptr == botAt);
        var played = controller.Pawn as RPawnPlayer ?? controller.OriginalPawn;
        var partner = info.PlayerSwitchPawn as RPawnPlayer;
        if (bot is null || played is null || partner is null || (played.Ptr != bodyAt && partner.Ptr != bodyAt))
        {
            return;
        }

        _bot = bot;
        _body = played.Ptr == bodyAt ? played : partner;
        _caller = played.Ptr == bodyAt ? partner : played;
        _arena = arenaAt != 0 ? Game.FindObjects<RCombatBotVolume>().FirstOrDefault(found => found.IsValid && found.Ptr == arenaAt) : null;
        _name = Survive.Get(Survive.SidekickName, "");
        _slot = Survive.Get(Survive.SidekickSlot, -1);
        _ownsSlot = Survive.Get(Survive.SidekickOwnsSlot, false);
        _leaving = Survive.Get(Survive.SidekickLeaving, false);
        _leaveSince = World.Now;
        _poseSince = World.Now;
    }

    static bool Same(FName one, FName other) => one.Index == other.Index && one.Number == other.Number;

    // The states the story's partners follow in.
    static bool Following(RPlayerController controller)
    {
        var state = controller.GetStateName();
        foreach (var follow in FollowStates)
        {
            if (Same(state, follow))
            {
                return true;
            }
        }

        return false;
    }

    static bool Ours(RRobinBotController bot)
    {
        Adopt();
        return _bot is { } mine && mine.Ptr == bot.Ptr;
    }

    // The story's partners only follow while a mission flag says so. Ours follows on its own
    // option, so no story flag is touched.
    [Redirect(typeof(RRobinBotController), nameof(RRobinBotController.ShouldPathToPlayer))]
    static bool ShouldPathToPlayer(RRobinBotController self)
    {
        try
        {
            if (Ours(self))
            {
                return Follows.On && !_leaving && Game.TryGetPlayerController(out var controller) && Following(controller);
            }
        }
        catch (Exception error)
        {
            Log.Error("sidekick.follow", "The sidekick's follow rule failed", error);
        }

        return Redirects.RunOriginal<bool>();
    }

    // The game's crouch rule reads the same mission flag and would stand ours straight back up.
    // Ours crouches on its own option.
    [Redirect(typeof(RRobinBotController), nameof(RRobinBotController.CheckForCrouching))]
    static void CheckForCrouching(RRobinBotController self, float deltaTime)
    {
        try
        {
            if (Ours(self))
            {
                return;
            }
        }
        catch (Exception error)
        {
            Log.Error("sidekick.crouch", "The sidekick's crouch rule failed", error);
        }

        Redirects.RunOriginal();
    }

    // The call Batmobile button asks for an unforced swap whenever there is a switch partner.
    // The story's swaps and DISMISS are forced, and go through.
    [Redirect(typeof(RPlayerController), nameof(RPlayerController.SwitchPlayer))]
    static bool SwitchPlayer(RPlayerController self, bool force, bool reverseLook, bool lockCamera, bool tryDualplayCombatMove, bool slowMoSwitch, bool forSwitchPlayerAction)
    {
        try
        {
            Adopt();
            if (!force && !_trading && Present && (_leaving || !ButtonSwaps.On))
            {
                return false;
            }
        }
        catch (Exception error)
        {
            Log.Error("sidekick.swap", "The sidekick's swap rule failed", error);
        }

        return Redirects.RunOriginal<bool>();
    }

    // The loader has no use for this call itself. It only tells us the character is in.
    [Redirect(typeof(RSeqAct_SpawnerBase), nameof(RSeqAct_SpawnerBase.OnDataLoaded))]
    static void OnDataLoaded(RSeqAct_SpawnerBase self)
    {
        if (_loader is { } loader && loader.Ptr == self.Ptr)
        {
            _loaded = true;
            return;
        }

        Redirects.RunOriginal();
    }
}
