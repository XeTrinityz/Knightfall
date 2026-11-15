#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// SWOOP. He dives at the enemy nearest the crosshair, then does what he did to Batman on the
// rooftop the night he was first seen. He drops onto him and pins him, rears up, slashes him
// down for good and launches back into the sky. The ground his attack covers carries the pawn,
// so he really goes up and away at the end.
static partial class ManBat
{
    // What he is doing besides flying where he is steered.
    enum Act
    {
        None,
        Dive,
        Strike,
        Screech,
    }

    const string ImpactPath = "Ww_SEQ_Manbat_BM_Takedown_MB_Chase01.Play_SEQ_Manbat_Takedown_BigImpact_2D";
    const string ScreamPath = "Ww_SEQ_Manbat_BM_Takedown_MB_Chase01.Play_SEQ_Manbat_Takedown_Rdm_Scream_2D";
    const string ImpactLookPath = "EFX_SideStories.ManBat.Manbat_Ground_Impact";

    // Who he can pick: in reach, close to the crosshair and in plain sight.
    const float SwoopReach = 3000f;
    const float SwoopCone = 0.9f;
    const float ChestHeight = 40f;

    // He dives this fast, and gives up if he hasn't got there in this long or is held up by
    // something in the way. His attack starts with his feet on the ground.
    const float DiveSpeed = 3200f;
    const float DiveLongest = 2.5f;
    const float StuckLongest = 0.3f;
    const float Arrived = 40f;
    const float CloseEnough = 280f;
    const float FloorReach = 300f;

    // Where his victim lies in his attack, under his hands as he crouches and where his claw
    // comes down. It is in his model's own space with the attack turned his flight's way, from
    // where his attack starts. Until he lands he closes on his victim if they have moved, no
    // faster than this.
    static readonly Vector3 Victim = new(-25f, 165f, 0f);
    const float CloseSpeed = 1500f;

    // Points in his attack, in its own seconds. He lands on his victim, rears up, slashes and is
    // off the ground again. Each stretch plays at its own pace, so the crouch over his victim
    // goes by quickly.
    const float LandsAt = 0.62f;
    const float RearsAt = 1.9f;
    const float SlashesAt = 2.62f;
    const float WingsAt = 3.3f;
    const float EndsAt = 5.1f;
    const float SwoopInRate = 1.2f;
    const float CrouchRate = 2.6f;
    const float SlashRate = 1.25f;
    const float LaunchRate = 1.45f;

    // Landing on him crushes him down where he stands, as Batman's glide kick knocks a thug
    // over, rather than throwing him out from under Man-Bat.
    const float PinForce = 250f;
    const int PinDamage = 20;
    const float BreakReach = 250f;

    // How he leaves, still moving the way he launched.
    const float LeaveSpeed = 700f;

    static AkEvent? _impact;
    static AkEvent? _scream;
    static ParticleSystem? _impactLook;

    static Act _act;
    static RPawnVillain? _prey;
    static float _swoopAt;
    static float _stuck;
    static bool _landed;
    static bool _slashed;
    static Vector3 _lastRoot;
    static int _downed;
    static float _downedAt = -10f;

    static Outcome StartSwoop()
    {
        if (!Option.On || _body is not { IsValid: true } || World.Player is not { IsValid: true } player)
        {
            return Outcome.Unchanged("MAN-BAT IS OFF");
        }

        if (_act != Act.None || _aiming)
        {
            return Outcome.Unchanged("NOT YET");
        }

        if (_carried is not null)
        {
            return Outcome.Unchanged("HANDS FULL");
        }

        if (Copies[Action] is not { IsValid: true })
        {
            return Outcome.Unchanged("NOT READY");
        }

        // He dives once the key is let go, on whoever is marked then.
        _snatching = false;
        _prey = Prey(player);
        _lookedAt = World.Now;
        _aiming = true;
        return Outcome.Done("SWOOP");
    }

    // The enemy nearest the crosshair that he can see, in reach.
    static RPawnVillain? Prey(RPawnPlayer player)
    {
        if (!Crosshair.View(out var eye, out var look))
        {
            return null;
        }

        RPawnVillain? best = null;
        var bestDot = SwoopCone;
        foreach (var thug in Crowd.StandingEnemies())
        {
            var chest = thug.Location + Vector3.UnitZ * ChestHeight;
            var to = chest - eye;
            if (Vector3.Distance(thug.Location, player.Location) > SwoopReach || to.LengthSquared() < 1f || thug.DrivenVehicle is { IsValid: true }
                || thug.Base is Vehicle)
            {
                continue;
            }

            var dot = Vector3.Dot(Vector3.Normalize(to), look);
            if (dot <= bestDot || Crosshair.Line(player, eye, chest, Crosshair.Meets.Level, out _))
            {
                continue;
            }

            best = thug;
            bestDot = dot;
        }

        return best;
    }

    // Every tick while one of his moves is on.
    static void Acting(RPawnPlayer player, SkeletalMeshComponent own, RPlayerController controller)
    {
        switch (_act)
        {
            case Act.Dive:
                Dive(player, own);
                break;
            case Act.Strike:
                Strike(player, own, controller);
                break;
            case Act.Screech:
                Screeching(player);
                break;
        }
    }

    // Straight at a spot just short of him, level with his feet.
    static void Dive(RPawnPlayer player, SkeletalMeshComponent own)
    {
        if (_prey is not { IsAlive: true } prey || !Crowd.Standing(prey) || World.Now - _swoopAt > DiveLongest)
        {
            if (_snatching)
            {
                Log.Info(World.Now - _swoopAt > DiveLongest ? "Man-Bat's snatch missed: he couldn't reach the thug in time." : "Man-Bat's snatch missed: the thug went down or out of reach first.");
            }

            Unact();
            return;
        }

        if (_snatching)
        {
            DiveToGrab(player, prey);
            return;
        }

        var flat = (prey.Location - player.Location) with { Z = 0f };
        var distance = flat.Length();
        var toward = distance > 1f ? MathF.Atan2(flat.Y, flat.X) * (180f / MathF.PI) : _yaw;

        // He comes in facing so that his victim lies where his attack wants him.
        _yaw = toward - (MathF.Atan2(Victim.Y, Victim.X) * (180f / MathF.PI) + own.Rotation.Yaw + _turn);
        var feet = prey.Location.Z - prey.CylinderComponent.CollisionHeight;
        var spot = prey.Location - ModelToWorld(own, Victim);
        spot.Z = feet + player.CylinderComponent.CollisionHeight;
        Face(player);

        var to = spot - player.Location;
        var gap = to.Length();
        var seconds = MathF.Min(World.Delta, LongestStep);
        if (gap <= Arrived)
        {
            BeginStrike(player);
            return;
        }

        var step = MathF.Min(gap, DiveSpeed * seconds);
        var went = Glide(player, to / gap * step);
        _velocity = seconds > 0f ? went / seconds : Vector3.Zero;
        Gauge();

        // Something in the way. Close enough, he strikes from where he is.
        _stuck = went.Length() < step * 0.2f ? _stuck + seconds : 0f;
        if (_stuck > StuckLongest)
        {
            if (distance <= CloseEnough)
            {
                BeginStrike(player);
            }
            else
            {
                Unact();
            }
        }
    }

    // His feet go down on the ground and the rooftop attack starts.
    static void BeginStrike(RPawnPlayer player)
    {
        if (Played[Action] is not { IsValid: true } played || Copies[Action] is not { IsValid: true } copy)
        {
            Unact();
            return;
        }

        player.Move(-Vector3.UnitZ * FloorReach);
        played.SetAnim(Moves[Action]);
        played.PlayAnim(false, SwoopInRate, 0f);
        copy.ForceSkelUpdate();
        _pin = Root(copy);
        _lastRoot = Vector3.Transform(_pin, AttackTurn);
        _velocity = Vector3.Zero;
        Gauge();
        _landed = false;
        _slashed = false;
        _act = Act.Strike;
    }

    static void Strike(RPawnPlayer player, SkeletalMeshComponent own, RPlayerController controller)
    {
        if (Played[Action] is not { IsValid: true } played || Copies[Action] is not { IsValid: true } copy)
        {
            Unact();
            return;
        }

        var time = played.CurrentTime;
        played.Rate = time < LandsAt + 0.3f ? SwoopInRate : time < RearsAt ? CrouchRate : time < WingsAt ? SlashRate : LaunchRate;
        Face(player);

        // The ground his attack covers carries the pawn, up and away at the end.
        var root = Vector3.Transform(Root(copy), AttackTurn);
        var step = root - _lastRoot;
        _lastRoot = root;
        if (step.LengthSquared() > 0.01f)
        {
            Glide(player, ModelToWorld(own, step));
        }

        if (!_landed)
        {
            Close(player, own, time);
        }

        if (!_landed && time >= LandsAt)
        {
            _landed = true;
            Pin(player);
        }

        if (!_slashed && time >= SlashesAt)
        {
            _slashed = true;
            Slash(player, controller);
        }

        if (time >= EndsAt || !played.bPlaying)
        {
            _velocity = new Rotator(0f, _yaw, 0f).ToDirection() * LeaveSpeed;
            Unact();
        }
    }

    // Where his victim lies now, held where his attack started while the pawn carries the
    // ground it covers. Before he lands he closes the gap to his victim, spread over the time
    // left, so he comes down right on him.
    static void Close(RPawnPlayer player, SkeletalMeshComponent own, float time)
    {
        if (_prey is not { IsAlive: true } prey || !Crowd.Standing(prey))
        {
            return;
        }

        var lies = player.Location + ModelToWorld(own, Vector3.Transform(_pin, AttackTurn) - _lastRoot + Victim);
        var gap = (prey.Location - lies) with { Z = 0f };
        var seconds = MathF.Min(World.Delta, LongestStep);
        var left = MathF.Max((LandsAt - time) / SwoopInRate, seconds);
        var length = gap.Length();
        if (length < 1f || seconds <= 0f)
        {
            return;
        }

        var step = MathF.Min(length * MathF.Min(1f, seconds / left), CloseSpeed * seconds);
        Glide(player, gap / length * step);
    }

    // A way in his model's own space, laid out in the world.
    static Vector3 ModelToWorld(SkeletalMeshComponent own, Vector3 off)
    {
        var yaw = (_yaw + own.Rotation.Yaw + _turn) * (MathF.PI / 180f);
        var (sin, cos) = MathF.SinCos(yaw);
        return new Vector3(off.X * cos - off.Y * sin, off.X * sin + off.Y * cos, off.Z);
    }

    // He comes down on him and knocks him flat.
    static void Pin(RPawnPlayer player)
    {
        if (_prey is not { IsAlive: true } prey)
        {
            return;
        }

        var way = new Rotator(0f, _yaw, 0f).ToDirection();
        if (Crowd.Standing(prey))
        {
            Villains.Knock(player, prey, PinDamage, Vector3.Normalize(way * 0.3f - Vector3.UnitZ), PinForce, fall: true);
        }

        var ground = prey.Location - Vector3.UnitZ * prey.CylinderComponent.CollisionHeight;
        if (_impact is { IsValid: true } impact)
        {
            player.StartAudioEvent(impact);
        }

        if (_impactLook is { IsValid: true } look && Game.TryGetWorldInfo(out var world) && world.GRI?.ObjectPool is { IsValid: true } pool)
        {
            pool.SpawnEmitter(look, ground, new Rotator(0f, _yaw, 0f), 0, 1f);
        }

        Villains.Break(player, ground, BreakReach);
    }

    // The slash finishes him the way a takedown does.
    static void Slash(RPawnPlayer player, RPlayerController controller)
    {
        if (_scream is { IsValid: true } scream)
        {
            player.StartAudioEvent(scream);
        }

        if (_prey is not { IsAlive: true } prey || prey.bGoodAsDead || prey.Health <= 0)
        {
            return;
        }

        prey.TakeDamage(10000, controller, prey.Location, Vector3.UnitZ, RDmgType_CombatTakedown.StaticClass(), default, player);
        _downed++;
        _downedAt = World.Now;
    }

    // His attack's root in his own mesh.
    static Vector3 Root(SkeletalMeshComponent copy)
    {
        var atoms = copy.LocalAtoms;
        return atoms.Count > 0 ? atoms[0].Translation : Vector3.Zero;
    }

    // Ends whatever move is on and lets his flight take over again.
    static void Unact()
    {
        if (Played[Action] is { IsValid: true } played && _act == Act.Strike)
        {
            played.StopAnim();
        }

        _act = Act.None;
        _aiming = false;
        _snatching = false;
        _prey = null;
        _stuck = 0f;
    }
}
