#nullable enable

namespace Knightfall;

// The pages and tabs of the menu, in the order they appear, and the work that runs every game
// tick whatever the options say. A job's key is the page and tab it serves, or the id of the one
// option it serves. It is the name a failure is logged and shown under.
static class Layout
{
    public static void Build()
    {
        Catalog.Clear();

        Catalog.AddPage("PLAYER")
            .Add("STATUS", Status.Sections)
            .Add("MOVEMENT", Movement.Sections)
            .Add("COMBAT", Combat.Sections)
            .Add("GADGETS", Gadgets.Sections)
            .Add("CHARACTER",
                new Catalog.Section("SWITCH", Characters.PlayAs, Characters.Switch, Characters.Back),
                new Catalog.Section("STYLE", MovementStyle.Style),
                new Catalog.Section("DISGUISE", Disguise.Look, Disguise.Variant, Disguise.Option, Disguise.WearAimed, Disguise.Shuffle, Disguise.Stance, Disguise.Props, DisguiseFit.Camera, DisguiseFit.Gadgets))
            .Add("SUIT",
                new Catalog.Section("SUIT", Suits.Suit, Suits.Apply, Suits.Cancel),
                new Catalog.Section("APPEARANCE", Appearance.NoCape, Appearance.BodySize, Appearance.Damage, Appearance.ApplyDamage),
                new Catalog.Section("PAINT", SuitPaint.Paint, SuitPaint.Which, SuitPaint.Colour, SuitPaint.Strength, SuitPaint.Brightness,
                    SuitPaint.CopyLook, SuitPaint.PasteLook, SuitPaint.Shuffle, SuitPaint.Back, SuitPaint.Unmark, SuitPaint.ClearPart, SuitPaint.ClearSuit),
                new Catalog.Section("LOOKS", SuitLooks.Saved, SuitLooks.SaveNew, SuitLooks.WearSaved, SuitLooks.PutOn, SuitLooks.Delete))
            .Add("VILLAINS", Villains.Sections)
            .Add("SIDEKICK", Sidekick.Sections)
            .Add("PROGRESS", Progress.Sections);

        Catalog.Background("player.locks", PlayerLocks.Tick);
        Catalog.Background("player.villains", Villains.Watch);
        Catalog.Background("player.combat.enemies", Combat.HoldEnemies);
        // Defence runs first, so AUTO FIGHT can see that it acted this tick.
        Catalog.Background("player.combat.defence", AutoDefence.Tick);
        Catalog.Background("player.combat.auto_fight", AutoFight.Tick);
        Catalog.Background("player.character.remesh", Remesh.Tick, Remesh.Release);
        Catalog.Background("player.character.switch", Switcher.Tick, Switcher.Release);
        Catalog.Background("player.suit.paint", SuitPaint.Tick, SuitPaint.Release);
        Catalog.Background("player.sidekick.who", Sidekick.Tick, Sidekick.Release);
        Catalog.Background("player.character.movement_style", MovementStyle.Tick);
        Catalog.Background("player.progress.achievements", Progress.Tick, Progress.Release);
        Catalog.Background("player.character.disguise_fit", DisguiseFit.Tick, DisguiseFit.Release);
        Catalog.AddPage("BATMOBILE")
            .Add("DRIVING", Driving.Sections)
            .Add("TUNING", Driving.Tuning)
            .Add("WEAPONS", Weapons.Sections)
            .Add("DASHBOARD", Dashboard.Sections)
            .Add("SKIN",
                new Catalog.Section("SKINS", Skins.Skin, Skins.Apply, Skins.Cancel),
                new Catalog.Section("SOUND", EngineSound.Sound),
                new Catalog.Section("BODYWORK", CarWash.Clean, CarWash.KeepClean),
                new Catalog.Section("PAINT", CarPaint.Paint, CarPaint.Which, CarPaint.Colour, CarPaint.Strength, CarPaint.Brightness,
                    CarPaint.CopyLook, CarPaint.PasteLook, CarPaint.Shuffle, CarPaint.Back, CarPaint.Unmark, CarPaint.ClearPart, CarPaint.ClearAll),
                new Catalog.Section("LOOKS", CarLooks.Saved, CarLooks.SaveNew, CarLooks.WearSaved, CarLooks.PutOn, CarLooks.Delete));

        Catalog.Background("batmobile.car", Car.Tick);
        Catalog.Background("batmobile.driving.autopilot", Autopilot.Tick, Autopilot.Release);
        Catalog.Background("core.borrowed", Borrowed.Tick, Borrowed.Release);
        Catalog.Background("batmobile.skin", Skins.Tick, Skins.Release);
        Catalog.Background("batmobile.skin.paint", CarPaint.Tick, CarPaint.Release);
        Catalog.Background("batmobile.driving.dashboard", Dashboard.Track);

        Catalog.AddPage("TELEPORT")
            .Add("WAYPOINT", Teleport.Sections)
            .Add("PLACES", Places.Sections)
            .Add("LOCATIONS", Locations.Section)
            .Add("MOVE", Blink.Sections.Append(Fetch.Section).Append(Leave.Section).ToArray());

        Catalog.Background("teleport.waypoint", Teleport.Tick, Teleport.Release);
        Catalog.Background("teleport.places", Places.Tick);
        Catalog.Background("teleport.locations", Locations.Tick, Locations.Release);

        Catalog.AddPage("CAMERA")
            .Add("VIEW", CameraView.Sections)
            .Add("FREE CAMERA", FreeCamera.Sections)
            .Add("EYES", Eyes.Sections);

        Catalog.Background("camera", Eyes.Steer, CameraHook.Release);

        Catalog.AddPage("WORLD")
            .Add("CITY",
                new Catalog.Section("TIME", WorldTime.Speed, WorldTime.Freeze), Streets.Section, Riots.Section, Spawns.Section, Joker.Section)
            .Add("ENEMIES", Enemies.Sections)
            .Add("FRIENDLIES", Enemies.FriendlySections)
            .Add("MARKERS", Markings.Sections.Append(Threats.Section).Append(MapMarks.Section).ToArray())
            .Add("ATMOSPHERE", Atmosphere.Sections)
            .Add("WEATHER", Weather.Section, Rain.Section, Snow.Section, Lightning.Section, Wind.Section)
            .Add("SCENES", Scenes.Section);

        Catalog.Background("world.city.game_speed", WorldTime.HoldSpeed, WorldTime.ReleaseSpeed);
        Catalog.Background("world.city.spawns", Spawns.Watch, Spawns.Forget);
        Catalog.Background("world.city.joker_thugs", Joker.TurnBack, Joker.TurnAllBack);
        Catalog.Background("world.enemies.holds", EnemyHolds.Tick);
        Catalog.Background("world.enemies.hacks", Militia.Check, Militia.Forget);
        // The sky first, so the look can find the grade the era's package brings.
        Catalog.Background("world.atmosphere.sky", CitySky.Tick, CitySky.Release);
        Catalog.Background("world.atmosphere", Atmosphere.Tick, Atmosphere.Release);
        Catalog.Background("world.weather.snow", Snow.Tick, Snow.Release);
        Catalog.Background("world.weather.rain", Rain.Tick, Rain.Release);
        Catalog.Background("world.weather.lightning", Lightning.Tick, Lightning.Release);
        Catalog.Background("world.weather.wind", Wind.Tick, Wind.Release);

        Catalog.AddPage("SPAWNER")
            .Add("CHARACTERS", People.Sections)
            .Add("BODYGUARDS", Bodyguards.Sections)
            .Add("VEHICLES", Vehicles.Sections)
            .Add("OBJECTS", Objects.Sections)
            .Add("WEAPONS", Arms.Sections)
            .Add("SPAWNED", Spawned.Sections.Append(Editor.SpawnerSection).ToArray());

        Catalog.Background("spawner.spawned", Spawned.Tick, Spawned.Forget);
        Catalog.Background("spawner.characters", People.Tick);
        Catalog.Background("spawner.bodyguards", Bodyguards.Tick, Bodyguards.Release);
        Catalog.Background("spawner.vehicles", Vehicles.Tick);
        Catalog.Background("spawner.objects", Objects.Tick);
        Catalog.Background("spawner.weapons", Arms.Tick);

        Catalog.AddPage(Editor.Page)
            .Add("SELECT", Editor.Sections)
            .Add("MOVE", Moving.Sections)
            .Add("CHANGE", Change.Sections)
            .Add("MODE", EditorMode.Sections)
            .Add("KEEP", Kept.Sections);

        Catalog.Background("editor", Editor.Tick, Editor.Release);

        Catalog.AddPage("SOUNDS")
            .Add("GAME SOUNDS", GameSounds.Sections);

        Catalog.Background("sounds.game_sounds", GameSounds.Tick, GameSounds.Release);

        Catalog.Background("assets.textures", Textures.Tick);
        Catalog.Background("assets.fonts", Fonts.Tick);
        Catalog.Background("assets.viewer", Viewer.Tick, Viewer.Release);
        Menu.Beside = Viewer.Beside;
        Menu.Beside += CaseFile.Beside;

        // Before SETTINGS, whose CONTROLS tab lists every option that has a key.
        DefaultKeys();

        Catalog.AddPage("SETTINGS")
            .Add("GENERAL", General.Sections)
            .Add("LOOK", Look.Rows)
            .Add("CONTROLS", KeySetup.Sections)
            .Add("RADIAL", Radial.Sections)
            .Add("GAME", GameTweaks.Sections)
            .Add("TEXTURES", Textures.Sections)
            .Add("FONTS", Fonts.Sections)
            .Add("SAVES", Saves.Sections);

        Catalog.Background("settings.game.values", SystemValues.Tick);
        Catalog.Background("settings.game.debug_keys", DebugKeys.Tick, DebugKeys.Release);
        Catalog.Background("settings.saves", Saves.Tick);
        Catalog.Background("settings.look.gauntlet", Gauntlet.Tick, Gauntlet.Release);
    }

    // The hotkeys options come with. Any the player bound are read after these.
    public static void DefaultKeys()
    {
        Hotkeys.Clear();
        Hotkeys.Add(Stunts.Jump, key: "J");
        Villains.DefaultKeys();
    }
}
