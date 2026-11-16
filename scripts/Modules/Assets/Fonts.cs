#nullable enable

using BmSDK;
using BmSDK.Engine;

namespace Knightfall;

// ASSETS > FONTS. The fonts the game has loaded, each with a line written in it and the pages of
// letters it's drawn from.
static class Fonts
{
    static readonly (string Id, string Label)[] NotInGame = { ("", "OPEN THE MENU IN GAME") };
    static readonly (string Id, string Label)[] Nothing = { ("", "NONE LOADED") };
    static readonly (string Id, string Label)[] NoPages = { ("", "NO PAGES") };

    static readonly (string Id, string Label, string Text)[] Lines =
    {
        ("caps", "CAPITALS", "ABCDEFGHIJKLMNOPQRSTUVWXYZ"),
        ("small", "SMALL LETTERS", "abcdefghijklmnopqrstuvwxyz"),
        ("numbers", "NUMBERS", "0123456789 !?&%+-=/()"),
        ("words", "WORDS", "Gotham City Police Department"),
    };

    static readonly List<Font> Found = new();
    static int _epoch = -1;
    static bool _stale = true;
    static bool _shown;
    static int _listedFont = -1;

    // The picked font's path and pages, read once when it's picked.
    static string _path = "";
    static Textures.Entry[] _pages = Array.Empty<Textures.Entry>();

    // The picked font's pages, for the light table. Turning goes to the next font.
    static readonly Shelf Pages = new(
        Count: static () => _pages.Length,
        Index: PageIndex,
        At: static index => index >= 0 && index < _pages.Length ? _pages[index] : null,
        Pick: PageAt,
        Turn: NextFont,
        Title: static () => _path.Length > 0 ? Textures.Name(_path) : "");

    public static readonly Choice Pick = new(
        "assets.fonts.font", "FONT",
        "A font the game has loaded. The number is how many pages of letters it has.",
        NotInGame, saved: false)
    {
        Block = static () => Picked() is null ? Empty() : null,
    };

    public static readonly Choice Page = new(
        "assets.fonts.page", "PAGE",
        "One page of the font's letters. Select opens it on the light table.",
        NoPages, saved: false)
    {
        Pressed = static () => LightTable.Open(Pages),
        PressName = "LIGHT TABLE",
        Block = static () => PageTexture() is null ? Picked() is null ? Empty() : NoPages[0].Label : null,
    };

    public static readonly Choice Sample = new(
        "assets.fonts.sample", "SAMPLE",
        "What the viewer writes in the font.",
        Lines.Select(line => (line.Id, line.Label)).ToArray())
    {
        Preference = true,
    };

    public static readonly Command Copy = new(
        "assets.fonts.copy", "COPY PATH",
        "Puts the font's full path on the clipboard.",
        static () => Picked() is { } font
            ? Clipboard.Put(_path) ? Outcome.Done("COPIED") : Outcome.Failed("CLIPBOARD BUSY")
            : Outcome.Failed("NOTHING PICKED"))
    {
        Cap = "COPY",
        Block = static () => Picked() is null ? "NOTHING PICKED" : null,
    };

    public static readonly Command Refresh = new(
        "assets.fonts.refresh", "REFRESH",
        "Reads which fonts are loaded again.",
        static () =>
        {
            List();
            return Found.Count == 0 ? Outcome.Unchanged("NONE LOADED") : Outcome.Done($"{Found.Count} FONTS");
        })
    {
        Block = static () => World.Info is null ? "NOT IN GAME" : null,
    };

    public static readonly Catalog.Section[] Sections =
    {
        new("FIND", Pick, Page, Sample),
        new("VIEW", Viewer.Channels, Viewer.Backdrop) { Borrowed = true },
        new("USE", Copy, Refresh),
    };

    public static void Tick()
    {
        if (_epoch != World.Epoch)
        {
            _epoch = World.Epoch;
            Found.Clear();
            _stale = true;
            Pick.SetOptions(NotInGame, 0);
            ListPages();
        }

        var shown = Menu.Shows(Pick);
        if (shown && World.Info is not null && (_stale || !_shown))
        {
            List();
        }

        _shown = shown;
        if (!_stale && _listedFont != Pick.Index)
        {
            ListPages();
        }
    }

    static void List()
    {
        var keep = Picked();
        _stale = false;
        Found.Clear();
        foreach (var font in Game.FindObjects<Font>())
        {
            if (font.IsValid && !font.IsClassDefaultObject)
            {
                Found.Add(font);
            }
        }

        Found.Sort(static (a, b) => string.Compare(a.GetPathName(), b.GetPathName(), StringComparison.OrdinalIgnoreCase));
        if (Found.Count == 0)
        {
            Pick.SetOptions(World.Info is null ? NotInGame : Nothing, 0);
            ListPages();
            return;
        }

        var index = keep is null ? 0 : Math.Max(0, Found.IndexOf(keep));
        Pick.SetOptions(Found.Select((font, i) => (i.ToString(), $"{Textures.Name(font.GetPathName())}  ({font.Textures.Count})")).ToArray(), index);
        ListPages();
    }

    static int PageIndex() => Page.Index;

    static void PageAt(int index) => Page.SetTo(index);

    static void NextFont(int direction)
    {
        Pick.Step(direction);
        ListPages();
    }

    static string Empty() => World.Info is null ? NotInGame[0].Label : Nothing[0].Label;

    static void ListPages()
    {
        _listedFont = Pick.Index;
        var picked = Picked();
        _path = picked?.GetPathName() ?? "";
        _pages = picked is null
            ? Array.Empty<Textures.Entry>()
            : picked.Textures.Where(static page => page is { IsValid: true }).Select(static page => new Textures.Entry(page, page.GetPathName(), Array.Empty<string>())).ToArray();
        var count = _pages.Length;
        Page.SetOptions(count == 0 ? NoPages : Enumerable.Range(0, count).Select(i => (i.ToString(), $"PAGE {i + 1} OF {count}")).ToArray(), 0);
    }

    static Font? Picked() =>
        int.TryParse(Pick.CurrentId, out var index) && index >= 0 && index < Found.Count && Found[index] is { IsValid: true } font ? font : null;

    static Texture? PageTexture() =>
        Picked() is not null && int.TryParse(Page.CurrentId, out var index) && index >= 0 && index < _pages.Length
        && _pages[index].Picture is { IsValid: true } page
            ? page
            : null;

    const float Inset = 14f;
    const float Top = 52f;
    const float LinesHeight = 112f;
    const float BoxTop = Top + LinesHeight + 10f;
    const float BoxHeight = 250f;
    const float FactsTop = BoxTop + BoxHeight + 14f;

    public const float PanelHeight = FactsTop + 18f * 2f + 52f;

    // The viewer's panel on this tab.
    public static void Panel(MenuView view, float width, Rgba accent)
    {
        var font = Picked();
        var name = font is not null ? Textures.Name(_path) : "NOTHING LISTED";
        Viewer.Head("FONT", Found.Count > 0 ? $"{Pick.Index + 1} / {Found.Count}" : "", name, width, accent);

        var boxWidth = width - 2f * Inset;
        Draw.Rect(Inset, Top, boxWidth, LinesHeight, new Rgba(0, 0, 0, 200));
        var page = PageTexture();
        if (font is not null && _pages.Length > 0 && !Viewer.Rested)
        {
            Draw.Tracked("LOADING", Inset + 10f, Top + 10f, 10f, 2f, Viewer.Dim);
        }
        else if (font is { } shown && _pages.Length > 0 && Array.TrueForAll(_pages, static page => Viewer.Ready(page.Picture)))
        {
            var text = Lines[Math.Clamp(Sample.Index, 0, Lines.Length - 1)].Text;
            var (wide, tall) = Draw.SampleSize(shown, text);
            if (wide > 0f && tall > 0f)
            {
                // Its own size first, then as big as fits.
                var own = 1f / (Draw.Zoom * Draw.Scale);
                Draw.Tracked("1:1", Inset + 8f, Top + 8f, 8f, 1.5f, Viewer.Ghost);
                Draw.Sample(shown, text, Inset + 34f, Top + 6f, MathF.Min(own, (boxWidth - 44f) / wide), Viewer.Pale);
                var big = MathF.Min(52f / tall, (boxWidth - 20f) / wide);
                Draw.Sample(shown, text, Inset + 10f, Top + LinesHeight - 10f - tall * big, big, Viewer.White);
            }
        }
        else if (font is not null)
        {
            Draw.Tracked("NOTHING TO DRAW WITH", Inset + 10f, Top + 10f, 10f, 2f, Viewer.Dim);
        }

        Viewer.Shown(page, Inset, BoxTop, boxWidth, BoxHeight, false, view.Time, main: page is not null, accent);
        Kit.Corners(Inset - 1f, BoxTop - 1f, boxWidth + 2f, BoxHeight + 2f, 12f, accent);

        if (font is not { } picked)
        {
            return;
        }

        var half = (width - 2f * Inset - 24f) / 2f;
        var second = Inset + half + 24f;
        Viewer.Fact("PAGES", picked.Textures.Count.ToString(), Inset, FactsTop, half, Viewer.Pale);
        Viewer.Fact("LETTERS", picked.Characters.Count.ToString(), second, FactsTop, half, Viewer.Pale);
        if (page is not null)
        {
            var facts = Facts.Of(page);
            Viewer.Fact("PAGE SIZE", facts.Size, Inset, FactsTop + 18f, half, Viewer.Pale);
            Viewer.Fact("FORMAT", facts.Format, second, FactsTop + 18f, half, Viewer.Pale);
        }

        Viewer.Foot(_path, width, PanelHeight);
    }
}
