#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// WORLD > MARKERS > LOOK > THOUGHTS. The lines the game's own AI debugger shows for a person: what
// its behaviour is, whether it can see you, if it is in combat. They are asked of the controller
// of each person the markers show, a few times a second, and drawn in a box over their head. Only
// the nearest few are asked, since each answer is a long script.
static class Thoughts
{
    const float Metre = Teleport.UnitsPerMetre;
    const float LookEvery = 0.2f;
    const float Reach = 40f * Metre;
    const int Most = 4;
    const int MostLines = 8;
    const float TextSize = 7f;
    const float LineHeight = 9f;
    const float Pad = 5f;

    // Long lines are cut so a box never spans the screen.
    const int WidestLine = 44;

    static readonly Rgba Ink = new(8, 10, 14);

    readonly record struct Line(string Text, Rgba Color);

    readonly record struct Card(Actor Actor, List<Line> Lines);

    static List<Card> _cards = new();
    static float _lookedAt = -100f;

    public static readonly Toggle On = new(
        "world.markers.thoughts", "THOUGHTS",
        "Shows what the enemies and friendlies marked are thinking, in a box over their head: their behaviour, whether they can see you, if they are in combat. Only the nearest few within 40 metres.")
    {
        Tick = Look,
        Restore = Forget,
    };

    public static bool Showing => On.On && _cards.Count > 0;

    static void Forget()
    {
        _cards = new List<Card>();
        _lookedAt = -100f;
    }

    static void Look()
    {
        if (World.Paused || World.Player is not { } player || MathF.Abs(World.Now - _lookedAt) < LookEvery)
        {
            return;
        }

        _lookedAt = World.Now;
        var cards = new List<Card>();
        foreach (var actor in Markings.Marked)
        {
            if (cards.Count >= Most)
            {
                break;
            }

            if (actor is RBMPawnAI { IsAlive: true, Health: > 0, Controller: RBMAIController ai } && Vector3.Distance(actor.Location, player.Location) <= Reach
                && Read(ai) is { Count: > 0 } lines)
            {
                cards.Add(new Card(actor, lines));
            }
        }

        _cards = cards;
    }

    // The controller's own thoughts. The pawn's version also needs the game's debug flags.
    static List<Line>? Read(RBMAIController ai)
    {
        try
        {
            ai.GetAIThoughts(out var thoughts);
            var lines = new List<Line>(Math.Min(thoughts.Count, MostLines));
            for (var i = 0; i < thoughts.Count && lines.Count < MostLines; i++)
            {
                var thought = thoughts[i];
                var text = thought.Text.ToString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                text = text.Trim();
                if (text.Length > WidestLine)
                {
                    text = text[..WidestLine];
                }

                lines.Add(new Line(text, new Rgba(thought.Red, thought.Green, thought.Blue)));
            }

            return lines;
        }
        catch (Exception error)
        {
            Log.Error("thoughts.read", "An enemy's thoughts could not be read", error);
            return null;
        }
    }

    // Drawn while the hologram is framed for this view, flat on the screen.
    public static void Render(float time)
    {
        if (!Showing)
        {
            return;
        }

        Hologram.Pop();
        Draw.Place(0f, 0f, 1f);
        Draw.Fade(1f);
        foreach (var card in _cards)
        {
            if (!Things.Alive(card.Actor))
            {
                continue;
            }

            var shape = Things.ShapeOf(card.Actor);
            var top = shape.Middle + Vector3.UnitZ * shape.Half.Z;
            if (!Hologram.Screen(top, out var pixels))
            {
                continue;
            }

            Box(pixels / Draw.Scale, card.Lines);
        }
    }

    // A box beside the head, so the marker's own name card above it stays clear.
    static void Box(Vector2 at, List<Line> lines)
    {
        var width = 0f;
        foreach (var line in lines)
        {
            width = MathF.Max(width, Draw.TextWidth(line.Text, TextSize));
        }

        width += Pad * 2f;
        var height = lines.Count * LineHeight + Pad * 2f;
        var left = Math.Clamp(at.X + 14f, 4f, MathF.Max(4f, Draw.ScreenWidth - width - 4f));
        var top = Math.Clamp(at.Y - 4f, 4f, MathF.Max(4f, Draw.ScreenHeight - height - 4f));
        Draw.Rect(left, top, width, height, Ink with { A = 170 });
        Draw.Rect(left, top, 2f, height, new Rgba(255, 200, 90));
        for (var i = 0; i < lines.Count; i++)
        {
            Draw.Text(lines[i].Text, left + Pad + 1f, top + Pad + i * LineHeight, TextSize, lines[i].Color);
        }
    }
}
