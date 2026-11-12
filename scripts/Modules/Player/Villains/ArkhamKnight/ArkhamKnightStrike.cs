#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// The Arkham Knight's drone strike. Secondary fire while aiming calls a barrage of heavy tank
// mortar shells down on whatever his laser is on. The shells whistle down from high above, spread
// about the spot, and each lands in a tank missile's blast with an explosive gel's bang, throws
// the thugs near it and breaks what is there. The mortar's own burst is small and its bang barely
// carries, which is why the bigger blast and bang are added.
static partial class ArkhamKnight
{
    const string MortarPath = "Vehicle_Weapon_Archetypes.Projectiles.Heavy_Tank_Mortar_Projectile";
    const string BlastLookPath = "VFX_Vehicles.Particles.Tank.Tank_Missile_Explosion";
    const string BangPath = "Ww_CHR_BatmanBatgel.Play_Batgel_Upgrade_Explode";
    const string WhizPath = "ww_veh_tank_mortar.Play_Tank_Mortar_Whiz";
    const float BlastScale = 1.5f;

    const int Shells = 6;
    const float ShellGap = 0.3f;
    const float StrikeEvery = 20f;

    // Shells land spread this far about the spot, dropped from this high and this far to one side
    // so they come in on a slant.
    const float Spread = 300f;
    const float DropHeight = 2500f;
    const float DropSlant = 600f;
    const float FallTime = 1.5f;


    // What a shell does to the thugs near where it lands.
    const float ShellSplash = 450f;
    const int ShellDamage = 1000;
    const float ShellThrow = 1000f;
    const float ShellLift = 0.6f;

    // Where his laser is on, for a strike. Nothing when it reaches nothing.
    static Vector3? _markAt;

    static readonly List<(float At, Vector3 Target)> Incoming = new();
    static readonly List<(RMortarProjectile Shell, Vector3 Last, float By)> Falling = new();
    static readonly HashSet<nint> Blasted = new();
    static float _nextStrike;
    static bool _strikeWas;

    static void Strike(RPawnPlayer player)
    {
        var pressed = Aim.Secondary && !_strikeWas;
        _strikeWas = Aim.Secondary;
        if (pressed && World.Now >= _nextStrike && _markAt is { } at)
        {
            _nextStrike = World.Now + StrikeEvery;
            for (var i = 0; i < Shells; i++)
            {
                var angle = Random.Shared.NextSingle() * MathF.Tau;
                var off = MathF.Sqrt(Random.Shared.NextSingle()) * Spread;
                Incoming.Add((World.Now + i * ShellGap, at + new Vector3(MathF.Cos(angle) * off, MathF.Sin(angle) * off, 0f)));
            }
        }

        for (var i = Incoming.Count - 1; i >= 0; i--)
        {
            if (World.Now >= Incoming[i].At)
            {
                Drop(player, Incoming[i].Target);
                Incoming.RemoveAt(i);
            }
        }

        Landed(player);
    }

    static void Drop(RPawnPlayer player, Vector3 target)
    {
        if (Game.FindObject<RMortarProjectile>(MortarPath) is not { IsValid: true } mortar)
        {
            Log.Warn("arkham_knight.strike", "The heavy tank mortar isn't loaded, so the Arkham Knight's drone strike can't fall.");
            return;
        }

        var angle = Random.Shared.NextSingle() * MathF.Tau;
        var from = target + new Vector3(MathF.Cos(angle) * DropSlant, MathF.Sin(angle) * DropSlant, DropHeight);
        if (player.Spawn(mortar.Class, player, FName.None, from, Rotator.FromDirection(target - from), mortar, true) is not RMortarProjectile { IsValid: true } shell)
        {
            Log.Warn("arkham_knight.strike.spawn", "A drone strike shell couldn't be made.");
            return;
        }

        // A straight fall onto the mark, which it bursts on as it hits. The game's own mortar
        // launch lobs up from a tank below, so from above its pull is the wrong way and it climbs
        // away again. The mortar's whiz is played from the shell as it falls.
        shell.TargetPosition = target;
        shell.Velocity = (target - from) / FallTime;
        shell.Acceleration = Vector3.Zero;
        shell.MortarLifespan = FallTime + 1f;
        shell.LifeSpan = FallTime + 3f;
        if (Game.FindObject<AkEvent>(WhizPath) is { IsValid: true } whiz)
        {
            shell.StartAudioEvent(whiz);
        }

        Falling.Add((shell, from, World.Now + FallTime + 1f));
    }

    // The blast is the game's pooled effect, and the bang is played from it as the game plays an
    // explosion's bang from its blast.
    static void Boom(Vector3 at)
    {
        if (Game.FindObject<ParticleSystem>(BlastLookPath) is not { IsValid: true } look || !Game.TryGetWorldInfo(out var world)
            || world.GRI?.ObjectPool is not { IsValid: true } pool
            || pool.SpawnEmitter(look, at, Rotator.FromDirection(Vector3.UnitZ), 0, BlastScale) is not { IsValid: true } blast)
        {
            return;
        }

        if (Game.FindObject<AkEvent>(BangPath) is { IsValid: true } bang)
        {
            blast.StartAudioEvent(bang);
        }
    }

    static void Landed(RPawnPlayer player)
    {
        for (var i = Falling.Count - 1; i >= 0; i--)
        {
            var (shell, last, by) = Falling[i];
            // A shell is marked dead the moment it bursts, and stays a while to be cleaned up.
            if (shell.IsValid && !shell.bDeleteMe && !shell.bDead && World.Now < by)
            {
                Falling[i] = (shell, shell.Location, by);
                continue;
            }

            Falling.RemoveAt(i);
            var at = shell.IsValid ? shell.Location : last;
            Boom(at);
            Blast(player, at);
        }
    }

    // Thugs caught near a shell are thrown down and away from it, and it breaks what is near.
    static void Blast(RPawnPlayer player, Vector3 at)
    {
        foreach (var thug in Crowd.StandingEnemies())
        {
            if (thug.IsIncapacitated())
            {
                continue;
            }

            var away = thug.Location - at;
            if (away.LengthSquared() > ShellSplash * ShellSplash)
            {
                continue;
            }

            away.Z = 0f;
            var push = away.LengthSquared() > 1f ? Vector3.Normalize(away) : Vector3.UnitX;
            Villains.Knock(player, thug, ShellDamage, Vector3.Normalize(push + new Vector3(0f, 0f, ShellLift)), ShellThrow, true);
        }

        Blasted.Clear();
        Villains.Break(player, at, ShellSplash, Blasted);
    }

    static void StopStrike()
    {
        Incoming.Clear();
        Falling.Clear();
        _markAt = null;
        _strikeWas = false;
    }

    static void UnloadStrike()
    {
        StopStrike();
        _nextStrike = 0f;
    }
}
