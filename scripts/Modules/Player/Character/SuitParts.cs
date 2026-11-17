#nullable enable

using System.Globalization;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// How one part of a suit is painted. Brightness 1 with the suit's own colour is no paint at all.
readonly record struct SuitLook(int Colour, double Strength, double Brightness)
{
    public static readonly SuitLook Plain = new(0, 100, 1);

    public static readonly (string Id, string Label, byte R, byte G, byte B)[] Palette =
    {
        ("own", "SUIT'S OWN", 255, 255, 255),
        ("white", "WHITE", 235, 235, 235),
        ("silver", "SILVER", 170, 175, 185),
        ("grey", "GREY", 100, 100, 105),
        ("black", "BLACK", 12, 12, 14),
        ("red", "RED", 200, 20, 20),
        ("crimson", "CRIMSON", 120, 8, 20),
        ("orange", "ORANGE", 240, 110, 10),
        ("gold", "GOLD", 212, 160, 40),
        ("yellow", "YELLOW", 245, 220, 30),
        ("lime", "LIME", 140, 230, 30),
        ("green", "GREEN", 20, 150, 50),
        ("teal", "TEAL", 0, 140, 140),
        ("cyan", "CYAN", 30, 210, 240),
        ("blue", "BLUE", 30, 80, 220),
        ("navy", "NAVY", 15, 25, 90),
        ("purple", "PURPLE", 110, 40, 180),
        ("magenta", "MAGENTA", 220, 30, 200),
        ("pink", "PINK", 250, 120, 170),
        ("brown", "BROWN", 100, 60, 30),
    };

    public bool IsPlain => Colour == 0 && Brightness == 1;

    // Written like "red,80,1.5".
    public override string ToString() => string.Join(',',
        Palette[Math.Clamp(Colour, 0, Palette.Length - 1)].Id,
        Strength.ToString("0.##", CultureInfo.InvariantCulture),
        Brightness.ToString("0.##", CultureInfo.InvariantCulture));

    public static bool TryParse(string text, out SuitLook look)
    {
        look = Plain;
        var values = text.Split(',');
        if (values.Length != 3)
        {
            return false;
        }

        var colour = Array.FindIndex(Palette, swatch => swatch.Id == values[0].Trim());
        if (colour < 0
            || !double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var strength)
            || !double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var bright)
            || !double.IsFinite(strength) || !double.IsFinite(bright))
        {
            return false;
        }

        look = new SuitLook(colour, Math.Clamp(strength, 0, 100), Math.Clamp(bright, 0.2, 4));
        return true;
    }
}

// One place a part's material is worn: an element of one mesh.
sealed class SuitSlot(MeshComponent mesh, int element, MaterialInterface was, MaterialInterface? kept)
{
    public readonly MeshComponent Mesh = mesh;
    public readonly int Element = element;

    // What the mesh shows there, which can be its model's own material.
    public readonly MaterialInterface Was = was;

    // What the mesh itself holds for the slot, or none, to put back exactly.
    public readonly MaterialInterface? Kept = kept;
    public MaterialInstanceConstant? Ours;
}

// Every place the suit wears one material from its package, with what can change on it and
// what the mod has put on it.
sealed class SuitPart(string key, string label)
{
    public readonly string Key = key;
    public readonly string Label = label;
    public readonly List<SuitSlot> Slots = new();
    public readonly List<FName> Colours = new();
    public FName Map;
    public FName Bright;
    public float BrightBase = 1f;
    public bool HasBright;
    public Texture2D? Picture;
    public ScriptedTexture? Copy;
    public GameObject.FLinearColor CopyTint;
    public ScriptedTexture? Next;
    public GameObject.FLinearColor NextTint;
    public bool NextDrawn;
    public GameObject.FLinearColor WantTint;
    public long WantSince;
    public bool CopyPending;
    public long NextAsked;
    public SuitLook Worn = SuitLook.Plain;
    public bool Lit;
    public bool Waiting;
}

// Reads the parts off the suit the player wears. Every suit, modded ones too, names its
// materials and their settings its own way, so nothing here knows a suit by name.
static class SuitParts
{
    // Words in material names that say nothing about which part it is.
    static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "mic", "mat", "mats", "inst", "mi", "master", "material", "materials", "mtl", "playable", "skin",
    };

    // Set on every material the mod puts on the suit, so one is known again after a reload has
    // made the mod forget it, and never taken for the suit's own.
    public static readonly FName Marker = new("KnightfallPaint");

    static bool IsOurs(MaterialInterface material)
    {
        if (material is not MaterialInstanceConstant instance)
        {
            return false;
        }

        var scalars = instance.ScalarParameterValues;
        for (var i = 0; i < scalars.Count; i++)
        {
            if (scalars[i].ParameterName == Marker)
            {
                return true;
            }
        }

        return false;
    }

    // The material under ours, put back on the mesh, for paint left on by an earlier load.
    static MaterialInterface Unwrap(MeshComponent mesh, int element, MaterialInterface worn)
    {
        for (var depth = 0; depth < 4 && IsOurs(worn) && worn is MaterialInstanceConstant { Parent: { IsValid: true } parent }; depth++)
        {
            mesh.SetMaterial(element, parent);
            worn = parent;
        }

        return worn;
    }

    public static List<SuitPart> Find(RPawnPlayer pawn)
    {
        var parts = new List<SuitPart>();
        var owner = OwnerWords(pawn);
        var found = new Dictionary<nint, SuitPart?>();
        var meshes = new HashSet<nint>();
        var all = pawn.AllComponents;
        for (var i = 0; i < all.Count; i++)
        {
            if (all[i] is not MeshComponent { IsValid: true } mesh || !meshes.Add(mesh.Ptr))
            {
                continue;
            }

            for (var element = 0; element < mesh.GetNumElements(); element++)
            {
                if (mesh.GetMaterial(element) is not { IsValid: true } first)
                {
                    continue;
                }

                var worn = Unwrap(mesh, element, first);
                if (SourceOf(worn) is not { } source)
                {
                    continue;
                }

                if (!found.TryGetValue(source.Ptr, out var part))
                {
                    part = Describe(source, owner, parts);
                    found[source.Ptr] = part;
                    if (part is not null)
                    {
                        parts.Add(part);
                    }
                }

                if (part is not null)
                {
                    var held = mesh.Materials;
                    part.Slots.Add(new SuitSlot(mesh, element, worn, element < held.Count ? held[element] : null));
                }
            }
        }

        return parts;
    }

    // The suit's package name without its battle damage level, so damage keeps the colours.
    public static string SuitKey(RPawnPlayer pawn)
    {
        if (!Game.TryGetGameRI(out var game) || Loaded.IndexOf(game, pawn) is var index && index < 0)
        {
            return "";
        }

        var name = Loaded.Slot(game, index).MeshName.ToString() ?? "";
        var cut = name.LastIndexOf('_');
        if (cut > 0 && name[(cut + 1)..].All(char.IsDigit) && name.Contains("_Std", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..cut];
        }

        // Ascii only, since it is written as a bare key in settings.toml.
        var key = new string(name.Select(letter => char.IsAsciiLetterOrDigit(letter) ? letter : '_').ToArray());
        return key.Trim('_').Length == 0 ? "" : key;
    }

    // The material from the suit's package, under the copies the game and the mod make per body.
    static MaterialInterface? SourceOf(MaterialInterface worn)
    {
        var material = worn;
        for (var depth = 0; depth < 8 && material is MaterialInstanceConstant instance && instance.IsInMapOrTransientPackage(); depth++)
        {
            material = instance.Parent;
        }

        return material is { IsValid: true } && !(material is MaterialInstance instanced && instanced.IsInMapOrTransientPackage()) ? material : null;
    }

    // A part, or null when its material has nothing a colour can go on.
    static SuitPart? Describe(MaterialInterface source, string[] owner, List<SuitPart> parts)
    {
        var colours = new List<FName>();
        var map = default(FName);
        var hasMap = false;
        var bright = default(FName);
        var hasBright = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddColour(FName name)
        {
            var text = name.ToString() ?? "";
            if (seen.Add("v" + text) && IsColour(text))
            {
                colours.Add(name);
            }
        }

        void AddMap(FName name)
        {
            var text = name.ToString() ?? "";
            if (!hasMap && seen.Add("t" + text) && IsColourMap(text))
            {
                map = name;
                hasMap = true;
            }
        }

        void AddScalar(FName name)
        {
            if (!hasBright && string.Equals(name.ToString(), "Brightness_Multiplier", StringComparison.OrdinalIgnoreCase))
            {
                bright = name;
                hasBright = true;
            }
        }

        var material = source;
        for (var depth = 0; depth < 8 && material is MaterialInstanceConstant instance; depth++)
        {
            var scalarValues = instance.ScalarParameterValues;
            for (var k = 0; k < scalarValues.Count; k++)
            {
                AddScalar(scalarValues[k].ParameterName);
            }

            var vectors = instance.VectorParameterValues;
            for (var k = 0; k < vectors.Count; k++)
            {
                AddColour(vectors[k].ParameterName);
            }

            var textures = instance.TextureParameterValues;
            for (var k = 0; k < textures.Count; k++)
            {
                AddMap(textures[k].ParameterName);
            }

            material = instance.Parent;
        }

        if (material is Material master)
        {
            var expressions = master.Expressions;
            for (var k = 0; k < expressions.Count; k++)
            {
                switch (expressions[k])
                {
                    case MaterialExpressionVectorParameter vector:
                        AddColour(vector.ParameterName);
                        break;
                    case MaterialExpressionTextureSampleParameter texture:
                        AddMap(texture.ParameterName);
                        break;
                    case MaterialExpressionScalarParameter scalar:
                        AddScalar(scalar.ParameterName);
                        break;
                }
            }
        }

        var picture = hasMap && source.GetTextureParameterValue(map, out var found) ? found as Texture2D : null;
        if (colours.Count == 0 && picture is null)
        {
            return null;
        }

        var named = Label(source.Name.ToString() ?? "", owner);
        var (key, label) = (named.ToLowerInvariant().Replace(' ', '_'), named);
        for (var n = 2; parts.Exists(part => part.Key == key); n++)
        {
            (key, label) = ($"{named.ToLowerInvariant().Replace(' ', '_')}_{n}", $"{named} {n}");
        }

        var part = new SuitPart(key, label) { Map = picture is null ? default : map, Picture = picture };
        part.Colours.AddRange(colours);
        if (hasBright && source.GetScalarParameterValue(bright, out var baseValue))
        {
            part.Bright = bright;
            part.BrightBase = baseValue;
            part.HasBright = true;
        }

        return part;
    }

    // Names like Glass_Colour, Glow Colour, edge_glow_colour and internal_reflection. Rain and
    // displacement vectors aren't colours even when they sit beside them.
    static bool IsColour(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("rain") || lower.Contains("displace") || lower.Contains("size") || lower.Contains("point"))
        {
            return false;
        }

        return lower.Contains("colo") || lower.Contains("glow") || lower.Contains("reflection") || lower.Contains("fresnel");
    }

    static bool IsColourMap(string name)
    {
        var letters = new string(name.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        return letters is "colourmap" or "colormap" or "diffusemap" or "diffuse" or "diffusetexture" or "basecolour" or "basecolor" or "albedo";
    }

    static string Label(string name, string[] owner)
    {
        var words = name.Split(new[] { '_', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !Filler.Contains(word)
                && !owner.Contains(word, StringComparer.OrdinalIgnoreCase)
                && !(word.Any(char.IsLetter) && word.Any(char.IsDigit)))
            .Select(word => word.ToUpperInvariant())
            .ToArray();

        return words.Length == 0 ? "PART" : string.Join(' ', words);
    }

    static string[] OwnerWords(RPawnPlayer pawn)
    {
        if (!Game.TryGetGameRI(out var game) || Loaded.IndexOf(game, pawn) is var index && index < 0)
        {
            return Array.Empty<string>();
        }

        return (Loaded.Slot(game, index).CharacterName.ToString() ?? "").Split('_', StringSplitOptions.RemoveEmptyEntries);
    }
}
