#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// TELEPORT > LOCATIONS. Places the game ships as levels of their own: older Arkham sets from the
// challenge maps, DLC maps, villain lairs, race tracks and story sets. One is streamed into the
// city's world far below the streets, where it touches nothing. The city never learns the player
// left. Its district stays the current one and stays loaded, and the rooms the game uses to switch
// districts are told to ignore the player while they're in the place. The game drops a district it
// isn't using, and some never load again after that, so the place is never made current.
static class Locations
{
    // Parts are the place's levels. The game streams each part's own small parent in with it,
    // named like the parts without their ending.
    sealed record Spot(string Id, string Label, params string[] Parts)
    {
        public string Family => Parts[0].Split('_')[0];

        // The place's levels run their own story when they load, so going there can start a
        // cutscene, a mission or a fight.
        public bool Mission { get; init; }

        // Where to arrive when the place's own start is somewhere odd, in its own coordinates.
        public (Vector3 At, float Yaw)? Arrive { get; init; }
    }

    sealed record Home(Vector3 At, float Yaw);

    enum Stage
    {
        Idle,
        Darkening,
        Moving,
        Brightening,
    }

    static readonly Spot[] Spots =
    {
        new("batcave", "BATCAVE", "BatCave_B1"),
        new("wayne_manor", "WAYNE MANOR", "WayneManor_B1"),
        new("crime_alley", "CRIME ALLEY", "CPAlley_01"),
        new("iceberg_lounge", "ICEBERG LOUNGE", "IcebergLounge_B1"),
        new("medical", "ARKHAM MEDICAL", "Medical_A"),
        new("monarch_theatre", "MONARCH THEATRE", "CPTheatre_S2"),
        new("killer_croc", "KILLER CROC'S SHIP", "VPKillerCroc_B1", "VPKillerCroc_B2", "VPKillerCroc_C1") { Mission = true },
        new("mad_hatter", "WONDERLAND", "VPMadHatter_A1") { Mission = true },
        new("ras_al_ghul", "RA'S AL GHUL'S CHAMBER", "VPRasAlGhul_B1", "VPRasAlGhul_C1") { Mission = true },
        new("lazarus_pit", "LAZARUS PIT", "LazarusPit_A1", "LazarusPit_C1") { Mission = true },
        new("mr_freeze", "FREEZE'S LAIR", "VPFreeze_01") { Mission = true },
        new("oil_rig", "SEAGATE OIL RIG", "DLCOilrig_A1", "DLCOilrig_B1", "DLCOilrig_C1", "DLCOilrig_C2", "DLCOilrig_D1", "DLCOilrig_E1",
            "DLCOilrig_F1", "DLCOilrig_G1", "DLCOilrig_G2", "DLCOilrig_H1", "DLCOilrig_H2", "DLCOilrig_H3", "DLCOilrig_I1") { Mission = true },
        new("red_hood_docks", "RED HOOD DOCKS", "DLCRedhoodDocks_A1") { Mission = true },
        new("red_hood_mall", "RED HOOD MALL", "DLCRedhoodMall_B1") { Mission = true },
        new("red_hood_office", "RED HOOD OFFICE", "DLCRedhoodOffice_B1") { Mission = true },
        new("catwoman", "CATWOMAN'S REVENGE", "GSCatwomanDLC_01", "GSCatwomanDLC_02") { Mission = true },
        new("gcpd_lockdown", "GCPD LOCKDOWN", "GSGCPD_01", "GSGCPD_02") { Mission = true, Arrive = (new Vector3(-76731f, -58822f, 2938f), 167f) },
        new("two_face", "TWO-FACE DLC", "GSTwoFace_01", "GSTwoFace_02") { Mission = true },
        new("track_waynetech_1", "WAYNETECH TRACK 1", "RTWayneTech_01"),
        new("track_waynetech_2", "WAYNETECH TRACK 2", "RTWayneTech_02"),
        new("joker_arkham", "JOKER'S ARKHAM", "JokerBoss_B1") { Mission = true, Arrive = (new Vector3(33477f, 30459f, 98f), 4f) },
        new("joker_finale", "JOKER'S FINALE", "JokerBoss_B2") { Mission = true, Arrive = (new Vector3(-73376f, 58211f, 122f), -95f) },
        new("stagg", "STAGG AIRSHIPS", "Stagg_A1", "Stagg_A2", "Stagg_B1", "Stagg_B2", "Stagg_B3", "Stagg_C1") { Mission = true, Arrive = (new Vector3(-10817f, -4161f, 2236f), 89f) },
        new("panessa", "PANESSA STUDIOS", "Film_A1", "Film_A2", "Film_B1", "Film_B3", "Film_B4") { Mission = true },
        new("sacrifice", "PANESSA SACRIFICE SET", "Sacrifice_C1") { Mission = true },
        new("man_bat", "MAN-BAT LAB", "ManBat_B1") { Mission = true, Arrive = (new Vector3(1705f, 5061f, 610f), 138f) },
    };

    // Every place's middle goes here, under Bleake Island and far below anything the city has.
    // Only one is ever loaded, so they can share it.
    static readonly Vector3 Middle = new(-25000f, -39000f, -60000f);

    // Real seconds, so slow motion doesn't stretch them.
    const float FadeOut = 0.4f;
    const float FadeBack = 0.6f;
    const float LoadSeconds = 20f;

    // The city is still there on the way out, so its floor only needs a moment.
    const float BackSeconds = 5f;

    // How often the place's streaming entries are checked while it's in use.
    const float CheckEvery = 1f;

    // An arrival point's floor is looked for this far above and below it.
    const float FloorAbove = 200f;
    const float FloorBelow = 1000f;

    // A spot saved where the player stood is their middle, so the floor is looked for from just
    // above it. Anything higher, like a beam overhead, would be landed on instead.
    const float StoodAbove = 30f;
    const float HomeAbove = 150f;
    const float HomeBelow = 5000f;

    // A player this much further from the place's middle than its own size has left it some
    // other way, like a teleport.
    const float LeftSet = 15000f;

    const string NotInstalled = "DLC NOT INSTALLED";

    const string InsideKey = "Knightfall.LocationInside";
    const string HomeKey = "Knightfall.LocationHome";
    const string WorldKey = "Knightfall.LocationWorld";

    static Stage _stage;
    static long _stageAt;
    static bool _goingIn;
    static Command? _by;
    static Spot? _spot;
    static Spot? _inside;
    static Home? _home;
    static long _loadAt;
    static float _checkedAt = -1f;
    static int _epoch = -1;
    static bool _moved;

    // Whether our ask for the place is in, so it's only made and taken back once.
    static bool _asked;

    // Whether the place is asked for once the screen is dark, after its old copy is cleared.
    static bool _fresh;

    // When to clear out a place that was just left, once the game has stopped showing it.
    static long _clearAt;
    const float ClearAfter = 1.5f;
    static int _away;
    static RLevelVolume? _room;
    static RLevelVolume? _cityRoom;

    // Where the place goes and how big it is, from the game's own list of its levels.
    static (string Id, Vector3 Offset, float Size)? _placed;

    // Entries the game already had showing before the place was asked for. They are the game's,
    // so they are never moved.
    static readonly HashSet<nint> Before = new();

    public static readonly Choice Place = new(
        "teleport.locations.location", "LOCATION",
        "Which place GO THERE takes you to: sets from the older Arkham games, DLC maps, villain lairs, race tracks and story sets.",
        Spots.Select(spot => (spot.Id, spot.Label)).ToArray())
    {
        Preference = true,
    };

    public static readonly Command Go = new(
        "teleport.locations.go", "GO THERE",
        "Takes you to the place picked above. The city waits as you left it. RETURN TO THE CITY brings you back to the same spot.",
        () => In(Go!))
    {
        Confirm = static () => Picked.Mission
            ? new Confirmation("MISSION AREA", Picked.Label,
                "This place runs its own story. Going there may start a cutscene, a mission or a fight, and that can count towards your story progress.",
                "GO THERE", "STAY HERE")
            : null,
        Block = static () => !World.InCity ? "NOT IN THE CITY"
            : _inside is not null ? "ALREADY THERE"
            : Teleport.Away is { } away ? away
            : World.Driving is not null ? "ON FOOT ONLY"
            : Installed(Picked) ? null : NotInstalled,
    };

    public static readonly Command Back = new(
        "teleport.locations.return", "RETURN TO THE CITY",
        "Takes you back to where you were in the city before you went in.",
        () => Out(Back!))
    {
        Block = static () => _inside is null ? "NOT IN A LOCATION" : null,
    };

    public static readonly Command SetArrival = new(
        "teleport.locations.set_arrival", "SET ARRIVAL HERE",
        "While you're in a place, makes where you stand the spot you arrive at there from now on. For places whose own start is somewhere odd.",
        SaveArrival)
    {
        Block = static () => _inside is null ? "NOT IN A LOCATION" : null,
    };

    public static readonly Command ClearArrival = new(
        "teleport.locations.reset_arrival", "RESET ARRIVAL",
        "Goes back to arriving at the place's own start for the place picked above.",
        ForgetArrival)
    {
        Block = static () => Arrival(Picked) is null ? "NONE SAVED" : null,
    };

    public static readonly Catalog.Section Section = new("LOCATIONS", Place, Go, Back, SetArrival, ClearArrival);

    const string ArrivalSection = "teleport.locations";

    // The spot the player chose to arrive at in a place, in the place's own coordinates, so it
    // still matches wherever the place is put.
    static (Vector3 At, float Yaw)? Arrival(Spot spot)
    {
        // As last set, so a spot just saved counts before the settings are written out.
        var parts = (Settings.Kept(ArrivalSection, "arrival_" + spot.Id) as string ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var numbers = new float[4];
        if (parts.Length != 4)
        {
            return null;
        }

        for (var i = 0; i < 4; i++)
        {
            if (!float.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return (new Vector3(numbers[0], numbers[1], numbers[2]), numbers[3]);
    }

    static Outcome SaveArrival()
    {
        if (World.Player is not { } player)
        {
            return Outcome.NoPlayer;
        }

        if (_inside is not { } spot || Measure(spot) is not { } placed)
        {
            return Outcome.Failed("NOT IN A LOCATION");
        }

        var at = player.Location - placed.Offset;
        Settings.Set(ArrivalSection, "arrival_" + spot.Id,
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{at.X:0} {at.Y:0} {at.Z:0} {player.Rotation.Yaw:0}"));
        return Outcome.Done("SAVED");
    }

    static Outcome ForgetArrival()
    {
        if (Arrival(Picked) is null)
        {
            return Outcome.Unchanged("NONE SAVED");
        }

        Settings.Set(ArrivalSection, "arrival_" + Picked.Id, "");
        return Outcome.Done("RESET");
    }

    static Spot Picked => Spots.FirstOrDefault(spot => spot.Id == Place.CurrentId) ?? Spots[0];

    static bool Installed(Spot spot) => spot.Parts.All(GameFiles.HasPackage);

    // While the player is in a place, or on the way, the rooms leave the current district alone.
    static bool Away => _inside is not null || _stage is Stage.Darkening or Stage.Moving;

    static Outcome In(Command command)
    {
        if (World.Player is not { } player || World.Controller is null || Teleport.Ri is not { } ri)
        {
            return Outcome.NoPlayer;
        }

        if (_stage != Stage.Idle)
        {
            return Outcome.Unchanged("ALREADY GOING");
        }

        if (_inside is not null)
        {
            return Outcome.Unchanged("ALREADY THERE");
        }

        if (Teleport.Away is { } away)
        {
            return Outcome.Failed(away);
        }

        if (World.Driving is not null)
        {
            return Outcome.Failed("ON FOOT ONLY");
        }

        var spot = Picked;
        if (!Installed(spot))
        {
            return Outcome.Failed(NotInstalled);
        }

        // The story has a level of this place out itself, so it's left alone.
        if (Showing(spot.Parts))
        {
            return Outcome.Failed("IN USE BY THE STORY");
        }

        if (Measure(spot) is null)
        {
            Log.Info($"{spot.Label} isn't in the game's list of levels here, so it can't be placed.");
            return Outcome.Failed("NOT AVAILABLE");
        }

        _spot = spot;
        _by = command;
        _goingIn = true;
        _home = new Home(player.Location, player.Rotation.Yaw);
        _cityRoom = ri.CurrentLevelVolume;

        // A place left earlier stays in memory, hidden, until the engine next clears out what
        // isn't used. Shown again as it is, it comes back low detail and half drawn, so it's
        // cleared out now and loaded fresh once the screen is dark.
        _fresh = Spots.Any(Leftover);
        if (_fresh && Game.TryGetWorldInfo(out var world) && world.IsValid)
        {
            world.ForceGarbageCollection(true);
        }
        else
        {
            Request(ri, spot);
        }

        _loadAt = System.Diagnostics.Stopwatch.GetTimestamp();
        ri.SetFadeType(false, FadeOut, false, false, 0f, 0f, 0f, 0f);
        Enter(Stage.Darkening);
        return Outcome.Unchanged("GOING");
    }

    static Outcome Out(Command command)
    {
        if (World.Player is null || World.Controller is null || Teleport.Ri is not { } ri)
        {
            return Outcome.NoPlayer;
        }

        if (_stage != Stage.Idle)
        {
            return Outcome.Unchanged("ALREADY GOING");
        }

        if (_inside is null || _home is null)
        {
            return Outcome.Unchanged("NOT IN A LOCATION");
        }

        _spot = _inside;
        _by = command;
        _goingIn = false;
        ri.SetFadeType(false, FadeOut, false, false, 0f, 0f, 0f, 0f);
        Enter(Stage.Darkening);
        return Outcome.Unchanged("GOING");
    }

    public static void Tick()
    {
        if (_epoch != World.Epoch)
        {
            // The first tick after a hot reload picks up a trip that was already made. A new world
            // after that has none of the place left in it.
            if (_epoch < 0)
            {
                Restore();
            }
            else
            {
                Forget();
            }

            _epoch = World.Epoch;
        }

        // A place left stays in memory, hidden, until the engine next clears out what isn't
        // used. A few of them fill the memory the city's textures stream into, so it's cleared
        // straight away.
        if (_clearAt != 0 && Seconds(_clearAt) >= 0f && Game.TryGetWorldInfo(out var world) && world.IsValid)
        {
            _clearAt = 0;
            world.ForceGarbageCollection(true);
        }

        var ri = Teleport.Ri;
        if (ri is null || (_spot is null && _inside is null))
        {
            return;
        }

        if (_stage == Stage.Moving || MathF.Abs(World.Now - _checkedAt) >= CheckEvery)
        {
            _checkedAt = World.Now;
            if ((_spot ?? _inside) is { } placed)
            {
                Keep(placed);
            }

            if (_stage == Stage.Idle && _inside is { } within)
            {
                Enter(ri, within);
            }

            // Twice in a row, so a moment mid-move can't count.
            _away = _stage == Stage.Idle && _inside is { } far && Gone(far) ? _away + 1 : 0;
            if (_away >= 2 && _inside is { } inside)
            {
                _away = 0;
                Log.Info($"Left {inside.Label} without RETURN TO THE CITY, so it was put away.");
                Leave(ri, inside);
                Drop(ri, inside);
                _inside = null;
                _spot = null;
                Save();
            }
        }

        if (_stage == Stage.Idle || _spot is not { } spot)
        {
            return;
        }

        var waited = Seconds(_stageAt);
        switch (_stage)
        {
            case Stage.Darkening when waited >= FadeOut:
                if (_goingIn && _fresh)
                {
                    _fresh = false;
                    Request(ri, spot);
                }

                Enter(Stage.Moving);
                break;

            case Stage.Moving:
                if (_goingIn)
                {
                    Arrive(ri, spot, waited);
                }
                else
                {
                    Return(ri, spot, waited);
                }

                break;

            case Stage.Brightening when waited >= FadeBack:
                _stage = Stage.Idle;
                if (_inside is null)
                {
                    _spot = null;
                }

                break;
        }
    }

    static void Arrive(RGameRI ri, Spot spot, float waited)
    {
        if (World.Player is not { } player)
        {
            Drop(ri, spot);
            Finish(Outcome.Failed("LOST"));
            return;
        }

        if (!Shown(spot) || Start(spot) is not { } start
            || !Teleport.Floor(player, start.At, start.At.Z + (start.Exact ? StoodAbove : FloorAbove), start.At.Z - FloorBelow, out var floor))
        {
            if (waited > LoadSeconds)
            {
                Log.Info($"{spot.Label} didn't load in {LoadSeconds:0} s.");
                Drop(ri, spot);
                Finish(Outcome.Failed("DIDN'T LOAD"));
            }

            return;
        }

        // Inside already, so the rooms ignore the player leaving their district.
        _inside = spot;
        var placed = Teleport.Hop(player, floor, new Rotator(0f, start.Yaw, 0f), true);
        if (placed.Kind != OutcomeKind.Done)
        {
            _inside = null;
            Drop(ri, spot);
            Finish(placed);
            return;
        }

        World.Controller?.OnPlayerHasBeenMoved();
        _room = null;
        Enter(ri, spot);
        Save();
        Log.Info($"Arrived in {spot.Label} {Seconds(_loadAt):0.0} s after asking, at its {start.Kind}.");
        Finish(Outcome.Done("ARRIVED"));
    }

    static void Return(RGameRI ri, Spot spot, float waited)
    {
        if (World.Player is not { } player || _home is not { } home)
        {
            Finish(Outcome.Failed("LOST"));
            return;
        }

        if (!Teleport.Floor(player, home.At, home.At.Z + HomeAbove, home.At.Z - HomeBelow, out var floor))
        {
            if (waited > BackSeconds)
            {
                Log.Info($"There was no floor where the player left the city for {spot.Label}.");
                Finish(Outcome.Failed("NO GROUND"));
            }

            return;
        }

        var placed = Teleport.Hop(player, floor, new Rotator(0f, home.Yaw, 0f), true);
        if (placed.Kind != OutcomeKind.Done)
        {
            Finish(placed);
            return;
        }

        World.Controller?.OnPlayerHasBeenMoved();
        Leave(ri, spot);
        Drop(ri, spot);
        _inside = null;
        Save();
        Log.Info($"Back from {spot.Label} {waited:0.0} s after asking.");
        Finish(Outcome.Done("BACK"));
    }

    // Where the place goes and how big it is. Its levels' bounds come from the game's own list,
    // the same one it keeps for the interiors of the city.
    static (Vector3 Offset, float Size)? Measure(Spot spot)
    {
        if (_placed is { } known && known.Id == spot.Id)
        {
            return (known.Offset, known.Size);
        }

        if (World.Info is not { } info)
        {
            return null;
        }

        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        var found = false;
        var rows = info.LevelVolumeList;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!spot.Parts.Contains(row.LevelName.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            found = true;
            min = Vector3.Min(min, row.Bounds.Min);
            max = Vector3.Max(max, row.Bounds.Max);
        }

        if (!found)
        {
            return null;
        }

        var offset = Middle - (min + max) / 2f;
        var size = (max - min).Length() / 2f;
        _placed = (spot.Id, offset, size);
        return (offset, size);
    }

    // The camera keeps its distance by the room the game counts the player in. Out in a district's
    // room, far below it, it stays right behind the player's head. So the place's own room is the
    // current one while the player is in it. Only the room changes, never the district, so
    // nothing in the city is let go of.
    static void Enter(RGameRI ri, Spot spot)
    {
        _room ??= Room(spot);
        if (_room is { IsValid: true } room && ri.CurrentLevelVolume?.Ptr != room.Ptr)
        {
            ri.CurrentLevelVolume = room;
        }
    }

    // The room the player left from, or after a reload the district room round where they left.
    static void Leave(RGameRI ri, Spot spot)
    {
        if (_room is { IsValid: true } room && ri.CurrentLevelVolume?.Ptr == room.Ptr)
        {
            var city = _cityRoom is { IsValid: true, bDeleteMe: false } kept ? kept : CityRoom(ri);
            if (city is not null)
            {
                ri.CurrentLevelVolume = city;
            }
        }

        _room = null;
        _cityRoom = null;
    }

    // The place's room the player is in, or else any of its rooms.
    static RLevelVolume? Room(Spot spot)
    {
        RLevelVolume? any = null;
        var at = World.Player?.Location;
        foreach (var volume in Game.FindObjects<RLevelVolume>())
        {
            if (!volume.IsValid || volume.bDeleteMe || !spot.Parts.Contains(volume.Level.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (at is { } here && volume.EncompassesPoint(here))
            {
                return volume;
            }

            any ??= volume;
        }

        return any;
    }

    static RLevelVolume? CityRoom(RGameRI ri)
    {
        var level = ri.CurrentLevel.ToString();
        foreach (var volume in Game.FindObjects<RLevelVolume>())
        {
            if (volume.IsValid && !volume.bDeleteMe && string.Equals(volume.Level.ToString(), level, StringComparison.OrdinalIgnoreCase)
                && (_home is null || volume.EncompassesPoint(_home.At)))
            {
                return volume;
            }
        }

        return null;
    }

    // The player is somewhere the place isn't, after a teleport or a fall.
    static bool Gone(Spot spot) =>
        World.Player is { } player && Measure(spot) is { } placed && Vector3.Distance(player.Location, Middle) > placed.Size + LeftSet;

    static void Finish(Outcome outcome)
    {
        Teleport.Ri?.SetFadeType(true, FadeBack, false, false, 0f, 0f, 0f, 0f);
        Enter(Stage.Brightening);
        _by?.Report(outcome);
        _by = null;
    }

    static void Enter(Stage stage)
    {
        _stage = stage;
        _stageAt = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    static float Seconds(long since) => (float)System.Diagnostics.Stopwatch.GetElapsedTime(since).TotalSeconds;

    // A room switches the current district when the player walks in or out of it. Away, they
    // stay out of it, so the district the player left stays current and loaded.
    [Redirect(typeof(RLevelVolume), nameof(RLevelVolume.Touch))]
    static void Touch(RLevelVolume self, Actor other, PrimitiveComponent otherComp, Vector3 hitLocation, Vector3 hitNormal)
    {
        if (Away)
        {
            return;
        }

        self.Touch(other, otherComp, hitLocation, hitNormal);
    }

    [Redirect(typeof(RLevelVolume), nameof(RLevelVolume.UnTouch))]
    static void UnTouch(RLevelVolume self, Actor other)
    {
        if (Away)
        {
            return;
        }

        self.UnTouch(other);
    }

    // A level of the place still in memory from an earlier visit.
    static bool Leftover(Spot spot)
    {
        if (!Game.TryGetWorldInfo(out var world) || !world.IsValid)
        {
            return false;
        }

        foreach (var level in world.StreamingLevels)
        {
            if (level is { IsValid: true, bIsVisible: false } && level.LoadedLevel is { IsValid: true } && Ours(level.PackageName.ToString() ?? "", spot))
            {
                return true;
            }
        }

        return false;
    }

    // Whether the game has any of these levels showing.
    static bool Showing(string[] parts)
    {
        if (!Game.TryGetWorldInfo(out var world) || !world.IsValid)
        {
            return false;
        }

        foreach (var level in world.StreamingLevels)
        {
            if (level is { IsValid: true, bIsVisible: true } && parts.Contains(level.PackageName.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // The game's own request for each part, the one its doors make. Each also brings in its
    // parent package and its other pieces.
    static void Request(RGameRI ri, Spot spot)
    {
        if (_asked)
        {
            Keep(spot);
            return;
        }

        _asked = true;
        Before.Clear();
        if (Game.TryGetWorldInfo(out var world) && world.IsValid)
        {
            foreach (var level in world.StreamingLevels)
            {
                if (level is { IsValid: true } && (level.bIsVisible || level.LoadedLevel is { IsValid: true }))
                {
                    Before.Add(level.Ptr);
                }
            }
        }

        var info = new RGameRI.FStreamingTransitionInfo { bIgnoreDistancePenaltyForNoVisibleOriginators = true };
        foreach (var part in spot.Parts)
        {
            var level = new FName(part);
            ri.AddStreamingLevelRequest(level, ri.CurrentLevel, ri, info, false, false);
            ri.AddStreamingLevelVisibleRequest(level, ri);
        }

        // The entries are made by the requests and load later in the frame, so they are moved now.
        Keep(spot);
    }

    static void Drop(RGameRI ri, Spot spot)
    {
        if (!_asked)
        {
            return;
        }

        _asked = false;
        _clearAt = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(ClearAfter * System.Diagnostics.Stopwatch.Frequency);
        foreach (var part in spot.Parts)
        {
            var level = new FName(part);
            ri.RemoveStreamingLevelVisibleRequest(level, ri);
            ri.RemoveStreamingLevelRequest(level, ri);
        }
    }

    // A level of the place: a part, a piece of one, or the parent they share.
    static bool Ours(string name, Spot spot) =>
        string.Equals(name, spot.Family, StringComparison.OrdinalIgnoreCase)
        || spot.Parts.Any(part => name.StartsWith(part, StringComparison.OrdinalIgnoreCase));

    // The game streams each level where it was built. The place's entries get our place before
    // they load, and keep it while it's in use.
    static void Keep(Spot spot)
    {
        if (!Game.TryGetWorldInfo(out var world) || !world.IsValid || Measure(spot) is not { } placed)
        {
            return;
        }

        foreach (var level in world.StreamingLevels)
        {
            if (level is not { IsValid: true } || Before.Contains(level.Ptr) || !Ours(level.PackageName.ToString() ?? "", spot)
                || level.Offset == placed.Offset)
            {
                continue;
            }

            if (level.bIsVisible && !_moved)
            {
                _moved = true;
                Log.Warn("locations.moved", $"{spot.Label}: the game moved {level.PackageName} to {level.Offset} while it was showing.");
            }

            level.Offset = placed.Offset;
        }
    }

    // Every part of the place is in and showing.
    static bool Shown(Spot spot)
    {
        if (!Game.TryGetWorldInfo(out var world) || !world.IsValid)
        {
            return false;
        }

        var showing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var level in world.StreamingLevels)
        {
            if (level is { IsValid: true, bIsVisible: true } && level.LoadedLevel is { IsValid: true })
            {
                showing.Add(level.PackageName.ToString() ?? "");
            }
        }

        return spot.Parts.All(showing.Contains);
    }

    // Where the player arrives: the spot they saved for it or the one set for the place, else
    // where the place's challenges
    // start the player, else where its own story starts them, else the path point nearest its
    // middle.
    static (Vector3 At, float Yaw, string Kind, bool Exact)? Start(Spot spot)
    {
        if ((Arrival(spot) ?? spot.Arrive) is { } chosen && Measure(spot) is { } placed)
        {
            return (chosen.At + placed.Offset, chosen.Yaw, "arrival spot", true);
        }

        bool InPlace(GameObject thing)
        {
            var path = thing.GetPathName(null);
            var dot = path.IndexOf('.');
            return dot > 0 && Ours(path[..dot], spot);
        }

        RChallengeModeStartPointBase? challenge = null;
        foreach (var point in Game.FindObjects<RChallengeModeStartPointBase>())
        {
            if (point.IsValid && !point.bDeleteMe && InPlace(point)
                && (challenge is null || string.CompareOrdinal(point.Name.ToString(), challenge.Name.ToString()) < 0))
            {
                challenge = point;
            }
        }

        if (challenge is not null)
        {
            return (challenge.Location, challenge.Rotation.Yaw, "challenge start", false);
        }

        foreach (var point in Game.FindObjects<PlayerStart>())
        {
            if (point.IsValid && !point.bDeleteMe && InPlace(point))
            {
                return (point.Location, point.Rotation.Yaw, "player start", false);
            }
        }

        NavigationPoint? nearest = null;
        var best = float.MaxValue;
        foreach (var point in Game.FindObjects<NavigationPoint>())
        {
            if (!point.IsValid || point.bDeleteMe || !InPlace(point))
            {
                continue;
            }

            var distance = Vector3.DistanceSquared(point.Location, Middle);
            if (distance < best)
            {
                best = distance;
                nearest = point;
            }
        }

        return nearest is null ? null : (nearest.Location, nearest.Rotation.Yaw, "middle", false);
    }

    static void Save()
    {
        if (_inside is { } spot && _home is { } home && Game.TryGetWorldInfo(out var world))
        {
            Survive.Set(InsideKey, spot.Id);
            Survive.Set(HomeKey, new double[] { home.At.X, home.At.Y, home.At.Z, home.Yaw });
            Survive.Set(WorldKey, (long)world.Ptr);
            return;
        }

        Survive.Set<string?>(InsideKey, null);
        Survive.Set<double[]?>(HomeKey, null);
        Survive.Set(WorldKey, 0L);
    }

    static void Restore()
    {
        var id = Survive.Get<string?>(InsideKey, null);
        var at = Survive.Get<double[]?>(HomeKey, null);
        if (id is null || at is not { Length: >= 4 } || !Game.TryGetWorldInfo(out var world)
            || Survive.Get(WorldKey, 0L) != (long)world.Ptr || Spots.FirstOrDefault(spot => spot.Id == id) is not { } inside)
        {
            Forget();
            return;
        }

        _inside = inside;
        _spot = inside;
        _asked = true;
        _home = new Home(new Vector3((float)at[0], (float)at[1], (float)at[2]), (float)at[3]);
    }

    static void Forget()
    {
        _stage = Stage.Idle;
        _spot = null;
        _inside = null;
        _home = null;
        _by = null;
        _moved = false;
        _asked = false;
        _placed = null;
        Before.Clear();
        Save();
    }

    // A hot reload mid-trip mustn't leave the screen black. A finished trip is kept for the new
    // assembly, and the place stays loaded under the player.
    public static void Release()
    {
        if (_stage is Stage.Darkening or Stage.Moving && Teleport.Ri is { } ri && _spot is { } spot)
        {
            if (_goingIn)
            {
                Drop(ri, spot);
            }

            _fresh = false;
            ri.SetFadeType(true, FadeBack, false, false, 0f, 0f, 0f, 0f);
        }

        _stage = Stage.Idle;
        _by = null;
    }
}
