#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static Knightfall.Turns;
using Atom = BmSDK.GameObject.FBoneAtom;

namespace Knightfall;

// SCREECH. He rears upright where he hangs in the air, rises a little as he sweeps his wings out
// wide, and shrieks. His moves have no shriek of their own, his face is worked by the game's
// facial animation, so it is the wide sweep of his fast wing beat with his jaw dropped here. The
// shriek is his line from the chase, spoken through the game's speech like a bark.
// Thugs around him cower, the game's own reaction to a fright, and those right under him are
// rocked off their feet. Screens and glass nearby crack.
static partial class ManBat
{
    static readonly VillainVoice Shrieks = new(Lender, new[] { FlightPackage }, new[] { "DLG_SIDE_MANBAT_SCREECHES" },
        ("screech", "Side_Manbat_Screeches.Lines."));

    // How long the whole screech lasts, when the shriek goes out, and how long his jaw takes to
    // drop and close again.
    const float ScreechSeconds = 1.4f;
    const float ShriekAfter = 0.2f;
    const float HeadIn = 0.15f;
    const float HeadOut = 0.3f;

    // His pose for it: nearly upright with his jaw wide, in degrees. His wings sweep out from this
    // far into his fast wing beat, at this pace, and he rises this far while they do.
    const float ScreechTip = 84f;
    const float JawOpen = 38f;
    const float SweepFrom = 0.1f;
    const float SweepRate = 1.2f;
    const float RiseHeight = 70f;
    const float RiseSeconds = 0.5f;

    // Thugs who can see him this far off cower, and any this close whether they see him or not.
    // Any this close are rocked off their feet.
    const float ScreechReach = 1400f;
    const float ScreechClose = 700f;
    const float RockReach = 450f;
    const int RockDamage = 5;
    const float RockForce = 900f;
    const float CrackReach = 500f;
    const float ScreechShake = 1.2f;
    const float ScreechWait = 8f;

    const string ShakeFrom = "BmGame.Default__RCombatMove_BatmanBatclawSlam";

    static readonly FName JawName = new("FcFX_Jaw");

    static float _nextScreech;
    static float _screechAt;
    static bool _shrieked;

    // How far his jaw has dropped, from 0 to 1.
    static float _shriek;

    static Outcome Screech()
    {
        if (!Option.On || _body is not { IsValid: true } || World.Player is not { IsValid: true })
        {
            return Outcome.Unchanged("MAN-BAT IS OFF");
        }

        if (_act != Act.None || _aiming)
        {
            return Outcome.Unchanged("NOT NOW");
        }

        if (World.Now < _nextScreech)
        {
            return Outcome.Unchanged("RECOVERING");
        }

        if (Copies[Spread] is not { IsValid: true } || Played[Spread] is not { IsValid: true } sweep)
        {
            return Outcome.Unchanged("NOT READY");
        }

        sweep.PlayAnim(false, SweepRate, SweepFrom);
        sweep.SetPosition(SweepFrom, false);

        _velocity = Vector3.Zero;
        Gauge();
        _screechAt = World.Now;
        _shrieked = false;
        _act = Act.Screech;
        return Outcome.Done("SCREECH");
    }

    // Each tick of the screech. He hangs where he is while it lasts.
    static void Screeching(RPawnPlayer player)
    {
        var since = World.Now - _screechAt;
        Face(player);
        if (since < RiseSeconds)
        {
            Glide(player, Vector3.UnitZ * (RiseHeight * MathF.Min(World.Delta, LongestStep) / RiseSeconds));
        }

        if (!_shrieked && since >= ShriekAfter)
        {
            _shrieked = true;
            Shriek(player);
        }

        if (since >= ScreechSeconds || since < 0f)
        {
            _nextScreech = World.Now + ScreechWait;
            Unact();
        }
    }

    // His jaw follows the screech in and out.
    static void ShriekPose()
    {
        var since = World.Now - _screechAt;
        var wanted = _act == Act.Screech ? Math.Clamp(since / HeadIn, 0f, 1f) : 0f;
        _shriek += (wanted - _shriek) * Settle(World.Delta, _act == Act.Screech ? HeadIn * 0.5f : HeadOut);
        if (_shriek < 0.001f)
        {
            _shriek = 0f;
        }
    }

    // Laid over the blended pose. His jaw drops about its own X.
    static void Throw(Span<Atom> pose)
    {
        if (_shriek <= 0f || _body is not { IsValid: true } body)
        {
            return;
        }

        Turn(pose, body.MatchRefBone(JawName), Vector3.UnitX, -JawOpen * _shriek);
    }

    static void Turn(Span<Atom> pose, int bone, Vector3 axis, float degrees)
    {
        if (bone < 0 || bone >= pose.Length)
        {
            return;
        }

        var atom = pose[bone];
        atom.Rotation = ToTurn(Numeric(atom.Rotation) * Quaternion.CreateFromAxisAngle(axis, degrees * (MathF.PI / 180f)));
        pose[bone] = atom;
    }

    static void Shriek(RPawnPlayer player)
    {
        if (!Shrieks.Speak("screech", always: true) && _scream is { IsValid: true } scream)
        {
            player.StartAudioEvent(scream);
        }

        if (player.Controller is RPlayerController { IsValid: true } controller)
        {
            if (Game.FindObject<RCombatMove_BatmanBatclawSlam>(ShakeFrom) is { IsValid: true } slam)
            {
                controller.AddScreenShake(slam.ImpactScreenShake, ScreechShake, player.Location);
            }

            if (controller is RPlayerControllerCombat combat)
            {
                combat.PlayImpactRumble(60f, 80f, 0.5f);
            }
        }

        foreach (var thug in Crowd.StandingEnemies().ToList())
        {
            var off = thug.Location - player.Location;
            if (off.Length() > RockReach)
            {
                continue;
            }

            var away = off with { Z = 0f };
            Villains.Knock(player, thug, RockDamage, away.LengthSquared() > 1f ? Vector3.Normalize(away) : Vector3.UnitX, RockForce, fall: false);
        }

        if (World.Info is { IsValid: true } game && game.CombatManager is { IsValid: true } fights)
        {
            fights.TriggerCombatCowerEvent(player, player.Location, ScreechReach, ScreechClose);
        }

        Villains.Break(player, player.Location, CrackReach);
    }
}
