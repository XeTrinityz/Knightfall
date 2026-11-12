#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// WORLD > ENEMIES, FREEZE ENEMIES and FREEZE FRIENDLIES. Stopping someone's tick isn't enough on
// its own in this game, something still carries them along. So each one also has its animation
// paused, its speed taken away and is put back where it froze, every tick. Someone knocked out
// is let go so they can fall.
sealed class Freezer
{
    // How often new people are looked for.
    const float LookEvery = 0.25f;

    // Further than this from where they froze, the game has moved them on purpose, like a respawn,
    // and they're frozen again where they are now.
    const float Moved = 2000f;
    const float Drift = 4f;

    // What each frozen person had, so it is put back exactly.
    sealed record Hold(RBMPawnAI Body, Vector3 At, bool BodyOff, Controller? Brain, bool BrainOff, SkeletalMeshComponent? Mesh, bool Paused);

    public static readonly Freezer Enemies = new(
        "world.enemies.freeze", "FREEZE ENEMIES",
        "Every enemy on foot stops where they are, mid-swing or mid-shot, until you switch it off. A knocked out enemy still falls.",
        Crowd.IsEnemy);

    public static readonly Freezer Friendlies = new(
        "world.friendlies.freeze", "FREEZE FRIENDLIES",
        "Every friendly on foot, police and fire crews too, stops where they are until you switch it off.",
        Crowd.IsFriendly);

    readonly Func<RBMPawnAI, bool> _side;
    readonly Dictionary<nint, Hold> _holds = new();
    readonly List<nint> _done = new();
    float _lookedAt = -1f;

    Freezer(string id, string label, string help, Func<RBMPawnAI, bool> side)
    {
        _side = side;
        Option = new Toggle(id, label, help, saved: false)
        {
            Tick = Keep,
            Restore = Thaw,
        };
    }

    public Toggle Option { get; }

    void Keep()
    {
        if (MathF.Abs(World.Now - _lookedAt) >= LookEvery)
        {
            _lookedAt = World.Now;
            foreach (var body in Game.FindObjects<RBMPawnAI>())
            {
                if (body.IsValid && !body.IsClassDefaultObject && Crowd.Standing(body) && _side(body) && !_holds.ContainsKey(body.Ptr))
                {
                    Start(body);
                }
            }
        }

        foreach (var (pointer, hold) in _holds)
        {
            var body = hold.Body;
            if (!body.IsValid || !Crowd.Standing(body) || Vector3.DistanceSquared(body.Location, hold.At) > Moved * Moved)
            {
                _done.Add(pointer);
                continue;
            }

            Stop(hold);
            if (Vector3.DistanceSquared(body.Location, hold.At) > Drift * Drift)
            {
                body.SetLocation(hold.At);
            }
        }

        foreach (var pointer in _done)
        {
            if (_holds.Remove(pointer, out var hold))
            {
                Let(hold);
            }
        }

        _done.Clear();
    }

    void Start(RBMPawnAI body)
    {
        var hold = Make(body);
        _holds[body.Ptr] = hold;
        Stop(hold);
    }

    // One person held still on their own, like someone the editor is carrying. Unpause puts back
    // exactly what they had.
    public static object Pause(RBMPawnAI body)
    {
        var hold = Make(body);
        Stop(hold);
        return hold;
    }

    public static void Unpause(object paused)
    {
        if (paused is Hold hold)
        {
            Let(hold);
        }
    }

    static Hold Make(RBMPawnAI body)
    {
        var brain = body.Controller is { IsValid: true } controller ? controller : null;
        var mesh = body.Mesh is { IsValid: true } skeleton ? skeleton : null;
        return new Hold(body, body.Location, body.bTickIsDisabled, brain, brain?.bTickIsDisabled ?? false, mesh, mesh?.bPauseAnims ?? false);
    }

    static void Stop(Hold hold)
    {
        if (!hold.Body.bTickIsDisabled)
        {
            hold.Body.SetTickIsDisabled(true);
        }

        if (hold.Brain is { IsValid: true } brain && !brain.bTickIsDisabled)
        {
            brain.SetTickIsDisabled(true);
        }

        if (hold.Mesh is { IsValid: true } mesh && !mesh.bPauseAnims)
        {
            mesh.bPauseAnims = true;
        }

        hold.Body.Velocity = Vector3.Zero;
    }

    static void Let(Hold hold)
    {
        if (hold.Body.IsValid && hold.Body.bTickIsDisabled != hold.BodyOff)
        {
            hold.Body.SetTickIsDisabled(hold.BodyOff);
        }

        if (hold.Brain is { IsValid: true } brain && brain.bTickIsDisabled != hold.BrainOff)
        {
            brain.SetTickIsDisabled(hold.BrainOff);
        }

        if (hold.Mesh is { IsValid: true } mesh)
        {
            mesh.bPauseAnims = hold.Paused;
        }
    }

    void Thaw()
    {
        foreach (var hold in _holds.Values)
        {
            Let(hold);
        }

        _holds.Clear();
        _lookedAt = -1f;
    }
}
