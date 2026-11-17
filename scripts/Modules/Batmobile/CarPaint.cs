#nullable enable

using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// BATMOBILE > SKIN, the PAINT rows. Each skin keeps its own colours. The painting itself is the
// shared Painter.
static class CarPaint
{
    // Words in the car's material names that say nothing about which part it is.
    static readonly string[] Owner = { "Batmobile", "New" };

    public static readonly Painter Brush = new(new PaintSpec(
        Prefix: "batmobile.skin",
        Store: "batmobile.skin_colours",
        Noun: "skin",
        Whole: "Batmobile",
        ClearAllId: "clear_all",
        NoSubject: "NO BATMOBILE HERE",
        Subject: Car.Find,
        KeyOf: SkinKey,
        Find: car =>
        {
            var parts = SuitParts.Find(car, Owner);
            if (car is RVehicleBatmobileBase batmobile)
            {
                parts.Add(Beams.Part(batmobile));
            }

            return parts;
        })
    {
        PutOn = skin =>
        {
            if (Survive.Get(Survive.SkinWorn, "") == skin)
            {
                return null;
            }

            Survive.Set(Survive.SkinWorn, skin);
            return CarLooks.PutOnLooks(skin);
        },
        Scanned = () => CarLooks.Refresh(),
    });

    public static readonly Toggle Paint = Brush.Paint;
    public static readonly Choice Which = Brush.Which;
    public static readonly Choice Colour = Brush.Colour;
    public static readonly Slider Strength = Brush.Strength;
    public static readonly Slider Brightness = Brush.Brightness;
    public static readonly Command CopyLook = Brush.CopyLook;
    public static readonly Command PasteLook = Brush.PasteLook;
    public static readonly Command Shuffle = Brush.Shuffle;
    public static readonly Command Back = Brush.Back;
    public static readonly Command Unmark = Brush.Unmark;
    public static readonly Command ClearPart = Brush.ClearPart;
    public static readonly Command ClearAll = Brush.ClearAll;

    public static void Tick() => Brush.Tick();

    public static void Release() => Brush.Release();

    public static void KeepSettings()
    {
        Brush.KeepStore();
        Settings.KeepAll(CarLooks.PutOnStore);
    }

    // The skin the car wears, by the mesh it loaded. The base car has no mesh name.
    static string SkinKey(Actor car)
    {
        var name = Game.TryGetGameRI(out var game) ? game.PlayerBatmobiles_0.MeshName.ToString() ?? "" : "";
        var key = SuitParts.Safe(name);
        return key.Length == 0 ? "base" : key;
    }
}
