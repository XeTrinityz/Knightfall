#nullable enable

using System.Numerics;
using BmSDK.BmGame;
using Row = BmSDK.BmGame.RPersistentData.FRandomPopulationDefine;

namespace Knightfall;

// WORLD > CITY, the RIOTS rows. The story switches riots on and off for each island in the
// population manager's district table, so these hold that table's fields.
static class Riots
{
    // The story only changes the table at mission points, so it's checked twice a second.
    const float CheckEvery = 0.5f;

    // More than one riot holds, so all of a riot comes for you. The game also uses it as the most
    // rioters in one fight, so the whole city's rioters can't pile in.
    const int AllOfThem = 20;

    // Rioters closer than this are pulled into a fight with you.
    const float PullRange = 2500f;

    // Riot spots come in two kinds, and the game checks a different switch for each.
    static readonly DistrictField<byte> Rioting = new(
        row => row.bRiotingThugs, (row, on) => { row.bRiotingThugs = on; return row; });

    static readonly DistrictField<byte> PassiveRioting = new(
        row => row.bPassiveRiotingThugs, (row, on) => { row.bPassiveRiotingThugs = on; return row; });

    static readonly DistrictField<int> FightMin = new(
        row => row.NumThugsWantToFightFromRiotMin, (row, count) => { row.NumThugsWantToFightFromRiotMin = count; return row; });

    static readonly DistrictField<int> FightMax = new(
        row => row.NumThugsWantToFightFromRiotMax, (row, count) => { row.NumThugsWantToFightFromRiotMax = count; return row; });

    // A riot spot copies its island's counts only when it wakes up, so the ones already awake are
    // given them directly.
    static readonly Held<ROverworldPopulationVolume, int> SpotMin = new(
        "riot spot fewest fighters", spot => spot.NumThugsWantToFightMin, (spot, count) => spot.NumThugsWantToFightMin = count);

    static readonly Held<ROverworldPopulationVolume, int> SpotMax = new(
        "riot spot most fighters", spot => spot.NumThugsWantToFightMax, (spot, count) => spot.NumThugsWantToFightMax = count);

    // A roaming rioter only joins a fight while the island's room for street thugs in it isn't
    // used up.
    static readonly DistrictField<int> StreetFighters = new(
        row => row.MaximumNonRiotThugsAllowedToFight, (row, count) => { row.MaximumNonRiotThugsAllowedToFight = count; return row; });

    // Each rioter's own choice to fight or run, and how few of its group can be left before it
    // breaks and runs.
    static readonly Held<RRiotBrain, bool> Wants = new(
        "rioter wants to fight", brain => brain.bRiotWantsToFight, (brain, wants) => brain.bRiotWantsToFight = wants);

    static readonly Held<RRiotBrain, int> BreakAt = new(
        "rioter breaks at", brain => brain.MilitiaWillBreakThreshold, (brain, count) => brain.MilitiaWillBreakThreshold = count);

    // The game's own two scales on how many rioters it lets out at once, one for on foot and one
    // for in the Batmobile. Its code multiplies the most rioters by whichever fits.
    static readonly Held<RPopulationManager, float> SizeOnFoot = new(
        "riot size on foot", people => people.NonBatmobilePawnCountMultiplier, (people, scale) => people.NonBatmobilePawnCountMultiplier = scale);

    static readonly Held<RPopulationManager, float> SizeDriving = new(
        "riot size driving", people => people.BatmobilePawnCountMultiplier, (people, scale) => people.BatmobilePawnCountMultiplier = scale);

    static float _checkedAt = -1f;
    static float _fightCheckedAt = -1f;
    static float _sizeCheckedAt = -1f;

    public static readonly Toggle Everywhere = new(
        "world.city.riots_everywhere", "RIOTS EVERYWHERE",
        "Every island has street riots, whatever the story says. Riots still stop for missions that pause them.")
    {
        Tick = () => Each(ref _checkedAt, people =>
        {
            Rioting.Hold(people, _ => 1);
            PassiveRioting.Hold(people, _ => 1);
        }),
        Restore = () =>
        {
            Rioting.Release();
            PassiveRioting.Release();
        },
    };

    public static readonly Toggle FightYou = new(
        "world.city.rioters_fight_you", "RIOTERS FIGHT YOU",
        "Every rioter comes for you when you get close, instead of only a few of them.")
    {
        Tick = () => Each(ref _fightCheckedAt, people =>
        {
            const int all = AllOfThem;
            FightMin.Hold(people, was => Math.Max(was, all));
            FightMax.Hold(people, was => Math.Max(was, all));
            StreetFighters.Hold(people, was => Math.Max(was, all));
            foreach (var spot in people.ActivePopulationVolumes)
            {
                if (spot is { IsValid: true })
                {
                    SpotMin.Hold(spot, was => Math.Max(was, all));
                    SpotMax.Hold(spot, was => Math.Max(was, all));
                }
            }

            // Militia always run from the Batmobile and keep the game's own rules.
            if (World.Info is not { AIManager: { IsValid: true } brains } game)
            {
                return;
            }

            // Rioters near you run off on their own run-away behaviour, which never asks whether
            // they want to fight. The game pulls a rioter into a fight with ForceIntoCombat, so
            // those near you are pulled in, up to the cap. It refuses while you're driving.
            var fighting = game.CombatManager is { IsValid: true } combat ? combat.GetNumCombatants() : all;
            var you = World.Driving is null && World.Player is { IsValid: true } player ? player.Location : (Vector3?)null;
            foreach (var known in brains.GlobalControllerInfoList)
            {
                if (known.Controller is not { IsValid: true } ai || ai.RiotBrain is not { IsValid: true, bMilitia: false } brain)
                {
                    continue;
                }

                Wants.Hold(brain, true);
                BreakAt.Hold(brain, 0);

                if (you is { } here && fighting < all
                    && ai.Pawn is RPawnVillain { IsValid: true, bDeleteMe: false, bGoodAsDead: false, bSpawnedInRiot: true, bIsCurrentCombatant: false } thug
                    && thug.Health > 0 && Vector3.DistanceSquared(thug.Location, here) < PullRange * PullRange)
                {
                    thug.ForceIntoCombat();
                    fighting++;
                }
            }
        }),
        Restore = () =>
        {
            FightMin.Release();
            FightMax.Release();
            SpotMin.Release();
            SpotMax.Release();
            StreetFighters.Release();
            Wants.Release();
            BreakAt.Release();
        },
    };

    public static readonly Slider Size = new(
        "world.city.riot_size", "RIOT SIZE",
        "How many rioters the city has out at once. Lower thins the riots out, higher fills them as far as the game has thugs to spare. Riots already going change as they come and go.",
        min: 0.25, max: 2, step: 0.25, value: 1, unit: "X")
    {
        Tick = () => Each(ref _sizeCheckedAt, people =>
        {
            SizeOnFoot.Hold(people, was => was * (float)Size!.Value);
            SizeDriving.Hold(people, was => was * (float)Size!.Value);
        }),
        Restore = () =>
        {
            SizeOnFoot.Release();
            SizeDriving.Release();
        },
    };

    public static readonly Catalog.Section Section = new("RIOTS", Everywhere, Size, FightYou);

    static void Each(ref float checkedAt, Action<RPopulationManager> hold)
    {
        if (MathF.Abs(World.Now - checkedAt) < CheckEvery)
        {
            return;
        }

        checkedAt = World.Now;
        if (World.Info is { } game && !game.IsChallengeMode() && game.PopulationManager is { IsValid: true } people)
        {
            hold(people);
        }
    }

    // One field of each island's row, held like Held does it: what the island had is put back, and
    // if the story writes its own value meanwhile, that is what gets put back instead.
    sealed class DistrictField<T> where T : struct, IEquatable<T>
    {
        readonly Func<Row, T> _read;
        readonly Func<Row, T, Row> _write;
        readonly Dictionary<string, (T Was, T Wrote)> _held = new();
        RPopulationManager? _owner;

        public DistrictField(Func<Row, T> read, Func<Row, T, Row> write)
        {
            _read = read;
            _write = write;
        }

        public void Hold(RPopulationManager people, Func<T, T> wanted)
        {
            // A new world has a new manager, and the old one's values went with it.
            if (!ReferenceEquals(_owner, people))
            {
                _held.Clear();
                _owner = people;
            }

            var table = people.DistrictRandomPopulation;
            for (var i = 0; i < table.Count; i++)
            {
                var row = table[i];
                if (Island(row) is not { } name)
                {
                    continue;
                }

                var now = _read(row);
                var entry = _held.TryGetValue(name, out var known) && now.Equals(known.Wrote) ? known : (Was: now, Wrote: now);
                var want = wanted(entry.Was);
                if (!now.Equals(want))
                {
                    table[i] = _write(row, want);
                }

                _held[name] = (entry.Was, want);
            }
        }

        public void Release()
        {
            if (_owner is { IsValid: true } people)
            {
                var table = people.DistrictRandomPopulation;
                for (var i = 0; i < table.Count; i++)
                {
                    var row = table[i];
                    if (Island(row) is { } name && _held.TryGetValue(name, out var entry) && !_read(row).Equals(entry.Was))
                    {
                        table[i] = _write(row, entry.Was);
                    }
                }
            }

            _held.Clear();
            _owner = null;
        }

        // The city's islands. The table also has an unnamed first row and rows for challenge maps.
        static string? Island(Row row)
        {
            var name = row.CityDistrict.ToString();
            return name is { Length: > 0 } && name.StartsWith("City", StringComparison.OrdinalIgnoreCase) && !name.Contains("Challenge", StringComparison.OrdinalIgnoreCase)
                ? name
                : null;
        }
    }
}
