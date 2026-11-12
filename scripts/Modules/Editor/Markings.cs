#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// WORLD > MARKERS. A box round everything of the kinds you pick, in each kind's colour, drawn
// the same way as the editor's own box. The world is looked over a few times a second and only
// the nearest are drawn, so it stays light.
static class Markings
{
    const float Metre = Teleport.UnitsPerMetre;
    const float LookEvery = 0.25f;

    // LABELS NEAR, and the health meter, reach this far.
    const float Near = 40f * Metre;

    const float CardHeight = 18f;
    const float CardGap = 4f;
    const float NameSize = 9f;
    const float NoteSize = 7f;

    // Behind a wall, a marker is drawn this strong.
    const float Hidden = 0.45f;

    static readonly Rgba[] Colors =
    {
        new(240, 80, 70),
        new(90, 220, 130),
        new(255, 160, 60),
        new(235, 90, 200),
        new(100, 180, 255),
        new(180, 140, 255),
        new(255, 215, 80),
    };

    static readonly Rgba Down = new(150, 155, 160);
    static readonly Rgba White = new(255, 255, 255);
    static readonly Rgba Ink = new(8, 10, 14);

    readonly record struct Mark(Actor Actor, Things.Kind Kind, bool Seen, bool Out);

    static List<Mark> _marks = new();
    static float _lookedAt = -1f;

    public static readonly Toggle On = new(
        "world.markers.on", "MARKERS",
        "Puts a box round everything of the kinds below, in each kind's colour, through walls. Grey means knocked out or destroyed.");

    static readonly Toggle[] Show =
    {
        ShowKind("enemies", Things.Kind.Enemy, true),
        ShowKind("friendlies", Things.Kind.Friendly, true),
        ShowKind("vehicles", Things.Kind.Vehicle, true),
        ShowKind("turrets", Things.Kind.Turret, true),
        ShowKind("props", Things.Kind.Prop, false),
        ShowKind("scenery", Things.Kind.Scenery, false),
        ShowKind("pickups", Things.Kind.Pickup, false),
    };

    public static readonly Choice Boxes = new(
        "world.markers.boxes", "BOXES",
        "3D BOX draws every edge, CORNERS only the corners facing you, NONE leaves just the labels.",
        new[] { ("box", "3D BOX"), ("corners", "CORNERS"), ("none", "NONE") })
    {
        Preference = true,
    };

    public static readonly Choice Labels = new(
        "world.markers.labels", "LABELS",
        "Names over the markers. NEAR shows them within 40 metres, ALL on every marker.",
        new[] { ("off", "OFF"), ("near", "NEAR"), ("all", "ALL") }, index: 1)
    {
        Preference = true,
    };

    public static readonly Toggle Details = new(
        "world.markers.details", "DETAILS",
        "Adds how far away each one is, and a health meter on people and vehicles close by.",
        on: true)
    {
        Preference = true,
    };

    public static readonly Toggle Tracers = new(
        "world.markers.tracers", "TRACERS",
        "A thin line from the top of the screen to each marker.")
    {
        Preference = true,
    };

    public static readonly Slider Range = new(
        "world.markers.range", "MARKER RANGE",
        "How far away things get a marker.",
        min: 10, max: 300, step: 10, value: 80, unit: " M")
    {
        Preference = true,
    };

    public static readonly Slider Most = new(
        "world.markers.most", "MOST SHOWN",
        "The most markers at once, nearest first. Fewer is lighter on a slow PC.",
        min: 5, max: 80, step: 5, value: 25)
    {
        Preference = true,
    };

    public static readonly Catalog.Section[] Sections =
    {
        new("MARKERS", new Feature[] { On }.Concat(Show).ToArray()),
        new("LOOK", Boxes, Labels, Details, Tracers, Range, Most),
    };

    public static bool Showing => On.On && _marks.Count > 0;

    static Toggle ShowKind(string id, Things.Kind kind, bool on) =>
        new("world.markers.show_" + id, "SHOW " + Things.Plurals[(int)kind], $"Marks {Things.Plurals[(int)kind].ToLowerInvariant()}.", on)
        {
            Preference = true,
        };

    // Runs every tick. The world is looked over a few times a second.
    public static void Tick()
    {
        if (!On.On || World.Paused)
        {
            if (!On.On)
            {
                _marks.Clear();
            }

            return;
        }

        if (MathF.Abs(World.Now - _lookedAt) < LookEvery || World.Controller is not { } controller || Teleport.Mover is not { } mover)
        {
            return;
        }

        _lookedAt = World.Now;
        controller.GetPlayerViewPoint(out var eye, out _);
        var marks = new List<Mark>();
        foreach (var item in Things.Near(eye, (float)Range.Value * Metre, null, kind => Show[(int)kind].On))
        {
            if (marks.Count >= (int)Most.Value)
            {
                break;
            }

            // Riders show with their vehicle.
            if (!Show[(int)item.Kind].On || item.Actor.Base is Vehicle)
            {
                continue;
            }

            var middle = Things.ShapeOf(item.Actor).Middle;
            marks.Add(new Mark(item.Actor, item.Kind, mover.FastTrace(middle, eye, Vector3.Zero, false), IsOut(item.Actor)));
        }

        _marks = marks;
    }

    static bool IsOut(Actor actor) => actor switch
    {
        RBMPawnAI body => !Crowd.Standing(body),
        Pawn pawn => pawn.Health <= 0 || pawn.HasDied,
        RSentryGunBase gun => gun.bGoneRagdoll || gun.bDisabled,
        RTurretBase turret => turret.bDisabled,
        _ => false,
    };

    // Drawn while the hologram is framed for this view. The selected object and the aim preview
    // have their own look, so they're left out.
    public static void Render(float time)
    {
        if (!On.On)
        {
            return;
        }

        var selected = Editor.Selected;
        var preview = Editor.Preview;
        var style = Boxes.Index switch
        {
            0 => Marks.Style.Full,
            1 => Marks.Style.Corners,
            _ => (Marks.Style?)null,
        };

        if (Tracers.On)
        {
            Hologram.Pop();
            Draw.Place(0f, 0f, 1f);
            var top = new Vector2(Hologram.Middle.X, 0f) / Draw.Scale;
            foreach (var mark in _marks)
            {
                if (Things.Alive(mark.Actor) && !ReferenceEquals(mark.Actor, selected)
                    && Hologram.Screen(Things.ShapeOf(mark.Actor).Middle, out var at))
                {
                    at /= Draw.Scale;
                    Draw.Line(top.X, top.Y, at.X, at.Y, ColorOf(mark) with { A = (byte)(mark.Seen ? 90 : 45) });
                }
            }
        }

        foreach (var mark in _marks)
        {
            if (!Things.Alive(mark.Actor) || mark.Actor.bHidden || ReferenceEquals(mark.Actor, selected) || ReferenceEquals(mark.Actor, preview)
                || (GuardMarks.On.On && Bodyguards.Is(mark.Actor)))
            {
                continue;
            }

            var shape = Things.ShapeOf(mark.Actor);
            var color = ColorOf(mark);
            var strength = mark.Seen ? 1f : Hidden;
            if (style is { } look)
            {
                Marks.Box(shape, color, look, time, strength);
            }

            var distance = Vector3.Distance(shape.Middle, Hologram.Eye);
            if (Labels.Index == 2 || (Labels.Index == 1 && distance <= Near))
            {
                Card(mark, shape, color, distance, strength);
            }
        }
    }

    static Rgba ColorOf(Mark mark) => mark.Out ? Down : Colors[(int)mark.Kind];

    static void Card(Mark mark, Things.Shape shape, Rgba color, float distance, float strength)
    {
        var top = shape.Middle + Vector3.UnitZ * shape.Half.Z;
        var pixels = Hologram.Pixels(top);
        if (pixels <= 0f)
        {
            return;
        }

        var name = Things.NameOf(mark.Actor);
        var note = Details.On ? $"{distance / Metre:0} M" : "";
        var health = Details.On && distance <= Near ? HealthOf(mark.Actor) : null;
        var width = MathF.Max(Draw.TextWidth(name, NameSize), Draw.TextWidth(note, NoteSize)) + 12f;
        var height = note.Length > 0 || health is not null ? CardHeight + (health is not null ? 4f : 0f) : CardHeight - 6f;

        var unit = Draw.Scale / pixels;
        var toward = Teleport.Flat(top - Hologram.Eye);
        var across = new Vector3(-toward.Y, toward.X, 0f);
        var lift = top + Vector3.UnitZ * (CardGap + height / 2f) * unit;
        var card = new Hologram.Panel(lift - across * width / 2f * unit, across * unit, -Vector3.UnitZ * unit);
        if (!Hologram.Place(card, width, height))
        {
            return;
        }

        var fade = (byte)(255 * strength);
        Draw.Rect(0f, 0f, width, height, Ink with { A = (byte)(160 * strength) });
        Draw.Rect(0f, 0f, 2f, height, color with { A = fade });
        Draw.Text(name, 6f, 1f, NameSize, White with { A = fade });
        if (note.Length > 0)
        {
            Draw.Text(note, 6f, 11f, NoteSize, color with { A = fade });
        }

        if (health is { } fill)
        {
            Draw.Rect(6f, height - 3f, width - 12f, 2f, Ink with { A = fade });
            Draw.Rect(6f, height - 3f, (width - 12f) * fill, 2f, color with { A = fade });
        }
    }

    // How much health is left, 0 to 1, for people and vehicles.
    static float? HealthOf(Actor actor)
    {
        if (actor is not Pawn pawn)
        {
            return null;
        }

        var full = pawn.HealthMax;
        if (pawn is RPawnCombat fighter && fighter.MaxHealth > full)
        {
            full = fighter.MaxHealth;
        }

        return full > 0 ? Math.Clamp(pawn.Health / (float)full, 0f, 1f) : null;
    }
}
