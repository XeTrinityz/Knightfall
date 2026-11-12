#nullable enable

using System.Buffers.Binary;
using System.Globalization;
using Microsoft.Win32;

namespace Knightfall;

enum SaveKind
{
    Kept,
    File,
    Backup,
}

// One save on disk and what its header says. Percent is the number the slot screen shows and
// can pass 100. Plus is how far its New Game + run has got. Either is -1 when it wasn't found.
sealed record SaveFile(string Path, SaveKind Kind, int Slot, int Number, DateTime When, long Stamp, int Place, string Difficulty, int Percent, float Plus, bool Readable);

// The save files on disk. A slot has three files, BAK1Save{slot}x{0..2}.sgd in Steam's cloud
// folder, and the game loads the newest. It also copies every write to a backup folder in
// Documents, with the time on the end of the name. Our kept copies are named the same way, in a
// folder the game never touches.
static class SaveFiles
{
    const string AppId = "208650";
    const long FirstSteamId = 76561197960265728L;
    const string NameStart = "BAK1Save";
    const string NameEnd = ".sgd";
    const string TimeFormat = "yyyy-MM-dd_HH-mm-ss";
    const int Version = 6;

    // The header is plain, and everything read here sits in the first part of it.
    const int HeaderBytes = 192 * 1024;
    const int OptionsWithin = 64 * 1024;
    const int PercentWithin = 16 * 1024;

    static readonly long Earliest = new DateTime(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
    static readonly long Latest = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

    static readonly byte[] Header = new byte[HeaderBytes];
    static readonly Dictionary<string, (DateTime Written, long Size, SaveFile File)> Known = new(StringComparer.OrdinalIgnoreCase);

    static bool _found;

    public static string Files { get; private set; } = "";

    public static string Backups { get; private set; } = "";

    public static string Kept { get; private set; } = "";

    // Every save in the three folders, newest first. Headers are only read for files that are
    // new or have changed since the last time.
    public static List<SaveFile> List()
    {
        Find();

        var list = new List<SaveFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(list, seen, Kept, SaveKind.Kept);
        Collect(list, seen, Files, SaveKind.File);
        Collect(list, seen, Backups, SaveKind.Backup);

        foreach (var gone in Known.Keys.Where(path => !seen.Contains(path)).ToArray())
        {
            Known.Remove(gone);
        }

        // The game backs a file up as it writes it, so the newest backups are the same saves as
        // the slot's own files. A kept copy of a backup stands in for it too.
        var shown = new HashSet<(int, long)>(list.Where(file => file.Kind != SaveKind.Backup && file.Stamp != 0).Select(file => (file.Slot, file.Stamp)));
        list.RemoveAll(file => file.Kind == SaveKind.Backup && shown.Contains((file.Slot, file.Stamp)));
        list.Sort(static (a, b) => b.When.CompareTo(a.When));
        return list;
    }

    public static byte[] Read(string path)
    {
        using var stream = Open(path);
        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        return data;
    }

    // Copies a save into the kept folder. Returns false when that save is already there.
    public static bool Keep(SaveFile file, out string path) => Keep(file.Slot, file.Number, file.When, Read(file.Path), out path);

    // Keeps a save the game is about to write, from the bytes it is writing.
    public static bool Keep(string name, ReadOnlySpan<byte> data, out string path)
    {
        path = "";
        if (!Named(name, out var slot, out var number))
        {
            return false;
        }

        var file = Parse(data[..Math.Min(data.Length, HeaderBytes)], name, SaveKind.Kept, slot, number, DateTime.Now);
        return file.Readable && Keep(slot, number, file.When, data, out path);
    }

    public static void Delete(SaveFile file)
    {
        if (file.Kind == SaveKind.Kept)
        {
            File.Delete(file.Path);
        }
    }

    public static bool Named(string name, out int slot, out int number)
    {
        slot = 0;
        number = 0;
        var at = NameStart.Length;
        if (name.Length < at + 3 + NameEnd.Length || !name.StartsWith(NameStart, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!char.IsAsciiDigit(name[at]) || name[at + 1] is not ('x' or 'X') || !char.IsAsciiDigit(name[at + 2]))
        {
            return false;
        }

        if (!name.AsSpan(at + 3).StartsWith(NameEnd, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Nothing after it, or the time a backup was made.
        var rest = name.AsSpan(at + 3 + NameEnd.Length);
        if (rest.Length != 0 && (rest.Length != TimeFormat.Length + 1 || rest[0] != '_'))
        {
            return false;
        }

        slot = name[at] - '0';
        number = name[at + 2] - '0';
        return true;
    }

    static bool Keep(int slot, int number, DateTime when, ReadOnlySpan<byte> data, out string path)
    {
        Find();
        if (Kept.Length == 0)
        {
            throw new InvalidOperationException("There is no folder to keep saves in");
        }

        Directory.CreateDirectory(Kept);
        path = Path.Combine(Kept, $"{NameStart}{slot}x{number}{NameEnd}_{when.ToString(TimeFormat, CultureInfo.InvariantCulture)}");
        if (File.Exists(path))
        {
            return false;
        }

        // Written under another name first, so a half written copy never shows in the list.
        var part = path + ".part";
        using (var stream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(data);
        }

        File.Move(part, path);
        return true;
    }

    static void Collect(List<SaveFile> list, HashSet<string> seen, string folder, SaveKind kind)
    {
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            return;
        }

        foreach (var info in new DirectoryInfo(folder).EnumerateFiles(NameStart + "*"))
        {
            if (!Named(info.Name, out var slot, out var number) || !seen.Add(info.FullName))
            {
                continue;
            }

            if (Known.TryGetValue(info.FullName, out var known) && known.Written == info.LastWriteTimeUtc && known.Size == info.Length)
            {
                list.Add(known.File);
                continue;
            }

            try
            {
                int read;
                using (var stream = Open(info.FullName))
                {
                    read = stream.ReadAtLeast(Header, Header.Length, throwOnEndOfStream: false);
                }

                var file = Parse(Header.AsSpan(0, read), info.FullName, kind, slot, number, info.LastWriteTime);
                Known[info.FullName] = (info.LastWriteTimeUtc, info.Length, file);
                list.Add(file);
            }
            catch (IOException error)
            {
                Log.Warn("saves.read", $"A save file could not be read: {error.Message}");
            }
            catch (UnauthorizedAccessException error)
            {
                Log.Warn("saves.read", $"A save file could not be read: {error.Message}");
            }
        }
    }

    // The game may be writing the file, so nothing is locked.
    static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    // A save starts with its version. Further in, at a place that moves from save to save, the
    // options hold the time it was saved, the date again as day, month and year, the map area and
    // the difficulty by name. They are found by that shape.
    static SaveFile Parse(ReadOnlySpan<byte> head, string path, SaveKind kind, int slot, int number, DateTime written)
    {
        if (head.Length < 8 || Int(head, 0) != Version)
        {
            return new SaveFile(path, kind, slot, number, written, 0, -1, "", -1, -1f, false);
        }

        var end = Math.Min(head.Length, OptionsWithin) - 32;
        for (var at = 8; at <= end; at++)
        {
            var stamp = BinaryPrimitives.ReadInt64LittleEndian(head[at..]);
            if (stamp < Earliest || stamp > Latest)
            {
                continue;
            }

            int day = Int(head, at + 8), month = Int(head, at + 12), year = Int(head, at + 16), place = Int(head, at + 20), length = Int(head, at + 24);
            if (day is < 1 or > 31 || month is < 1 or > 12 || year is < 2015 or > 2100 || place is < 0 or > 4096 || length is < 2 or > 32)
            {
                continue;
            }

            // The written out date is the same day as the time, give or take the time zone.
            var saved = DateTime.FromFileTimeUtc(stamp);
            if (day > DateTime.DaysInMonth(year, month) || Math.Abs((new DateTime(year, month, day) - saved.Date).TotalDays) > 1)
            {
                continue;
            }

            var after = at + 28 + length;
            if (after + 4 > head.Length || head[after - 1] != 0 || !Letters(head.Slice(at + 28, length - 1)))
            {
                continue;
            }

            var difficulty = System.Text.Encoding.ASCII.GetString(head.Slice(at + 28, length - 1));
            return new SaveFile(path, kind, slot, number, saved.ToLocalTime(), stamp, place, difficulty, Percent(head, after), Plus(head, after), true);
        }

        // A save all the same, just not one whose details could be found.
        return new SaveFile(path, kind, slot, number, written, 0, -1, "", -1, -1f, true);
    }

    // The slot screen's percentage sits right before a list of four tallies like "6/243".
    static int Percent(ReadOnlySpan<byte> head, int from)
    {
        var end = Math.Min(head.Length, from + PercentWithin) - 12;
        for (var at = from + 4; at <= end; at++)
        {
            if (Int(head, at) != 4)
            {
                continue;
            }

            var length = Int(head, at + 4);
            if (length is < 4 or > 8 || at + 8 + length > head.Length || head[at + 8 + length - 1] != 0)
            {
                continue;
            }

            if (Tally(head.Slice(at + 8, length - 1)) && Int(head, at - 4) is >= 0 and <= 999 and var percent)
            {
                return percent;
            }
        }

        return -1;
    }

    // Story progress is three numbers with a count of 3 before them: the main story, a spare
    // that is always 0, and New Game +. It has no name, so it is the first run of that shape.
    static float Plus(ReadOnlySpan<byte> head, int from)
    {
        for (var at = from; at + 16 <= head.Length; at++)
        {
            if (Int(head, at) != 3)
            {
                continue;
            }

            var story = BinaryPrimitives.ReadSingleLittleEndian(head[(at + 4)..]);
            var spare = BinaryPrimitives.ReadSingleLittleEndian(head[(at + 8)..]);
            var plus = BinaryPrimitives.ReadSingleLittleEndian(head[(at + 12)..]);
            if (story is > 0.01f and <= 100f && spare == 0f && plus is >= 0f and <= 100f)
            {
                return plus;
            }
        }

        return -1f;
    }

    static int Int(ReadOnlySpan<byte> head, int at) => BinaryPrimitives.ReadInt32LittleEndian(head[at..]);

    static bool Letters(ReadOnlySpan<byte> text)
    {
        foreach (var letter in text)
        {
            if (!char.IsAsciiLetterOrDigit((char)letter))
            {
                return false;
            }
        }

        return text.Length > 0;
    }

    // Digits, a slash, then digits.
    static bool Tally(ReadOnlySpan<byte> text)
    {
        var slash = text.IndexOf((byte)'/');
        if (slash is < 1 or > 3 || text.Length - slash - 1 is < 1 or > 3)
        {
            return false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (i != slash && !char.IsAsciiDigit((char)text[i]))
            {
                return false;
            }
        }

        return true;
    }

    // Steam names its folder by the short account number, and the game names its backup folder
    // by the long Steam id, which is that number added to a fixed start.
    static void Find()
    {
        if (_found)
        {
            return;
        }

        _found = true;
        try
        {
            var steam = (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string ?? "").Replace('/', '\\');
            var account = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", 0) is int user ? user : 0;

            var users = steam.Length > 0 ? Path.Combine(steam, "userdata") : "";
            var mine = account != 0 ? Path.Combine(users, account.ToString(CultureInfo.InvariantCulture), AppId, "remote") : "";
            Files = mine.Length > 0 && Directory.Exists(mine)
                ? mine
                : Newest(users, user => Path.Combine(user, AppId, "remote"));

            var game = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WB Games", "Batman Arkham Knight");
            var owner = Files.Length > 0 ? Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Files))) : null;
            var id = long.TryParse(owner, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? (FirstSteamId + number).ToString(CultureInfo.InvariantCulture)
                : "";

            var backups = id.Length > 0 ? Path.Combine(game, id, "SaveData", "backup") : "";
            Backups = backups.Length > 0 && Directory.Exists(backups)
                ? backups
                : Newest(game, user => Path.Combine(user, "SaveData", "backup"));

            // In Documents, so the copies outlast a mod update. One folder for each account.
            Kept = id.Length > 0 && Directory.Exists(game)
                ? Path.Combine(game, "Knightfall", id)
                : Path.Combine(GameFiles.Mod, "saves");
        }
        catch (Exception error)
        {
            Log.Error("saves.folders", "The save folders could not be found", error);
        }

        // The folder names carry the player's Steam id, so they stay out of the log.
        Log.Info($"Saves: game files {Shown(Files)}, game backups {Shown(Backups)}.");
    }

    static string Shown(string folder) => folder.Length > 0 ? "found" : "not found";

    // The folder written to last, out of one in each of the folders under the parent.
    static string Newest(string parent, Func<string, string> inside)
    {
        if (parent.Length == 0 || !Directory.Exists(parent))
        {
            return "";
        }

        return Directory.GetDirectories(parent).Select(inside).Where(Directory.Exists).OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault() ?? "";
    }
}
