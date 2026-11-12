#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Framework;

namespace Knightfall;

// AUTO COUNTER and AUTO DODGE RED ATTACKS. They press counter and evade for the player with the
// game's own handlers, TryCounter and TryRedirect, the same calls its dev combat bot makes.
static class AutoDefence
{
    // Red attacks a vault can't answer in this long get an evade away instead.
    const float EvadeAfterSeconds = 0.25f;

    // Attack moves are reused for later attacks, so an answer only counts for this long.
    const float AnswerSeconds = 2f;

    const float ChargeVaultRange = 500f;
    const float ChargeStopRange = 300f;

    // When each red attack's window opened, by move.
    static readonly Dictionary<nint, float> RedOpened = new();

    // When each attack was last answered, by move.
    static readonly Dictionary<nint, float> Answered = new();

    static readonly List<nint> Expired = new();

    public static readonly Toggle AutoCounter = new(
        "player.combat.auto_counter", "AUTO COUNTER",
        "Counters every attack aimed at you the moment it can be countered. Group attacks and knife combos too.");

    public static readonly Toggle AutoDodgeRed = new(
        "player.combat.auto_dodge_red", "AUTO DODGE RED ATTACKS",
        "Vaults over red attacks and charging thugs. A charge that can't be vaulted is stopped in its tracks.");

    // AUTO FIGHT defends with both, whatever their own rows say.
    static bool Countering => AutoCounter.On || AutoFight.Option.On;

    static bool Dodging => AutoDodgeRed.On || AutoFight.Option.On;

    // The game time of the last counter, vault or evade made here. AUTO FIGHT doesn't start a
    // move of its own on the same tick.
    public static float ActedAt { get; private set; } = -1f;

    public static void Tick()
    {
        var counter = Countering;
        var dodge = Dodging;
        if (!counter && !dodge)
        {
            if (RedOpened.Count > 0 || Answered.Count > 0)
            {
                RedOpened.Clear();
                Answered.Clear();
            }

            return;
        }

        if (World.Driving is not null || World.Player is not { Health: > 0, bDeleteMe: false } me)
        {
            return;
        }

        Sweep();
        foreach (var villain in Game.FindObjects<RPawnVillain>())
        {
            if (!villain.IsValid || villain.bDeleteMe || villain.CombatMove is not { IsValid: true } move)
            {
                continue;
            }

            if (counter && TryCounter(me, move))
            {
                ActedAt = World.Now;
                return;
            }

            if (dodge)
            {
                Dodge(me, villain, move);
            }
        }
    }

    // bCanFail false means a press with nothing to counter does nothing, where a real press
    // would play the whiff and lose the combo.
    static bool TryCounter(RPawnPlayer me, RCombatMove move)
    {
        bool open;
        if (move is RCombatMove_VillainSimultaneousAttack group)
        {
            // Its window only opens in the slow motion moment, which the game skips while Batman
            // is mid strike. Each counter answers the next attacker in the group.
            open = Aims(group.Target, me) && (group.CanCounter() || group.IsAttacking());
        }
        else
        {
            open = move is RCombatMove_VillainAttack attack && attack.bCanCounter && Aims(attack.Target, me);
        }

        return open && me.TryCounter(false, 0f) != RPawnPlayerCombat.ETryCounterResult.ETCR_Nothing;
    }

    static void Dodge(RPawnPlayer me, RPawnVillain villain, RCombatMove move)
    {
        if (move is not RCombatMove_VillainAttack attack || !Aims(attack.Target, me) || Answered.ContainsKey(move.Ptr))
        {
            return;
        }

        if (attack is RCombatMove_VillainChargeAttack charge)
        {
            DodgeCharge(me, villain, charge);
            return;
        }

        if (!IsRed(attack))
        {
            return;
        }

        // The icon goes on as the window opens. The hook covers an icon-less red attack from the
        // enemy Batman is already fighting, where the game skips the icon.
        if (!attack.bCounterIconOn && !RedOpened.ContainsKey(move.Ptr))
        {
            return;
        }

        if (!RedOpened.TryGetValue(move.Ptr, out var opened))
        {
            opened = World.Now;
            RedOpened[move.Ptr] = opened;
        }

        if (me.TryRedirect(villain))
        {
            Answer(move.Ptr);
            return;
        }

        // Batman can't vault out of every move. Once the vault has had its chance, evade away.
        if (World.Now - opened >= EvadeAfterSeconds)
        {
            me.SpawnEvade(Away(me, villain));
            Answer(move.Ptr);
        }
    }

    static void DodgeCharge(RPawnPlayer me, RPawnVillain villain, RCombatMove_VillainChargeAttack charge)
    {
        if (!charge.bCharging || charge.bStartedGrab)
        {
            return;
        }

        var distance = Vector3.Distance(villain.Location, me.Location);
        if (distance > ChargeVaultRange)
        {
            return;
        }

        // A vault out of a takedown cuts it short and loses the combo, so AUTO FIGHT stops the
        // charge instead while it is in one.
        var midSpecial = AutoFight.Option.On && me.CombatMove is RCombatMove_BatmanComboTakedown or RCombatMove_BatmanWeaponDestroy;
        if (midSpecial)
        {
            charge.StopCharge(false);
            Answer(charge.Ptr);
        }
        else if (me.TryRedirect(villain))
        {
            Answer(charge.Ptr);
        }
        else if (distance <= ChargeStopRange)
        {
            // What a batarang hit does to a charge.
            charge.StopCharge(false);
            Answer(charge.Ptr);
        }
    }

    static bool Aims(RPawnCombat? target, RPawnPlayer me) => target is { IsValid: true } && target.Ptr == me.Ptr;

    static bool IsRed(RCombatMove_VillainAttack attack) =>
        attack.AttackCounterType is RPawnVillain.AttackCounterTypes.ACT_Unblockable or RPawnVillain.AttackCounterTypes.ACT_UnblockableNoIcon;

    static Vector3 Away(RPawnPlayer me, RPawnVillain villain)
    {
        var away = me.Location - villain.Location;
        away.Z = 0f;
        return away.LengthSquared() < 1f ? Vector3.UnitX : Vector3.Normalize(away);
    }

    static void Answer(nint move)
    {
        ActedAt = World.Now;
        Answered[move] = World.Now;
        RedOpened.Remove(move);
    }

    static void Sweep()
    {
        var now = World.Now;
        foreach (var (move, at) in Answered)
        {
            // A new world starts the clock again, which also clears these.
            if (now - at >= AnswerSeconds || now < at)
            {
                Expired.Add(move);
            }
        }

        foreach (var (move, at) in RedOpened)
        {
            if (now - at >= AnswerSeconds || now < at)
            {
                Expired.Add(move);
            }
        }

        foreach (var move in Expired)
        {
            Answered.Remove(move);
            RedOpened.Remove(move);
        }

        Expired.Clear();
    }

    [Redirect(typeof(RCombatMove_VillainAttack), nameof(RCombatMove_VillainAttack.CanCounterStart))]
    static void CanCounterStart(RCombatMove_VillainAttack self)
    {
        Redirects.RunOriginal();
        try
        {
            if (Dodging && IsRed(self) && World.Player is { } me && Aims(self.Target, me))
            {
                RedOpened.TryAdd(self.Ptr, World.Now);
            }
        }
        catch (Exception error)
        {
            Log.Error("combat.red_window", "A red attack's window could not be noted", error);
        }
    }

    // Each knife slash asks whether the counter button is held and the stick pulled away. Auto
    // counter can't hold a button, so it answers yes for attacks on the player.
    [Redirect(typeof(RCombatMove_VillainMultiAttackAndCounter), nameof(RCombatMove_VillainMultiAttackAndCounter.PlayerIsCounteringProperly))]
    static bool PlayerIsCounteringProperly(RCombatMove_VillainMultiAttackAndCounter self)
    {
        try
        {
            if (Countering && World.Player is { } me && Aims(self.Target, me))
            {
                return true;
            }
        }
        catch (Exception error)
        {
            Log.Error("combat.knife", "The knife counter check failed", error);
        }

        return Redirects.RunOriginal<bool>();
    }

    // The finishing move only comes if the counter was tapped once per slash. Auto counter never
    // taps, so each slash is counted as tapped.
    [Redirect(typeof(RCombatMove_VillainMultiAttackAndCounter), nameof(RCombatMove_VillainMultiAttackAndCounter.VillainPlayedSuccess))]
    static void VillainPlayedSuccess(RCombatMove_VillainMultiAttackAndCounter self, RAnimUtil_PosePlayer.FTransitionId newId, float timeLeftOver)
    {
        try
        {
            if (Countering && World.Player is { } me && Aims(self.Target, me) && !self.bPlayerHit && self.NumCounterReleases < self.NumCombatAnimHits)
            {
                self.NumCounterReleases = self.NumCombatAnimHits;
            }
        }
        catch (Exception error)
        {
            Log.Error("combat.knife_finish", "The knife finish could not be set up", error);
        }

        Redirects.RunOriginal();
    }
}
