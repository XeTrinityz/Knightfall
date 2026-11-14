#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// Level doors with a sidekick. In Panessa Studios the story opens doors with Robin as one move:
// Batman holds the door, Robin goes through beside him and the camera follows them both. That
// move works on any level door, so a sidekick who is following goes through with you the same
// way. One who can't is brought in after you once you are through.
static partial class Sidekick
{
    const string DoorLender = "sidekick.doors";

    // The move and the animations for both halves of it. They are only cooked into the studio's
    // packages, and this is the smallest one.
    const string DoorPackage = "CityX_13";
    const string DoorMovePath = "Batman_Level_Moves.Film.DualDoorOpenMaster";

    // Closer than this, on your level and in sight, the sidekick goes through from where they
    // stand. Anyone else following is put this far to your side first.
    const float DoorReach = 500f;
    const float DoorStep = 100f;
    const float DoorBeside = 130f;

    static RSpecialMoveConfigConfigurable? _doorMove;
    static bool _doorMoveTried;

    // The player is going through a level door.
    static bool _doorOpening;

    // Finds the paired door move, or loads it once. Asked as a sidekick arrives, so the load
    // doesn't land in the middle of opening a door, and in every upkeep after. A hot reload hands
    // what was kept to the next copy, which lets it go unless it is kept again.
    static void LoadDoorMove()
    {
        if (_doorMove is { IsValid: true })
        {
            return;
        }

        _doorMove = Game.FindObject<RSpecialMoveConfigConfigurable>(DoorMovePath);
        if (_doorMove is not { IsValid: true } && !_doorMoveTried)
        {
            _doorMoveTried = true;
            if (Packages.TryLoad(DoorPackage, "the sidekick's door moves", "sidekick.doors"))
            {
                _doorMove = Game.FindObject<RSpecialMoveConfigConfigurable>(DoorMovePath);
            }

            if (_doorMove is not { IsValid: true, Config.IsValid: true })
            {
                Log.Info("The paired door move wasn't found, so sidekicks follow you through doors on their own.");
            }
        }

        if (_doorMove is { IsValid: true, Config.IsValid: true } move)
        {
            Borrowed.Keep(move, DoorLender);
        }
        else
        {
            _doorMove = null;
        }
    }

    // The paired move when the sidekick is at your side and free to go, or null.
    static RSpecialMoveConfig? DoorMove(RLevelTransitionDoorBase door)
    {
        if (_doorMove is not { IsValid: true } move || !Present || _leaving || !Follows.On || World.Info is not { } info
            || !Game.TryGetPlayerController(out var controller) || controller.Pawn is not RPawnPlayer player || controller.IsInCombat(false))
        {
            return null;
        }

        // The move plays its second half on the game's switch partner, whichever body that is.
        // It ends any move of theirs itself, so only a fight move keeps them out.
        if (info.PlayerSwitchPawn is not RPawnPlayer partner || partner.Controller is not { } driver || driver.Ptr != _bot!.Ptr || partner.CombatMove is not null)
        {
            return null;
        }

        // As in the story, a partner who isn't right there is put at your side as the move
        // starts, so they never miss it. One too far off to have been following is left to
        // catch up on their own.
        var gap = partner.Location - player.Location;
        if (gap.LengthSquared() > CatchUpRange * CatchUpRange)
        {
            return null;
        }

        var beside = gap.X * gap.X + gap.Y * gap.Y <= DoorReach * DoorReach && MathF.Abs(gap.Z) <= DoorStep
            && player.FastTrace(partner.Location, player.Location, Vector3.Zero, false);
        if (!beside)
        {
            if (!DoorSpot(player, partner, out var floor))
            {
                return null;
            }

            Place(partner, floor, player.Rotation.Yaw);
        }

        return move;
    }

    // Open floor at the player's side, on the side the partner was on, then the other side and
    // behind.
    static bool DoorSpot(RPawnPlayer player, RPawnPlayer partner, out Vector3 floor)
    {
        var right = Flat(player.Rotation.Yaw + 90f);
        var side = Vector3.Dot(partner.Location - player.Location, right) >= 0f ? 1f : -1f;
        foreach (var turn in new[] { 90f * side, -90f * side, 135f * side, -135f * side, 180f })
        {
            if (Standable(player, player.Location + Flat(player.Rotation.Yaw + turn) * DoorBeside, out floor))
            {
                return true;
            }
        }

        floor = default;
        return false;
    }

    [Redirect(typeof(RLevelTransitionDoorBase), nameof(RLevelTransitionDoorBase.GetPlayerMoveConfig))]
    static RSpecialMoveConfig GetPlayerMoveConfig(RLevelTransitionDoorBase self)
    {
        try
        {
            Adopt();
            if (DoorMove(self) is { } move)
            {
                return move;
            }
        }
        catch (Exception error)
        {
            Log.Error("sidekick.doors", "The sidekick's door move failed", error);
        }

        return Redirects.RunOriginal<RSpecialMoveConfig>();
    }

    // A sidekick who didn't go through with you comes in after you, where the camera isn't
    // looking. The partner AI can't open a level door, and the part of the city they were left
    // in may unload under them.
    static void ThroughDoor(RPlayerController controller, RPawnPlayer follower)
    {
        if (controller.bOpeningDoor)
        {
            _doorOpening = true;
            return;
        }

        if (!_doorOpening || follower.CurrentSpecialMove is not null)
        {
            return;
        }

        _doorOpening = false;
        if (!Follows.On || controller.Pawn is not RPawnPlayer player)
        {
            return;
        }

        var reach = _bot!.WalkBehindDist;
        if (Vector3.DistanceSquared(follower.Location, player.Location) < reach * reach && player.FastTrace(follower.Location, player.Location, Vector3.Zero, false))
        {
            return;
        }

        controller.GetPlayerViewPoint(out var eye, out var view);
        if (Unseen(player, eye, view, out var floor) || Beside(player, out floor))
        {
            Place(follower, floor, Heading(player.Location - floor, player.Rotation.Yaw));
        }
    }
}
