#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using View = BmSDK.GameObject.FTPOV;

namespace Knightfall;

// PLAYER > CHARACTER, the two DISGUISE rows that fit the game to the worn body. The game still
// frames and dresses Batman's hidden body, so both are moved over to the worn one.
static class DisguiseFit
{
    // How far the camera may be pulled back for a bigger body, and pushed in for a smaller one.
    const float Nearest = 0.5f;
    const float Furthest = 1.4f;

    // How quickly the camera eases over when the fit starts or stops, and follows a change of
    // height, per second.
    const float Ease = 6f;
    const float Settle = 3f;

    // Further apart than this, the two heads aren't on the same person and nothing is moved.
    const float Reach = 200f;

    static readonly FName Head = new("Bip01_Head");
    static readonly FName Neck = new("Bip01_Neck");

    public static readonly Toggle Camera = new(
        "player.character.disguise_camera", "FIT CAMERA",
        "Moves the camera to the worn body's head. A small character gets a lower, closer camera and a big one a higher, further one. Not in the Batmobile or in scenes.",
        on: true);

    public static readonly Toggle Gadgets = new(
        "player.character.disguise_gadgets", "FIT GADGETS",
        "Puts the gadgets you hold and carry on the worn body's hands and belt, not where Batman's would be.",
        on: true);

    static float _weight;
    static float _ratio = 1f;
    static float _ratioNow = float.NaN;
    static float _lift = float.NaN;
    static float _wantRatio = 1f;
    static float _wantLift;

    // Called from the camera hook with the view the game worked out.
    public static void Fit(ref View view, RPlayerController controller, float deltaTime)
    {
        var allowed = Camera.On && World.Driving is null && !Switcher.InScene(controller);
        var player = World.Player;
        var own = player?.Mesh is { IsValid: true } mesh ? mesh : null;
        var live = allowed && own is not null && Disguise.WornBody is { } body && Measure(player!, own, body);

        var step = 1f - MathF.Exp(-Ease * Math.Max(0f, deltaTime));
        _weight = !allowed ? 0f : _weight + ((live ? 1f : 0f) - _weight) * step;
        if (live)
        {
            // Only the height is taken, and eased, so the camera doesn't bob with the worn head.
            var settle = 1f - MathF.Exp(-Settle * Math.Max(0f, deltaTime));
            _lift = float.IsNaN(_lift) ? _wantLift : _lift + (_wantLift - _lift) * settle;
            _ratio = float.IsNaN(_ratioNow) ? _wantRatio : _ratio + (_wantRatio - _ratio) * settle;
            _ratioNow = _ratio;
        }
        if (_weight < 0.001f || own is null || own.MatchRefBone(Head) < 0)
        {
            _weight = 0f;
            return;
        }

        var pivot = own.GetBoneLocation(Head, 0);
        if (!Finite(pivot))
        {
            return;
        }

        var scale = 1f + (_ratio - 1f) * _weight;
        view.Location = pivot + new Vector3(0f, 0f, _lift * _weight) + (view.Location - pivot) * scale;
    }

    // Where the worn body's head is next to the player's own, and how tall it stands.
    static bool Measure(RPawnPlayer player, SkeletalMeshComponent own, SkeletalMeshComponent body)
    {
        var bone = body.MatchRefBone(Head) >= 0 ? Head : body.MatchRefBone(Neck) >= 0 && own.MatchRefBone(Neck) >= 0 ? Neck : default;
        if (bone.Index == 0 || own.MatchRefBone(bone) < 0)
        {
            return false;
        }

        var mine = own.GetBoneLocation(bone, 0);
        var worn = body.GetBoneLocation(bone, 0);
        if (!Finite(mine) || !Finite(worn) || Vector3.Distance(mine, worn) > Reach)
        {
            return false;
        }

        var feet = player.Location.Z - (player.CylinderComponent is { IsValid: true } cylinder ? cylinder.CollisionHeight : 0f);
        var myHeight = mine.Z - feet;
        var wornHeight = worn.Z - feet;
        if (myHeight < 20f || wornHeight < 5f)
        {
            return false;
        }

        _wantLift = worn.Z - mine.Z;
        _wantRatio = Math.Clamp(wornHeight / myHeight, Nearest, Furthest);
        return true;
    }

    // The game hangs gadgets on Batman's hidden bones. Each one keeps hanging from the player's
    // mesh, so the game's own attach and detach still work, but its offset is changed so it lands
    // on the same bone of the worn body. A bone the worn body hasn't got is taken from the nearest
    // one above it that it has.
    sealed class Moved
    {
        public required int Bone;
        public required FName Anchor;
        public required Vector3 GameAt;
        public required Rotator GameTurn;
        public Vector3 OurAt;
        public Rotator OurTurn;
    }

    static readonly Dictionary<nint, Moved> Fits = new();
    static readonly HashSet<nint> Seen = new();
    static SkeletalMeshComponent? _fitMesh;

    public static void Tick()
    {
        var own = World.Player?.Mesh is { IsValid: true } mesh ? mesh : null;
        if (!Gadgets.On || own is null || Disguise.WornBody is not { } body)
        {
            PutBackGadgets();
            return;
        }

        // A new body. The one before gets its gadgets back where the game had them.
        if (!ReferenceEquals(_fitMesh, own))
        {
            PutBackGadgets();
            _fitMesh = own;
        }

        Seen.Clear();
        var list = own.Attachments;
        for (var i = 0; i < list.Count; i++)
        {
            var entry = list[i];
            if (entry.Component is not PrimitiveComponent { IsValid: true, HiddenGame: false } held || entry.BoneName.Index == 0)
            {
                continue;
            }

            // Anything that isn't where we last put it was attached again by the game, so what
            // it has now is what the game wants.
            if (!Fits.TryGetValue(held.Ptr, out var fit) || fit.Bone != entry.BoneName.Index || !Near(entry.RelativeLocation, fit.OurAt) || !Near(entry.RelativeRotation, fit.OurTurn))
            {
                fit = new Moved
                {
                    Bone = entry.BoneName.Index,
                    Anchor = Shared(own, body, entry.BoneName),
                    GameAt = entry.RelativeLocation,
                    GameTurn = entry.RelativeRotation,
                    OurAt = entry.RelativeLocation,
                    OurTurn = entry.RelativeRotation,
                };
                Fits[held.Ptr] = fit;
            }

            Seen.Add(held.Ptr);
            if (fit.Anchor.Index == 0 || !Moving(own, body, entry.BoneName, fit, out var at, out var turn) || (Near(at, entry.RelativeLocation) && Near(turn, entry.RelativeRotation)))
            {
                continue;
            }

            entry.RelativeLocation = at;
            entry.RelativeRotation = turn;
            list[i] = entry;

            // Read back, so the rounding the game does to a turn still matches next tick.
            var written = list[i];
            fit.OurAt = written.RelativeLocation;
            fit.OurTurn = written.RelativeRotation;
        }

        if (Fits.Count > Seen.Count)
        {
            foreach (var gone in Fits.Keys.Where(key => !Seen.Contains(key)).ToList())
            {
                Fits.Remove(gone);
            }
        }
    }

    static bool Moving(SkeletalMeshComponent own, SkeletalMeshComponent body, FName bone, Moved fit, out Vector3 at, out Rotator turn)
    {
        Vector3 place;
        Rotator facing;
        if (fit.Anchor.Index == bone.Index)
        {
            body.TransformFromBoneSpace(bone, fit.GameAt, fit.GameTurn, out place, out facing);
        }
        else
        {
            own.TransformFromBoneSpace(bone, fit.GameAt, fit.GameTurn, out var wanted, out var wantedTurn);
            own.TransformToBoneSpace(fit.Anchor, wanted, wantedTurn, out var fromAnchor, out var fromAnchorTurn);
            body.TransformFromBoneSpace(fit.Anchor, fromAnchor, fromAnchorTurn, out place, out facing);
        }

        own.TransformToBoneSpace(bone, place, facing, out at, out turn);
        return Finite(at) && at.Length() < Reach;
    }

    static FName Shared(SkeletalMeshComponent own, SkeletalMeshComponent body, FName bone)
    {
        for (var steps = 0; steps < 64 && bone.Index != 0; steps++)
        {
            // The worn body keeps the bones a gun hangs from still, so a gadget goes by the hand.
            if (body.MatchRefBone(bone) >= 0 && !Disguise.Holds(bone))
            {
                return bone;
            }

            bone = own.GetParentBone(bone);
        }

        return default;
    }

    static void PutBackGadgets()
    {
        if (Fits.Count == 0)
        {
            return;
        }

        // The body may no longer be the player's, like a partner left behind in a team fight.
        if (_fitMesh is { IsValid: true, Owner: { IsValid: true, bDeleteMe: false } } own)
        {
            var list = own.Attachments;
            for (var i = 0; i < list.Count; i++)
            {
                var entry = list[i];
                if (entry.Component is not { IsValid: true } held || !Fits.TryGetValue(held.Ptr, out var fit) || fit.Bone != entry.BoneName.Index
                    || !Near(entry.RelativeLocation, fit.OurAt) || !Near(entry.RelativeRotation, fit.OurTurn))
                {
                    continue;
                }

                entry.RelativeLocation = fit.GameAt;
                entry.RelativeRotation = fit.GameTurn;
                list[i] = entry;
            }
        }

        Fits.Clear();
    }

    public static void Release()
    {
        PutBackGadgets();
        _fitMesh = null;

        _weight = 0f;
        _lift = float.NaN;
        _ratioNow = float.NaN;
    }

    static bool Near(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) < 0.0001f;

    static bool Near(Rotator a, Rotator b) => Turned(a.Pitch - b.Pitch) < 0.05f && Turned(a.Yaw - b.Yaw) < 0.05f && Turned(a.Roll - b.Roll) < 0.05f;

    static float Turned(float degrees)
    {
        degrees %= 360f;
        degrees = degrees < 0f ? degrees + 360f : degrees;
        return MathF.Min(degrees, 360f - degrees);
    }

    static bool Finite(Vector3 at) => float.IsFinite(at.X) && float.IsFinite(at.Y) && float.IsFinite(at.Z);
}
