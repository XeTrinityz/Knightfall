#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// WORLD > ENEMIES. The tab's sections, and the rows that act on the game's managers or on the
// enemies near you.
static class Enemies
{
    // What counts as near you for DISARM and REVIVE.
    const float NearRange = 9000f;

    // The game's own switch for scenes where enemies mustn't attack.
    static readonly Held<RBMCombatManager, bool> Attacks = new(
        "enemy attacks", combat => combat.bCanAttack, (combat, can) => combat.bCanAttack = can);

    // The debug one is checked as well by thrown weapons and a few others.
    static readonly Held<RPersistentDebugData, bool> CanAttack = new(
        "enemies can attack", data => data.Debug_Combat_CanAttack, (data, can) => data.Debug_Combat_CanAttack = can);

    // The game turns this off with attacks too, so enemies don't react to you as a threat either.
    static readonly Held<RPersistentDebugData, bool> React = new(
        "enemies react", data => data.bReactionBehavioursPlayer, (data, react) => data.bReactionBehavioursPlayer = react);

    // Taken off the most patrols the game lets the city have at once. Past the most there can
    // be, there are none.
    static readonly Held<RPopulationManager, int> PatrolCut = new(
        "militia patrols", people => people.VehicleCountReduction, (people, cut) => people.VehicleCountReduction = cut);

    static readonly int[] Cuts = { 0, 100, -6 };

    public static readonly Toggle NoAttacks = new(
        "world.enemies.cant_attack", "ENEMIES CAN'T ATTACK",
        "Enemies still chase you and gather round, but never swing or shoot.")
    {
        Tick = HoldNoAttacks,
        Restore = LetAttack,
    };

    public static readonly Command KnockOut = new(
        "world.enemies.knock_out_all", "KNOCK OUT ALL ENEMIES",
        "Takes down every enemy at once. One you can interrogate is always left standing, so the story can't get stuck.",
        KnockOutAll);

    public static readonly Command KnockOutAllButOne = new(
        "world.enemies.knock_out_all_but_one", "KNOCK OUT ALL BUT ONE",
        "Takes down every enemy in the fight around you except one.",
        AllButOne);

    public static readonly Command RemoveWeapons = new(
        "world.enemies.remove_weapons", "REMOVE ALL WEAPONS",
        "Clears every gun and melee weapon off the area, in hands or on the ground.",
        Unarm);

    public static readonly Command Disarm = new(
        "world.enemies.disarm", "DISARM ENEMIES",
        "Enemies near you drop what they're holding.",
        () => DisarmNear(Crowd.IsEnemy));

    public static readonly Command Revive = new(
        "world.enemies.revive", "REVIVE ENEMIES",
        "Knocked out enemies near you get back up with full health, as when a medic revives them.",
        () => ReviveNear(Crowd.IsEnemy));

    public static readonly Command DisarmFriendlies = new(
        "world.friendlies.disarm", "DISARM FRIENDLIES",
        "Friendlies near you drop what they're holding.",
        () => DisarmNear(Crowd.IsFriendly));

    public static readonly Command ReviveFriendlies = new(
        "world.friendlies.revive", "REVIVE FRIENDLIES",
        "Knocked out friendlies near you get back up with full health.",
        () => ReviveNear(Crowd.IsFriendly));

    public static readonly Choice Patrols = new(
        "world.enemies.militia_patrols", "MILITIA PATROLS",
        "How many drone, tank and APC patrols the militia sends out. NONE stops new ones, and the ones already out stay.",
        new[] { ("normal", "NORMAL"), ("none", "NONE"), ("more", "MORE") })
    {
        Tick = HoldPatrols,
        Restore = PatrolCut.Release,
    };

    public static readonly Command DestroyVehicles = new(
        "world.enemies.destroy_militia_vehicles", "DESTROY MILITIA VEHICLES",
        "Destroys every militia drone, tank and APC there is.",
        DestroyAll);

    public static readonly Catalog.Section[] Sections =
    {
        new("CONTROL", Freezer.Enemies.Option, NoAttacks, EnemyHolds.BodySize, EnemyHolds.Healthy, Disarm, Revive),
        Crowd.EnemySection,
        new("BEHAVIOUR", EnemyHolds.NewHealth, EnemyHolds.Aggressive, EnemyHolds.NoRevives, EnemyHolds.NoElectrifying, EnemyHolds.NoDynamicDifficulty),
        new("MILITIA", Patrols, DestroyVehicles, Militia.HackTanks, Militia.HackTime, Militia.ReleaseTanks, Militia.DisableTurrets, Militia.DestroySentries),
        new("INSTANT", KnockOut, KnockOutAllButOne, RemoveWeapons),
    };

    // WORLD > FRIENDLIES, the same rows for police, fire crews and anyone else not against you.
    public static readonly Catalog.Section[] FriendlySections =
    {
        new("CONTROL", Freezer.Friendlies.Option, EnemyHolds.FriendlyBodySize, EnemyHolds.FriendliesHealthy, DisarmFriendlies, ReviveFriendlies),
        new("MOVE", Crowd.ToNearestFriendly, Crowd.PullFriendlies, Crowd.LaunchFriendlies, Crowd.Range, Crowd.Count),
    };

    static void HoldNoAttacks()
    {
        if (World.Info?.CombatManager is { IsValid: true } combat)
        {
            Attacks.Hold(combat, false);
        }

        if (World.Player?.GetPDD() is { IsValid: true } data)
        {
            CanAttack.Hold(data, false);
            React.Hold(data, false);
        }
    }

    static void LetAttack()
    {
        Attacks.Release();
        CanAttack.Release();
        React.Release();
    }

    static void HoldPatrols()
    {
        if (World.Info?.PopulationManager is { IsValid: true } people)
        {
            PatrolCut.Hold(people, Cuts[Math.Clamp(Patrols.Index, 0, Cuts.Length - 1)]);
        }
    }

    static Outcome KnockOutAll()
    {
        if (World.Info is not { } game || World.Controller is not { } controller)
        {
            return Outcome.NoPlayer;
        }

        if (game.AIManager is not { IsValid: true } brains)
        {
            return Outcome.Failed("NOT NOW");
        }

        brains.KillAllEnemies(controller);
        return Outcome.Done("DOWN");
    }

    // The game's own version only counts enemies in the indoor room you're in, so it finds no one
    // outside. This takes the same steps as its knock out on the hostile enemies near you, and
    // leaves one standing: one you can interrogate if there is one, or else the nearest.
    static Outcome AllButOne()
    {
        if (World.Info is not { } game || World.Controller is not { } controller || World.Player is not { IsValid: true } you)
        {
            return Outcome.NoPlayer;
        }

        if (game.bSavingStartPoints)
        {
            return Outcome.Failed("NOT NOW");
        }

        var cheats = Game.GetCheatManager();
        var fighting = new List<RPawnVillain>();
        foreach (var thug in Near(you.Location, Crowd.IsEnemy).OfType<RPawnVillain>())
        {
            if (Crowd.Standing(thug) && thug.IsHostile() && thug.CanDebugKill() && !cheats.IsPawnImmuneToDebugKIll(thug))
            {
                fighting.Add(thug);
            }
        }

        if (fighting.Count < 2)
        {
            return Outcome.Unchanged(fighting.Count == 0 ? "NO ENEMIES" : "ONLY ONE");
        }

        var spared = fighting.Find(thug => thug.IsPotentiallyInterrogatable())
            ?? fighting.MinBy(thug => Vector3.DistanceSquared(thug.Location, you.Location))!;

        game.CombatManager?.DisableDDATracking();
        foreach (var thug in fighting)
        {
            if (!ReferenceEquals(thug, spared))
            {
                thug.Disarm();
                thug.TakeDamage(thug.Health, controller, thug.Location, Vector3.Zero, RDmgType_DebugKill.StaticClass());
            }
        }

        game.CombatManager?.EnableDDATracking();
        return Outcome.Done($"{fighting.Count - 1} DOWN");
    }

    static Outcome Unarm()
    {
        if (World.Info?.AIManager is not { IsValid: true } brains)
        {
            return Outcome.NoPlayer;
        }

        brains.DestroyAllWeapons();
        return Outcome.Done("REMOVED");
    }

    static Outcome DisarmNear(Func<RBMPawnAI, bool> side)
    {
        if (World.Player is not { IsValid: true } you)
        {
            return Outcome.NoPlayer;
        }

        var count = 0;
        foreach (var body in Near(you.Location, side))
        {
            if (Crowd.Standing(body) && body.CurrWeapon is { IsValid: true })
            {
                body.ForceDisarm();
                count++;
            }
        }

        return count > 0 ? Outcome.Done($"{count} DISARMED") : Outcome.Unchanged("NONE ARMED");
    }

    // The game's own wake-up. Hidden ones are back in the pool, not knocked out.
    static Outcome ReviveNear(Func<RBMPawnAI, bool> side)
    {
        if (World.Player is not { IsValid: true } you)
        {
            return Outcome.NoPlayer;
        }

        var count = 0;
        foreach (var body in Near(you.Location, side))
        {
            if (!body.bHidden && !Crowd.Standing(body))
            {
                body.WakeFromDeadWithDefaults();
                count++;
            }
        }

        return count > 0 ? Outcome.Done($"{count} REVIVED") : Outcome.Unchanged("NONE DOWN");
    }

    static List<RBMPawnAI> Near(Vector3 at, Func<RBMPawnAI, bool> side)
    {
        var near = new List<RBMPawnAI>();
        foreach (var body in Game.FindObjects<RBMPawnAI>())
        {
            if (body.IsValid && !body.bDeleteMe && !body.IsClassDefaultObject && side(body) && Vector3.DistanceSquared(body.Location, at) < NearRange * NearRange)
            {
                near.Add(body);
            }
        }

        return near;
    }

    static Outcome DestroyAll()
    {
        if (World.Info?.VehicleCombatManager is not { IsValid: true } vehicles)
        {
            return Outcome.NoPlayer;
        }

        vehicles.DebugKillAll();
        return Outcome.Done("DESTROYED");
    }
}
