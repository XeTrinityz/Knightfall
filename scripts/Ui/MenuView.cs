#nullable enable

namespace Knightfall;

// What a theme is given to draw. Themes read it and never change it.
sealed class MenuView
{
    public IReadOnlyList<Catalog.Page> Pages = Array.Empty<Catalog.Page>();
    public IReadOnlyList<GuideLine> Hints = Array.Empty<GuideLine>();
    public int PageIndex;
    public int TabIndex;
    public int RowIndex;

    // 1 when fully open and 0 when gone. It eases up on open and down on close.
    public float Opened;

    // True while the menu is on its way out.
    public bool Closing;

    // The selected row as a number that slides towards RowIndex, so a highlight can glide.
    public float Glide;

    // Seconds since the rows on show last changed: the menu opened, or a tab or page turned.
    public float Arrived;

    // Seconds on a clock that keeps running while the menu is shut. For things that loop.
    public float Time;

    // A Stopwatch timestamp for this frame.
    public long Now;

    // Keys for the page and tab buttons, named for the device last used.
    public string PagePrevKey = "";
    public string PageNextKey = "";
    public string TabPrevKey = "";
    public string TabNextKey = "";

    public Catalog.Page Page => Pages[PageIndex];

    public Catalog.Tab Tab => Page.Tabs[TabIndex];

    public IReadOnlyList<Feature> Rows => Tab.Rows;

    // The rows with their section headings, as they are drawn.
    public IReadOnlyList<Catalog.Line> Lines => Tab.Lines;

    // Glide measured in lines, so a highlight slides over a heading between two rows.
    public float GlideLine
    {
        get
        {
            if (Rows.Count == 0)
            {
                return 0f;
            }

            var glide = Math.Clamp(Glide, 0f, Rows.Count - 1);
            var low = (int)MathF.Floor(glide);
            var high = Math.Min(low + 1, Rows.Count - 1);
            var from = Tab.LineOf(low);
            return from + (Tab.LineOf(high) - from) * (glide - low);
        }
    }

    public Feature? Focus => RowIndex < Rows.Count ? Rows[RowIndex] : null;

    // The question on screen over the menu, or null.
    public ModalView? Modal;
}

// A question over the menu, as a theme draws it.
sealed class ModalView
{
    public ModalView(Confirmation ask)
    {
        Ask = ask;
    }

    public Confirmation Ask { get; }

    // 0 for the yes button, 1 for no.
    public int Choice;

    // 1 when fully open and 0 when gone. It eases up on open and down on close.
    public float Opened;

    // True once it has been answered and is on its way out.
    public bool Leaving;

    public IReadOnlyList<GuideLine> Hints = Array.Empty<GuideLine>();
}

// A screen that takes the menu's place and keys while it's up. Back takes it down again.
abstract class Overlay
{
    // Every frame it's up, in place of the menu's own keys.
    public abstract void Keys();

    public abstract void Render(MenuView view);

    public virtual void Shown()
    {
    }

    // Once when it goes, however it goes.
    public virtual void Gone()
    {
    }
}
