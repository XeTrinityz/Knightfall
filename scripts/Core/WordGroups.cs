#nullable enable

namespace Knightfall;

// A long list of names from the game, split into groups short enough to step through. Names are
// taken as their words, sorted, and grouped by the words they start with.
static class WordGroups
{
    // A run of the sorted names. Depth is how many leading words the group's name already says.
    public readonly record struct Bunch(string Label, int Start, int Count, int Depth);

    public static int Compare(string[] a, string[] b)
    {
        var shared = Math.Min(a.Length, b.Length);
        for (var i = 0; i < shared; i++)
        {
            var order = string.CompareOrdinal(a[i], b[i]);
            if (order != 0)
            {
                return order;
            }
        }

        return a.Length - b.Length;
    }

    // The groups of a list already sorted with Compare. None holds more than most, unless its
    // names are the same to the last word.
    public static List<Bunch> Split(IReadOnlyList<string[]> names, int most)
    {
        var bunches = new List<Bunch>();
        if (names.Count > 0)
        {
            Split(names, most, bunches, 0, names.Count, 0, "");
        }

        return bunches;
    }

    // The words the group's name doesn't already say.
    public static string Label(string[] words, int depth) =>
        depth > 0 && depth < words.Length ? string.Join(' ', words, depth, words.Length - depth) : string.Join(' ', words);

    // Splits a run that is too long by the next word of its names. Runs that share a word and
    // are still too long are split again by the word after. Small neighbours are put together,
    // named by the first and last word they cover.
    static void Split(IReadOnlyList<string[]> names, int most, List<Bunch> bunches, int start, int end, int depth, string prefix)
    {
        if (end - start <= most)
        {
            bunches.Add(new Bunch(prefix.Length == 0 ? "ALL" : prefix, start, end - start, depth));
            return;
        }

        var from = start;
        var held = 0;
        var first = "";
        var last = "";

        void Flush()
        {
            if (held == 0)
            {
                return;
            }

            var one = first == last;
            var words = one ? first : first.Length == 0 ? "TO " + last : first + " - " + last;
            bunches.Add(new Bunch(Join(prefix, words), from, held, one && first.Length > 0 ? depth + 1 : depth));
            held = 0;
        }

        string WordAt(int name) => depth < names[name].Length ? names[name][depth] : "";

        for (var i = start; i < end;)
        {
            var word = WordAt(i);
            var next = i + 1;
            while (next < end && WordAt(next) == word)
            {
                next++;
            }

            var size = next - i;
            if (size > most)
            {
                Flush();
                if (word.Length > 0)
                {
                    Split(names, most, bunches, i, next, depth + 1, Join(prefix, word));
                }
                else
                {
                    // Names that are the same to the last word can't be split any further.
                    bunches.Add(new Bunch(prefix.Length == 0 ? "ALL" : prefix, i, size, depth));
                }
            }
            else
            {
                if (held + size > most)
                {
                    Flush();
                }

                if (held == 0)
                {
                    from = i;
                    first = word;
                }

                last = word;
                held += size;
            }

            i = next;
        }

        Flush();
    }

    static string Join(string prefix, string words) =>
        prefix.Length == 0 ? words : words.Length == 0 ? prefix : prefix + " " + words;
}
