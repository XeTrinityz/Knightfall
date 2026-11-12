#nullable enable

namespace Knightfall;

// Mr. Freeze's voice. He has no taunts of his own, since in his story he fights beside Batman to
// save Nora, so his angriest lines from it are used in a fight. His suit's computer warns when
// coolant runs low. The lines are spread over his reveal, his tapes, his rooftop call and his
// tank fight, so each of those is loaded while he is on. What he says is typed into the log on
// his HUD.
static partial class MrFreeze
{
    const string Reveal = "Dlc_Rs_Scenes_Cine_Freeze_Freeze_Reveal.Lines.Dlc_Rs_Scenes_Cine_Freeze_Freeze_Reveal_";
    const string Tapes = "Dlc_Rs_Tapes_Freeze.Lines.Dlc_Rs_Tapes_Freeze_";

    // Each line's set, the start of its path, and what it says.
    static readonly (string Key, string Prefix, string Text)[] Lines =
    {
        ("beam", Reveal + "08", "THEY TOOK NORA. BECAUSE OF YOU!"),
        ("beam", "Dlc_Rs_Main_Freeze_S03_Nora_Roof_Post_Roof.Lines.Dlc_Rs_Main_Freeze_S03_Nora_Roof_Post_Roof_04", "NO! I WON'T FAIL HER."),
        ("frozen", Tapes + "03", "PROTOCOL MUST BE FOLLOWED."),
        ("frozen", Tapes + "13", "TO HELL WITH PROTOCOL."),
        ("quiet", "Dlc_Rs_Main_Freeze_S04_Battle_Detonation.Lines.Dlc_Rs_Main_Freeze_S04_Battle_Detonation_01", "I LOVE YOU, NORA."),
        ("quiet", Reveal + "05a", "NORA...!"),
        ("warning", Reveal + "02", "WARNING. CRYOGENERATOR UNSTABLE."),
        ("warning", Reveal + "04", "ERROR. SYSTEM FAILURE. CRYOGENERATOR UNSTABLE."),
        ("warning", Reveal + "11", "WARNING. CRYOGENERATOR UNSTABLE."),
        ("warning", Reveal + "13", "WARNING. CRYOGENERATOR UNSTABLE."),
    };

    // Each line cut into its typed lengths once, so the log only ever draws a fixed set of texts.
    static readonly string[][] Spoken = Lines.Select(line => Typed(line.Text)).ToArray();
    static readonly string[] Venting = Typed("COOLANT EXHAUSTED. VENTING.");
    static readonly string[] Restored = Typed("COOLANT RESTORED. BEAM ONLINE.");

    static string[] Typed(string line) => Enumerable.Range(0, line.Length + 1).Select(n => line[..n]).ToArray();

    static readonly VillainVoice Voice = new(
        "mr freeze voice",
        new[] { MissionPackage, "VPFreeze_01_Cine_Freeze_Reveal", "CityX__Ch_F23_Static_LOD2", "CityY_17_Ch_F12345_Static_LOD1" },
        new[]
        {
            "DLG_DLC_RS_SCENES_CINE_FREEZE_FREEZE_REVEAL", "DLG_DLC_RS_MAIN_FREEZE_S03_NORA_ROOF_POST_ROOF", "DLG_DLC_RS_TAPES_FREEZE",
            "DLG_DLC_RS_MAIN_FREEZE_S04_BATTLE_DETONATION",
        },
        Lines.Select(line => (line.Key, line.Prefix)).ToArray());

    // He talks to Nora this often when nothing else has been said. A beam can start every few
    // seconds, so a line as it starts waits this long after the last one.
    const float QuietEvery = 60f;
    const float BeamLineEvery = 25f;
    static float _nextBeamLine;

    // A line from the set, typed into the log when it plays.
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
        for (var i = 0; i < Lines.Length; i++)
        {
            if (said.StartsWith(Lines[i].Prefix, StringComparison.OrdinalIgnoreCase))
            {
                Record(Spoken[i]);
                return;
            }
        }
    }
}
