#nullable enable

using System.Diagnostics;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using Channel = BmSDK.Engine.PrimitiveComponent.ERBCollisionChannel;
using Pace = BmSDK.Engine.RAutomaticTransitions.EMovementSpeed;

namespace Knightfall;

// PLAYER > SIDEKICK. A sidekick is a partner made the way the story makes Robin and Nightwing.
// The game loads the character into a spare character slot in the background and says when it
// is in. Then a body is spawned beside the player, given the story's partner AI and named as the
// game's switch partner. From there the game's own swap, dual takedowns and team moves work
// with it.
static partial class Sidekick
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

    // A fight that starts with them further off than this would be over before they ran to it,
    // so they rejoin out of sight straight away.
    const float FightRange = 2000f;
    const float FightWaitSeconds = 0.75f;

    // Falling this far below a player who stands on solid ground, they have lost their floor.
    const float FallCatch = 800f;

    // Behind a player on the move the walk and run gaps close right up, so they run when you do.
    const float BriskWalkPast = 60f;
    const float BriskRunPast = 320f;
    const float BriskSpeed = 350f;
    const float BriskHoldSeconds = 1f;

    // Past the gap they run from, the run gets this much faster over this much more distance.
    // Asking these bodies to sprint gains nothing, so the run itself is sped up.
    const float HurryMost = 0.35f;
    const float HurryOver = 600f;

    // How near a point on their path counts as reached. The game's own figure.
    const float PathArrive = 50f;

    const double RejoinEverySeconds = 1;
    const double RejoinAfterSeconds = 3;
    const int RejoinTries = 5;
    const string Section = "player.sidekick";
    const string OutKey = "out";
    const string Coming = "ON THE WAY";

    // Batman's data ships inside the game's own packages, so he has no file to look for.
    const string Batman = "Playable_Batman";

    // The story's partner versions come first, which the partner AI was made for. Anyone else
    // runs on the same AI. Calling the character you are playing brings a twin.
    static readonly (string Name, string Label)[] Known =
    {
        ("Playable_Robin", "ROBIN"),
        ("Playable_Nightwing", "NIGHTWING"),
        ("Playable_Catwoman", "CATWOMAN"),
        ("Playable_DLCBatgirl", "BATGIRL"),
        (Batman, "BATMAN"),
        ("Playable_Azrael", "AZRAEL"),
        ("Playable_DLCRedHood", "RED HOOD"),
        ("Playable_DLCHarleyQuinn", "HARLEY QUINN"),
    };

    static readonly FName StealthMove = new("StealthMoveMode");
    static readonly FName Running = new("RunningMode");
    static readonly FName[] FollowStates = { new("WalkingMode"), Running, StealthMove };

    // A roll, a vault or a climb over something. The player is still going somewhere.
    static readonly FName[] MoveStates = { new("CombatMoveControlled"), new("SpecialMove") };
    static readonly FName JumpDown = new("JumpOffLedgeState");
    static readonly FName Crouching = new("Crouching");
    static readonly FName StandingBot = new("StandingBot");
    static readonly FName Relaxed = new("Relaxed");
    static readonly float[] CatchUpTurns = { 180f, 150f, 210f, 120f, 240f };

    public static readonly Choice Who = new(
        "player.sidekick.who", "SIDEKICK",
        "Who CALL SIDEKICK brings in. Select calls them. The character you are playing brings a second one, in the suit you are wearing.",
        Installed())
    {
        Preference = true,
        Pressed = static () => Call!.Press(),
        PressName = "CALL",
        Changed = static () => RefreshSuits(),
    };

    public static readonly Choice SuitPick = new(
        "player.sidekick.suit", "SIDEKICK SUIT",
        "The suit your sidekick arrives in, from the ones you have unlocked for them. STORY SUIT is the one picked for them in the showcase. It is read when they are called.",
        new[] { ("", "STORY SUIT") }, saved: false)
    {
        Opening = static () => RefreshSuits(),
        Block = static () => SuitLock,
    };

    public static readonly Command Call = new(
        "player.sidekick.call", "CALL SIDEKICK",
        "Calls your sidekick. They turn up close by where you aren't looking, then follow you, join fights and do dual takedowns. Called while you are busy, they arrive once you are free.",
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
        "Your sidekick heads off and is gone once out of sight. If you are playing as them you are put back first. One still on the way, or due to rejoin you, is called off.",
        SendAway)
    {
        Block = static () => OnTheWay || Rejoining ? null : Missing(),
    };

    public static readonly Toggle Rejoins = new(
        "player.sidekick.rejoins", "SIDEKICK REJOINS",
        "A sidekick the game takes away rejoins you once you are free. That covers scenes, restarts, loading a save and the story bringing in a partner of its own. Off, you call them again yourself.",
        on: true)
    {
        Preference = true,
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
        "With a sidekick out, the call Batmobile button swaps you with them on foot, as in the story. Off, the button calls the Batmobile as usual and SWAP WITH SIDEKICK does the swap. In a fight the button never calls the car. In the car it always gets you out.",
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
        "A sidekick left more than 40 m behind, with no way up to you, or 20 m from a fight you are in, rejoins you out of sight, as the story's partners do.", on: true)
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

    public static readonly Toggle Hits = new(
        "player.sidekick.hits", "SIDEKICK HITS HURT",
        "Your sidekick's hits knock enemies out, and land harder as your combo grows. Off, their hits only stagger, as the story's partners' do. Where the story limits which hits count in a fight, they keep to it.", on: true)
    {
        Preference = true,
        Block = static () => FightLock,
    };

    public static readonly Toggle Presses = new(
        "player.sidekick.presses", "SIDEKICK PRESSES THE ATTACK",
        "Your sidekick runs to the enemy nearest you, keeps hitting and finishes off whoever they have hurt. Off, they walk to whoever is nearest them and strike about once a second, as the story's partners do.", on: true)
    {
        Preference = true,
        Block = static () => FightLock,
    };

    public static readonly Toggle Covers = new(
        "player.sidekick.covers", "SIDEKICK COVERS YOU",
        "Your sidekick hits an enemy who is about to strike you, when one is within their reach.", on: true)
    {
        Preference = true,
        Block = static () => FightLock,
    };

    public static readonly Toggle Talks = new(
        "player.sidekick.talks", "SIDEKICK TALKS",
        "Your sidekick says the game's partner lines. They speak up as they join a fight, and praise a fight you won with a big combo or without being hit. The lines were recorded for the story's pairs, so they only talk to the hero they were made for. Off, they stay quiet.", on: true)
    {
        Preference = true,
    };

    static string? FightLock => Fights.On ? null : "FIGHTING IS OFF";

    public static readonly Command Order = new(
        "player.sidekick.order", "ORDER SIDEKICK",
        "Press to show where your sidekick would go, then aim and left click, or press it again, to send them. Right click cancels. A spot on the floor they hold, facing the way you looked. An enemy they go for until it is down, then they follow you again. Aimed at them, they come back. SIDEKICK COME BACK leaves order mode.",
        Issue)
    {
        Block = static () => Missing(),
    };

    public static readonly Command Recall = new(
        "player.sidekick.recall", "SIDEKICK COME BACK",
        "Ends a held spot or a target. Your sidekick follows you again. In order mode it leaves order mode instead.",
        CallBack)
    {
        Block = static () => Ordered || Aiming ? null : "NO ORDERS GIVEN",
    };

    public static readonly Catalog.Section[] Sections =
    {
        new("CALL", Who, SuitPick, Call, Bring, Dismiss, Rejoins),
        new("SWAP", Swap, ButtonSwaps),
        new("FOLLOWING", Follows, Distance, CatchesUp, Sneaks),
        new("FIGHTING", Fights, Hits, Presses, Covers, Talks),
        new("ORDERS", Order, Recall),
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
    static float _briskUntil;
    static bool _adopted;

    // Who should be out, whether or not they are right now. Empty for nobody.
    static string _wanted = "";
    static bool _rejoining;
    static int _rejoinTries;
    static long _rejoinAt;

    // The sidekick or the body that called them is gone, and the world may be coming down.
    static bool _lost;

    // A slot we loaded that a new world left behind. It is reused or emptied once the new world
    // has settled, and only while the same game object still holds it.
    static string _leftName = "";
    static int _leftSlot = -1;
    static long _leftGame;

    // What the rows' locks show. Looked at a few times a second, since a lock is asked every frame.
    static float _lookedAt = -100f;
    static string? _busy;
    static string? _swapLock;
    static string _askedFor = "";
    static FName _asked;

    // Loading, or loaded and waiting for the player to be free.
    static bool OnTheWay => _loader is not null || _arriving;

    // Taken away by the game and due back.
    static bool Rejoining => _wanted.Length > 0 && _body is null && !OnTheWay && Rejoins.On;

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
            if (name == Batman || GameFiles.HasPackage(name))
            {
                list.Add((name, label));
            }
        }

        return list.ToArray();
    }

    // Why the suit can't be picked right now, or null.
    static string? SuitLock => _body is not null || OnTheWay ? "ALREADY HERE" : _suitLock;

    static string? _suitLock;
    static string _suitsFor = "";

    // The suits unlocked for whoever is picked to be called.
    static void RefreshSuits()
    {
        var name = Who.CurrentId;
        var keep = SuitPick.CurrentId;
        _suitsFor = name;
        var list = new List<(string Id, string Label)> { ("", "STORY SUIT") };
        if (name.Length > 0)
        {
            list.AddRange(Suits.Unlocked(name));
        }

        SuitPick.SetOptions(list.ToArray(), Math.Max(0, list.FindIndex(entry => entry.Id == keep)));
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
        Save();
        _busy = _body is null && !OnTheWay ? Busy(Who.CurrentId) : null;
        _suitLock = Game.TryGetGameRI(out var look) && Who.CurrentId.Length > 0 && Loaded.IndexOf(look, new FName(Who.CurrentId)) >= 0
            ? Who.CurrentId == Batman ? "WEARS YOUR SUIT" : "ALREADY LOADED" : null;
        if (_suitsFor != Who.CurrentId)
        {
            RefreshSuits();
        }

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

        if (controller.IsPlayingFullScreenMovie(false))
        {
            return "LOADING";
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

        return name == Batman && Loaded.IndexOf(game, _asked) < 0 ? "BATMAN ISN'T LOADED" : null;
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

        _rejoining = false;
        var outcome = Begin(name);
        if (outcome.Kind != OutcomeKind.Failed)
        {
            Want(name);
        }

        return outcome;
    }

    // Starts bringing a character in, for a call or for one who is rejoining.
    static Outcome Begin(string name)
    {
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
        _ownsSlot = !already || (index == _leftSlot && name == _leftName && game.Ptr == _leftGame);
        _leaving = false;
        _farSince = -1f;
        Leftover("", -1, 0);

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
            if (_ownsSlot)
            {
                LendSuit(name, SuitPick.CurrentId);
            }

            loader.SetCharacterData(out var slot, character, null!, null!);
            _slot = slot;
        }
        catch
        {
            ReturnSuit();
            DropLoader();
            Forget();
            throw;
        }

        return Outcome.Done(Coming);
    }

    // Remembers who should be out, for this game and the next time it starts.
    static void Want(string name)
    {
        _wanted = name;
        _rejoinTries = 0;
        Save();
        Keep();
    }

    // The name is only written to the settings file once there has been one to write.
    static void Save()
    {
        if (_wanted.Length > 0 || Settings.Kept(Section, OutKey) is not null)
        {
            Settings.Set(Section, OutKey, _wanted);
        }
    }

    // Calls back a sidekick the game took away, once the player is on foot and free.
    static void Rejoin()
    {
        Tidy();
        if (_wanted.Length == 0)
        {
            return;
        }

        if (!Rejoins.On)
        {
            Want("");
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (now < _rejoinAt)
        {
            return;
        }

        _rejoinAt = now + (long)(RejoinEverySeconds * Stopwatch.Frequency);
        if (World.Player is not { bDeleteMe: false, Health: > 0, Physics: Actor.EPhysics.PHYS_Walking } pawn || World.Controller is not { } controller
            || controller.Pawn is not { } driven || driven.Ptr != pawn.Ptr || !Following(controller) || Remesh.Busy || Busy(_wanted) is not null)
        {
            return;
        }

        _rejoining = true;
        var outcome = Begin(_wanted);
        if (outcome.Kind == OutcomeKind.Failed || outcome.Text != Coming)
        {
            Ended(outcome);
        }
    }

    // How a call ended. One that failed is tried again a few times while they are due back,
    // and only the last try is worth telling the player about.
    static void Ended(Outcome outcome)
    {
        if (outcome.Kind != OutcomeKind.Failed)
        {
            _rejoinTries = 0;
            Call.Report(outcome);
            return;
        }

        if (_wanted.Length > 0 && ++_rejoinTries >= RejoinTries)
        {
            Log.Info($"{_wanted} could not rejoin as a sidekick after {RejoinTries} tries. {outcome.Text}.");
            Want("");
        }

        if (_wanted.Length == 0 || !_rejoining)
        {
            Call.Report(outcome);
        }
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
        if (OnTheWay || Rejoining)
        {
            if (OnTheWay)
            {
                CancelLoad();
            }

            Want("");
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
        Want("");
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
            ReturnCallKey();
            Rejoin();
            return;
        }

        // A new world took every actor with it. Its slot may still hold them.
        if (!_body.IsValid)
        {
            if (_ownsSlot && _slot > 0 && Game.TryGetGameRI(out var game))
            {
                Leftover(_name, _slot, game.Ptr);
            }

            _lost = false;
            Forget();
            return;
        }

        // A scene or a mission took the sidekick or the caller away. A load or a restart does
        // the same while it takes the old world apart, and destroying actors or reloading slots
        // in the middle of that crashed the game. So nothing is touched until the player has a
        // live body again. If the world goes instead, the branch above lets it all go.
        if (!Present || _caller is not { IsValid: true, bDeleteMe: false })
        {
            if (!_lost)
            {
                _lost = true;
                Log.Info($"{_name} or the body that called them was taken away. Waiting for the game to settle.");
            }

            if (Settled())
            {
                Log.Info($"{_name} was taken away, so the sidekick is gone.");
                Remove();
            }

            return;
        }

        _lost = false;
        SteerFocus();

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
            ClearHold();
            Leave(controller, follower);
            return;
        }

        Tune(_bot, controller);
        LoadDoorMove();
        CapesApart(_body, _caller);
        Arena(Fights.On);
        FightWatch(controller);
        Sneak(controller, follower);
        ThroughDoor(controller, follower);
        FreeSwap(controller);
        Catch(controller, follower);
        if (Ordered)
        {
            Hold(follower);
            return;
        }

        if (Follows.On && CatchesUp.On)
        {
            CatchUp(controller, follower);
        }
    }

    // A sidekick falling well below a player on solid ground has lost their floor, usually a
    // part of the city that unloaded after a door. Left alone they fall out of the world and are
    // only back after a rejoin. They are put beside the player, where the camera isn't looking.
    static void Catch(RPlayerController controller, RPawnPlayer follower)
    {
        if (follower.Physics != Actor.EPhysics.PHYS_Falling || follower.CurrentSpecialMove is not null || controller.Pawn is not RPawnPlayer player
            || player.Physics != Actor.EPhysics.PHYS_Walking || follower.Location.Z > player.Location.Z - FallCatch)
        {
            return;
        }

        controller.GetPlayerViewPoint(out var eye, out var view);
        if (Unseen(player, eye, view, out var floor) || Beside(player, out floor))
        {
            Place(follower, floor, Heading(player.Location - floor, player.Rotation.Yaw));
        }
    }

    // A load that outlives this copy of the class would keep its slot for good.
    public static void Release()
    {
        // Two reloads in a row can unload a copy before its first tick. It still has to take
        // over the sidekick first, or it would hand on an empty record and the next copy would
        // lose them.
        Adopt();
        GiveClick();
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
            if (!Rejoining)
            {
                Call.Report(Outcome.Unchanged("CALLED OFF"));
            }

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
            ReturnSuit();
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
            Want("");
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
            Ended(Outcome.Failed("CAN'T LOAD"));
            return;
        }

        if (stuck)
        {
            LogSlot($"{_name} took too long to load as a sidekick.");
            CancelLoad();
            Want("");
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

        if (controller.Pawn is not { } driven || driven.Ptr != pawn.Ptr || pawn.Health <= 0 || Remesh.Busy || Switcher.InScene(controller)
            || controller.IsPlayingFullScreenMovie(false))
        {
            return;
        }

        Ended(Spawn());
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

        ReturnSuit();
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

    // The game loads the suit picked in its options, so the pick is changed for the load and
    // put back once the suit is in.
    static string _lentFor = "";
    static string _lentWas = "";

    static void LendSuit(string character, string suit)
    {
        if (suit.Length == 0 || !Suits.Belongs(suit, character) || !Game.TryGetGameRI(out var game) || game.PersistentOptions is not { IsValid: true } options)
        {
            return;
        }

        var was = Suits.Active(options, character, 0);
        if (was.Equals(suit, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lentFor = suit;
        _lentWas = was;
        options.SetActivePlayerSkin(GameText.Once(suit));
    }

    static void ReturnSuit()
    {
        if (_lentFor.Length == 0)
        {
            return;
        }

        var was = _lentWas;
        _lentFor = "";
        _lentWas = "";
        if (Game.TryGetGameRI(out var game) && game.PersistentOptions is { IsValid: true } options)
        {
            options.SetActivePlayerSkin(GameText.Once(was));
        }
    }

    static void CancelLoad()
    {
        ReturnSuit();
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
            Want("");
            return GiveUp("CAN'T LOAD");
        }

        // They come in where the camera isn't looking and walk up, so nobody pops into view.
        controller.GetPlayerViewPoint(out var eye, out var view);
        var height = pawn.GetCollisionHeight();
        var floor = Unseen(pawn, eye, view, out var hidden) ? hidden
            : Beside(pawn, out var beside) ? beside
            : pawn.Location + Flat(pawn.Rotation.Yaw + 90f) * (ArriveBeside * 0.66f) - Vector3.UnitZ * height;
        var at = floor + new Vector3(0f, 0f, height + 5f);
        var facing = new Rotator(0f, Heading(pawn.Location - at, pawn.Rotation.Yaw), 0f);
        var body = Game.SpawnActor(kind, default, at, facing) as RPawnPlayer;
        RRobinBotController? bot = null;
        if (body is not null)
        {
            bot = Game.SpawnActor(RRobinBotController.StaticClass(), default, at, facing, null, body) as RRobinBotController;
            if (bot is not null)
            {
                body.RiddlerCharData = data;
                info.PlayerSwitchPawn = body;
                body.SharedBotController = bot;
                pawn.SharedBotController = bot;
                bot.Possess(body, false);
                GiveKit(body);
            }
        }

        if (body is null)
        {
            return GiveUp("NO ROOM HERE");
        }

        if (bot is null)
        {
            body.Destroy();
            return GiveUp("FAILED");
        }

        CapesApart(body, pawn);
        LoadDoorMove();

        _body = body;
        _bot = bot;
        _arriving = false;
        _upkeepAt = 0f;
        _stuckSeenAt = -100f;
        _poseSince = World.Now;
        _briskUntil = 0f;
        Tune(bot, controller);
        Keep();
        Log.Info($"{_name} joined as a sidekick from slot {_slot}. {ObjectTable.Headroom} objects are free.");
        return Outcome.Done(_standardSuit ? "ARRIVED IN STANDARD SUIT" : _rejoining ? "BACK WITH YOU" : "ARRIVED");
    }

    // The character's own starting gadgets. Most playable classes have no binding of their own in
    // BmSDK, and a binding always runs its own class's version, so the call goes through the
    // closest class that defines it.
    static void GiveKit(RPawnPlayer body)
    {
        for (var kind = body.Class as Struct; kind is { IsValid: true }; kind = kind.SuperStruct)
        {
            if (ScriptCall.Run(body, kind.GetPathName() + ".AddDefaultInventory"))
            {
                return;
            }
        }
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

    // The way a flat direction points, or the fallback when it points nowhere.
    static float Heading(Vector3 way, float fallback) =>
        way.X * way.X + way.Y * way.Y > 1f ? MathF.Atan2(way.Y, way.X) * (180f / MathF.PI) : fallback;

    // Open floor a short way from the player that the camera can't see.
    static bool Unseen(RPawnPlayer player, Vector3 eye, Rotator view, out Vector3 floor)
    {
        foreach (var turn in CatchUpTurns)
        {
            if (Standable(player, player.Location + Flat(view.Yaw + turn) * CatchUpBehind, out floor) && !InView(player, eye, view, floor + Vector3.UnitZ * 60f))
            {
                return true;
            }
        }

        floor = default;
        return false;
    }

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
        if (_bot is { } bot)
        {
            // The partner AI only looks for a new path when its goal has moved. From a new
            // spot the goal can be the same one, so the old goal is thrown away.
            bot.ClearNavigation();
            bot.LastMovePoint = new Vector3(0f, 0f, -1e9f);
            bot.TimeTillMovementCheck = 0f;
        }

        _farSince = -1f;
        _stuckSeenAt = -100f;
    }

    // How far back the partner AI stops, and from how far it walks and then runs. The game's
    // own gaps suit a player at a stroll. Behind one on the move they would walk until left far
    // behind and only then run, so the gaps follow the player's pace. A stride speeds up and
    // slows down, so the faster pace is held for a moment after it was last seen.
    static void Tune(RRobinBotController bot, RPlayerController controller)
    {
        if (World.Now < _briskUntil - BriskHoldSeconds)
        {
            _briskUntil = 0f;
        }

        if (controller.Pawn is { } player && (Same(controller.GetStateName(), Running) || player.Velocity.X * player.Velocity.X + player.Velocity.Y * player.Velocity.Y > BriskSpeed * BriskSpeed))
        {
            _briskUntil = World.Now + BriskHoldSeconds;
        }

        var brisk = World.Now < _briskUntil;
        var stand = (float)Distance.Value * Metre;
        var walk = stand + (brisk ? BriskWalkPast : WalkPast);
        if (bot.StandBehindDist == stand && bot.WalkBehindDist == walk)
        {
            return;
        }

        bot.StandBehindDist = stand;
        bot.WalkBehindDist = walk;
        bot.RunBehindDist = stand + (brisk ? BriskRunPast : RunPast);
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

    // Two capes on one collision channel catch each other's cloth when their wearers stand
    // close, and both flap about. The game keeps its own pairs on two channels, Batman's cape on
    // one and Robin's and Azrael's on the other. The sidekick's cape goes on whichever one the
    // player's isn't on. The game files a running cape by its channel, so a running cape is
    // stopped before the change and started again after it.
    static void CapesApart(RPawnPlayer sidekick, RPawnPlayer other)
    {
        if (sidekick is not RPawnPlayerBmBase { CapeComponent: { IsValid: true } theirs } || other is not RPawnPlayerBmBase { CapeComponent: { IsValid: true } mine })
        {
            return;
        }

        var channel = theirs.RBChannel;
        if (channel != mine.RBChannel || channel is not (Channel.RBCC_Cape or Channel.RBCC_RobinCape))
        {
            return;
        }

        var running = theirs.GetIsInitialised();
        var wanted = theirs.IsUserInitEnabled;
        var shown = !theirs.IsCapeHiddenGame;
        if (running)
        {
            theirs.InitEnable(RCapeComponent.EInitterType.INITTERTYPE_User, false);
            if (theirs.GetIsInitialised())
            {
                theirs.InitEnable(RCapeComponent.EInitterType.INITTERTYPE_User, wanted);
                return;
            }
        }

        theirs.SetRBChannel(channel == Channel.RBCC_Cape ? Channel.RBCC_RobinCape : Channel.RBCC_Cape);
        if (!running)
        {
            return;
        }

        // Stopping a cape hides its cloth, and starting it again leaves it hidden.
        theirs.InitEnable(RCapeComponent.EInitterType.INITTERTYPE_User, wanted);
        if (shown)
        {
            theirs.CapeSetHiddenGame(false);
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
            || follower.CombatMove is not null || Switcher.InScene(controller))
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
        var fight = Fights.On && gap >= FightRange * FightRange && controller.IsInCombat(false);
        if (gap < CatchUpRange * CatchUpRange && !stuck && !fight)
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
        if (waited < (fight ? FightWaitSeconds : CatchUpWaitSeconds) || (InView(player, eye, view, follower.Location) && waited < CatchUpAnywaySeconds))
        {
            return;
        }

        if (Unseen(player, eye, view, out var floor))
        {
            Place(follower, floor, Heading(player.Location - floor, player.Rotation.Yaw));
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

    // The player has a live body and no loading screen is up.
    static bool Settled() =>
        World.Player is { bDeleteMe: false, Health: > 0 } pawn && World.Controller is { } controller && controller.Pawn is { IsValid: true, bDeleteMe: false } driven
        && (driven.Ptr == pawn.Ptr || World.Driving is not null) && !controller.IsPlayingFullScreenMovie(false);

    static void Leftover(string name, int slot, long game)
    {
        _leftName = name;
        _leftSlot = slot;
        _leftGame = game;
        Survive.Set(Survive.SidekickLeftName, name);
        Survive.Set(Survive.SidekickLeftSlot, slot);
        Survive.Set(Survive.SidekickLeftGame, game);
    }

    // Empties a slot a new world left behind, once nobody is coming back for it.
    static void Tidy()
    {
        if (_leftName.Length == 0 || (_wanted == _leftName && Rejoins.On) || !Settled())
        {
            return;
        }

        if (Game.TryGetGameRI(out var game) && game.Ptr == _leftGame)
        {
            _name = _leftName;
            _slot = _leftSlot;
            _ownsSlot = true;
            ClearSlot();
            _name = "";
            _slot = -1;
            _ownsSlot = false;
        }

        Leftover("", -1, 0);
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
        _arriving = false;
        _leaving = false;
        _doorOpening = false;
        _farSince = -1f;
        _lookedAt = -100f;
        ClearHold();
        _rejoinAt = Stopwatch.GetTimestamp() + (long)(RejoinAfterSeconds * Stopwatch.Frequency);
        Keep();
        Catalog.MenuOpening();
    }

    // A hot reload starts this class over while the sidekick is still in the world.
    static void Keep()
    {
        Survive.Set(Survive.SidekickName, _name);
        Survive.Set(Survive.SidekickWanted, _wanted);
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
        _leftName = Survive.Get(Survive.SidekickLeftName, "");
        _leftSlot = Survive.Get(Survive.SidekickLeftSlot, -1);
        _leftGame = Survive.Get(Survive.SidekickLeftGame, 0L);

        // After a hot reload the name is in memory. At a game start it is in the settings file.
        var wanted = Survive.Get<string?>(Survive.SidekickWanted, null) ?? Settings.Kept(Section, OutKey) as string ?? "";
        _wanted = Array.Exists(Known, known => known.Name == wanted) && GameFiles.HasPackage(wanted) ? wanted : "";
        _rejoinAt = Stopwatch.GetTimestamp() + (long)(RejoinAfterSeconds * Stopwatch.Frequency);

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

    static bool MidMove(RPlayerController controller)
    {
        var state = controller.GetStateName();
        foreach (var move in MoveStates)
        {
            if (Same(state, move))
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
                return Follows.On && !_leaving && !Ordered && Game.TryGetPlayerController(out var controller)
                    && (Following(controller) || (MidMove(controller) && !controller.IsInCombat(false)));
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

    // The story's partners only go through a door with the player while a mission flag says
    // so. Ours goes wherever it follows.
    [Redirect(typeof(RRobinBotController), nameof(RRobinBotController.CanUseDoors))]
    static bool CanUseDoors(RRobinBotController self)
    {
        try
        {
            if (Ours(self))
            {
                return Follows.On && !_leaving;
            }
        }
        catch (Exception error)
        {
            Log.Error("sidekick.doors", "The sidekick's door rule failed", error);
        }

        return Redirects.RunOriginal<bool>();
    }

    // The game runs them at one speed however far back they are, and a faster run asked for
    // after the game has set its own only half takes. So past the gap they run from, the step
    // along the path is taken here. It is the game's step with a run that gets faster the
    // further behind they are.
    [Redirect(typeof(RRobinBotController), nameof(RRobinBotController.FollowPath))]
    static void FollowPath(RRobinBotController self)
    {
        try
        {
            if (Ours(self) && Hurry(self))
            {
                return;
            }
        }
        catch (Exception error)
        {
            Log.Error("sidekick.hurry", "The sidekick's hurry rule failed", error);
        }

        Redirects.RunOriginal();
    }

    static bool Hurry(RRobinBotController bot)
    {
        if (_leaving || bot.bInKismetMove || bot.bForceRun || bot.NavHandle is not { IsValid: true } path || bot.BotPawn is not { IsValid: true, bDeleteMe: false } follower
            || !Game.TryGetPlayerController(out var controller) || controller.Pawn is not { } player)
        {
            return false;
        }

        var away = player.Location - follower.Location;
        var over = MathF.Sqrt(away.X * away.X + away.Y * away.Y) - bot.RunBehindDist;
        if (over <= 0f)
        {
            return false;
        }

        if (!path.GetNextMoveLocation(out var next, PathArrive))
        {
            bot.bWasMoving = false;
            follower.StopMoveTo();
            return true;
        }

        if (bot.bNeedsToJumpDown)
        {
            bot.bNeedsToJumpDown = false;
            bot.GotoState(JumpDown);
        }

        // The game leaves the speed alone while the body blends from one gait to the next, and
        // a speed set then is lost. From a standing start the faster run comes in once the
        // body is up to a run.
        bot.bWasMoving = true;
        if (!follower.IsInAnyAutomaticTransition(default, out _))
        {
            follower.SetMovementSpeed(Pace.MS_Run, 1f + HurryMost * MathF.Min(1f, over / HurryOver), default);
            bot.ActualMoveSpeed = bot.RunBase;
        }

        follower.MoveInDirection(next - follower.Location);
        return true;
    }

    // The call Batmobile button asks for an unforced swap whenever there is a switch partner.
    // The story's swaps and DISMISS are forced, and go through.
    [Redirect(typeof(RPlayerController), nameof(RPlayerController.SwitchPlayer))]
    static bool SwitchPlayer(RPlayerController self, bool force, bool reverseLook, bool lockCamera, bool tryDualplayCombatMove, bool slowMoSwitch, bool forSwitchPlayerAction)
    {
        try
        {
            Adopt();
            if (!force && !_trading && Present && (_leaving || !ButtonSwaps.On || World.Driving is not null))
            {
                return false;
            }
        }
        catch (Exception error)
        {
            Log.Error("sidekick.swap", "The sidekick's swap rule failed", error);
        }

        var before = self.Pawn?.Ptr ?? 0;
        var swapped = Redirects.RunOriginal<bool>();
        try
        {
            if (Present && self.Pawn is RPawnPlayer taken && taken.Ptr != before && (taken.Ptr == _body!.Ptr || taken.Ptr == _caller?.Ptr))
            {
                TakeOver(taken);
            }
            else if (!swapped && !force && Present)
            {
                Refused(self);
            }
        }
        catch (Exception error)
        {
            Log.Error("sidekick.swap", "The sidekick's swap clean-up failed", error);
        }

        return swapped;
    }

    // The partner AI leaves its body aiming, looking and facing at the player, and the game's
    // swap only lets go of the aim. A body taken over from it would keep turning to face the
    // other one instead of running. Its gadget is the one the save names, which is often one
    // this character doesn't carry, so they get their own first one then.
    static void TakeOver(RPawnPlayer taken)
    {
        taken.StopAimAt();
        taken.StopLookAt();
        taken.StopFaceAt();
        taken.bIsBot = false;
        ClearHold();
        if (taken.InvManager is RInventoryManager inventory && inventory.CurrentGadget is not { IsValid: true })
        {
            for (var item = inventory.InventoryChain; item is not null; item = item._Inventory)
            {
                if (item is RInventoryGadget { IsValid: true } gadget && gadget is not RGrappleGun)
                {
                    inventory.SetCurrentGadget(gadget, false);
                    inventory.DisplayedGadget = gadget;
                    break;
                }
            }
        }
    }

    // Why the game said no to a swap, once per reason, for the support log.
    static string _refusedFor = "";

    static void Refused(RPlayerController controller)
    {
        var gadget = controller.GetCurrentGadget();
        var why = World.Info is { bDualPlayerSwitchingDisabledBySeq: true } ? "the story locked it"
            : World.Info is { bDualPlayerSwitchingDisabled: true } ? "a team move had it switched off"
            : gadget is { IsValid: true, bAiming: true } ? "aiming"
            : gadget is { IsValid: true } held && held.IsGadgetAttachedToHand() ? $"{held.Class.Name} in hand"
            : controller.CombatPawn?.CurrentSpecialMove is { } move ? $"in {move.Class.Name}"
            : "";
        if (why.Length > 0 && why != _refusedFor)
        {
            _refusedFor = why;
            Log.Info($"The game refused a swap with {_name}: {why}.");
        }
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
