#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using static Knightfall.Turns;
using Atom = BmSDK.GameObject.FBoneAtom;
using Turn = BmSDK.GameObject.FQuat;

namespace Knightfall;

// How the worn body is posed from the player's, on top of what the engine's follow mode gives.
static partial class Disguise
{
    const string Main = "Bip01";
    const string Dummy = "Gundummy";

    // Batman's collarbones sit at the angle his build needs. Copied onto a slimmer character they
    // dropped the shoulders, so these keep the worn body's own.
    const string Collarbone = "Clavicle";

    // Far enough from the player's height that the hips are not scaled past it.
    const float ShortestStride = 0.5f;
    const float LongestStride = 1.5f;

    // How many ticks the rest poses get to arrive before they are given up on.
    const int RestWait = 30;

    // A bone whose child lies further off this angle from where the player's does is built a
    // different way, like Killer Croc's, and taking the player's turn would bend it wrong.
    const float Unlike = 35f;

    // Kept for each look, as what suits one character breaks another.
    const string StanceSection = "player.character.disguise_pose";

    // Each look's POSE is only read when that look is worn, so they're all kept for saving.
    public static void KeepStances() => Settings.KeepAll(StanceSection);
    const string Yours = "yours";

    public static readonly Choice Stance = new(
        "player.character.disguise_stance", "POSE",
        "THEIR OWN keeps the character's stance, like Croc's hunch, but some hold their hands inside the body. LIKE YOURS copies your pose. Each look keeps its own.",
        new[] { ("own", "THEIR OWN"), (Yours, "LIKE YOURS") }, saved: false);

    static string _stanceOf = "";
    static int _stanceWas = -1;

    // For a feature whose look holds its hands inside the body in its own stance. The look starts
    // on LIKE YOURS unless a POSE was already picked for it.
    public static void PreferYourStance(string look)
    {
        if (Settings.Kept(StanceSection, look) is not null)
        {
            return;
        }

        Settings.Set(StanceSection, look, Yours);
        if (look.Equals(_stanceOf, StringComparison.OrdinalIgnoreCase))
        {
            _stanceOf = "";
        }
    }

    // Each main bone of the worn body, with the same bone on the player's mesh. The follow mode
    // keeps the worn body's own rest pose under the player's moves, which is their own stance.
    // With LIKE YOURS these bones take the player's turn exactly instead. They are made the same
    // way on most characters, so the turn means the same, and the ones that aren't are left out.
    //
    // Free is set on a bone whose parent doesn't take the player's turn, like an upper arm under
    // a collarbone. Its turn from the parent would point it somewhere else, so it is given the
    // player's turn against the whole body instead, and Chain is the bones above it.
    static (int Worn, int Mine, bool Free, int[] Chain)[] _turns = Array.Empty<(int, int, bool, int[])>();

    // The worn body's bones, their parents and the same bones on the player's mesh, kept until
    // the rest poses arrive to choose the turns from.
    static int[] _parents = Array.Empty<int>();
    static int[] _mineOf = Array.Empty<int>();
    static List<(int Worn, int Mine)> _maybe = new();

    // The bones a gun or sword hangs from. The player's own animations swing them about for
    // gadgets, so the worn body keeps them where its rest pose has them, in the hand.
    static int[] _holds = Array.Empty<int>();
    static readonly Dictionary<int, Atom> HoldAt = new();

    // The worn hips move by the player's hip moves scaled to the worn body's leg length, so a
    // small character doesn't drop through the ground in a roll.
    static int _hips = -1;
    static System.Numerics.Vector3 _hipsAt;
    static float _stride = 1f;

    // Hidden copies of the two meshes in their rest pose, read once and taken off again.
    static SkeletalMeshComponent? _wornRest;
    static SkeletalMeshComponent? _mineRest;
    static int _restWait;

    static void Shaping(RPawnPlayer player, SkeletalMeshComponent own, SkeletalMeshComponent body)
    {
        var maybe = new List<(int Worn, int Mine)>();
        var holds = new List<int>();
        var count = body.SkeletalMesh?.RefSkeleton.Count ?? 0;
        var parents = new int[count];
        var mineOf = new int[count];
        for (var i = 0; i < count; i++)
        {
            var bone = body.GetBoneName(i);
            var name = bone.ToString() ?? "";
            parents[i] = i == 0 ? -1 : body.MatchRefBone(body.GetParentBone(bone));
            mineOf[i] = own.MatchRefBone(bone);
            if (name.StartsWith(Dummy, StringComparison.OrdinalIgnoreCase))
            {
                holds.Add(i);
            }
            else if (name.StartsWith(Main, StringComparison.OrdinalIgnoreCase) && !name.Contains(Collarbone, StringComparison.OrdinalIgnoreCase) && mineOf[i] >= 0)
            {
                maybe.Add((i, mineOf[i]));
            }
        }

        _turns = Array.Empty<(int, int, bool, int[])>();
        _parents = parents;
        _mineOf = mineOf;
        _maybe = maybe;
        _holds = holds.ToArray();
        _hips = body.MatchRefBone(new FName(Main));
        HoldAt.Clear();
        _stride = 1f;
        _wornRest = Rest(player, body.SkeletalMesh!);
        _mineRest = own.SkeletalMesh is { IsValid: true } mesh ? Rest(player, mesh) : null;
        _restWait = RestWait;
    }

    static SkeletalMeshComponent Rest(RPawnPlayer player, SkeletalMesh mesh)
    {
        var rest = Made.Tracked(new SkeletalMeshComponent(player));
        rest.SetActorCollision(false, false);
        rest.SetSkeletalMesh(mesh, false);
        rest.bUpdateSkelWhenNotRendered = true;
        rest.bCastDynamicShadow = false;
        rest.SetHidden(true);
        player.AttachComponent(rest);
        return rest;
    }

    // A mesh with nothing animating it stands in its rest pose once it has been updated.
    static void ReadRest(RPawnPlayer player, SkeletalMeshComponent own)
    {
        if (_wornRest is null)
        {
            return;
        }

        var worn = _wornRest.IsValid ? _wornRest.LocalAtoms : null;
        var mine = _mineRest is { IsValid: true } ? _mineRest.LocalAtoms : null;
        var ready = worn is { Count: > 0 } && mine is { Count: > 0 };
        if (!ready && --_restWait > 0)
        {
            return;
        }

        if (ready && worn is not null && mine is not null)
        {
            foreach (var hold in _holds)
            {
                if (hold < worn.Count)
                {
                    HoldAt[hold] = worn[hold];
                }
            }

            var theirs = own.MatchRefBone(new FName(Main));
            if (_hips >= 0 && _hips < worn.Count && theirs >= 0 && theirs < mine.Count)
            {
                _hipsAt = worn[_hips].Translation;
                var tall = mine[theirs].Translation.Length();
                _stride = tall > 1f ? Math.Clamp(_hipsAt.Length() / tall, ShortestStride, LongestStride) : 1f;
            }

            Choose(worn, mine);
        }
        else
        {
            Log.Info("The worn body's rest pose didn't arrive, so its held things and hips are left as they come.");
        }

        TakeOffRest(player);
    }

    // A bone only takes the player's turn if its children lie the way the player's do. That is
    // how the two skeletons show they were built alike, whatever pose each one rests in.
    static void Choose(TArray<Atom> worn, TArray<Atom> mine)
    {
        var alike = new List<(int Worn, int Mine)>();
        var dropped = 0;
        foreach (var (bone, theirs) in _maybe)
        {
            var same = true;
            for (var child = 0; child < _parents.Length && same; child++)
            {
                if (_parents[child] != bone || _mineOf[child] is not (>= 0 and var match) || child >= worn.Count || match >= mine.Count)
                {
                    continue;
                }

                var a = worn[child].Translation;
                var b = mine[match].Translation;
                if (a.Length() < 0.5f || b.Length() < 0.5f)
                {
                    continue;
                }

                var cos = Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b));
                same = cos >= MathF.Cos(Unlike * MathF.PI / 180f);
            }

            if (same)
            {
                alike.Add((bone, theirs));
            }
            else
            {
                dropped++;
            }
        }

        _turns = Turns(alike);

        if (dropped > 0)
        {
            Log.Info($"{dropped} of the worn body's main bones are built unlike yours, so they keep its own stance.");
        }
    }

    static (int, int, bool, int[])[] Turns(List<(int Worn, int Mine)> picked)
    {
        var taken = new HashSet<int>(picked.Select(turn => turn.Worn));
        return picked.Select(turn =>
        {
            var parent = _parents[turn.Worn];
            if (parent < 0 || taken.Contains(parent))
            {
                return (turn.Worn, turn.Mine, false, Array.Empty<int>());
            }

            var chain = new List<int>();
            for (var up = parent; up >= 0 && chain.Count < 64; up = _parents[up])
            {
                chain.Add(up);
            }

            chain.Reverse();
            return (turn.Worn, turn.Mine, true, chain.ToArray());
        }).ToArray();
    }

    // Shows the look's own POSE when it is picked, and remembers a change for that look.
    static void KeepStance(string look)
    {
        if (!look.Equals(_stanceOf, StringComparison.OrdinalIgnoreCase))
        {
            _stanceOf = look;
            var yours = Settings.Kept(StanceSection, look) is string stored && stored == Yours;
            Stance.SetOptions(new[] { ("own", "THEIR OWN"), (Yours, "LIKE YOURS") }, yours ? 1 : 0);
            _stanceWas = Stance.Index;
            return;
        }

        if (Stance.Index != _stanceWas)
        {
            _stanceWas = Stance.Index;
            Settings.Set(StanceSection, look, Stance.CurrentId);
        }
    }

    static void TakeOffRest(RPawnPlayer? player)
    {
        foreach (var rest in new[] { _wornRest, _mineRest })
        {
            if (rest is { IsValid: true } && player is { IsValid: true, bDeleteMe: false } && rest.Owner is { } owner && owner.Ptr == player.Ptr)
            {
                player.DetachComponent(rest);
            }
        }

        _wornRest = null;
        _mineRest = null;
    }

    // Runs right after the worn body is handed the hidden copy's pose for the tick.
    static void Shape(RAnimNode_AnimCapture pose, SkeletalMeshComponent own)
    {
        var saved = pose.SavedAtoms;
        var mine = own.LocalAtoms;
        var mineWhole = own.SpaceBases;
        var turns = Stance.Index == 1 ? _turns : Array.Empty<(int, int, bool, int[])>();
        foreach (var (worn, theirs, free, chain) in turns)
        {
            if (worn >= saved.Count || theirs >= mine.Count)
            {
                continue;
            }

            var atom = saved[worn];
            if (!free)
            {
                atom.Rotation = mine[theirs].Rotation;
            }
            else if (theirs < mineWhole.Count)
            {
                // The worn parent's turn against the whole body, built down from the root, then
                // taken off the player's turn of this bone against the whole body.
                var above = Quaternion.Identity;
                foreach (var up in chain)
                {
                    if (up < saved.Count)
                    {
                        above *= Numeric(saved[up].Rotation);
                    }
                }

                atom.Rotation = ToTurn(Quaternion.Normalize(Quaternion.Inverse(above) * Numeric(mineWhole[theirs].Rotation)));
            }

            saved[worn] = atom;
        }

        foreach (var (hold, atom) in HoldAt)
        {
            if (hold < saved.Count)
            {
                saved[hold] = atom;
            }
        }

        if (_hips >= 0 && _hips < saved.Count && _stride != 1f && _wornRest is null)
        {
            var atom = saved[_hips];
            atom.Translation = _hipsAt + (atom.Translation - _hipsAt) * _stride;
            saved[_hips] = atom;
        }
    }

    // Another feature can pose the worn body itself, like Firefly flying with his own animations.
    // It is handed the body and the pose taken from the player, and can write over that pose.
    public static Action<SkeletalMeshComponent, TArray<Atom>>? Repose;

    // Whether a bone is one a held thing hangs from, which the worn body keeps still.
    public static bool Holds(FName bone) => (bone.ToString() ?? "").StartsWith(Dummy, StringComparison.OrdinalIgnoreCase);

    // The game moves the player's mesh against the pawn when the pawn crouches or steps up.
    // Worn parts take the mesh's place once a tick, before the game does that, so they were a
    // tick behind and jumped. They are given the new place straight away instead.
    [Redirect(typeof(RPawn), nameof(RPawn.SyncMeshTranslationToCollisionCylinder))]
    static void MeshMoved(RPawn self)
    {
        Redirects.RunOriginal();
        try
        {
            if (_wearer is { IsValid: true } wearer && wearer.Ptr == self.Ptr && self.Mesh is { IsValid: true } own)
            {
                KeepUp(own);
            }
        }
        catch (Exception error)
        {
            Log.Error("disguise.place", "Disguise placing failed", error);
        }
    }

    // The worn body is handed its pose when the tick starts, before the player has moved. Going
    // up a step the game lifts the pawn and lowers the hips by the same amount, so a pose from
    // before the step put the worn body a step too high for a frame. The camera is worked out
    // after every mesh has moved, so the pose is taken again from there and the parts redone.
    public static void CatchUp()
    {
        try
        {
            if (_wearer is not { IsValid: true } wearer || World.Player is not { } player || player.Ptr != wearer.Ptr || wearer.Mesh is not { IsValid: true } own
                || _frame is not { IsValid: true } frame || _pose is not { IsValid: true } pose || _body is not { IsValid: true } body)
            {
                return;
            }

            pose.Capture(frame);
            Shape(pose, own);
            Repose?.Invoke(body, pose.SavedAtoms);
            body.ForceSkelUpdate();

            // The clothes and props copy the body's pose, so they go after it.
            foreach (var (piece, _) in Worn)
            {
                if (piece.IsValid && piece.Ptr != body.Ptr)
                {
                    piece.ForceSkelUpdate();
                }
            }

            foreach (var (shown, _, follows) in Carried)
            {
                if (follows && shown is SkeletalMeshComponent { IsValid: true } follower)
                {
                    follower.ForceSkelUpdate();
                }
            }
        }
        catch (Exception error)
        {
            Log.Error("disguise.catchup", "Disguise posing failed", error);
        }
    }

    static void KeepUp(SkeletalMeshComponent own)
    {
        foreach (var (piece, _) in Worn)
        {
            if (piece.IsValid)
            {
                Place(piece, own);
            }
        }

        foreach (var (shown, _, follows) in Carried)
        {
            if (follows && shown is SkeletalMeshComponent { IsValid: true } follower)
            {
                Place(follower, own);
            }
        }
    }
}
