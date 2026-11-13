#nullable enable

using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// PLAYER > CHARACTER, the SUIT rows. The pick is saved in the game's options at once, the way the
// showcase menu does it, and the suit goes on once the player is on foot.
static class Suits
{
    const string ShowcasePage = "Players";
    const string Prefix = "Playable_";

    public static readonly Choice Suit = new(
        "player.character.suit", "SUIT",
        "Pick one of the suits you have unlocked for the character you are playing, then press APPLY SUIT. It starts on the suit you are wearing.",
        new[] { ("", "OPEN IN GAME") }, saved: false)
    {
        Opening = Refresh,
    };

    public static readonly Command Apply = new(
        "player.character.apply_suit", "APPLY SUIT",
        "Puts the picked suit on right where you stand. In the Batmobile it waits until you get out. The game remembers the pick, as it does from the showcase.",
        Start)
    {
        Block = static () => Suit.CurrentId.Length == 0 ? "NO SUIT TO APPLY" : null,
    };

    public static readonly Command Cancel = new(
        "player.character.cancel_suit", "CANCEL SUIT CHANGE",
        "Stops a suit or battle damage change that is still waiting to go on.",
        Remesh.Cancel)
    {
        Block = static () => Remesh.Busy ? null : "NOTHING WAITING",
    };

    static void Refresh()
    {
        if (World.Info is not { } info || World.Player is not { } pawn || !Game.TryGetGameRI(out var game) || game.PersistentOptions is not { IsValid: true } options)
        {
            return;
        }

        var index = Loaded.IndexOf(game, pawn);
        if (index < 0)
        {
            Suit.SetOptions(new[] { ("", "NO CHARACTER") }, 0);
            return;
        }

        ref var slot = ref Loaded.Slot(game, index);
        var character = slot.CharacterName.ToString();
        var id = SkinId(Owner(character));

        // Some installs list a suit more than once, with spare rows that point at another suit's
        // skin. So each suit takes the skin no other suit uses, when it has one.
        info.ShowcaseValidateSorting();
        var suits = new List<(string Key, List<string> Skins)>();
        var users = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in info.ShowcaseItems)
        {
            var skin = item.Skin.ToString() ?? "";
            var key = item.Item.ToString() ?? "";
            if (skin.Length == 0 || key.Length == 0
                || !string.Equals(item.Page.ToString(), ShowcasePage, StringComparison.OrdinalIgnoreCase)
                || !SkinId(skin).Equals(id, StringComparison.OrdinalIgnoreCase)
                || !Showcase.Unlocked(options, item, key))
            {
                continue;
            }

            var at = suits.FindIndex(suit => suit.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (at < 0)
            {
                at = suits.Count;
                suits.Add((key, new List<string>()));
            }

            if (!suits[at].Skins.Exists(known => known.Equals(skin, StringComparison.OrdinalIgnoreCase)))
            {
                suits[at].Skins.Add(skin);
            }

            if (!users.TryGetValue(skin, out var keys))
            {
                users[skin] = keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            keys.Add(key);
        }

        var list = new List<(string Id, string Label)>();
        foreach (var (key, skins) in suits)
        {
            var skin = skins.Find(candidate => users[candidate].Count == 1) ?? skins[0];
            if (!list.Exists(entry => entry.Id.Equals(skin, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add((skin, Showcase.Label(key, skin)));
            }
        }

        if (list.Count == 0)
        {
            Suit.SetOptions(new[] { ("", "NONE UNLOCKED") }, 0);
            return;
        }

        var worn = slot.MeshName.ToString() ?? "";
        var wearing = list.FindIndex(entry => Wearing(worn, entry.Id));
        if (wearing < 0)
        {
            wearing = list.FindIndex(entry => entry.Id.Equals(Owner(character) + "_Std", StringComparison.OrdinalIgnoreCase));
        }

        Suit.SetOptions(list.ToArray(), Math.Max(0, wearing));
    }

    // Robin, Nightwing and Catwoman have second versions whose names end in 0. They wear the first
    // one's suits.
    public static string Owner(string character) => character.EndsWith('0') ? character[..^1] : character;

    // The suit the game would load for a character. That is the one picked in the showcase, or
    // the standard suit at its battle damage level.
    public static string Active(RPersistentOptions options, string character, int damage)
    {
        var standard = Owner(character) + "_Std";
        var id = SkinId(Owner(character));
        var picks = options.ActivePlayerSkins;
        for (var i = 0; i < picks.Count; i++)
        {
            var pick = picks[i];
            if (!string.Equals(pick.Id.ToString(), id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var skin = pick.Skin.ToString() ?? "";
            if (skin.Length > 0 && !skin.Equals(standard, StringComparison.OrdinalIgnoreCase) && GameFiles.HasPackage(skin))
            {
                return skin;
            }

            break;
        }

        var worn = $"{standard}_{damage}";
        return damage > 0 && GameFiles.HasPackage(worn) ? worn : standard;
    }

    // The game's own rule for which character a skin belongs to. The package prefix comes off,
    // then the style on the end. It is done here so no text has to go through a script call.
    static string SkinId(string skin)
    {
        if (skin.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            skin = skin[Prefix.Length..];
        }

        var style = skin.LastIndexOf('_');
        return style >= 0 ? skin[..style] : skin;
    }

    // A worn standard suit has its battle damage level on the end of its name.
    static bool Wearing(string worn, string skin)
    {
        if (!worn.StartsWith(skin, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = worn.AsSpan(skin.Length);
        if (rest.Length == 0)
        {
            return true;
        }

        if (rest[0] != '_' || rest.Length == 1)
        {
            return false;
        }

        foreach (var letter in rest[1..])
        {
            if (!char.IsAsciiDigit(letter))
            {
                return false;
            }
        }

        return true;
    }

    static Outcome Start()
    {
        var requested = Suit.CurrentId;
        if (requested.Length == 0)
        {
            return Outcome.Failed("NO SUIT");
        }

        if (World.Player is not { } pawn || World.Controller is not { } controller || !Game.TryGetGameRI(out var game) || game.PersistentOptions is not { IsValid: true } options)
        {
            return Outcome.NoPlayer;
        }

        if (Remesh.Busy)
        {
            return Outcome.Unchanged("STILL APPLYING");
        }

        var index = Loaded.IndexOf(game, pawn);
        if (index < 0)
        {
            return Outcome.Failed("NO CHARACTER");
        }

        ref var slot = ref Loaded.Slot(game, index);
        var character = slot.CharacterName;
        if (!SkinId(requested).Equals(SkinId(Owner(character.ToString())), StringComparison.OrdinalIgnoreCase))
        {
            return Outcome.Failed("REOPEN THE MENU");
        }

        if (Wearing(slot.MeshName.ToString() ?? "", requested))
        {
            return Outcome.Unchanged("WEARING IT");
        }

        options.SetActivePlayerSkin(GameText.Once(requested));
        controller.SaveGameOptionsToMemory();

        // A character the player switched to is switched in again wearing it.
        if (Switcher.Away)
        {
            return Switcher.Again(Apply);
        }

        return Remesh.Start(Apply, character, _ => Reload(character));
    }

    // What the game's own character load does, for whichever slot the character is in. Emptying
    // the slot's data makes the loader fetch it again with the suit that is now picked.
    static void Reload(FName character)
    {
        if (!Game.TryGetGameRI(out var game))
        {
            return;
        }

        var index = Loaded.IndexOf(game, character);
        if (index < 0)
        {
            return;
        }

        ref var slot = ref Loaded.Slot(game, index);
        slot.CharacterData = null!;
        slot.MeshData = null!;
        game.ReallyLoadPlayableCharacters(false, false);
    }
}
