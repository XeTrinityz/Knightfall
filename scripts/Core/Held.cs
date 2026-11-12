#nullable enable

using BmSDK;

namespace Knightfall;

// Keeps a game value at ours on any number of objects, and puts back exactly what each one had.
// If the game writes its own value while we hold ours, that is what gets put back instead.
sealed class Held<TTarget, TValue> where TTarget : GameObject
{
    readonly record struct Entry(TTarget Target, TValue Was, TValue Wrote);

    static readonly EqualityComparer<TValue> Same = EqualityComparer<TValue>.Default;

    readonly string _name;
    readonly Func<TTarget, TValue> _read;
    readonly Action<TTarget, TValue> _write;
    readonly Dictionary<nint, Entry> _held = new();
    readonly List<nint> _gone = new();

    public Held(string name, Func<TTarget, TValue> read, Action<TTarget, TValue> write)
    {
        _name = name;
        _read = read;
        _write = write;
    }

    public void Hold(TTarget target, TValue value) => Apply(target, value, null);

    // For a value worked out from the game's own, like a multiple of it.
    public void Hold(TTarget target, Func<TValue, TValue> fromOriginal) => Apply(target, default!, fromOriginal);

    void Apply(TTarget target, TValue value, Func<TValue, TValue>? fromOriginal)
    {
        var now = _read(target);

        // BmSDK keeps one wrapper per live object, so a different wrapper at the same address is
        // a new object that took the place of one that was destroyed.
        if (!_held.TryGetValue(target.Ptr, out var entry) || !ReferenceEquals(entry.Target, target))
        {
            ForgetDestroyed();
            entry = new Entry(target, now, now);
        }
        else if (!Same.Equals(now, entry.Wrote))
        {
            entry = entry with { Was = now };
        }

        var wanted = fromOriginal is null ? value : fromOriginal(entry.Was);
        if (!Same.Equals(now, wanted))
        {
            _write(target, wanted);
        }

        _held[target.Ptr] = entry with { Wrote = wanted };
    }

    public void Release()
    {
        foreach (var (target, was, _) in _held.Values)
        {
            if (!target.IsValid)
            {
                continue;
            }

            try
            {
                _write(target, was);
            }
            catch (Exception error)
            {
                Log.Error("held." + _name, $"Could not put back {_name}", error);
            }
        }

        _held.Clear();
    }

    // Only runs when a new object is held, which is rare, so the list is only walked then.
    void ForgetDestroyed()
    {
        foreach (var (pointer, entry) in _held)
        {
            if (!entry.Target.IsValid)
            {
                _gone.Add(pointer);
            }
        }

        foreach (var pointer in _gone)
        {
            _held.Remove(pointer);
        }

        _gone.Clear();
    }
}
