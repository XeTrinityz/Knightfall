#nullable enable

namespace Knightfall;

// Every option, in the pages and tabs the menu shows them in.
static class Catalog
{
    // A group of rows under a heading, like SURVIVAL on PLAYER > STATUS.
    public sealed record Section(string Heading, params Feature[] Rows)
    {
        // Rows that live on another tab and are only listed here as well, like every option
        // with a hotkey.
        public bool Borrowed { get; init; }
    }

    // One line of a tab as the menu shows it. A heading has no row and can't be selected.
    public readonly record struct Line(string? Heading, Feature? Row, int RowIndex);

    public sealed class Tab
    {
        readonly Func<IReadOnlyList<Section>>? _live;
        int[] _lineOfRow = Array.Empty<int>();

        public Tab(string name, IReadOnlyList<Section> sections)
        {
            Name = name;
            Fill(sections);
        }

        // A tab whose rows are worked out again each time it is shown, from rows that live on
        // other tabs too, like every option with a hotkey.
        public Tab(string name, Func<IReadOnlyList<Section>> live)
        {
            Name = name;
            _live = live;
            Fill(live());
        }

        public bool Live => _live is not null;

        // Builds a live tab's rows again. Others stay as they are.
        public void Refresh()
        {
            if (_live is not null)
            {
                Fill(_live());
            }
        }

        void Fill(IReadOnlyList<Section> sections)
        {
            var rows = new List<Feature>();
            var own = new List<Feature>();
            var lines = new List<Line>();
            foreach (var section in sections)
            {
                if (section.Heading.Length > 0)
                {
                    lines.Add(new Line(section.Heading, null, -1));
                }

                foreach (var row in section.Rows)
                {
                    lines.Add(new Line(null, row, rows.Count));
                    rows.Add(row);
                    if (!section.Borrowed)
                    {
                        own.Add(row);
                    }
                }
            }

            Rows = rows;
            Own = own;
            Lines = lines;
            _lineOfRow = new int[rows.Count];
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Row is not null)
                {
                    _lineOfRow[lines[i].RowIndex] = i;
                }
            }
        }

        public string Name { get; }

        // What can be selected, in order. Navigation and settings only ever see these.
        public IReadOnlyList<Feature> Rows { get; private set; } = Array.Empty<Feature>();

        // The rows that live on this tab, without the ones it only lists.
        public IReadOnlyList<Feature> Own { get; private set; } = Array.Empty<Feature>();

        // The rows with their headings between them, for drawing.
        public IReadOnlyList<Line> Lines { get; private set; } = Array.Empty<Line>();

        public int LineOf(int row) => _lineOfRow.Length == 0 ? 0 : _lineOfRow[Math.Clamp(row, 0, _lineOfRow.Length - 1)];
    }

    public sealed class Page
    {
        readonly List<Tab> _tabs = new();

        public Page(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public IReadOnlyList<Tab> Tabs => _tabs;

        // A tab with no headings.
        public Page Add(string tab, params Feature[] rows) => Add(tab, new Section("", rows));

        public Page Add(string tab, params Section[] sections)
        {
            foreach (var section in sections)
            {
                foreach (var row in section.Rows)
                {
                    Register(row);
                }
            }

            _tabs.Add(new Tab(tab, sections));
            return this;
        }

        // A live tab. Its rows are registered where they are declared, apart from any it shows
        // now, which are registered here.
        public Page Add(string tab, Func<IReadOnlyList<Section>> live)
        {
            var added = new Tab(tab, live);
            foreach (var row in added.Rows)
            {
                Register(row);
            }

            _tabs.Add(added);
            return this;
        }
    }

    static readonly List<Page> PageList = new();
    static readonly List<Feature> Everything = new();
    static readonly List<Feature> Ticking = new();
    static readonly List<Feature> Restoring = new();
    static readonly List<Command> Commands = new();
    static readonly List<(string Key, Action Tick, Action? Release)> Jobs = new();
    static readonly Dictionary<string, Feature> ById = new(StringComparer.OrdinalIgnoreCase);

    static int _active;
    static bool _activeStale = true;
    static bool _openingDue;

    public static IReadOnlyList<Page> Pages => PageList;

    public static IReadOnlyList<Feature> All => Everything;

    // How many options are switched away from normal. Look settings don't count.
    public static int Active
    {
        get
        {
            if (_activeStale)
            {
                _activeStale = false;
                _active = Everything.Count(feature => !feature.Preference && !feature.IsDefault);
            }

            return _active;
        }
    }

    // The option with this id, or null.
    public static Feature? Find(string id) => ById.TryGetValue(id, out var feature) ? feature : null;

    public static Page AddPage(string name)
    {
        var page = new Page(name);
        PageList.Add(page);
        return page;
    }

    public static void Clear()
    {
        PageList.Clear();
        Everything.Clear();
        Ticking.Clear();
        Restoring.Clear();
        Commands.Clear();
        Jobs.Clear();
        ById.Clear();
    }

    // Work that runs every game tick whatever the options say, like an autopilot that is driving.
    // Each one has to return straight away when it has nothing to do.
    public static void Background(string key, Action tick, Action? release = null)
    {
        Jobs.Add((key, tick, release));
    }

    public static void MenuOpening()
    {
        _openingDue = true;
    }

    public static void Load()
    {
        foreach (var feature in Everything)
        {
            if (feature.Stored is null)
            {
                continue;
            }

            feature.Load(Settings.Raw(feature.Section, feature.Key));
            Settings.Set(feature.Section, feature.Key, feature.Stored!);
        }

        _activeStale = true;
    }

    // The player changed a value.
    public static void Touched(Feature feature)
    {
        feature.TouchedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _activeStale = true;

        if (feature.Restore is not null && feature.IsDefault && !Restoring.Contains(feature))
        {
            Restoring.Add(feature);
        }

        if (feature.Stored is { } stored)
        {
            Settings.Set(feature.Section, feature.Key, stored);
        }

        feature.Changed?.Invoke();
    }

    // A command row was pressed. Like a restore, it runs on the next game tick.
    public static void Pressed(Command command)
    {
        command.TouchedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!Commands.Contains(command))
        {
            Commands.Add(command);
        }
    }

    // Each option runs in its own guard, so one that fails doesn't stop the rest.
    public static void Tick()
    {
        if (_openingDue)
        {
            _openingDue = false;
            foreach (var feature in Everything)
            {
                if (feature.Opening is not null)
                {
                    Kernel.Run(feature.Id, feature.Opening);
                }
            }
        }

        foreach (var (key, tick, _) in Jobs)
        {
            Kernel.Run(key, tick);
        }

        // Commands and restores can press or touch other options, which adds to these lists, so
        // each runs from a copy.
        if (Commands.Count > 0)
        {
            var due = Commands.ToArray();
            Commands.Clear();
            foreach (var command in due)
            {
                Kernel.Run(command.Id, command.Execute);
            }
        }

        // Options are switched off from the menu, which runs in the draw callback. The game is
        // only changed from here, so a restore waits for this tick.
        if (Restoring.Count > 0)
        {
            var due = Restoring.ToArray();
            Restoring.Clear();
            foreach (var feature in due)
            {
                if (feature.IsDefault)
                {
                    Kernel.Run(feature.Id, feature.Restore!);
                }
            }
        }

        foreach (var feature in Ticking)
        {
            if (!feature.IsDefault)
            {
                Kernel.Run(feature.Id, feature.Tick!);
            }
        }
    }

    // On unload every changed game value is put back, so the game is left as it was. After a hot
    // reload the options that are still on take hold again with fresh originals.
    public static void Release()
    {
        foreach (var feature in Everything)
        {
            if (feature.Restore is not null && (!feature.IsDefault || Restoring.Contains(feature)))
            {
                Kernel.Run(feature.Id, feature.Restore);
            }
        }

        foreach (var (key, _, release) in Jobs)
        {
            if (release is not null)
            {
                Kernel.Run(key, release);
            }
        }

        Restoring.Clear();
        Commands.Clear();
    }

    // Returns how many options were switched off.
    public static int AllOff()
    {
        var count = 0;
        foreach (var feature in Everything)
        {
            if (!feature.Preference && !feature.IsDefault)
            {
                feature.Reset();
                count++;
            }
        }

        return count;
    }

    // A row may be shown on more than one tab, but is only registered once.
    static void Register(Feature feature)
    {
        if (ById.TryGetValue(feature.Id, out var known))
        {
            if (ReferenceEquals(known, feature))
            {
                return;
            }

            throw new InvalidOperationException($"Two options share the id '{feature.Id}'.");
        }

        ById[feature.Id] = feature;
        Everything.Add(feature);
        if (feature.Tick is not null)
        {
            Ticking.Add(feature);
        }
    }
}
