#nullable enable

namespace Knightfall;

// The Scarecrow's lines are all broadcasts, made for the screens he takes over across the city.
// Most are to Batman or about the people around him, so only the lines that fit any street are
// used. His BROADCAST speaks one with its video.
static partial class Scarecrow
{
    static string Line(string scene, string name) => $"scenes_tv_scarecrow_city_{scene}.Lines.Scenes_Tv_Scarecrow_City_{name}";

    static readonly VillainVoice Speech = new(
        "scarecrow voice",
        new[]
        {
            "BARKSET_SCENES-Scarecrow-AlwaysLoaded-any-CH1A_SF",
            "BARKSET_SCENES-Scarecrow-AlwaysLoaded-any-CH3A_SF",
            "BARKSET_SCENES-Scarecrow-AlwaysLoaded-any-CH5A_SF",
            "BARKSET_SCENES-Scarecrow-AlwaysLoaded-any-CH7A_SF",
            "BARKSET_SCENES-Scarecrow-AlwaysLoaded-any-CH7B_SF",
            "BARKSET_SCENES-Scarecrow-AlwaysLoaded-any-CH7D_SF",
            "BARKSET_SCENES-Scarecrow-AlwaysLoaded-any-CH9B_SF",
        },
        new[]
        {
            "SFX_DLG_PA",
            "DLG_SCENES_TV_SCARECROW_CITY_CH01_B",
            "DLG_SCENES_TV_SCARECROW_CITY_CH01_H",
            "DLG_SCENES_TV_SCARECROW_CITY_CH03_A",
            "DLG_SCENES_TV_SCARECROW_CITY_CH05_D",
            "DLG_SCENES_TV_SCARECROW_CITY_CH07_PRE_GAS_F",
            "DLG_SCENES_TV_SCARECROW_CITY_CH07_PRE_GAS_I",
            "DLG_SCENES_TV_SCARECROW_CITY_CH07_GAS_B",
            "DLG_SCENES_TV_SCARECROW_CITY_CH07_POST_GAS_J",
            "DLG_SCENES_TV_SCARECROW_CITY_CH09_J",
        },
        ("toxin", Line("ch07_gas_b", "Ch07_Gas_B_01")),
        ("toxin", Line("ch07_pre_gas_i", "Ch07_Pre_Gas_I_01")),
        ("air army", Line("ch03_a", "Ch03_Army_01")),
        ("air city", Line("ch01_h", "Ch01_H_01")),
        ("air night", Line("ch01_b", "Ch01_B_01")),
        ("air gotham", Line("ch05_d", "Ch05_D_01")),
        ("air witness", Line("ch07_pre_gas_f", "Ch07_Pre_Gas_F_01")),
        ("air gift", Line("ch07_post_gas_j", "Ch07_Post_Gas_J_01")),
        ("air face", Line("ch09_j", "Ch09_J_01")));

    // He taunts this often in a fight when nothing else has been said, with one of the lines he
    // broadcasts that isn't said to criminals. A line belongs to one set only. His lines are long.
    const float TauntSeconds = 30f;
    static readonly string[] Taunts = { "air night", "air gotham", "air witness", "air gift", "air face" };
}
