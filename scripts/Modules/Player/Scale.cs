#nullable enable

using BmSDK;

namespace Knightfall;

static class Fields
{
    public static (Func<T, float> Read, Action<T, float> Write) Of<T>(Func<T, float> read, Action<T, float> write)
        where T : GameObject => (read, write);
}

// A slider that multiplies a set of the game's values on one object.
sealed class Scale<T>
    where T : GameObject
{
    readonly Held<T, float>[] _fields;
    readonly Func<T?> _find;
    readonly Func<float, float> _scaled;

    // The scale makes its own slider, so the slider's tick and restore can point back at it.
    public Scale(string name, Func<Action, Action, Slider> slider, Func<T?> find, params (Func<T, float> Read, Action<T, float> Write)[] fields)
    {
        _fields = Array.ConvertAll(fields, field => new Held<T, float>(name, field.Read, field.Write));
        _find = find;
        Slider = slider(Apply, Release);
        _scaled = was => was * (float)Slider.Value * (Extra?.Invoke() ?? 1f);
    }

    public Slider Slider { get; }

    public Func<float>? Extra { get; init; }

    public void Apply()
    {
        if (_find() is not { IsValid: true } target)
        {
            return;
        }

        foreach (var field in _fields)
        {
            field.Hold(target, _scaled);
        }
    }

    public void Release()
    {
        foreach (var field in _fields)
        {
            field.Release();
        }
    }
}
