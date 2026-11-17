#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// SPAWNER > WAVES. Hostile enemies come at you in waves wherever you are. When the last one of
// a wave is down there is a short break, then a bigger wave. It goes on until it is switched off.
static class Waves
{
    const float Metre = Teleport.UnitsPerMetre;
    const float CheckEvery = 0.5f;

    // After a wave found no room, how long before it looks again.
    const float RetryAfter = 2f;

    // The ring is turned a little each wave so they don't always come from the same side.
    const float Turn = 1.3f;

    static readonly List<RPawnVillain> Wave = new();

    // Who of the wave is still on their feet, for the markers.
    public static IEnumerable<RPawnVillain> Standing => Wave.Where(body => body.IsAlive && Crowd.Standing(body));

    // An enemy that wandered off or got stuck is brought back near you. Far is past a few times
    // SPAWN DISTANCE for a while. Stuck is barely moving for longer, out of your sight or well away.
    const float FarTimes = 4f;
    const float FarLeast = 40f * Metre;
    const float FarFor = 3f;
    const float StuckMove = 60f;
    const float StuckFor = 8f;
    const int BringTries = 3;

    sealed class Watch
    {
        public Vector3 At;
        public float StillSince;
        public float FarSince = -1f;
        public int Tries;
    }

    static readonly Dictionary<RPawnVillain, Watch> Watched = new();

    // The fallen and the weapons the wave carried, cleared away as the next wave comes in, so the
    // street doesn't fill up. Anyone who fell in the last few seconds stays a little longer, and a
    // weapon still in a standing enemy's hands is left alone.
    const float FallenStay = 10f;
    static readonly List<(RPawnVillain Body, float At)> Fallen = new();
    static readonly HashSet<RBMWeapon> Carried = new();
    static int _number;
    static int _epoch = -1;
    static float _nextAt;
    static float _checkedAt = -1f;
    static bool _was;
    static bool _told;

    // A wave comes in batches a short time apart, the way the AR challenges send them. What
    // didn't fit yet is tried again a little later, and given up after a few tries that found
    // no room at all.
    const int Batch = 6;
    const float BatchEvery = 0.5f;
    const float TopUpEvery = 1.5f;
    const int TopUpTries = 4;
    static int _owed;
    static int _topUps;
    static float _topUpAt;

    // BUILD UP, after the AR challenges: half armed from the start and more each wave, shields
    // from wave 2, stun sticks from wave 3, and with guns a few rifles from wave 6.
    const int ShieldsFrom = 2;
    const int SticksFrom = 3;
    const int GunsFrom = 6;
    const float ArmedFirst = 0.5f;
    const float ArmedPerWave = 0.12f;
    const float ArmedMost = 0.85f;
    const int GunsMost = 3;
    static readonly List<Class> Light = new();
    static readonly List<Class> Shields = new();
    static readonly List<Class> Sticks = new();
    static readonly List<Class> Guns = new();
    static int _gunsGiven;

    // What the wave counter shows.
    public static int Number => _number;
    public static int Total { get; private set; }

    // How many the wave was sent with, before its batches arrive.
    public static int Planned { get; private set; }
    public static int Best { get; private set; }

    // Enemies of this run knocked down or gone, for the counter.
    public static int Down { get; private set; }
    public static float SentAt { get; private set; } = -100f;

    // Standing enemies of this wave, counted live for the counter.
    public static int Left
    {
        get
        {
            var left = 0;
            foreach (var body in Wave)
            {
                if (body.IsAlive && Crowd.Standing(body))
                {
                    left++;
                }
            }

            return left;
        }
    }

    // Seconds to the next wave during a break, or below 0 while a wave is on.
    public static float NextIn => Wave.Count == 0 && _nextAt > 0f ? MathF.Max(0f, _nextAt - World.Now) : -1f;

    // How long the current break is, for the counter's bar.
    public static float BreakLength => MathF.Max(Break(), RetryAfter);

    public static readonly Toggle On = new(
        "spawner.waves.on", "WAVES",
        "Wave after wave of enemies comes at you wherever you are. Clear one and a bigger one follows after a break. Switching it off takes the last wave away.",
        saved: false)
    {
        Block = static () => World.Info?.PopulationManager is not { IsValid: true } ? "NOT IN THIS AREA" : null,
    };

    public static readonly Choice Enemies = new(
        "spawner.waves.enemies", "ENEMIES",
        "Who the waves are made of. MIXED is every enemy this area has loaded except the named villains. The groups change with where you are.",
        new[] { ("", "MIXED") })
    {
        Preference = true,
    };

    public static readonly Command Next = new(
        "spawner.waves.next", "NEXT WAVE",
        "Sends the next wave now, without waiting for this one to be cleared.",
        () =>
        {
            if (!On.On)
            {
                return Outcome.Failed("WAVES ARE OFF");
            }

            _nextAt = 0f;
            return Spawn() ? Outcome.Done("WAVE SENT") : Outcome.Failed("NO ROOM AROUND YOU");
        });

    public static readonly Command Restart = new(
        "spawner.waves.restart", "RESTART WAVES",
        "Takes the current wave away and starts again from wave 1 after the break.",
        () =>
        {
            Clear();
            _number = 0;
            Down = 0;
            _nextAt = World.Now + Break();
            return Outcome.Done("RESTARTED");
        });

    public static readonly Choice Weapons = new(
        "spawner.waves.weapons", "WEAPONS",
        "BUILD UP arms half of wave 1 with bats, pipes and knives, brings in shields from wave 2 and stun sticks from wave 3, and arms more of each wave as it goes. WITH GUNS also brings a few rifles and shotguns from wave 6. THEIR OWN is what the game gives each one.",
        new[] { ("build", "BUILD UP"), ("guns", "BUILD UP WITH GUNS"), ("own", "THEIR OWN"), ("none", "UNARMED") })
    {
        Preference = true,
    };

    public static readonly Slider Tougher = new(
        "spawner.waves.tougher", "TOUGHER EACH WAVE",
        "How much more health each wave has than the one before, on top of ENEMY HEALTH.",
        min: 0, max: 25, step: 5, value: 5, unit: "%", zero: "OFF")
    {
        Preference = true,
    };

    public static readonly Slider StartSize = new(
        "spawner.waves.start_size", "FIRST WAVE",
        "How many enemies the first wave has.",
        min: 1, max: 20, step: 1, value: 7)
    {
        Preference = true,
    };

    public static readonly Slider Growth = new(
        "spawner.waves.growth", "GROWTH",
        "How many more enemies each wave has than the one before.",
        min: 0, max: 5, step: 1, value: 2, zero: "NONE")
    {
        Preference = true,
    };

    public static readonly Slider Most = new(
        "spawner.waves.most", "MOST IN A WAVE",
        "A wave never gets bigger than this. MOST AT ONCE on the SPAWNED tab holds it back too.",
        min: 2, max: 30, step: 1, value: 16)
    {
        Preference = true,
    };

    public static readonly Slider Pause = new(
        "spawner.waves.break", "BREAK",
        "How long you get between a cleared wave and the next.",
        min: 0, max: 30, step: 1, value: 5, unit: " S", zero: "NONE")
    {
        Preference = true,
    };

    public static readonly Slider Health = new(
        "spawner.waves.health", "ENEMY HEALTH",
        "How much health they get, times their normal amount.",
        min: 0.25, max: 5, step: 0.25, value: 1, unit: "X")
    {
        Preference = true,
    };

    public static readonly Slider Distance = new(
        "spawner.waves.distance", "SPAWN DISTANCE",
        "How far from you they appear. Where there is no open ground that far, they come as close as it allows.",
        min: 5, max: 40, step: 1, value: 9, unit: " M")
    {
        Preference = true,
    };

    public static readonly Catalog.Section[] Sections =
    {
        new("WAVES", On, Enemies, Next, Restart),
        new("STRENGTH", Weapons, Tougher, Health),
        new("TUNING", StartSize, Growth, Most, Pause, Distance),
    };

    static float Break() => (float)Pause.Value;

    // Read each time the lists are read, so it follows where you are.
    public static void ListGroups(string[] groups)
    {
        var now = Enemies.CurrentId;
        var options = new[] { ("", "MIXED") }.Concat(groups.Select(name => (name, name))).ToArray();
        Enemies.SetOptions(options, Math.Max(0, Array.FindIndex(options, option => option.Item1 == now)));
    }

    // Runs every tick.
    public static void Tick()
    {
        if (!On.On)
        {
            if (_was)
            {
                _was = false;
                Clear();
                _number = 0;
                Down = 0;
                _told = false;
            }

            return;
        }

        if (!_was)
        {
            _was = true;
            _number = 0;
            Down = 0;
            _nextAt = World.Now + Break();
            _epoch = World.Epoch;
        }

        // A new world or a load takes the old wave with it.
        if (_epoch != World.Epoch)
        {
            _epoch = World.Epoch;
            Wave.Clear();
            Fallen.Clear();
            Carried.Clear();
            _number = 0;
            Down = 0;
            _nextAt = World.Now + Break();
        }

        if (World.Paused || World.Controller is not { IsValid: true } controller || controller.bCinematicMode
            || World.Info?.PopulationManager is not { IsValid: true } || MathF.Abs(World.Now - _checkedAt) < CheckEvery)
        {
            return;
        }

        _checkedAt = World.Now;
        for (var i = Wave.Count - 1; i >= 0; i--)
        {
            var body = Wave[i];
            if (body.IsAlive && Crowd.Standing(body))
            {
                continue;
            }

            Wave.RemoveAt(i);
            Down++;
            if (body.IsAlive)
            {
                Fallen.Add((body, World.Now));
            }
        }

        Keep();
        if (Wave.Count > 0)
        {
            if (_owed > 0 && World.Now >= _topUpAt)
            {
                TopUp();
            }

            return;
        }

        _owed = 0;

        if (_number > 0 && _nextAt == 0f)
        {
            _nextAt = World.Now + Break();
            Toasts.Show("WAVES", $"WAVE {_number} CLEARED", from: On);
        }

        if (World.Now >= _nextAt && _nextAt > 0f)
        {
            Spawn();
        }
    }

    // Sends the next wave. False when nothing fit round you, and it looks again shortly.
    static bool Spawn()
    {
        _nextAt = World.Now + RetryAfter;
        if (Teleport.Mover is not { } mover)
        {
            return false;
        }

        var wave = _number + 1;
        ReadArms();
        ReadPool(wave);
        if (Regulars.Count == 0)
        {
            if (!_told)
            {
                _told = true;
                Toasts.Show("WAVES", "NOBODY LOADED HERE TO SEND", OutcomeKind.Failed, On);
            }

            return false;
        }

        var size = (int)Math.Min(Most.Value, StartSize.Value + _number * Growth.Value);
        size = Math.Min(size, Spawned.Room);
        if (size <= 0)
        {
            return false;
        }

        var specials = SpecialsFor(wave, size);
        var spots = Crowd.ScatterSpots(mover, Math.Min(size - specials, Batch), (float)Distance.Value * Metre, _number * Turn, Wave);
        if (spots.Count == 0)
        {
            return false;
        }

        _gunsGiven = 0;
        var made = People.Muster(Regulars, spots, mover, Toughness(wave), Wave.Add, Armer(wave));
        if (made == 0)
        {
            return false;
        }

        _told = false;
        _number = wave;
        Sweep(FallenStay);
        _nextAt = 0f;
        Total = made;
        Planned = size;
        _owed = size - made;
        _specialsOwed = specials;
        _topUps = 0;
        _topUpAt = World.Now + BatchEvery;
        Best = Math.Max(Best, _number);
        SentAt = World.Now;
        Toasts.Show("WAVES", $"WAVE {_number}  {size} ENEMIES", from: On);
        return true;
    }

    // The wave's next batch, or the ones it came short by. They join the same wave.
    static void TopUp()
    {
        if (Teleport.Mover is not { } mover)
        {
            _owed = 0;
            return;
        }

        var want = Math.Min(Math.Min(_owed, Batch), Spawned.Room);
        ReadPool(_number);
        var made = 0;
        if (want > 0 && Regulars.Count > 0)
        {
            var spots = Crowd.ScatterSpots(mover, want, (float)Distance.Value * Metre, _number * Turn + _topUps + 0.5f, Wave);

            // Specials first, with what they came with. Nobody hands a brute a bat.
            var special = Specials.Count > 0 ? Math.Min(_specialsOwed, spots.Count) : 0;
            if (special > 0)
            {
                var got = People.Muster(Specials, spots.GetRange(0, special), mover, Toughness(_number), Wave.Add, Armer(_number));
                _specialsOwed -= got;
                made += got;
            }

            if (spots.Count > special)
            {
                made += People.Muster(Regulars, spots.GetRange(special, spots.Count - special), mover, Toughness(_number), Wave.Add, Armer(_number));
            }
        }

        _owed -= made;
        Total += made;
        if (made > 0)
        {
            _topUps = 0;
            _topUpAt = World.Now + BatchEvery;
        }
        else if (++_topUps >= TopUpTries)
        {
            _owed = 0;
        }
        else
        {
            _topUpAt = World.Now + TopUpEvery;
        }
    }

    static float Toughness(int wave) => (float)(Health.Value * (1.0 + Tougher.Value / 100.0 * (wave - 1)));

    // Each enemy's weapon, picked as they're built. Medics never carry one, since they're there
    // to revive and shock. Gunmen always get a gun. Other specials keep what they came with.
    // Regulars follow WEAPONS.
    static Func<string, Class?, (bool Keep, Class? Weapon)> Armer(int wave) => (key, own) => KindOf.GetValueOrDefault(key) switch
    {
        Kind.Medic => (false, null),
        Kind.Gunman => own is { IsValid: true } && Descends(own, "RBMWeaponRanged") ? (true, null) : (false, Guns[Random.Shared.Next(Guns.Count)]),
        Kind.Special => (true, null),
        _ => Weapons.CurrentId switch
        {
            "own" => (true, null),
            "none" => (false, null),
            _ => (false, BuildUp(wave, Weapons.CurrentId == "guns")),
        },
    };

    static Class? BuildUp(int wave, bool guns)
    {
        if (Random.Shared.NextSingle() >= MathF.Min(ArmedFirst + ArmedPerWave * (wave - 1), ArmedMost))
        {
            return null;
        }

        if (guns && wave >= GunsFrom && Guns.Count > 0 && _gunsGiven < Math.Min(GunsMost, 1 + (wave - GunsFrom) / 3)
            && Random.Shared.NextSingle() < 0.25f)
        {
            _gunsGiven++;
            return Guns[Random.Shared.Next(Guns.Count)];
        }

        // Bats and knives count twice, so they stay the most common, as in the challenges.
        Kinds.Clear();
        if (Light.Count > 0)
        {
            Kinds.Add(Light);
            Kinds.Add(Light);
        }

        if (wave >= ShieldsFrom && Shields.Count > 0)
        {
            Kinds.Add(Shields);
        }

        if (wave >= SticksFrom && Sticks.Count > 0)
        {
            Kinds.Add(Sticks);
        }

        if (Kinds.Count == 0)
        {
            return null;
        }

        var from = Kinds[Random.Shared.Next(Kinds.Count)];
        return from[Random.Shared.Next(from.Count)];
    }

    static readonly List<List<Class>> Kinds = new();

    // Who a wave is made of. Regulars are street thugs and robots. Specials are the rest: brutes
    // and medics from wave 2, anyone else, like combat experts, from wave 4. As in the challenges,
    // wave 1 has none, then two, then three at most, and they come in after the first batch.
    // Gunmen only come with guns, and always carry one, since some are built to do nothing
    // without. The ones built for one job never come: drone operators and jammers belong to
    // predator rooms, snipers only shoot from a perch, and checkpoint commanders set off a beeping
    // alarm when they go down.
    const int SpecialsFrom = 2;
    const int ExpertsFrom = 4;
    const int SpecialsMost = 3;
    static readonly string[] Never = { "MiniDroneController", "DetectiveModeDetector", "MilitiaCaptain", "Sniper" };
    static readonly List<string> Regulars = new();
    static readonly List<string> Specials = new();

    enum Kind
    {
        Regular,
        Special,
        Medic,
        Gunman,
    }

    static readonly Dictionary<string, Kind> KindOf = new();
    static int _specialsOwed;

    static void ReadPool(int wave)
    {
        Regulars.Clear();
        Specials.Clear();
        KindOf.Clear();
        var guns = Weapons.CurrentId == "guns" && wave >= GunsFrom && Guns.Count > 0;
        foreach (var (key, pawn, gun) in People.WavePool(Enemies.CurrentId))
        {
            if (Never.Any(word => pawn.Contains(word, StringComparison.Ordinal)))
            {
                continue;
            }

            // A robot keeps the gun it's built with, so a gun robot counts as a gunman.
            var robot = pawn.Contains("Robot", StringComparison.Ordinal);
            var medic = pawn.Contains("Medic", StringComparison.Ordinal);
            if (pawn == "RPawnVillainThug" || (robot && !gun))
            {
                Regulars.Add(key);
                KindOf[key] = Kind.Regular;
            }
            else if (pawn.StartsWith("RPawnVillainGun", StringComparison.Ordinal) || robot)
            {
                if (guns)
                {
                    Specials.Add(key);
                    KindOf[key] = Kind.Gunman;
                }
            }
            else if (wave >= (pawn.Contains("MultiStage", StringComparison.Ordinal) || medic ? SpecialsFrom : ExpertsFrom))
            {
                Specials.Add(key);
                KindOf[key] = medic ? Kind.Medic : Kind.Special;
            }
        }

        // An area with nobody but specials still gets a wave.
        if (Regulars.Count == 0)
        {
            Regulars.AddRange(Specials);
        }
    }

    static int SpecialsFor(int wave, int size) =>
        Specials.Count == 0 || wave < SpecialsFrom ? 0 : Math.Min(Math.Min(SpecialsMost, 2 + (wave - SpecialsFrom) / 2), size / 3);

    // Sorts the weapons this area has loaded by what they are, going by their parent classes.
    static void ReadArms()
    {
        Light.Clear();
        Shields.Clear();
        Sticks.Clear();
        Guns.Clear();
        foreach (var kind in Arms.Find())
        {
            // Combat experts' swords sit under the knives, but a street thug can't use one.
            if ((kind.Class.Name.ToString() ?? "").Contains("Expert", StringComparison.Ordinal))
            {
                continue;
            }

            if (Descends(kind.Class, "RBMWeaponBatBase") || Descends(kind.Class, "RBMWeaponKnifeBase"))
            {
                Light.Add(kind.Class);
            }
            else if (Descends(kind.Class, "RBMWeaponShieldBase"))
            {
                Shields.Add(kind.Class);
            }
            else if (Descends(kind.Class, "RBMWeaponStunStickBase"))
            {
                Sticks.Add(kind.Class);
            }
            else if (Descends(kind.Class, "RBMWeaponRanged") && !Descends(kind.Class, "RBMWeaponSniperBase"))
            {
                Guns.Add(kind.Class);
            }
        }
    }

    static bool Descends(Class type, string parent)
    {
        for (Struct? at = type; at is { IsValid: true }; at = at.SuperStruct)
        {
            if ((at.Name.ToString() ?? "") == parent)
            {
                return true;
            }
        }

        return false;
    }

    // Brings back anyone of the wave who is far off or stuck.
    static void Keep()
    {
        foreach (var gone in Watched.Keys.Where(body => !Wave.Contains(body)).ToList())
        {
            Watched.Remove(gone);
        }

        if (Teleport.Mover is not { } mover)
        {
            return;
        }

        var you = mover.Location;
        var eyes = you + new Vector3(0f, 0f, 60f);
        var far = MathF.Max(FarLeast, (float)Distance.Value * Metre * FarTimes);
        foreach (var body in Wave.ToArray())
        {
            if (body.CurrWeapon is RBMWeapon { IsValid: true } weapon)
            {
                Carried.Add(weapon);
            }

            if (!Watched.TryGetValue(body, out var watch))
            {
                Watched[body] = new Watch { At = body.Location, StillSince = World.Now };
                continue;
            }

            if (Vector3.Distance(body.Location, watch.At) > StuckMove || body.CombatMove is { IsValid: true })
            {
                watch.At = body.Location;
                watch.StillSince = World.Now;
            }

            var away = Vector3.Distance(body.Location, you);
            watch.FarSince = away > far ? (watch.FarSince < 0f ? World.Now : watch.FarSince) : -1f;
            // A trace that starts inside a wall can come back clear, so standing still well away from
            // you counts too.
            var stuck = World.Now - watch.StillSince > StuckFor
                && (!mover.FastTrace(body.Location, eyes, Vector3.Zero, false) || away > (float)Distance.Value * Metre * 2f);
            if (stuck || (watch.FarSince >= 0f && World.Now - watch.FarSince > FarFor))
            {
                Bring(body, mover, watch);
            }
        }
    }

    static void Bring(RPawnVillain body, Pawn mover, Watch watch)
    {
        var spots = Crowd.ScatterSpots(mover, 1, (float)Distance.Value * Metre, Random.Shared.NextSingle() * MathF.Tau, Wave.Where(other => !ReferenceEquals(other, body)));
        if (spots.Count > 0 && body.SetLocation(spots[0] + new Vector3(0f, 0f, Teleport.Lift(body))))
        {
            var toYou = mover.Location - spots[0];
            body.Velocity = Vector3.Zero;
            body.SetRotation(new Rotator(0f, MathF.Atan2(toYou.Y, toYou.X) * 180f / MathF.PI, 0f));
            watch.At = body.Location;
            watch.StillSince = World.Now;
            watch.FarSince = -1f;
            watch.Tries = 0;
            return;
        }

        // Nowhere to put them. After a few tries they're swapped for a fresh one near you.
        if (++watch.Tries < BringTries)
        {
            return;
        }

        Wave.Remove(body);
        Watched.Remove(body);
        Spawned.Take(body);
        Total--;
        _owed++;
        _topUps = 0;
        _topUpAt = World.Now;
    }

    // Takes away the fallen who have been down this long, and every weapon nobody standing holds.
    static void Sweep(float after)
    {
        for (var i = Fallen.Count - 1; i >= 0; i--)
        {
            var (body, at) = Fallen[i];
            if (!body.IsAlive)
            {
                Fallen.RemoveAt(i);
            }
            else if (World.Now - at >= after)
            {
                Fallen.RemoveAt(i);
                Spawned.Take(body);
            }
        }

        foreach (var weapon in Carried.ToArray())
        {
            if (!weapon.IsAlive)
            {
                Carried.Remove(weapon);
            }
            // Lying on the ground, or still with someone who's down. Batman can be holding one he took
            // off them, so anyone else alive keeps theirs.
            else if (weapon.Owner is not Pawn { IsAlive: true } holder || (holder is RPawnVillain villain && !Crowd.Standing(villain)))
            {
                Carried.Remove(weapon);
                weapon.Destroy();
            }
        }
    }

    // Takes the wave's enemies out of the world, and what's left of the run with them.
    static void Clear()
    {
        foreach (var body in Wave.ToArray())
        {
            if (body.IsAlive)
            {
                Spawned.Take(body);
            }
        }

        Wave.Clear();
        Sweep(0f);
    }

    // A hot reload keeps the enemies in the world and only drops the list.
    public static void Release()
    {
        Wave.Clear();
        Fallen.Clear();
        Carried.Clear();
        _was = false;
    }
}
