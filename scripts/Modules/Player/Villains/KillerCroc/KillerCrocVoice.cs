#nullable enable

namespace Knightfall;

// Croc's lines from his fight in Blackgate. Most of what he says there is to Batman or about the
// Warden, so only the lines that fit any fight are used.
static partial class KillerCroc
{
    const string FightLines = "dlc_rs_main_croc_s06_croc_finale_boss_fight_";

    static string FightLine(string group, string number) =>
        $"{FightLines}{group}.Lines.Dlc_Rs_Main_Croc_S06_Croc_Finale_Boss_Fight_{group}_{number}";

    static readonly VillainVoice Speech = new(
        "killer croc voice",
        new[] { "BARKSET_DLC_RS_MAIN_CROC-Killer_Croc-AlwaysLoaded-any-ALL_SF" },
        new[] { "DLG_DLC_RS_MAIN_CROC_S06_CROC_FINALE_BOSS_FIGHT_" },
        ("hit", FightLine("Croc_Hits_Bm", "02")),
        ("hit", FightLine("Croc_Hits_Bm", "03")),
        ("hit", FightLine("Croc_Hits_Nw", "02")),
        ("hit", FightLine("Croc_Hits_Nw", "03")),
        ("hit", FightLine("Croc_Hits_Nw", "05")),
        ("hurt", FightLine("Bm_Hit_Croc", "01")),
        ("hurt", FightLine("Bm_Hit_Croc", "03")),
        ("hurt", FightLine("Bm_Hit_Croc", "04")),
        ("hurt", FightLine("Bm_Hit_Croc", "05")),
        ("hurt", FightLine("Strike_Success_Cocky", "01")),
        ("hurt", FightLine("Strike_Success_Cocky", "02")),
        ("charge", FightLine("Bm_Taunts_A", "04")),
        ("charge", FightLine("Bm_Taunts_B", "04")),
        ("charge", FightLine("Bm_Taunts_B", "05")),
        ("charge", FightLine("Strike_Fail", "03")),
        ("revive", FightLine("Revive", "01")),
        ("revive", FightLine("Revive", "02")),
        ("revive", FightLine("Revive", "03")),
        ("revive", FightLine("Revive", "04")),
        ("taunt", FightLine("Strike_Fail", "02")),
        ("taunt", FightLine("Strike_Fail", "04")),
        ("taunt", FightLine("Strike_Fail", "05")),
        ("taunt", FightLine("Nw_Taunts_A", "05")),
        ("taunt", FightLine("Nw_Taunts_B", "05")));

    // He taunts this often in a fight when nothing else has been said.
    const float TauntSeconds = 20f;
}
