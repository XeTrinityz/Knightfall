#nullable enable

using System.Diagnostics;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using Pace = BmSDK.Engine.RAutomaticTransitions.EMovementSpeed;

namespace Knightfall;

// PLAYER > CHARACTER, the SIDEKICK rows. A sidekick is a partner made the way the story makes
// Robin and Nightwing. The game loads the character into a spare character slot in the
// background and says when it is in. Then a body is spawned beside the player, given the story's
// partner AI and named as the game's switch partner. From there the game's own swap, team
// takedowns and team moves work with it.
static class Sidekick
{
    const string Lender = "sidekick";
    const double LoadGiveUpSeconds = 20;
    const float UpkeepSeconds = 0.25f;

    // 40 m. Left further behind than this and out of sight, the sidekick rejoins behind you.
    const float CatchUpRange = 4000f;
    const float CatchUpBehind = 600f;
    const float CatchUpWaitSeconds = 1.5f;

    // Still that far behind after this long, the sidekick rejoins even on screen.
    const float CatchUpAnywaySeconds = 8f;

    const float ArriveBeside = 180f;
    const float LeaveRun = 2500f;
    const float GoneRange = 1500f;
    const float LeaveGiveUpSeconds = 8f;
    const float FloorReach = 200f;
    const float WalkableSlope = 0.7f;
    const float ViewCone = 0.4f;

    // The story's partner versions, which the partner AI was made for.
    static readonly (string Name, string Label)[] Known =
    {
        ("Playable_Robin", "ROBIN"),
        ("Playable_Nightwing", "NIGHTWING"),
        ("Playable_Catwoman", "CATWOMAN"),
        ("Playable_DLCBatgirl", "BATGIRL"),
    };

    static readonly string[] FollowStates = { "WalkingMode", "RunningMode", "StealthMoveMode" };

    static RSeqAct_SwitchDualPlayerCharacter? _loader;
    static bool _loaded;
    static bool _standardSuit;
    static long _loadSince;

    static RPawnPlayer? _body;
    static RPawnPlayer? _caller;
    static RRobinBotController? _bot;
    static RCombatBotVolume? _arena;
    static string _name = "";
    static int _slot = -1;
    static bool _ownsSlot;

    static bool _leaving;
    static float _leaveSince;
    static float _farSince = -1f;
    static float _upkeepAt;
    static bool _adopted;

    public static readonly Choice Who = new(
        "player.sidekick.who", "SIDEKICK",
        "Who CALL SIDEKICK brings in. They arrive in the suit you have picked for them.",
        Installed())
    {
        Preference = true,
    };

    public static readonly Command Call = new(
        "player.sidekick.call", "CALL SIDEKICK",
        "Brings your sidekick in beside you. They follow you, join fights and do team takedowns. The call Batmobile button swaps between you, as in the story.",
        Summon);

    public static readonly Command Swap = new(
        "player.sidekick.swap", "SWAP WITH SIDEKICK",
        "Takes control of your sidekick, or hands it back. Your save still records the story's character.",
        Trade);

    public static readonly Command Dismiss = new(
        "player.sidekick.dismiss", "DISMISS SIDEKICK",
        "Your sidekick heads off and is gone once out of sight. If you are playing as them you are put back first.",
        SendAway);

    public static readonly Toggle Follows = new(
        "player.sidekick.follows", "SIDEKICK FOLLOWS YOU",
        "Off makes your sidekick wait where they are until you turn it back on.", on: true)
    {
        Preference = true,
    };

    public static readonly Toggle CatchesUp = new(
        "player.sidekick.catches_up", "SIDEKICK CATCHES UP",
        "When you leave your sidekick more than 40 m behind, they rejoin you out of sight, as the story's partners do.", on: true)
    {
        Preference = true,
    };

    static bool Loading => _loader is not null;

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

    static Outcome Summon()
    {
        Adopt();
        var name = Who.CurrentId;
        if (name.Length == 0)
        {
            return Outcome.Failed("NONE INSTALLED");
        }

        if (Loading)
        {
            return Outcome.Unchanged("ON THE WAY");
        }

        if (_body is not null)
        {
            return Outcome.Unchanged(_leaving ? "STILL LEAVING" : "ALREADY HERE");
        }

        if (World.Player is not { } pawn || World.Controller is not { } controller || !Game.TryGetGameInfo(out var info) || !Game.TryGetGameRI(out var game)
            || !Game.TryGetWorldInfo(out var world))
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

        if (info.IsChallengeMode())
        {
            return Outcome.Failed("NOT IN CHALLENGES");
        }

        if (Switcher.InScene(controller))
        {
            return Outcome.Failed("IN A SCENE");
        }

        if (controller.IsInCombat(false))
        {
            return Outcome.Failed("FINISH THE FIGHT");
        }

        // The story's own partner owns the switch. Taking it would break the mission.
        if (info.PlayerSwitchPawn is { IsValid: true, bDeleteMe: false })
        {
            return Outcome.Failed("PARTNER IS HERE");
        }

        if (game.PlayerCharacters_0.CharacterName.ToString().Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return Outcome.Failed("THAT IS YOU");
        }

        if (world.GetGameSequence() is not { IsValid: true } sequence)
        {
            return Outcome.Failed("NOT HERE");
        }

        var character = new FName(name);
        var index = Loaded.IndexOf(game, character);
        var already = index >= 0;
        _loaded = false;
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
        _loadSince = Stopwatch.GetTimestamp();

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
        if (!Present || _leaving)
        {
            return Outcome.Failed(Loading ? "ON THE WAY" : "NO SIDEKICK");
        }

        if (World.Controller is not { } controller || controller.Pawn is not RPawnPlayer)
        {
            return Outcome.Failed(World.Driving is not null ? "GET OUT FIRST" : "NOT NOW");
        }

        // Not forced, so the game's own rules apply. It won't swap mid-move, while aiming or
        // when the story has locked it.
        if (!controller.SwitchPlayer(false, false, false, false, true, false))
        {
            return Outcome.Failed("NOT NOW");
        }

        World.Tick();
        Catalog.MenuOpening();
        return Outcome.Done("SWAPPED");
    }

    static Outcome SendAway()
    {
        Adopt();
        if (Loading)
        {
            CancelLoad();
            return Outcome.Done("CANCELLED");
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
        return Outcome.Done("HEADING OFF");
    }

    public static void Tick()
    {
        Adopt();
        if (Loading)
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
            Remove();
            return;
        }

        if (World.Now >= _upkeepAt - UpkeepSeconds && World.Now < _upkeepAt)
        {
            return;
        }

        _upkeepAt = World.Now + UpkeepSeconds;
        if (World.Controller is not { } controller || _bot!.BotPawn is not { IsValid: true, bDeleteMe: false } follower)
        {
            return;
        }

        if (_leaving)
        {
            Leave(controller, follower);
            return;
        }

        AllowFights(follower);
        if (Follows.On && CatchesUp.On)
        {
            CatchUp(controller, follower);
        }
    }

    public static void Release() => Keep();

    static void TickLoad()
    {
        // The game's call to say it is done doesn't reach us when it comes from inside our own call
        // into the loader, as it does for packages already in memory. So the slot is checked too.
        if (_loaded || (_slot >= 0 && Game.TryGetGameRI(out var game) && Ready(game, _slot)))
        {
            _loaded = false;
            Unhook();
            DropLoader();
            Call.Report(Spawn());
            return;
        }

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
        var stuck = (Stopwatch.GetTimestamp() - _loadSince) / (double)Stopwatch.Frequency > LoadGiveUpSeconds;
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
        if (_loader is not { } loader || _slot < 0 || !Game.TryGetGameRI(out var game))
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
        _loadSince = Stopwatch.GetTimestamp();
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
        if (_loader is { } loader && _slot >= 0 && Game.TryGetGameRI(out var game))
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

        var at = Beside(pawn);
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
        _upkeepAt = 0f;
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

    // The first open spot at the player's side, then behind and in front.
    static Vector3 Beside(RPawnPlayer pawn)
    {
        var forward = Facing(pawn.Rotation.Yaw);
        var right = new Vector3(-forward.Y, forward.X, 0f);
        foreach (var side in new[] { right, -right, -forward, forward })
        {
            if (Floor(pawn, pawn.Location + side * ArriveBeside, out var floor))
            {
                return floor + new Vector3(0f, 0f, pawn.GetCollisionHeight() + 5f);
            }
        }

        return pawn.Location + right * (ArriveBeside * 0.66f);
    }

    static Vector3 Facing(float yaw)
    {
        var turn = yaw * MathF.PI / 180f;
        return new Vector3(MathF.Cos(turn), MathF.Sin(turn), 0f);
    }

    // Ground the pawn could stand on, about level with it and with nothing in the way.
    static bool Floor(RPawnPlayer from, Vector3 spot, out Vector3 floor)
    {
        floor = default;
        if (!from.FastTrace(spot, from.Location, Vector3.Zero, false))
        {
            return false;
        }

        var hit = from.Trace(out floor, out var normal, spot - Vector3.UnitZ * 600f, spot + Vector3.UnitZ * 150f, Component.ECollisionFilter.COF_TraceWorld, Vector3.Zero, out _, 0);
        if (hit is null || normal.Z < WalkableSlope)
        {
            return false;
        }

        var feet = from.Location.Z - from.GetCollisionHeight();
        return MathF.Abs(floor.Z - feet) < FloorReach;
    }

    // The partner AI only attacks inside one of the arenas the story's missions place. This one
    // has no size and is simply handed to whichever body the AI is driving.
    static void AllowFights(RPawnPlayer follower)
    {
        if (follower.CombatBotVolume is { IsValid: true, bDeleteMe: false })
        {
            return;
        }

        if (_arena is not { IsValid: true, bDeleteMe: false })
        {
            _arena = Game.SpawnActor(RCombatBotVolume.StaticClass(), default, follower.Location) as RCombatBotVolume;
            if (_arena is null)
            {
                return;
            }

            _arena.SetHidden(true);
            _arena.SetCollision(false, false, false);
            Keep();
        }

        follower.CombatBotVolume = _arena;
    }

    static void CatchUp(RPlayerController controller, RPawnPlayer follower)
    {
        if (controller.Pawn is not RPawnPlayer player || player.Physics != Actor.EPhysics.PHYS_Walking || follower.CurrentSpecialMove is not null
            || Vector3.DistanceSquared(follower.Location, player.Location) < CatchUpRange * CatchUpRange)
        {
            _farSince = -1f;
            return;
        }

        // Game time starts over with a new world.
        if (_farSince < 0f || World.Now < _farSince)
        {
            _farSince = World.Now;
        }

        var waited = World.Now - _farSince;
        if (waited < CatchUpWaitSeconds || (InView(controller, player, follower.Location) && waited < CatchUpAnywaySeconds))
        {
            return;
        }

        controller.GetPlayerViewPoint(out _, out var view);
        foreach (var turn in new[] { 180f, 150f, 210f, 120f, 240f })
        {
            if (!Floor(player, player.Location + Facing(view.Yaw + turn) * CatchUpBehind, out var floor) || InView(controller, player, floor + Vector3.UnitZ * 60f))
            {
                continue;
            }

            follower.SetLocationIgnoringCollision(floor + new Vector3(0f, 0f, follower.GetCollisionHeight() + 5f));
            follower.SetRotation(new Rotator(0f, player.Rotation.Yaw, 0f));
            follower.Velocity = Vector3.Zero;
            follower.StopMoveTo();
            _bot?.ClearNavigation();
            _farSince = -1f;
            return;
        }
    }

    static bool InView(RPlayerController controller, RPawnPlayer player, Vector3 at)
    {
        controller.GetPlayerViewPoint(out var eye, out var view);
        var to = at - eye;
        if (to.LengthSquared() < 1f)
        {
            return false;
        }

        var pitch = view.Pitch * MathF.PI / 180f;
        var flat = Facing(view.Yaw);
        var looking = new Vector3(flat.X * MathF.Cos(pitch), flat.Y * MathF.Cos(pitch), MathF.Sin(pitch));
        return Vector3.Dot(Vector3.Normalize(to), looking) >= ViewCone && player.FastTrace(at, eye, Vector3.Zero, false);
    }

    // Runs off, and is gone once out of sight or far enough away.
    static void Leave(RPlayerController controller, RPawnPlayer follower)
    {
        var caller = _caller!;
        if (World.Now < _leaveSince)
        {
            _leaveSince = World.Now;
        }

        var far = Vector3.DistanceSquared(follower.Location, caller.Location) > GoneRange * GoneRange;
        if (far || World.Now - _leaveSince > LeaveGiveUpSeconds || !InView(controller, caller, follower.Location + Vector3.UnitZ * 60f))
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
        away = away.LengthSquared() > 1f ? Vector3.Normalize(away) : Facing(follower.Rotation.Yaw);
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

    // Empties the slot the sidekick was loaded into, unless the story had them loaded already.
    static void ClearSlot()
    {
        if (!_ownsSlot || _slot <= 0 || !Game.TryGetGameRI(out var game))
        {
            return;
        }

        ref var slot = ref Loaded.Slot(game, _slot);
        if (!slot.CharacterName.ToString().Equals(_name, StringComparison.OrdinalIgnoreCase))
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

    static void Forget()
    {
        _body = null;
        _caller = null;
        _bot = null;
        _arena = null;
        _name = "";
        _slot = -1;
        _ownsSlot = false;
        _leaving = false;
        _farSince = -1f;
        Keep();
        Catalog.MenuOpening();
    }

    // A hot reload starts this class over while the sidekick is still in the world.
    static void Keep()
    {
        Survive.Set(Survive.SidekickName, _name);
        Survive.Set(Survive.SidekickSlot, _slot);
        Survive.Set(Survive.SidekickOwnsSlot, _ownsSlot);
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
        var played = controller.Pawn as RPawnPlayer;
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
    }

    static bool Ours(RRobinBotController bot) => _bot is { } mine && mine.Ptr == bot.Ptr;

    // The story's partners only follow while a mission flag says so. Ours follows on its own
    // option, so no story flag is touched.
    [Redirect(typeof(RRobinBotController), nameof(RRobinBotController.ShouldPathToPlayer))]
    static bool ShouldPathToPlayer(RRobinBotController self)
    {
        try
        {
            Adopt();
            if (Ours(self))
            {
                return Follows.On && !_leaving && Game.TryGetPlayerController(out var controller)
                    && Array.IndexOf(FollowStates, controller.GetStateName().ToString()) >= 0;
            }
        }
        catch (Exception error)
        {
            Log.Error("sidekick.follow", "The sidekick's follow rule failed", error);
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
