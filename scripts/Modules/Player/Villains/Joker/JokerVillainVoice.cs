#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// The Joker's voice. His laughs and fight lines are from the night he fights Batman in Crime
// Alley, and his gas lines from when Batman breathes in the toxin. Nearly all of those lines are
// said to Batman by name, so only the few that fit anyone he fights are used. He speaks with his
// own voice the whole time, so the game's own grunt when he is hit is his.
static partial class JokerVillain
{
    const string CrimeAlley = "main_ch10_s03_joker_c1_crime_alley_";
    const string FightLines = CrimeAlley + "combat_lines.Lines.Main_Ch10_S03_Joker_C1_Crime_Alley_Combat_Lines_";
    const string GasLines = "Main_Ch07_Jk_Gas_Grapple.Lines.Main_Ch07_Jk_Gas_Grapple_";

    static readonly VillainVoice Voice = new(
        "joker voice",
        new[] { "BARKSET_MAIN_CH10_S03_JOKER_C1_CRIME_ALLEY-Joker-AlwaysLoaded-any-CH9B_SF", "Main_Ch07_Jk_Gas_Grapple_SF" },
        new[] { "DLG_MAIN_CH10_S03_JOKER_C1_CRIME_ALLEY_", "DLG_MAIN_CH07_JK_GAS_GRAPPLE" },
        ("laugh", CrimeAlley + "laughs.Lines."),
        ("taunt", FightLines + "02"),
        ("taunt", FightLines + "07"),
        ("taunt", FightLines + "13"),
        ("gas", GasLines + "01"),
        ("gas", GasLines + "02"));

    // What each line says, for the HUD's log, found by the start of the line's path.
    static readonly (string Prefix, string Text)[] Said =
    {
        (CrimeAlley + "laughs.", "HA HA HA HA HA!"),
        (FightLines + "02", "JUST IMAGINE THE FUN WE'LL HAVE WHEN I'M IN CHARGE!"),
        (FightLines + "07", "JUST LET GO. ALL THIS FIGHTING, IT'S SO UNNECESSARY!"),
        (FightLines + "13", "ADMIT IT, GOTHAM'S NOT BEEN THE SAME SINCE I'VE BEEN GONE!"),
        (GasLines + "01", "A COUPLE MORE SNIFFS OF THAT GAS AND YOU'RE ALL MINE!"),
        (GasLines + "02", "THIS STUFF'S INTOXICATING! MORE! MORE!"),
    };

    // Each line cut into its typed lengths once, so the log only ever draws a fixed set of texts.
    static readonly string[][] Typing = Said.Select(line => Enumerable.Range(0, line.Text.Length + 1).Select(n => line.Text[..n]).ToArray()).ToArray();

    // Thugs he has put down while he has been on, each counted once.
    static readonly HashSet<nint> Downed = new();

    // A fight line this often while he is fighting, if nothing else has been said.
    const float TauntEvery = 30f;

    // Thugs this near that go down are taken as his doing, and are looked at this often.
    const float KnockoutReach = 1500f;
    const float KnockoutsEvery = 0.3f;

    static readonly Dictionary<nint, bool> Standing = new();
    static readonly HashSet<nint> Seen = new();
    static float _nextKnockouts;

    static void Talk(RPawnPlayer player, RPlayerController controller)
    {
        if (controller.IsInCombat(false) && Voice.Quiet("taunt", TauntEvery))
        {
            Logged();
        }

        if (World.Now >= _nextKnockouts)
        {
            _nextKnockouts = World.Now + KnockoutsEvery;
            if (KnockedOut(player))
            {
                Say("laugh");
            }
        }
    }

    // A line from the set, typed into the HUD's log when it plays.
    static void Say(string key, bool always = false)
    {
        if (Voice.Speak(key, always))
        {
            Logged();
        }
    }

    static void Logged()
    {
        var said = Voice.LastLine;
        for (var i = 0; i < Said.Length; i++)
        {
            if (said.StartsWith(Said[i].Prefix, StringComparison.OrdinalIgnoreCase))
            {
                Record(Typing[i]);
                return;
            }
        }
    }

    // True when a thug near him who was standing last time is down now.
    static bool KnockedOut(RPawnPlayer player)
    {
        var any = false;
        Seen.Clear();
        foreach (var thug in Game.FindObjects<RPawnVillain>())
        {
            if (!thug.IsValid || thug.IsClassDefaultObject || !Crowd.IsEnemy(thug) || Vector3.DistanceSquared(thug.Location, player.Location) > KnockoutReach * KnockoutReach)
            {
                continue;
            }

            var up = Crowd.Standing(thug) && !thug.IsIncapacitated();
            if (Standing.TryGetValue(thug.Ptr, out var was) && was && !up)
            {
                any = true;
                Downed.Add(thug.Ptr);
            }

            Standing[thug.Ptr] = up;
            Seen.Add(thug.Ptr);
        }

        foreach (var gone in Standing.Keys.Where(key => !Seen.Contains(key)).ToList())
        {
            Standing.Remove(gone);
        }

        return any;
    }

    static void ResetVoice()
    {
        Standing.Clear();
        Downed.Clear();
        _nextKnockouts = 0f;
    }
}
