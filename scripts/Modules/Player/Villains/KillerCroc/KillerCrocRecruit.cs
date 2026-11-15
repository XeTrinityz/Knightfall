#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using ParentMode = BmSDK.Engine.RSkeletalMeshComponent_Export.EParentAnimComponentMode;

namespace Knightfall;

// His revive from his fight, where he hauls a knocked out ally up off the floor by the scruff.
// Here the one he hauls up gets up fighting for him, as a bodyguard.
//
// The thug's half of the haul is made to be played in step with Croc's, held in his claw. The
// game only does that for a pawn it animates itself, so while Croc hauls, the real thug is hidden
// and a stand-in with his body and clothes plays his half in Croc's place, frame for frame. The
// real thug gets up out of sight meanwhile, and takes the stand-in's place once he is on his feet.
static partial class KillerCroc
{
    // The game tells his two hauls apart by which way up the thug lies. Both are the same move
    // for Croc.
    static readonly FName HaulOnBack = new("Pick_Up_Thug_Back");
    static readonly FName HaulOnFront = new("Pick_Up_Thug_Left_Up");
    static readonly FName Pelvis = new("Bip01_Pelvis");
    static readonly FName HeadBone = new("Bip01_Head");

    // Where the thug lies in the haul, from Croc's feet, and which way his head points from
    // there, in Croc's own mesh space. Croc lines up with the body as it lies, so the stand-in
    // starts just as the real thug lies.
    static readonly Vector2 HaulMiddle = new(134f, 218f);
    static readonly Vector2 HeadOnFront = new(46f, -53f);
    static readonly Vector2 HeadOnBack = new(-60f, 33f);

    // How far he goes to reach someone, how he walks up, and how long he may take about it.
    const float RecruitReach = 600f;
    const float WalkUpSpeed = 250f;
    const float WalkUpSeconds = 4f;
    const float Arrived = 20f;
    const float CloseEnough = 120f;
    const float LinedUp = 8f;

    // A step that gets less than this share of the way is blocked, and he tries this far to
    // either side, then twice that.
    const float Blocked = 0.3f;
    static float _detour = 45f;

    // When Croc has stood back up from the haul and when the thug is on his feet in it, after
    // which each only holds still. The real thug gets this long past that to finish getting up.
    const float HaulSeconds = 1.45f;
    const float StoodAt = 1.75f;
    const float GetUpGrace = 1.5f;

    static readonly Held<PrimitiveComponent, bool> HauledHidden = new("killer croc hauled thug", part => part.HiddenGame, (part, hidden) => part.SetHidden(hidden));

    static RPawnVillain? _hauled;
    static FName _haulName;
    static Vector3 _haulTo;
    static float _haulYaw;
    static float _haulMeshYaw;
    static bool _walkingUp;
    static float _walkSpeedNow;
    static float _walkUntil;

    static SkeletalMeshComponent? _standIn;
    static AnimNodeSequence? _standInMove;
    static readonly List<SkeletalMeshComponent> StandInParts = new();
    static readonly List<PrimitiveComponent> HauledParts = new();
    static float _standInTime;
    static float _revealBy;

    // While he walks up to the thug he moves on his own legs, not in a move's pose.
    static bool Posing => Acting && !(_move == Move.Recruit && _walkingUp);

    static Outcome Recruit()
    {
        if (!Option.On || _body is not { IsValid: true } || World.Player is not { IsValid: true } player || player.Mesh is not { IsValid: true } own
            || World.Controller is not { } controller)
        {
            return Outcome.Unchanged("CROC IS OFF");
        }

        if (Acting || _standIn is not null || player.Physics != Actor.EPhysics.PHYS_Walking || player.CurrentSpecialMove is { IsValid: true } || controller.bCinematicMode)
        {
            return Outcome.Unchanged("NOT NOW");
        }

        if (_thugMoves is not { IsValid: true })
        {
            return Outcome.Failed("NOT READY");
        }

        if (Bodyguards.Room == 0)
        {
            return Outcome.Failed("TEAM IS FULL");
        }

        if (Downed(player) is not { } thug || thug.Mesh is not { IsValid: true } skin || skin.MatchRefBone(Pelvis) < 0 || skin.MatchRefBone(HeadBone) < 0)
        {
            return Outcome.Unchanged("NO ONE DOWN");
        }

        // The haul's frame is turned so the body in it lies the way the real one does, and Croc
        // stands where that puts his feet.
        var middle = skin.GetBoneLocation(Pelvis, 0);
        var lying = Level(skin.GetBoneLocation(HeadBone, 0) - middle);
        var onBack = thug.Anim is { IsValid: true } pose && pose.GetRagdollFacing() > 0f;
        var head = onBack ? HeadOnBack : HeadOnFront;
        _haulMeshYaw = Degrees(lying.Y, lying.X) - Degrees(head.Y, head.X);
        var feet = Level(middle) - Turned(new Vector3(HaulMiddle, 0f), _haulMeshYaw);

        // Lying along a wall or a car, that spot can be in it. Then he stands on the open side, toward
        // where he is now, and the body turns a little as the haul starts.
        if (Walled(player, middle, feet))
        {
            var open = Level(player.Location - middle);
            _haulMeshYaw = Degrees(open.Y, open.X) - Degrees(-HaulMiddle.Y, -HaulMiddle.X);
            feet = Level(middle) - Turned(new Vector3(HaulMiddle, 0f), _haulMeshYaw);
            if (Walled(player, middle, feet))
            {
                return Outcome.Unchanged("NO ROOM");
            }
        }

        _haulTo = new Vector3(feet.X, feet.Y, player.Location.Z);
        _haulYaw = _haulMeshYaw - own.Rotation.Yaw - _turn;
        if (Level(_haulTo - player.Location).Length() > RecruitReach + HaulMiddle.Length())
        {
            return Outcome.Unchanged("GET CLOSER");
        }

        _haulName = onBack ? HaulOnBack : HaulOnFront;
        _hauled = thug;
        _move = Move.Recruit;
        _walkingUp = true;
        _walkUntil = World.Now + WalkUpSeconds;
        _base = player.Rotation.Yaw;
        _aim = _base;
        Hold(controller);
        return Outcome.Done("RECRUIT");
    }

    // The nearest enemy knocked out near him, who isn't already his.
    static RPawnVillain? Downed(RPawnPlayer player)
    {
        RPawnVillain? best = null;
        var bestDistance = RecruitReach;
        foreach (var thug in Game.FindObjects<RPawnVillainThug>())
        {
            if (thug is not { IsValid: true, IsClassDefaultObject: false, bHidden: false, bPooledPawn_Inactive: false } || thug.Health > 0
                || thug.DrivenVehicle is { IsValid: true } || thug.Base is Vehicle || Bodyguards.Is(thug))
            {
                continue;
            }

            var distance = Level(thug.Location - player.Location).Length();
            if (distance < bestDistance && MathF.Abs(thug.Location.Z - player.Location.Z) < SwipeHeight)
            {
                best = thug;
                bestDistance = distance;
            }
        }

        return best;
    }

    // Each tick of the recruit while Croc is busy with it. He walks up and turns to the body,
    // then hauls. His move ends once he has stood back up.
    static bool Recruiting(RPawnPlayer player, AnimNodeSequence played)
    {
        if (_hauled is not { IsAlive: true })
        {
            Unact();
            return true;
        }

        if (_walkingUp)
        {
            WalkUp(player);
            return true;
        }

        if (played.CurrentTime >= HaulSeconds)
        {
            Unact();
        }

        return true;
    }

    static void WalkUp(RPawnPlayer player)
    {
        var to = Level(_haulTo - player.Location);
        var distance = to.Length();
        if (distance > Arrived)
        {
            _walkSpeedNow = WalkUpSpeed;
            _aim = Degrees(to.Y, to.X) + WalkAround(player, to / distance, MathF.Min(distance, WalkUpSpeed * World.Delta));
        }
        else
        {
            _aim = _haulYaw;
            _walkSpeedNow = 0f;
        }

        var lined = distance <= Arrived && MathF.Abs(Wrap(_haulYaw - _base)) <= LinedUp;
        var late = World.Now >= _walkUntil;
        if (!lined && !late)
        {
            return;
        }

        _walkingUp = false;
        _walkSpeedNow = 0f;
        if (late && distance > CloseEnough)
        {
            Log.Info("Killer Croc couldn't get to the thug he went to haul up.");
            Unact();
            return;
        }

        StartHaul(player);
    }

    // A bodyguard or a car in the way stops a straight walk, so he steps round it, trying the
    // side that worked last first so he keeps going round one way. Returns how far off straight
    // he went, to face that way.
    static float WalkAround(RPawnPlayer player, Vector3 way, float step)
    {
        if (Villains.Stride(player, way * step) >= step * Blocked)
        {
            return 0f;
        }

        foreach (var turn in new[] { _detour, -_detour, _detour * 2f, -_detour * 2f })
        {
            if (Villains.Stride(player, Turned(way, turn) * step) >= step * Blocked)
            {
                _detour = turn is > 0f ? MathF.Abs(_detour) : -MathF.Abs(_detour);
                return turn;
            }
        }

        return 0f;
    }

    static void StartHaul(RPawnPlayer player)
    {
        if (_hauled is not { IsAlive: true } thug || player.Mesh is not { IsValid: true } own || Play(_haulName, _haulYaw) is null)
        {
            Unact();
            return;
        }

        _base = _haulYaw;
        _aim = _haulYaw;
        player.SetRotation(new Rotator(0f, _haulYaw, 0f));
        if (!BuildStandIn(player, own, thug))
        {
            Unact();
            return;
        }

        // He has to be on his feet as far as the game is concerned, or the bodyguards would wake
        // him again their own way.
        HideHauled(thug);
        thug.Health = Math.Max(1, thug.HealthMax);
        thug.WakeFromDeadWithDefaults();
        Speech.Speak("revive", always: true);

        // He joins as the haul starts. Waking makes him an enemy again, and the other bodyguards
        // would go for him while he is still getting up out of sight.
        if (!Bodyguards.Win(thug))
        {
            Log.Info("Killer Croc hauled a thug up, but the bodyguard team was full.");
        }

        _standInTime = 0f;
        _revealBy = -1f;
    }

    // A copy of the thug's body playing his half of the haul, with his clothes following it,
    // fixed where the haul's frame is in the world.
    static bool BuildStandIn(RPawnPlayer player, SkeletalMeshComponent own, RPawnVillain thug)
    {
        if (thug.Mesh is not { IsValid: true, SkeletalMesh: { IsValid: true } } skin || _thugMoves is not { IsValid: true } moves)
        {
            return false;
        }

        var at = new Vector3(_haulTo.X, _haulTo.Y, player.Location.Z + own.Translation.Z);
        var turn = new Rotator(0f, _haulMeshYaw, 0f);
        var body = StandInPiece(player, skin, at, turn);
        body.AnimSets.Add(moves);
        var move = Made.Tracked(new AnimNodeSequence(body));
        move.bNoNotifies = true;
        body.Animations = move;
        body.bUpdateSkelWhenNotRendered = true;
        player.AttachComponent(body);
        move.SetAnim(_haulName);
        if (move.AnimSeq is not { IsValid: true })
        {
            player.DetachComponent(body);
            return false;
        }

        move.SetPosition(0f, false);
        _standIn = body;
        _standInMove = move;
        Ours.Add(body.Ptr);

        var components = thug.Components;
        for (var i = 0; i < components.Count; i++)
        {
            if (components[i] is not SkeletalMeshComponent { IsValid: true, HiddenGame: false, SkeletalMesh: { IsValid: true } } part || part.Ptr == skin.Ptr
                || part.ParentAnimComponent is not { } parent || parent.Ptr != skin.Ptr)
            {
                continue;
            }

            var piece = StandInPiece(player, part, at, turn);
            piece.ParentAnimComponentMode = Disguise.Unshared(piece, body) ? ParentMode.PACM_FillIn : part.ParentAnimComponentMode;
            piece.bTransformFromAnimParent = 0;
            piece.SetParentAnimComponent(body);
            player.AttachComponent(piece);
            StandInParts.Add(piece);
            Ours.Add(piece.Ptr);
        }

        return true;
    }

    static SkeletalMeshComponent StandInPiece(RPawnPlayer player, SkeletalMeshComponent source, Vector3 at, Rotator turn)
    {
        var piece = Made.Tracked(new SkeletalMeshComponent(player));
        piece.SetActorCollision(false, false);
        piece.SetSkeletalMesh(source.SkeletalMesh!, false);
        var painted = source.Materials;
        var slots = Math.Min(painted.Count, piece.GetNumElements());
        for (var i = 0; i < slots; i++)
        {
            if (painted[i] is { IsValid: true } material)
            {
                piece.SetMaterial(i, material);
            }
        }

        piece.SetTickGroup(Shown);
        piece.bCastDynamicShadow = source.bCastDynamicShadow;
        piece.SetLightingChannels(source.LightingChannels);
        piece.SetDepthPriorityGroup(source.DepthPriorityGroup);
        piece.SetAbsolute(true, true, false);
        piece.SetTranslation(at);
        piece.SetRotation(turn);
        piece.SetScale(source.Scale);
        piece.SetScale3D(source.Scale3D);
        return piece;
    }

    // Every tick while there is a stand-in. It keeps step with Croc's haul, then holds the
    // thug's last pose until the real one is up, who then takes its place.
    static void KeepHauled(RPawnPlayer player)
    {
        if (_standIn is null)
        {
            return;
        }

        if (_hauled is not { IsAlive: true } thug || _standIn is not { IsValid: true } body || _standInMove is not { IsValid: true } move)
        {
            DropHaul();
            return;
        }

        HideHauled(thug);
        if (_move == Move.Recruit && Played[_slot] is { IsValid: true } haul && haul.AnimSeq is { IsValid: true } seq && seq.SequenceName == _haulName)
        {
            _standInTime = haul.CurrentTime;
        }
        else
        {
            // Croc stands up before the thug has his feet, so the stand-in plays on by itself.
            _standInTime += World.Delta;
        }

        move.SetPosition(MathF.Min(_standInTime, move.GetAnimPlaybackLength()), false);
        if (_standInTime < StoodAt)
        {
            return;
        }

        if (_revealBy < 0f)
        {
            _revealBy = World.Now + GetUpGrace;
        }

        var state = thug.GetStateName().ToString() ?? "";
        var stillDown = thug.IsRagdoll() || state.Contains("GetUp", StringComparison.OrdinalIgnoreCase) || state.Contains("GettingUp", StringComparison.OrdinalIgnoreCase);
        if (stillDown && World.Now < _revealBy)
        {
            return;
        }

        // He stands where the stand-in stands, facing Croc.
        var middle = body.MatchRefBone(Pelvis) >= 0 ? body.GetBoneLocation(Pelvis, 0) : body.Translation;
        thug.SetLocation(new Vector3(middle.X, middle.Y, thug.Location.Z));
        var toCroc = Level(player.Location - thug.Location);
        if (toCroc.LengthSquared() > 1f)
        {
            thug.SetRotation(new Rotator(0f, Degrees(toCroc.Y, toCroc.X), 0f));
        }

        DropHaul();
    }

    // Hides the real thug, all of him, while the stand-in plays. The game shows parts again as
    // it gets him up, so this runs every tick.
    static void HideHauled(RPawnVillain thug)
    {
        HauledParts.Clear();
        var components = thug.Components;
        for (var i = 0; i < components.Count; i++)
        {
            if (components[i] is PrimitiveComponent { IsValid: true } part && part is MeshComponent)
            {
                HauledParts.Add(part);
                HauledHidden.Hold(part, true);
            }
        }
    }

    // The stand-in goes and the real thug is shown again.
    static void DropHaul()
    {
        HauledHidden.Release();
        HauledParts.Clear();
        if (_on is { IsAlive: true } owner)
        {
            foreach (var piece in StandInParts)
            {
                Detach(owner, piece);
            }

            Detach(owner, _standIn);
        }

        StandInParts.Clear();
        _standIn = null;
        _standInMove = null;
        _hauled = null;
        _revealBy = -1f;
    }

    static float Degrees(float y, float x) => MathF.Atan2(y, x) * (180f / MathF.PI);

    // Whether a wall or a vehicle stands between the thug and where Croc's feet would go, at his
    // waist and at his chest. A step he can climb is lower than both.
    static bool Walled(RPawnPlayer player, Vector3 middle, Vector3 feet)
    {
        foreach (var up in new[] { 0f, ChestHeight * 2f })
        {
            var z = player.Location.Z + up;
            if (Crosshair.Line(player, new Vector3(middle.X, middle.Y, z), new Vector3(feet.X, feet.Y, z), Crosshair.Meets.Solid, out _))
            {
                return true;
            }
        }

        return false;
    }
}
