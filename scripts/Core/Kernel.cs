#nullable enable

using BmSDK.Engine;

namespace Knightfall;

// Every BmSDK callback comes through here, and nothing is allowed out. An exception that leaves
// a callback ends the game on stock BmSDK. On the fork it switches that whole callback off once
// it has happened ten times in a row, and the menu would go with it.
static class Kernel
{
    static bool _ready;

    public static string Version { get; private set; } = "?";

    public static void Boot(Script entry)
    {
        if (_ready)
        {
            return;
        }

        var folder = "";
        var failed = false;

        // The order is the order settings.toml lists its sections in, so it stays as it is.
        // Without a needed step there is no menu, so the start stops there. The others only add
        // to a menu that already works, so one that fails is left out and the rest carry on.
        void Step(string what, bool needed, Action step)
        {
            if (failed)
            {
                return;
            }

            try
            {
                step();
            }
            catch (Exception error) when (needed)
            {
                failed = true;
                Log.Error("boot", $"Knightfall could not start, {what} failed", error);
            }
            catch (Exception error)
            {
                Log.Error("boot." + what, $"The {what} could not be set up", error);
            }
        }

        Step("log", needed: true, () =>
        {
            var mod = entry.Mod;
            folder = mod.DirectoryPath;
            if (mod.Config.TryGetValue("version", out var version) && version is string text)
            {
                Version = text;
            }

            var firstLoad = Log.Start(folder);
            Log.Info(firstLoad ? $"Knightfall {Version} started. {Facts()}" : "Scripts reloaded.");
        });
        Step("game folders", needed: true, () => GameFiles.Find(folder));
        Step("system values", needed: false, SystemValues.Repair);
        Step("settings", needed: true, () => Settings.Load(Path.Combine(folder, "settings.toml")));
        Step("menu keys", needed: true, Controls.Bind);
        Step("radial keys", needed: false, Radial.Bind);
        Step("menu layout", needed: true, Layout.Build);
        Step("options", needed: true, Catalog.Load);
        Step("menu position", needed: false, Look.LoadMove);
        Step("hotkeys", needed: false, Hotkeys.LoadSaved);
        Step("favourites", needed: false, Favourites.Load);
        Step("disguise stances", needed: false, Disguise.KeepStances);
        Step("settings file", needed: false, Settings.Save);
        Step("menu", needed: true, Menu.Resume);
        _ready = !failed;
    }

    // Each step has its own guard. The menu's keys still have to go back to the game when an
    // option's restore fails before them.
    public static void Shutdown()
    {
        Stop("options", Catalog.Release);
        Stop("radial", Radial.Release);
        Stop("menu keys", Controls.Release);
        Stop("drawing", Draw.Release);
        Stop("hologram", Hologram.Release);
        Stop("world time", WorldTime.Release);
        Stop("settings file", Settings.Save);
        _ready = false;
    }

    static void Stop(string what, Action step)
    {
        try
        {
            step();
        }
        catch (Exception error)
        {
            Log.Error("unload." + what, $"Unloading the {what} failed", error);
        }
    }

    public static void EnterMenu()
    {
        Run("enter menu", static () => Log.Info("At the main menu."));
    }

    public static void EnterGame()
    {
        Run("enter game", static () => Log.Info("Entered a game world."));
    }

    // The game tick is where options act on the game. It stops while the game is paused.
    public static void Tick()
    {
        Run("world tick", static () => World.Tick());
        Run("options", static () => Catalog.Tick());
    }

    // Input is read here and not in the game tick, because the draw callback also runs while the
    // game is paused and held keys still have to be handed back then.
    public static void Frame(Canvas canvas)
    {
        Run("world", static () => World.Refresh());
        Run("input", static () => Controls.Poll());
        Run("hotkeys", static () => Hotkeys.Poll());
        Run("radial", static () => Radial.Poll());
        Run("menu", static () => Menu.Update());
        Run("dash", static () => Dash.Update());
        Run("free camera", static () => FreeCamera.Poll());
        Run("editor mode", static () => EditorMode.Poll());
        Run("sidekick orders", static () => Sidekick.PollOrders());
        Run("guide", static () => Guide.Update());
        Run("freeze", static () => WorldTime.SyncFreeze());
        Run("bars draw", static target => CameraView.RenderBars(target), canvas);
        Run("dash draw", static target => Dash.Render(target), canvas);
        Run("editor draw", static target => Marks.Render(target), canvas);
        Run("radial draw", static target => Radial.Render(target), canvas);
        Run("guide draw", static target => Guide.Render(target), canvas);
        Run("villain hud draw", static target => VillainHud.Render(target), canvas);
        Run("draw", static target => Menu.Render(target), canvas);

        // After the menu, so they can see where it is and stay on top of it.
        Run("toasts draw", static target => Toasts.Render(target), canvas);
        Run("settings", static () => Settings.SaveWhenSettled());
    }

    public static void Run(string key, Action body)
    {
        if (!_ready)
        {
            return;
        }

        try
        {
            body();
        }
        catch (Exception error)
        {
            Log.Error(key, $"'{key}' failed", error);
            Told(key);
        }
    }

    public static void Run<T>(string key, Action<T> body, T argument)
    {
        if (!_ready)
        {
            return;
        }

        try
        {
            body(argument);
        }
        catch (Exception error)
        {
            Log.Error(key, $"'{key}' failed", error);
            Told(key);
        }
    }

    // Nothing may get out of here into the game's callback, not even a failed notification.
    static void Told(string key)
    {
        try
        {
            Toasts.Broke(key);
        }
        catch
        {
        }
    }

    static string Facts()
    {
        var sdk = typeof(Script).Assembly;
        var built = File.Exists(sdk.Location) ? File.GetLastWriteTime(sdk.Location).ToString("yyyy-MM-dd HH:mm") : "unknown";
        return $"BmSDK {sdk.GetName().Version} built {built}, .NET {Environment.Version}, {Environment.OSVersion}.";
    }
}
