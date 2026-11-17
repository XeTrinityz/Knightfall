#nullable enable

using System.Diagnostics;
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using View = BmSDK.GameObject.FTPOV;

namespace Knightfall;

// CAMERA > EYES. The view moves into someone nearby who can see Batman and watches him from
// there. The game turns the stick by the view it drew, so moving and fighting follow the screen
// without any help. Only a switch needs care, see Steer.
static class Eyes
{
    const float Metre = Teleport.UnitsPerMetre;
    static readonly FName Head = new("Bip01_Head");

    // How often the people around are looked over, in seconds.
    const float LookOver = 0.25f;

    // The one watching keeps the view until they go down, leave RANGE or lose sight of Batman.
    // Only once they're this far out, as a share of RANGE, can someone much closer take over, and
    // not before they've watched this long. Swapping mid fight is hard to follow.
    const float Far = 0.6f;
    const float Hold = 3f;

    // Someone who loses sight of Batman, behind a pillar say, keeps the view this long.
    const float Blink = 2f;

    // Someone new has to score this much better than the one watching to take over.
    const float Better = 0.4f;

    // The one watching is kept a little past RANGE, so the view doesn't flick at the edge.
    const float RangeSlack = 1.15f;

    // Nearer than this the view is all chest, so they count as further away.
    const float TooNear = 3f * Metre;

    // Someone facing away from Batman counts as this much further. Facing means within 60
    // degrees either side.
    const float FacingAway = 2.5f;
    const float FacingCone = 0.5f;

    // Lines of sight tried per look, best first.
    const int SightChecks = 6;

    // Where the view sits from the head bone, up and out in front of the face.
    const float EyeUp = 8f;
    const float EyeOut = 18f;

    // Where on Batman the view looks, above his middle.
    const float Chest = 25f;

    // How quickly the view follows the head and turns, per second.
    const float Follow = 20f;
    const float Turn = 8f;

    // How long GLIDE takes to cross to someone new.
    const float GlideTime = 0.6f;

    const float SteepestPitch = 89f;
    const float LongestStep = 0.1f;

    // How far the stick has to be pushed to count as held, on the pad's 0 to 1 scale.
    const float StickHeld = 0.2f;

    // NEXT PAIR OF EYES goes round this many before coming back to one.
    const int Remembered = 4;

    static readonly List<(RBMPawnAI Body, float Score)> Found = new();
    static readonly Queue<nint> Recent = new();

    static RBMPawnAI? _watcher;
    static int _epoch = -1;
    static long _lastAt;
    static float _sinceLook = LookOver;
    static float _watched;
    static float _unseen;
    static (SkeletalMeshComponent Mesh, int Bone)? _hidden;

    static bool _settled;
    static Vector3 _eye;
    static float _yaw;
    static float _pitch;

    // What was on screen when a switch began, and how far the glide has come from it.
    static View _from;
    static float _glide = 1f;
    static View _shown;
    static bool _known;

    // True while the game has given the view over this frame, so the tag can show.
    static bool _active;

    // The way the stick pointed before a switch, kept until it's let go.
    static bool _steering;
    static float _steerYaw;

    // The tag's line, made again only when the name or the metres change.
    static string _tagName = "";
    static int _tagMetres = -1;
    static GuideLine? _tagLine;
    static GuideLine? _nobodyLine;

    public static readonly ModeToggle On = new(
        "camera.eyes.on", "THROUGH THEIR EYES",
        "Shows Batman through the eyes of someone nearby who can see him, like a thug, a cop or a rioter. You play as normal and the stick follows what you see. When nobody can see you the normal camera comes back. Left and right pick who can lend you their eyes. FRIENDLIES are the police, fire crews and your bodyguards.",
        "whose",
        new[] { ("anyone", "ANYONE"), ("enemies", "ENEMIES"), ("friendlies", "FRIENDLIES") },
        saved: false)
    {
        Restore = Release,
    };

    public static readonly Choice Look = new(
        "camera.eyes.look", "LOOK",
        "AT BATMAN keeps him in the middle of the view. WHERE THEY LOOK follows their head, so he can drift out of sight.",
        new[] { ("batman", "AT BATMAN"), ("theirs", "WHERE THEY LOOK") })
    {
        Preference = true,
    };

    public static readonly Choice Switch = new(
        "camera.eyes.switch", "SWITCH",
        "How the view moves to someone new. CUT jumps straight there. GLIDE moves across.",
        new[] { ("cut", "CUT"), ("glide", "GLIDE") })
    {
        Preference = true,
    };

    public static readonly Slider Range = new(
        "camera.eyes.range", "RANGE",
        "How far away someone can be and still lend you their eyes.",
        min: 10, max: 60, step: 5, value: 30, unit: " M")
    {
        Preference = true,
    };

    public static readonly Toggle ShowWho = new(
        "camera.eyes.show_who", "SHOW WHO'S WATCHING",
        "Shows who you're seen by and how far away they are, or that nobody can see you.",
        on: true)
    {
        Preference = true,
    };

    public static readonly Command Next = new(
        "camera.eyes.next", "NEXT PAIR OF EYES",
        "Moves the view to someone else who can see Batman. Give it a key to switch while you play.",
        Cycle)
    {
        Block = static () => On.On ? null : "EYES ARE OFF",
    };

    public static readonly Catalog.Section[] Sections =
    {
        new("EYES", On, Look, Switch, Range, ShowWho, Next),
    };

    // True while someone is lending their eyes, so Batman keeps his head.
    public static bool Watching => _watcher is not null;

    // True while the view is wholly someone else's, so the work on the view before it can be skipped.
    public static bool HasView => _watcher is not null && _glide >= 1f;

    // Called from the camera hook with the view the game worked out. Scenes, aiming, the car and
    // the free camera all take the view straight back.
    public static void Fit(ref View view, R3rdPersonCamera camera, RPlayerController controller)
    {
        // Real time, so slow motion doesn't slow the view.
        var now = Stopwatch.GetTimestamp();
        var seconds = _lastAt == 0 ? 0f : MathF.Min((float)((now - _lastAt) / (double)Stopwatch.Frequency), LongestStep);
        _lastAt = now;

        // A new world has none of the old one's people.
        if (_epoch != World.Epoch)
        {
            _epoch = World.Epoch;
            _watcher = null;
            _hidden = null;
            _glide = 1f;
            _known = false;
        }

        var player = World.Player;
        var wanted = On.On && player is { IsValid: true } && World.Driving is null && !FreeCamera.HasView
            && !controller.bCinematicMode && !Switcher.InScene(controller) && !CameraView.Aims(camera);
        if (!wanted)
        {
            Release();
            return;
        }

        _active = true;

        if (_watcher is { } watcher && !(watcher.IsValid && watcher.Mesh is { IsValid: true }))
        {
            Swap(null);
        }

        _watched += seconds;
        _sinceLook += seconds;
        if (_sinceLook >= LookOver)
        {
            Choose(player!, _sinceLook);
            _sinceLook = 0f;
        }

        var shown = view;
        if (_watcher is { } body)
        {
            Watch(ref shown, body, player!, seconds);
        }

        if (_glide < 1f)
        {
            _glide = MathF.Min(1f, _glide + seconds / GlideTime);
            shown = Mix(_from, shown, Smooth(_glide));
        }

        // What's on screen is kept, so GLIDE can start from it.
        if (_watcher is null && _glide >= 1f)
        {
            _shown = view;
            _known = true;
            return;
        }

        view = shown;
        _shown = shown;
        _known = true;
    }

    // An unload, the row going off or the game taking the view leaves the game its own camera
    // and the one watching their head back.
    public static void Release()
    {
        ShowHead();
        _watcher = null;
        _glide = 1f;
        _known = false;
        _sinceLook = LookOver;
        _active = false;
        _steering = false;
    }

    // Called each game tick, before Batman moves. The game steers him by the view it drew last,
    // so a cut would turn him round mid run. While the stick stays held through a switch he keeps
    // going the way he was. Letting go hands the stick back to what's on screen.
    public static void Steer()
    {
        if (!_steering)
        {
            return;
        }

        if (!On.On || !_active || !Pushed())
        {
            _steering = false;
            return;
        }

        CameraHook.Steer(_steerYaw);
    }

    static bool Pushed()
    {
        if (World.Controller?.PlayerInput is not { IsValid: true } input)
        {
            return false;
        }

        if (input.aForward != 0f || input.aStrafe != 0f)
        {
            return true;
        }

        return input is RPlayerInput pad && pad.RawLeftX * pad.RawLeftX + pad.RawLeftY * pad.RawLeftY > StickHeld * StickHeld;
    }

    // Fills the on-screen tag. It shares the key guide's panel, so every theme draws it.
    public static bool Guide(GuideView view)
    {
        if (!On.On || !ShowWho.On || !_active || World.Player is not { IsValid: true } player)
        {
            return false;
        }

        view.Lines.Clear();
        if (_watcher is { IsValid: true } watcher)
        {
            var name = Things.NameOf(watcher);
            var metres = (int)MathF.Round(Vector3.Distance(watcher.Location, player.Location) / Metre);
            if (_tagLine is null || metres != _tagMetres || !ReferenceEquals(name, _tagName))
            {
                _tagName = name;
                _tagMetres = metres;
                _tagLine = new GuideLine(Array.Empty<string>(), $"{name}  {metres} M");
            }

            view.Title = "SEEN BY";
            view.Lines.Add(_tagLine.Value);
            var key = Hotkeys.KeyText(Next);
            if (key.Length > 0)
            {
                view.Lines.Add(new GuideLine(new[] { key }, "NEXT PAIR OF EYES"));
            }

            return true;
        }

        _nobodyLine ??= new GuideLine(Array.Empty<string>(), "THE NORMAL CAMERA IS BACK");
        view.Title = "NOBODY SEES YOU";
        view.Lines.Add(_nobodyLine.Value);
        return true;
    }

    // NEXT PAIR OF EYES. It goes round the ones it hasn't shown lately before coming back.
    static Outcome Cycle()
    {
        if (World.Player is not { IsValid: true } player || World.Driving is not null)
        {
            return Outcome.NoPlayer;
        }

        var target = Target(player);
        var range = (float)Range.Value * Metre;
        var next = Best(target, range, float.MaxValue, fresh: true) ?? Best(target, range, float.MaxValue, fresh: false);
        if (next is null)
        {
            return Outcome.Unchanged(_watcher is null ? "NOBODY SEES YOU" : "NOBODY ELSE");
        }

        Swap(next);
        return Outcome.Done("SWITCHED");
    }

    static void Choose(RPawnPlayer player, float interval)
    {
        var target = Target(player);
        var range = (float)Range.Value * Metre;
        var keep = float.MaxValue;
        var near = false;
        if (_watcher is { } current)
        {
            var away = Vector3.Distance(current.Location, target);
            near = away < range * Far;
            if (!Usable(current) || away > range * RangeSlack)
            {
                Swap(null);
            }
            else
            {
                _unseen = Sees(current, target) ? 0f : _unseen + interval;
                if (_unseen > Blink)
                {
                    Swap(null);
                }
                else
                {
                    keep = Score(current, target, away);
                }
            }
        }

        if (_watcher is not null && (near || _watched < Hold))
        {
            return;
        }

        if (Best(target, range, keep * Better, fresh: false) is { } better)
        {
            Swap(better);
        }
    }

    // The best placed person who can see Batman, other than the one watching, scoring under
    // most. Fresh leaves out the ones NEXT PAIR OF EYES showed lately.
    static RBMPawnAI? Best(Vector3 target, float range, float most, bool fresh)
    {
        Found.Clear();
        var watching = _watcher?.Ptr ?? 0;
        foreach (var body in Game.FindObjects<RBMPawnAI>())
        {
            if (!body.IsValid || body.IsClassDefaultObject || body.Ptr == watching || (fresh && Recent.Contains(body.Ptr)) || !Usable(body))
            {
                continue;
            }

            var away = Vector3.Distance(body.Location, target);
            if (away <= range)
            {
                Found.Add((body, Score(body, target, away)));
            }
        }

        Found.Sort(static (a, b) => a.Score.CompareTo(b.Score));
        RBMPawnAI? best = null;
        for (var i = 0; i < Found.Count && i < SightChecks; i++)
        {
            var (body, score) = Found[i];
            if (score > most)
            {
                break;
            }

            if (HasHead(body) && Sees(body, target))
            {
                best = body;
                break;
            }
        }

        Found.Clear();
        return best;
    }

    static bool Usable(RBMPawnAI body)
    {
        if (!Crowd.Standing(body) || body.Mesh is not { IsValid: true })
        {
            return false;
        }

        return On.Mode switch
        {
            1 => Crowd.IsEnemy(body),
            2 => Crowd.IsFriendly(body),
            _ => true,
        };
    }

    // Lower is better. Close and facing Batman wins, but right up against him is no view at all.
    static float Score(RBMPawnAI body, Vector3 target, float away)
    {
        var score = away < TooNear ? 2f * TooNear - away : away;
        var toward = target - body.Location;
        toward.Z = 0f;
        if (toward.LengthSquared() > 1f)
        {
            var facing = new Rotator(0f, body.Rotation.Yaw, 0f).ToDirection();
            if (Vector3.Dot(facing, Vector3.Normalize(toward)) < FacingCone)
            {
                score *= FacingAway;
            }
        }

        return score;
    }

    static bool HasHead(RBMPawnAI body) => body.Mesh is { IsValid: true } mesh && mesh.MatchRefBone(Head) >= 0;

    // Walls, props and cars block the line. People don't, or a crowd would hide Batman from itself.
    static bool Sees(RBMPawnAI body, Vector3 target) =>
        body.Mesh is { IsValid: true } mesh && !Crosshair.Line(body, mesh.GetBoneLocation(Head, 0), target, Crosshair.Meets.Solid, out _);

    static Vector3 Target(RPawnPlayer player) => player.Location + new Vector3(0f, 0f, Chest);

    static void Watch(ref View view, RBMPawnAI body, RPawnPlayer player, float seconds)
    {
        var head = body.Mesh.GetBoneLocation(Head, 0);
        var target = Target(player);
        Vector3 at;
        Rotator turn;
        if (Look.Index == 0)
        {
            var toward = target - head;
            toward.Z = 0f;
            var flat = toward.LengthSquared() > 1f ? Vector3.Normalize(toward) : new Rotator(0f, body.Rotation.Yaw, 0f).ToDirection();
            at = head + flat * EyeOut + new Vector3(0f, 0f, EyeUp);
            turn = Rotator.FromDirection(target - at);
        }
        else
        {
            turn = body.GetBaseAimRotation();
            at = head + new Rotator(0f, turn.Yaw, 0f).ToDirection() * EyeOut + new Vector3(0f, 0f, EyeUp);
        }

        if (!float.IsFinite(at.X) || !float.IsFinite(at.Y) || !float.IsFinite(at.Z) || !float.IsFinite(turn.Yaw) || !float.IsFinite(turn.Pitch))
        {
            return;
        }

        var pitch = Math.Clamp(Normal(turn.Pitch), -SteepestPitch, SteepestPitch);
        if (!_settled)
        {
            _eye = at;
            _yaw = Normal(turn.Yaw);
            _pitch = pitch;
            _settled = true;
        }
        else
        {
            _eye = Vector3.Lerp(_eye, at, 1f - MathF.Exp(-Follow * seconds));
            var step = 1f - MathF.Exp(-Turn * seconds);
            _yaw = Normal(_yaw + Normal(turn.Yaw - _yaw) * step);
            _pitch += (pitch - _pitch) * step;
        }

        view.Location = _eye;

        // The roll is kept, so CAMERA TILT still leans it.
        view.Rotation = new Rotator(_pitch, _yaw, view.Rotation.Roll);
    }

    static void Swap(RBMPawnAI? next)
    {
        // The first switch of a run keeps its way. Later ones in the same run keep that too.
        if (_known && !_steering && Pushed())
        {
            _steering = true;
            _steerYaw = _shown.Rotation.Yaw;
        }

        ShowHead();
        _watcher = next;
        _watched = 0f;
        _unseen = 0f;
        _settled = false;
        if (next is not null)
        {
            HideHead(next);
            if (Recent.Count >= Remembered)
            {
                Recent.Dequeue();
            }

            Recent.Enqueue(next.Ptr);
        }

        _from = _shown;
        _glide = _known && Switch.Index == 1 ? 0f : 1f;
    }

    // Their own head would fill the view. One the game hid itself is left to the game.
    static void HideHead(RBMPawnAI body)
    {
        if (body.Mesh is not { IsValid: true } mesh)
        {
            return;
        }

        var bone = mesh.MatchRefBone(Head);
        if (bone < 0 || mesh.IsBoneHidden(bone))
        {
            return;
        }

        mesh.HideBone(bone, SkeletalMeshComponent.EPhysBodyOp.PBO_None);
        _hidden = (mesh, bone);
    }

    static void ShowHead()
    {
        if (_hidden is { } hidden && hidden.Mesh.IsValid)
        {
            hidden.Mesh.UnHideBone(hidden.Bone);
        }

        _hidden = null;
    }

    static View Mix(in View from, in View to, float t)
    {
        var mixed = to;
        mixed.Location = Vector3.Lerp(from.Location, to.Location, t);
        mixed.Rotation = new Rotator(
            from.Rotation.Pitch + Normal(to.Rotation.Pitch - from.Rotation.Pitch) * t,
            from.Rotation.Yaw + Normal(to.Rotation.Yaw - from.Rotation.Yaw) * t,
            from.Rotation.Roll + Normal(to.Rotation.Roll - from.Rotation.Roll) * t);
        mixed.FOV = from.FOV + (to.FOV - from.FOV) * t;
        return mixed;
    }

    static float Smooth(float t) => t * t * (3f - 2f * t);

    static float Normal(float degrees)
    {
        degrees %= 360f;
        return degrees > 180f ? degrees - 360f : degrees < -180f ? degrees + 360f : degrees;
    }
}
