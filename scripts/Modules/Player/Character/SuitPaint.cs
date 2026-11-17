#nullable enable

using System.Diagnostics;
using System.Text;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// PLAYER > SUIT, the PAINT rows. Glowing bits like a visor have colour settings of their own.
// Cloth, leather and plates only have a colour picture, so those get a tinted copy of the
// picture drawn by the mod. One job owns every material the mod puts on the suit.
static class SuitPaint
{
    // Where each suit's colours are kept, one line per suit. Not under [player.suit], which holds
    // the SUIT PAINT switch itself.
    const string Store = "player.suit_colours";

    // A copy is a plain texture with no smaller versions, so very large pictures are drawn at
    // this size. It still costs 16 MB per painted part.
    const int LargestCopy = 2048;

    const double CheckSeconds = 0.5;

    // How long a part flashes when it's picked on PART.
    const double FlashSeconds = 0.25;

    // A new tinted copy waits until the tint has held still this long.
    const double TintSettleSeconds = 0.25;

    // The flash colour, bright enough to show on a black suit.
    static readonly GameObject.FLinearColor FlashColour = new() { R = 0.1f, G = 2.5f, B = 2.2f, A = 1 };

    // How long a copy waits for the game to stream its picture in fully before it's drawn anyway.
    const double SharpWaitSeconds = 3;

    // Slider steps closer together than this are one change for UNDO.
    const double UndoJoinSeconds = 1;

    const int UndoDepth = 30;

    const string AllParts = "*all";

    static readonly (string Id, string Label)[] PaletteRows = SuitLook.Palette.Select(swatch => (swatch.Id, swatch.Label)).ToArray();

    static readonly (string Id, string Label)[] NoParts = { ("", "NONE") };

    static List<SuitPart> Parts = new();
    static readonly Dictionary<string, SuitLook> Looks = new();
    static readonly HashSet<string> Marked = new();
    static readonly List<Dictionary<string, SuitLook>> Undo = new();
    static readonly Random Dice = new();
    static readonly HashSet<string> Flashing = new();
    static long _flashUntil;
    static ScriptedTexture? _flat;
    static bool _flatDrawn;
    static RPawnPlayer? _pawn;
    static string _suit = "";
    static long _checkedAt;
    static long _editedAt;
    static bool _dressed;
    static bool _scanning;
    static SuitLook? _copied;

    // The suit worn now, as its colours are kept.
    public static string Suit => _suit;

    // Anything is painted on the suit worn now.
    public static bool Painted => Looks.Count > 0;

    // Keeps every suit's colours and put-on look in settings.toml, not only the suit worn now.
    public static void KeepSettings()
    {
        Settings.KeepAll(Store);
        Settings.KeepAll(SuitLooks.PutOnStore);
    }

    // The grey that stands for the suit's own colour in the list, which isn't known.
    static readonly (double R, double G, double B) Stand = (43, 51, 61);

    // The colour each palette entry stands for, in the list. SUIT'S OWN is the stand-in grey. This
    // comes before the rows, which use it as they are made.
    static readonly Rgba[] Swatches = SuitLook.Palette
        .Select((swatch, i) => i == 0 ? new Rgba((byte)Stand.R, (byte)Stand.G, (byte)Stand.B) : new Rgba(swatch.R, swatch.G, swatch.B))
        .ToArray();

    public static readonly Toggle Paint = new(
        "player.suit.paint", "SUIT PAINT",
        "Wears the colours set below. Each suit keeps its own colours, so a change of suit brings back what you gave that one.");

    public static readonly Choice Which = new(
        "player.suit.part", "PART",
        "The part to colour. Press it to mark it, and every marked part takes the same colour. ALL PARTS does the whole suit. A part flashes once when you pick it, so you can tell which one it is.",
        NoParts, saved: false)
    {
        Opening = Refresh,
        Changed = Picked,
        Pressed = ToggleMark,
        PressName = "MARK",
        Block = NoPart,
        Preference = true,
    };

    public static readonly Choice Colour = new(
        "player.suit.colour", "COLOUR",
        "The colour for the picked parts. SUIT'S OWN keeps the suit's colour, so BRIGHTNESS can still lighten or darken it.",
        PaletteRows, saved: false)
    {
        Swatches = Swatches,
        Changed = Edited,
        Block = NoPart,
        Preference = true,
    };

    public static readonly Slider Strength = new(
        "player.suit.strength", "STRENGTH",
        "How much of the colour goes on. Lower keeps more of the suit's own colour showing through.",
        min: 0, max: 100, step: 5, value: 100, unit: "%", saved: false)
    {
        Tone = Tone,
        Changed = Edited,
        Block = NoPart,
        Preference = true,
    };

    public static readonly Slider Brightness = new(
        "player.suit.brightness", "BRIGHTNESS",
        "Lightens or darkens the picked parts. A dark suit needs this turned up for a colour to show well.",
        min: 0.2, max: 4, step: 0.1, value: 1, unit: "X", saved: false)
    {
        Tone = Tone,
        Changed = Edited,
        Block = NoPart,
        Preference = true,
    };

    public static readonly Command CopyLook = new(
        "player.suit.copy_look", "COPY LOOK",
        "Remembers the colour, strength and brightness of the part under PART.",
        CopyOne)
    {
        Block = () => NoPart() ?? (Which.CurrentId == AllParts ? "PICK ONE PART" : null),
    };

    public static readonly Command PasteLook = new(
        "player.suit.paste_look", "PASTE LOOK",
        "Puts the copied look on the picked parts.",
        PasteOne)
    {
        Block = () => NoPart() ?? (_copied is null ? "COPY ONE FIRST" : null),
    };

    public static readonly Command Shuffle = new(
        "player.suit.random", "RANDOM COLOURS",
        "Gives each picked part a random colour. Press again for another go.",
        Randomise)
    {
        Block = NoPart,
    };

    public static readonly Command Back = new(
        "player.suit.undo", "UNDO",
        "Takes back the last change to this suit's colours.",
        UndoOne)
    {
        Block = () => NoPart() ?? (Undo.Count == 0 ? "NOTHING TO UNDO" : null),
    };

    public static readonly Command Unmark = new(
        "player.suit.clear_marks", "CLEAR MARKS",
        "Unmarks every part, so changes go to the part under PART again.",
        ClearMarks)
    {
        Block = () => NoPart() ?? (Marked.Count == 0 ? "NONE MARKED" : null),
    };

    public static readonly Command ClearPart = new(
        "player.suit.clear_part", "CLEAR PART",
        "Puts the picked parts back to how the suit made them.",
        ClearPicked)
    {
        Block = () => NoPart() ?? (Looks.Count == 0 ? "NOTHING SET" : null),
    };

    public static readonly Command ClearSuit = new(
        "player.suit.clear_suit", "CLEAR SUIT",
        "Puts every part of this suit back to how the suit made it.",
        ClearAll)
    {
        Block = () => NoPart() ?? (Looks.Count == 0 ? "NOTHING SET" : null),
    };

    // The meters light in the picked colour. The suit's own colour keeps the theme's.
    static Rgba? Tone() => Colour.Index == 0 ? null : Swatches[Colour.Index];

    static string? NoPart() => World.Player is null ? "NO PLAYER" : Parts.Count == 0 ? "NOTHING TO PAINT" : null;

    static SuitPart? Current => Parts.Find(part => part.Key == Which.CurrentId);

    static SuitLook LookOf(SuitPart part) => Looks.TryGetValue(part.Key, out var look) ? look : SuitLook.Plain;

    // What a change goes to: every part for ALL PARTS, else the marked parts, else the one under
    // PART.
    static List<SuitPart> Targets()
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

    // Reads the suit again when it isn't the one the parts were read from. A suit can change while
    // the menu is open, and an edit then belongs to the new suit.
    static void Fresh()
    {
        if (!_scanning && World.Player is { } pawn && Stale(pawn))
        {
            Scan(pawn);
        }
    }

    // The menu is opening. Reads the suit again only when it changed.
    static void Refresh()
    {
        if (World.Player is { } pawn && Stale(pawn))
        {
            Scan(pawn);
            return;
        }

        List();
    }

    // Fills PART from the parts found, keeping the pick when that part is still there.
    static void List()
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
    static void Picked()
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

    static void ShowPart()
    {
        var look = Targets() is { Count: > 0 } targets ? LookOf(targets[0]) : SuitLook.Plain;
        Colour.SetOptions(PaletteRows, look.Colour);
        Strength.Show(look.Strength);
        Brightness.Show(look.Brightness);
    }

    static void ToggleMark()
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

    static Outcome ClearMarks()
    {
        Marked.Clear();
        List();
        return Outcome.Done("UNMARKED");
    }

    static void Edited()
    {
        var look = new SuitLook(Colour.Index, Strength.Value, Brightness.Value);
        Put(Targets(), _ => look, join: true);
    }

    // Changes the looks of these parts, kept for UNDO and saved.
    static int Put(List<SuitPart> parts, Func<SuitPart, SuitLook> look, bool join = false)
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

    static Outcome CopyOne()
    {
        if (Current is not { } part)
        {
            return Outcome.Failed("PICK ONE PART");
        }

        _copied = LookOf(part);
        return Outcome.Done("COPIED");
    }

    static Outcome PasteOne()
    {
        if (_copied is not { } look)
        {
            return Outcome.Failed("NOTHING COPIED");
        }

        var changed = Put(Targets(), _ => look);
        ShowPart();
        return changed == 0 ? Outcome.Unchanged("NO CHANGE") : Outcome.Done("PASTED");
    }

    static Outcome Randomise()
    {
        Put(Targets(), part => new SuitLook(Dice.Next(1, SuitLook.Palette.Length), 100, Math.Round(1 + Dice.NextDouble() * 1.5, 1)));
        ShowPart();
        return Outcome.Done("SHUFFLED");
    }

    static Outcome UndoOne()
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

    static Outcome ClearPicked()
    {
        if (Put(Targets(), _ => SuitLook.Plain) == 0)
        {
            return Outcome.Unchanged("NOTHING SET");
        }

        ShowPart();
        return Outcome.Done("CLEARED");
    }

    static Outcome ClearAll()
    {
        Fresh();
        if (Put(Parts, _ => SuitLook.Plain) == 0)
        {
            return Outcome.Unchanged("NOTHING SET");
        }

        ShowPart();
        return Outcome.Done("CLEARED");
    }

    // This suit's colours as they are now, for a saved look.
    public static Dictionary<string, SuitLook> Snapshot() => new(Looks);

    // Puts a saved look on in place of this suit's colours. Parts the look doesn't name go back
    // to the suit's own.
    public static int Wear(IReadOnlyDictionary<string, SuitLook> looks)
    {
        Fresh();
        Put(Parts, part => looks.TryGetValue(part.Key, out var look) ? look : SuitLook.Plain);

        // Parts this suit doesn't have now are still kept, for when it shows them again.
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
    public static void Tick()
    {
        var now = Stopwatch.GetTimestamp();
        var flashing = now < _flashUntil;
        if (!Paint.On && !_dressed && !flashing)
        {
            return;
        }

        if (World.Player is not { } pawn)
        {
            return;
        }

        if (!ReferenceEquals(pawn, _pawn) || Seconds(now - _checkedAt) >= CheckSeconds)
        {
            _checkedAt = now;
            if (Stale(pawn))
            {
                Scan(pawn);
            }
        }

        _dressed = false;
        foreach (var part in Parts)
        {
            var look = Paint.On ? LookOf(part) : SuitLook.Plain;
            var lit = flashing && Flashing.Contains(part.Key);
            if (look != part.Worn || lit != part.Lit || part.Waiting)
            {
                Dress(part, look, lit, now);
            }

            _dressed |= part.Slots.Exists(slot => slot.Ours is not null);
        }
    }

    public static void Release()
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

    // Whether the parts list no longer matches what the player wears: a new body, a new suit,
    // battle damage or anything else that swapped a material.
    static bool Stale(RPawnPlayer pawn)
    {
        if (!ReferenceEquals(pawn, _pawn) || SuitParts.SuitKey(pawn) != _suit)
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

    static void Scan(RPawnPlayer pawn)
    {
        _scanning = true;
        try
        {
            foreach (var part in Parts)
            {
                Undress(part);
            }

            _pawn = pawn;
            var suit = SuitParts.SuitKey(pawn);
            Parts = SuitParts.Find(pawn);
            if (suit != _suit)
            {
                _suit = suit;
                Marked.Clear();
                Undo.Clear();
                Load();

                // The put-on look goes on when the suit is put on, which a reload of the mod isn't.
                if (suit.Length > 0 && Survive.Get(Survive.SuitWorn, "") != suit)
                {
                    Survive.Set(Survive.SuitWorn, suit);
                    if (SuitLooks.PutOnLooks(suit) is { } looks)
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
            }

            Marked.RemoveWhere(key => !Parts.Exists(part => part.Key == key));
            List();
            SuitLooks.Refresh();
        }
        finally
        {
            _scanning = false;
        }
    }

    // Puts the part's look on every place it's worn.
    static void Dress(SuitPart part, SuitLook look, bool lit, long now)
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
        // The suit's own colour at full brightness is the picture as it is, so no copy is made.
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
                // Always a new material over the suit's own. The game's CreateAndSet hands back the
                // material the mesh already has when that is a copy made for this body, which is
                // nearly all of them, and that would paint over the suit's own colours.
                ours = Made.Tracked(new MaterialInstanceConstant(slot.Mesh));
                if (ours is not { IsValid: true })
                {
                    Log.Warn("suit.paint.material", $"A colour couldn't be put on {part.Label}.");
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
    static ScriptedTexture? FlatTexture()
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

    static void Undress(SuitPart part)
    {
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
    // old one stays on the suit until the new one is ready, so the part never shows blank. A new
    // copy is only made once the tint has held still, so a slider that's held down doesn't make
    // one at every step.
    static ScriptedTexture? Copy(SuitPart part, Texture2D picture, GameObject.FLinearColor tint, long now)
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
                Log.Warn("suit.paint.copy", $"A tinted copy of {part.Label} couldn't be made.");
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

    static void DrawCopy(SuitPart part, Texture2D picture, Canvas canvas)
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
    // blue doesn't darken the suit more than a yellow does.
    static GameObject.FLinearColor Tint((float R, float G, float B) colour, bool own, float strength, float bright)
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
    static GameObject.FLinearColor Mix(GameObject.FLinearColor was, (float R, float G, float B) colour, bool own, float strength, float bright)
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
    static bool Same(GameObject.FLinearColor a, GameObject.FLinearColor b) => a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;

    static float Linear(byte channel) => MathF.Pow(channel / 255f, 2.2f);

    static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

    // One line per suit, like "cloth=red,80,1.5;visor=cyan,100,2".
    static void Load()
    {
        Looks.Clear();
        if (_suit.Length == 0 || Settings.Kept(Store, _suit) is not string text)
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

    static void Save()
    {
        if (_suit.Length == 0)
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

        Settings.Set(Store, _suit, text.ToString());
    }
}
