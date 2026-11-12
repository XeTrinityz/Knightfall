#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// WORLD > ENEMIES, the MOVE rows and their FRIENDLIES twins. Each move takes a filter for who it
// picks.
static class Crowd
{
    const float Metre = Teleport.UnitsPerMetre;

    // Where you land behind someone, on foot and in the car.
    const float Behind = 170f;
    const float CarBehind = 600f;

    // The smallest pull ring, and the room each body takes on it.
    const float Ring = 320f;
    const float Spacing = 90f;

    // A pull spot can be this far above or below you.
    const float StepUp = 200f;
    const float StepDown = 500f;

    // Eye height for checking a pulled body can be seen from where you stand.
    const float Eyes = 60f;

    const float LaunchUp = 1800f;
    const float LaunchOut = 350f;

    // Each body tries its own spot first, then nearer, then further.
    static readonly float[] RingTries = { 1f, 0.7f, 1.35f };

    public static readonly Slider Range = new(
        "world.enemies.move_range", "PULL AND LAUNCH RANGE",
        "How far away PULL and LAUNCH reach, for enemies and friendlies.",
        min: 10, max: 300, step: 10, value: 50, unit: " M")
    {
        Preference = true,
    };

    public static readonly Slider Count = new(
        "world.enemies.move_count", "PULL AND LAUNCH COUNT",
        "The most PULL and LAUNCH move at once, nearest first, for enemies and friendlies.",
        min: 1, max: 50, step: 1, value: 12)
    {
        Preference = true,
    };

    public static readonly Command ToNearestEnemy = new(
        "world.enemies.to_nearest", "TELEPORT TO NEAREST ENEMY",
        "Puts you right behind the closest standing enemy, facing them.",
        () => ToNearest(IsEnemy));

    public static readonly Command PullEnemies = new(
        "world.enemies.pull", "PULL ENEMIES TO ME",
        "Brings the nearest enemies into a ring around you, each on open ground you can see.",
        () => Pull(IsEnemy));

    public static readonly Command LaunchEnemies = new(
        "world.enemies.launch", "LAUNCH ENEMIES",
        "Throws the nearest enemies high into the air.",
        () => Launch(IsEnemy));

    public static readonly Catalog.Section EnemySection = new("MOVE", ToNearestEnemy, PullEnemies, LaunchEnemies, Range, Count);

    public static readonly Command ToNearestFriendly = new(
        "world.friendlies.to_nearest", "TELEPORT TO NEAREST FRIENDLY",
        "Puts you right behind the closest friendly, facing them.",
        () => ToNearest(IsFriendly));

    public static readonly Command PullFriendlies = new(
        "world.friendlies.pull", "PULL FRIENDLIES TO ME",
        "Brings the nearest friendlies into a ring around you, each on open ground you can see.",
        () => Pull(IsFriendly));

    public static readonly Command LaunchFriendlies = new(
        "world.friendlies.launch", "LAUNCH FRIENDLIES",
        "Throws the nearest friendlies high into the air.",
        () => Launch(IsFriendly));

    public static bool IsEnemy(RBMPawnAI body) => body is RPawnVillain && !Bodyguards.Is(body);

    // Police, fire crews, your bodyguards and anyone else the game runs who isn't against you.
    public static bool IsFriendly(RBMPawnAI body) => body is not RPawnVillain || Bodyguards.Is(body);

    public static bool Standing(RBMPawnAI body) =>
        !body.bDeleteMe && !body.bHidden && body.Health > 0 && body is not RPawnVillain { bGoodAsDead: true };

    // Every enemy on their feet, for an attack to pick who it reaches.
    public static IEnumerable<RPawnVillain> StandingEnemies()
    {
        foreach (var thug in Game.FindObjects<RPawnVillain>())
        {
            if (thug.IsValid && !thug.IsClassDefaultObject && IsEnemy(thug) && Standing(thug))
            {
                yield return thug;
            }
        }
    }

    public static Outcome ToNearest(Func<RBMPawnAI, bool> side)
    {
        if (Teleport.Mover is not { } mover)
        {
            return Outcome.NoPlayer;
        }

        var picked = Nearest(side, mover.Location, float.MaxValue, 1);
        if (picked.Count == 0)
        {
            return Outcome.Failed("NOBODY THERE");
        }

        var target = picked[0];
        var back = mover is RVehicleBatmobileBase ? CarBehind : Behind;
        var ahead = Teleport.Flat(target.Rotation.ToDirection());
        var facing = new Rotator(0f, target.Rotation.Yaw, 0f);

        // Behind them if there's room, else beside them, else in front facing them.
        foreach (var (way, turn) in new[] { (-ahead, 0f), (Side(ahead), -90f), (-Side(ahead), 90f), (ahead, 180f) })
        {
            var spot = target.Location + way * back;
            if (mover.FastTrace(spot, target.Location, Vector3.Zero, false))
            {
                return Teleport.Hop(mover, spot, new Rotator(0f, facing.Yaw + turn, 0f), false);
            }
        }

        return Outcome.Failed("NO ROOM");
    }

    public static Outcome Pull(Func<RBMPawnAI, bool> side)
    {
        if (Teleport.Mover is not { } mover)
        {
            return Outcome.NoPlayer;
        }

        var here = mover.Location;
        var bodies = Nearest(side, here, (float)Range.Value * Metre, (int)Count.Value);
        if (bodies.Count == 0)
        {
            return Outcome.Failed("NOBODY IN RANGE");
        }

        var spots = RingSpots(mover, bodies.Count);
        var placed = 0;
        for (var i = 0; i < bodies.Count && i < spots.Count; i++)
        {
            var body = bodies[i];
            if (body.SetLocation(spots[i] + new Vector3(0f, 0f, Teleport.Lift(body))))
            {
                body.Velocity = Vector3.Zero;
                body.SetRotation(new Rotator(0f, Yaw(here - spots[i]), 0f));
                placed++;
            }
        }

        return placed > 0 ? Outcome.Done($"{placed} PULLED") : Outcome.Failed("NO ROOM AROUND YOU");
    }

    // Up to count floor points in a ring round the mover, each on open dry ground that can be
    // seen from where the mover stands. Fewer when there isn't room.
    public static List<Vector3> RingSpots(Pawn mover, int count)
    {
        var spots = new List<Vector3>();
        var here = mover.Location;

        // About a body's width apart, so nobody lands inside anyone else.
        var ring = MathF.Max(Ring, count * Spacing / MathF.Tau);
        var eyes = here + new Vector3(0f, 0f, Eyes);
        for (var i = 0; i < count; i++)
        {
            var angle = MathF.Tau * i / count;
            var way = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f);
            foreach (var scale in RingTries)
            {
                var spot = here + way * ring * scale;
                if (Teleport.Floor(mover, spot, here.Z + StepUp, here.Z - StepDown, out var floor) && Teleport.Dry(floor)
                    && mover.FastTrace(floor + new Vector3(0f, 0f, Eyes), eyes, Vector3.Zero, false))
                {
                    spots.Add(floor);
                    break;
                }
            }
        }

        return spots;
    }

    public static Outcome Launch(Func<RBMPawnAI, bool> side)
    {
        if (Teleport.Mover is not { } mover)
        {
            return Outcome.NoPlayer;
        }

        var bodies = Nearest(side, mover.Location, (float)Range.Value * Metre, (int)Count.Value);
        if (bodies.Count == 0)
        {
            return Outcome.Failed("NOBODY IN RANGE");
        }

        foreach (var body in bodies)
        {
            var angle = Random.Shared.NextSingle() * MathF.Tau;
            body.SetPhysics(Actor.EPhysics.PHYS_Falling, true);
            body.Velocity = new Vector3(MathF.Cos(angle) * LaunchOut, MathF.Sin(angle) * LaunchOut, LaunchUp);
        }

        return Outcome.Done($"{bodies.Count} LAUNCHED");
    }

    // Standing people of one side, nearest first.
    static List<RBMPawnAI> Nearest(Func<RBMPawnAI, bool> side, Vector3 at, float reach, int most)
    {
        var found = new List<(RBMPawnAI Body, float Away)>();
        foreach (var body in Game.FindObjects<RBMPawnAI>())
        {
            if (!body.IsValid || body.IsClassDefaultObject || !Standing(body) || !side(body))
            {
                continue;
            }

            var away = Vector3.DistanceSquared(body.Location, at);
            if (away <= reach * reach)
            {
                found.Add((body, away));
            }
        }

        found.Sort((a, b) => a.Away.CompareTo(b.Away));
        var picked = new List<RBMPawnAI>();
        for (var i = 0; i < found.Count && i < most; i++)
        {
            picked.Add(found[i].Body);
        }

        return picked;
    }

    static Vector3 Side(Vector3 ahead) => new(-ahead.Y, ahead.X, 0f);

    // BmSDK's Rotator is in degrees.
    static float Yaw(Vector3 way) => MathF.Atan2(way.Y, way.X) * 180f / MathF.PI;
}
