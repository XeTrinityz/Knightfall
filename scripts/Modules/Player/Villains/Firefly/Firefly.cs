#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// PLAYER > VILLAINS, FIREFLY. His look goes on through DISGUISE. In the air he flies on his own
// jets, not the game's glide, so the glide's settings leave him alone. The game's fall carries
// him and the jets set his speed each tick. He hovers, flies fast while FLY is held, climbs on
// the jump key and sinks on the crouch key.
static partial class Firefly
{
    const string Lender = "firefly";

    // Take off speed straight up, then how fast the jets lift him to his hover height above
    // where he took off.
    const float LaunchSpeed = 900f;
    const float RiseSpeed = 600f;
    const float LiftHeight = 400f;

    // How fast CLIMB lifts him and DESCEND lowers him in a hover.
    const float ClimbSpeed = 700f;
    const float DescendSpeed = 500f;

    // In a flight he climbs and dives at this share of his flying speed, which is a slope steep
    // enough to see, and eases into it this slowly so it doesn't snap.
    const float FlyClimbShare = 0.55f;
    const float FlyClimbEase = 0.4f;

    // In a flight the camera steers him up and down too. Looking above the first climbs, and
    // looking below the second dives, each at full strength this many degrees further. Between
    // them, where the game's camera rests, he flies level.
    const float LookUpFrom = 0f;
    const float LookDownFrom = -20f;
    const float LookFull = 30f;

    // How quickly a flight gets up to speed, and how fast it turns him toward where he goes.
    const float FlyEase = 0.6f;
    const float FlyTurnRate = 540f;

    // How fast he turns to the crosshair while aiming.
    const float AimTurnRate = 900f;

    // Coming into a hover he brakes to a stop and holds the height he stops at. He counts as
    // stopped once his climb or sink is under this.
    const float Stopped = 60f;

    // A hover holds the height it started at, climbing or sinking back to it at up to this speed.
    const float HoldGain = 3f;
    const float HoldFastest = 400f;

    // The game's fall never turns the body, so in a hover he is turned toward his drift at this
    // rate, once he drifts faster than the other.
    const float HoverTurnRate = 360f;
    const float HoverTurnSpeed = 100f;

    // The pull measured in a tick is believed up to this many times the reported gravity. The
    // game caps how fast you can rise, and a capped tick looks like a huge pull.
    const float MostPull = 3f;

    // How fast he drifts in a hover, and how quickly he gets there or settles.
    const float HoverSpeed = 700f;
    const float HoverEase = 0.4f;
    const float VerticalEase = 0.15f;

    // Two taps of the take off key this close together lift you off.
    const float TapGap = 0.35f;

    // Room needed over your head to take off.
    const float HeadRoom = 400f;

    // How often a look that isn't ready yet is tried again.
    const float DressRetry = 1f;

    // A fall is the falling state, or the moving state the game goes to when a take off lifts you
    // out of a fight stance.
    static readonly FName FallingMode = new("FallingMode");
    static readonly FName CombatMoving = new("CombatMoveControlled");

    // In the air the jump key climbs, so the game's own use of it there, a glide, is kept from it.
    const string JumpKey = "SpaceBar";

    // A fall grabs any ledge it passes and drops onto whatever is close below. The game's own
    // switch for that is held on while he is in the air, so he flies over rooftops and past ledges.
    static readonly Held<RPlayerController, bool> NoLedges = new("firefly ledge grab", controller => controller.bLedgeGrabDisabled, (controller, off) => controller.bLedgeGrabDisabled = off);

    // His own lines from his chases: taunts, his bombs going down and landing, and getting away.
    // The ones that name Batman, his car or the chase itself are left out.
    static readonly VillainVoice Voice = new(
        "firefly voice", new[] { "BARKSET_SIDE-Firefly-AlwaysLoaded-any-ALL_SF" }, new[] { "DLG_SIDE_FIREFLY_" },
        ("taunt", "side_firefly_chase_1_taunts."),
        ("taunt", "side_firefly_chase_2_taunts."),
        ("taunt", "side_firefly_chase_3_taunts."),
        ("dropped", "side_firefly_taunts_bomb_dropped."),
        ("hit", "side_firefly_taunts_bomb_hit."),
        ("escape", "side_firefly_chase_1_escape."))
    {
        Without = new[]
        {
            "Side_Firefly_Chase_1_Taunts_02", "Side_Firefly_Chase_1_Taunts_03", "Side_Firefly_Chase_1_Taunts_05",
            "Side_Firefly_Chase_1_Taunts_07", "Side_Firefly_Chase_1_Taunts_09",
            "Side_Firefly_Chase_2_Taunts_02", "Side_Firefly_Chase_2_Taunts_09", "side_firefly_chase_2_taunts_11",
            "Side_Firefly_Chase_3_Taunts_01", "Side_Firefly_Chase_3_Taunts_02", "Side_Firefly_Chase_3_Taunts_04",
            "Side_Firefly_Chase_3_Taunts_08",
            "Side_Firefly_Taunts_Bomb_Hit_02", "Side_Firefly_Taunts_Bomb_Hit_07",
        },
    };

    // He taunts this often while he flies and nothing else has been said.
    const float TauntEvery = 25f;

    enum Mode
    {
        Grounded,
        Hovering,
        Flying,
    }

    static float _lastTap = -10f;
    static float _liftTo;
    static bool _dressed;
    static bool _propsWere;
    static float _nextDress;
    static bool _hovering;
    static bool _settling;
    static float _hoverHeight;
    static float _wroteZ;
    static bool _jumpTaken;
    static bool _flying;

    // True while he flies fast, for the boost in his smoke.
    static bool Pushing => _flying;

    public static readonly Toggle Option = new(
        "player.villains.firefly", "FIREFLY",
        "Become Firefly, with his own flight, jets and sound. Hold aim and press fire for his flamethrower, or secondary fire for a grenade. His flight keys are below.")
    {
        Tick = Fly,
        Restore = Stop,
    };

    public static readonly Command TakeOffKey = new(
        "player.villains.firefly_take_off", "TAKE OFF / CLIMB",
        "Double tap on the ground to lift off into a hover. Hold it in the air to climb. It shares the jump key, so a single tap on the ground still runs.",
        TakeOff)
    {
        Quiet = true,
    };

    public static readonly Command FlyKey = new(
        "player.villains.firefly_fly", "FLY",
        "Hold in the air to fly fast where you steer, or where the camera looks with the stick at rest. Look up to climb and down to dive.",
        static () => Outcome.Done("FLY"))
    {
        Quiet = true,
    };

    public static readonly Command DescendKey = new(
        "player.villains.firefly_descend", "DESCEND",
        "Hold to go down, hovering or flying. Let go to hold the new height. It shares the crouch key.",
        static () => Outcome.Done("DESCEND"))
    {
        Quiet = true,
    };

    public static readonly Slider FlySpeed = new(
        "player.villains.firefly_fly_speed", "FLY SPEED",
        "How fast he flies with FLY held. In his chase he flies at about 26.",
        min: 10, max: 60, step: 2, value: 26, unit: " M/S")
    {
        Preference = true,
    };

    public static readonly Slider CameraBack = new(
        "player.villains.firefly_camera_back", "CAMERA DISTANCE",
        "How much further back the camera sits while you are Firefly, to fit his jets in. It sits half as far again in a flight.",
        min: 0, max: 5, step: 0.25, value: 1.5, unit: " M")
    {
        Preference = true,
    };

    public static readonly Toggle Hud = new(
        "player.villains.firefly_hud", "HUD",
        "Shows his fire badge, nozzle heat, thugs torched, cars lit, speed, height, grenade and controls in the bottom left while you are Firefly.",
        on: true)
    {
        Preference = true,
    };

    // The keys he comes with. The player's own are read after these.
    public static void DefaultKeys()
    {
        Hotkeys.Add(TakeOffKey, key: "Space", pad: "A");
        Hotkeys.Add(FlyKey, key: "ShiftKey", pad: "LeftStick");
        Hotkeys.Add(DescendKey, key: "ControlKey", pad: "RT");
    }

    public static readonly Catalog.Section[] Sections =
    {
        new("FIREFLY", Option, TakeOffKey, FlyKey, DescendKey, FlySpeed, CameraBack, Hud),
    };

    static Outcome TakeOff()
    {
        if (!Option.On)
        {
            return Outcome.Unchanged("FIREFLY IS OFF");
        }

        // In the air the key climbs while it is held.
        if (_hovering)
        {
            return Outcome.Done("CLIMB");
        }

        var now = World.Now;
        var second = now - _lastTap <= TapGap;
        _lastTap = second ? -10f : now;
        if (!second)
        {
            return Outcome.Unchanged("TAP AGAIN");
        }

        if (World.Driving is not null)
        {
            return Outcome.Unchanged("NOT IN THE CAR");
        }

        if (World.Player is not { IsValid: true } player || World.Controller is not { } controller)
        {
            return Outcome.NoPlayer;
        }

        if (player.Physics != Actor.EPhysics.PHYS_Walking)
        {
            return Outcome.Unchanged("ALREADY UP");
        }

        if (controller.bCinematicMode || player.CurrentSpecialMove is { IsValid: true })
        {
            return Outcome.Failed("NOT NOW");
        }

        if (!player.FastTrace(player.Location + new Vector3(0f, 0f, HeadRoom), player.Location, Vector3.Zero, false))
        {
            return Outcome.Failed("ROOF ABOVE");
        }

        // The game's own walking state sees the fall and takes it from there.
        player.SetPhysics(Actor.EPhysics.PHYS_Falling);
        player.Velocity = player.Velocity with { Z = LaunchSpeed };
        _liftTo = player.Location.Z + LiftHeight;
        Voice.Speak("escape", always: true);
        return Outcome.Done("TAKE OFF");
    }

    static void Fly()
    {
        if (!Villains.Claim(Option))
        {
            Option.SetTo(false);
            return;
        }

        Dress();
        Load();

        if (World.Player is not { IsValid: true } player || World.Controller is not { } controller
            || World.Driving is not null || controller.bCinematicMode)
        {
            _hovering = false;
            _flying = false;
            TakeJump(false);
            NoLedges.Release();
            Villains.Release();
            Voice.Release();
            Show(null, Mode.Grounded);
            VillainHud.Show(null);
            StopWeapons();
            return;
        }

        if (player.Physics == Actor.EPhysics.PHYS_Walking || player.Location.Z >= _liftTo)
        {
            _liftTo = 0f;
        }

        Villains.Keep(player, controller, "DLG_Voices.firefly");

        // A glide that slips in, from a grapple or a press before the key was kept, is ended so
        // the jets take over.
        if (player.bGliding)
        {
            controller.GotoState(FallingMode);
        }

        var mode = Mode.Grounded;
        if (player.Physics == Actor.EPhysics.PHYS_Falling && !player.bGliding
            && (controller.IsInState(FallingMode) || controller.IsInState(CombatMoving))
            && player.CurrentSpecialMove is not { IsValid: true })
        {
            var flying = Hotkeys.IsHeld(FlyKey);
            Move(player, controller, flying, Hotkeys.IsHeld(TakeOffKey), Hotkeys.IsHeld(DescendKey) && !Aiming);
            mode = flying ? Mode.Flying : Mode.Hovering;
        }

        _hovering = mode != Mode.Grounded;
        _flying = mode == Mode.Flying;
        TakeJump(_hovering);
        if (_hovering)
        {
            NoLedges.Hold(controller, true);
        }
        else
        {
            NoLedges.Release();
        }

        Show(player, mode);
        if (_hovering)
        {
            Voice.Quiet("taunt", TauntEvery);
        }

        Weapons(player, controller, _hovering);
        ShowHud(player, mode);
    }

    // His look goes on once each time FIREFLY is turned on. Changing DISGUISE after that is left alone.
    static void Dress()
    {
        if (_dressed || World.Now < _nextDress)
        {
            return;
        }

        _nextDress = World.Now + DressRetry;
        var propsWere = Disguise.Props.On;
        Disguise.Props.SetTo(true);
        if (Disguise.Put(LookPackage))
        {
            _dressed = true;
            _propsWere = propsWere;
        }
        else
        {
            Disguise.Props.SetTo(propsWere);
        }
    }

    static void TakeJump(bool take)
    {
        if (take == _jumpTaken)
        {
            return;
        }

        _jumpTaken = take;
        if (take)
        {
            Swallowed.Take(JumpKey);
            Swallowed.Take(Pad.Button.A);
        }
        else
        {
            Swallowed.Give(JumpKey);
            Swallowed.Give(Pad.Button.A);
        }
    }

    // The fall pulls harder than the gravity the game reports, so its pull is measured as what
    // it took off the speed written last tick, and given ahead of the next tick. He goes the way
    // the stick points, and in a flight straight ahead of the camera when it is at rest.
    // The fastest he climbs or dives in a flight.
    static float FlyClimb => (float)FlySpeed.Value * Teleport.UnitsPerMetre * FlyClimbShare;

    static void Move(RPawnPlayer player, RPlayerController controller, bool flying, bool climbing, bool descending)
    {
        var rising = _liftTo > 0f || climbing;
        var up = flying ? FlyClimb : ClimbSpeed;
        var down = flying ? FlyClimb : DescendSpeed;

        // The keys win over the camera, and aiming leaves the camera free to look at a target.
        var look = 0f;
        if (flying && !rising && !descending && !Aiming)
        {
            var pitch = controller.Rotation.Pitch;
            pitch -= 360f * MathF.Round(pitch / 360f);
            look = pitch > LookUpFrom ? Math.Clamp((pitch - LookUpFrom) / LookFull, 0f, 1f)
                : pitch < LookDownFrom ? -Math.Clamp((LookDownFrom - pitch) / LookFull, 0f, 1f) : 0f;
        }

        var steering = look != 0f;
        var delta = World.Delta;
        var velocity = player.Velocity;
        var height = player.Location.Z;
        var gravity = MathF.Abs(player.GetGravityZ()) * delta;
        var pull = gravity;
        if (!_hovering)
        {
            _settling = true;
        }
        else if (float.IsFinite(velocity.Z))
        {
            pull = Math.Clamp(_wroteZ - velocity.Z, 0f, gravity * MostPull);
        }

        if (_settling && MathF.Abs(velocity.Z) < Stopped)
        {
            _settling = false;
        }

        // Lifting or sinking, the height follows him, and once he lets go he settles where he stops.
        if (rising || descending || steering || _settling)
        {
            _settling |= rising || descending || steering;
            _hoverHeight = height;
        }

        // The pull given ahead last tick has already been taken, so the speed read is the real
        // one. It eases to the wanted climb and the next tick's pull is given ahead. Nothing
        // written is ever past the fastest climb or sink.
        var climb = climbing ? up : _liftTo > 0f ? RiseSpeed : descending ? -down : steering ? look * FlyClimb
            : _settling ? 0f : Math.Clamp((_hoverHeight - height) * HoldGain, -HoldFastest, HoldFastest);
        var most = MathF.Max(FlyClimb, MathF.Max(ClimbSpeed, RiseSpeed));
        var now = float.IsFinite(velocity.Z) ? Math.Clamp(velocity.Z, -most, most) : 0f;
        now += (climb - now) * Settle(delta, flying ? FlyClimbEase : VerticalEase);
        velocity.Z = now + pull;
        _wroteZ = velocity.Z;

        var push = player.Acceleration with { Z = 0f };
        var way = push.LengthSquared() > 1f ? Vector3.Normalize(push)
            : flying ? Teleport.Flat(controller.Rotation.ToDirection()) : Vector3.Zero;
        var speed = flying ? (float)FlySpeed.Value * Teleport.UnitsPerMetre : HoverSpeed;
        var drift = Settle(delta, flying ? FlyEase : HoverEase);
        velocity.X += (way.X * speed - velocity.X) * drift;
        velocity.Y += (way.Y * speed - velocity.Y) * drift;
        player.Velocity = velocity;

        // Aiming he turns to the crosshair, whichever way he drifts.
        var facing = Aiming ? Teleport.Flat(controller.Rotation.ToDirection()) * HoverSpeed : velocity;
        Face(player, facing, delta, Aiming ? AimTurnRate : flying ? FlyTurnRate : HoverTurnRate);
    }

    static void Face(RPawnPlayer player, Vector3 velocity, float delta, float rate)
    {
        if (velocity.X * velocity.X + velocity.Y * velocity.Y < HoverTurnSpeed * HoverTurnSpeed)
        {
            return;
        }

        var facing = player.Rotation;
        var wanted = MathF.Atan2(velocity.Y, velocity.X) * (180f / MathF.PI);
        var turn = wanted - facing.Yaw;
        turn -= 360f * MathF.Round(turn / 360f);
        var most = rate * delta;
        player.SetRotation(new Rotator(0f, facing.Yaw + Math.Clamp(turn, -most, most), 0f));
    }

    static float Settle(float delta, float seconds) => 1f - MathF.Exp(-delta / seconds);

    static void Stop()
    {
        VillainHud.Show(null);
        StopWeapons();
        TakeJump(false);
        NoLedges.Release();
        Villains.Release();
        Voice.Unload();
        ResetHud();
        _flying = false;
        Unload();
        _liftTo = 0f;
        _hovering = false;
        _nextDress = 0f;
        if (_dressed)
        {
            _dressed = false;
            Disguise.TakeOffLook(LookPackage);
            Disguise.Props.SetTo(_propsWere);
        }
    }
}
