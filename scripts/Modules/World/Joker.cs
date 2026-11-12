#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// WORLD > CITY, the JOKER rows: the hallucinations the game shows on its own, on demand.
static class Joker
{
    // Billboards the game turned back are swapped again this often, and new ones looked for
    // less often, since that walks every billboard.
    const float RecheckEvery = 0.25f;
    const float LookEvery = 2f;

    const int ThugLimit = 6;
    const float ThugRange = 6000f;
    const float ThugSeconds = 10f;

    // Seen in the last half second counts as in view, as the game's own Joker thugs do it.
    const float SeenWithin = 0.5f;

    // Longer than any session, for NONE.
    const float Never = 1e7f;

    // The game undoes a swap once the billboard has been seen, unless this is off.
    static readonly Held<RJokerSwappableBase, bool> UndoWhenSeen = new(
        "joker billboard undo", board => board.bUnswapAutomatically, (board, undo) => board.bUnswapAutomatically = undo);

    // The game's native billboard update reads these. The chance is the odds an unseen board
    // turns, by story chapter. The cooldown is how long one board waits to turn again, and the
    // interval how long between lightning reveals in front of you.
    static readonly Held<RGameInfo, float> Chance3 = new("joker chance 3", game => game.jokerSwappableChance_Ch3, (game, chance) => game.jokerSwappableChance_Ch3 = chance);
    static readonly Held<RGameInfo, float> Chance5 = new("joker chance 5", game => game.jokerSwappableChance_Ch5, (game, chance) => game.jokerSwappableChance_Ch5 = chance);
    static readonly Held<RGameInfo, float> Chance7 = new("joker chance 7", game => game.jokerSwappableChance_Ch7, (game, chance) => game.jokerSwappableChance_Ch7 = chance);
    static readonly Held<RGameInfo, float> Chance9 = new("joker chance 9", game => game.jokerSwappableChance_Ch9, (game, chance) => game.jokerSwappableChance_Ch9 = chance);
    static readonly Held<RGameInfo, float> Cooldown = new("joker cooldown", game => game.jokerSwappableCooldownTime, (game, seconds) => game.jokerSwappableCooldownTime = seconds);
    static readonly Held<RGameInfo, float> Interval = new("joker reveal interval", game => game.onScreenJokerfy_MinInterval, (game, seconds) => game.onScreenJokerfy_MinInterval = seconds);

    static readonly List<RJokerBillboard> Swapped = new();
    static readonly HashSet<nint> SwappedAt = new();
    static readonly List<(RBMPawnAI Thug, RRiotBrain Brain, float Until)> Jokers = new();
    static float _recheckedAt = -1f;
    static float _lookedAt = -1f;

    public static readonly Toggle City = new(
        "world.city.jokerfy", "JOKERFY THE CITY",
        "Every billboard around you that has a Joker version turns into it and stays that way. Statues and video screens aren't included. Off puts them back.")
    {
        Tick = KeepJokerfied,
        Restore = PutBack,
    };

    public static readonly Command Thugs = new(
        "world.city.joker_thugs", "JOKER THUGS",
        "Up to six thugs you can see within 60 m wear Joker's face for ten seconds, as they do in Batman's hallucinations.",
        TurnThugs);

    public static readonly Slider Sightings = new(
        "world.city.joker_sightings", "JOKER SIGHTINGS",
        "How often billboards and statues turn into Joker by themselves, and how soon lightning can show you another. NONE stops them.",
        min: 0, max: 5, step: 0.25, value: 1, unit: "X", zero: "NONE")
    {
        Tick = HoldSightings,
        Restore = LetSightings,
    };

    public static readonly Catalog.Section Section = new("JOKER", City, Thugs, Sightings);

    // Also kept going by the Joker on PLAYER > VILLAINS while he is played.
    public static void KeepJokerfied()
    {
        if (MathF.Abs(World.Now - _recheckedAt) >= RecheckEvery)
        {
            _recheckedAt = World.Now;
            for (var i = Swapped.Count - 1; i >= 0; i--)
            {
                var board = Swapped[i];
                if (!board.IsValid || board.bDeleteMe)
                {
                    SwappedAt.Remove(board.Ptr);
                    Swapped.RemoveAt(i);
                }
                else if (!board.bCurrentlySwapped && !board.bCurrentlyForced)
                {
                    Swap(board);
                }
            }
        }

        if (MathF.Abs(World.Now - _lookedAt) < LookEvery)
        {
            return;
        }

        _lookedAt = World.Now;
        foreach (var board in Game.FindObjects<RJokerBillboard>())
        {
            // One the game forced belongs to a story moment, so it's left to the game.
            if (!board.IsValid || board.bDeleteMe || board.IsClassDefaultObject || board.bCurrentlyForced || SwappedAt.Contains(board.Ptr)
                || board.AlternateMaterial is not { IsValid: true } || board.StaticMeshComponent is not { IsValid: true })
            {
                continue;
            }

            if (!board.bCurrentlySwapped)
            {
                Swap(board);
            }

            Swapped.Add(board);
            SwappedAt.Add(board.Ptr);
        }
    }

    // Never the forced swap. In Oracle it hung the game four times.
    static void Swap(RJokerBillboard board)
    {
        UndoWhenSeen.Hold(board, false);
        board.SwapToJokerVersion(false);
    }

    // Leaves them be while JOKERFY THE CITY is on, for anyone else that asked for them.
    public static void PutBackUnlessKept()
    {
        if (!City.On)
        {
            PutBack();
        }
    }

    static void PutBack()
    {
        foreach (var board in Swapped)
        {
            if (board.IsValid && !board.bDeleteMe && board.bCurrentlySwapped && !board.bCurrentlyForced)
            {
                board.SwapToOriginalVersion(false);
            }
        }

        UndoWhenSeen.Release();
        Swapped.Clear();
        SwappedAt.Clear();
        _lookedAt = -1f;
    }

    // Each thug's own BecomeJoker. It hides the thug's body even when there's no Joker look to
    // show, and turning back strips as many pieces as the default Joker look has, so only thugs
    // whose Joker look has that many are picked.
    static Outcome TurnThugs()
    {
        if (World.Player is not { IsValid: true } you || World.Info?.AIManager is not { IsValid: true } brains)
        {
            return Outcome.NoPlayer;
        }

        var picks = new List<(RBMPawnAI Thug, RRiotBrain Brain, float Away)>();
        var pieces = -1;
        foreach (var known in brains.GlobalControllerInfoList)
        {
            if (known.Controller is not { IsValid: true } ai || ai.RiotBrain is not { IsValid: true, bIsJokerfied: false } brain
                || brain.JokerDefine is not { IsValid: true } look || ai.Pawn is not RBMPawnAI { IsValid: true, bDeleteMe: false, bHidden: false } thug
                || thug.Health <= 0 || thug is RPawnVillain { bGoodAsDead: true }
                || thug.Mesh is not { IsValid: true } body || World.Now - body.LastRenderTime > SeenWithin)
            {
                continue;
            }

            if (pieces < 0)
            {
                pieces = brain.Class.DefaultObject is RRiotBrain { JokerDefine: { IsValid: true } usual } ? usual.CharacterMeshes.Count : 0;
            }

            var away = Vector3.DistanceSquared(thug.Location, you.Location);
            if (pieces > 0 && look.CharacterMeshes.Count == pieces && away < ThugRange * ThugRange)
            {
                picks.Add((thug, brain, away));
            }
        }

        if (picks.Count == 0)
        {
            return Outcome.Failed("NO THUGS IN VIEW");
        }

        picks.Sort((a, b) => a.Away.CompareTo(b.Away));
        var turned = 0;
        foreach (var (thug, brain, _) in picks)
        {
            if (turned == ThugLimit)
            {
                break;
            }

            brain.BecomeJoker(thug);
            Jokers.Add((thug, brain, World.Now + ThugSeconds));
            turned++;
        }

        return Outcome.Done("HA HA HA");
    }

    // Runs every tick. Thugs whose ten seconds are up turn back.
    public static void TurnBack()
    {
        for (var i = Jokers.Count - 1; i >= 0; i--)
        {
            if (World.Now >= Jokers[i].Until || World.Now < Jokers[i].Until - ThugSeconds)
            {
                Normal(i);
            }
        }
    }

    public static void TurnAllBack()
    {
        for (var i = Jokers.Count - 1; i >= 0; i--)
        {
            Normal(i);
        }
    }

    static void Normal(int index)
    {
        var (thug, brain, _) = Jokers[index];
        Jokers.RemoveAt(index);
        if (thug.IsValid && brain.IsValid && brain.bIsJokerfied)
        {
            brain.BecomeNormal(thug);
        }
    }

    static void HoldSightings()
    {
        if (World.Info is not { IsValid: true } game)
        {
            return;
        }

        var times = (float)Sightings.Value;
        foreach (var chance in new[] { Chance3, Chance5, Chance7, Chance9 })
        {
            chance.Hold(game, was => Math.Clamp(was * times, 0f, 1f));
        }

        Cooldown.Hold(game, was => times > 0f ? was / times : Never);
        Interval.Hold(game, was => times > 0f ? was / times : Never);
    }

    static void LetSightings()
    {
        Chance3.Release();
        Chance5.Release();
        Chance7.Release();
        Chance9.Release();
        Cooldown.Release();
        Interval.Release();
    }
}
