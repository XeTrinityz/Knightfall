#nullable enable

using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// Team moves and the call button with a sidekick.
static partial class Sidekick
{
    // Set while the call button is handled in a fight, so a team move that can't start never
    // calls the Batmobile in the middle of it.
    static bool _holdCar;

    // Each enemy carries a table of team takedowns by hero. With Batman or Batgirl leading, the
    // takedown is picked from the row of the other hero's names for them. Anyone else leading
    // picks from their own row. Every name the row can pick has to be in the lead's, the other
    // hero's and the enemy's animations, or one of them has nothing to play and the move never
    // ends. A twin has no second half at all.
    static bool PairHasMoves(RPawnPlayerCombat one, RPawnPlayerCombat other, RPawnVillain villain) =>
        (one.CharacterName.Index != other.CharacterName.Index || one.CharacterName.Number != other.CharacterName.Number)
        && Leads(villain, one, other) && Leads(villain, other, one);

    static bool Leads(RPawnVillain villain, RPawnPlayerCombat first, RPawnPlayerCombat second)
    {
        var lead = villain.GetPlayerDualPlayAnimset(first);
        var follow = villain.GetPlayerDualPlayAnimset(second);
        var theirs = villain.GetVillainDualPlayAnimset(first);
        if (lead is null || follow is null || theirs is null)
        {
            return false;
        }

        var name = first.CharacterName.ToString();
        var batLead = name == "Batman" || name == "Batgirl";
        var defs = villain.PlayerPairedAnimsetPackageDefs.Count > 0 ? villain.PlayerPairedAnimsetPackageDefs : villain.CombatManager?.ThugPlayerPairedAnimsetPackageDefs;
        var row = villain.GetPlayerPairedIndex(batLead ? second : first, default);
        if (defs is null || row < 0 || row >= defs.Count)
        {
            return false;
        }

        var picks = batLead ? defs[row].BMDualPlayTakedownAnims : defs[row].DualPlayTakedownAnims;
        if (picks.Count == 0)
        {
            return false;
        }

        var key = (lead.Ptr, theirs.Ptr, follow.Ptr, (nint)row, batLead);
        if (!_covers.TryGetValue(key, out var covers))
        {
            var has = Names(follow);
            var mine = Names(lead);
            var enemy = Names(theirs);
            covers = true;
            for (var i = 0; i < picks.Count && covers; i++)
            {
                var pick = picks[i].ToString();
                covers = has.Contains(pick) && mine.Contains(pick) && enemy.Contains(pick);
            }

            _covers[key] = covers;
        }

        return covers;
    }

    static readonly Dictionary<(nint, nint, nint, nint, bool), bool> _covers = new();

    static HashSet<string> Names(BmSDK.Engine.AnimSet set)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var sequences = set.Sequences;
        for (var i = 0; i < sequences.Count; i++)
        {
            if (sequences[i] is { IsValid: true } sequence)
            {
                names.Add(sequence.SequenceName.ToString());
            }
        }

        return names;
    }

    // The other body of our pair, or null when this one isn't in it.
    static RPawnPlayerCombat? Other(RPawnPlayerCombat pawn)
    {
        if (!Present || _caller is not RPawnPlayerCombat caller || _body is not RPawnPlayerCombat body)
        {
            return null;
        }

        return pawn.Ptr == body.Ptr ? caller : pawn.Ptr == caller.Ptr ? body : null;
    }

    // Which enemies count for a team move. The game asks it to light the button and fill the
    // meter, so an enemy the pair has no moves for is left out of both.
    [Redirect(typeof(RPawnPlayerCombat), nameof(RPawnPlayerCombat.IsValidDualPlayTarget))]
    static bool IsValidDualPlayTarget(RPawnPlayerCombat self, RPawnVillain villain)
    {
        try
        {
            Adopt();
            if (Other(self) is { } other && !PairHasMoves(self, other, villain))
            {
                return false;
            }
        }
        catch (Exception error)
        {
            Log.Error("sidekick.team", "The sidekick's team move check failed", error);
        }

        return Redirects.RunOriginal<bool>();
    }

    // The takedown itself checks no animations, so the same rule stops it.
    [Redirect(typeof(RPawnPlayerCombat), nameof(RPawnPlayerCombat.SpawnDualComboTakeDown))]
    static bool SpawnDualComboTakeDown(RPawnPlayerCombat self, RPawnVillain target, RPawnPlayerCombat buddy)
    {
        try
        {
            Adopt();
            if (Other(self) is { } other && other.Ptr == buddy.Ptr && !PairHasMoves(self, buddy, target))
            {
                return false;
            }
        }
        catch (Exception error)
        {
            Log.Error("sidekick.team", "The sidekick's team takedown check failed", error);
        }

        return Redirects.RunOriginal<bool>();
    }

    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.DualPlayPressed))]
    static void DualPlayPressed(RPlayerControllerCombat self)
    {
        try
        {
            Adopt();
            _holdCar = Present && !_leaving && self.Pawn is RPawnPlayer && self.IsInCombat(false);
            Redirects.RunOriginal();
        }
        finally
        {
            _holdCar = false;
        }
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.CallBatmobileToMe))]
    static void CallBatmobileToMe(RPlayerController self)
    {
        if (_holdCar)
        {
            return;
        }

        Redirects.RunOriginal();
    }

    // A team takedown that was cut short, like by calling the car mid-move, never gives swapping
    // back, and the button then shows nothing and the swap stays refused. When nothing of the pair
    // is moving and the story hasn't locked it, the flag is put back.
    static float _lockedSince = -1f;

    static void FreeSwap(RPlayerController controller)
    {
        if (World.Info is not { } info || !info.bDualPlayerSwitchingDisabled || info.bDualPlayerSwitchingDisabledBySeq
            || _body is not RPawnPlayerCombat body || _caller is not RPawnPlayerCombat caller || Switcher.InScene(controller)
            || body.CombatMove is not null || caller.CombatMove is not null || body.CurrentSpecialMove is not null || caller.CurrentSpecialMove is not null)
        {
            _lockedSince = -1f;
            return;
        }

        if (_lockedSince < 0f || World.Now < _lockedSince)
        {
            _lockedSince = World.Now;
            return;
        }

        if (World.Now - _lockedSince >= SwapLockSeconds)
        {
            info.bDualPlayerSwitchingDisabled = false;
            body.bDualMoveQueued = false;
            caller.bDualMoveQueued = false;
            _lockedSince = -1f;
            Log.Info("A team takedown left swapping switched off, so it was put back.");
        }
    }

    const float SwapLockSeconds = 1.5f;

    // Red Hood's own keyboard layout comes from his story, which had no partner and no car, so
    // it has no call button at all. While a sidekick is out he gets the key Batman's layout uses
    // for it, unless his own layout already has that key for something else.
    const string CallCommand = "DualPlay";
    const string KeyPrefix = "$PC.Keys.";
    static bool _lentKey;

    [Redirect(typeof(RGameInfo), nameof(RGameInfo.SetPCBinds))]
    static void SetPCBinds(RGameInfo self)
    {
        Redirects.RunOriginal();
        try
        {
            _lentKey = false;
            if (Present && Game.TryGetPlayerController(out var controller) && controller.Pawn is RPawnPlayerCombat { IsValid: true } played && IsRedHood(played))
            {
                LendCallKey(self, controller);
            }
        }
        catch (Exception error)
        {
            Log.Error("sidekick.keys", "The sidekick's call key for Red Hood failed", error);
        }
    }

    static void LendCallKey(RGameInfo info, RPlayerController controller)
    {
        if (controller.PlayerInput is not RPlayerInput input || !Game.TryGetGameRI(out var game) || game.PersistentOptions is not { IsValid: true } options)
        {
            return;
        }

        var batman = info.DefaultBatmanKeyMap;
        for (var i = 0; i < batman.Count; i++)
        {
            // A row can chain several commands, like a debug one before the call.
            var command = batman[i].Command.ToString() ?? "";
            if (!Array.Exists(command.Split('|'), part => part.Trim() == CallCommand))
            {
                continue;
            }

            var stored = i < options.BatmanKeyBinds.Count ? options.BatmanKeyBinds[i] : default;
            var name = stored.PrimaryKeyName.ToString() is { Length: > 0 } picked ? picked : batman[i].PrimaryKeyName.ToString() ?? "";
            var key = name.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase) ? name[KeyPrefix.Length..] : name;
            if (key.Length == 0 || key.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase) || RedHoodUses(info, options, name))
            {
                return;
            }

            input.SetBindWithOptionalSave(new FName(key), GameText.Once(command), false, stored.bPrimaryCtrl, stored.bPrimaryShift, stored.bPrimaryAlt);
            _lentKey = true;
            return;
        }
    }

    // His class has no binding of its own, so its IsRedHood can't be asked from here.
    static bool IsRedHood(RPawnPlayerCombat pawn) => pawn.CharacterName.ToString() == RedHoodName;

    const string RedHoodName = "Redhood";

    static bool RedHoodUses(RGameInfo info, RPersistentOptions options, string name)
    {
        var map = info.DefaultRedHoodKeyMap;
        for (var i = 0; i < map.Count; i++)
        {
            var stored = i < options.RedHoodKeyBinds.Count ? options.RedHoodKeyBinds[i] : default;
            foreach (var used in new[] { stored.PrimaryKeyName.ToString(), stored.SecondaryKeyName.ToString(), map[i].PrimaryKeyName.ToString(), map[i].SecondaryKeyName.ToString() })
            {
                if (string.Equals(used, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Once the sidekick is gone the game's own layout goes back on.
    static void ReturnCallKey()
    {
        if (_lentKey && !Present && World.Info is { } info)
        {
            _lentKey = false;
            info.SetPCBinds();
        }
    }
}

