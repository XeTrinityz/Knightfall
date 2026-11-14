#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using Pace = BmSDK.Engine.RAutomaticTransitions.EMovementSpeed;

namespace Knightfall;

// Orders for a sidekick that is out. A held spot is kept by the upkeep itself, and the partner
// AI's follow is switched off while it holds, so the follow never walks them back to you.
static partial class Sidekick
{
    const float OrderReach = Metre * 50f;
    const float HoldClose = Metre * 1.5f;

    // How far off the crosshair an enemy can be and still be picked.
    const float AimDegrees = 8f;

    static Vector3? _hold;
    static float _holdYaw;
    static bool _arrived;
    static RPawnVillain? _focus;

    static bool Holding => _hold is not null;

    // Holding a spot or going after a target. Either way the partner AI's follow is off.
    static bool Ordered => Holding || _focus is not null;

    // Order mode lets go by itself after this long without an order.
    const float AimSeconds = 12f;

    static bool _aiming;
    static float _aimSince;

    static bool Aiming => _aiming && Present && World.Now >= _aimSince && World.Now - _aimSince < AimSeconds;

    // In order mode a left click gives the order, and the game never sees it.
    static readonly Keys[] OrderKeys = { Keys.LButton, Keys.RButton };
    static readonly bool[] OrderTaken = new bool[2];
    static readonly bool[] OrderWas = new bool[2];

    // Every drawn frame. The buttons go back to the game under the menu and in a pause, and only
    // change hands while they're up, or the game would see one stuck down.
    public static void PollOrders()
    {
        var want = Aiming && !Menu.Showing && !World.Paused;
        for (var i = 0; i < OrderKeys.Length; i++)
        {
            var key = OrderKeys[i];
            if (want != OrderTaken[i] && !Keyboard.IsDown(key))
            {
                if (want)
                {
                    Swallowed.Take(key);
                }
                else
                {
                    Swallowed.Give(key);
                }

                OrderTaken[i] = want;
            }

            var down = OrderTaken[i] && Keyboard.IsDown(key);
            if (down && !OrderWas[i])
            {
                Toasts.Run(i == 0 ? Order : Recall);
            }

            OrderWas[i] = down;
        }
    }

    // Swallowed keys outlive a reload, so the buttons are handed back before this copy goes.
    static void GiveClick()
    {
        for (var i = 0; i < OrderKeys.Length; i++)
        {
            if (OrderTaken[i])
            {
                Swallowed.Give(OrderKeys[i]);
                OrderTaken[i] = false;
            }

            OrderWas[i] = false;
        }
    }

    // What the crosshair would order right now.
    enum Aim
    {
        Spot,
        Enemy,
        Back,
        Stay,
    }

    readonly record struct Target(Aim Kind, Vector3 At, float Yaw, RPawnVillain? Enemy);

    static Target? Pick()
    {
        if (!Present || _bot?.BotPawn is not { IsValid: true } follower || World.Controller is not { } controller || !Crosshair.View(out var eye, out var way))
        {
            return null;
        }

        var player = controller.Pawn is { } driven ? driven.Rotation.Yaw : 0f;

        // The sidekick or an enemy who is still up, by what the crosshair touches or nearly does.
        var picked = Crosshair.Pick(OrderReach, null, static kind => kind is Things.Kind.Enemy or Things.Kind.Friendly,
            actor => actor.Ptr == follower.Ptr || (actor is RPawnVillain { bHostile: true, bDeleteMe: false } enemy && !enemy.IsIncapacitated()), AimDegrees);
        if ((picked is not null && picked.Ptr == follower.Ptr) || Aimed(follower, eye, way))
        {
            return new Target(Aim.Back, Feet(follower), player, null);
        }

        if (picked is RPawnVillain chosen)
        {
            return new Target(Aim.Enemy, chosen.Location, player, chosen);
        }

        // Anything solid aimed at counts, a car's roof too, put down on the floor under it.
        // Aimed at nothing, or at a place with no floor, they hold where they stand.
        var yaw = Heading(new Vector3(way.X, way.Y, 0f), player);
        return Crosshair.Surface(OrderReach, out var hit, Crosshair.Meets.Solid, follower)
            && Teleport.Floor(follower, hit.Spot, hit.Spot.Z + 150f, hit.Spot.Z - 600f, out var floor)
            ? new Target(Aim.Spot, floor, yaw, null)
            : new Target(Aim.Stay, Feet(follower), yaw, null);
    }

    static Vector3 Feet(RPawnPlayer body) => body.Location - Vector3.UnitZ * body.GetCollisionHeight();

    // The first press shows where the order would go. The second gives it.
    static Outcome Issue()
    {
        if (!Aiming)
        {
            if (!Present)
            {
                return Outcome.NoPlayer;
            }

            _aiming = true;
            _aimSince = World.Now;
            return Outcome.Done("LEFT CLICK SENDS, RIGHT CLICK CANCELS");
        }

        _aiming = false;
        if (Pick() is not { } target || _bot is not { } bot)
        {
            return Outcome.NoPlayer;
        }

        switch (target.Kind)
        {
            case Aim.Back:
                return Recalled();
            case Aim.Enemy:
                ClearHold();
                Pursue(bot, target.Enemy!);
                return Outcome.Done("GOING AFTER IT");
            default:
                Stand(target.At, target.Yaw);
                if (bot.BotPawn is { IsValid: true } walker)
                {
                    Walk(bot, walker);
                }

                return Outcome.Done("HOLDING");
        }
    }

    // In order mode this only leaves it. Otherwise the sidekick comes back.
    static Outcome CallBack()
    {
        if (Aiming)
        {
            _aiming = false;
            return Outcome.Unchanged("ORDER CANCELLED");
        }

        return Recalled();
    }

    // How close to the crosshair's line the sidekick has to be, when the crosshair's own pick
    // misses them. About 10 degrees, which is a body's width from across a street.
    const float AimCone = 0.985f;

    static bool OnLine(Vector3 eye, Vector3 way, Vector3 at, out float distance)
    {
        var to = at - eye;
        distance = to.Length();
        return distance > 1f && distance <= OrderReach && Vector3.Dot(to / distance, way) >= AimCone;
    }

    static bool Aimed(RPawnPlayer follower, Vector3 eye, Vector3 way) => OnLine(eye, way, follower.Location, out _);

    static void Stand(Vector3 at, float yaw)
    {
        _hold = at;
        _holdYaw = yaw;
        _focus = null;
        _arrived = false;
        _walkedAt = -100f;
    }

    // Further than this they run flat out. Nearer, the game's own pace for its partners walks
    // them in and speeds up with the distance left.
    const float RunFrom = Metre * 8f;

    static float _walkedAt = -100f;

    // The story's scripted move, which paths round what is in the way. Told to wait at the goal,
    // it stops there facing the way it was given and never reports to the story's move object,
    // which ours doesn't have. It measures arrival from the body's middle, so the goal is raised
    // off the floor by the body's height.
    static void Walk(RRobinBotController bot, RPawnPlayer follower)
    {
        var at = _hold!.Value;
        bot.ClearNavigation();
        bot.bInKismetMove = true;
        bot.KismetFails = 0;
        bot.bForceRun = Flat(at, follower.Location) > RunFrom * RunFrom;
        bot.KismetGoal = at + Vector3.UnitZ * follower.GetCollisionHeight();
        bot.KismetGoalDir = new Rotator(0f, _holdYaw, 0f).ToDirection();
        bot.bMoveInitialised = false;
        bot.bKismetWaitAtGoal = true;
        bot.TimeTillMovementCheck = 0f;
        bot.StopAllLookAtPlayer();
        _walkedAt = World.Now;
        _arrived = false;
    }

    // The partner AI skips its walking while its body is changing pose, and then turns it to look
    // at the player, which is another pose change. With the player on the move that never ends,
    // so a held sidekick only set off once the player stood still. Holding, they look the way
    // they were ordered instead.
    [Redirect(typeof(RRobinBotController), nameof(RRobinBotController.UpdateLookAtPlayer))]
    static void UpdateLookAtPlayer(RRobinBotController self, float deltaTime)
    {
        try
        {
            if (Ordered && Ours(self))
            {
                return;
            }
        }
        catch (Exception error)
        {
            Log.Error("sidekick.look", "The sidekick's look rule failed", error);
        }

        Redirects.RunOriginal();
    }

    static Outcome Recalled()
    {
        ClearHold();
        if (_bot is { } bot)
        {
            ResetPath(bot);
        }

        return Outcome.Done("COMING BACK");
    }

    // Ending a hold also ends the scripted move, or after a swap it would walk the other body there.
    static void ClearHold()
    {
        var had = Ordered;
        _hold = null;
        _focus = null;
        _steering = false;
        _arrived = false;
        _walkedAt = -100f;
        if (had && _bot is { IsValid: true, bDeleteMe: false } bot)
        {
            ResetPath(bot);
        }
    }

    // Lets the partner AI find a path to wherever the player is now, rather than to the old goal.
    static void ResetPath(RRobinBotController bot)
    {
        bot.ClearNavigation();
        bot.LastMovePoint = new Vector3(0f, 0f, -1e9f);
        bot.TimeTillMovementCheck = 0f;
    }

    // Runs from the upkeep under orders. A target is kept while it is worth it, and once it isn't
    // they follow the player again. A spot is walked to on the scripted move, which keeps them
    // there and turned the right way.
    static void Hold(RPawnPlayer follower)
    {
        if (_focus is { } target)
        {
            if (!Keeps(target, follower))
            {
                DropFocus(follower);
            }

            return;
        }

        var at = _hold!.Value;
        if (Flat(at, follower.Location) <= HoldClose * HoldClose)
        {
            _arrived = true;
        }

        // The scripted move does the walking. It only needs starting, and starting again when the
        // game gives up on a path it can't find, which it does after five tries. A spot it can't
        // reach is reached out of sight, the way catch-up brings them to you.
        var bot = _bot!;
        if (bot.bInKismetMove || World.Controller is not { } controller || controller.IsInCombat(false))
        {
            return;
        }

        if (_walkedAt >= 0f && !_arrived && controller.Pawn is RPawnPlayer player)
        {
            controller.GetPlayerViewPoint(out var eye, out var view);
            var body = at + Vector3.UnitZ * follower.GetCollisionHeight();
            if (!InView(player, eye, view, body) && !InView(player, eye, view, follower.Location))
            {
                Place(follower, at, _holdYaw);
            }
        }

        Walk(bot, follower);
    }

    // A target is let go when it is down or gone, out of the fight, too far from the player, after
    // a while with no hit landed and no ground gained, or after long enough whatever happens. So
    // they never chase one enemy across the city or stand stuck behind a car.
    const float FocusMost = 30f;
    const float FocusStall = 6f;
    const float FocusLeash = Metre * 30f;
    const float FocusGain = Metre * 1f;

    static float _focusSince;
    static float _focusProgressAt;
    static float _focusNearest;
    static int _focusHealth;

    static void Pursue(RRobinBotController bot, RPawnVillain enemy)
    {
        ResetPath(bot);
        bot.StopAllLookAtPlayer();
        _focus = enemy;
        _focusSince = World.Now;
        _focusProgressAt = World.Now;
        _focusNearest = float.MaxValue;
        _focusHealth = enemy.Health;
        _searchedAt = -100f;
        _steering = false;
    }

    static bool Keeps(RPawnVillain target, RPawnPlayer follower)
    {
        if (!target.IsValid || target.bDeleteMe || !target.bHostile || target.IsIncapacitated() || World.Now < _focusSince
            || World.Now - _focusSince > FocusMost || World.Controller?.Pawn is not { } player
            || Flat(target.Location, player.Location) > FocusLeash * FocusLeash)
        {
            return false;
        }

        var gap = MathF.Sqrt(Flat(target.Location, follower.Location));
        if (target.Health < _focusHealth || gap < _focusNearest - FocusGain)
        {
            _focusHealth = Math.Min(_focusHealth, target.Health);
            _focusNearest = Math.Min(_focusNearest, gap);
            _focusProgressAt = World.Now;
        }

        return World.Now - _focusProgressAt <= FocusStall;
    }

    static void DropFocus(RPawnPlayer follower)
    {
        _focus = null;
        _steering = false;
        follower.StopMoveTo();
        if (_bot is { } bot)
        {
            ResetPath(bot);
        }
    }

    // The partner AI only paths when it follows, and never in a fight, so the way to a target is
    // searched here and steered every tick. A path is searched again once the last one is done,
    // or sooner when the target has moved off it.
    const float SearchEvery = 0.75f;
    const float SearchMoved = Metre * 1.5f;
    const float CloseIn = 0.6f;

    static float _searchedAt = -100f;
    static Vector3 _searchedFor;
    static bool _steering;
    static float _pacedAt = -100f;

    static void SteerFocus()
    {
        if (_focus is not { IsValid: true, bDeleteMe: false } target || _bot is not { } bot || bot.BotPawn is not { IsValid: true, bDeleteMe: false } follower
            || follower.CombatMove is not null || follower.CurrentSpecialMove is not null)
        {
            return;
        }

        if (Flat(target.Location, follower.Location) <= StrikeReach * StrikeReach * CloseIn)
        {
            if (_steering)
            {
                _steering = false;
                follower.StopMoveTo();
                follower.FaceAt(target.Location);
            }

            return;
        }

        var path = bot.NavHandle is { IsValid: true } handle ? handle : null;
        var done = path is null || path.PathFound() || path.PathNotFound();
        if (World.Now < _searchedAt || (done && World.Now - _searchedAt >= SearchEvery)
            || Vector3.DistanceSquared(target.Location, _searchedFor) > SearchMoved * SearchMoved)
        {
            bot.StartNavMeshSearch(target.Location);
            _searchedAt = World.Now;
            _searchedFor = target.Location;
            path = bot.NavHandle is { IsValid: true } fresh ? fresh : null;
        }

        Vector3 next;
        if (path is not null && path.PathFound() && path.GetNextMoveLocation(out var step, PathArrive))
        {
            next = step;
        }
        else if (path is not null && path.PathNotFound())
        {
            next = target.Location;
        }
        else
        {
            return;
        }

        // A speed set while the body blends between gaits is lost, so it is asked again now and then.
        if ((!_steering || World.Now - _pacedAt > 1f || World.Now < _pacedAt) && !follower.IsInAnyAutomaticTransition(default, out _))
        {
            follower.SetMovementSpeed(Pace.MS_Run, 1f, default);
            _pacedAt = World.Now;
        }

        follower.MoveInDirection(next - follower.Location);
        _steering = true;
    }

    // A strike at an ordered target, if it is in reach. The game's ground strike when it is down.
    static bool Focused(RRobinBotController bot, RPawnPlayerCombat body, RPawnVillain target)
    {
        if (!target.IsValid || target.bDeleteMe || target.IsIncapacitated() || Flat(target.Location, body.Location) > StrikeReach * StrikeReach)
        {
            return false;
        }

        bot.TryMoveTime = 1f;
        body.StopMoveTo();
        body.StopLookAt();
        body.StopFaceAt();
        body.StopAimAt();
        if (target.IsFloorStrikeable())
        {
            body.SpawnStrike(RCombatMove_BatmanHeavyStrike.StaticClass(), RDmgType_StrikeFloor.StaticClass(), true, target, 0f);
            bot.LastFloorStrikeTime = World.Now;
            return true;
        }

        return body.TryStrike(0f, target);
    }

    const float RingRadius = 55f;
    const int RingSides = 28;
    const int Dashes = 14;
    const float TagText = 8f;

    static readonly Rgba OrderInk = new(8, 10, 14);
    static readonly Rgba OrderWhite = new(255, 255, 255);
    static readonly Rgba OrderEnemy = new(255, 178, 70);

    public static bool ShowingOrders => Aiming || (Ordered && Present);

    // Drawn by the editor's mark pass, flat on the screen once the hologram's framing is off. In
    // order mode it shows what the order would do. A held spot keeps a faint ring.
    public static void RenderOrders(float time)
    {
        if (!ShowingOrders)
        {
            return;
        }

        Hologram.Pop();
        Draw.Place(0f, 0f, 1f);
        Draw.Fade(1f);
        var accent = Knightfall.Look.AccentOr(Theme.Current.Accent);
        if (_hold is { } held)
        {
            Ring(held, accent with { A = 90 }, 1f);
        }

        if (_focus is { IsValid: true, bDeleteMe: false } chased)
        {
            Brackets(chased, Height(chased), OrderEnemy with { A = 120 }, 1f);
        }

        if (!Aiming || Pick() is not { } target || _bot?.BotPawn is not { IsValid: true } follower)
        {
            return;
        }

        var pulse = 0.85f + 0.15f * MathF.Sin(time * 6f);
        var feet = Feet(follower);
        switch (target.Kind)
        {
            case Aim.Enemy when target.Enemy is { } enemy:
                var height = Height(enemy);
                Brackets(enemy, height, OrderEnemy, pulse);
                Path(feet, enemy.Location - Vector3.UnitZ * height, OrderEnemy with { A = 150 }, time);
                Tag(enemy.Location + Vector3.UnitZ * (height + 20f), "TAKE DOWN", Metres(Vector3.Distance(feet, enemy.Location)), OrderEnemy);
                break;
            case Aim.Back:
                Tag(follower.Location + Vector3.UnitZ * (follower.GetCollisionHeight() + 20f), "COME BACK", "TO YOU", accent);
                break;
            case Aim.Stay:
                Ring(target.At, accent, pulse);
                Tag(target.At + Vector3.UnitZ * 40f, "HOLD THERE", "WHERE THEY STAND", accent);
                break;
            default:
                Ring(target.At, accent, pulse);
                Path(feet, target.At, accent with { A = 150 }, time);
                Tag(target.At + Vector3.UnitZ * 40f, "HOLD HERE", Metres(Vector3.Distance(feet, target.At)), accent);
                break;
        }
    }

    static float Height(Pawn pawn) => pawn.CylinderComponent is { IsValid: true } shape ? shape.CollisionHeight : 90f;

    static string Metres(float units) => $"{MathF.Round(units / Metre):0} M";

    // A ring lying on the floor, a little above it so the ground doesn't cut it.
    static void Ring(Vector3 at, Rgba color, float scale)
    {
        var last = Vector2.Zero;
        var had = false;
        for (var i = 0; i <= RingSides; i++)
        {
            var angle = i * MathF.Tau / RingSides;
            var point = at + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f) * (RingRadius * scale) + Vector3.UnitZ * 3f;
            var seen = Hologram.Screen(point, out var pixels);
            var here = pixels / Draw.Scale;
            if (seen && had)
            {
                Draw.Line(last.X, last.Y, here.X, here.Y, color);
            }

            last = here;
            had = seen;
        }
    }

    // Dashes from the sidekick to where they would go, marching toward it.
    static void Path(Vector3 from, Vector3 to, Rgba color, float time)
    {
        var lift = Vector3.UnitZ * 3f;
        var shift = time * 2f % 1f;
        for (var i = 0; i < Dashes; i++)
        {
            var a = (i + shift) / Dashes;
            var b = MathF.Min((i + shift + 0.5f) / Dashes, 1f);
            if (a >= 1f || !Hologram.Screen(Vector3.Lerp(from, to, a) + lift, out var p) || !Hologram.Screen(Vector3.Lerp(from, to, b) + lift, out var q))
            {
                continue;
            }

            Draw.Line(p.X / Draw.Scale, p.Y / Draw.Scale, q.X / Draw.Scale, q.Y / Draw.Scale, color);
        }
    }

    // Corner brackets round an enemy, sized to them on screen.
    static void Brackets(RPawnVillain enemy, float height, Rgba color, float pulse)
    {
        if (!Hologram.Screen(enemy.Location + Vector3.UnitZ * height, out var top) || !Hologram.Screen(enemy.Location - Vector3.UnitZ * height, out var bottom))
        {
            return;
        }

        top /= Draw.Scale;
        bottom /= Draw.Scale;
        var tall = MathF.Max(bottom.Y - top.Y, 12f) * (0.95f + 0.1f * pulse);
        var x = (top.X + bottom.X) / 2f;
        var y = (top.Y + bottom.Y) / 2f;
        var across = tall * 0.32f;
        var down = tall / 2f + 4f;
        var arm = MathF.Max(across * 0.45f, 4f);

        void Corner(float cx, float cy, float dx, float dy)
        {
            Draw.Rect(dx > 0f ? cx : cx - arm, cy - 1f, arm, 2f, color);
            Draw.Rect(cx - 1f, dy > 0f ? cy : cy - arm, 2f, arm, color);
        }

        Corner(x - across, y - down, 1f, 1f);
        Corner(x + across, y - down, -1f, 1f);
        Corner(x - across, y + down, 1f, -1f);
        Corner(x + across, y + down, -1f, -1f);
    }

    // A small card over a point, with a pointer down to it.
    static void Tag(Vector3 at, string title, string note, Rgba color)
    {
        if (!Hologram.Screen(at, out var pixels))
        {
            return;
        }

        var spot = pixels / Draw.Scale;
        var width = MathF.Max(Draw.TrackedWidth(title, TagText, 1.5f), Draw.TextWidth(note, TagText)) + 12f;
        var top = spot.Y - 10f - Threats.TagHeight;
        var left = spot.X - width / 2f;
        Draw.Rect(left, top, width, Threats.TagHeight, OrderInk with { A = 170 });
        Draw.Rect(left, top, 2f, Threats.TagHeight, color);
        Draw.Tracked(title, left + 6f, top + 3f, TagText, 1.5f, OrderWhite);
        Draw.Text(note, left + 6f, top + 12f, TagText, color);
        Draw.Triangle(spot.X - 4f, spot.Y - 9f, spot.X + 4f, spot.Y - 9f, spot.X, spot.Y - 3f, color);
    }
}
