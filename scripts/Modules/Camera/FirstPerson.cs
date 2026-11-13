#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using View = BmSDK.GameObject.FTPOV;

namespace Knightfall;

// CAMERA > VIEW, FIRST PERSON. The game's own first person camera is only for crawlspaces, so the
// view is moved here instead and works whatever Batman is doing on foot. The Batmobile has its
// own bonnet camera. Scenes keep the game's camera.
static class FirstPerson
{
    static readonly FName Head = new("Bip01_Head");

    // Where the eyes are from the head bone, forward and up.
    const float EyeForward = 14f;
    const float EyeUp = 10f;

    // How quickly the view follows the head, per second. Enough to smooth the bob of a run
    // without lagging behind.
    const float Follow = 30f;

    static readonly List<(SkeletalMeshComponent Mesh, int Bone)> Hidden = new();
    static Vector3 _eye;
    static bool _following;
    static SkeletalMeshComponent? _askedMesh;
    static SkeletalMesh? _askedModel;
    static bool _meshHasHead;

    public static readonly Toggle OnFoot = new(
        "camera.view.first_person", "FIRST PERSON",
        "Puts the camera in Batman's eyes, walking, gliding and fighting. His head is hidden so it doesn't block the view. Scenes keep the game's camera.")
    {
        Tick = HideHead,
        Restore = ShowHead,
    };

    public static readonly Slider Eye = new(
        "camera.view.first_person_eye", "FIRST PERSON EYE",
        "Moves the first person view forward or back from Batman's eyes. Push it forward if bits of the cowl show at the edges.",
        min: -10, max: 30, step: 2, value: 0, unit: " CM", zero: "DEFAULT")
    {
        Preference = true,
    };

    static bool Allowed(RPlayerController controller) => !controller.bCinematicMode && !Switcher.InScene(controller);

    // True when the view will be put in the eyes, so the work on the place before it can be skipped.
    public static bool Replaces => OnFoot.On && World.Driving is null && World.Player?.Mesh is { IsValid: true } mesh && HasHead(mesh);

    // A mesh only has to be asked for its head bone once. A suit change can swap the model on
    // the same mesh, so the model counts too.
    static bool HasHead(SkeletalMeshComponent mesh)
    {
        var model = mesh.SkeletalMesh;
        if (!ReferenceEquals(mesh, _askedMesh) || !ReferenceEquals(model, _askedModel))
        {
            _askedMesh = mesh;
            _askedModel = model;
            _meshHasHead = mesh.MatchRefBone(Head) >= 0;
        }

        return _meshHasHead;
    }

    public static void Fit(ref View view, RPlayerController controller, float deltaTime)
    {
        // The free camera replaces the view after this anyway.
        if (!OnFoot.On || World.Driving is not null || FreeCamera.HasView || !Allowed(controller))
        {
            _following = false;
            return;
        }

        if (World.Player?.Mesh is not { IsValid: true } mesh || !HasHead(mesh))
        {
            _following = false;
            return;
        }

        var turn = controller.Rotation;
        var flat = new Rotator(0f, turn.Yaw, 0f).ToDirection();
        var at = mesh.GetBoneLocation(Head, 0) + flat * (EyeForward + (float)Eye.Value) + new Vector3(0f, 0f, EyeUp);
        if (!float.IsFinite(at.X) || !float.IsFinite(at.Y) || !float.IsFinite(at.Z))
        {
            return;
        }

        _eye = _following ? Vector3.Lerp(_eye, at, 1f - MathF.Exp(-Follow * MathF.Max(0f, deltaTime))) : at;
        _following = true;
        view.Location = _eye;
        // The roll is kept, so CAMERA TILT still leans it.
        view.Rotation = new Rotator(turn.Pitch, turn.Yaw, view.Rotation.Roll);
    }

    // The head is hidden on Batman and on any disguise worn over him, but only while the view is
    // actually in it.
    static void HideHead()
    {
        // The free camera sees Batman from outside, so he keeps his head then.
        var want = World.Controller is { } controller && Allowed(controller) && World.Driving is null && World.Player is not null && !FreeCamera.Free.On;
        if (!want)
        {
            ShowHead();
            return;
        }

        // A body that's no longer worn, after a suit or disguise change, gets its head back.
        var body = World.Player!.Mesh;
        var worn = Disguise.WornBody;
        for (var i = Hidden.Count - 1; i >= 0; i--)
        {
            var (mesh, bone) = Hidden[i];
            if (mesh.IsValid && (ReferenceEquals(mesh, body) || ReferenceEquals(mesh, worn)))
            {
                continue;
            }

            if (mesh.IsValid)
            {
                mesh.UnHideBone(bone);
            }

            Hidden.RemoveAt(i);
        }

        Keep(body);
        Keep(worn);
    }

    static void Keep(SkeletalMeshComponent? mesh)
    {
        if (mesh is not { IsValid: true })
        {
            return;
        }

        if (Hidden.Exists(entry => ReferenceEquals(entry.Mesh, mesh)))
        {
            return;
        }

        var bone = mesh.MatchRefBone(Head);
        if (bone < 0)
        {
            return;
        }

        // One the game hid itself is left to the game.
        if (mesh.IsBoneHidden(bone))
        {
            return;
        }

        mesh.HideBone(bone, SkeletalMeshComponent.EPhysBodyOp.PBO_None);
        Hidden.Add((mesh, bone));
    }

    static void ShowHead()
    {
        foreach (var (mesh, bone) in Hidden)
        {
            if (mesh.IsValid)
            {
                mesh.UnHideBone(bone);
            }
        }

        Hidden.Clear();
    }
}
