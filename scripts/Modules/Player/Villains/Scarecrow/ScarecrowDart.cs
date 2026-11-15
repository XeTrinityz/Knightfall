#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// His fear dart, the gadget cut from the game. He flicks it from his wrist with the move made for
// it, with its puff and its shot, and the thug it finds takes the panic it was made to give.
static partial class Scarecrow
{
    static readonly FName QuickFire = new("Wrist_Dart_Stand_Quick_Fire");
    const string DartMark = "QuickFireDart";
    const float DartLeaves = 0.18f;

    // The flick can be cut short from here, so he goes back to standing.
    const float DartUntil = 0.95f;
    const float DartTurnRate = 1000f;

    // He carries this many, and one fills back up this often.
    const int DartsMost = 3;
    const float DartRefill = 4f;

    // The gadget picks a target this far off at most and this far round from the aim. The dart
    // flies this fast.
    const float DartRange = 2000f;
    const float DartCone = 0.9f;
    const float DartSpeed = 3500f;
    const float ChestHeight = 40f;
    const float DartSplash = 0.5f;

    static readonly FName Wrist = new("ScrCrw_Injct_Bone_Needle_Root");

    static float _darts = DartsMost;
    static RPawnVillain? _dartAt;
    static float _firedAt = -10f;

    sealed record InFlight(RPawnVillain Thug, float Lands);

    static readonly List<InFlight> Flying = new();

    static Outcome Dart()
    {
        if (!Ready(out var player, out var controller) || _wrist is not { IsValid: true })
        {
            return Outcome.Unchanged("SCARECROW IS OFF");
        }

        if (!Free(player, controller))
        {
            return Outcome.Unchanged("NOT NOW");
        }

        if (_darts < 1f)
        {
            return Outcome.Unchanged("NO DARTS");
        }

        _dartAt = DartTarget(player, controller);
        var way = _dartAt is { } target ? target.Location - player.Location : controller.Rotation.ToDirection();
        if (Play(QuickFire, 0f, DartUntil, player.Rotation.Yaw) is null)
        {
            return Outcome.Unchanged("NOT READY");
        }

        _aim = MathF.Atan2(way.Y, way.X) * (180f / MathF.PI);
        _move = Move.Dart;
        _hitAt = Marked(QuickFire, DartMark, DartLeaves);
        Hold(controller);
        return Outcome.Done("FEAR DART");
    }

    // The enemy nearest the crosshair that he can see, in reach.
    static RPawnVillain? DartTarget(RPawnPlayer player, RPlayerController controller)
    {
        if (!Crosshair.View(out var eye, out var look))
        {
            return null;
        }

        RPawnVillain? best = null;
        var bestDot = DartCone;
        foreach (var thug in Crowd.StandingEnemies())
        {
            var chest = thug.Location + Vector3.UnitZ * ChestHeight;
            var to = chest - eye;
            var distance = Vector3.Distance(thug.Location, player.Location);
            if (distance > DartRange || to.LengthSquared() < 1f || thug.DrivenVehicle is { IsValid: true })
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

    // As the dart leaves his wrist.
    static void Fire(RPawnPlayer player, SkeletalMeshComponent body)
    {
        _darts = MathF.Max(0f, _darts - 1f);
        _firedAt = World.Now;
        var from = body.MatchRefBone(Wrist) >= 0 ? body.GetBoneLocation(Wrist, 0)
            : body.MatchRefBone(RightHand) >= 0 ? body.GetBoneLocation(RightHand, 0) : player.Location;
        Burst(_puff, from, 1f, BurstSeconds);
        if (_dartFire is { IsValid: true } shot)
        {
            player.StartAudioEvent(shot);
        }

        if (_dartAt is { IsAlive: true } thug)
        {
            Flying.Add(new InFlight(thug, World.Now + Vector3.Distance(from, thug.Location) / DartSpeed));
        }

        _dartAt = null;
    }

    // Darts land when their flight is over, on whoever is still there to take one.
    static void Fly(RPawnPlayer player)
    {
        for (var i = Flying.Count - 1; i >= 0; i--)
        {
            var dart = Flying[i];
            if (World.Now < dart.Lands)
            {
                continue;
            }

            Flying.RemoveAt(i);
            if (dart.Thug.IsAlive && Crowd.Standing(dart.Thug))
            {
                Burst(_splash, dart.Thug.Location + Vector3.UnitZ * ChestHeight, DartSplash, BurstSeconds);
                Panic(player, dart.Thug);
            }
        }
    }

    static void Refill()
    {
        _darts = MathF.Min(DartsMost, _darts + World.Delta / DartRefill);
    }
}
