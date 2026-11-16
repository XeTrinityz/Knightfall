#nullable enable

using System.Diagnostics;
using BmSDK;

namespace Knightfall;

// A villain's page in the case file: his photo, the key of his line among the game's character
// trophies, and either his page in the game's Bios or what the file holds back about him.
sealed record Dossier(Art? Photo, string Caption, string? Bio = null, Withheld? Withheld = null);

// For a villain with no Bios page. Freeze and Croc came with Season of Infamy, which added none.
// The file gives his real name, stamps his photo, blacks out the rest and keeps one note from the
// game's own stories about him. With no photo at all, InTheDark draws something of his own there.
sealed record Withheld(string RealName, string Stamp, string Note, Withheld.Dark? InTheDark = null)
{
    public delegate void Dark(float x, float y, float size, Rgba behind, float fade, float time);
}

// The case file beside the menu while the cursor is on one villain's rows. The photos are the
// portraits on the pause menu's Bios screen and the words are the game's own, so they follow the
// game's language.
static class CaseFile
{
    const string BiosAtlas = "Backscreen_Bios.Screen_Bios_I1";

    // The Bios portraits sit in a grid of 130 texel cells. A texel is left off each edge so the
    // next portrait can't bleed in.
    const float Cell = 130f;
    const float GridTop = 826f;

    const float Width = 470f;
    const float Inset = 14f;
    const float NameTop = 26f;
    const float CaptionTop = 60f;
    const float CaptionLine = 14f;
    const float RuleTop = 94f;
    const float PhotoTop = 106f;
    const float PhotoSize = 200f;
    const float FieldsLeft = Inset + PhotoSize + 16f;
    const float TraitsRule = 322f;
    const float TraitsTop = TraitsRule + 32f;
    const float TraitLeft = Inset + 14f;
    const float TraitWidth = Width - Inset - TraitLeft;
    const float TraitLine = 16f;
    const float TraitGap = 6f;
    const float NoteHead = 18f;
    const float BarHeight = 12f;
    const float StatusHeight = 34f;
    const int MostTraits = 3;
    const int MostLines = 2;

    const float SlideSeconds = 0.22f;
    const float Slide = 18f;
    const float WipeSeconds = 0.32f;

    static readonly Rgba Shade = new(5, 8, 11);
    static readonly Rgba ScanLine = new(0, 0, 0, 56);
    static readonly Rgba Beam = new(200, 244, 255);
    static readonly Rgba Bar = new(60, 66, 74);
    static readonly Rgba StampRed = new(208, 64, 50);

    public static Art Portrait(int column, int row) => new(BiosAtlas, column * Cell + 1f, GridTop + row * Cell + 1f, Cell - 2f, Cell - 2f);

    // Freeze's snowflake, one of the case photos beside the portraits.
    public static readonly Art FreezeEmblem = new(BiosAtlas, 1542f, 567f, 128f, 128f);

    // A null value or trait is blacked out.
    sealed record Page(int Seed, string Count, string Caption, (string Label, string? Value)[] Fields, string?[] Traits, string? Note, string? Stamp);

    // Built the first time a villain is shown, since the text is read from the game.
    static readonly Dictionary<int, Page> Pages = new();

    static Villain? _shown;
    static long _enteredAt;
    static long _changedAt;
    static long _lastFrame;
    static bool _wipe;
    static float _height;

    // Drawn after the menu. It shows while the cursor is on a villain's rows.
    public static void Beside(MenuView view)
    {
        if (view.Pages.Count == 0 || view.Page.Tabs.Count == 0 || Villains.Owning(view.Focus) is not { } owner
            || owner.Villain.Dossier is not { } dossier)
        {
            _shown = null;
            return;
        }

        var now = view.Now;
        var page = PageOf(owner.Index, dossier);
        var tall = Tall(page);
        if (!ReferenceEquals(owner.Villain, _shown))
        {
            // Coming in from another row slides the file in. Moving on to the next villain wipes
            // his photo in instead.
            _wipe = _shown is not null;
            if (_shown is null)
            {
                _enteredAt = now;
                _height = tall;
            }

            _changedAt = now;
            _shown = owner.Villain;
        }
        else
        {
            // The page grows or shrinks to fit the next villain's over a moment.
            var frame = MathF.Min(Seconds(now - _lastFrame), 0.1f);
            _height += (tall - _height) * (1f - MathF.Exp(-frame * 14f));
        }

        _lastFrame = now;
        var height = _height;
        var arrived = Kit.EaseOut(MathF.Min(view.Arrived, Seconds(now - _enteredAt)) / SlideSeconds);
        if (!Viewer.Place(Width, height, Slide * (1f - arrived)))
        {
            return;
        }

        var since = Seconds(now - _changedAt);
        var fade = view.Opened * arrived;
        var accent = Viewer.Accent;

        Draw.Fade(fade);
        Kit.Shadow(0f, 0f, Width, height, 0f, 0f);
        Draw.Rect(0f, 0f, Width, height, Viewer.Panel);
        Draw.Frame(0f, 0f, Width, height, 1f, accent with { A = 45 });
        Draw.Rect(0f, 0f, Width, 1f, accent with { A = 130 });
        Kit.Corners(-2f, -2f, Width + 4f, height + 4f, 8f, accent);

        Head(owner.Villain.Option.Plain, page, since, fade, accent);
        Photo(dossier, page, since, fade, view.Time, accent);
        Fields(page, since, fade);
        Traits(page, since, fade, accent);
        Status(owner.Villain.Option, height, view.Time, accent);
        Draw.Fade(1f);
    }

    static float Seconds(long ticks) => (float)(ticks / (double)Stopwatch.Frequency);

    // How tall the page is for this villain.
    static float Tall(Page page)
    {
        var top = TraitsTop;
        foreach (var trait in page.Traits)
        {
            top += LinesOf(trait) * TraitLine + TraitGap;
        }

        if (page.Note is { } note)
        {
            top += NoteHead + Math.Min(MostLines, Draw.Wrap(note, 12f, Width - 2f * Inset).Length) * TraitLine;
        }

        return top + 8f + StatusHeight;
    }

    static int LinesOf(string? trait) => trait is null ? 1 : Math.Min(MostLines, Draw.Wrap(trait, 12f, TraitWidth).Length);

    // Blacked out lines are about as long as words would be, and the same each time the file
    // comes up.
    static float Redaction(int seed, int slot)
    {
        var hash = (uint)(seed * 73856093 ^ slot * 19349663);
        hash ^= hash >> 13;
        hash *= 0x5bd1e995u;
        hash ^= hash >> 15;
        return 0.3f + 0.6f * (hash & 1023) / 1023f;
    }

    // Each part of the page steps in a moment after the one before it. Returns how far it has
    // come in, and fades it to match.
    static float Step(float since, int index, float fade)
    {
        var step = Kit.Cascade(since, index);
        Draw.Fade(fade * step);
        return step;
    }

    static void Head(string name, Page page, float since, float fade, Rgba accent)
    {
        Draw.Tracked("CASE FILE", Inset, 10f, 9f, 2.5f, accent);
        Draw.TextRight(page.Count, Width - Inset, 9f, 11f, Viewer.Dim);

        var x = Inset - 6f * (1f - Step(since, 0, fade));
        if (!Draw.TrackedHead(name, x, NameTop, 26f, 4f, Viewer.White, Width - Inset))
        {
            Draw.Text(name, x, NameTop, 26f, Viewer.White);
        }

        var lines = Draw.Wrap(page.Caption, 12f, Width - 2f * Inset);
        for (var i = 0; i < Math.Min(2, lines.Length); i++)
        {
            Draw.Text(lines[i], x, CaptionTop + i * CaptionLine, 12f, Viewer.Dim);
        }

        Draw.Fade(fade);
        Draw.Rect(Inset, RuleTop, Width - 2f * Inset, 1f, accent with { A = 60 });
    }

    static void Photo(Dossier dossier, Page page, float since, float fade, float time, Rgba accent)
    {
        const float x = Inset;
        const float y = PhotoTop;
        const float size = PhotoSize;

        Draw.Rect(x, y, size, size, Shade);
        var shown = true;
        if (dossier.Photo is { } photo)
        {
            shown = photo.At(x, y, size, size, Viewer.White);
        }
        else if (dossier.Withheld?.InTheDark is { } dark)
        {
            dark(x, y, size, Shade, fade, time);
        }
        else
        {
            NoPhoto(x, y, size);
            shown = false;
        }

        if (shown)
        {
            for (var line = y + 1f; line < y + size; line += 3f)
            {
                Draw.Rect(x, line, size, 1f, ScanLine);
            }
        }

        if (page.Stamp is { } stamp)
        {
            Stamp(stamp, x, y, size);
        }

        if (_wipe && since < WipeSeconds)
        {
            var edge = y + size * Kit.EaseOut(since / WipeSeconds);
            Draw.Rect(x, edge, size, y + size - edge, Shade);
            Draw.Rect(x - 6f, edge - 3f, size + 12f, 6f, accent with { A = 50 });
            Draw.Rect(x - 6f, edge - 1f, size + 12f, 2f, Beam);
        }

        Kit.Corners(x - 4f, y - 4f, size + 8f, size + 8f, 10f, accent);
    }

    static void NoPhoto(float x, float y, float size)
    {
        Draw.Hatch(x, y, size, size, 12f, Viewer.Amber with { A = 40 });
        const string big = "NO PHOTO";
        const string small = "ON FILE";
        var bigWidth = Draw.TrackedWidth(big, 18f, 4f);
        var smallWidth = Draw.TrackedWidth(small, 9f, 3f);
        Draw.Tracked(big, x + (size - bigWidth) / 2f, y + size / 2f - 16f, 18f, 4f, Viewer.Pale);
        Draw.Tracked(small, x + (size - smallWidth) / 2f, y + size / 2f + 8f, 9f, 3f, Viewer.Amber);
    }

    // An ink stamp across the bottom of the photo, in a double frame.
    static void Stamp(string text, float x, float y, float size)
    {
        const float textSize = 17f;
        const float spacing = 5f;
        const float tall = 34f;
        var wide = Draw.TrackedWidth(text, textSize, spacing) + 28f;
        var left = x + (size - wide) / 2f;
        var top = y + size - tall - 22f;
        Draw.Rect(left, top, wide, tall, Shade with { A = 170 });
        Draw.Frame(left, top, wide, tall, 2f, StampRed);
        Draw.Frame(left + 4f, top + 4f, wide - 8f, tall - 8f, 1f, StampRed with { A = 150 });
        Draw.Tracked(text, left + 14f, top + (tall - textSize) / 2f, textSize, spacing, StampRed);
    }

    static void Fields(Page page, float since, float fade)
    {
        const float width = Width - Inset - FieldsLeft;
        var top = PhotoTop + 2f;
        for (var i = 0; i < page.Fields.Length; i++)
        {
            var (label, value) = page.Fields[i];
            var lines = value is null ? Array.Empty<string>() : Draw.Wrap(value, 13f, width);
            var shown = value is null ? 1 : Math.Min(2, lines.Length);
            if (top + 12f + shown * 15f > TraitsRule - 4f)
            {
                break;
            }

            var x = FieldsLeft - 6f * (1f - Step(since, i + 1, fade));
            Draw.Tracked(label, x, top, 8f, 2f, Viewer.Dim);
            if (value is null)
            {
                Draw.Rect(x, top + 13f, width * Redaction(page.Seed, i), BarHeight, Bar);
            }

            for (var j = 0; j < shown && j < lines.Length; j++)
            {
                Draw.Text(lines[j], x, top + 12f + j * 15f, 13f, Viewer.Pale);
            }

            top += 12f + shown * 15f + 9f;
        }

        Draw.Fade(fade);
    }

    static void Traits(Page page, float since, float fade, Rgba accent)
    {
        Draw.Rect(Inset, TraitsRule, Width - 2f * Inset, 1f, accent with { A = 60 });
        Draw.Tracked("KNOWN TRAITS", Inset, TraitsRule + 12f, 8f, 2.5f, Viewer.Dim);

        var top = TraitsTop;
        for (var i = 0; i < page.Traits.Length; i++)
        {
            var x = -6f * (1f - Step(since, 6 + i, fade));
            Draw.Rect(Inset + x, top + 5f, 5f, 5f, accent);
            if (page.Traits[i] is not { } trait)
            {
                Draw.Rect(TraitLeft + x, top + 2f, TraitWidth * Redaction(page.Seed, 10 + i), BarHeight, Bar);
                top += TraitLine + TraitGap;
                continue;
            }

            var lines = Draw.Wrap(trait, 12f, TraitWidth);
            for (var j = 0; j < Math.Min(MostLines, lines.Length); j++)
            {
                Draw.Text(lines[j], TraitLeft + x, top, 12f, Viewer.Pale);
                top += TraitLine;
            }

            top += TraitGap;
        }

        if (page.Note is { } note)
        {
            var x = -6f * (1f - Step(since, 6 + page.Traits.Length, fade));
            Draw.Tracked("FILE NOTE", Inset + x, top + 4f, 8f, 2.5f, Viewer.Amber);
            top += NoteHead;
            var lines = Draw.Wrap(note, 12f, Width - 2f * Inset);
            for (var j = 0; j < Math.Min(MostLines, lines.Length); j++)
            {
                Draw.Text(lines[j], Inset + x, top, 12f, Viewer.Pale);
                top += TraitLine;
            }
        }

        Draw.Fade(fade);
    }

    static void Status(Toggle option, float height, float time, Rgba accent)
    {
        var y = height - StatusHeight;
        var middle = y + StatusHeight / 2f;
        Draw.Rect(Inset, y, Width - 2f * Inset, 1f, accent with { A = 60 });

        if (option.Blocked is { } why)
        {
            var width = Kit.Padlock(Inset, middle - 5f, 10f, Viewer.Amber);
            Draw.Tracked(why, Inset + width + 8f, middle - 4.5f, 9f, 2.5f, Viewer.Amber);
            return;
        }

        if (option.On)
        {
            var glow = 0.6f + 0.4f * MathF.Sin(time * 4.5f);
            Draw.Rect(Inset, middle - 3f, 6f, 6f, accent with { A = (byte)(255f * glow) });
            Draw.Tracked("ACTIVE", Inset + 14f, middle - 4.5f, 9f, 2.5f, accent);
            return;
        }

        Draw.Rect(Inset, middle - 3f, 6f, 6f, Viewer.Ghost);
        Draw.Tracked("STANDBY", Inset + 14f, middle - 4.5f, 9f, 2.5f, Viewer.Dim);
    }

    static Page PageOf(int index, Dossier dossier)
    {
        if (Pages.TryGetValue(index, out var known))
        {
            return known;
        }

        var fields = new List<(string Label, string? Value)>();
        var traits = new List<string?>();
        string? note = null;
        string? stamp = null;
        if (dossier.Bio is { } bio)
        {
            Field(fields, "REAL NAME", bio, "02_REALNAME");
            Field(fields, "OCCUPATION", bio, "03_OCCUPATION");
            Field(fields, "HEIGHT", bio, "07_HEIGHT");
            Field(fields, "WEIGHT", bio, "08_WEIGHT");
            Field(fields, "FIRST SEEN", bio, "09_FIRST");

            // Some pages list an empty trait, so the first few that have words are taken.
            for (var n = 1; n <= 9 && traits.Count < MostTraits; n++)
            {
                if (Localized(bio, $"ATTRIB_{n:00}", "Bios") is { } trait)
                {
                    traits.Add(trait);
                }
            }
        }
        else if (dossier.Withheld is { } held)
        {
            fields.Add(("REAL NAME", held.RealName));
            fields.Add(("OCCUPATION", null));
            fields.Add(("HEIGHT", null));
            fields.Add(("WEIGHT", null));
            fields.Add(("FIRST SEEN", null));
            for (var n = 0; n < MostTraits; n++)
            {
                traits.Add(null);
            }

            note = held.Note;
            stamp = held.Stamp;
        }

        var page = new Page(index, $"{Kit.Number(index + 1)} / {Kit.Number(Villains.Count)}", Localized("Showcase", dossier.Caption, "GFxUI") ?? "",
            fields.ToArray(), traits.ToArray(), note, stamp);
        Pages[index] = page;
        return page;
    }

    static void Field(List<(string Label, string? Value)> fields, string label, string bio, string key)
    {
        if (Localized(bio, key, "Bios") is { } value)
        {
            fields.Add((label, value));
        }
    }

    // The game's own text. A key it hasn't got comes back wrapped in question marks. Line breaks
    // come through as \n, and the game's fonts have no curly apostrophe.
    static string? Localized(string section, string key, string package)
    {
        var text = GameObject.Localize(section, key, package).ToString();
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith('?'))
        {
            return null;
        }

        text = text.Replace("\\n", " ").Replace('\n', ' ').Replace('’', '\'');
        while (text.Contains("  "))
        {
            text = text.Replace("  ", " ");
        }

        return text.Trim();
    }
}
