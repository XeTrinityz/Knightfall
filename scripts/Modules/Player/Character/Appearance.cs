#nullable enable

using System.Globalization;
using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// PLAYER > CHARACTER, the APPEARANCE rows.
static class Appearance
{
    // The game has flags for this many levels. No suit ships with all of them.
    const int MostDamageLevels = 20;

    static readonly Held<RCapeComponent, bool> CapeHidden = new("cape hidden", cape => cape.IsCapeHiddenGame, (cape, hidden) => cape.CapeSetHiddenGame(hidden));
    static readonly Held<RPawnPlayer, float> BodyScale = new("body size", pawn => pawn.DrawScale, (pawn, scale) => pawn.SetDrawScale(scale));

    static readonly Dictionary<string, int> LevelCounts = new(StringComparer.OrdinalIgnoreCase);

    public static readonly Toggle NoCape = new(
        "player.character.no_cape", "NO CAPE",
        "Takes the cape off Batman and anyone else who wears one. Gliding still works.")
    {
        Tick = HideCape,
        Restore = CapeHidden.Release,
    };

    public static readonly Slider BodySize = new(
        "player.character.body_size", "BODY SIZE",
        "Makes your character smaller or bigger. Only the look changes. What you fit through and bump into stays the same.",
        min: 0.6, max: 1.6, step: 0.05, value: 1, unit: "X")
    {
        Tick = ScaleBody,
        Restore = BodyScale.Release,
    };

    public static readonly Choice Damage = new(
        "player.character.battle_damage", "BATTLE DAMAGE",
        "How torn up your suit is. Pick a level, then press APPLY BATTLE DAMAGE. It starts on the level you have now. Only standard suits show damage.",
        new[] { ("0", "CLEAN") }, saved: false)
    {
        Opening = RefreshDamage,
    };

    public static readonly Command ApplyDamage = new(
        "player.character.apply_battle_damage", "APPLY BATTLE DAMAGE",
        "Puts the picked damage on your suit. Your save isn't changed, so the next load brings back the story's level.",
        StartDamage);

    static void HideCape()
    {
        if (World.Player is RPawnPlayerBmBase { CapeComponent: { IsValid: true } cape })
        {
            CapeHidden.Hold(cape, true);
        }
    }

    static void ScaleBody()
    {
        if (World.Player is { } pawn)
        {
            var scale = (float)BodySize.Value;
            BodyScale.Hold(pawn, was => was * scale);
        }
    }

    static void RefreshDamage()
    {
        if (World.Player is not { } pawn || !Game.TryGetGameRI(out var game))
        {
            return;
        }

        var index = Loaded.IndexOf(game, pawn);
        if (index < 0)
        {
            return;
        }

        ref var slot = ref Loaded.Slot(game, index);
        var levels = Levels(slot.CharacterName.ToString());
        var list = new (string Id, string Label)[levels + 1];
        list[0] = ("0", "CLEAN");
        for (var level = 1; level <= levels; level++)
        {
            list[level] = (level.ToString(CultureInfo.InvariantCulture), $"LEVEL {level}");
        }

        Damage.SetOptions(list, Math.Clamp(slot.DamageLevel, 0, levels));
    }

    // Each level of a standard suit is its own package, named after the suit with the level on
    // the end. The levels on disk are the ones that can be worn.
    static int Levels(string character)
    {
        if (!LevelCounts.TryGetValue(character, out var count))
        {
            while (count < MostDamageLevels && GameFiles.HasPackage($"{character}_Std_{count + 1}"))
            {
                count++;
            }

            LevelCounts[character] = count;
        }

        return count;
    }

    static Outcome StartDamage()
    {
        if (World.Player is not { } pawn || !Game.TryGetGameRI(out var game))
        {
            return Outcome.NoPlayer;
        }

        if (Remesh.Busy)
        {
            return Outcome.Unchanged("STILL APPLYING");
        }

        var index = Loaded.IndexOf(game, pawn);
        if (index < 0 || !int.TryParse(Damage.CurrentId, NumberStyles.None, CultureInfo.InvariantCulture, out var level))
        {
            return Outcome.Failed("NO CHARACTER");
        }

        ref var slot = ref Loaded.Slot(game, index);
        var character = slot.CharacterName;
        var standard = character.ToString() + "_Std";

        // The game only changes the level while a standard suit is on. Asked for a level that has
        // no package, it takes the character out of the slot.
        if (!(slot.MeshName.ToString() ?? "").StartsWith(standard, StringComparison.OrdinalIgnoreCase))
        {
            return Outcome.Failed("STANDARD SUIT ONLY");
        }

        if (slot.DamageLevel == level)
        {
            return Outcome.Unchanged("NO CHANGE");
        }

        if (level > 0 && !GameFiles.HasPackage($"{standard}_{level}"))
        {
            return Outcome.Failed("NOT INSTALLED");
        }

        return Remesh.Start(ApplyDamage, character, who => who.SetDamageLevel(character, level, false));
    }
}
