#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.Engine;
using FColor = BmSDK.GameObject.FColor;
using FLinearColor = BmSDK.GameObject.FLinearColor;
using PostSettings = BmSDK.Engine.PostProcessVolume.FPostProcessSettings;

namespace Knightfall;

// WORLD > ATMOSPHERE. Every row but SKY changes the post-process volumes, the boxes that set the
// look of each part of the city, plus the world's own default look. CitySky puts up the skies.
// This is the only place that writes them: it keeps each one's own values once and works every
// row out from those in one pass, so the rows can't undo each other. Each gets back exactly what
// it had.
static class Atmosphere
{
    const float Metre = Teleport.UnitsPerMetre;

    // New volumes stream in as you move, so they're looked for this often.
    const float LookEvery = 0.5f;

    delegate float Getter(ref PostSettings settings);

    delegate void Setter(ref PostSettings settings, float value);

    delegate FColor ColourGetter(ref PostSettings settings);

    delegate void ColourSetter(ref PostSettings settings, FColor colour);

    delegate FLinearColor LinearGetter(ref PostSettings settings);

    delegate void LinearSetter(ref PostSettings settings, FLinearColor colour);

    delegate Vector3 VectorGetter(ref PostSettings settings);

    delegate void VectorSetter(ref PostSettings settings, Vector3 vector);

    sealed record Field(Getter Get, Setter Set);

    // Every value this owns, each as a float. Colours and vectors take one slot per channel.
    static readonly List<Field> Fields = new();

    static readonly int Exposure_ = One((ref PostSettings s) => s.ExposureBaseOffset, (ref PostSettings s, float v) => s.ExposureBaseOffset = v);
    static readonly int Bracketing = One((ref PostSettings s) => s.ExposureAutoBracketing, (ref PostSettings s, float v) => s.ExposureAutoBracketing = v);
    static readonly int BloomOn = Flag((ref PostSettings s) => s.bEnableBloom ? 1f : 0f, (ref PostSettings s, float v) => s.bEnableBloom = v > 0.5f);
    static readonly int Bloom_ = One((ref PostSettings s) => s.BloomOverload, (ref PostSettings s, float v) => s.BloomOverload = v);
    static readonly int BloomCut = One((ref PostSettings s) => s.BloomLowerCut, (ref PostSettings s, float v) => s.BloomLowerCut = v);
    static readonly int BlurOn = Flag((ref PostSettings s) => s.bEnableMotionBlur ? 1f : 0f, (ref PostSettings s, float v) => s.bEnableMotionBlur = v > 0.5f);
    static readonly int Blur = One((ref PostSettings s) => s.MotionBlur_Amount, (ref PostSettings s, float v) => s.MotionBlur_Amount = v);
    static readonly int Grain_ = One((ref PostSettings s) => s.Scene_ImageGrainScale, (ref PostSettings s, float v) => s.Scene_ImageGrainScale = v);

    static readonly int D1On = Flag((ref PostSettings s) => s.bAtmosD1 ? 1f : 0f, (ref PostSettings s, float v) => s.bAtmosD1 = v > 0.5f);
    static readonly int D1Colour = Colour((ref PostSettings s) => s.AtmosD1_Colour, (ref PostSettings s, FColor c) => s.AtmosD1_Colour = c);
    static readonly int D1Den = One((ref PostSettings s) => s.AtmosD1_Density, (ref PostSettings s, float v) => s.AtmosD1_Density = v);
    static readonly int D1Start = One((ref PostSettings s) => s.AtmosD1_DistanceStart, (ref PostSettings s, float v) => s.AtmosD1_DistanceStart = v);
    static readonly int D1End = One((ref PostSettings s) => s.AtmosD1_DistanceEnd, (ref PostSettings s, float v) => s.AtmosD1_DistanceEnd = v);
    static readonly int D2On = Flag((ref PostSettings s) => s.bAtmosD2 ? 1f : 0f, (ref PostSettings s, float v) => s.bAtmosD2 = v > 0.5f);
    static readonly int D2Colour = Colour((ref PostSettings s) => s.AtmosD2_Colour, (ref PostSettings s, FColor c) => s.AtmosD2_Colour = c);
    static readonly int D2Den = One((ref PostSettings s) => s.AtmosD2_Density, (ref PostSettings s, float v) => s.AtmosD2_Density = v);
    static readonly int D2Start = One((ref PostSettings s) => s.AtmosD2_DistanceStart, (ref PostSettings s, float v) => s.AtmosD2_DistanceStart = v);
    static readonly int D2End = One((ref PostSettings s) => s.AtmosD2_DistanceEnd, (ref PostSettings s, float v) => s.AtmosD2_DistanceEnd = v);
    static readonly int H1On = Flag((ref PostSettings s) => s.bAtmosH1 ? 1f : 0f, (ref PostSettings s, float v) => s.bAtmosH1 = v > 0.5f);
    static readonly int H1Colour = Colour((ref PostSettings s) => s.AtmosH1_Colour, (ref PostSettings s, FColor c) => s.AtmosH1_Colour = c);
    static readonly int H1Den = One((ref PostSettings s) => s.AtmosH1_Density, (ref PostSettings s, float v) => s.AtmosH1_Density = v);
    static readonly int H1Size = One((ref PostSettings s) => s.AtmosH1_GradientSize, (ref PostSettings s, float v) => s.AtmosH1_GradientSize = v);
    static readonly int H1Pos = One((ref PostSettings s) => s.AtmosH1_GradientPosition, (ref PostSettings s, float v) => s.AtmosH1_GradientPosition = v);
    static readonly int H2On = Flag((ref PostSettings s) => s.bAtmosH2 ? 1f : 0f, (ref PostSettings s, float v) => s.bAtmosH2 = v > 0.5f);
    static readonly int H2Colour = Colour((ref PostSettings s) => s.AtmosH2_Colour, (ref PostSettings s, FColor c) => s.AtmosH2_Colour = c);
    static readonly int H2Den = One((ref PostSettings s) => s.AtmosH2_Density, (ref PostSettings s, float v) => s.AtmosH2_Density = v);
    static readonly int H2Size = One((ref PostSettings s) => s.AtmosH2_GradientSize, (ref PostSettings s, float v) => s.AtmosH2_GradientSize = v);
    static readonly int H2Pos = One((ref PostSettings s) => s.AtmosH2_GradientPosition, (ref PostSettings s, float v) => s.AtmosH2_GradientPosition = v);

    static readonly int SkyColour = Colour((ref PostSettings s) => s.AtmosGlobal_Gradient_Colour, (ref PostSettings s, FColor c) => s.AtmosGlobal_Gradient_Colour = c);
    static readonly int SkyWay = Vector((ref PostSettings s) => s.AtmosGlobal_Gradient_Direction, (ref PostSettings s, Vector3 v) => s.AtmosGlobal_Gradient_Direction = v);
    static readonly int SkyDen = One((ref PostSettings s) => s.AtmosGlobal_Gradient_Density, (ref PostSettings s, float v) => s.AtmosGlobal_Gradient_Density = v);
    static readonly int SkyCos = One((ref PostSettings s) => s.AtmosGlobal_Gradient_Cosine, (ref PostSettings s, float v) => s.AtmosGlobal_Gradient_Cosine = v);
    static readonly int Haze = One((ref PostSettings s) => s.AtmosHazeWeight, (ref PostSettings s, float v) => s.AtmosHazeWeight = v);
    static readonly int HazeNear = One((ref PostSettings s) => s.AtmosHazeNear, (ref PostSettings s, float v) => s.AtmosHazeNear = v);
    static readonly int HazeFar = One((ref PostSettings s) => s.AtmosHazeFar, (ref PostSettings s, float v) => s.AtmosHazeFar = v);
    static readonly int AmbientD1 = Linear((ref PostSettings s) => s.AtmosAmbientD1, (ref PostSettings s, FLinearColor c) => s.AtmosAmbientD1 = c);
    static readonly int AmbientD2 = Linear((ref PostSettings s) => s.AtmosAmbientD2, (ref PostSettings s, FLinearColor c) => s.AtmosAmbientD2 = c);
    static readonly int AmbientH1 = Linear((ref PostSettings s) => s.AtmosAmbientH1, (ref PostSettings s, FLinearColor c) => s.AtmosAmbientH1 = c);
    static readonly int AmbientH2 = Linear((ref PostSettings s) => s.AtmosAmbientH2, (ref PostSettings s, FLinearColor c) => s.AtmosAmbientH2 = c);
    static readonly int NoiseD1 = One((ref PostSettings s) => s.AtmosNoiseD1, (ref PostSettings s, float v) => s.AtmosNoiseD1 = v);
    static readonly int NoiseD2 = One((ref PostSettings s) => s.AtmosNoiseD2, (ref PostSettings s, float v) => s.AtmosNoiseD2 = v);
    static readonly int NoiseH1 = One((ref PostSettings s) => s.AtmosNoiseH1, (ref PostSettings s, float v) => s.AtmosNoiseH1 = v);
    static readonly int NoiseH2 = One((ref PostSettings s) => s.AtmosNoiseH2, (ref PostSettings s, float v) => s.AtmosNoiseH2 = v);
    static readonly int HeightD1 = Vector((ref PostSettings s) => s.AtmosHeightMapModD1, (ref PostSettings s, Vector3 v) => s.AtmosHeightMapModD1 = v);
    static readonly int HeightD2 = Vector((ref PostSettings s) => s.AtmosHeightMapModD2, (ref PostSettings s, Vector3 v) => s.AtmosHeightMapModD2 = v);
    static readonly int HeightH1 = Vector((ref PostSettings s) => s.AtmosHeightMapModH1, (ref PostSettings s, Vector3 v) => s.AtmosHeightMapModH1 = v);
    static readonly int HeightH2 = Vector((ref PostSettings s) => s.AtmosHeightMapModH2, (ref PostSettings s, Vector3 v) => s.AtmosHeightMapModH2 = v);

    static readonly int Desat = One((ref PostSettings s) => s.Scene_Desaturation, (ref PostSettings s, float v) => s.Scene_Desaturation = v);
    static readonly int DesatOn = Flag((ref PostSettings s) => s.bOverride_Scene_Desaturation ? 1f : 0f, (ref PostSettings s, float v) => s.bOverride_Scene_Desaturation = v > 0.5f);
    static readonly int TintColour = Vector((ref PostSettings s) => s.Scene_Colorize, (ref PostSettings s, Vector3 v) => s.Scene_Colorize = v);
    static readonly int TintOn = Flag((ref PostSettings s) => s.bOverride_Scene_Colorize ? 1f : 0f, (ref PostSettings s, float v) => s.bOverride_Scene_Colorize = v > 0.5f);
    static readonly int Highs = Linear((ref PostSettings s) => s.Scene_HighLights, (ref PostSettings s, FLinearColor c) => s.Scene_HighLights = c);
    static readonly int Mids = Linear((ref PostSettings s) => s.Scene_MidTones, (ref PostSettings s, FLinearColor c) => s.Scene_MidTones = c);
    static readonly int Lows = Linear((ref PostSettings s) => s.Scene_Shadows, (ref PostSettings s, FLinearColor c) => s.Scene_Shadows = c);
    static readonly int LutOn = Flag((ref PostSettings s) => s.bOverride_Scene_ColorGradingLUT ? 1f : 0f, (ref PostSettings s, float v) => s.bOverride_Scene_ColorGradingLUT = v > 0.5f);

    static readonly int[] FogStarts = { D1Start, D1End, D2Start, D2End };
    static readonly int[] FogDensities = { H1Den, H2Den, D1Den, D2Den };

    // The look of the Gordon section before the Scarecrow attack: the chapter's top volume,
    // CityZ_10_Ch2C PostProcessVolume_0, as the game has it.
    const string EraGrade = "Postprocess_Level.ColorGrading.CityY_Lut_02";

    static readonly (int At, float Value)[] EraLook = Join(
        Set(D1On, 1f), Rgba(D1Colour, 227, 170, 113, 255), Set(D1Den, 0f), Set(D1Start, 500f), Set(D1End, 10000f),
        Set(D2On, 1f), Rgba(D2Colour, 227, 170, 113, 255), Set(D2Den, 0f), Set(D2Start, 500f), Set(D2End, 10000f),
        Set(H1On, 1f), Rgba(H1Colour, 128, 128, 128, 80), Set(H1Den, 0.3f), Set(H1Size, 35000f), Set(H1Pos, 15000f),
        Set(H2On, 1f), Rgba(H2Colour, 197, 186, 178, 2), Set(H2Den, 0.05f), Set(H2Size, 8000f), Set(H2Pos, 50000f),
        Rgba(SkyColour, 240, 150, 20, 255), Xyz(SkyWay, -20f, 80f, 0f), Set(SkyDen, 1.5f), Set(SkyCos, 0f),
        Set(Haze, 0.05f), Set(HazeNear, 0f), Set(HazeFar, 800f),
        Rgba(AmbientD1, 0f, 0f, 0f, 1f), Rgba(AmbientD2, 0f, 0f, 0f, 1f), Rgba(AmbientH1, 0.1f, 0.03f, 0f, 1f), Rgba(AmbientH2, 0f, 0f, 0f, 1f),
        Set(NoiseD1, 0f), Set(NoiseD2, 0f), Set(NoiseH1, 0f), Set(NoiseH2, 0f),
        Xyz(HeightD1, 0f, 0f, 1f), Xyz(HeightD2, 0f, 0f, 1f), Xyz(HeightH1, 0f, 0f, 1f), Xyz(HeightH2, 0f, 0f, 1f),
        Set(Bracketing, 4f), Set(Exposure_, 1f), Set(Bloom_, 0.02f), Set(BloomCut, 0.01f),
        Set(Desat, 0f), Set(DesatOn, 1f), Xyz(TintColour, 1.2f, 1.2f, 1f), Set(TintOn, 1f),
        Rgba(Highs, 1.2f, 1.2f, 1f, 1f), Rgba(Mids, 0.42f, 0.4f, 0.4f, 1f), Rgba(Lows, 0f, 0f, 0f, 1f), Set(LutOn, 1f));

    // Per volume, and one for the world's default: what the game had, and what was last written.
    // The two lists are written over in place, so a pass makes nothing new for a volume it knows.
    sealed class Entry
    {
        public Entry(GameObject owner, float[] now, Texture? lut)
        {
            Owner = owner;
            Was = (float[])now.Clone();
            Wrote = (float[])now.Clone();
            WasLut = lut;
            WroteLut = lut;
        }

        public GameObject Owner { get; }

        public float[] Was { get; }

        public float[] Wrote { get; }

        public Texture? WasLut { get; set; }

        public Texture? WroteLut { get; set; }
    }

    // The game's own colour grades, all cooked into the always loaded BmGame package: its photo
    // mode filters, and two grades from story areas.
    static readonly (string Key, string Label, string Path)[] Grades =
    {
        ("ak", "ARKHAM KNIGHT", "DLC_VFX_PP.CLUT.LUT_AK"),
        ("bleach_bypass", "BLEACH BYPASS", "DLC_VFX_PP.CLUT.LUT_BleachBypass"),
        ("demon", "DEMON", "DLC_VFX_PP.CLUT.LUT_Demon"),
        ("film_colour", "FILM COLOUR", "DLC_VFX_PP.CLUT.LUT_FilmColour"),
        ("laughing_matter", "LAUGHING MATTER", "DLC_VFX_PP.CLUT.LUT_LaughingMatter"),
        ("poison", "POISON", "DLC_VFX_PP.CLUT.LUT_Poison"),
        ("pro_bw", "PRO B&W", "DLC_VFX_PP.CLUT.LUT_ProBW"),
        ("vintage", "VINTAGE", "DLC_VFX_PP.CLUT.LUT_Vintage"),
        ("ace_chemicals", "ACE CHEMICALS", "Postprocess_Level.ColorGrading.Ace_Chemicals_A1_Lut"),
        ("stagg", "STAGG", "Postprocess_Level.ColorGrading.Stagg_Lut"),
    };

    // GAME and NONE come before the grades in the list.
    const int FirstGrade = 2;

    static readonly Texture?[] GradeTextures = new Texture?[Grades.Length];
    static Texture? _eraGrade;

    // The two grades a pass may hand out, looked up once at its start.
    static Texture? _era;
    static Texture? _picked;

    static readonly Dictionary<nint, Entry> Held = new();
    static float _lookedAt = -1f;
    static int? _applied;

    // One volume's values while a pass works on it: as read, with the era under them, and as wanted.
    static float[] _now = Array.Empty<float>();
    static float[] _from = Array.Empty<float>();
    static float[] _want = Array.Empty<float>();

    public static readonly Toggle Era = new(
        "world.atmosphere.pre_scarecrow_era", "PRE-SCARECROW ERA",
        "The city before the Scarecrow attack, from the Gordon section. Its colour grading, dusk sky and sun, and no lightning. Other rows here still work on top.")
    {
        Changed = static () =>
        {
            if (Era!.On)
            {
                CitySky.Sky.SetTo(0);
            }
        },
    };

    public static readonly Choice Grade = new(
        "world.atmosphere.colour_grade", "COLOUR GRADE",
        "Puts one of the game's colour grades over the city. Its photo mode filters, or the grade from Ace Chemicals or Stagg's airship. NONE takes grading off.",
        new[] { ("game", "GAME"), ("none", "NONE") }.Concat(Grades.Select(grade => (grade.Key, grade.Label))).ToArray());

    public static readonly Slider Tint = new(
        "world.atmosphere.tint", "TINT",
        "Tints the whole picture this colour, round the colour wheel from red.",
        min: 0, max: 350, step: 10, value: 0, zero: "OFF");

    public static readonly Slider TintStrength = new(
        "world.atmosphere.tint_strength", "TINT STRENGTH",
        "How strongly TINT colours the picture.",
        min: 5, max: 100, step: 5, value: 50, unit: "%")
    {
        Block = static () => Tint.Value > 0 ? null : "TINT IS OFF",
    };

    public static readonly Slider Saturation = new(
        "world.atmosphere.saturation", "SATURATION",
        "How colourful the picture is. 0X is black and white, and over 1X makes colours stronger.",
        min: 0, max: 2, step: 0.1, value: 1, unit: "X");

    public static readonly Slider Exposure = new(
        "world.atmosphere.exposure", "EXPOSURE",
        "Brightens or darkens the whole picture, in camera stops.",
        min: -3, max: 3, step: 0.25, value: 0, unit: " STOPS", zero: "GAME");

    public static readonly Slider Bloom = new(
        "world.atmosphere.bloom", "BLOOM",
        "The glow around neon signs, lamps and bright lights. OFF removes it.",
        min: 0, max: 3, step: 0.25, value: 1, unit: "X", zero: "OFF");

    public static readonly Slider FogThickness = new(
        "world.atmosphere.fog_thickness", "FOG THICKNESS",
        "Thins out or thickens the city's fog and haze.",
        min: 0, max: 3, step: 0.1, value: 1, unit: "X", zero: "NONE");

    public static readonly Slider FogDistance = new(
        "world.atmosphere.fog_distance", "FOG DISTANCE",
        "Moves where the fog starts and how far off it thickens. Higher keeps the streets around you clear and pushes the fog out to the skyline.",
        min: 0.25, max: 4, step: 0.25, value: 1, unit: "X");

    public static readonly Slider FogHeight = new(
        "world.atmosphere.fog_height", "FOG HEIGHT",
        "Raises or lowers the layer of low fog that hangs over the streets.",
        min: -100, max: 100, step: 5, value: 0, unit: " M", zero: "GAME");

    public static readonly Slider MotionBlur = new(
        "world.atmosphere.motion_blur", "MOTION BLUR",
        "The smear when the camera or you move fast. OFF gives a sharp picture.",
        min: 0, max: 3, step: 0.25, value: 1, unit: "X", zero: "OFF");

    public static readonly Slider Grain = new(
        "world.atmosphere.film_grain", "FILM GRAIN",
        "Adds film grain to the picture. GAME leaves it as the game has it.",
        min: 0, max: 100, step: 5, value: 0, unit: "%", zero: "GAME");

    // TINT STRENGTH does nothing by itself, so it only counts towards a change.
    static readonly Feature[] All = { Era, Grade, Tint, Saturation, Exposure, Bloom, FogThickness, FogDistance, FogHeight, MotionBlur, Grain };
    static readonly Feature[] Stamped = All.Append(TintStrength).ToArray();

    public static readonly Command Reset = new(
        "world.atmosphere.reset", "RESET ATMOSPHERE",
        "Puts every row on this tab back to the game's look.",
        static () => Weather.Back(Stamped.Append(CitySky.Sky).ToArray()));

    public static readonly Catalog.Section[] Sections =
    {
        new("SCENE", Era, CitySky.Sky, Reset),
        new("GRADE", Grade, Tint, TintStrength, Saturation),
        new("LIGHT", Exposure, Bloom),
        new("FOG", FogThickness, FogDistance, FogHeight),
        new("LENS", MotionBlur, Grain),
    };

    // Runs every tick. Writes straight away when a row changes, and every half second for new
    // volumes and for values the game wrote itself.
    public static void Tick()
    {
        var any = false;
        foreach (var row in All)
        {
            any |= !row.IsDefault;
        }

        if (!any)
        {
            if (Held.Count > 0)
            {
                Release();
            }

            return;
        }

        var wanted = Feature.Stamp(Stamped);
        if (wanted == _applied && MathF.Abs(World.Now - _lookedAt) < LookEvery)
        {
            return;
        }

        _lookedAt = World.Now;
        _applied = wanted;
        if (_now.Length != Fields.Count)
        {
            _now = new float[Fields.Count];
            _from = new float[Fields.Count];
            _want = new float[Fields.Count];
        }

        // The era's grade comes with the package its sky loads, so it can turn up a pass late.
        _era = EraTexture();
        _picked = PickedGrade();
        foreach (var volume in Game.FindObjects<PostProcessVolume>())
        {
            if (volume.IsValid && !volume.IsClassDefaultObject)
            {
                Apply(volume, ref volume.Settings);
            }
        }

        if (Game.TryGetWorldInfo(out var world) && world.IsValid)
        {
            Apply(world, ref world.DefaultPostProcessSettings);
        }
    }

    public static void Release()
    {
        foreach (var entry in Held.Values)
        {
            if (!entry.Owner.IsValid)
            {
                continue;
            }

            if (entry.Owner is PostProcessVolume volume)
            {
                Write(ref volume.Settings, entry.Was, entry.WasLut);
            }
            else if (entry.Owner is WorldInfo world)
            {
                Write(ref world.DefaultPostProcessSettings, entry.Was, entry.WasLut);
            }
        }

        Held.Clear();
        _applied = null;
        _eraGrade = null;
        _era = null;
        _picked = null;
    }

    static void Apply(GameObject owner, ref PostSettings settings)
    {
        Read(ref settings, _now);
        var nowLut = Usable(settings.ColorGrading_LookupTable);
        if (!Held.TryGetValue(owner.Ptr, out var entry) || !ReferenceEquals(entry.Owner, owner))
        {
            entry = new Entry(owner, _now, nowLut);
            Held[owner.Ptr] = entry;
        }
        else if (!Same(_now, entry.Wrote) || !ReferenceEquals(nowLut, entry.WroteLut))
        {
            // The game set its own look on this one, so that's the new one to go back to.
            _now.CopyTo(entry.Was, 0);
            entry.WasLut = nowLut;
        }

        Compose(entry.Was, _from, _want);
        var wantLut = GradeFor(entry.WasLut, _want);
        if (!Same(_now, _want) || !ReferenceEquals(nowLut, wantLut))
        {
            Write(ref settings, _want, wantLut);
        }

        _want.CopyTo(entry.Wrote, 0);
        entry.WroteLut = wantLut;
    }

    // The era's look replaces the volume's own as the base, and every other row works on top.
    static void Compose(float[] was, float[] from, float[] to)
    {
        was.CopyTo(from, 0);
        if (Era.On)
        {
            foreach (var (at, value) in EraLook)
            {
                from[at] = value;
            }
        }

        from.CopyTo(to, 0);

        to[Exposure_] = from[Exposure_] + (float)Exposure.Value;

        var bloom = (float)Bloom.Value;
        to[BloomOn] = bloom <= 0f ? 0f : from[BloomOn];
        to[Bloom_] = from[Bloom_] * bloom;

        var thickness = (float)FogThickness.Value;
        foreach (var at in FogDensities)
        {
            to[at] = from[at] * thickness;
        }

        to[Haze] = Math.Clamp(from[Haze] * thickness, 0f, 1f);

        foreach (var at in FogStarts)
        {
            to[at] = from[at] * (float)FogDistance.Value;
        }

        var lift = (float)FogHeight.Value * Metre;
        to[H1Pos] = from[H1Pos] + lift;
        to[H2Pos] = from[H2Pos] + lift;

        var blur = (float)MotionBlur.Value;
        if (blur <= 0f)
        {
            to[BlurOn] = 0f;
        }
        else if (!MotionBlur.IsDefault)
        {
            to[BlurOn] = 1f;
            to[Blur] = (from[Blur] > 0.001f ? from[Blur] : 1f) * blur;
        }

        if (Grain.Value > 0)
        {
            to[Grain_] = (float)Grain.Value / 100f;
        }

        // A volume with its switch off has no saturation or tint of its own, so plain ones go
        // under ours.
        if (!Saturation.IsDefault)
        {
            var desat = from[DesatOn] > 0.5f ? from[Desat] : 0f;
            to[Desat] = 1f - (1f - desat) * (float)Saturation.Value;
            to[DesatOn] = 1f;
        }

        if (Tint.Value > 0)
        {
            var plain = from[TintOn] < 0.5f;
            var (r, g, b) = Hue((float)Tint.Value);
            var k = (float)TintStrength.Value / 100f;
            to[TintColour] = (plain ? 1f : from[TintColour]) * (1f + (r - 1f) * k);
            to[TintColour + 1] = (plain ? 1f : from[TintColour + 1]) * (1f + (g - 1f) * k);
            to[TintColour + 2] = (plain ? 1f : from[TintColour + 2]) * (1f + (b - 1f) * k);
            to[TintOn] = 1f;
        }
    }

    // The grade texture to use. GAME keeps the volume's own, or the era's during the era, and
    // anything else switches the volume's grade on.
    static Texture? GradeFor(Texture? was, float[] want)
    {
        var pick = Grade.Index;
        if (pick == 0)
        {
            return _era ?? was;
        }

        want[LutOn] = 1f;
        return pick < FirstGrade ? null : _picked ?? was;
    }

    // The texture of the grade COLOUR GRADE is on, or null for GAME, NONE and one that isn't
    // loaded.
    static Texture? PickedGrade()
    {
        var index = Grade.Index - FirstGrade;
        if (index < 0)
        {
            return null;
        }

        if (GradeTextures[index] is not { IsValid: true })
        {
            GradeTextures[index] = Game.FindObject<Texture>(Grades[index].Path);
            if (GradeTextures[index] is null)
            {
                Log.Warn("atmosphere.grade." + Grades[index].Key, "The " + Grades[index].Label + " grade isn't loaded at " + Grades[index].Path + ".");
            }
        }

        return GradeTextures[index] is { IsValid: true } grade ? grade : null;
    }

    // The era's grade comes with the chapter package its sky loads.
    static Texture? EraTexture()
    {
        if (!Era.On)
        {
            return null;
        }

        if (_eraGrade is not { IsValid: true })
        {
            _eraGrade = Game.FindObject<Texture>(EraGrade);
        }

        return _eraGrade is { IsValid: true } grade ? grade : null;
    }

    static Texture? Usable(Texture? texture) => texture is { IsValid: true } ? texture : null;

    static void Read(ref PostSettings settings, float[] at)
    {
        for (var i = 0; i < at.Length; i++)
        {
            at[i] = Fields[i].Get(ref settings);
        }
    }

    static void Write(ref PostSettings settings, float[] at, Texture? lut)
    {
        for (var i = 0; i < at.Length; i++)
        {
            Fields[i].Set(ref settings, at[i]);
        }

        settings.ColorGrading_LookupTable = lut!;
    }

    static int One(Getter get, Setter set)
    {
        Fields.Add(new Field(get, set));
        return Fields.Count - 1;
    }

    static int Flag(Getter get, Setter set) => One(get, set);

    static int Colour(ColourGetter get, ColourSetter set)
    {
        var first = One((ref PostSettings s) => get(ref s).R, (ref PostSettings s, float v) => { var c = get(ref s); c.R = Byte(v); set(ref s, c); });
        One((ref PostSettings s) => get(ref s).G, (ref PostSettings s, float v) => { var c = get(ref s); c.G = Byte(v); set(ref s, c); });
        One((ref PostSettings s) => get(ref s).B, (ref PostSettings s, float v) => { var c = get(ref s); c.B = Byte(v); set(ref s, c); });
        One((ref PostSettings s) => get(ref s).A, (ref PostSettings s, float v) => { var c = get(ref s); c.A = Byte(v); set(ref s, c); });
        return first;
    }

    static int Linear(LinearGetter get, LinearSetter set)
    {
        var first = One((ref PostSettings s) => get(ref s).R, (ref PostSettings s, float v) => { var c = get(ref s); c.R = v; set(ref s, c); });
        One((ref PostSettings s) => get(ref s).G, (ref PostSettings s, float v) => { var c = get(ref s); c.G = v; set(ref s, c); });
        One((ref PostSettings s) => get(ref s).B, (ref PostSettings s, float v) => { var c = get(ref s); c.B = v; set(ref s, c); });
        One((ref PostSettings s) => get(ref s).A, (ref PostSettings s, float v) => { var c = get(ref s); c.A = v; set(ref s, c); });
        return first;
    }

    static int Vector(VectorGetter get, VectorSetter set)
    {
        var first = One((ref PostSettings s) => get(ref s).X, (ref PostSettings s, float v) => set(ref s, get(ref s) with { X = v }));
        One((ref PostSettings s) => get(ref s).Y, (ref PostSettings s, float v) => set(ref s, get(ref s) with { Y = v }));
        One((ref PostSettings s) => get(ref s).Z, (ref PostSettings s, float v) => set(ref s, get(ref s) with { Z = v }));
        return first;
    }

    static byte Byte(float value) => (byte)Math.Clamp(MathF.Round(value), 0f, 255f);

    static (int, float)[] Set(int at, float value) => new[] { (at, value) };

    static (int, float)[] Rgba(int at, float r, float g, float b, float a) => new[] { (at, r), (at + 1, g), (at + 2, b), (at + 3, a) };

    static (int, float)[] Xyz(int at, float x, float y, float z) => new[] { (at, x), (at + 1, y), (at + 2, z) };

    static (int, float)[] Join(params (int, float)[][] parts) => parts.SelectMany(part => part).ToArray();

    // Full colour round the wheel, brightest channel at 1.
    static (float R, float G, float B) Hue(float degrees)
    {
        var h = degrees % 360f / 60f;
        var x = 1f - MathF.Abs(h % 2f - 1f);
        return (int)h switch
        {
            0 => (1f, x, 0f),
            1 => (x, 1f, 0f),
            2 => (0f, 1f, x),
            3 => (0f, x, 1f),
            4 => (x, 0f, 1f),
            _ => (1f, 0f, x),
        };
    }

    static bool Same(float[] a, float[] b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            if (MathF.Abs(a[i] - b[i]) > 0.0001f)
            {
                return false;
            }
        }

        return true;
    }
}
