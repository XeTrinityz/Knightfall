#nullable enable

using System.Diagnostics;
using System.Text;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// What a Painter paints and where it keeps its colours.
sealed record PaintSpec(
    string Prefix,
    string Store,
    string Noun,
    string Whole,
    string ClearAllId,
    string NoSubject,
    Func<Actor?> Subject,
    Func<Actor, string> KeyOf,
    Func<Actor, List<SuitPart>> Find)
{
    // The look to put on when a new key is first seen, or null for none.
    public Func<string, Dictionary<string, SuitLook>?>? PutOn { get; init; }

    public Action? Scanned { get; init; }
}

// The PAINT rows for one thing that wears materials, a suit or the Batmobile. Glowing bits like
// a visor have colour settings of their own. Cloth, leather and plates only have a colour
// picture, so those get a tinted copy of the picture drawn by the mod. One job owns every
// material the mod puts on the subject.
sealed class Painter
{
    readonly PaintSpec _spec;


    // A copy is a plain texture with no smaller versions, so very large pictures are drawn at
    // this size. It still costs 16 MB per painted part.
    const int LargestCopy = 2048;

    const double CheckSeconds = 0.5;

    // How long a part flashes when it's picked on PART.
    const double FlashSeconds = 0.25;

    // A new tinted copy waits until the tint has held still this long.
    const double TintSettleSeconds = 0.25;

    // The flash colour, bright enough to show on a black part.
    static readonly GameObject.FLinearColor FlashColour = new() { R = 0.1f, G = 2.5f, B = 2.2f, A = 1 };

    // How long a copy waits for the game to stream its picture in fully before it's drawn anyway.
    const double SharpWaitSeconds = 3;

    // Slider steps closer together than this are one change for UNDO.
    const double UndoJoinSeconds = 1;

    const int UndoDepth = 30;

    const string AllParts = "*all";

    static readonly (string Id, string Label)[] PaletteRows = SuitLook.Palette.Select(swatch => (swatch.Id, swatch.Label)).ToArray();

    static readonly (string Id, string Label)[] NoParts = { ("", "NONE") };

    List<SuitPart> Parts = new();
    readonly Dictionary<string, SuitLook> Looks = new();
    readonly HashSet<string> Marked = new();
    readonly List<Dictionary<string, SuitLook>> Undo = new();
    readonly Random Dice = new();
    readonly HashSet<string> Flashing = new();
    long _flashUntil;
    ScriptedTexture? _flat;
    bool _flatDrawn;
    nint _subjectPtr;
    string _key = "";
    long _checkedAt;
    long _editedAt;
    bool _dressed;
    bool _scanning;
    SuitLook? _copied;

    public readonly Toggle Paint;
    public readonly Choice Which;
    public readonly Choice Colour;
    public readonly Slider Strength;
    public readonly Slider Brightness;
    public readonly Command CopyLook;
    public readonly Command PasteLook;
    public readonly Command Shuffle;
    public readonly Command Back;
    public readonly Command Unmark;
    public readonly Command ClearPart;
    public readonly Command ClearAll;

    public Painter(PaintSpec spec)
    {
        _spec = spec;
        var noun = spec.Noun;
        var whole = spec.Whole;
        var id = spec.Prefix;

        Paint = new Toggle(
            $"{id}.paint", $"{whole.ToUpperInvariant()} PAINT",
            $"Wears the colours set below. Each {noun} keeps its own colours, so a change of {noun} brings back what you gave that one.");

        Which = new Choice(
            $"{id}.part", "PART",
            $"The part to colour. Press it to mark it, and every marked part takes the same colour. ALL PARTS does the whole {whole}. A part flashes once when you pick it, so you can tell which one it is.",
            NoParts, saved: false)
        {
            Opening = Refresh,
            Changed = Picked,
            Pressed = ToggleMark,
            PressName = "MARK",
            Block = NoPart,
            Preference = true,
        };

        Colour = new Choice(
            $"{id}.colour", "COLOUR",
            $"The colour for the picked parts. ORIGINAL keeps the {whole}'s own colour, so BRIGHTNESS can still lighten or darken it.",
            PaletteRows, saved: false)
        {
            Changed = Edited,
            Block = NoPart,
            Preference = true,
        };

        Strength = new Slider(
            $"{id}.strength", "STRENGTH",
            $"How much of the colour goes on. Lower keeps more of the {whole}'s own colour showing through.",
            min: 0, max: 100, step: 5, value: 100, unit: "%", saved: false)
        {
            Changed = Edited,
            Block = NoPart,
            Preference = true,
        };

        Brightness = new Slider(
            $"{id}.brightness", "BRIGHTNESS",
            $"Lightens or darkens the picked parts. A dark {whole} needs this turned up for a colour to show well.",
            min: 0.2, max: 4, step: 0.1, value: 1, unit: "X", saved: false)
        {
            Changed = Edited,
            Block = NoPart,
            Preference = true,
        };

        CopyLook = new Command(
            $"{id}.copy_look", "COPY LOOK",
            "Remembers the colour, strength and brightness of the part under PART.",
            CopyOne)
        {
            Block = () => NoPart() ?? (Which.CurrentId == AllParts ? "PICK ONE PART" : null),
        };

        PasteLook = new Command(
            $"{id}.paste_look", "PASTE LOOK",
            "Puts the copied look on the picked parts.",
            PasteOne)
        {
            Block = () => NoPart() ?? (_copied is null ? "COPY ONE FIRST" : null),
        };

        Shuffle = new Command(
            $"{id}.random", "RANDOM COLOURS",
            "Gives each picked part a random colour. Press again for another go.",
            Randomise)
        {
            Block = NoPart,
        };

        Back = new Command(
            $"{id}.undo", "UNDO",
            $"Takes back the last change to this {noun}'s colours.",
            UndoOne)
        {
            Block = () => NoPart() ?? (Undo.Count == 0 ? "NOTHING TO UNDO" : null),
        };

        Unmark = new Command(
            $"{id}.clear_marks", "CLEAR MARKS",
            "Unmarks every part, so changes go to the part under PART again.",
            ClearMarks)
        {
            Block = () => NoPart() ?? (Marked.Count == 0 ? "NONE MARKED" : null),
        };

        ClearPart = new Command(
            $"{id}.clear_part", "CLEAR PART",
            $"Puts the picked parts back to how the {whole} made them.",
            ClearPicked)
        {
            Block = () => NoPart() ?? (Looks.Count == 0 ? "NOTHING SET" : null),
        };

        ClearAll = new Command(
            $"{id}.{spec.ClearAllId}", $"CLEAR {whole.ToUpperInvariant()}",
            $"Puts every part of this {noun} back to how the {whole} made it.",
            ClearEverything)
        {
            Block = () => NoPart() ?? (Looks.Count == 0 ? "NOTHING SET" : null),
        };
    }

    // The key the colours are kept under, for the thing painted now.
    public string Key => _key;

    // Anything is painted on it now.
    public bool Painted => Looks.Count > 0;

    // Keeps every key's colours in settings.toml, not only the one worn now.
    public void KeepStore() => Settings.KeepAll(_spec.Store);

    string? NoPart() => _spec.Subject() is null ? _spec.NoSubject : Parts.Count == 0 ? "NOTHING TO PAINT" : null;

    SuitPart? Current => Parts.Find(part => part.Key == Which.CurrentId);

    SuitLook LookOf(SuitPart part) => Looks.TryGetValue(part.Key, out var look) ? look : SuitLook.Plain;

    // What a change goes to: every part for ALL PARTS, else the marked parts, else the one under
    // PART.
    List<SuitPart> Targets()
    {
        Fresh();
        if (Which.CurrentId == AllParts)
        {
            return Parts;
        }

        if (Marked.Count > 0)
        {
            return Parts.FindAll(part => Marked.Contains(part.Key));
        }

        return Current is { } part ? new List<SuitPart> { part } : new List<SuitPart>();
    }

    // Reads the parts again when they are not the ones the parts were read from. The key can change while
    // the menu is open, and an edit then belongs to the new one.
    void Fresh()
    {
        if (!_scanning && _spec.Subject() is { } subject && Stale(subject))
        {
            Scan(subject);
        }
    }

    // The menu is opening. Reads the suit again only when it changed.
    void Refresh()
    {
        if (_spec.Subject() is { } subject && Stale(subject))
        {
            Scan(subject);
            return;
        }

        List();
    }

    // Fills PART from the parts found, keeping the pick when that part is still there.
    void List()
    {
        var keep = Which.CurrentId;
        var list = Parts.Count == 0
            ? NoParts
            : Parts.Select(part => (part.Key, Marked.Contains(part.Key) ? part.Label + " +" : part.Label)).Prepend((AllParts, "ALL PARTS")).ToArray();
        var index = Array.FindIndex(list, option => option.Item1 == keep);
        Which.SetOptions(list, index >= 0 ? index : Math.Min(1, list.Length - 1));
        ShowPart();
    }

    // PART moved: the new part flashes once, so it's easy to tell which one it is.
    void Picked()
    {
        ShowPart();
        Flashing.Clear();
        if (Which.CurrentId == AllParts)
        {
            Flashing.UnionWith(Parts.Select(part => part.Key));
        }
        else if (Current is { } part)
        {
            Flashing.Add(part.Key);
        }

        _flashUntil = Stopwatch.GetTimestamp() + (long)(FlashSeconds * Stopwatch.Frequency);
        FlatTexture();
    }

    void ShowPart()
    {
        var look = Targets() is { Count: > 0 } targets ? LookOf(targets[0]) : SuitLook.Plain;
        Colour.SetOptions(PaletteRows, look.Colour);
        Strength.Show(look.Strength);
        Brightness.Show(look.Brightness);
    }

    void ToggleMark()
    {
        // On ALL PARTS it marks every part, or unmarks them all when they already are.
        if (Which.CurrentId == AllParts)
        {
            if (Marked.Count == Parts.Count)
            {
                Marked.Clear();
            }
            else
            {
                Marked.UnionWith(Parts.Select(each => each.Key));
            }

            List();
            return;
        }

        if (Current is not { } part)
        {
            return;
        }

        if (!Marked.Remove(part.Key))
        {
            Marked.Add(part.Key);
        }

        List();
    }

    Outcome ClearMarks()
    {
        Marked.Clear();
        List();
        return Outcome.Done("UNMARKED");
    }

    void Edited()
    {
        var look = new SuitLook(Colour.Index, Strength.Value, Brightness.Value);
        Put(Targets(), _ => look, join: true);
    }

    // Changes the looks of these parts, kept for UNDO and saved.
    int Put(List<SuitPart> parts, Func<SuitPart, SuitLook> look, bool join = false)
    {
        if (parts.Count == 0)
        {
            return 0;
        }

        var now = Stopwatch.GetTimestamp();
        var remember = !join || Seconds(now - _editedAt) >= UndoJoinSeconds;
        var before = remember ? new Dictionary<string, SuitLook>(Looks) : null;
        var changed = 0;
        foreach (var part in parts)
        {
            var next = look(part);
            if (next == LookOf(part))
            {
                continue;
            }

            changed++;
            if (next.IsPlain)
            {
                Looks.Remove(part.Key);
            }
            else
            {
                Looks[part.Key] = next;
            }
        }

        if (changed == 0)
        {
            return 0;
        }

        if (before is not null)
        {
            Undo.Add(before);
            if (Undo.Count > UndoDepth)
            {
                Undo.RemoveAt(0);
            }
        }

        _editedAt = join ? now : 0;
        Save();
        if (Looks.Count > 0 && !Paint.On)
        {
            Paint.SetTo(true);
        }

        return changed;
    }

    Outcome CopyOne()
    {
        if (Current is not { } part)
        {
            return Outcome.Failed("PICK ONE PART");
        }

        _copied = LookOf(part);
        return Outcome.Done("COPIED");
    }

    Outcome PasteOne()
    {
        if (_copied is not { } look)
        {
            return Outcome.Failed("NOTHING COPIED");
        }

        var changed = Put(Targets(), _ => look);
        ShowPart();
        return changed == 0 ? Outcome.Unchanged("NO CHANGE") : Outcome.Done("PASTED");
    }

    Outcome Randomise()
    {
        Put(Targets(), part => new SuitLook(Dice.Next(1, SuitLook.Palette.Length), 100, Math.Round(1 + Dice.NextDouble() * 1.5, 1)));
        ShowPart();
        return Outcome.Done("SHUFFLED");
    }

    Outcome UndoOne()
    {
        if (Undo.Count == 0)
        {
            return Outcome.Unchanged("NOTHING TO UNDO");
        }

        var last = Undo[^1];
        Undo.RemoveAt(Undo.Count - 1);
        Looks.Clear();
        foreach (var (key, look) in last)
        {
            Looks[key] = look;
        }

        _editedAt = 0;
        Save();
        ShowPart();
        return Outcome.Done("UNDONE");
    }

    Outcome ClearPicked()
    {
        if (Put(Targets(), _ => SuitLook.Plain) == 0)
        {
            return Outcome.Unchanged("NOTHING SET");
        }

        ShowPart();
        return Outcome.Done("CLEARED");
    }

    Outcome ClearEverything()
    {
        Fresh();
        if (Put(Parts, _ => SuitLook.Plain) == 0)
        {
            return Outcome.Unchanged("NOTHING SET");
        }

        ShowPart();
        return Outcome.Done("CLEARED");
    }

    // The colours as they are now, for a saved look.
    public Dictionary<string, SuitLook> Snapshot() => new(Looks);

    // Puts a saved look on in place of the colours. Parts the look doesn't name go back
    // to their own.
    public int Wear(IReadOnlyDictionary<string, SuitLook> looks)
    {
        Fresh();
        Put(Parts, part => looks.TryGetValue(part.Key, out var look) ? look : SuitLook.Plain);

        // Parts it doesn't have now are still kept, for when it shows them again.
        foreach (var (key, look) in looks)
        {
            if (!look.IsPlain && !Parts.Exists(part => part.Key == key))
            {
                Looks[key] = look;
            }
        }

        Save();
        ShowPart();
        return looks.Count;
    }

    // Runs every tick. Returns at once when nothing is painted or picked.
    public void Tick()
    {
        var now = Stopwatch.GetTimestamp();
        var flashing = now < _flashUntil;
        if (!Paint.On && !_dressed && !flashing)
        {
            return;
        }

        if (_spec.Subject() is not { } subject)
        {
            return;
        }

        if (subject.Ptr != _subjectPtr || Seconds(now - _checkedAt) >= CheckSeconds)
        {
            _checkedAt = now;
            if (Stale(subject))
            {
                Scan(subject);
            }
        }

        _dressed = false;
        foreach (var part in Parts)
        {
            var look = Paint.On ? LookOf(part) : SuitLook.Plain;
            if (part.Custom is { } custom)
            {
                if (!look.IsPlain || !part.Worn.IsPlain)
                {
                    custom(look);
                }

                part.Worn = look;
                _dressed |= !look.IsPlain;
                continue;
            }

            var lit = flashing && Flashing.Contains(part.Key);
            if (look != part.Worn || lit != part.Lit || part.Waiting)
            {
                Dress(part, look, lit, now);
            }

            _dressed |= part.Slots.Exists(slot => slot.Ours is not null);
        }
    }

    public void Release()
    {
        foreach (var part in Parts)
        {
            Undress(part);
        }

        Scripted.Drop(_flat);
        _flat = null;
        _flatDrawn = false;
        _flashUntil = 0;
        _dressed = false;
    }

    // Whether the parts list no longer matches what is worn: a new body, a new suit or skin,
    // battle damage or anything else that swapped a material.
    bool Stale(Actor subject)
    {
        if (subject.Ptr != _subjectPtr || _spec.KeyOf(subject) != _key)
        {
            return true;
        }

        foreach (var part in Parts)
        {
            foreach (var slot in part.Slots)
            {
                if (!slot.Mesh.IsValid)
                {
                    return true;
                }

                var worn = slot.Mesh.GetMaterial(slot.Element);
                var expected = slot.Ours ?? slot.Was;
                if (worn is null || worn.Ptr != expected.Ptr)
                {
                    return true;
                }
            }
        }

        return false;
    }

    void Scan(Actor subject)
    {
        _scanning = true;
        try
        {
            foreach (var part in Parts)
            {
                Undress(part);
            }

            _subjectPtr = subject.Ptr;
            var worn = _spec.KeyOf(subject);
            Parts = _spec.Find(subject);
            if (worn != _key)
            {
                _key = worn;
                Marked.Clear();
                Undo.Clear();
                Load();

                if (worn.Length > 0 && _spec.PutOn?.Invoke(worn) is { } looks)
                {
                    Looks.Clear();
                    foreach (var (key, look) in looks)
                    {
                        Looks[key] = look;
                    }

                    Save();
                    if (Looks.Count > 0 && !Paint.On)
                    {
                        Paint.SetTo(true);
                    }
                }
            }

            Marked.RemoveWhere(key => !Parts.Exists(part => part.Key == key));
            List();
            _spec.Scanned?.Invoke();
        }
        finally
        {
            _scanning = false;
        }
    }

    // Puts the part's look on every place it's worn.
    void Dress(SuitPart part, SuitLook look, bool lit, long now)
    {
        if (look.IsPlain && !lit)
        {
            Undress(part);
            return;
        }

        var swatch = SuitLook.Palette[Math.Clamp(look.Colour, 0, SuitLook.Palette.Length - 1)];
        var colour = (R: Linear(swatch.R), G: Linear(swatch.G), B: Linear(swatch.B));
        var own = look.Colour == 0;
        var strength = (float)(look.Strength / 100);
        var bright = (float)look.Brightness;

        // A picture can only be dimmed, since it holds no more than white. A part with a
        // brightness setting of its own gets the rest of the brightness from that.
        var copyBright = part.HasBright ? Math.Min(bright, 1f) : bright;
        // The original colour at full brightness is the picture as it is, so no copy is made.
        var tinted = !look.IsPlain && !(own && copyBright == 1f);
        if (!tinted && (part.Copy is not null || part.Next is not null))
        {
            Scripted.Drop(part.Copy);
            Scripted.Drop(part.Next);
            part.Copy = null;
            part.Next = null;
        }

        var copy = tinted && part.Picture is { IsValid: true } picture ? Copy(part, picture, Tint(colour, own, strength, copyBright), now) : null;
        part.Waiting = tinted && part.Picture is { IsValid: true } && (copy is null || part.CopyPending);
        Texture? shown = lit ? FlatTexture() : null;
        shown ??= copy is not null ? copy : part.Picture;

        foreach (var slot in part.Slots)
        {
            if (!slot.Mesh.IsValid || !slot.Was.IsValid)
            {
                continue;
            }

            if (slot.Ours is not { IsValid: true } ours)
            {
                // Always a new material over the original. The game's CreateAndSet hands back the
                // material the mesh already has when that is a copy made for this body, which is
                // nearly all of them, and that would paint over the original colours.
                ours = Made.Tracked(new MaterialInstanceConstant(slot.Mesh));
                if (ours is not { IsValid: true })
                {
                    Log.Warn($"{_spec.Prefix}.paint.material", $"A colour couldn't be put on {part.Label}.");
                    continue;
                }

                ours.SetParent(slot.Was);
                ours.SetScalarParameterValue(SuitParts.Marker, 1f);
                slot.Mesh.SetMaterial(slot.Element, ours);
                slot.Ours = ours;
            }

            if (part.HasBright)
            {
                ours.SetScalarParameterValue(part.Bright, part.BrightBase * Math.Max(bright, 1f));
            }

            foreach (var name in part.Colours)
            {
                if (slot.Was.GetVectorParameterValue(name, out var was))
                {
                    var value = lit ? FlashColour : Mix(was, colour, own, strength, bright);
                    ours.SetVectorParameterValue(name, out value);
                }
            }

            if (shown is not null && part.Picture is not null)
            {
                ours.SetTextureParameterValue(part.Map, shown);
            }
        }

        if (copy is not null && part.Next is not null)
        {
            // The new copy is on now, so the old one can go.
            Scripted.Drop(part.Copy);
            (part.Copy, part.CopyTint, part.Next) = (part.Next, part.NextTint, null);
        }

        part.Worn = look;
        part.Lit = lit;
    }

    // A small flat texture in the flash colour. Null until the game has drawn it.
    ScriptedTexture? FlatTexture()
    {
        if (_flat is not { IsValid: true })
        {
            Scripted.Drop(_flat);
            _flatDrawn = false;
            _flat = Scripted.Make(4, 4, canvas =>
            {
                if (canvas.DefaultTexture is not { } white)
                {
                    return;
                }

                canvas.SetPos(0, 0, 0);
                canvas.SetDrawColor(20, 255, 235, 255);
                canvas.DrawRect(canvas.ClipX, canvas.ClipY, white);
                _flatDrawn = true;
            });
        }

        if (_flat is null)
        {
            return null;
        }

        if (!_flatDrawn)
        {
            _flat.bNeedsUpdate = true;
            return null;
        }

        return _flat;
    }

    void Undress(SuitPart part)
    {
        part.Custom?.Invoke(SuitLook.Plain);
        foreach (var slot in part.Slots)
        {
            if (slot.Ours is { } ours && slot.Mesh.IsValid && slot.Mesh.GetMaterial(slot.Element) is { } worn && worn.Ptr == ours.Ptr)
            {
                slot.Mesh.SetMaterial(slot.Element, slot.Kept);
            }

            slot.Ours = null;
        }

        Scripted.Drop(part.Copy);
        Scripted.Drop(part.Next);
        part.Copy = null;
        part.Next = null;
        part.Waiting = false;
        part.Lit = false;
        part.Worn = SuitLook.Plain;
    }

    // The tinted copy of the part's picture, or null while it's still being drawn. A changed
    // tint gets a new copy, since a copy drawn again in place keeps showing its first drawing. The
    // old one stays on until the new one is ready, so the part never shows blank. A new
    // copy is only made once the tint has held still, so a slider that's held down doesn't make
    // one at every step.
    ScriptedTexture? Copy(SuitPart part, Texture2D picture, GameObject.FLinearColor tint, long now)
    {
        if (!Same(tint, part.WantTint))
        {
            part.WantTint = tint;
            part.WantSince = now;
        }

        if (part.Next is null && part.Copy is { IsValid: true } && Same(tint, part.CopyTint))
        {
            part.CopyPending = false;
            return part.Copy;
        }

        if (part.Next is { IsValid: true } && !part.NextDrawn)
        {
            // Still being drawn, so it's aimed at the newest tint instead of making another.
            part.NextTint = tint;
        }
        else if (part.Next is not { IsValid: true } || !Same(tint, part.NextTint))
        {
            if (part.Copy is { IsValid: true } shown && Seconds(now - part.WantSince) < TintSettleSeconds)
            {
                part.CopyPending = true;
                return shown;
            }

            Scripted.Drop(part.Next);
            var width = Math.Min(Math.Max(1, picture.SizeX), LargestCopy);
            var height = Math.Max(1, (int)((long)Math.Max(1, picture.SizeY) * width / Math.Max(1, picture.SizeX)));
            part.Next = Scripted.Make(width, height, canvas => DrawCopy(part, picture, canvas));
            part.NextTint = tint;
            part.NextDrawn = false;
            part.NextAsked = now;
            if (part.Next is null)
            {
                Log.Warn($"{_spec.Prefix}.paint.copy", $"A tinted copy of {part.Label} couldn't be made.");
                part.Picture = null;
                return null;
            }

            picture.SetForceMipLevelsToBeResident((float)SharpWaitSeconds + 1, 0);
        }

        part.CopyPending = false;
        if (part.NextDrawn)
        {
            return part.Next;
        }

        var sharp = picture.ResidentMips >= picture.RequestedMips && picture.ResidentMips >= Math.Max(1, picture.Mips.ArrayNum);
        if (Viewer.Ready(picture) && (sharp || Seconds(now - part.NextAsked) >= SharpWaitSeconds))
        {
            part.Next!.bNeedsUpdate = true;
        }

        return null;
    }

    void DrawCopy(SuitPart part, Texture2D picture, Canvas canvas)
    {
        if (!Viewer.Ready(picture))
        {
            return;
        }

        canvas.SetPos(0, 0, 0);
        canvas.DrawTile(picture, canvas.ClipX, canvas.ClipY, 0, 0, picture.SizeX, picture.SizeY, part.NextTint, false, EngineTypes.EBlendMode.BLEND_Opaque);
        part.NextDrawn = true;
    }

    // What the picture is multiplied by. A colour is scaled to the brightness of white, so a dark
    // blue doesn't darken the part more than a yellow does.
    GameObject.FLinearColor Tint((float R, float G, float B) colour, bool own, float strength, float bright)
    {
        var (r, g, b) = (1f, 1f, 1f);
        if (!own)
        {
            var light = 0.2126f * colour.R + 0.7152f * colour.G + 0.0722f * colour.B;
            var scale = light < 0.02f ? 1f : 1f / light;
            r = 1 + (Math.Min(colour.R * scale, 8) - 1) * strength;
            g = 1 + (Math.Min(colour.G * scale, 8) - 1) * strength;
            b = 1 + (Math.Min(colour.B * scale, 8) - 1) * strength;
        }

        return new GameObject.FLinearColor { R = r * bright, G = g * bright, B = b * bright, A = 1 };
    }

    // Brightest a glow is allowed to get. Past this the screen's bloom turns every colour white.
    const float GlowCeiling = 2.5f;

    // Glows that start dark, like clear glass or eyes, are given something to scale.
    const float GlowFloor = 0.3f;

    // A glow takes the colour's hue. Brightness above 1 grows slowly, and the result stops
    // short of the level where everything washes out to white.
    GameObject.FLinearColor Mix(GameObject.FLinearColor was, (float R, float G, float B) colour, bool own, float strength, float bright)
    {
        var (r, g, b) = (was.R, was.G, was.B);
        var level = Math.Max(was.R, Math.Max(was.G, was.B));
        if (!own)
        {
            var peak = Math.Max(colour.R, Math.Max(colour.G, colour.B));
            var scale = peak > 0 ? Math.Max(level, GlowFloor) / peak : 0;
            r += (colour.R * scale - r) * strength;
            g += (colour.G * scale - g) * strength;
            b += (colour.B * scale - b) * strength;
        }

        var factor = bright <= 1f ? bright : 1f + (bright - 1f) * 0.5f;
        var top = Math.Max(r, Math.Max(g, b)) * factor;
        if (top > GlowCeiling)
        {
            factor *= GlowCeiling / top;
        }

        return new GameObject.FLinearColor { R = r * factor, G = g * factor, B = b * factor, A = was.A };
    }

    // The SDK's colour struct keeps its channels in raw memory, so its own == finds every two
    // colours equal.
    bool Same(GameObject.FLinearColor a, GameObject.FLinearColor b) => a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;

    float Linear(byte channel) => MathF.Pow(channel / 255f, 2.2f);

    double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

    // One line per key, like "cloth=red,80,1.5;visor=cyan,100,2".
    void Load()
    {
        Looks.Clear();
        if (_key.Length == 0 || Settings.Kept(_spec.Store, _key) is not string text)
        {
            return;
        }

        foreach (var entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = entry.Split('=', 2);
            if (pair.Length == 2 && SuitLook.TryParse(pair[1], out var look) && !look.IsPlain)
            {
                Looks[pair[0].Trim()] = look;
            }
        }
    }

    void Save()
    {
        if (_key.Length == 0)
        {
            return;
        }

        var text = new StringBuilder();
        foreach (var (key, look) in Looks)
        {
            if (text.Length > 0)
            {
                text.Append(';');
            }

            text.Append(key).Append('=').Append(look);
        }

        Settings.Set(_spec.Store, _key, text.ToString());
    }
}
