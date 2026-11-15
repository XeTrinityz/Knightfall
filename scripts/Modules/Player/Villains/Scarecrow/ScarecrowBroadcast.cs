#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.GFxUI;

namespace Knightfall;

// His broadcast, the way he took over every screen in the city. One of his own broadcasts plays
// into the texture the game's takeovers use, and the screens around him that he took over in the
// story cut to it through static. The videos carry no sound, as the game speaks their lines
// separately, so he speaks the line with it. Thugs who hear it run for their lives.
static partial class Scarecrow
{
    const string FeedMoviePath = "FullScreenVideo.TextureMovie";
    const string FeedPath = "VFX_Textures.TV_Sequences.Steel_C4_JokerTV";
    static readonly FName MovieParameter = new("Movie");

    // His broadcasts whose lines fit any street, by their video's name.
    static readonly string[] Broadcasts =
    {
        "Scenes_Tv_Scarecrow_City_Ch03_A",
        "Scenes_Tv_Scarecrow_City_Ch01_H",
        "Scenes_Tv_Scarecrow_City_Ch01_B",
        "Scenes_Tv_Scarecrow_City_Ch05_D",
        "Scenes_Tv_Scarecrow_City_Ch07_Pre_Gas_F",
        "Scenes_Tv_Scarecrow_City_Ch07_Post_Gas_J",
        "Scenes_Tv_Scarecrow_City_Ch09_J",
    };

    // The set of the line each one speaks, in the same order.
    static readonly string[] OnAirLines = { "air army", "air city", "air night", "air gotham", "air witness", "air gift", "air face" };

    // Screens this close are taken over. Thugs this close to him or to a screen showing him hear
    // it. A broadcast is cut off after this long, and he can go on air again this long after one
    // ends.
    const float ScreenReach = 12000f;
    const float HeardNear = 2000f;
    const float HeardAtScreen = 1500f;
    const float HeardHeight = 500f;
    const float LongestBroadcast = 20f;
    const float BroadcastWait = 25f;

    // The static the screens cut in and out through.
    const float StaticSeconds = 0.6f;

    static RGFxMovieVideoPlayer? _feed;
    static TextureRenderTarget2D? _feedTexture;
    static Texture? _overrideWas;
    static bool _overrideHeld;
    static readonly List<RTakeoverVideoScreenBase> TakenOver = new();
    static float _onAirAt = -100f;
    static float _offAirAt;
    static float _nextBroadcast;
    static float _closingAt;
    static int _lastBroadcast = -1;

    static bool OnAir => _feed is not null;

    static Outcome Broadcast()
    {
        if (!Ready(out var player, out var controller))
        {
            return Outcome.Unchanged("SCARECROW IS OFF");
        }

        if (OnAir)
        {
            return Outcome.Unchanged("ON AIR");
        }

        if (World.Now < _nextBroadcast)
        {
            return Outcome.Unchanged("RECOVERING");
        }

        if (Game.FindObject<SwfMovie>(FeedMoviePath) is not { IsValid: true } movie || Game.FindObject<TextureRenderTarget2D>(FeedPath) is not { IsValid: true } texture)
        {
            return Outcome.Unchanged("NO SIGNAL");
        }

        var pick = Random.Shared.Next(Broadcasts.Length - 1);
        if (pick >= _lastBroadcast && _lastBroadcast >= 0)
        {
            pick++;
        }

        _lastBroadcast = pick;
        var screens = ScreensNear(player.Location);
        var feed = Made.Tracked(new RGFxMovieVideoPlayer(controller));
        feed.MovieInfo = movie;
        feed.RenderTexture = texture;
        feed.RenderTextureMode = GFxMoviePlayer.GFxRenderTextureMode.RTM_Opaque;
        if (!feed.Initialize(controller, Broadcasts[pick], 0f, false))
        {
            Log.Warn("scarecrow.broadcast", "The Scarecrow's broadcast wouldn't start.");
            return Outcome.Unchanged("NO SIGNAL");
        }

        feed.MoviePlay();
        _feed = feed;
        _feedTexture = texture;
        _onAirAt = World.Now;
        _offAirAt = World.Now + LongestBroadcast;
        _closingAt = 0f;
        _nextBroadcast = float.MaxValue;
        Speech.Speak(OnAirLines[pick], always: true);

        // The screens show the takeover through the game's own override material.
        if (World.Info is { IsValid: true } game && game.VideoScreenOverrideMIC is { IsValid: true } shared && !_overrideHeld
            && shared.GetTextureParameterValue(MovieParameter, out var was))
        {
            _overrideWas = was;
            _overrideHeld = true;
            shared.SetTextureParameterValue(MovieParameter, texture);
        }

        foreach (var screen in screens)
        {
            screen.SetTakeoverTexture(texture);
            screen.SetStaticTransitionParameter(1f);
            TakenOver.Add(screen);
        }

        Frighten(player, screens);
        return Outcome.Done("ON AIR");
    }

    // The screens he took over in the story, nearest first.
    static List<RTakeoverVideoScreenBase> ScreensNear(Vector3 at)
    {
        var found = new List<(RTakeoverVideoScreenBase Screen, float Distance)>();
        foreach (var screen in Game.FindObjects<RTakeoverVideoScreenBase>())
        {
            if (screen is not { IsValid: true, IsClassDefaultObject: false, bHidden: false, bCanBeTakenOverByScarecrow: true, bTakeoverInProgress: false })
            {
                continue;
            }

            var distance = Vector3.Distance(screen.Location, at);
            if (distance <= ScreenReach)
            {
                found.Add((screen, distance));
            }
        }

        return found.OrderBy(entry => entry.Distance).Select(entry => entry.Screen).ToList();
    }

    // Everyone in earshot of him or of a screen showing him runs. A thug already lost in his
    // panic doesn't hear a thing.
    static void Frighten(RPawnPlayer player, List<RTakeoverVideoScreenBase> screens)
    {
        foreach (var thug in Crowd.StandingEnemies().ToList())
        {
            if (Panicked(thug) || thug.DrivenVehicle is { IsValid: true }
                || (!Hears(thug, player.Location, HeardNear) && !screens.Any(screen => Hears(thug, screen.Location, HeardAtScreen))))
            {
                continue;
            }

            if (!People.Flee(thug))
            {
                Panic(player, thug);
            }
        }
    }

    static bool Hears(RPawnVillain thug, Vector3 from, float reach) =>
        Level(thug.Location - from).Length() <= reach && MathF.Abs(thug.Location.Z - from.Z) <= HeardHeight;

    // Each tick on air: the screens come out of static, and back into it as the feed ends.
    static void KeepBroadcast()
    {
        if (_feed is null)
        {
            return;
        }

        var ended = !_feed.IsValid || _feed.Finished || _feed.bSWFClosed || World.Now >= _offAirAt;
        if (ended && _closingAt <= 0f)
        {
            _closingAt = World.Now;
        }

        var level = _closingAt > 0f
            ? Math.Clamp((World.Now - _closingAt) / StaticSeconds, 0f, 1f)
            : 1f - Math.Clamp((World.Now - _onAirAt) / StaticSeconds, 0f, 1f);
        foreach (var screen in TakenOver)
        {
            if (screen.IsAlive)
            {
                screen.SetStaticTransitionParameter(level);
            }
        }

        if (_closingAt > 0f && level >= 1f)
        {
            OffAir();
        }
    }

    // Ends the broadcast, puts every screen back as it was and the override back as it was.
    static void OffAir()
    {
        foreach (var screen in TakenOver)
        {
            if (screen.IsAlive)
            {
                screen.RevertToStandardTexture();
            }
        }

        TakenOver.Clear();
        if (_feed is { IsValid: true } feed && !feed.bSWFClosed)
        {
            feed.MovieClose();
        }

        if (_overrideHeld)
        {
            if (World.Info is { IsValid: true } game && game.VideoScreenOverrideMIC is { IsValid: true } shared)
            {
                shared.SetTextureParameterValue(MovieParameter, _overrideWas is { IsValid: true } was ? was : null!);
            }

            _overrideWas = null;
            _overrideHeld = false;
        }

        if (_feed is not null)
        {
            _nextBroadcast = World.Now + BroadcastWait;
        }

        _feed = null;
        _closingAt = 0f;
    }
}
