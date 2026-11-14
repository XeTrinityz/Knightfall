#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using Pace = BmSDK.Engine.RAutomaticTransitions.EMovementSpeed;

namespace Knightfall;

// Vaults and grapples. The story's partners have neither, so a fence or a roof stopped them. Any
// hero can play the player's own vault, and fires their own grapple gun the way the player
// does. The player's controller finishes a grapple onto the ledge, and a sidekick has none, so
// that last part is done here with the game's own landing move.
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
    const float BlockedSpeed = 40f;
    const float BlockedSeconds = 0.4f;
    const float TraverseRest = 1.5f;

    // A grapple is for a player standing at least this far above them, this near, when they have
    // stood blocked this long.
    const float GrappleRise = 250f;
    const float GrappleNear = Metre * 40f;
    const float GrappleWait = 1.5f;
    const float EdgeStep = 50f;
    const float EdgeDrop = 200f;
    const float EdgeStepDown = 120f;
    const float EdgeSearchPast = 600f;
    const float EdgeSearchMost = Metre * 30f;

    static readonly (float Out, float Up)[] SightPoints = { (60f, 60f), (60f, 150f), (120f, 250f), (0f, 250f) };

    // The flight stops short of the ledge, and the landing move covers the rest. Pulled in to
    // this far first at this speed, so the landing doesn't jump.
    const float LandFrom = 200f;

    // Within this far, a flight that turns away from the ledge or into a fall lands at once.
    const float LandFar = Metre * 12f;
    const float LandPassing = 50f;

    static bool _flown;
    static float _nearest = float.MaxValue;
    const float PullSpeed = 2500f;
    const float GrappleGiveUp = 6f;

    static float _blockedSince = -1f;
    static float _restUntil = -100f;
    static float _lowSince = -1f;

    static bool _grappling;
    static Vector3 _grappleTo;
    static Vector3 _grappleOut;
    static float _grappleSince;
    static long _flewAt;

    static AnimSet? _climbSet;
    static RSpecialMoveConfig? _landing;

    static bool Grappling => _grappling;

    // A new world starts game time over, so a rest that reaches further ahead than one could is
    // from the old world.
    static bool Resting => World.Now < _restUntil && _restUntil - World.Now <= TraverseRest;

    // From the upkeep. True while a grapple is under way, when nothing else should move them.
    static bool Traverse(RPlayerController controller, RPawnPlayer follower)
    {
        if (_grappling || Approach(follower))
        {
            return true;
        }

        Untangle(follower);
        if (!Climbs.On || controller.IsInCombat(false) || follower.CurrentSpecialMove is not null || follower.CombatMove is not null
            || follower.Physics != Actor.EPhysics.PHYS_Walking || Resting)
        {
            _blockedSince = -1f;
            _lowSince = -1f;
            return false;
        }

        var speed = new Vector2(follower.Velocity.X, follower.Velocity.Y).Length();
        if (Goal(controller, out var goal, out var facing) && (Overhead(follower, goal, facing, speed) || Across(follower, goal, speed)))
        {
            return true;
        }

        if (speed > BlockedSpeed || !Trying(follower))
        {
            _blockedSince = -1f;
            return false;
        }

        if (_blockedSince < 0f || World.Now < _blockedSince)
        {
            _blockedSince = World.Now;
        }

        if (World.Now - _blockedSince >= BlockedSeconds && Vault(follower))
        {
            _blockedSince = -1f;
            _restUntil = World.Now + TraverseRest;
        }

        return false;
    }

    // A player standing on a roof above them, with the sidekick standing still below for a
    // moment, is grappled up to.
    static bool Overhead(RPawnPlayer follower, Vector3 goal, float facing, float speed)
    {
        if (goal.Z - Feet(follower).Z < GrappleRise || Flat(goal, follower.Location) > GrappleNear * GrappleNear || speed > BlockedSpeed)
        {
            _lowSince = -1f;
            return false;
        }

        if (_lowSince < 0f || World.Now < _lowSince)
        {
            _lowSince = World.Now;
        }

        if (World.Now - _lowSince < GrappleWait)
        {
            return false;
        }

        _lowSince = -1f;
        if (!GrappleUp(follower, goal, facing))
        {
            _restUntil = World.Now + TraverseRest;
            return false;
        }

        return true;
    }

    // Why a grapple didn't happen, once per reason, for the support log.
    static string _grappleRefused = "";

    static bool Refuse(string why)
    {
        if (why != _grappleRefused)
        {
            _grappleRefused = why;
            Log.Info($"{_name} can't reach you: {why}.");
        }

        return false;
    }

    // Where they are trying to get to: a spot they were ordered to hold, or the player on their
    // feet. Nowhere while they go after a target.
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
            return true;
        }

        if (controller.Pawn is not RPawnPlayer player || player.Physics != Actor.EPhysics.PHYS_Walking)
        {
            return false;
        }

        goal = Feet(player);
        facing = player.Rotation.Yaw;
        return true;
    }

    // A run of their own to somewhere the partner AI has no path to, like the foot of a wall or
    // the edge of their roof. The follow and a held spot wait until they are there, or a few
    // seconds have gone.
    const float UnderEdge = 200f;
    const float ApproachMost = 4f;

    static Vector3 _approachTo;
    static float _approachUntil = -100f;

    static bool Approaching => World.Now < _approachUntil && _approachUntil - World.Now <= ApproachMost;

    static void RunTo(RPawnPlayer follower, Vector3 at)
    {
        _bot!.ClearNavigation();
        _bot.bWasMoving = false;
        _walkedAt = -100f;
        _approachTo = at;
        _approachUntil = World.Now + ApproachMost;
        follower.SetMovementSpeed(Pace.MS_Run, 1f, default);
        follower.MoveTo(at);
    }

    static bool Approach(RPawnPlayer follower)
    {
        if (!Approaching)
        {
            return false;
        }

        if (Flat(_approachTo, follower.Location) > HoldClose * HoldClose)
        {
            return true;
        }

        _approachUntil = -100f;
        follower.StopMoveTo();
        return false;
    }

    // Walks out from a point until the floor drops away. The last floor before the drop, or
    // null when the floor runs on that far.
    static Vector3? EdgeToward(Actor tracer, Vector3 feet, Vector3 toward, float reach)
    {
        var last = feet;
        for (var step = EdgeStep; step <= reach; step += EdgeStep)
        {
            if (!Teleport.Floor(tracer, feet + toward * step, feet.Z + 80f, feet.Z - EdgeDrop, out var floor) || floor.Z < last.Z - EdgeStepDown)
            {
                return last;
            }

            last = floor;
        }

        return null;
    }

    // The first gap on the line between two floors: the last floor before it and the first
    // floor after it. Anything more than a short drop below both floors counts as the gap.
    static bool GapBetween(Actor tracer, Vector3 from, Vector3 to, Vector3 toward, float apart, out Vector3 mine, out Vector3 other)
    {
        mine = from;
        other = to;
        var top = MathF.Max(from.Z, to.Z) + 80f;
        var low = MathF.Min(from.Z, to.Z) - EdgeDrop;
        var last = from;
        var open = false;
        for (var step = EdgeStep; step <= apart; step += EdgeStep)
        {
            if (!Teleport.Floor(tracer, from + toward * step, top, low, out var floor))
            {
                if (!open)
                {
                    open = true;
                    mine = last;
                }

                continue;
            }

            if (open)
            {
                other = floor;
                return true;
            }

            last = floor;
        }

        return false;
    }

    // A player on another roof across a gap, level with them or lower, is grappled across to. The
    // story's partners only follow paths, and no path crosses a gap between roofs.
    const float CrossFrom = Metre * 3f;
    const float CrossDrop = Metre * 10f;

    static float _gapSince = -1f;


    static bool Across(RPawnPlayer follower, Vector3 goal, float speed)
    {
        var bot = _bot!;
        if (speed > BlockedSpeed)
        {
            _gapSince = -1f;
            return false;
        }

        var rise = goal.Z - Feet(follower).Z;
        var gap = Flat(goal, follower.Location);
        var near = bot.StandBehindDist + StuckShort;
        if (rise >= GrappleRise || rise < -CrossDrop || gap > GrappleNear * GrappleNear || gap < near * near)
        {
            _gapSince = -1f;
            return false;
        }

        if (_gapSince < 0f || World.Now < _gapSince)
        {
            _gapSince = World.Now;
        }

        if (World.Now - _gapSince < GrappleWait)
        {
            return false;
        }

        _gapSince = -1f;
        if (!Cross(follower, goal))
        {
            _restUntil = World.Now + TraverseRest;
            return false;
        }

        return true;
    }

    static bool Cross(RPawnPlayer follower, Vector3 theirs)
    {
        var feet = Feet(follower);
        var toward = theirs - feet;
        toward.Z = 0f;
        var apart = toward.Length();
        if (apart < 1f)
        {
            return false;
        }

        toward /= apart;
        if (!GapBetween(follower, feet, theirs, toward, apart, out var mine, out var other))
        {
            return Refuse("there is no gap between you");
        }

        // From well back from their own edge they can't see the far side, so they run up first.
        if (Flat(mine, feet) > CrossFrom * CrossFrom)
        {
            RunTo(follower, mine - toward * 40f);
            return true;
        }

        return GrappleAcrossTo(follower, other, -toward, MathF.Sqrt(Flat(mine, other)));
    }

    // Whether the partner AI or an order is trying to move them right now.
    static bool Trying(RPawnPlayer follower)
    {
        var bot = _bot!;
        if (_steering || (bot.bInKismetMove && !_arrived) || bot.bWasMoving)
        {
            return true;
        }

        return Game.TryGetPlayerController(out var controller) && controller.Pawn is { } player && Following(controller) && !Ordered
            && Flat(player.Location, follower.Location) > MathF.Pow(bot.StandBehindDist + StuckShort, 2f);
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
        if (follower.FastTrace(knee + way * VaultLook, knee, Vector3.Zero, false))
        {
            return false;
        }

        var above = feet + way * (VaultLook * 0.7f);
        if (!Teleport.Floor(follower, above, feet.Z + VaultHighest + 40f, feet.Z, out var top))
        {
            return false;
        }

        var height = top.Z - feet.Z;
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
        return true;
    }

    // The game's lookup by name runs its native version without the caller's defaults and comes
    // back empty, so the sequence is put in by hand.
    static RAnimConfig.FCustomAnimConfig? ClimbAnim(string name)
    {
        if (_climbSet is not { IsValid: true })
        {
            _climbSet = Game.FindObjects<AnimSet>().FirstOrDefault(set => set.IsValid && set.GetPathName() == ClimbSet);
        }

        if (_climbSet is null)
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

    // The edge of the player's roof on the sidekick's side, found by walking out from the player
    // until the floor drops away. They grapple to it only when they can see it.
    static bool GrappleUp(RPawnPlayer follower, Vector3 start, float facing)
    {
        if (follower.GrappleGun is not { IsValid: true } gun || gun.GrappleFireMove is not { IsValid: true } fire)
        {
            return Refuse("they have no grapple gun");
        }

        var toward = follower.Location - start;
        toward.Z = 0f;
        if (toward.LengthSquared() < 1f)
        {
            toward = -new Rotator(0f, facing, 0f).ToDirection();
            toward.Z = 0f;
        }

        if (toward.LengthSquared() < 0.01f)
        {
            return Refuse("there is no way to face");
        }

        // The edge can lie past them when they stand close under it, so the search goes on a
        // little further than the gap.
        toward = Vector3.Normalize(toward);
        var reach = MathF.Min(MathF.Sqrt(Flat(start, follower.Location)) + EdgeSearchPast, EdgeSearchMost);
        if (EdgeToward(follower, start, toward, reach) is not { } ledge)
        {
            return Refuse("no edge was found between you");
        }

        if (ledge.Z - Feet(follower).Z < GrappleRise)
        {
            return Refuse("the edge isn't above them");
        }

        var seen = Sees(follower, ledge, toward);

        // From the floor under the edge they can see it, so they run there first.
        if (!seen)
        {
            if (Teleport.Floor(follower, ledge + toward * UnderEdge, ledge.Z, Feet(follower).Z - 300f, out var under)
                && Flat(under, follower.Location) > HoldClose * HoldClose)
            {
                RunTo(follower, under);
                return true;
            }

            return Refuse("they can't see the edge");
        }

        return FireAt(follower, fire, ledge, toward, "up to you");
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

    // Fires their own grapple gun at a ledge. Outward points from the ledge back toward them.
    static bool FireAt(RPawnPlayer follower, RSpecialMoveConfig fire, Vector3 ledge, Vector3 outward, string where)
    {
        _grappleRefused = "";
        Log.Info($"{_name} is grappling {where}.");
        _bot!.ClearNavigation();
        _walkedAt = -100f;
        follower.StopMoveTo();
        follower.GrappleGun?.GrappleRope?.SetHidden(false);
        var locator = new REnvironmentCheckTicker.FEnvironmentSpecialMoveLocator();
        locator.Location = ledge;
        locator.Normal = outward;
        locator.Distance = Vector3.Distance(ledge, follower.Location);
        follower.TriggerSpecialMove(fire, locator);
        _grappling = true;
        _grappleTo = ledge;
        _grappleOut = outward;
        _grappleSince = World.Now;
        _flewAt = 0;
        _flown = false;
        _nearest = float.MaxValue;
        return true;
    }

    // A gap too wide to jump but within a grapple is grappled across, to the far edge.
    const float GrappleAcross = Metre * 25f;

    static bool GrappleAcrossTo(RPawnPlayer follower, Vector3 far, Vector3 outward, float width)
    {
        if (width > GrappleAcross)
        {
            return Refuse($"the gap is too wide to cross ({width / Metre:0} m)");
        }

        if (follower.GrappleGun is not { IsValid: true } gun || gun.GrappleFireMove is not { IsValid: true } fire)
        {
            return Refuse("they have no grapple gun");
        }

        if (!Sees(follower, far, outward))
        {
            return Refuse("they can't see the other side");
        }

        return FireAt(follower, fire, far, outward, "across to you");
    }

    // Every tick while a grapple is under way. Once the flight has run out they are pulled in
    // close to the ledge and given the landing move.
    static void FlyGrapple()
    {
        if (!_grappling)
        {
            return;
        }

        if (_bot?.BotPawn is not { IsValid: true, bDeleteMe: false } follower)
        {
            _grappling = false;
            return;
        }

        if (World.Now < _grappleSince || World.Now - _grappleSince > GrappleGiveUp)
        {
            Untangle(follower, force: true);
            return;
        }

        // Firing, or already landing.
        if (follower.CurrentSpecialMove is not null)
        {
            _flewAt = 0;
            return;
        }

        var away = _grappleTo - follower.Location;
        var gap = away.Length();

        // Without the player's controller the flight doesn't end on the ledge. It runs out short
        // of it in the air, or carries them up past it and turns into a fall. They land as soon
        // as they are close, or the moment they start to move away from it.
        if (follower.Physics == Actor.EPhysics.PHYS_Flying)
        {
            _flown = true;
            if (gap < _nearest)
            {
                _nearest = gap;
            }

            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var seconds = _flewAt == 0 ? 0f : MathF.Min((float)((now - _flewAt) / (double)System.Diagnostics.Stopwatch.Frequency), 0.1f);
            _flewAt = now;
            var passing = gap > _nearest + LandPassing && _nearest <= LandFar;
            if (gap > LandFrom && !passing)
            {
                if (follower.Velocity.LengthSquared() < BlockedSpeed * BlockedSpeed)
                {
                    follower.SetLocation(follower.Location + away / gap * MathF.Min(gap - LandFrom, PullSpeed * seconds));
                }

                return;
            }
        }
        else if (!_flown)
        {
            return;
        }
        else if (follower.Physics == Actor.EPhysics.PHYS_Walking)
        {
            // They came down somewhere by themselves.
            PutAway(follower);
            _grappling = false;
            _restUntil = World.Now + TraverseRest;
            return;
        }
        else if (gap > LandFar)
        {
            return;
        }

        if (_landing is not { IsValid: true })
        {
            _landing = Game.FindObjects<RSpecialMoveConfig>().FirstOrDefault(config => config.IsValid && config.GetPathName() == GrappleLanding);
        }

        PutAway(follower);
        _grappling = false;
        if (_landing is null)
        {
            Untangle(follower, force: true);
            Place(follower, _grappleTo - _grappleOut * 80f, Heading(-_grappleOut, follower.Rotation.Yaw));
            return;
        }

        var locator = new REnvironmentCheckTicker.FEnvironmentSpecialMoveLocator();
        locator.Location = _grappleTo;
        locator.Normal = _grappleOut;
        locator.Distance = gap;
        follower.TriggerSpecialMove(_landing, locator);
        _restUntil = World.Now + TraverseRest;
    }

    // A reload forgets a grapple under way, and the follow would then take over mid-air with the
    // rope still out. So before this copy goes, a grapple is ended and they are put on the ledge.
    static void EndGrapple()
    {
        if (!_grappling || _bot?.BotPawn is not { IsValid: true, bDeleteMe: false } follower)
        {
            _grappling = false;
            return;
        }

        var ledge = _grappleTo - _grappleOut * 80f;
        var yaw = Heading(-_grappleOut, follower.Rotation.Yaw);
        Untangle(follower, force: true);
        Place(follower, ledge, yaw);
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

    // A body left flying or in the grapple pose with nothing moving it would wander in circles.
    // It is put back on its feet.
    static readonly FName GrapplePose = new("Grapple");

    static void Untangle(RPawnPlayer follower, bool force = false)
    {
        var gun = follower.GrappleGun is { IsValid: true } held ? held : null;
        var tangled = follower.Physics == Actor.EPhysics.PHYS_Flying || Same(follower.GetMovementStance(), GrapplePose)
            || (gun is not null && (gun.bActive || gun.Projectile is { IsValid: true, bDeleteMe: false }));
        if (!force && (follower.CurrentSpecialMove is not null || !tangled))
        {
            return;
        }

        _grappling = false;
        PutAway(follower);
        _bot?.ClearNavigation();
        follower.StopMoveTo();
        follower.Velocity = Vector3.Zero;
        if (follower.Physics == Actor.EPhysics.PHYS_Flying)
        {
            follower.SetPhysics(Actor.EPhysics.PHYS_Falling);
        }

        follower.ChangePose(StandingBot, Relaxed, default, false, default, default, default, 1f);
        Log.Info($"{_name} was left mid-grapple, so they were put back on their feet.");
    }
}
