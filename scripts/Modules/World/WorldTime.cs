#nullable enable

using System.Diagnostics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// WORLD > CITY, the TIME rows. For GAME SPEED, the game keeps two speeds and runs at both
// multiplied, its own for slow motion moments and a debug one that is normally 1. Only the debug
// one is held, so the game's own slow motion still plays on top. SLOW TIME for the menu and the
// radial goes through the same hold, multiplied with GAME SPEED.
static class WorldTime
{
    // How quickly slow time eases in and out, per second of real time.
    const float SlowEase = 10f;

    static readonly Held<RGameInfoBase, float> DebugSpeed = new(
        "game speed", game => game.DebugGameSpeed, (game, speed) => game.SetDebugGameSpeed(speed));

    static readonly (string Id, string Label)[] SlowSteps = { ("off", "OFF"), ("tenth", "10%"), ("quarter", "25%"), ("half", "50%") };
    static readonly float[] SlowFactors = { 1f, 0.1f, 0.25f, 0.5f };

    static float _slow = 1f;
    static long _easedAt;

    public static readonly Slider Speed = new(
        "world.city.game_speed", "GAME SPEED",
        "How fast the whole world runs, you included, from deep slow motion to double speed. The game's own slow motion still happens on top.",
        min: 0.1, max: 2, step: 0.1, value: 1, unit: "X");

    public static readonly Toggle Freeze = new(
        "world.city.freeze_world", "FREEZE WORLD",
        "Everything but you stops where it is: enemies, bullets, vehicles and the rest of the city. You can still move and fight. Switch it off to let it all go again. A load or a restart ends it.",
        saved: false);

    // A SLOW TIME row. It is a look setting, so TURN EVERYTHING OFF leaves it alone.
    public static Choice SlowChoice(string id, string label, string help) => new(id, label, help, SlowSteps)
    {
        Preference = true,
    };

    static WorldInfo? _frozen;

    // The game's own players-only switch, which photo mode uses. Only the player and their car
    // keep going. It is set from the draw callback and not the game tick, because the game tick
    // stops with the world and then couldn't let it go again.
    public static void SyncFreeze()
    {
        if (Freeze.On && Game.TryGetWorldInfo(out var world) && world.IsValid)
        {
            // A world frozen from its first frame can't run what finishes its own load, so the
            // freeze ends with the world it was set in.
            if (_frozen is not null && !ReferenceEquals(_frozen, world))
            {
                _frozen = null;
                Freeze.Show(false);
                return;
            }

            if (!world.bPlayersOnly)
            {
                world.bPlayersOnly = true;
            }

            _frozen = world;
            return;
        }

        Release();
    }

    // Only let go when it was ours, so photo mode keeps its own.
    public static void Release()
    {
        if (_frozen is { IsValid: true } world && world.bPlayersOnly)
        {
            world.bPlayersOnly = false;
        }

        _frozen = null;
    }

    // Every game tick. Slow time eases by the real clock, since the game's own clock is the one
    // being slowed. At normal speed with nothing slowing it, the game's value is put back.
    public static void HoldSpeed()
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = _easedAt == 0 ? 0f : MathF.Min((float)((now - _easedAt) / (double)Stopwatch.Frequency), 0.25f);
        _easedAt = now;

        var target = 1f;
        if (Menu.Showing)
        {
            target = MathF.Min(target, SlowFactors[General.SlowTime.Index]);
        }

        if (Radial.IsOpen)
        {
            target = MathF.Min(target, SlowFactors[Radial.SlowTime.Index]);
        }

        _slow += (target - _slow) * (1f - MathF.Exp(-SlowEase * seconds));
        if (MathF.Abs(_slow - target) < 0.005f)
        {
            _slow = target;
        }

        var wanted = (float)Speed.Value * _slow;
        if (MathF.Abs(wanted - 1f) < 0.0005f)
        {
            DebugSpeed.Release();
            return;
        }

        if (World.Info is { IsValid: true } game)
        {
            DebugSpeed.Hold(game, wanted);
        }
    }

    public static void ReleaseSpeed()
    {
        DebugSpeed.Release();
        _slow = 1f;
        _easedAt = 0;
    }
}
