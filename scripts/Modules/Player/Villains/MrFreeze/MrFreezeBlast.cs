#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// Mr. Freeze's ice blast, on secondary fire while he aims. The ground where he aims cracks, then
// bursts into the ice spire from his tank fight. Anyone the spike bursts under is knocked out and
// thrown, frozen or not. Further out, legs freeze to the ground the way Batman's freeze cluster
// grenade does it, so they stay there until they thaw or are taken down, and one the game won't
// freeze is knocked down instead. Every vehicle it reaches is destroyed, and what breaks around it
// breaks.
static partial class MrFreeze
{
    const string CrackPath = "DLC_VFX_Freeze.Particles.DLC_Freeze_Tanks_Pre_P";
    const string SpirePath = "DLC_VFX_Freeze.Particles.DLC_Freeze_Tanks_Spire_P";
    const string CrackSoundPath = "Ww_DLC14_OBJ_Freeze_DestructibleIce.Play_DLC14_OBJ_Freeze_DestructibleIce_Ice_Spike_Pre";
    const string SpireSoundPath = "Ww_DLC14_OBJ_Freeze_DestructibleIce.Play_DLC14_OBJ_Freeze_DestructibleIce_Ice_Spike";

    // How far away it can land, how long the crack shows before the spire bursts, how far the
    // burst reaches and how long until the next one.
    const float BlastReach = 2500f;
    const float BlastDelay = 0.35f;
    const float BlastRadius = 450f;
    const float BlastHeight = 250f;
    const float BlastWait = 4f;
    const float BlastForce = 900f;

    // The spike itself reaches this far. Anyone in it takes this much and is thrown this hard.
    const float SpikeReach = 250f;
    const int SpikeDamage = 1000;
    const float SpikeForce = 1600f;

    // Vehicles take many times their full health, past any armour, and parked cars blow up.
    const float SpikeWreck = 10f;
    const int SpikeParked = 1000;

    // The spire's middle sits this far above the ground it bursts from.
    const float GroundAbove = 100f;

    static ParticleSystem? _crackLook;
    static ParticleSystem? _spireLook;
    static AkEvent? _crackSound;
    static AkEvent? _spireSound;

    static bool _secondaryWas;
    static float _nextBlast;
    static bool _blastPending;
    static Vector3 _blastAt;
    static float _blastWhen;

    // From nothing to full as the next blast gets ready, for the HUD.
    static float BlastReady => Math.Clamp(1f - (_nextBlast - World.Now) / BlastWait, 0f, 1f);

    static void LoadBlast()
    {
        _crackLook = Kept(Game.FindObject<ParticleSystem>(CrackPath));
        _spireLook = Kept(Game.FindObject<ParticleSystem>(SpirePath));
        _crackSound = Kept(Game.FindObject<AkEvent>(CrackSoundPath));
        _spireSound = Kept(Game.FindObject<AkEvent>(SpireSoundPath));
        if (_spireLook is null)
        {
            Log.Warn("mr_freeze.blast", "Mr. Freeze's ice spire wasn't found, so his ice blast doesn't show.");
        }
    }

    static void Blast(RPawnPlayer player)
    {
        var secondary = Aim.Secondary;
        var now = World.Now;
        // Holding secondary fire shows where the spire will burst, and letting go while still
        // aiming bursts it.
        if (!secondary && _secondaryWas && Aim.On && !_blastPending && now >= _nextBlast && Aim.Ground(player, BlastReach, out var at))
        {
            _nextBlast = now + BlastWait;
            _blastPending = true;
            _blastAt = at;
            _blastWhen = now + BlastDelay;
            Effect(_crackLook, _crackSound, at);
        }

        _secondaryWas = secondary;
        if (_blastPending && now >= _blastWhen)
        {
            _blastPending = false;
            Burst(player, _blastAt);
        }
    }

    // The ground under where he aims, no further than the blast reaches.
    static void Effect(ParticleSystem? look, AkEvent? sound, Vector3 at)
    {
        if (look is not { IsValid: true } || !Game.TryGetWorldInfo(out var world) || world.GRI?.ObjectPool is not { IsValid: true } pool)
        {
            return;
        }

        if (pool.SpawnEmitter(look, at, new Rotator(0f, 0f, 0f), 0, 1f) is { IsValid: true } shown && sound is { IsValid: true })
        {
            shown.StartAudioEvent(sound);
        }
    }

    // Whether a spire at this spot reaches a thug, seen from its middle. Speared is the spike
    // itself, the rest are frozen.
    static bool InBlast(RPawnPlayer player, RPawnVillain thug, Vector3 at, out bool speared)
    {
        var off = thug.Location - at;
        var flat = off.X * off.X + off.Y * off.Y;
        speared = flat <= SpikeReach * SpikeReach;
        return flat <= BlastRadius * BlastRadius && MathF.Abs(off.Z) <= BlastHeight
            && player.FastTrace(thug.Location, at + Vector3.UnitZ * GroundAbove, Vector3.Zero, false);
    }

    static void Burst(RPawnPlayer player, Vector3 at)
    {
        Effect(_spireLook, _spireSound, at);
        var frozen = 0;
        var taken = 0;
        foreach (var thug in Crowd.StandingEnemies())
        {
            if (!InBlast(player, thug, at, out var speared))
            {
                continue;
            }

            var away = Teleport.Flat(thug.Location - at);
            if (speared)
            {
                Villains.Knock(player, thug, SpikeDamage, Vector3.Normalize(away * 0.5f + Vector3.UnitZ), SpikeForce, fall: true);
                taken++;
                continue;
            }

            if (Frozen(thug))
            {
                continue;
            }

            if (FreezeLegs(player, thug))
            {
                frozen++;
            }
            else if (!thug.bCanTakeFreezeClusterHit)
            {
                Villains.Knock(player, thug, 1, Vector3.Normalize(away + Vector3.UnitZ * 0.6f), BlastForce, fall: true);
            }
        }

        _frozen += frozen;
        if (frozen + taken > 0)
        {
            Say("frozen");
        }

        Villains.Wreck(player, at, BlastRadius, SpikeWreck, SpikeParked);
        Villains.Break(player, at, BlastRadius);
    }

    // The freeze cluster grenade's own hit, with the checks its blast makes first: one it can
    // freeze, not busy with an attack that can't be cut short, not the little ones, and not one
    // the game says can't be frozen right now.
    static bool FreezeLegs(RPawnPlayer player, RPawnVillain thug)
    {
        if (!thug.bCanTakeFreezeClusterHit || !thug.bHostile || thug.AIController is not { IsValid: true } brain
            || brain.GetBehaviour(0) is { IsValid: true } doing && !doing.CanThugBeFrozen()
            || thug.CombatMove is { IsValid: true } move && !move.CanNewAttackBeStarted()
            || thug.CannotBeTargettedByPlayer is { IsValid: true } blocked && player.Controller is { } controller && blocked.Ptr == controller.Ptr
            || (thug.Class.Name.ToString() ?? "").Equals("RPawnVillainPyg", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!brain.HandleFreezeClusterGrenadeHit(player, Vector3.UnitZ))
        {
            return false;
        }

        thug.StartFreezeBlastLegEffect();
        return true;
    }

    static void StopBlast()
    {
        _blastPending = false;
        _secondaryWas = false;
    }

    static void UnloadBlast()
    {
        StopBlast();
        _crackLook = null;
        _spireLook = null;
        _crackSound = null;
        _spireSound = null;
    }
}
