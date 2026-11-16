#nullable enable

using System.Diagnostics;
using System.Text;
using BmSDK;
using BmSDK.Engine;
using Lod = BmSDK.Engine.Texture.TextureGroup;

namespace Knightfall;

// ASSETS > TEXTURES. Lists the textures the game has loaded, or the ones on what you aim at or on
// you, sorted by the game's own texture groups and split by name so a group is short enough to
// step through. The viewer beside the menu and the light table draw the one picked.
static class Textures
{
    sealed record Kind(string Id, string Label, params Lod[] Groups);

    static readonly Kind[] Kinds =
    {
        new("interface", "INTERFACE", Lod.TEXTUREGROUP_UI),
        new("characters", "CHARACTERS",
            Lod.TEXTUREGROUP_Character, Lod.TEXTUREGROUP_CharacterNormalMap, Lod.TEXTUREGROUP_CharacterSpecular,
            Lod.TEXTUREGROUP_CharacterSpecPower, Lod.TEXTUREGROUP_CharacterLow, Lod.TEXTUREGROUP_CharacterHigh),
        new("vehicles", "VEHICLES",
            Lod.TEXTUREGROUP_Vehicle, Lod.TEXTUREGROUP_VehicleNormalMap, Lod.TEXTUREGROUP_VehicleSpecular,
            Lod.TEXTUREGROUP_Vehicle_Low, Lod.TEXTUREGROUP_Vehicle_High),
        new("weapons", "WEAPONS", Lod.TEXTUREGROUP_Weapon, Lod.TEXTUREGROUP_WeaponNormalMap, Lod.TEXTUREGROUP_WeaponSpecular),
        new("world", "WORLD",
            Lod.TEXTUREGROUP_World, Lod.TEXTUREGROUP_WorldNormalMap, Lod.TEXTUREGROUP_WorldSpecular,
            Lod.TEXTUREGROUP_World_Low, Lod.TEXTUREGROUP_World_Hi, Lod.TEXTUREGROUP_WorldNormalMap_Hi,
            Lod.TEXTUREGROUP_WorldSpecular_Hi, Lod.TEXTUREGROUP_ProcBuilding_Face,
            Lod.TEXTUREGROUP_Terrain_Heightmap, Lod.TEXTUREGROUP_Terrain_Weightmap),
        new("interiors", "INTERIORS", Lod.TEXTUREGROUP_Interior_High, Lod.TEXTUREGROUP_InteriorNormalMap_High, Lod.TEXTUREGROUP_InteriorSpec_High),
        new("effects", "EFFECTS", Lod.TEXTUREGROUP_Effects, Lod.TEXTUREGROUP_EffectsNotFiltered, Lod.TEXTUREGROUP_Bokeh),
        new("cinematics", "CINEMATICS", Lod.TEXTUREGROUP_Cinematic),
        new("masks", "DISTANCE MASKS", Lod.TEXTUREGROUP_LOD1_RRM_Mask, Lod.TEXTUREGROUP_LOD1_Opacity_Mask, Lod.TEXTUREGROUP_LOD1_Emissive),
        new("lighting", "SKY AND LIGHTING",
            Lod.TEXTUREGROUP_Skybox, Lod.TEXTUREGROUP_Lightmap, Lod.TEXTUREGROUP_ProcBuilding_LightMap,
            Lod.TEXTUREGROUP_Shadowmap, Lod.TEXTUREGROUP_ColorLookupTable, Lod.TEXTUREGROUP_ImageBasedReflection),
        new("targets", "LIVE SCREENS", Lod.TEXTUREGROUP_RenderTarget),
        new("other", "EVERYTHING ELSE"),
    };

    static readonly int[] KindOfGroup = Groups();

    // A texture as listed. Words are its path cut up for sorting and grouping.
    public readonly record struct Entry(Texture Picture, string Path, string[] Words);

    // The most one group lists. A bigger set is split by the words of its names.
    const int GroupMost = 80;

    // How far the crosshair reaches for WHAT YOU AIM AT.
    const float AimReach = 20000f;

    static readonly (string Id, string Label)[] NotInGame = { ("", "OPEN THE MENU IN GAME") };
    static readonly (string Id, string Label)[] Nothing = { ("", "NONE HERE") };
    static readonly (string Id, string Label)[] NothingAimed = { ("", "AIM AT SOMETHING, THEN REFRESH") };

    static readonly List<Entry>[] ByKind = Kinds.Select(_ => new List<Entry>()).ToArray();
    static readonly List<Entry> Found = new();
    static readonly List<WordGroups.Bunch> Bunches = new();
    static Entry[] _entries = Array.Empty<Entry>();
    static int _total;
    static int _listedKind = -1;
    static int _listedGroup = -1;
    static int _listedSource = -1;
    static int _epoch = -1;
    static bool _stale = true;
    static bool _shown;

    // What the last WHAT YOU AIM AT or ON YOU list was taken from.
    static string _from = "";

    // The picked texture's group, for the viewer's strip and the light table.
    public static readonly Shelf Shelf = new(
        Count: static () => _listedGroup >= 0 && _listedGroup < Bunches.Count ? Bunches[_listedGroup].Count : 0,
        Index: PickedIndex,
        At: EntryAt,
        Pick: PickAt,
        Turn: TurnGroup,
        Title: static () => _listedGroup >= 0 && _listedGroup < Bunches.Count ? Bunches[_listedGroup].Label : "");

    public static readonly Choice Source = new(
        "assets.textures.source", "SOURCE",
        "Where to list textures from. WHAT YOU AIM AT reads the materials of whatever is under the crosshair, ON YOU your own. The list is read again each time this tab comes up.",
        new[] { ("loaded", "LOADED HERE"), ("aimed", "WHAT YOU AIM AT"), ("worn", "ON YOU") })
    {
        Preference = true,
        Changed = static () => _stale = true,
    };

    public static readonly Choice Show = new(
        "assets.textures.show", "SHOW",
        "The game's own texture groups, like INTERFACE, CHARACTERS, WORLD and EFFECTS.",
        Kinds.Select(kind => (kind.Id, kind.Label)).ToArray())
    {
        Preference = true,
        Block = static () => Source.Index != 0 ? "EVERY KIND" : null,
    };

    public static readonly Choice Group = new(
        "assets.textures.group", "GROUP",
        "Textures whose names start the same way. The number is how many it has.",
        NotInGame, saved: false)
    {
        Block = static () => Bunches.Count == 0 ? Placeholder() : null,
    };

    public static readonly Choice Picture = new(
        "assets.textures.texture", "TEXTURE",
        "The texture shown beside the menu. Hold left or right to run through them. Select opens it on the light table, to look closer.",
        NotInGame, saved: false)
    {
        Pressed = static () => LightTable.Open(Shelf),
        PressName = "LIGHT TABLE",
        Block = static () => Picked() is null ? Placeholder() : null,
    };

    public static readonly Command Copy = new(
        "assets.textures.copy", "COPY PATH",
        "Puts the texture's full path on the clipboard, to paste into a mod or a script.",
        static () => Picked() is { } entry
            ? Clipboard.Put(entry.Path) ? Outcome.Done("COPIED") : Outcome.Failed("CLIPBOARD BUSY")
            : Outcome.Failed("NOTHING PICKED"))
    {
        Cap = "COPY",
        Block = static () => Picked() is null ? "NOTHING PICKED" : null,
    };

    public static readonly Command Save = new(
        "assets.textures.save", "SAVE LIST",
        "Writes every texture loaded right now, with its size, format and group, to data/exports/assets.txt in the Knightfall folder.",
        SaveList)
    {
        Cap = "SAVE",
        Block = OutOfGame,
    };

    public static readonly Command Refresh = new(
        "assets.textures.refresh", "REFRESH",
        "Reads the list again. What's loaded changes as you move around the city, and WHAT YOU AIM AT reads what you aim at now.",
        static () =>
        {
            Scan(report: true);
            return _total == 0 ? Outcome.Unchanged("NONE FOUND") : Outcome.Done($"{_total} TEXTURES");
        })
    {
        Block = OutOfGame,
    };

    public static readonly Catalog.Section[] Sections =
    {
        new("FIND", Source, Show, Group, Picture),
        new("VIEW", Viewer.Channels, Viewer.Backdrop, Viewer.Tile, Viewer.Detail),
        new("USE", Copy, Save, Refresh),
    };

    public static string From => _from;

    public static bool Listing => Source.Index != 0;

    // Every tick. The list is read when the tab comes up, and follows SOURCE, SHOW and GROUP from
    // then on.
    public static void Tick()
    {
        // A new world took most of the textures with it.
        if (_epoch != World.Epoch)
        {
            _epoch = World.Epoch;
            Forget();
        }

        var shown = Menu.Shows(Picture);
        if (shown && World.Info is not null && (_stale || !_shown || _listedSource != Source.Index))
        {
            Scan(report: _stale);
        }

        _shown = shown;
        if (_stale)
        {
            return;
        }

        if (_listedKind != Show.Index)
        {
            ListGroups();
        }
        else if (_listedGroup != Group.Index)
        {
            ListPictures(null);
        }
    }

    static int[] Groups()
    {
        var kinds = Enumerable.Repeat(Kinds.Length - 1, 256).ToArray();
        for (var i = 0; i < Kinds.Length; i++)
        {
            foreach (var group in Kinds[i].Groups)
            {
                kinds[(int)group] = i;
            }
        }

        return kinds;
    }

    static int KindOf(Texture picture) => KindOfGroup[(int)picture.LODGroup];

    static int PickedIndex() => Picture.Index;

    static void PickAt(int index) => Picture.SetTo(index);

    static void TurnGroup(int direction) => Group.Step(direction);

    static string? OutOfGame() => World.Info is null ? "NOT IN GAME" : null;

    // What a list with nothing in it says, in place of a value.
    static string Placeholder() => World.Info is null ? NotInGame[0].Label : Source.Index == 1 ? NothingAimed[0].Label : Nothing[0].Label;

    // Report logs how many were found and how long it took, once a world and on REFRESH.
    static void Scan(bool report)
    {
        var clock = Stopwatch.StartNew();
        _stale = false;
        _listedSource = Source.Index;
        _total = 0;
        _from = "";
        Found.Clear();
        foreach (var list in ByKind)
        {
            list.Clear();
        }

        switch (Source.Index)
        {
            case 0:
                foreach (var picture in Game.FindObjects<Texture2D>())
                {
                    Add(picture);
                }

                foreach (var picture in Game.FindObjects<TextureRenderTarget2D>())
                {
                    Add(picture);
                }

                break;
            case 1:
                Aimed();
                break;
            default:
                Worn();
                break;
        }

        // Textures are reached through many materials, and each is listed once.
        if (Source.Index != 0)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Found.RemoveAll(entry => !seen.Add(entry.Path));
            _total = Found.Count;
        }

        if (report)
        {
            Log.Info($"Assets: listed {_total} textures in {clock.Elapsed.TotalMilliseconds:0} ms.");
        }
        _listedKind = -1;
        ListGroups();
    }

    // Cube maps and movies can't be drawn flat, so only these two kinds are listed.
    static void Add(Texture picture)
    {
        if (picture is not (Texture2D or TextureRenderTarget2D) || !picture.IsValid || picture.IsClassDefaultObject)
        {
            return;
        }

        var path = picture.GetPathName();
        var entry = new Entry(picture, path, Words(path));
        if (Source.Index == 0)
        {
            ByKind[KindOf(picture)].Add(entry);
        }
        else
        {
            Found.Add(entry);
        }

        _total++;
    }

    // The package as one word, then each folder, then the words of the name.
    static string[] Words(string path)
    {
        var parts = path.ToUpperInvariant().Split('.');
        var words = new List<string>(8) { parts[0].Replace('_', ' ') };
        for (var i = 1; i < parts.Length - 1; i++)
        {
            words.Add(parts[i].Replace('_', ' '));
        }

        if (parts.Length > 1)
        {
            words.AddRange(parts[^1].Split('_', StringSplitOptions.RemoveEmptyEntries));
        }

        return words.ToArray();
    }

    // The textures on the materials of whatever part of the world is under the crosshair.
    static void Aimed()
    {
        if (!Crosshair.Surface(AimReach, out var hit, Crosshair.Meets.Anything))
        {
            return;
        }

        _from = Plain(hit.Actor.Name.ToString());
        if (hit.Part is MeshComponent { IsValid: true } part)
        {
            Materials(part);
        }

        // Some parts have no materials of their own to read, like a person's collision.
        if (Found.Count == 0)
        {
            foreach (var other in hit.Actor.Components)
            {
                if (other is MeshComponent { IsValid: true, HiddenGame: false } mesh)
                {
                    Materials(mesh);
                }
            }
        }
    }

    static void Worn()
    {
        if (World.Player is not { } player)
        {
            return;
        }

        _from = "YOU";
        foreach (var part in player.AllComponents)
        {
            if (part is MeshComponent { IsValid: true, HiddenGame: false } mesh)
            {
                Materials(mesh);
            }
        }
    }

    static void Materials(MeshComponent mesh)
    {
        var count = mesh.GetNumElements();
        for (var i = 0; i < count; i++)
        {
            Material(mesh.GetMaterial(i));
        }
    }

    // Values set on each instance first, then what its parent and the base material use.
    static void Material(MaterialInterface? material)
    {
        for (var depth = 0; material is { IsValid: true } && depth < 16; depth++)
        {
            switch (material)
            {
                case MaterialInstanceConstant constant:
                    foreach (var value in constant.TextureParameterValues)
                    {
                        if (value.ParameterValue is { IsValid: true } picture)
                        {
                            Add(picture);
                        }
                    }

                    break;
                case Material root:
                    foreach (var picture in root.ReferencedTextures)
                    {
                        if (picture is { IsValid: true })
                        {
                            Add(picture);
                        }
                    }

                    return;
            }

            material = material is MaterialInstance instance ? instance.Parent : null;
        }
    }

    static string Plain(string name) => name.Replace('_', ' ').ToUpperInvariant();

    // The listed kind's textures, sorted by the words of their paths and split into groups.
    static void ListGroups()
    {
        var picked = _listedGroup >= 0 && _listedGroup < Bunches.Count ? Bunches[_listedGroup].Label : null;
        var keep = Picked()?.Picture;

        _listedKind = Show.Index;
        var source = Source.Index == 0 ? ByKind[Math.Clamp(_listedKind, 0, Kinds.Length - 1)] : Found;
        var entries = new List<Entry>(source.Count);
        foreach (var entry in source)
        {
            if (entry.Picture.IsValid)
            {
                entries.Add(entry);
            }
        }

        entries.Sort(static (a, b) => WordGroups.Compare(a.Words, b.Words));
        _entries = entries.ToArray();
        Bunches.Clear();
        Bunches.AddRange(WordGroups.Split(Array.ConvertAll(_entries, static entry => entry.Words), GroupMost));

        if (Bunches.Count == 0)
        {
            _listedGroup = 0;
            var none = World.Info is null ? NotInGame : Source.Index == 1 ? NothingAimed : Nothing;
            Group.SetOptions(none, 0);
            Picture.SetOptions(none, 0);
            return;
        }

        var index = picked is null ? 0 : Math.Max(0, Bunches.FindIndex(bunch => bunch.Label == picked));
        Group.SetOptions(Bunches.Select((bunch, i) => (i.ToString(), $"{bunch.Label}  ({bunch.Count})")).ToArray(), index);
        ListPictures(keep);
    }

    static void ListPictures(Texture? keep)
    {
        _listedGroup = Group.Index;
        if (_listedGroup < 0 || _listedGroup >= Bunches.Count)
        {
            return;
        }

        var bunch = Bunches[_listedGroup];
        var options = new (string, string)[bunch.Count];
        var index = 0;
        for (var i = 0; i < bunch.Count; i++)
        {
            var entry = _entries[bunch.Start + i];
            options[i] = (i.ToString(), Name(entry.Path));
            if (ReferenceEquals(entry.Picture, keep))
            {
                index = i;
            }
        }

        Picture.SetOptions(options, index);
    }

    // The last part of a path, as the menu writes it.
    public static string Name(string path)
    {
        var dot = path.LastIndexOf('.');
        return (dot >= 0 ? path[(dot + 1)..] : path).Replace('_', ' ').ToUpperInvariant();
    }

    static Entry? EntryAt(int index)
    {
        if (_listedGroup < 0 || _listedGroup >= Bunches.Count)
        {
            return null;
        }

        var bunch = Bunches[_listedGroup];
        return index >= 0 && index < bunch.Count ? _entries[bunch.Start + index] : null;
    }

    public static Entry? Picked() => int.TryParse(Picture.CurrentId, out var index) ? EntryAt(index) : null;

    static Outcome SaveList()
    {
        if (World.Info is null)
        {
            return Outcome.Failed("NOT IN GAME");
        }

        var rows = new List<(string Kind, string Line)>();
        foreach (var picture in Game.FindObjects<Texture2D>().Cast<Texture>().Concat(Game.FindObjects<TextureRenderTarget2D>()))
        {
            if (!picture.IsValid || picture.IsClassDefaultObject)
            {
                continue;
            }

            var facts = Facts.Of(picture);
            rows.Add((Kinds[KindOf(picture)].Label, $"{picture.GetPathName()}  {facts.Size}  {facts.Format}  {facts.Group}"));
        }

        var text = new StringBuilder();
        text.Append("# Knightfall asset list, ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append('\n');
        text.Append("# path  width x height  format  texture group\n");
        foreach (var kind in rows.GroupBy(row => row.Kind).OrderBy(kind => kind.Key, StringComparer.Ordinal))
        {
            text.Append('\n').Append('[').Append(kind.Key).Append("]\n");
            foreach (var line in kind.Select(row => row.Line).OrderBy(line => line, StringComparer.OrdinalIgnoreCase))
            {
                text.Append(line).Append('\n');
            }
        }

        var file = ModFiles.Ready(ModFiles.Export("assets.txt"));
        File.WriteAllText(file, text.ToString());
        Log.Info($"Assets: saved {rows.Count} textures to {ModFiles.Shown(file)}.");
        return Outcome.Done($"SAVED {rows.Count}");
    }

    static void Forget()
    {
        _entries = Array.Empty<Entry>();
        Bunches.Clear();
        Found.Clear();
        foreach (var list in ByKind)
        {
            list.Clear();
        }

        _total = 0;
        _listedGroup = -1;
        _listedKind = -1;
        _stale = true;
        _from = "";
        Group.SetOptions(NotInGame, 0);
        Picture.SetOptions(NotInGame, 0);
    }
}
