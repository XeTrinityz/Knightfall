#nullable enable

namespace Knightfall;

static class Log
{
    const double RepeatSeconds = 30;
    const int StackLines = 12;

    sealed class Repeat
    {
        public DateTime Last;
        public int Skipped;
    }

    static readonly object Gate = new();
    static readonly Dictionary<string, Repeat> Repeats = new();
    static string? _path;

    // Returns true the first time it runs in this game session.
    public static bool Start(string modDir)
    {
        var firstLoad = !Survive.Get(Survive.LogStarted, false);
        var path = Path.Combine(modDir, "Knightfall.log");

        lock (Gate)
        {
            if (firstLoad)
            {
                KeepPrevious(path);
            }

            _path = path;
        }

        Survive.Set(Survive.LogStarted, true);
        return firstLoad;
    }

    public static void Info(string text)
    {
        Write("INFO ", text);
    }

    public static void Info(string key, string text)
    {
        if (Due(key, out var skipped))
        {
            Write("INFO ", WithSkipped(text, skipped));
        }
    }

    public static void Warn(string key, string text)
    {
        if (Due(key, out var skipped))
        {
            Write("WARN ", WithSkipped(text, skipped));
        }
    }

    // The stack text is only built when the line will be written. Building it every frame is slow.
    public static void Error(string key, string what, Exception error)
    {
        if (Due(key, out var skipped))
        {
            Write("ERROR", WithSkipped($"{what}: {Describe(error)}", skipped));
        }
    }

    static bool Due(string key, out int skipped)
    {
        var now = DateTime.UtcNow;
        lock (Gate)
        {
            if (!Repeats.TryGetValue(key, out var repeat))
            {
                Repeats[key] = new Repeat { Last = now };
                skipped = 0;
                return true;
            }

            if ((now - repeat.Last).TotalSeconds < RepeatSeconds)
            {
                repeat.Skipped++;
                skipped = 0;
                return false;
            }

            skipped = repeat.Skipped;
            repeat.Skipped = 0;
            repeat.Last = now;
            return true;
        }
    }

    static string WithSkipped(string text, int skipped)
    {
        return skipped > 0 ? $"{text} (happened {skipped} more times since the last line)" : text;
    }

    static string Describe(Exception error)
    {
        var lines = error.ToString().Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0);
        return string.Join(Environment.NewLine + "      ", lines.Take(StackLines));
    }

    static void KeepPrevious(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Move(path, Path.ChangeExtension(path, ".prev.log"), overwrite: true);
            }
        }
        catch (Exception)
        {
            // A locked old log is not worth failing over. New lines are added to it instead.
        }
    }

    static void Write(string level, string text)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {text}";

        try
        {
            lock (Gate)
            {
                if (_path != null)
                {
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
            }

            Debug.Log($"[Knightfall] {level.Trim()} {text}", skipSender: true);
        }
        catch (Exception)
        {
            // Logging must never be the thing that takes the game down.
        }
    }
}
