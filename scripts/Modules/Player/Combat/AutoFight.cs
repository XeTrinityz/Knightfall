#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// AUTO FIGHT. Defence is AutoDefence. Offence here is a short list of rules, run whenever a new
// move can start without losing the combo.
static class AutoFight
{
    const float Reach = 1500f;
    const float SpecialReach = 900f;
    const float GroundReach = 600f;
    const float ChargeThreatRange = 1000f;

    const float StrikeGapSeconds = 0.12f;
    const float SpecialGapSeconds = 0.6f;
    const float SpecialRetrySeconds = 3f;
    const float GroundGapSeconds = 1.5f;

    // A missed gadget or a whiff leaves the combo shut with no move running. After this long
    // Batman carries on anyway.
    const float ShutWaitSeconds = 0.5f;

    // Takedowns only reopen the combo when they end, which can take a few seconds. A move that
    // has held it shut for longer than this is waiting on a button press that won't come.
    const float StuckMoveSeconds = 6f;

    // How long offence waits in all for attacks on Batman before it stops waiting.
    const float ThreatHoldSeconds = 1f;

    // Medics, gunmen and electrified thugs are dealt with first.
    const float PriorityScale = 0.25f;

    static readonly Dictionary<nint, float> SpecialFailed = new();

    static bool _fighting;
    static float _nextStrikeAt;
    static float _nextSpecialAt;
    static float _nextGroundAt;
    static float _shutSince = -1f;
    static float _heldSince = -1f;
    static nint _waitMove;
    static float _waitMoveSince;

    public static readonly Toggle Option = new(
        "player.combat.auto_fight", "AUTO FIGHT",
        "Batman fights on his own. He counters and dodges every attack, strikes the nearest enemy and uses special takedowns on blockers. Those stay ready all fight.")
    {
        Restore = End,
    };

    public static void Tick()
    {
        if (!Option.On)
        {
            return;
        }

        if (World.Driving is not null
            || World.Player is not { Health: > 0, bDeleteMe: false } me
            || World.Controller is not RPlayerControllerCombat controller
            || !controller.IsInCombat(false))
        {
            End();
            return;
        }

        _fighting = true;
        Combat.KeepTakedownsReady();

        var now = World.Now;
        if (AutoDefence.ActedAt == now || now < _nextStrikeAt || !ComboOpen(controller, me, now))
        {
            return;
        }

        var seen = Look(controller, me, now);

        // A counter or vault that starts just after Batman's own move breaks the combo, because
        // his move has shut the window. So an attack already on its way is answered first.
        if (seen.Threat)
        {
            if (_heldSince < 0f)
            {
                _heldSince = now;
            }

            if (now - _heldSince < ThreatHoldSeconds)
            {
                return;
            }
        }

        _nextStrikeAt = now + StrikeGapSeconds;
        if (seen.Special is { } blocker && now >= _nextSpecialAt)
        {
            switch (Special(controller, me, blocker))
            {
                case Try.Started:
                    _nextSpecialAt = now + SpecialGapSeconds;
                    _heldSince = -1f;
                    return;

                // Batman's last move hasn't reached the point a special can follow it. Striking
                // someone else now would only delay the special.
                case Try.NotYet:
                    return;

                case Try.Failed:
                    SpecialFailed[blocker.Ptr] = now;
                    break;
            }
        }

        // Blockers are never struck. A punch bounces off and breaks the combo, so a blocker whose
        // special failed is left to defence until it is tried again.
        if (seen.Plain is { } target)
        {
            if (me.TryStrike(0f, target))
            {
                _heldSince = -1f;
            }

            return;
        }

        if (seen.Standing == 0 && seen.Downed is not null && now >= _nextGroundAt)
        {
            _nextGroundAt = now + GroundGapSeconds;
            controller.TriggerGroundTakedown(false);
        }
    }

    enum Try
    {
        Started,
        NotYet,
        Failed,
    }

    readonly record struct Sight(RPawnVillain? Plain, RPawnVillain? Special, RPawnVillain? Downed, int Standing, bool Threat);

    // One pass over the enemies: who to hit, who needs a special, and whether an attack is coming.
    static Sight Look(RPlayerControllerCombat controller, RPawnPlayer me, float now)
    {
        RPawnVillain? plain = null, special = null, downed = null;
        float plainScore = float.MaxValue, specialScore = float.MaxValue, downedDistance = GroundReach;
        var standing = 0;
        var threat = false;
        var at = me.Location;

        foreach (var villain in Game.FindObjects<RPawnVillain>())
        {
            if (!villain.IsValid || villain.bDeleteMe || Bodyguards.Is(villain))
            {
                continue;
            }

            var distance = Vector3.Distance(villain.Location, at);
            if (distance > Reach)
            {
                continue;
            }

            threat = threat || Threatens(villain, me, distance);
            if (villain.bGoodAsDead || villain.Health <= 0 || !villain.bIsValidCombatant || !villain.bCanBeHitInCombat)
            {
                continue;
            }

            // A strike at someone still on his way in hangs until he arrives, and the combo
            // runs out first.
            var floored = villain.bCanBeTakenDownOnFloor && villain.IsFloorStrikeable();
            if (floored || villain.IsRagdoll() || !villain.ReadyForCombat())
            {
                if (floored && distance < downedDistance)
                {
                    downed = villain;
                    downedDistance = distance;
                }

                continue;
            }

            standing++;
            var score = distance * (IsPriority(villain) ? PriorityScale : 1f);
            if (!NeedsSpecial(me, villain))
            {
                if (score < plainScore)
                {
                    plain = villain;
                    plainScore = score;
                }
            }
            else if (SpecialFailed.TryGetValue(villain.Ptr, out var failed) && now - failed < SpecialRetrySeconds)
            {
                continue;
            }
            else if (distance <= SpecialReach && score < specialScore)
            {
                special = villain;
                specialScore = score;
            }
        }

        return new Sight(plain, special, downed, standing, threat);
    }

    static bool Threatens(RPawnVillain villain, RPawnPlayer me, float distance)
    {
        if (villain.CombatMove is not { IsValid: true } move)
        {
            return false;
        }

        if (move is RCombatMove_VillainSimultaneousAttack group)
        {
            return Aims(group.Target, me) && group.IsAttacking();
        }

        if (move is not RCombatMove_VillainAttack attack || !Aims(attack.Target, me))
        {
            return false;
        }

        if (attack is RCombatMove_VillainChargeAttack charge)
        {
            return charge.bCharging && !charge.bStartedGrab && distance <= ChargeThreatRange;
        }

        return attack.bCanCounter || attack.bCounterIconOn || attack.IsAttacking();
    }

    static bool Aims(RPawnCombat? target, RPawnPlayer me) => target is { IsValid: true } && target.Ptr == me.Ptr;

    static bool IsPriority(RPawnVillain villain) =>
        villain is RPawnVillainThugMedic
        || villain.ChargedUpBy is { IsValid: true }
        || villain.CurrWeapon is { IsValid: true, WeaponDestroyType: RPawnPlayerCombat.EWeaponDestroyType.WDT_Gun };

    // Enemies an ordinary strike bounces off: shields, stun sticks, brutes, armour, electrified
    // thugs and ninjas, plus anyone the game says would block one.
    static bool NeedsSpecial(RPawnPlayer me, RPawnVillain villain)
    {
        if (BreakableWeapon(villain)
            || villain.bArmoured
            || villain.ChargedUpBy is { IsValid: true }
            || villain.CombatPawnType is RPawnCombat.ECombatPawnType.ECPT_MultiStage or RPawnCombat.ECombatPawnType.ECPT_Ninja)
        {
            return true;
        }

        return villain.CanBlockAttack(me, RDmgType_Strike.StaticClass()) || villain.CanBlockAttack(me, RDmgType_HeavyStrike.StaticClass());
    }

    static bool BreakableWeapon(RPawnVillain villain) =>
        villain.CurrWeapon is { IsValid: true } weapon
        && (weapon.bCanBlockHoldingWeapon
            || weapon.WeaponDestroyType is RPawnPlayerCombat.EWeaponDestroyType.WDT_Shield
                or RPawnPlayerCombat.EWeaponDestroyType.WDT_StunStick
                or RPawnPlayerCombat.EWeaponDestroyType.WDT_Gun);

    // The game's own special handlers pick their target from the stick. These are the same steps
    // with the target chosen for them.
    static Try Special(RPlayerControllerCombat controller, RPawnPlayer me, RPawnVillain villain)
    {
        var current = me.CombatMove;
        if (!controller.CanDoCombat(false, false)
            || current is RCombatMove_BatmanComboTakedown or RCombatMove_BatmanWeaponDestroy
            || (current is { IsValid: true } && !current.CanPerformNextComboMove(true, false)))
        {
            return Try.NotYet;
        }

        var before = current is { IsValid: true } ? current.Ptr : 0;

        // Weapon destroy is an upgrade, so it is only used once the player owns it.
        if (BreakableWeapon(villain) && !villain.IsLieutenant() && controller.CanDoWeaponDestroy(false))
        {
            controller.SpawnWeaponDestroy(false, villain);
        }
        else if (villain.bCanBeTakenDown)
        {
            controller.MovesSinceSpecial = 0;
            controller.SpendAdrenaline();
            controller.NewComboMoveStarted();
            if (!villain.SpawnOverrideTakedown(me))
            {
                me.SpawnComboTakeDown(villain);
            }
        }
        else
        {
            return Try.Failed;
        }

        return me.CombatMove is { IsValid: true } started && started.Ptr != before ? Try.Started : Try.Failed;
    }

    // Starting a move while the combo is shut resets it. The game opens it again when the last
    // move lands.
    static bool ComboOpen(RPlayerControllerCombat controller, RPawnPlayer me, float now)
    {
        if (controller.ComboSize <= 0 || controller.CanContinueCombo())
        {
            _shutSince = -1f;
            _waitMove = 0;
            return true;
        }

        if (me.CombatMove is { IsValid: true } move)
        {
            _shutSince = -1f;
            if (move.Ptr != _waitMove)
            {
                _waitMove = move.Ptr;
                _waitMoveSince = now;
            }

            if (now - _waitMoveSince < StuckMoveSeconds)
            {
                return false;
            }

            _waitMoveSince = now;
            return true;
        }

        if (_shutSince < 0f)
        {
            _shutSince = now;
        }

        return now - _shutSince >= ShutWaitSeconds;
    }

    // Also runs when the option goes off, and whenever Batman is not in a fight.
    static void End()
    {
        if (!_fighting)
        {
            return;
        }

        _fighting = false;
        _nextStrikeAt = 0f;
        _nextSpecialAt = 0f;
        _nextGroundAt = 0f;
        _shutSince = -1f;
        _heldSince = -1f;
        _waitMove = 0;
        SpecialFailed.Clear();
        Combat.ReleaseTakedowns();
    }
}
