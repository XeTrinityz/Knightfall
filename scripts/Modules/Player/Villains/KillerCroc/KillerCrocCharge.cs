#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// His charge from his fight. He winds up and launches, runs head down where the camera looks,
// bowling over everyone in his path and smashing through what breaks, then digs his feet in and
// stops. Nothing he meets stops him, and hits don't rock him while he runs.
static partial class KillerCroc
{
    enum Leg
    {
        In,
        Run,
        Out,
    }

    static readonly FName ChargeIn = new("Tripple_Strike_In");
    static readonly FName ChargeRun = new("Tripple_Strike_Loop");
    static readonly FName ChargeOut = new("Tripple_Strike_Out");

    // How long he runs at most, how hard he can steer while running, and how long it takes to
    // build up again.
    const float RunSeconds = 2.4f;
    const float ChargeTurnRate = 100f;
    const float ChargeWait = 4f;

    // His stop has him stood up by this far in. The rest of it holds still, so he goes back to
    // standing from here.
    const float StopSeconds = 1.15f;

    // He stops short of a wall this close ahead, traced at his chest.
    const float WallAhead = 180f;
    const float ChestHeight = 40f;

    // Who his charge meets. His fight bowls over anyone within 1.5 m of his head, pushed off to
    // the side they were on, half ahead and half aside.
    const float BowlAhead = 80f;
    const float BowlReach = 170f;
    const float BowlImpulse = 4000f;

    // A vehicle he runs into loses this share of its full health, a parked car this much, and
    // the charge stops against it as against a wall.
    const float RamWreck = 0.34f;
    const int RamParked = 40;
    static bool _rammed;
    const int BowlDamage = 20;

    // The shake and rumble as he digs in. A wall shakes harder.
    const float StopShake = 1f;
    const float WallShake = 2f;
    const string ShakeFrom = "BmGame.Default__RCombatMove_BatmanBatclawSlam";

    static Leg _leg;
    static float _runUntil;
    static float _nextCharge;
    static float _lastTime;
    static readonly HashSet<nint> Bowled = new();
    static readonly HashSet<nint> Smashed = new();

    static Outcome Charge()
    {
        if (!Option.On || _body is not { IsValid: true } || World.Player is not { IsValid: true } player || World.Controller is not { } controller)
        {
            return Outcome.Unchanged("CROC IS OFF");
        }

        if (Acting || player.Physics != Actor.EPhysics.PHYS_Walking || player.CurrentSpecialMove is { IsValid: true } || controller.bCinematicMode)
        {
            return Outcome.Unchanged("NOT NOW");
        }

        if (World.Now < _nextCharge)
        {
            return Outcome.Unchanged("RECOVERING");
        }

        if (Play(ChargeIn, player.Rotation.Yaw) is null)
        {
            return Outcome.Unchanged("NOT READY");
        }

        _move = Move.Charge;
        _leg = Leg.In;
        Bowled.Clear();
        Smashed.Clear();
        _rammed = false;
        Hold(controller);
        Speech.Speak("charge", always: true);
        return Outcome.Done("CHARGE");
    }

    // Until he digs in, he heads where the camera looks.
    static void Steer()
    {
        if (_move != Move.Charge || _leg == Leg.Out || World.Controller is not { } controller)
        {
            return;
        }

        _aim = controller.Rotation.Yaw;
    }

    static float TurnRateNow => _move == Move.Charge && _leg == Leg.Run ? ChargeTurnRate : TurnRate;

    // Each tick of the charge. True while it runs on, false once he has dug in and stood up.
    static bool Charging(RPawnPlayer player, AnimNodeSequence played)
    {
        var ahead = new Rotator(0f, _base, 0f).ToDirection() with { Z = 0f };
        ahead = Vector3.Normalize(ahead);
        if (_leg != Leg.Out)
        {
            Bowl(player, ahead);
        }

        switch (_leg)
        {
            case Leg.In:
                if (played.CurrentTime >= played.GetAnimPlaybackLength() - EndEarly && Play(ChargeRun, _base, loop: true) is not null)
                {
                    _leg = Leg.Run;
                    _runUntil = World.Now + RunSeconds;
                }

                return true;
            case Leg.Run:
                var wall = _rammed || Walled(player, ahead);
                if ((wall || World.Now >= _runUntil) && Play(ChargeOut, _base) is not null)
                {
                    _leg = Leg.Out;
                    Stomp(player, wall ? WallShake : StopShake);
                }

                return true;
            default:
                if (played.CurrentTime >= StopSeconds)
                {
                    Unact();
                }

                return true;
        }
    }

    // Everyone close to his head is bowled aside the way his fight throws them, once each.
    // What breaks there breaks.
    static void Bowl(RPawnPlayer player, Vector3 ahead)
    {
        var head = player.Location + ahead * BowlAhead;
        foreach (var thug in Crowd.StandingEnemies())
        {
            var off = Level(thug.Location - head);
            if (off.Length() > BowlReach || MathF.Abs(thug.Location.Z - player.Location.Z) > SwipeHeight
                || thug.DrivenVehicle is { IsValid: true } || thug.Base is Vehicle || !Bowled.Add(thug.Ptr))
            {
                continue;
            }

            var side = Vector3.Cross(Vector3.UnitZ, ahead);
            var push = Vector3.Normalize(ahead + side * (Vector3.Dot(off, side) >= 0f ? 1f : -1f));
            thug.DamagedBy(new RPawnCombat.FDamageInfo
            {
                Attacker = player,
                BoneName = Chest,
                Impulse = push * BowlImpulse,
                DamageAmount = BowlDamage,
                StunTime = DazedSeconds,
                DamageType = RDmgType_ExplosiveGel.StaticClass(),
                WritheAnim = BlownUp,
                bHeavyHit = true,
            });

            _thrown++;
            _thrownAt = World.Now;
            if (_thud is { IsValid: true } thud)
            {
                player.StartAudioEvent(thud);
            }
        }

        Villains.Break(player, head, BowlReach, Smashed);
        if (Villains.Wreck(player, head, BowlReach, RamWreck, RamParked, Smashed) > 0)
        {
            _rammed = true;
        }
    }

    static bool Walled(RPawnPlayer player, Vector3 ahead)
    {
        var from = player.Location + Vector3.UnitZ * ChestHeight;
        return Crosshair.Line(player, from, from + ahead * WallAhead, Crosshair.Meets.Level, out _);
    }

    // The ground shakes as he digs in, with the slam's own shake and a heavy rumble.
    static void Stomp(RPawnPlayer player, float scale)
    {
        if (player.Controller is not RPlayerController { IsValid: true } controller)
        {
            return;
        }

        if (Game.FindObject<RCombatMove_BatmanBatclawSlam>(ShakeFrom) is { IsValid: true } slam)
        {
            controller.AddScreenShake(slam.ImpactScreenShake, scale, player.Location);
        }

        if (controller is RPlayerControllerCombat combat)
        {
            combat.PlayImpactRumble(MathF.Min(100f, 80f * scale), MathF.Min(100f, 60f * scale), 0.3f * scale);
        }
    }
}
