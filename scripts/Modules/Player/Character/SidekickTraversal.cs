#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using Pace = BmSDK.Engine.RAutomaticTransitions.EMovementSpeed;

namespace Knightfall;

// Getting to where the partner AI has no path. The story's partners only walk, so a fence, a
// ledge or a roof stopped them. Ours vaults what is in the way, climbs a ledge within reach,
// fires their own grapple gun up to a roof or across a gap, and drops off an edge with the game's
// own jump for partners. The player's controller finishes a grapple onto the ledge, and a
// sidekick has none, so that last part is done here with the gun's own landing move.
//
// The upkeep asks Traverse, which picks what to do and starts it. Advance runs every tick and
// sees it through. While any of it is under way Traversing is true, and the follow, the fight
// moves, orders and catch-up all leave them alone.
static partial class Sidekick
{
    const string ClimbSet = "Anim_Batman_Climb.Code.BM_Climb";
    const string VaultAnim = "Run_Climb_Over_128";

    const string GrappleLanding = "Batman_Gadget_Moves.GrappleGun.Grapple_End_Climb_Flat";

    // A vault is for something between knee and chest high, with floor on the far side.
    const float VaultLowest = 30f;
    const float VaultHighest = 150f;
    const float VaultLook = 110f;
    const float VaultOver = 260f;
    const float VaultMost = 3f;
    const float BlockedSpeed = 40f;
    const float BlockedSeconds = 0.4f;

    // How far past the front of what is in the way its top is looked for. A thin fence has
    // hardly any top to find.
    static readonly float[] VaultTops = { 4f, 24f };

    // After a move they stand a moment before the next one is thought about, longer after one
    // that failed. After this many tries at the same place came to nothing they stop trying for
    // a while, so catch-up gets its turn. A goal that moves this far is a new place.
    const float ArrivedRest = 0.4f;
    const float TraverseRest = 1.5f;
    const int GiveUpAfter = 2;
    const float GiveUpRest = 10f;
    const float GoalMoved = Metre * 5f;
    const float JoinedRest = 5f;

    // They wait this long standing still before anything but a vault, in case the partner AI
    // finds its own way. It looks for a path a few times a second. The partner AI walks up from
    // this much further off than it stops at, so nearer than that on the level is near enough.
    const float ReachWait = 0.8f;
    const float ReachShort = 300f;

    // A goal this much higher needs a climb. An edge within arm's reach is climbed by hand, a
    // higher one with the gun. The game's landing move pulls them onto the edge from this far.
    const float ClimbRise = 130f;
    const float GrappleRise = 250f;
    const float GrappleNear = Metre * 40f;
    const float MantleReach = 300f;
    const float MantleStand = 100f;
    const float UnderEdge = 200f;

    // How an edge is looked for: steps along the floor until it falls away.
    const float EdgeStep = 50f;
    const float EdgeRise = 80f;
    const float EdgeDrop = 200f;
    const float EdgeStepDown = 120f;
    const float EdgeSearchPast = 600f;
    const float EdgeSearchMost = Metre * 30f;

    static readonly (float Out, float Up)[] SightPoints = { (60f, 60f), (60f, 150f), (120f, 250f), (0f, 250f) };

    // The flight stops short of the ledge, and the landing move covers the rest. They are pulled
    // in to this far first at this speed, so the landing doesn't jump.
    const float LandFrom = 200f;
    const float PullSpeed = 2500f;

    // The pull aims at where they will hang, this far out from the wall and under the edge, so
    // neither is in the way. Blocked all round for this long, they land from where they are.
    const float HangOut = 90f;
    const float HangUnder = 40f;
    const float HungSeconds = 0.4f;

    // Within this far, a flight that turns away from the ledge or into a fall lands at once.
    const float LandFar = Metre * 12f;
    const float LandPassing = 50f;

    // The longest a shot may take to turn into a flight, a grapple to reach its ledge, and a
    // landing or a drop to end. A long grapple gets more time, a second for every so far.
    const float FireMost = 3f;
    const float GrappleGiveUp = 6f;
    const float GrappleSecondEvery = Metre * 15f;
    const float LandingMost = 5f;
    const float DropGiveUp = 6f;

    // A gap between roofs is grappled across, from near their own edge, when the far side is no
    // wider off or further down than this. A line this high over both edges has to be clear, so
    // a wall isn't taken for a gap.
    const float CrossFrom = Metre * 3f;
    const float CrossDrop = Metre * 10f;
    const float CrossLeast = Metre * 1.5f;
    const float CrossBack = 40f;
    const float GrappleAcross = Metre * 35f;
    const float AirHeight = 150f;

    // A goal this much lower is dropped down to, off an edge no higher than the most. The floor
    // they land on may lie this far under the goal, like a dip before it.
    const float DropLeast = 200f;
    const float DropMost = Metre * 20f;
    const float DropUnder = 300f;
    const float DropFrom = 110f;
    const float DropBack = 25f;
    const float DropArrive = 40f;
    const float DropTook = 80f;

    // A run of their own to somewhere the partner AI has no path to, like the foot of a wall or
    // the edge of their roof. It may take this long plus the time the distance takes at a slow
    // walk, and ends early when they stand blocked. This near counts as there.
    const float RunMost = 2f;
    const float RunSlowest = 180f;
    const float RunStalled = 0.75f;
    const float RunArrive = 100f;

    // The last stretch is walked. An edge further off than this is run to in two goes, the
    // first one stopping this far short of it.
    const float WalkWithin = 400f;
    const int RunsMost = 3;
    const float EdgeRunFrom = 500f;
    const float EdgeRunShort = 250f;

    // A partner AI still waiting on a move this long after the move ended is woken.
    const float ParkedSeconds = 1f;

    static readonly FName GrapplePose = new("Grapple");
    static readonly FName WaitsOnMove = new("SpecialMove");
    static readonly FName WaitsOnFall = new("FallingState");
    static readonly FName NoState = new("None");

    // The part of a traversal they are on.
    enum Leg
    {
        None,
        Run,
        Fire,
        Flight,
        Landing,
        Vault,
        Drop,
    }

    // How long something has gone on without a break. Game time starts over in a new world, so a
    // start that lies ahead is from the old one.
    struct Spell
    {
        bool _on;
        float _from;

        public void Stop() => _on = false;

        public bool Lasted(float seconds)
        {
            if (!_on || World.Now < _from)
            {
                _on = true;
                _from = World.Now;
            }

            return World.Now - _from >= seconds;
        }
    }

    static Leg _leg;
    static float _legSince;
    static bool _aloft;
    static bool _byHand;

    static Vector3 _ledge;
    static Vector3 _ledgeOut;
    static float _grappleSince;
    static float _grappleFor;
    static float _nearest = float.MaxValue;
    static Vector3 _runTo;
    static float _runFor;
    static float _runWithin = RunArrive;
    static float _dropFrom;

    static Spell _still;
    static Spell _blocked;
    static Spell _stalled;
    static Spell _parked;
    static Spell _hung;
    static bool _ready;
    static int _runs;

    static float _restFrom;
    static float _restFor;
    static int _tries;
    static bool _gaveUp;
    static Vector3 _triedFor;
    static string _refused = "";

    static AnimSet? _climbSet;
    static RSpecialMoveConfig? _landing;

    // In the middle of a run-up, a vault, a climb, a grapple or a drop.
    static bool Traversing => _leg != Leg.None;

    // The body the partner AI has, while both are still in the world.
    static RPawnPlayer? Follower =>
        _bot is { IsValid: true, bDeleteMe: false } bot && bot.BotPawn is { IsValid: true, bDeleteMe: false } body ? body : null;

    static bool Resting => World.Now >= _restFrom && World.Now - _restFrom < _restFor;

    // What the marker says while they are at it, or once they have given up on a place.
    static string? TraversalNote => _leg switch
    {
        Leg.Run => "FINDING A WAY",
        Leg.Fire or Leg.Flight => "GRAPPLING",
        Leg.Landing => _byHand ? "CLIMBING" : "GRAPPLING",
        Leg.Vault => "VAULTING",
        Leg.Drop => "DROPPING DOWN",
        _ => Stranded ? Holding ? "CAN'T GET THERE" : "CAN'T REACH YOU" : null,
    };

    static bool Stranded => _gaveUp && Resting;

    // Who or what they are trying to get to, for the log.
    static string Where => Holding ? "the spot they hold" : "you";

    // From the upkeep. True while a traversal is under way, when nothing else should move them.
    static bool Traverse(RPlayerController controller, RPawnPlayer follower)
    {
        if (_leg == Leg.Run)
        {
            Run(controller, follower);
        }

        if (_leg != Leg.None)
        {
            return true;
        }

        Untangle(follower);
        if (!Climbs.On || controller.IsInCombat(false) || Switcher.InScene(controller) || follower.CurrentSpecialMove is not null || follower.CombatMove is not null
            || follower.Physics != Actor.EPhysics.PHYS_Walking)
        {
            Idle();
            return false;
        }

        var bound = Goal(controller, out var goal, out var facing);
        if (bound && Vector3.DistanceSquared(goal, _triedFor) > GoalMoved * GoalMoved)
        {
            NewPlace(goal);
        }

        if (Resting)
        {
            Idle();
            return false;
        }

        // Further off than a grapple reaches is left to catch-up.
        var feet = Feet(follower);
        var slow = Slow(follower);
        if (bound && (slow || _ready) && Apart(feet, goal) && Flat(goal, feet) <= GrappleNear * GrappleNear)
        {
            if (_ready || _still.Lasted(ReachWait))
            {
                if (!_ready)
                {
                    _runs = 0;
                }

                Idle();
                return goal.Z - feet.Z >= ClimbRise ? Up(follower, feet, goal, facing) : Over(follower, feet, goal);
            }
        }
        else
        {
            _still.Stop();
            _ready = false;
        }

        if (!slow || !Trying(follower))
        {
            _blocked.Stop();
            return false;
        }

        if (!_blocked.Lasted(BlockedSeconds) || !Vault(follower))
        {
            return false;
        }

        Idle();
        return true;
    }

    static bool Slow(RPawnPlayer follower) =>
        follower.Velocity.X * follower.Velocity.X + follower.Velocity.Y * follower.Velocity.Y <= BlockedSpeed * BlockedSpeed;

    static void Idle()
    {
        _still.Stop();
        _blocked.Stop();
        _ready = false;
    }

    // Where they are trying to get to: a spot they were ordered to hold, or the player on their
    // feet while they follow. Nowhere while they go after a target, and nowhere the story keeps
    // its partners out of.
    static bool Goal(RPlayerController controller, out Vector3 goal, out float facing)
    {
        goal = default;
        facing = 0f;
        if (_focus is not null)
        {
            return false;
        }

        if (_hold is { } held)
        {
            goal = held;
            facing = _holdYaw;
            return !_arrived;
        }

        if (!Follows.On || !Following(controller) || controller.Pawn is not RPawnPlayer { Physics: Actor.EPhysics.PHYS_Walking } player || player.Base is Pawn)
        {
            return false;
        }

        goal = Feet(player);
        facing = player.Rotation.Yaw;
        return _bot!.BotExclusionVolume is not { IsValid: true, bDeleteMe: false } keptOut || !keptOut.EncompassesPoint(player.Location);
    }

    // Out of the reach the partner AI stands at: well above, well below or a good way off. A
    // spot they were sent to is out of reach until they stand on it.
    static bool Apart(Vector3 feet, Vector3 goal)
    {
        if (Holding)
        {
            return true;
        }

        var rise = goal.Z - feet.Z;
        var near = _bot!.StandBehindDist + ReachShort;
        return rise >= ClimbRise || rise <= -DropLeast || Flat(goal, feet) > near * near;
    }

    static void NewPlace(Vector3 goal)
    {
        _triedFor = goal;
        _tries = 0;
        _refused = "";
        _gaveUp = false;
        _restFor = 0f;
    }

    static void Rest(float seconds)
    {
        _restFrom = World.Now;
        _restFor = seconds;
    }

    static void Begin(Leg leg)
    {
        _leg = leg;
        _legSince = World.Now;
        _aloft = false;
    }

    // A move that got them somewhere.
    static void Arrived(RPawnPlayer follower)
    {
        _leg = Leg.None;
        _tries = 0;
        _runs = 0;
        Rest(ArrivedRest);
        if (_bot is not { IsValid: true, bDeleteMe: false } bot)
        {
            return;
        }

        ResetPath(bot);
        if (bot.bCrouched && follower.Physics == Actor.EPhysics.PHYS_Walking && follower.CurrentSpecialMove is null)
        {
            Stance(bot, follower, true);
        }
    }

    // A try that came to nothing. After a few at the same place they stop for a while.
    static void Missed()
    {
        _leg = Leg.None;
        _runs = 0;
        if (++_tries < GiveUpAfter)
        {
            Rest(TraverseRest);
            return;
        }

        Rest(GiveUpRest);
        if (!_gaveUp)
        {
            _gaveUp = true;
            Log.Info($"{_name} gave up getting to {Where} after {_tries} tries. Catch-up or BRING SIDEKICK HERE brings them.");
        }
    }

    // Nothing to climb or cross, so it is the partner AI's to walk. Looked at again in a while.
    static bool Joined()
    {
        _leg = Leg.None;
        Rest(JoinedRest);
        return false;
    }

    // Why a traversal didn't happen, once per reason, for the support log.
    static bool Refuse(string why)
    {
        if (why != _refused)
        {
            _refused = why;
            Log.Info($"{_name} can't get to {Where}: {why}.");
        }

        Missed();
        return false;
    }

    // New orders or a swap change where they are going, so a run-up ends and old tries are
    // forgotten.
    static void Rethink()
    {
        if (_leg == Leg.Run)
        {
            _leg = Leg.None;
            Follower?.StopMoveTo();
        }

        Idle();
        _tries = 0;
        _runs = 0;
        _gaveUp = false;
        _restFor = 0f;
        _refused = "";
    }

    // The sidekick is gone or about to be stood somewhere else.
    static void ForgetTraversal()
    {
        Rethink();
        _leg = Leg.None;
        _aloft = false;
        _byHand = false;
        _nearest = float.MaxValue;
        _parked.Stop();
        _stalled.Stop();
    }

    // They are about to be stood somewhere else, so whatever they were in the middle of is over.
    static void StopTraversal(RPawnPlayer follower)
    {
        var flying = _leg is Leg.Fire or Leg.Flight || follower.Physics == Actor.EPhysics.PHYS_Flying;
        ForgetTraversal();
        if (flying)
        {
            Untangle(follower, force: true);
        }
    }

    // A reload forgets a grapple under way, and a forced swap hands the body over as it is. The
    // follow would then take over in mid-air with the rope still out. So a grapple is ended and
    // they are put on the ledge.
    static void SettleTraversal()
    {
        var leg = _leg;
        var ledge = _ledge - _ledgeOut * 80f;
        if (leg == Leg.None || Follower is not { } follower)
        {
            ForgetTraversal();
            return;
        }

        if (leg is Leg.Fire or Leg.Flight)
        {
            Place(follower, ledge, Heading(-_ledgeOut, follower.Rotation.Yaw));
        }

        ForgetTraversal();
    }

    // The edge of the goal's floor on the sidekick's side, found by walking out from the goal
    // until the floor drops away. Within arm's reach they climb it. Higher up they grapple to it,
    // from where they can see it.
    static bool Up(RPawnPlayer follower, Vector3 feet, Vector3 goal, float facing)
    {
        var toward = feet - goal;
        toward.Z = 0f;
        var apart = toward.Length();
        toward = apart > 1f ? toward / apart : -Flat(facing);

        switch (Edge(follower, goal, feet, toward, apart, out var ledge, out _))
        {
            case Ground.Joined:
                return Joined();
            case Ground.Endless:
                return Refuse("no edge was found on the way up");
        }

        var up = ledge.Z - feet.Z;
        if (up < ClimbRise)
        {
            return Refuse("the edge isn't above them");
        }

        var seen = Sees(follower, ledge, toward);
        if (seen && Vector3.DistanceSquared(follower.Location, ledge) <= MantleReach * MantleReach)
        {
            return Mantle(follower, ledge, toward, up);
        }

        // A low edge is climbed from the floor right under it, when that is the floor they are
        // on. Across a gap it is grappled to like a high one.
        if (up < GrappleRise && Foot(follower, feet, ledge, toward, MantleStand, out var foot))
        {
            return RunTo(follower, foot);
        }

        if (GrappleShot(follower) is not { } shot)
        {
            return Refuse("they have no grapple gun");
        }

        if (seen)
        {
            return Fire(follower, shot, ledge, toward, "up");
        }

        // From the floor under the edge they can see it, so they run there first.
        return Foot(follower, feet, ledge, toward, UnderEdge, out var under) ? RunTo(follower, under) : Refuse("they can't see the edge");
    }

    // The floor under an edge, a little way out from it, when it is the floor they stand on and
    // they aren't there yet. A floor further down is the bottom of a gap, not somewhere to run to.
    static bool Foot(RPawnPlayer follower, Vector3 feet, Vector3 ledge, Vector3 outward, float away, out Vector3 foot) =>
        Teleport.Floor(follower, ledge + outward * away, ledge.Z, feet.Z - EdgeStepDown, out foot) && foot.Z < feet.Z + EdgeRise
        && Flat(foot, feet) > RunArrive * RunArrive;

    // A goal level with them or lower. Their own edge on the way is found first. With floor
    // right under it they drop down. With open air under it they grapple across to the far side.
    static bool Over(RPawnPlayer follower, Vector3 feet, Vector3 goal)
    {
        var toward = goal - feet;
        toward.Z = 0f;
        var apart = toward.Length();
        if (apart < 1f)
        {
            return Refuse("you are straight under them");
        }

        toward /= apart;
        switch (Edge(follower, feet, goal, toward, apart, out var mine, out var past))
        {
            case Ground.Joined:
                return Joined();
            case Ground.Endless:
                return Refuse("no edge was found on the way down");
        }

        // A dip with the goal's floor on its far side is crossed, not climbed down into and out
        // of. Floor under their edge is dropped to when the goal is below, or there is no far
        // side to cross to.
        var back = MathF.Sqrt(Flat(mine, feet));
        var top = mine.Z - EdgeStepDown;
        var bottom = MathF.Min(goal.Z, mine.Z) - DropUnder;
        var landed = Teleport.Floor(follower, past, top, bottom, out var landing) || Teleport.Floor(follower, past + toward * EdgeStep, top, bottom, out landing);
        var far = FarSide(follower, mine, goal, toward, apart - back + EdgeStep, out var other);
        var width = far ? MathF.Sqrt(Flat(mine, other)) : 0f;
        if (landed && (!far || width < CrossLeast || goal.Z - feet.Z <= -DropLeast))
        {
            var drop = mine.Z - landing.Z;
            if (drop > DropMost)
            {
                return Refuse($"it is too far to drop ({drop / Metre:0} m)");
            }

            if (!Teleport.Dry(landing))
            {
                return Refuse("they would land in water");
            }

            // The jump only carries them a step, so it starts from the very edge.
            var brink = Brink(follower, mine, past);
            back = MathF.Sqrt(Flat(brink, feet));
            return back > DropFrom ? ToEdge(follower, brink, toward, back, DropBack, DropArrive) : Drop(follower, brink, toward, drop);
        }

        if (goal.Z - feet.Z < -CrossDrop)
        {
            return Refuse("you are too far below to grapple across to");
        }

        if (!far)
        {
            return Refuse("no floor was found on the far side");
        }

        if (width < CrossLeast || width > GrappleAcross)
        {
            return Refuse($"the gap is too {(width < CrossLeast ? "small to grapple" : "wide to cross")} ({width / Metre:0.#} m)");
        }

        var air = Vector3.UnitZ * AirHeight;
        if (!follower.FastTrace(other + air, mine + air, Vector3.Zero, false))
        {
            return Refuse("something solid is in the way");
        }

        if (GrappleShot(follower) is not { } shot)
        {
            return Refuse("they have no grapple gun");
        }

        // From well back from their own edge they can't see the far side, so they run up first.
        if (back > CrossFrom)
        {
            return ToEdge(follower, mine, toward, back, CrossBack);
        }

        return Sees(follower, other, -toward) ? Fire(follower, shot, other, -toward, "across") : Refuse("they can't see the other side");
    }

    // What the floor does on the way from one of them to the other.
    enum Ground
    {
        // It falls away at an edge.
        Edge,

        // It runs all the way, like a slope or stairs, so the partner AI can walk it.
        Joined,

        // It runs on over or under the other one with no edge near.
        Endless,
    }

    // The edge can lie past the other one, when one stands close under or over it. So with no
    // edge between them, and the floor not ending at the other's height, the search goes on a
    // little further.
    static Ground Edge(Actor tracer, Vector3 from, Vector3 to, Vector3 toward, float apart, out Vector3 lip, out Vector3 past)
    {
        if (Lip(tracer, from, toward, apart, out lip, out past))
        {
            return Ground.Edge;
        }

        if (MathF.Abs(lip.Z - to.Z) < ClimbRise)
        {
            return Ground.Joined;
        }

        return Lip(tracer, from, toward, MathF.Min(apart + EdgeSearchPast, EdgeSearchMost), out lip, out past) ? Ground.Edge : Ground.Endless;
    }

    // Walks out from a floor point until the floor falls away. Lip is the last floor before
    // that, and past is the first place with none. False when the floor runs on that far.
    static bool Lip(Actor tracer, Vector3 from, Vector3 toward, float reach, out Vector3 lip, out Vector3 past)
    {
        lip = from;
        past = from;
        for (var step = EdgeStep; step <= reach; step += EdgeStep)
        {
            past = from + toward * step;
            if (!Teleport.Floor(tracer, past, lip.Z + EdgeRise, lip.Z - EdgeStepDown, out var floor))
            {
                return true;
            }

            lip = floor;
        }

        return false;
    }

    // The edge itself, between the last floor found and the first place with none.
    static Vector3 Brink(Actor tracer, Vector3 lip, Vector3 past)
    {
        for (var i = 0; i < 3; i++)
        {
            var middle = (lip + past) / 2f;
            if (Teleport.Floor(tracer, middle, lip.Z + EdgeRise, lip.Z - EdgeStepDown, out var floor))
            {
                lip = floor;
            }
            else
            {
                past = middle;
            }
        }

        return lip;
    }

    // The first floor past their edge on the way to the goal, about level with either of them.
    static bool FarSide(Actor tracer, Vector3 mine, Vector3 goal, Vector3 toward, float reach, out Vector3 other)
    {
        var top = MathF.Max(mine.Z, goal.Z) + EdgeRise;
        var low = MathF.Min(mine.Z, goal.Z) - EdgeDrop;
        for (var step = EdgeStep; step <= reach; step += EdgeStep)
        {
            if (Teleport.Floor(tracer, mine + toward * step, top, low, out other))
            {
                return true;
            }
        }

        other = default;
        return false;
    }

    // The edge found is the last floor before the drop, a little inside the roof, and a parapet
    // can stand on it. So a few points out over the edge and higher up are tried.
    static bool Sees(RPawnPlayer follower, Vector3 ledge, Vector3 outward)
    {
        var eye = follower.Location + Vector3.UnitZ * 60f;
        foreach (var (over, up) in SightPoints)
        {
            if (follower.FastTrace(ledge + outward * over + Vector3.UnitZ * up, eye, Vector3.Zero, false))
            {
                return true;
            }
        }

        return false;
    }

    // Whether the partner AI or an order is trying to move them right now.
    static bool Trying(RPawnPlayer follower)
    {
        var bot = _bot!;
        if (_steering || (bot.bInKismetMove && !_arrived) || bot.bWasMoving)
        {
            return true;
        }

        return Game.TryGetPlayerController(out var controller) && controller.Pawn is { } player && Follows.On && Following(controller) && !Ordered
            && Flat(player.Location, follower.Location) > MathF.Pow(bot.StandBehindDist + StuckShort, 2f);
    }

    // To their own edge. From far off they run to a little short of it first and walk the rest,
    // so they don't run off it.
    static bool ToEdge(RPawnPlayer follower, Vector3 mine, Vector3 toward, float back, float stop, float within = RunArrive) =>
        back > EdgeRunFrom ? RunTo(follower, mine - toward * EdgeRunShort) : RunTo(follower, mine - toward * stop, within);

    // Run-ups that only lead to more run-ups haven't lined them up, so that counts as a try
    // that failed. The last stretch is walked, since a run carries on past where it should stop.
    static bool RunTo(RPawnPlayer follower, Vector3 at, float within = RunArrive)
    {
        if (_runs >= RunsMost)
        {
            return Refuse("their run-up didn't line them up");
        }

        var bot = _bot!;
        bot.ClearNavigation();
        bot.bWasMoving = false;
        _walkedAt = -100f;
        var far = MathF.Sqrt(Flat(at, follower.Location));
        _runTo = at;
        _runWithin = within;
        _runFor = RunMost + far / RunSlowest;
        _runs++;
        _stalled.Stop();
        follower.SetMovementSpeed(far > WalkWithin ? Pace.MS_Run : Pace.MS_Walk, 1f, default);
        follower.MoveTo(at);
        Begin(Leg.Run);
        return true;
    }

    // From the upkeep while they run up. Once there, the next thing is done at once. A fight or
    // the row going off ends the run. Standing blocked or taking too long is a try that failed.
    static void Run(RPlayerController controller, RPawnPlayer follower)
    {
        var spent = World.Now - _legSince;
        var left = Flat(_runTo, follower.Location);
        var slow = Slow(follower);
        if (!slow)
        {
            _stalled.Stop();
        }

        // Stopped a little short of the spot counts as there.
        var stalled = slow && _stalled.Lasted(RunStalled);
        if (left <= _runWithin * _runWithin || (stalled && left <= _runWithin * _runWithin * 4f))
        {
            follower.StopMoveTo();
            _leg = Leg.None;
            _ready = true;
            return;
        }

        if (!Climbs.On || controller.IsInCombat(false) || follower.CurrentSpecialMove is not null || follower.Physics != Actor.EPhysics.PHYS_Walking)
        {
            follower.StopMoveTo();
            _leg = Leg.None;
            return;
        }

        if (spent < 0f || spent > _runFor || stalled)
        {
            follower.StopMoveTo();
            Refuse("something was in the way of their run-up");
        }
    }

    // Something low across the way they face, with clear air over it and floor beyond.
    static bool Vault(RPawnPlayer follower)
    {
        var way = follower.Rotation.ToDirection();
        way.Z = 0f;
        if (way.LengthSquared() < 0.01f)
        {
            return false;
        }

        way = Vector3.Normalize(way);
        var feet = Feet(follower);
        var knee = feet + Vector3.UnitZ * 50f;
        if (!Crosshair.Line(follower, knee, knee + way * VaultLook, Crosshair.Meets.Level, out var wall))
        {
            return false;
        }

        var height = 0f;
        foreach (var past in VaultTops)
        {
            if (Teleport.Floor(follower, feet + way * (wall.Distance + past), feet.Z + VaultHighest + 40f, feet.Z, out var top))
            {
                height = MathF.Max(height, top.Z - feet.Z);
            }
        }

        var over = feet + Vector3.UnitZ * (height + 60f);
        if (height < VaultLowest || height > VaultHighest || !follower.FastTrace(over + way * VaultOver, over, Vector3.Zero, false)
            || !Teleport.Floor(follower, feet + way * VaultOver, feet.Z + 60f, feet.Z - 300f, out _)
            || ClimbAnim(VaultAnim) is not { } anim)
        {
            return false;
        }

        follower.StopMoveTo();
        follower.SetRotation(new Rotator(0f, Heading(way, follower.Rotation.Yaw), 0f));
        follower.BeginChangePose();
        follower.Pose.MovementStance = StandingBot;
        follower.Pose.WeaponStance = Relaxed;
        follower.Pose.CustomTransition = anim;
        follower.EndChangePose();
        Begin(Leg.Vault);
        Log.Info("sidekick.vault", $"{_name} is vaulting something {height / Metre:0.#} m high.");
        return true;
    }

    // The game's lookup by name runs its native version without the caller's defaults and comes
    // back empty, so the sequence is put in by hand.
    static RAnimConfig.FCustomAnimConfig? ClimbAnim(string name)
    {
        if (_climbSet is not { IsValid: true })
        {
            _climbSet = Game.FindObject<AnimSet>(ClimbSet);
        }

        if (_climbSet is not { IsValid: true })
        {
            return null;
        }

        var sequences = _climbSet.Sequences;
        for (var i = 0; i < sequences.Count; i++)
        {
            if (sequences[i] is { IsValid: true } sequence && sequence.SequenceName.ToString() == name)
            {
                var anim = new RAnimConfig.FCustomAnimConfig();
                anim.FullBodyAnim = new FName(name);
                anim.CustomFullBodyAnim = sequence;
                return anim;
            }
        }

        return null;
    }

    static RSpecialMoveConfig? GrappleShot(RPawnPlayer follower) =>
        follower.GrappleGun is { IsValid: true } gun && gun.GrappleFireMove is { IsValid: true } shot ? shot : null;

    // The move that ends a grapple on a ledge. Batman's plays on every hero's body. Where his
    // isn't loaded their own gun's is used.
    static RSpecialMoveConfig? LandingMove(RPawnPlayer follower)
    {
        if (_landing is not { IsValid: true })
        {
            _landing = Game.FindObject<RSpecialMoveConfig>(GrappleLanding);
        }

        return _landing is { IsValid: true } ? _landing
            : follower.GrappleGun is { IsValid: true } gun && gun.GrappleEndClimbFlat is { IsValid: true } own ? own
            : null;
    }

    static REnvironmentCheckTicker.FEnvironmentSpecialMoveLocator Locator(RPawnPlayer follower, Vector3 ledge, Vector3 outward)
    {
        var locator = new REnvironmentCheckTicker.FEnvironmentSpecialMoveLocator();
        locator.Location = ledge;
        locator.Normal = outward;
        locator.Distance = Vector3.Distance(ledge, follower.Location);
        return locator;
    }

    // The partner AI stops walking them, so nothing pulls against the move.
    static void Still(RPawnPlayer follower)
    {
        _bot!.ClearNavigation();
        _walkedAt = -100f;
        follower.StopMoveTo();
    }

    // Fires their own grapple gun at a ledge. Outward points from the ledge back toward them.
    static bool Fire(RPawnPlayer follower, RSpecialMoveConfig shot, Vector3 ledge, Vector3 outward, string how)
    {
        Still(follower);
        follower.GrappleGun?.GrappleRope?.SetHidden(false);
        follower.TriggerSpecialMove(shot, Locator(follower, ledge, outward));
        if (follower.CurrentSpecialMove is null)
        {
            PutAway(follower);
            return Refuse("their grapple gun wouldn't fire");
        }

        _ledge = ledge;
        _ledgeOut = outward;
        var far = Vector3.Distance(ledge, follower.Location);
        _grappleSince = World.Now;
        _grappleFor = GrappleGiveUp + far / GrappleSecondEvery;
        _nearest = float.MaxValue;
        _hung.Stop();
        _byHand = false;
        _refused = "";
        Begin(Leg.Fire);
        Log.Info($"{_name} is grappling {how} to {Where}, {far / Metre:0} m.");
        return true;
    }

    // Pulls them up onto an edge within reach, with the move that ends a grapple.
    static bool Mantle(RPawnPlayer follower, Vector3 ledge, Vector3 outward, float up)
    {
        Still(follower);
        _ledge = ledge;
        _ledgeOut = outward;
        if (!Land(follower))
        {
            return Refuse("they have no climbing move");
        }

        _byHand = true;
        _refused = "";
        Log.Info($"{_name} is climbing {up / Metre:0.#} m up to {Where}.");
        return true;
    }

    static bool Land(RPawnPlayer follower)
    {
        if (LandingMove(follower) is not { } move)
        {
            return false;
        }

        follower.TriggerSpecialMove(move, Locator(follower, _ledge, _ledgeOut));
        if (follower.CurrentSpecialMove is null)
        {
            return false;
        }

        Begin(Leg.Landing);
        return true;
    }

    // The game's own jump off a ledge for its partners. It lines them up on the edge, and their
    // AI sees the fall through and lands them.
    static bool Drop(RPawnPlayer follower, Vector3 lip, Vector3 toward, float height)
    {
        var bot = _bot!;
        Still(follower);
        _dropFrom = follower.Location.Z;
        bot.JumpOffLedge(height, toward, lip);
        bot.bNeedsToJumpDown = false;
        bot.GotoState(JumpDown);
        _refused = "";
        Begin(Leg.Drop);
        Log.Info($"{_name} is dropping {height / Metre:0.#} m down to {Where}.");
        return true;
    }

    // Every tick. Sees a vault, a climb, a grapple or a drop through to its end.
    static void Advance()
    {
        if (_leg is Leg.None or Leg.Run)
        {
            return;
        }

        if (Follower is not { } follower)
        {
            ForgetTraversal();
            return;
        }

        var spent = World.Now - _legSince;
        var walking = follower.Physics == Actor.EPhysics.PHYS_Walking;
        _aloft |= !walking;
        switch (_leg)
        {
            case Leg.Fire:
            case Leg.Flight:
                Fly(follower);
                break;

            case Leg.Landing:
                if (follower.CurrentSpecialMove is null)
                {
                    Arrived(follower);
                }
                else if (spent < 0f || spent > LandingMost)
                {
                    GiveUp(follower, "landing");
                }

                break;

            case Leg.Vault:
                if (spent < 0f || spent > VaultMost || (spent > BlockedSeconds && walking && !follower.IsInAnyTransition(default)))
                {
                    Arrived(follower);
                }

                break;

            case Leg.Drop:
                if (spent < 0f || spent > DropGiveUp)
                {
                    GiveUp(follower, "drop");
                }
                else if (spent > BlockedSeconds && walking && follower.CurrentSpecialMove is null && !follower.IsInAnyTransition(default))
                {
                    // Still up at their edge means the jump didn't carry them off it. They
                    // are a step nearer it now, so it is tried again at once.
                    if (follower.Location.Z < _dropFrom - DropTook)
                    {
                        Arrived(follower);
                        break;
                    }

                    if (!_aloft)
                    {
                        Untangle(follower, force: true);
                    }

                    if (++_tries >= GiveUpAfter)
                    {
                        _tries--;
                        Refuse("the jump didn't take them off the edge");
                        break;
                    }

                    _leg = Leg.None;
                    _ready = true;
                }

                break;
        }
    }

    // Without the player's controller the flight doesn't end on the ledge. It runs out short of
    // it in the air, or carries them up past it and turns into a fall. Once it has run out they
    // are pulled in close and given the landing move, and they land as soon as they are close or
    // the moment they start to move away.
    static void Fly(RPawnPlayer follower)
    {
        var spent = World.Now - _grappleSince;
        if (spent < 0f || spent > _grappleFor)
        {
            GiveUp(follower, "grapple");
            return;
        }

        // Firing.
        if (follower.CurrentSpecialMove is not null)
        {
            return;
        }

        var away = _ledge - follower.Location;
        var gap = away.Length();
        if (follower.Physics == Actor.EPhysics.PHYS_Flying)
        {
            _leg = Leg.Flight;
            _nearest = MathF.Min(_nearest, gap);
            var passing = gap > _nearest + LandPassing && _nearest <= LandFar;
            if (gap > LandFrom && !passing)
            {
                if (follower.Velocity.LengthSquared() >= BlockedSpeed * BlockedSpeed || Pull(follower))
                {
                    _hung.Stop();
                    return;
                }

                if (!_hung.Lasted(HungSeconds) || gap > LandFar)
                {
                    return;
                }
            }
        }
        else if (_leg == Leg.Fire)
        {
            // The shot is over and they never left the ground.
            if (spent > FireMost)
            {
                GiveUp(follower, "grapple");
            }

            return;
        }
        else if (follower.Physics == Actor.EPhysics.PHYS_Walking)
        {
            // They came down somewhere by themselves.
            PutAway(follower);
            if (gap <= LandFar)
            {
                Arrived(follower);
            }
            else
            {
                Missed();
            }

            return;
        }
        else if (gap > LandFar)
        {
            return;
        }

        PutAway(follower);
        if (!Land(follower))
        {
            GiveUp(follower, "grapple");
        }
    }

    // A step toward where they will hang. Straight there first. With something in the way, up
    // alone and then across alone, which takes them round an overhang or a beam. False when
    // nothing moved them.
    static bool Pull(RPawnPlayer follower)
    {
        var from = follower.Location;
        var to = _ledge + _ledgeOut * HangOut - Vector3.UnitZ * HangUnder - from;
        var left = to.Length();
        if (left < 1f)
        {
            return false;
        }

        var step = to / left * MathF.Min(left, PullSpeed * World.Delta);
        return Shift(follower, from, step) || Shift(follower, from, new Vector3(0f, 0f, step.Z)) || Shift(follower, from, new Vector3(step.X, step.Y, 0f));
    }

    static bool Shift(RPawnPlayer follower, Vector3 from, Vector3 step) =>
        step.LengthSquared() > 0.01f && follower.SetLocation(from + step) && Vector3.DistanceSquared(follower.Location, from) > 0.01f;

    // A move that ran out of time. Close to the ledge they are stood on it. Further off they are
    // let down where they are.
    static void GiveUp(RPawnPlayer follower, string what)
    {
        var grappling = _leg is Leg.Fire or Leg.Flight or Leg.Landing;
        var close = grappling && Vector3.DistanceSquared(_ledge, follower.Location) <= LandFar * LandFar;
        var ledge = _ledge - _ledgeOut * 80f;
        var yaw = Heading(-_ledgeOut, follower.Rotation.Yaw);
        Untangle(follower, force: true);
        Log.Info($"{_name}'s {what} didn't finish, so they were {(close ? "stood on the ledge" : "put back on their feet")}.");
        if (close)
        {
            Place(follower, ledge, yaw);
            _tries = 0;
            Rest(TraverseRest);
            return;
        }

        Missed();
    }

    // The grapple gun back on the belt and the rope gone, as the player's grapple ends.
    static void PutAway(RPawnPlayer body)
    {
        if (body.GrappleGun is not { IsValid: true } gun)
        {
            return;
        }

        gun.bActive = false;
        gun.bForcedGrapple = false;
        gun.StopGrappleTrails();
        if (gun.Projectile is { IsValid: true, bDeleteMe: false } projectile)
        {
            projectile.Destroy();
        }

        gun.Projectile = null!;
        gun.AttachToBelt();
        gun.GrappleRope?.SetHidden(true);
        body.IsGrappling = false;
    }

    // A body left flying or in the grapple pose with nothing moving it would wander in circles,
    // so it is put back on its feet. Forced, it is done whatever state they are in.
    static void Untangle(RPawnPlayer follower, bool force = false)
    {
        if (!force)
        {
            if (follower.CurrentSpecialMove is not null)
            {
                _parked.Stop();
                return;
            }

            var gun = follower.GrappleGun is { IsValid: true } held ? held : null;
            var left = follower.Physics == Actor.EPhysics.PHYS_Flying ? "flying"
                : Same(follower.GetMovementStance(), GrapplePose) ? "in the grapple pose"
                : gun is not null && (gun.bActive || gun.Projectile is { IsValid: true, bDeleteMe: false }) ? "with their grapple gun out"
                : null;
            if (left is null)
            {
                Unpark(follower);
                return;
            }

            Log.Info($"{_name} was left {left} with no grapple under way, so they were put back on their feet.");
        }

        _leg = Leg.None;
        PutAway(follower);
        follower.StopMoveTo();
        follower.Velocity = Vector3.Zero;
        if (follower.Physics == Actor.EPhysics.PHYS_Flying)
        {
            follower.SetPhysics(Actor.EPhysics.PHYS_Falling);
        }

        if (_bot is not { IsValid: true, bDeleteMe: false } bot)
        {
            return;
        }

        bot.ClearNavigation();
        follower.ChangePose(bot.bCrouched ? Crouching : StandingBot, Relaxed, default, false, default, default, default, 1f);
        if (follower.CurrentSpecialMove is null && Waits(bot))
        {
            bot.GotoState(NoState);
        }
    }

    // The partner AI waits out a special move or a fall in a state of its own, and only some
    // endings take it back out. Left there it never walks or fights again.
    static bool Waits(RRobinBotController bot)
    {
        var state = bot.GetStateName();
        return Same(state, WaitsOnMove) || Same(state, WaitsOnFall);
    }

    static void Unpark(RPawnPlayer follower)
    {
        var bot = _bot!;
        if (follower.Physics != Actor.EPhysics.PHYS_Walking || !Waits(bot))
        {
            _parked.Stop();
            return;
        }

        if (_parked.Lasted(ParkedSeconds))
        {
            _parked.Stop();
            bot.GotoState(NoState);
            Log.Info("sidekick.parked", $"{_name}'s AI was still waiting on a move that had ended, so it was woken.");
        }
    }
}
