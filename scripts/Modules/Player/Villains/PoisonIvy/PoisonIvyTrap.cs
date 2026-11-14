#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// Poison Ivy's flytrap, on secondary fire while she aims. It is her thug-eating plant from the
// Harley Quinn pack, planted where she aims. It waits until an enemy comes close, then bites them
// and swallows them whole, and keeps them. One meal each.
//
// The game's own eating only works on armed thugs in a predator room. Anyone else is frozen and
// taken down where they stand, and never pulled in. So the plant's side is played here as its
// script plays it, and the meal is knocked out and dragged into its head as it bites.
static partial class PoisonIvy
{
    const string HarleyPackage = "Playable_DLCHarleyQuinn";
    const string TrapClassPath = "BmScript.RThugEatingPlant";

    // The plant's own bite, which picks its move by how far away the meal is, and the control
    // that turns its head.
    const string BitePath = "BmScript.RThugEatingPlant.StartEatAnim";
    static readonly FName HeadTurn = new("RotationSkelNode");

    // How far away she can plant one, how close an enemy must come to be eaten, how long until
    // the next one, and how many can stand at once. The plant's own moves reach this far.
    const float TrapReach = 2500f;
    const float TrapBite = 550f;
    const float TrapWait = 6f;
    const int TrapsAtOnce = 3;

    // The jaws reach the meal this long after the bite starts. Then the meal is knocked out and
    // dragged into the plant's head over this long, and goes inside out of sight.
    static readonly FName Mouth = new("BONE_Head");
    static readonly FName MealBone = new("Bip01_Spine2");
    const float BiteReach = 0.25f;
    const float SwallowSeconds = 0.6f;
    const float SwallowFastest = 3000f;
    const int SwallowDamage = 1000;

    // A new plant breaks up through the ground and grows to full size over this long, and only
    // bites once it has.
    const float GrowSeconds = 0.6f;
    const float SeedSize = 0.05f;

    // After a meal a plant shows it inside for this long, then is hungry again.
    const float DigestSeconds = 3f;
    static readonly FName FullBlend = new("HasThugBlend");
    static readonly FName BiteBlend = new("EatBlend");

    sealed class Trap
    {
        public required RThugEatingPlantBase Plant;
        public float Born;
        public bool Grown;

        // How long it has stood grown, not counting while it eats, and when it began to sink.
        public float Lived;
        public bool Sinking;
        public float SinkAt;
        public RPawnVillain? Meal;
        public float BitAt;
        public bool Caught;
        public bool Swallowed;
        public float SwallowedAt;

        // Everyone it has eaten, kept out of sight inside it.
        public readonly List<RPawnVillain> Eaten = new();
    }

    static readonly List<Trap> Traps = new();

    static bool _trapTried;
    static Class? _trapClass;
    static bool _secondaryWas;
    static float _nextTrap;

    // Hungry plants look for a meal this often, as it means looking through every thug.
    const float FeedEvery = 0.2f;
    static float _nextFeed;

    // Taken by plants since she was turned on, for the HUD.
    static int _eaten;

    static bool TrapMissing => !GameFiles.HasPackage(HarleyPackage);

    static void LoadTrap()
    {
        if (_trapTried)
        {
            return;
        }

        _trapTried = true;
        if (TrapMissing)
        {
            Log.Info("Poison Ivy's flytrap needs the Harley Quinn pack, which isn't installed.");
            return;
        }

        if (Game.FindObject<Class>(TrapClassPath) is not { IsValid: true }
            && !Packages.TryLoad(HarleyPackage + "_SF", "the Harley Quinn pack for Poison Ivy's flytrap", "poison_ivy.trap_load"))
        {
            return;
        }

        _trapClass = Kept(Game.FindObject<Class>(TrapClassPath));
        if (_trapClass is null)
        {
            Log.Warn("poison_ivy.trap", "Poison Ivy's flytrap wasn't found in the Harley Quinn pack.");
        }
    }

    static void Plant(RPawnPlayer player)
    {
        // Holding secondary fire shows where the flytrap will grow, and letting go while still
        // aiming plants it.
        var released = !Aim.Secondary && _secondaryWas && Aim.On;
        _secondaryWas = Aim.Secondary;
        var now = World.Now;
        _trapSpot = Aim.Secondary && _trapClass is not null && Aim.Ground(player, TrapReach, out var spot) ? spot : null;
        if (released && now >= _nextTrap && _trapClass is not null && Aim.Ground(player, TrapReach, out var at))
        {
            var facing = Teleport.Flat(player.Location - at);
            var turn = facing.LengthSquared() > 1e-4f ? Rotator.FromDirection(facing) : new Rotator(0f, 0f, 0f);
            if (player.Spawn(_trapClass, player, FName.None, at, turn, null!, true) is RThugEatingPlantBase { IsValid: true } plant)
            {
                _nextTrap = now + TrapWait;
                _castUntil = now + CastSeconds;
                _plantedAt = now;
                plant.SetDrawScale(SeedSize);
                Puff(_burstLook, _burstSound, at);
                Traps.Add(new Trap { Plant = plant, Born = now });
                while (Traps.Count > TrapsAtOnce)
                {
                    Uproot(Traps[0]);
                    Traps.RemoveAt(0);
                }
            }
        }

        for (var i = Traps.Count - 1; i >= 0; i--)
        {
            var trap = Traps[i];
            Sprouting(trap);
            Age(trap);
            if (Sunk(trap))
            {
                Uproot(trap);
                Traps.RemoveAt(i);
            }
        }

        Feed(player);
        foreach (var trap in Traps)
        {
            Swallow(player, trap);
        }
    }

    // Grows fast and settles, like something forcing its way up.
    static void Sprouting(Trap trap)
    {
        if (trap.Grown || trap.Plant is not { IsValid: true, bDeleteMe: false } plant)
        {
            return;
        }

        var t = Math.Clamp((World.Now - trap.Born) / GrowSeconds, 0f, 1f);
        var eased = 1f - (1f - t) * (1f - t) * (1f - t);
        plant.SetDrawScale(SeedSize + (1f - SeedSize) * eased);
        trap.Grown = t >= 1f;
    }

    // A grown plant's time runs while it isn't eating. When it is up, the plant sinks back into
    // the ground the way it came up.
    static void Age(Trap trap)
    {
        if (!trap.Grown || trap.Sinking || trap.Meal is not null || trap.Plant is not { IsValid: true, bDeleteMe: false } plant)
        {
            return;
        }

        trap.Lived += World.Delta;
        if (trap.Lived >= (float)TrapLife.Value)
        {
            trap.Sinking = true;
            trap.SinkAt = World.Now;
            Puff(_burstLook, _sinkSound, plant.Location);
        }
    }

    // Shrinks slowly at first, then drops away. True once it is back in the ground.
    static bool Sunk(Trap trap)
    {
        if (!trap.Sinking || trap.Plant is not { IsValid: true, bDeleteMe: false } plant)
        {
            return trap.Sinking;
        }

        var t = Math.Clamp((World.Now - trap.SinkAt) / GrowSeconds, 0f, 1f);
        plant.SetDrawScale(SeedSize + (1f - SeedSize) * (1f - t * t * t));
        return t >= 1f;
    }

    // A one-off effect and its sound on the ground at a spot.
    static void Puff(ParticleSystem? look, AkEvent? sound, Vector3 at)
    {
        if (look is not { IsValid: true } || !Game.TryGetWorldInfo(out var world) || world.GRI?.ObjectPool is not { IsValid: true } pool)
        {
            return;
        }

        if (pool.SpawnEmitter(look, at, new Rotator(0f, 0f, 0f), 0, 1f) is { IsValid: true } shown && sound is { IsValid: true })
        {
            shown.StartAudioEvent(sound);
        }
    }

    // Each hungry plant bites the nearest enemy that comes in reach and isn't already some other
    // plant's meal.
    static void Feed(RPawnPlayer player)
    {
        if (World.Now < _nextFeed)
        {
            return;
        }

        _nextFeed = World.Now + FeedEvery;
        for (var i = Traps.Count - 1; i >= 0; i--)
        {
            if (Traps[i].Plant is not { IsValid: true, bDeleteMe: false })
            {
                Traps.RemoveAt(i);
            }
        }

        foreach (var trap in Traps)
        {
            if (trap.Meal is not null || !trap.Grown || trap.Sinking)
            {
                continue;
            }

            var plant = trap.Plant;
            var mouth = plant.Location + Vector3.UnitZ * 40f;
            RPawnVillain? best = null;
            var bestOff = TrapBite * TrapBite;
            foreach (var thug in Crowd.StandingEnemies())
            {
                if (thug.DrivenVehicle is { IsValid: true } || thug.Base is Vehicle || thug.bBeingTakenDown || Taken(thug))
                {
                    continue;
                }

                var off = Vector3.DistanceSquared(thug.Location, plant.Location);
                if (off < bestOff && player.FastTrace(thug.Location, mouth, Vector3.Zero, false))
                {
                    best = thug;
                    bestOff = off;
                }
            }

            if (best is not null)
            {
                Bite(trap, best);
            }
        }
    }

    static bool Taken(RPawnVillain thug)
    {
        foreach (var trap in Traps)
        {
            if (trap.Meal is { } meal && meal.Ptr == thug.Ptr)
            {
                return true;
            }
        }

        return false;
    }

    // The plant turns its head to the meal and bites with the move for how far away it is.
    static unsafe void Bite(Trap trap, RPawnVillain thug)
    {
        var plant = trap.Plant;
        if (plant.PlantMesh is { IsValid: true } mesh && mesh.FindSkelControl(HeadTurn) is SkelControlSingleBone { IsValid: true } head)
        {
            var way = Teleport.Flat(thug.Location - plant.Location);
            if (way.LengthSquared() > 1e-4f)
            {
                ref var turn = ref head.BoneRotation;
                turn.Yaw = Rotator.FromDirection(way).Yaw - plant.Rotation.Yaw;
            }
        }

        // The bite reads its target field to pick its move. The field's binding only takes armed
        // thugs, but the bite only reads where the target is, so any enemy is written there.
        *(nint*)(plant.Ptr + Offsets.FlytrapTarget) = thug.Ptr;
        if (!ScriptCall.Run(plant, BitePath))
        {
            return;
        }

        trap.Meal = thug;
        trap.BitAt = World.Now;
    }

    // As the jaws reach the meal it is knocked out, pulled into the plant's head and kept there.
    static void Swallow(RPawnPlayer player, Trap trap)
    {
        if (trap.Swallowed)
        {
            Digest(trap);
            return;
        }

        if (trap.Meal is not { IsValid: true, bDeleteMe: false } meal)
        {
            return;
        }

        var since = World.Now - trap.BitAt;
        if (since < BiteReach)
        {
            return;
        }

        var plant = trap.Plant;
        if (!trap.Caught)
        {
            trap.Caught = true;
            _eaten++;
            Say(player, "eat", always: true);
            Villains.Knock(player, meal, SwallowDamage, Vector3.UnitZ, 0f, fall: true);
        }

        if (since >= BiteReach + SwallowSeconds)
        {
            trap.Swallowed = true;
            trap.SwallowedAt = World.Now;
            trap.Eaten.Add(meal);
            meal.SetHidden(true);
            plant.ThugFinished();
            return;
        }

        if (meal.Mesh is not { IsValid: true } body || !meal.IsRagdoll())
        {
            return;
        }

        var mouth = plant.PlantMesh is { IsValid: true } trapMesh && trapMesh.MatchRefBone(Mouth) >= 0
            ? trapMesh.GetBoneLocation(Mouth, 0)
            : plant.Location + Vector3.UnitZ * 120f;
        var pull = (mouth - body.GetBoneLocation(MealBone, 0)) / MathF.Max(World.Delta, 1f / 120f);
        if (pull.LengthSquared() > SwallowFastest * SwallowFastest)
        {
            pull = Vector3.Normalize(pull) * SwallowFastest;
        }

        body.WakeRigidBody(FName.None);
        body.SetRBLinearVelocity(pull, false);
    }

    // Once the meal is down the plant closes back to its hungry look and can bite again.
    static void Digest(Trap trap)
    {
        if (World.Now - trap.SwallowedAt < DigestSeconds || trap.Plant is not { IsValid: true, bDeleteMe: false } plant)
        {
            return;
        }

        plant.bHasThugInside = false;
        if (plant.PlantMesh is { IsValid: true } mesh)
        {
            if (mesh.FindAnimNode(FullBlend) is AnimNodeBlend { IsValid: true } full)
            {
                full.SetBlendTarget(0f, 0.4f);
            }

            if (mesh.FindAnimNode(BiteBlend) is AnimNodeBlend { IsValid: true } bite)
            {
                bite.SetBlendTarget(0f, 0.4f);
            }
        }

        trap.Meal = null;
        trap.Caught = false;
        trap.Swallowed = false;
    }

    // A plant pulled up lets everyone it ate fall out where it stood.
    static void Uproot(Trap trap)
    {
        foreach (var meal in trap.Eaten)
        {
            if (meal is { IsValid: true, bDeleteMe: false })
            {
                meal.SetHidden(false);
            }
        }

        if (trap.Plant is { IsValid: true, bDeleteMe: false } plant)
        {
            plant.Destroy();
        }
    }

    static void StopTraps()
    {
        _secondaryWas = false;
    }

    static void UnloadTrap()
    {
        foreach (var trap in Traps)
        {
            Uproot(trap);
        }

        Traps.Clear();
        StopTraps();
        _trapClass = null;
        _trapTried = false;
        _eaten = 0;
    }
}
