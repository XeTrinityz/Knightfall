#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using MoveType = BmSDK.BmGame.RRobinBotController.BotCombatMoveType;
using Pace = BmSDK.Engine.RAutomaticTransitions.EMovementSpeed;

namespace Knightfall;

// How the sidekick fights and talks in a fight.
static partial class Sidekick
{
    // The partner AI's own strike reach. An enemy further off is run to, never struck from afar.
    const float StrikeReach = 600f;

    // They stop this far short of an enemy they run to. The game's own figure.
    const float StrikeStop = 100f;

    // How soon the next move is tried once one has started. The game waits a full second.
    const float PressTempo = 0.5f;

    // How often they look again while running to an enemy.
    const float ChaseTempo = 0.3f;

    // How hard their strikes land. A start, rising with the player's combo up to the most.
    const float MightFrom = 1.5f;
    const float MightMost = 3f;
    const int MightCombo = 20;

    // A fight earns praise when it lasted this long and the player hit this combo or was never hurt.
    const float PraiseAfter = 6f;
    const int PraiseCombo = 20;
    const float PraiseDelay = 1.2f;
    const float PraiseRange = 2500f;

    // Partner lines are named for the speaker and, when the line says a name, the hero it was
    // recorded for, like combat_player_dual_play_rb_bm_succ for Robin praising Batman. A line
    // that names nobody, like Robin joining a team move, suits any partner.
    const string LineMark = "dual_play_";

    static readonly (string Code, string Hero)[] Heroes =
    {
        ("bm", "Batman"),
        ("rb", "Robin"),
        ("nw", "Nightwing"),
        ("cw", "Catwoman"),
        ("bg", "Batgirl"),
    };

    static nint _strengthened;
    static float _strengthenedTo;
    static bool _inFight;
    static float _fightFrom;
    static bool _joined;
    static int _bestCombo;
    static bool _hurt;
    static int _healthFrom;
    static float _praiseAt = -1f;

    // The game lets partners land hits that only stagger. A story fight can pause the player's
    // hits or allow only some, and then the sidekick keeps the game's own rule.
    [Redirect(typeof(RPawnVillain), nameof(RPawnVillain.IsBotDamageAllowed))]
    static bool IsBotDamageAllowed(RPawnVillain self, RPawnCombat.FDamageInfo info)
    {
        try
        {
            if (Hits.On && Fights.On && info.Attacker is RPawnPlayerCombat { Controller: RRobinBotController bot } && Ours(bot))
            {
                if (self.CombatManager is RBMCombatManager manager && manager.IsPlayerDamageSuspended())
                {
                    return false;
                }

                if (AllowedHits(self) == 0)
                {
                    return true;
                }
            }
        }
        catch (Exception error)
        {
            Log.Error("sidekick.hits", "The sidekick's hit check failed", error);
        }

        return Redirects.RunOriginal<bool>();
    }

    // How many kinds of hit a story fight allows on this enemy, none meaning all. A length the
    // list can't have counts as a limit, so the game's own rule stays.
    const int MostAllowedHits = 16;

    static unsafe int AllowedHits(RPawnVillain villain)
    {
        var length = *(int*)(villain.Ptr + Offsets.VillainDamageAllowed + Offsets.ListLengthAfterData);
        return length is >= 0 and <= MostAllowedHits ? length : MostAllowedHits;
    }

    [Redirect(typeof(RRobinBotController), nameof(RRobinBotController.TryCombatMove))]
    static void TryCombatMove(RRobinBotController self)
    {
        RPawnPlayerCombat? body = null;
        try
        {
            if (Ours(self) && self.BotPawn is RPawnPlayerCombat { IsValid: true } mine && Game.TryGetPlayerController(out var controller)
                && controller.Pawn is RPawnPlayerCombat { IsValid: true } player)
            {
                body = mine;
                if (Pressed(self, mine, player))
                {
                    Started(self, mine);
                    return;
                }
            }
        }
        catch (Exception error)
        {
            Log.Error("sidekick.fight", "The sidekick's fight move failed", error);
        }

        Redirects.RunOriginal();
        try
        {
            if (body is { IsValid: true, CombatMove: not null })
            {
                Started(self, body);
            }
        }
        catch (Exception error)
        {
            Log.Error("sidekick.fight", "The sidekick's fight move failed", error);
        }
    }

    // A move of our own, or false to let the game pick. A combo the game has lined up, like the
    // next hit of a beatdown, is always the game's to finish.
    static bool Pressed(RRobinBotController bot, RPawnPlayerCombat body, RPawnPlayerCombat player)
    {
        if (bot.NextMove != MoveType.BCMT_None || !body.CanBotAttack() || !Game.TryGetGameRI(out var game))
        {
            return false;
        }

        if (Covers.On && Cover(bot, body, player, game))
        {
            return true;
        }

        return Presses.On && (Finish(bot, body, player, game) || Chase(bot, body, player, game));
    }

    static void Started(RRobinBotController bot, RPawnPlayerCombat body)
    {
        if (Presses.On && body.CombatMove is not null)
        {
            bot.TryMoveTime = MathF.Min(bot.TryMoveTime, PressTempo);
        }

        Strengthen(body);

        if (Talks.On && _inFight && !_joined && body.CombatMove is not null)
        {
            _joined = true;
            body.PlayDualPlayTalk();
        }
    }

    // An enemy on the way to hit the player, or mid-swing, is hit first when one is in reach.
    static bool Cover(RRobinBotController bot, RPawnPlayerCombat body, RPawnPlayerCombat player, RGameRI game)
    {
        RPawnVillain? threat = null;
        var nearest = StrikeReach * StrikeReach;
        var list = game.CombatPawnList;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is not RPawnVillain { IsValid: true, bDeleteMe: false } villain || villain.GetTargetPawn() is not { } target || target.Ptr != player.Ptr
                || !villain.IsAttacking())
            {
                continue;
            }

            var gap = Flat(villain.Location, body.Location);
            if (gap < nearest)
            {
                nearest = gap;
                threat = villain;
            }
        }

        if (threat is null)
        {
            return false;
        }

        bot.TryMoveTime = 1f;
        body.StopMoveTo();
        body.StopLookAt();
        body.StopFaceAt();
        body.StopAimAt();
        return body.TryStrike(0f, threat);
    }

    // A strike works out its damage once, as it starts, and lands with that. The game gives a
    // partner's strikes the same base damage as the player's, but none of the player's combo
    // moves or finishing blows, so thugs took many hits. Their strikes grow with the player's
    // combo instead, so the pair's rhythm carries both of them. A finishing blow is left alone.
    // A chained strike reuses the same move and works its damage out again, so a strike counts
    // as done only while it still holds the damage it was given.
    const float FinishingBlow = 10000f;

    static void Strengthen(RPawnPlayerCombat body)
    {
        if (!Hits.On || body.CombatMove is not RCombatMove_BatmanStrike { IsValid: true } strike || strike.StrikeDamageAmount <= 0f
            || strike.StrikeDamageAmount >= FinishingBlow || (strike.Ptr == _strengthened && strike.StrikeDamageAmount == _strengthenedTo))
        {
            return;
        }

        var combo = World.Controller is RPlayerControllerCombat combat ? Math.Clamp(combat.ComboSize, 0, MightCombo) : 0;

        // Hits land as whole numbers, so the boost rounds up rather than being lost.
        var boosted = MathF.Ceiling(strike.StrikeDamageAmount * (MightFrom + (MightMost - MightFrom) * combo / MightCombo));
        _strengthened = strike.Ptr;
        _strengthenedTo = boosted;
        strike.StrikeDamageAmount = boosted;
    }

    // The most hurt enemy in reach is hit again, so they finish someone instead of spreading
    // their hits around. Their heavy strikes knock enemies down, so one on the ground gets the
    // game's own ground strike. The player's target is the player's, as the game has it. With
    // nobody hurt the game picks.
    static bool Finish(RRobinBotController bot, RPawnPlayerCombat body, RPawnPlayerCombat player, RGameRI game)
    {
        var free = body.CombatMove is null;
        RPawnVillain? hurt = null;
        var down = false;
        var lowest = 1f;
        var list = game.CombatPawnList;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is not RPawnVillain { IsValid: true, bDeleteMe: false, bHostile: true, HealthMax: > 0 } villain || villain.Health >= villain.HealthMax
                || villain.IsIncapacitated() || Flat(villain.Location, body.Location) > StrikeReach * StrikeReach || villain.IsTargettedBy(player))
            {
                continue;
            }

            var floored = villain.IsFloorStrikeable();
            if ((floored && !free) || (!floored && villain.IsRagdoll()))
            {
                continue;
            }

            var left = (float)villain.Health / villain.HealthMax;
            if (left < lowest)
            {
                lowest = left;
                hurt = villain;
                down = floored;
            }
        }

        if (hurt is null)
        {
            return false;
        }

        bot.TryMoveTime = 1f;
        body.StopMoveTo();
        body.StopLookAt();
        body.StopFaceAt();
        body.StopAimAt();
        if (!down)
        {
            return body.TryStrike(0f, hurt);
        }

        body.SpawnStrike(RCombatMove_BatmanHeavyStrike.StaticClass(), RDmgType_StrikeFloor.StaticClass(), true, hurt, 0f);
        bot.LastFloorStrikeTime = World.Now;
        return true;
    }

    // The game keeps a partner's strikes off an enemy who is attacking the player, so they never
    // land. Covering the player needs them to, so for ours that one rule is lifted. The rest
    // stays: not the enemy the player is striking, and not one only some heroes can hit.
    [Redirect(typeof(RPawnPlayerCombat), nameof(RPawnPlayerCombat.IsValidBotStrikeTarget))]
    static bool IsValidBotStrikeTarget(RPawnPlayerCombat self, RPawnVillain target, Class damageType)
    {
        var valid = Redirects.RunOriginal<bool>();
        try
        {
            if (!valid && Covers.On && Fights.On && self.Controller is RRobinBotController bot && Ours(bot) && target is { IsValid: true }
                && Game.TryGetPlayerController(out var controller) && controller.Pawn is RPawnPlayerCombat player
                && target.GetTargetPawn() is { } aimed && aimed.Ptr == player.Ptr && target.IsAttacking() && !target.IsTargettedBy(player)
                && target.IsVulnerableToPawn(self) && target.CanBeTargettedByBot(self, damageType) && Hittable(target, self))
            {
                return true;
            }
        }
        catch (Exception error)
        {
            Log.Error("sidekick.cover", "The sidekick's cover check failed", error);
        }

        return valid;
    }

    static bool Hittable(RPawnVillain target, RPawnPlayerCombat by)
    {
        var only = target.CanOnlyBeHitBy;
        return only.ToString() is "None" or "" || (only.Index == by.CharacterName.Index && only.Number == by.CharacterName.Number);
    }

    // With nobody in reach the game walks them to whoever is nearest them, however far off. They
    // run to the enemy nearest the player instead, so the fight stays around you. A fight with
    // its own spacing, like a boss the camera keeps in view, is left to the game.
    static bool Chase(RRobinBotController bot, RPawnPlayerCombat body, RPawnPlayerCombat player, RGameRI game)
    {
        if (body.CombatManager is RBMCombatManager manager && manager.GetPawnToKeepInView() is not null)
        {
            return false;
        }

        if (body.FindHighestPriorityClosePawn(body.GetStrikeDamageType(), out var close, StrikeReach) && close is not null)
        {
            return false;
        }

        RPawnVillain? target = null;
        var nearest = float.MaxValue;
        var list = game.CombatPawnList;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is not RPawnVillain { IsValid: true, bDeleteMe: false, bHostile: true } villain || !villain.IsCurrentCombatant() || villain.IsIncapacitated())
            {
                continue;
            }

            var gap = Flat(villain.Location, player.Location);
            if (gap < nearest)
            {
                nearest = gap;
                target = villain;
            }
        }

        if (target is null)
        {
            return false;
        }

        var away = body.Location - target.Location;
        away.Z = 0f;
        var wanted = target.Location + (away.LengthSquared() > 1f ? Vector3.Normalize(away) * StrikeStop : Vector3.Zero);

        // The game moves the spot onto ground they can walk. The binding sends in the value the
        // variable already holds.
        var spot = wanted;
        bot.GetBestMoveToPoint(out spot);
        if (!float.IsFinite(spot.X) || !float.IsFinite(spot.Y) || !float.IsFinite(spot.Z) || Vector3.DistanceSquared(spot, wanted) > StrikeReach * StrikeReach)
        {
            spot = wanted;
        }

        bot.TryMoveTime = ChaseTempo;
        body.FaceAt(target.Location);
        body.SetMovementSpeed(Pace.MS_Run, 1f, default);
        body.MoveTo(spot);
        return true;
    }

    static float Flat(Vector3 one, Vector3 other)
    {
        var x = one.X - other.X;
        var y = one.Y - other.Y;
        return x * x + y * y;
    }

    // Keeps track of the player's fight for the sidekick's lines.
    static void FightWatch(RPlayerController controller)
    {
        if (World.Now < _fightFrom)
        {
            _inFight = false;
            _praiseAt = -1f;
        }

        if (controller.IsInCombat(false))
        {
            if (!_inFight)
            {
                _inFight = true;
                _fightFrom = World.Now;
                _joined = false;
                _bestCombo = 0;
                _hurt = false;
                _healthFrom = controller.Pawn?.Health ?? 0;
                _praiseAt = -1f;
            }

            if (controller is RPlayerControllerCombat combat)
            {
                _bestCombo = Math.Max(_bestCombo, combat.ComboSize);
            }

            if (controller.Pawn is { } player && player.Health < _healthFrom)
            {
                _hurt = true;
            }

            return;
        }

        if (_inFight)
        {
            _inFight = false;
            if (World.Now - _fightFrom >= PraiseAfter && (_bestCombo >= PraiseCombo || !_hurt))
            {
                _praiseAt = World.Now + PraiseDelay;
            }
        }

        if (_praiseAt < 0f || World.Now < _praiseAt)
        {
            return;
        }

        _praiseAt = -1f;
        if (Talks.On && _bot?.BotPawn is RPawnPlayerCombat { IsValid: true } speaker && controller.Pawn is { } listener
            && Vector3.DistanceSquared(speaker.Location, listener.Location) <= PraiseRange * PraiseRange)
        {
            speaker.PlayDualPlayPraiseLine();
        }
    }

    // Every partner line goes through here, the game's own ones too. Ours only speaks to the hero
    // the line was recorded for, so nobody is called by the wrong name.
    [Redirect(typeof(RPawnPlayerCombat), nameof(RPawnPlayerCombat.PlayDualPlaySpeech))]
    static void PlayDualPlaySpeech(RPawnPlayerCombat self, AkDialogueLine line, bool timeLimited)
    {
        try
        {
            Adopt();
            if (Present && _bot?.BotPawn is { } speaker && speaker.Ptr == self.Ptr && (!Talks.On || !Fits(line)))
            {
                return;
            }
        }
        catch (Exception error)
        {
            Log.Error("sidekick.talk", "The sidekick's line check failed", error);
        }

        Redirects.RunOriginal();
    }

    static bool Fits(AkDialogueLine? line)
    {
        if (line is null || !Game.TryGetPlayerController(out var controller) || controller.Pawn is not RPawnPlayerCombat listener)
        {
            return false;
        }

        var name = line.Name.ToString() ?? "";
        var at = name.IndexOf(LineMark, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return true;
        }

        // After the mark come the speaker's code, then the hero's code when the line has one.
        var parts = name[(at + LineMark.Length)..].Split('_');
        if (parts.Length < 2 || parts[1].Length != 2)
        {
            return true;
        }

        var to = Array.Find(Heroes, hero => hero.Code.Equals(parts[1], StringComparison.OrdinalIgnoreCase)).Hero;
        return to is not null && to == listener.CharacterName.ToString();
    }
}
