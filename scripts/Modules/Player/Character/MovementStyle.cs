#nullable enable

using System.Diagnostics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// PLAYER > CHARACTER, the STYLE rows. The game finds each animation by name, in the pawn's sets
// in order, so another character's movement sets put ahead of the player's own play instead.
// What they haven't got still comes from the player's own. Idles and turns are not looked up by
// name but sit in their own slots on the animation config, so those slots are swapped.
static class MovementStyle
{
    const string Lender = "movement style";
    const string Own = "";

    // A pick is only loaded once it has stayed put this long, so stepping through the list
    // doesn't load every character on the way.
    const double SettleSeconds = 0.6;

    // A character that didn't fit is tried again once the last one has been let go, which takes
    // Borrowed's grace time, and then after the engine has been asked to collect.
    const double FirstRetrySeconds = 6;
    const double LaterRetrySeconds = 1;
    const int Tries = 3;

    public static readonly Choice Style = new(
        "player.character.movement_style", "MOVEMENT STYLE",
        "Stand, walk, run, turn, climb and glide like another character. Your fighting and gadgets stay your own. Only the characters you have installed are listed.",
        Options())
    {
        Tick = Keep,
        Restore = Undo,
    };

    // The idle and turn slots a style takes over. The fighting ones are left alone.
    static readonly (Func<RPawnPlayerConfig, AnimSet> Read, Held<RPawnPlayerConfig, AnimSet> Held)[] Slots =
    {
        Slot("standing idle", config => config.DefaultIdleOverlayAnimSet, (config, set) => config.DefaultIdleOverlayAnimSet = set),
        Slot("crouching idle", config => config.CrouchIdleOverlayAnimSet, (config, set) => config.CrouchIdleOverlayAnimSet = set),
        Slot("cover idle", config => config.CoverIdleOverlayAnimSet, (config, set) => config.CoverIdleOverlayAnimSet = set),
        Slot("dangling idle", config => config.DangleIdleOverlayAnimSet, (config, set) => config.DangleIdleOverlayAnimSet = set),
        Slot("hanging idle", config => config.HangIdleOverlayAnimSet, (config, set) => config.HangIdleOverlayAnimSet = set),
        Slot("stealth idle", config => config.SilentPredatorIdleOverlayAnimSet, (config, set) => config.SilentPredatorIdleOverlayAnimSet = set),
        Slot("standing turns", config => config.DefaultTurnAnimSet, (config, set) => config.DefaultTurnAnimSet = set),
        Slot("crouching turns", config => config.CrouchTurnAnimSet, (config, set) => config.CrouchTurnAnimSet = set),
        Slot("grate turns", config => config.GrateWalkTurnAnimSet, (config, set) => config.GrateWalkTurnAnimSet = set),
        Slot("railing turns", config => config.RailingTurnAnimSet, (config, set) => config.RailingTurnAnimSet = set),
        Slot("wire turns", config => config.WireTurnAnimSet, (config, set) => config.WireTurnAnimSet = set),
    };

    static (Func<RPawnPlayerConfig, AnimSet>, Held<RPawnPlayerConfig, AnimSet>) Slot(string name, Func<RPawnPlayerConfig, AnimSet> read, Action<RPawnPlayerConfig, AnimSet> write) =>
        (read, new("movement style " + name, read, write));

    static readonly List<AnimSet> Lent = new();
    static RPawnPlayerConfig? _theirs;
    static RPawnPlayerCombat? _pawn;
    static string _style = Own;
    static (string Style, nint Pawn) _failed;
    static string _picked = Own;
    static long _pickedAt;
    static int _tries;
    static long _retryAt;

    // The game only looks its animations up again when the pose changes, so the pose is set
    // again once the player is on their feet.
    static bool _repose;

    static (string Id, string Label)[] Options() =>
        new[] { (Own, "YOUR OWN") }.Concat(Characters.Installed()).ToArray();

    static void Keep()
    {
        if (World.Player is not RPawnPlayerCombat { IsValid: true } pawn || pawn.AnimationConfig is not { IsValid: true } mine)
        {
            return;
        }

        // A new pawn, from a switch or a reload. The old one took its lent sets with it.
        if (_pawn is not null && (!_pawn.IsValid || _pawn.Ptr != pawn.Ptr))
        {
            Forget();
        }

        var style = Style.CurrentId;
        if (_pawn is not null && style == _style)
        {
            HoldSlots(mine);
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (style != _picked)
        {
            _picked = style;
            _pickedAt = now;
            _tries = 0;
            _retryAt = 0;
            return;
        }

        if (Stopwatch.GetElapsedTime(_pickedAt, now).TotalSeconds < SettleSeconds || _failed == (style, pawn.Ptr) || now < _retryAt)
        {
            return;
        }

        Undo();
        if (ObjectTable.Headroom < Packages.Cost<RAddContentPlayerCharacter>(style) + Packages.Spare)
        {
            if (_tries >= Tries)
            {
                _failed = (style, pawn.Ptr);
                Log.Info($"MOVEMENT STYLE {Style.ValueText} can't be used: the game is too close to its object limit to load them.");
                return;
            }

            if (_tries > 0 && Game.TryGetWorldInfo(out var world))
            {
                world.ForceGarbageCollection(true);
            }

            _tries++;
            _retryAt = now + (long)((_tries == 1 ? FirstRetrySeconds : LaterRetrySeconds) * Stopwatch.Frequency);
            return;
        }

        try
        {
            Apply(pawn, mine, style);
            _repose = true;
        }
        catch (InvalidOperationException problem)
        {
            _failed = (style, pawn.Ptr);
            Log.Info($"MOVEMENT STYLE {Style.ValueText} can't be used: {problem.Message}.");
        }
    }

    // Runs every tick, with the style on or off, so going back to your own is picked up too.
    public static void Tick()
    {
        if (!_repose || World.Player is not RPawnPlayerCombat { IsValid: true } pawn || pawn.Physics != Actor.EPhysics.PHYS_Walking)
        {
            return;
        }

        _repose = false;
        pawn.ForceChangePose(pawn.GetMovementStance(), pawn.GetWeaponStance(), TransitionDuration: 0.2f);
    }

    static void Apply(RPawnPlayerCombat pawn, RPawnPlayerConfig mine, string style)
    {
        var data = Borrowed.Keep(Packages.Main<RAddContentPlayerCharacter>(style), Lender);
        var theirs = data.AnimationConfig is { IsValid: true } config ? Borrowed.Keep(config, Lender) : throw new InvalidOperationException("their animation data is missing");
        _pawn = pawn;
        _style = style;

        // Their own character needs nothing changed.
        if (theirs.Ptr == mine.Ptr)
        {
            return;
        }

        // Kept in their order, so their first set still wins. Sets both characters share are
        // already there.
        var wanted = new List<AnimSet>();
        foreach (var sets in new[] { theirs.MovementAnimSets, theirs.ClimbingAnimSets })
        {
            for (var i = 0; i < sets.Count; i++)
            {
                if (sets[i] is { IsValid: true } set && !wanted.Exists(each => each.Ptr == set.Ptr) && !Mine(mine, set))
                {
                    wanted.Add(set);
                }
            }
        }

        for (var i = wanted.Count - 1; i >= 0; i--)
        {
            Borrowed.Keep(wanted[i], Lender);
            Lending.AddAnimSet(pawn, wanted[i], Lender, ahead: true);
            Lent.Add(wanted[i]);
        }

        _theirs = theirs;
        HoldSlots(mine);
    }

    static void HoldSlots(RPawnPlayerConfig mine)
    {
        if (_theirs is not { IsValid: true } theirs)
        {
            return;
        }

        foreach (var (read, held) in Slots)
        {
            if (read(theirs) is { IsValid: true } set)
            {
                held.Hold(mine, set);
            }
        }
    }

    static bool Mine(RPawnPlayerConfig mine, AnimSet set)
    {
        foreach (var sets in new[] { mine.MovementAnimSets, mine.ClimbingAnimSets, mine.GadgetAnimSets, mine.CharacterSpecificAnimSets })
        {
            for (var i = 0; i < sets.Count; i++)
            {
                if (sets[i] is { } found && found.Ptr == set.Ptr)
                {
                    return true;
                }
            }
        }

        return false;
    }

    static void Undo()
    {
        _repose |= Lent.Count > 0;
        if (_pawn is { IsValid: true } pawn)
        {
            foreach (var set in Lent)
            {
                if (set.IsValid)
                {
                    Lending.RemoveAnimSet(pawn, set, Lender);
                }
            }
        }

        Forget();
        Borrowed.Let(Lender);
    }

    static void Forget()
    {
        foreach (var (_, held) in Slots)
        {
            held.Release();
        }

        Lent.Clear();
        _theirs = null;
        _pawn = null;
        _style = Own;
        _failed = default;
    }
}
