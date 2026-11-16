#nullable enable

using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// His roar, the taunt he gives in his fight. The ground shakes and the thugs around him cower,
// the game's own reaction to a fright like a fear takedown.
static partial class KillerCroc
{
    static readonly FName RoarMove = new("Stand_Idle_Overlay");

    // Thugs who can see him this far off cower, and any this close whether they see him or not.
    const float RoarReach = 800f;
    const float RoarClose = 400f;
    const float RoarShake = 1.5f;
    const float RoarWait = 6f;

    static float _nextRoar;
    static float _roarAt;
    static bool _roared;

    public static void Rest()
    {
        Villains.Rest(ref _nextRoar);
        Villains.Rest(ref _nextCharge);
    }

    static Outcome Roar()
    {
        if (!Option.On || _body is not { IsValid: true } || World.Player is not { IsValid: true } player || World.Controller is not { } controller)
        {
            return Outcome.Unchanged("CROC IS OFF");
        }

        if (Acting || player.Physics != Actor.EPhysics.PHYS_Walking || player.CurrentSpecialMove is { IsValid: true } || controller.bCinematicMode)
        {
            return Outcome.Unchanged("NOT NOW");
        }

        if (World.Now < _nextRoar)
        {
            return Outcome.Unchanged("RECOVERING");
        }

        if (Play(RoarMove, player.Rotation.Yaw) is not { } played)
        {
            return Outcome.Unchanged("NOT READY");
        }

        _move = Move.Roar;
        _roared = false;
        _roarAt = RoarTime(played);
        Hold(controller);
        return Outcome.Done("ROAR");
    }

    // When he roars, read from the first sound the move plays.
    static float RoarTime(AnimNodeSequence played)
    {
        if (played.AnimSeq is { IsValid: true } sequence)
        {
            var notifies = sequence.Notifies;
            for (var i = 0; i < notifies.Count; i++)
            {
                if (notifies[i].Notify is AnimNotify_Sound)
                {
                    return notifies[i].Time;
                }
            }
        }

        return 0.3f;
    }

    // Each tick of the roar. It ends like any other move.
    static void Roaring(RPawnPlayer player, AnimNodeSequence played)
    {
        if (_roared || played.CurrentTime < _roarAt)
        {
            return;
        }

        _roared = true;
        Stomp(player, RoarShake);
        if (World.Info is { IsValid: true } game && game.CombatManager is { IsValid: true } fights)
        {
            fights.TriggerCombatCowerEvent(player, player.Location, RoarReach, RoarClose);
        }
    }
}
