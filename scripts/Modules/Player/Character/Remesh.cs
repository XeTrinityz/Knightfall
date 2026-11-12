#nullable enable

using System.Diagnostics;
using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// The game keeps each playable character it has loaded in one of eight slots, with the suit
// that goes with them.
static class Loaded
{
    public const int Slots = 8;

    public static ref RGameRI.FLoadedPlayerCharacter Slot(RGameRI game, int index)
    {
        switch (index)
        {
            case 0: return ref game.PlayerCharacters_0;
            case 1: return ref game.PlayerCharacters_1;
            case 2: return ref game.PlayerCharacters_2;
            case 3: return ref game.PlayerCharacters_3;
            case 4: return ref game.PlayerCharacters_4;
            case 5: return ref game.PlayerCharacters_5;
            case 6: return ref game.PlayerCharacters_6;
            case 7: return ref game.PlayerCharacters_7;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public static int IndexOf(RGameRI game, FName character)
    {
        if (character.Index == 0)
        {
            return -1;
        }

        for (var i = 0; i < Slots; i++)
        {
            var name = Slot(game, i).CharacterName;
            if (name.Index == character.Index && name.Number == character.Number)
            {
                return i;
            }
        }

        return -1;
    }

    // The first slot with no character in it, or -1 when all are taken.
    public static int Free(RGameRI game)
    {
        for (var i = 0; i < Slots; i++)
        {
            if (Slot(game, i).CharacterName.Index == 0)
            {
                return i;
            }
        }

        return -1;
    }

    // The pawn's own name for its character, or the slot whose character is this pawn's class,
    // which is how the game itself finds it.
    public static int IndexOf(RGameRI game, RPawnPlayer pawn)
    {
        var named = IndexOf(game, pawn.CharacterName);
        if (named >= 0)
        {
            return named;
        }

        for (var i = 0; i < Slots; i++)
        {
            ref var slot = ref Slot(game, i);
            if (slot.CharacterName.Index != 0 && slot.CharacterData is { IsValid: true } data && data.PlayerClass is { } kind && kind.Ptr == pawn.Class.Ptr)
            {
                return i;
            }
        }

        return -1;
    }

    public static bool Ready(in RGameRI.FLoadedPlayerCharacter slot) =>
        slot.CharacterData is not null && ((slot.MeshName.ToString() ?? "").Length == 0 || slot.MeshData is not null);
}

// Puts a changed suit on the player. The change is asked for once the player is on foot, the
// game loads the new suit over the next moments, and then the player's body is built again from
// it. One change runs at a time.
static class Remesh
{
    const double TimeoutSeconds = 20;

    enum Step
    {
        Idle,
        WaitingOnFoot,
        Loading,
    }

    static Step _step;
    static FName _character;
    static Action<RPawnPlayer>? _ask;
    static Command? _asker;
    static long _since;
    static nint _pawn;

    public static bool Busy => _step != Step.Idle;

    public static Outcome Start(Command asker, FName character, Action<RPawnPlayer> ask)
    {
        _asker = asker;
        _character = character;
        _ask = ask;
        _step = Step.WaitingOnFoot;
        return Outcome.Done(World.Driving is not null ? "GET OUT" : "APPLYING");
    }

    public static Outcome Cancel()
    {
        if (!Busy)
        {
            return Outcome.Unchanged("NOT WAITING");
        }

        Stop(Outcome.Unchanged("CANCELLED"));
        return Outcome.Done("CANCELLED");
    }

    public static void Tick()
    {
        if (_step == Step.Idle || World.Player is not { } pawn || World.Controller is not { } controller || !Game.TryGetGameRI(out var game))
        {
            return;
        }

        var index = Loaded.IndexOf(game, _character);
        var onFoot = controller.Pawn is { } driven && driven.Ptr == pawn.Ptr && !pawn.bDeleteMe && pawn.Health > 0;
        if (_step == Step.WaitingOnFoot)
        {
            // Another character is being played now, so this change is no longer theirs.
            if (index < 0 || Loaded.IndexOf(game, pawn) != index)
            {
                Stop(Outcome.Unchanged("CANCELLED"));
            }
            else if (onFoot)
            {
                _ask!(pawn);
                _pawn = pawn.Ptr;
                _step = Step.Loading;
                _since = Stopwatch.GetTimestamp();
            }

            return;
        }

        if (index < 0)
        {
            Log.Warn("remesh", $"The game dropped {_character} while loading the new suit.");
            Stop(Outcome.Failed("FAILED"));
            return;
        }

        if (pawn.Ptr != _pawn)
        {
            Stop(Outcome.Unchanged("CANCELLED"));
            return;
        }

        if (!Loaded.Ready(Loaded.Slot(game, index)))
        {
            if ((Stopwatch.GetTimestamp() - _since) / (double)Stopwatch.Frequency > TimeoutSeconds)
            {
                Log.Info($"The suit for {_character} took too long to load.");
                Stop(Outcome.Failed("TIMED OUT"));
            }

            return;
        }

        if (!onFoot)
        {
            return;
        }

        pawn.SetupPlayerMesh(true);
        Stop(Outcome.Done("CHANGED"));
    }

    public static void Release() => Reset();

    static void Stop(Outcome outcome)
    {
        var asker = _asker;
        Reset();
        asker?.Report(outcome);
    }

    static void Reset()
    {
        _step = Step.Idle;
        _ask = null;
        _asker = null;
    }
}
