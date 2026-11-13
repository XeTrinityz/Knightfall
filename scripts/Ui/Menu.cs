#nullable enable

using System.Diagnostics;
using BmSDK.Engine;

namespace Knightfall;

// Where the player is in the menu, and what each button does there. It draws nothing itself.
static class Menu
{
    const float OpenSeconds = 0.2f;
    const float CloseSeconds = 0.14f;
    const float GlideRate = 22f;
    const double LoopSeconds = 3600;

    static readonly MenuView View = new();

    // What the player asked for. The menu still hides while the game is paused and comes back after.
    static bool _wanted;
    static bool _hintsBuilt;
    static bool _hintListening;
    static Device _hintDevice;
    static Pad.Style _hintStyle;
    static (string? Press, string? Bind, string? Favourite, bool Passing) _hintRow;
    static long _lastFrame;
    static long _arrivedAt;

    // The menu was last drawn on the gauntlet.
    static bool _onWrist;

    // Where the open and close animation is, 0 to 1, before easing.
    static float _progress;

    // The tab each page was last left on, so turning back to a page lands where you were.
    static int[] _tabOfPage = Array.Empty<int>();

    public static bool Showing { get; private set; }

    // What the menu shows this frame, for drawing it somewhere other than the screen.
    public static MenuView Current => View;

    // The page on screen, or null while the menu is closed.
    public static string? OpenPage => Showing && View.Pages.Count > 0 ? View.Page.Name : null;

    public static void Resume()
    {
        View.Pages = Catalog.Pages;
        _tabOfPage = new int[View.Pages.Count];
        View.PageIndex = Survive.Get(Survive.MenuPage, 0);
        View.TabIndex = Survive.Get(Survive.MenuTab, 0);
        View.RowIndex = Survive.Get(Survive.MenuRow, 0);
        Clamp();
        View.Glide = View.RowIndex;

        _wanted = Survive.Get(Survive.MenuOpen, false);
        if (_wanted)
        {
            Catalog.MenuOpening();
        }
    }

    public static void Update()
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = _lastFrame == 0 ? 0f : MathF.Min((float)((now - _lastFrame) / (double)Stopwatch.Frequency), 0.1f);
        _lastFrame = now;
        View.Now = now;
        View.Time = (float)(now / (double)Stopwatch.Frequency % LoopSeconds);

        // While a row waits for its key, the menu's own keys belong to that row.
        if (!World.Paused && Hotkeys.Listening is null)
        {
            if (Controls.Pressed(Act.Open))
            {
                Want(!_wanted);
            }
            else if (_wanted && Controls.Pressed(Act.Back))
            {
                Want(false);
            }
        }

        var show = _wanted && !World.Paused && View.Pages.Count > 0;
        if (show != Showing)
        {
            Showing = show;
            Controls.Capture(show);

            if (show)
            {
                View.Glide = View.RowIndex;
                Arrive();
            }
            else
            {
                // A row can only wait for a key while the menu is there to hear it. Left
                // waiting, it would keep every hotkey and the radial switched off.
                Hotkeys.StopListening();
            }
        }

        // The pause menu opens over a hidden menu, not over one that is still fading out.
        if (World.Paused)
        {
            _progress = 0f;
        }

        if (Showing)
        {
            // While a row waits for its hotkey, keys and buttons go to it, not the menu.
            if (Hotkeys.Listening is not null)
            {
                Hotkeys.Listen();
            }
            else
            {
                Navigate();
            }

            RefreshHints();
        }

        Animate(seconds);
    }

    public static void Render(Canvas canvas)
    {
        if (View.Opened <= 0f || View.Pages.Count == 0 || !Draw.Begin(canvas))
        {
            Theme.NoMenu();
            _onWrist = false;
            return;
        }

        try
        {
            // A menu that closes on the wrist fades out there, even once the arm is on its way down.
            if (Gauntlet.Hosting || (View.Closing && _onWrist))
            {
                _onWrist = true;
                Theme.NoMenu();
                GauntletScreen.Refresh();
                if (Gauntlet.Place(canvas, GauntletMenu.Width, out var height, out var shown))
                {
                    GauntletMenu.Render(View, height, shown * View.Opened);
                }

                return;
            }

            if (Gauntlet.Waiting)
            {
                Theme.NoMenu();
                return;
            }

            _onWrist = false;
            Theme.BeginMenu();
            Theme.Current.Render(View);
        }
        finally
        {
            Hologram.End();
            Draw.End();
        }
    }

    // Timed with a real clock, so it runs the same in slow motion and at any frame rate.
    static void Animate(float seconds)
    {
        if (Showing)
        {
            _progress = MathF.Min(1f, _progress + seconds / OpenSeconds);
            var left = 1f - _progress;
            View.Opened = 1f - left * left * left;
        }
        else
        {
            // Closing starts fast and settles, the reverse of opening.
            _progress = MathF.Max(0f, _progress - seconds / CloseSeconds);
            View.Opened = _progress * _progress;
        }

        View.Closing = !Showing;
        View.Arrived = (float)((View.Now - _arrivedAt) / (double)Stopwatch.Frequency);

        var gap = View.RowIndex - View.Glide;
        View.Glide = MathF.Abs(gap) < 0.01f ? View.RowIndex : View.Glide + gap * (1f - MathF.Exp(-GlideRate * seconds));
    }

    static void Navigate()
    {
        if (Controls.Pressed(Act.PagePrev))
        {
            TurnPage(-1);
        }
        else if (Controls.Pressed(Act.PageNext))
        {
            TurnPage(1);
        }

        if (Controls.Pressed(Act.TabPrev))
        {
            TurnTab(-1);
        }
        else if (Controls.Pressed(Act.TabNext))
        {
            TurnTab(1);
        }

        var rows = View.Rows.Count;
        if (rows == 0)
        {
            return;
        }

        if (Controls.Pressed(Act.Up))
        {
            MoveTo((View.RowIndex - 1 + rows) % rows);
        }
        else if (Controls.Pressed(Act.Down))
        {
            MoveTo((View.RowIndex + 1) % rows);
        }

        if (Controls.Pressed(Act.Left))
        {
            View.Focus?.Step(-1);
        }
        else if (Controls.Pressed(Act.Right))
        {
            View.Focus?.Step(1);
        }

        if (Controls.Pressed(Act.Accept))
        {
            View.Focus?.Press();
        }

        // A row with a job of its own for one of these keys does that in place of the usual.
        if (Controls.Pressed(Act.Bind))
        {
            if (View.Focus?.BindKey is { } own)
            {
                own.Run();
            }
            else if (Hotkeys.Bindable(View.Focus))
            {
                Hotkeys.Listen(View.Focus!);
            }
        }

        if (Controls.Pressed(Act.Favourite))
        {
            if (View.Focus?.FavouriteKey is { } own)
            {
                own.Run();
            }
            else if (Favourites.Allowed(View.Focus))
            {
                Favourites.Flip(View.Focus!);
            }
        }
    }

    static void TurnPage(int direction)
    {
        var count = View.Pages.Count;
        if (count < 2)
        {
            return;
        }

        _tabOfPage[View.PageIndex] = View.TabIndex;
        View.PageIndex = (View.PageIndex + direction + count) % count;
        View.TabIndex = _tabOfPage[View.PageIndex];
        Landed();
    }

    static void TurnTab(int direction)
    {
        var count = View.Page.Tabs.Count;
        if (count < 2)
        {
            return;
        }

        View.TabIndex = (View.TabIndex + direction + count) % count;
        Landed();
    }

    static void Landed()
    {
        View.RowIndex = 0;
        Clamp();
        View.Glide = View.RowIndex;
        Arrive();
        Remember();
    }

    // A live tab is built again each time it comes on screen. The selection stays in range in
    // case it got shorter.
    static void Arrive()
    {
        _arrivedAt = Stopwatch.GetTimestamp();
        if (View.Pages.Count > 0 && View.Page.Tabs.Count > 0 && View.Tab.Live)
        {
            View.Tab.Refresh();
            Clamp();
            View.Glide = View.RowIndex;
        }
    }

    public static void Close() => Want(false);

    // Whether this row is on the tab that is on screen.
    public static bool Shows(Feature row) => Showing && View.Page.Tabs.Count > 0 && View.Rows.Contains(row);

    // For a live tab whose rows changed while it may be on screen. It only acts when the tab
    // showing is the one with this row on it. The selection follows its row to where that is now.
    public static void Reshow(Feature anchor)
    {
        if (View.Pages.Count == 0 || View.Page.Tabs.Count == 0 || !View.Tab.Live || !View.Rows.Contains(anchor))
        {
            return;
        }

        var id = View.Focus?.Id;
        View.Tab.Refresh();

        var rows = View.Rows;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Id == id)
            {
                View.Glide += i - View.RowIndex;
                View.RowIndex = i;
                break;
            }
        }

        Clamp();
        Remember();
    }

    static void MoveTo(int row)
    {
        View.RowIndex = row;
        Remember();
    }

    static void Clamp()
    {
        if (View.Pages.Count == 0)
        {
            return;
        }

        View.PageIndex = Math.Clamp(View.PageIndex, 0, View.Pages.Count - 1);
        View.TabIndex = Math.Clamp(View.TabIndex, 0, Math.Max(0, View.Page.Tabs.Count - 1));
        View.RowIndex = Math.Clamp(View.RowIndex, 0, Math.Max(0, View.Rows.Count - 1));
    }

    static void Remember()
    {
        Survive.Set(Survive.MenuPage, View.PageIndex);
        Survive.Set(Survive.MenuTab, View.TabIndex);
        Survive.Set(Survive.MenuRow, View.RowIndex);
    }

    static void Want(bool open)
    {
        _wanted = open;
        Survive.Set(Survive.MenuOpen, open);
        if (open)
        {
            Catalog.MenuOpening();
        }
    }

    // The hints name the keys of whatever the player last touched.
    static void RefreshHints()
    {
        var listening = Hotkeys.Listening is not null;
        var focus = View.Focus;
        (string? Press, string? Bind, string? Favourite, bool Passing) row = (focus?.PressName, focus?.BindKey?.Name, focus?.FavouriteKey?.Name, focus?.Passing ?? false);
        if (_hintsBuilt && _hintDevice == Controls.LastDevice && _hintStyle == Pad.Look && _hintListening == listening && _hintRow == row)
        {
            return;
        }

        _hintsBuilt = true;
        _hintListening = listening;
        _hintRow = row;
        _hintDevice = Controls.LastDevice;
        _hintStyle = Pad.Look;

        var pad = _hintDevice == Device.Pad;
        View.PagePrevKey = pad ? Controls.PadLabel(Act.PagePrev) : Controls.KeyLabel(Act.PagePrev);
        View.PageNextKey = pad ? Controls.PadLabel(Act.PageNext) : Controls.KeyLabel(Act.PageNext);
        View.TabPrevKey = pad ? Controls.PadLabel(Act.TabPrev) : Controls.KeyLabel(Act.TabPrev);
        View.TabNextKey = pad ? Controls.PadLabel(Act.TabNext) : Controls.KeyLabel(Act.TabNext);

        if (listening)
        {
            var lines = new List<GuideLine>(3) { new(new[] { pad ? "BUTTONS" : "ANY KEY" }, "BIND IT") };
            if (Hotkeys.Clearable)
            {
                lines.Add(new GuideLine(new[] { pad ? Controls.PadLabel(Act.Bind) : Controls.KeyLabel(Act.Bind) }, "CLEAR IT"));
            }

            lines.Add(new GuideLine(new[] { pad ? Controls.PadLabel(Act.Back) : Controls.KeyLabel(Act.Back) }, "CANCEL"));
            View.Hints = lines;
            return;
        }

        // Page and tab keys are shown next to the pages and tabs themselves, so they aren't here.
        // A row names the keys it gives a job of its own. One that can't hold a hotkey or go on
        // the radial leaves those two out.
        string Key(Act act) => pad ? Controls.PadLabel(act) : Controls.KeyLabel(act);
        var hints = new List<GuideLine>(6);
        if (pad)
        {
            hints.Add(new GuideLine(new[] { "D-PAD" }, "MOVE"));
        }
        else
        {
            hints.Add(new GuideLine(new[] { Controls.KeyLabel(Act.Up), Controls.KeyLabel(Act.Down) }, "MOVE"));
            hints.Add(new GuideLine(new[] { Controls.KeyLabel(Act.Left), Controls.KeyLabel(Act.Right) }, "CHANGE"));
        }

        hints.Add(new GuideLine(new[] { Key(Act.Accept) }, row.Press ?? "SELECT"));
        if (row.Bind is not null || !row.Passing)
        {
            hints.Add(new GuideLine(new[] { Key(Act.Bind) }, row.Bind ?? "HOTKEY"));
        }

        if (row.Favourite is not null || !row.Passing)
        {
            hints.Add(new GuideLine(new[] { Key(Act.Favourite) }, row.Favourite ?? "FAVOURITE"));
        }

        hints.Add(new GuideLine(new[] { Key(Act.Back) }, "CLOSE"));
        View.Hints = hints;
    }
}
