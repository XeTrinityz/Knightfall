#nullable enable

using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// WORLD > ENEMIES, the rows that hold something on every enemy or friendly. One pass over
// everyone does them all, and each gets back exactly what they had.
static class EnemyHolds
{
    // New enemies are picked up this quickly.
    const float LookEvery = 0.25f;

    static readonly Held<Actor, float> Size = new(
        "enemy size", body => body.DrawScale, (body, scale) => body.SetDrawScale(scale));

    static readonly Held<Actor, float> FriendSize = new(
        "friendly size", body => body.DrawScale, (body, scale) => body.SetDrawScale(scale));

    static readonly Held<RBMPawnAI, bool> AttackAtOnce = new(
        "enemy attacks at once", thug => thug.bAttackASAP, (thug, now) => thug.bAttackASAP = now);

    static readonly Held<RPawnVillain, float> AttackWait = new(
        "enemy attack wait", thug => thug.AttackDelay, (thug, seconds) => thug.AttackDelay = seconds);

    static readonly Held<RPawnVillain, bool> Revivable = new(
        "enemy can be revived", thug => thug.bCanBeRevived, (thug, can) => thug.bCanBeRevived = can);

    // The combat manager's own switch, which the medics check before electrifying anyone.
    static readonly Held<RBMCombatManager, bool> NoCharging = new(
        "medic electrifying off", combat => combat.bDisableMedicChargeUpThug, (combat, off) => combat.bDisableMedicChargeUpThug = off);

    // Every enemy's health is multiplied by this when they show up.
    static readonly Held<RBMCombatManager, float> HealthScale = new(
        "enemy health", combat => combat.PawnHealthMultiplier, (combat, scale) => combat.PawnHealthMultiplier = scale);

    static readonly Held<RPersistentDebugData, bool> NoDda = new(
        "no dynamic difficulty", data => data.Debug_AI_IgnoreDDA, (data, on) => data.Debug_AI_IgnoreDDA = on);

    // The most health each healed person has had, so a boost from the game is kept.
    static readonly Dictionary<nint, int> FullHealth = new();
    static readonly Dictionary<nint, int> FriendFullHealth = new();

    static float _lookedAt = -1f;

    public static readonly Slider BodySize = new(
        "world.enemies.body_size", "ENEMY BODY SIZE",
        "Makes every enemy smaller or bigger. Only how they look, not how they fight.",
        min: 0.5, max: 2, step: 0.05, value: 1, unit: "X")
    {
        Restore = Size.Release,
    };

    public static readonly Toggle Healthy = new(
        "world.enemies.keep_healthy", "KEEP ENEMIES HEALTHY",
        "Enemies keep healing back to full. One big enough hit can still knock them out.")
    {
        Restore = FullHealth.Clear,
    };

    public static readonly Slider FriendlyBodySize = new(
        "world.friendlies.body_size", "FRIENDLY BODY SIZE",
        "Makes every friendly smaller or bigger. Only how they look.",
        min: 0.5, max: 2, step: 0.05, value: 1, unit: "X")
    {
        Restore = FriendSize.Release,
    };

    public static readonly Toggle FriendliesHealthy = new(
        "world.friendlies.keep_healthy", "KEEP FRIENDLIES HEALTHY",
        "Friendlies keep healing back to full. One big enough hit can still knock them out.")
    {
        Restore = FriendFullHealth.Clear,
    };

    public static readonly Slider NewHealth = new(
        "world.enemies.new_health", "NEW ENEMY HEALTH",
        "How much health enemies have when they show up, from now on. Ones already there keep theirs.",
        min: 0.1, max: 3, step: 0.1, value: 1, unit: "X")
    {
        Tick = () =>
        {
            if (World.Info?.CombatManager is { IsValid: true } combat)
            {
                HealthScale.Hold(combat, was => was * (float)NewHealth!.Value);
            }
        },
        Restore = HealthScale.Release,
    };

    public static readonly Toggle Aggressive = new(
        "world.enemies.aggressive", "AGGRESSIVE ENEMIES",
        "Enemies attack as soon as they can instead of waiting their turn.")
    {
        Restore = () =>
        {
            AttackAtOnce.Release();
            AttackWait.Release();
        },
    };

    public static readonly Toggle NoRevives = new(
        "world.enemies.no_medic_revives", "NO MEDIC REVIVES",
        "Medics can't wake up enemies you knocked out.")
    {
        Restore = Revivable.Release,
    };

    public static readonly Toggle NoElectrifying = new(
        "world.enemies.no_medic_electrifying", "NO MEDIC ELECTRIFYING",
        "Medics can't electrify thugs, and thugs already electrified lose it.")
    {
        Tick = () =>
        {
            if (World.Info?.CombatManager is { IsValid: true } combat)
            {
                NoCharging.Hold(combat, true);
            }
        },
        Restore = NoCharging.Release,
    };

    public static readonly Toggle NoDynamicDifficulty = new(
        "world.enemies.no_dynamic_difficulty", "NO DYNAMIC DIFFICULTY",
        "Stops the game quietly making fights easier after you struggle, so enemies stay as tough as your difficulty.")
    {
        Tick = () =>
        {
            if (Combat.DebugData() is { } data)
            {
                NoDda.Hold(data, true);
            }
        },
        Restore = NoDda.Release,
    };

    public static void Tick()
    {
        var size = !BodySize.IsDefault;
        var healthy = Healthy.On;
        var aggressive = Aggressive.On;
        var noRevives = NoRevives.On;
        var noCharge = NoElectrifying.On;
        var friendSize = !FriendlyBodySize.IsDefault;
        var friendHealthy = FriendliesHealthy.On;
        if (!size && !healthy && !aggressive && !noRevives && !noCharge && !friendSize && !friendHealthy || MathF.Abs(World.Now - _lookedAt) < LookEvery)
        {
            return;
        }

        _lookedAt = World.Now;
        foreach (var body in Game.FindObjects<RBMPawnAI>())
        {
            if (!body.IsValid || body.bDeleteMe || body.IsClassDefaultObject)
            {
                continue;
            }

            if (body is not RPawnVillain thug)
            {
                if (friendSize)
                {
                    FriendSize.Hold(body, was => was * (float)FriendlyBodySize.Value);
                }

                if (friendHealthy)
                {
                    Heal(body, FriendFullHealth);
                }

                continue;
            }

            if (size)
            {
                Size.Hold(thug, was => was * (float)BodySize.Value);
            }

            if (aggressive)
            {
                AttackAtOnce.Hold(thug, true);
                AttackWait.Hold(thug, 0f);
            }

            if (noRevives)
            {
                Revivable.Hold(thug, false);
            }

            if (noCharge && thug.ChargedUpBy is { IsValid: true })
            {
                thug.ChargeUpOver();
            }

            if (healthy)
            {
                Heal(thug, FullHealth);
            }
        }
    }

    // Back up to the most health they've had. Someone knocked out can come back from the pool as
    // someone else, so they're forgotten.
    static void Heal(RBMPawnAI body, Dictionary<nint, int> fullest)
    {
        if (!Crowd.Standing(body))
        {
            fullest.Remove(body.Ptr);
            return;
        }

        var full = Math.Max(body.Health, body.HealthMax);
        if (fullest.TryGetValue(body.Ptr, out var seen))
        {
            full = Math.Max(full, seen);
        }

        fullest[body.Ptr] = full;
        if (body.Health < full)
        {
            body.Health = full;
        }
    }
}
