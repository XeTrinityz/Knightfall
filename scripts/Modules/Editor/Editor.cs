#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// EDITOR > SELECT. One object at a time is selected, and every editor row works on it. Picking
// looks from the camera, so it works the same on foot, driving or with the free camera.
static class Editor
{
    public const string Page = "EDITOR";

    const float Metre = Teleport.UnitsPerMetre;
    const float Reach = 30000f;
    const int Layers = 8;
    const float StepPast = 60f;

    // How far off the middle of the screen something can be and still be what you aim at.
    const float AimCone = 5f;

    const int ListMost = 40;
    const float PreviewEvery = 0.2f;
    const float PruneEvery = 5f;

    // A selected person the game has hidden for this long is back in its pool.
    const float GoneAfter = 3f;

    static Actor? _selected;
    static Actor? _preview;
    static float _previewAt = -1f;
    static float _prunedAt = -1f;
    static float _hiddenAt = -1f;
    static string _pickSeen = "";
    static bool _relistDue;
    static readonly Dictionary<string, Actor> Listed = new();

    public static Actor? Selected => Things.Alive(_selected) ? _selected : null;

    // What SELECT AIMED would take, while the EDITOR page is open.
    public static Actor? Preview => Things.Alive(_preview) ? _preview : null;

    public static readonly Command SelectAimed = new(
        "editor.select.aimed", "SELECT AIMED",
        "Selects what's in the middle of the screen. While this page is open a white bracket shows what it would take.",
        () => Aimed() is { } actor ? Chose(actor) : Outcome.Failed("NOTHING THERE"));

    public static readonly Choice Pick = new(
        "editor.select.selected", "SELECTED",
        "The object every editor row works on. Step through what's nearby, nearest first. A * marks one you've changed.",
        new[] { ("", "NONE") }, saved: false)
    {
        Opening = List,
    };

    public static readonly Choice From = new(
        "editor.select.from", "SELECT FROM",
        "Which kinds of thing SELECT AIMED and the SELECTED list pick from.",
        new[] { ("all", "EVERYTHING") }.Concat(Things.Plurals.Select((name, i) => (((Things.Kind)i).ToString().ToLowerInvariant(), name))).ToArray())
    {
        Preference = true,
    };

    public static readonly Slider Range = new(
        "editor.select.range", "RANGE",
        "How far away the editor looks for things to select.",
        min: 10, max: 300, step: 10, value: 100, unit: " M")
    {
        Preference = true,
    };

    public static readonly Command Clear = new(
        "editor.select.clear", "CLEAR SELECTION",
        "Lets go of the selected object. Its changes stay, UNDO and RESTORE take them back.",
        () =>
        {
            if (Selected is null)
            {
                return Outcome.Unchanged("NOTHING SELECTED");
            }

            Select(null);
            return Outcome.Done("CLEARED");
        })
    {
        Block = static () => Selected is null ? "NOTHING SELECTED" : null,
    };

    public static readonly Command Undo = new(
        "editor.restore.undo", "UNDO",
        "Takes back the last change the editor made, on whichever object it was. Press again to go further back, up to 40 changes.",
        () =>
        {
            Moving.LetGo();
            var outcome = Edits.StepBack();
            Moving.Selected(Selected);
            _relistDue = true;
            return outcome;
        })
    {
        Block = static () => Edits.CanStepBack ? null : "NOTHING TO UNDO",
    };

    public static readonly Command RestoreSelected = new(
        "editor.restore.selected", "RESTORE SELECTED",
        "Puts the selected object back where and how it was before you changed it. UNDO brings the changes back.",
        () =>
        {
            if (Selected is not { } actor)
            {
                return Outcome.Failed("NOTHING SELECTED");
            }

            Moving.LetGo();
            var outcome = Edits.Restore(actor);
            Moving.Selected(actor);
            _relistDue = true;
            return outcome;
        })
    {
        Block = static () => Selected is not { } actor ? "NOTHING SELECTED" : Edits.Changed(actor) ? null : "NOTHING CHANGED",
    };

    public static readonly Command RestoreAll = new(
        "editor.restore.all", "RESTORE EVERYTHING",
        "Puts every object you've changed back the way it was. UNDO can't bring them back, so press it twice.",
        () =>
        {
            if (!Edits.All.Any())
            {
                return Outcome.Unchanged("NOTHING CHANGED");
            }

            if (!Confirm.Second("restore everything"))
            {
                return Outcome.Unchanged("PRESS AGAIN");
            }

            Moving.LetGo();
            var count = Edits.RestoreAll();
            Moving.Selected(Selected);
            _relistDue = true;
            return count > 0 ? Outcome.Done($"{count} RESTORED") : Outcome.Unchanged("NOTHING CHANGED");
        })
    {
        Block = static () => Edits.Any ? null : "NOTHING CHANGED",
    };

    public static readonly Toggle SelectSpawns = new(
        "spawner.spawned.select_spawns", "SELECT WHAT YOU SPAWN",
        "Each new spawn becomes the editor's selection, ready to move.")
    {
        Preference = true,
    };

    public static readonly Command CopySelected = new(
        "spawner.spawned.copy", "SPAWN COPY OF SELECTED",
        "Spawns another of the editor's selected person, vehicle or object, the way PLACE says.",
        Copy);

    public static readonly Catalog.Section[] Sections =
    {
        new("PICK", SelectAimed, Pick, From, Range, Clear),
        new("UNDO", Undo, RestoreSelected, RestoreAll),
    };

    // The rows SPAWNER > SPAWNED shows for the editor.
    public static readonly Catalog.Section SpawnerSection = new("EDITOR", Spawned.EditIt, SelectSpawns, CopySelected);

    public static void Select(Actor? actor)
    {
        if (ReferenceEquals(actor, _selected))
        {
            return;
        }

        _selected = actor;
        _hiddenAt = -1f;
        Moving.Selected(actor);
        _relistDue = true;
    }

    // Called for every new spawn.
    public static void NewSpawn(Actor actor)
    {
        if (SelectSpawns.On)
        {
            Select(actor);
        }
    }

    static Outcome Chose(Actor actor)
    {
        Select(actor);
        return Outcome.Done(Things.NameOf(actor));
    }

    static Things.Kind? Only() => From.Index == 0 ? null : (Things.Kind)(From.Index - 1);

    public static void Tick()
    {
        if (_selected is not null && !Things.Alive(_selected))
        {
            Select(null);
        }

        // The game hides a person when it takes them back into its pool, where they may come out
        // again as somebody else. REMOVE hides them too, and those stay selected.
        if (_selected is RBMPawnAI { bHidden: true } && Edits.Find(_selected) is not { Removed: true })
        {
            if (_hiddenAt < 0f || World.Now < _hiddenAt)
            {
                _hiddenAt = World.Now;
            }
            else if (World.Now - _hiddenAt >= GoneAfter)
            {
                Select(null);
            }
        }
        else
        {
            _hiddenAt = -1f;
        }

        // The player stepped through SELECTED.
        if (Pick.CurrentId != _pickSeen)
        {
            _pickSeen = Pick.CurrentId;
            Select(Pick.CurrentId.Length > 0 && Listed.TryGetValue(Pick.CurrentId, out var listed) && Things.Alive(listed) ? listed : null);
        }

        if (_relistDue && Menu.Showing)
        {
            _relistDue = false;
            List();
        }

        if ((Menu.OpenPage == Page || (EditorMode.Active && !Menu.Showing)) && !World.Paused)
        {
            if (MathF.Abs(World.Now - _previewAt) >= PreviewEvery)
            {
                _previewAt = World.Now;
                _preview = Aimed();
            }
        }
        else
        {
            _preview = null;
        }

        if (MathF.Abs(World.Now - _prunedAt) >= PruneEvery)
        {
            _prunedAt = World.Now;
            Edits.Prune();
        }

        Moving.Tick();
        Change.Tick();
        Markings.Tick();
    }

    public static void Release()
    {
        Moving.LetGo();
        Edits.Release();
        _preview = null;
    }

    static void List()
    {
        Listed.Clear();
        var options = new List<(string Id, string Label)> { ("", "NONE") };
        var selected = Selected;
        var index = 0;
        if (World.Controller is { } controller)
        {
            controller.GetPlayerViewPoint(out var eye, out _);
            var only = Only();
            var found = Things.Near(eye, (float)Range.Value * Metre, only);
            var shown = found.Take(ListMost).ToList();
            if (selected is not null && !shown.Any(item => ReferenceEquals(item.Actor, selected)) && Things.KindOf(selected) is { } kind)
            {
                shown.Insert(0, new Things.Found(selected, kind, Vector3.Distance(selected.Location, eye)));
            }

            // Removed things are hidden from the scan, but they stay listed so they can come back.
            foreach (var edit in Edits.All)
            {
                if (edit.Removed && !shown.Any(item => ReferenceEquals(item.Actor, edit.Actor)) && Things.KindOf(edit.Actor) is { } gone
                    && (only is null || gone == only))
                {
                    shown.Add(new Things.Found(edit.Actor, gone, Vector3.Distance(edit.Actor.Location, eye)));
                }
            }

            foreach (var item in shown)
            {
                var id = item.Actor.Ptr.ToString();
                var mark = Edits.Changed(item.Actor) ? " *" : "";
                Listed[id] = item.Actor;
                options.Add((id, $"{Things.Labels[(int)item.Kind]} {Things.NameOf(item.Actor)}  {item.Distance / Metre:0} M{mark}"));
                if (ReferenceEquals(item.Actor, selected))
                {
                    index = options.Count - 1;
                }
            }
        }

        Pick.SetOptions(options.ToArray(), index);
        _pickSeen = Pick.CurrentId;
    }

    // What's in the middle of the screen: whatever the view runs into first, or failing that the
    // thing nearest the middle within a few degrees, which catches people and thin things a line
    // slips past. Nothing behind a wall counts.
    public static Actor? Aimed() => Aimed(Only());

    // Wanted narrows the kinds further, for callers that only want, say, people.
    public static Actor? Aimed(Things.Kind? only, Func<Things.Kind, bool>? wanted = null)
    {
        if (World.Controller is not { } controller || Teleport.Mover is not { } mover)
        {
            return null;
        }

        controller.GetPlayerViewPoint(out var eye, out var view);
        var way = view.ToDirection();
        var range = (float)Range.Value * Metre;
        var start = eye;
        var blocked = range;
        for (var i = 0; i < Layers; i++)
        {
            var hit = mover.Trace(out var spot, out _, eye + way * Reach, start, Component.ECollisionFilter.COF_TraceAll, Vector3.Zero, out _, 0);
            if (hit is null)
            {
                break;
            }

            if (hit is Volume or WorldInfo || ReferenceEquals(hit, World.Player) || ReferenceEquals(hit, World.Driving))
            {
                start = spot + way * StepPast;
                continue;
            }

            if (!hit.bHidden && Things.KindOf(hit) is { } kind && (only is null || kind == only) && (wanted?.Invoke(kind) ?? true))
            {
                return hit;
            }

            blocked = MathF.Min(blocked, Vector3.Distance(eye, spot));
            break;
        }

        Actor? best = null;
        var closest = AimCone;
        foreach (var item in Things.Near(eye, range, only, wanted))
        {
            Things.Box(item.Actor, out var middle, out var half);
            var toward = middle - eye;
            var distance = toward.Length();
            if (distance < 1f || distance - MathF.Max(half.X, half.Y) > blocked)
            {
                continue;
            }

            var angle = MathF.Acos(Math.Clamp(Vector3.Dot(toward / distance, way), -1f, 1f)) * 180f / MathF.PI;
            if (angle < closest)
            {
                closest = angle;
                best = item.Actor;
            }
        }

        return best;
    }

    static Outcome Copy()
    {
        return Selected switch
        {
            null => Outcome.Failed("NOTHING SELECTED"),
            RBMPawnAI body => People.Copy(body),
            RVehicleNPC vehicle => Vehicles.Copy(vehicle),
            DynamicSMActor dynamic => Objects.Copy(dynamic),
            _ => Outcome.Failed("CAN'T COPY THAT"),
        };
    }
}
