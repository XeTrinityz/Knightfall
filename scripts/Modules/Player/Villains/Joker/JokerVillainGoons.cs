#nullable enable

using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using Follow = BmSDK.Engine.RSkeletalMeshComponent_Export.EParentAnimComponentMode;

namespace Knightfall;

// The Joker's goons. A few of the area's thugs, rioters when there are any, come to his side as
// bodyguards and fight for him, dressed as Harley Quinn's clown gang. They are dressed the way the
// game dresses a thug in Batman's Joker hallucinations: the new look follows their body, and
// their own body and props are hidden under it. They leave when he does.
static partial class JokerVillain
{
    const int GoonCount = 3;
    const float GoonHealth = 1f;
    const float GoonsEvery = 45f;
    const string GoonGroup = "RIOTERS";

    // Harley's clown gang, as the showcase has them. They share the thugs' skeleton, and every
    // piece of a thug comes from a thug package, unlike the crates and barrels about them.
    const string GangLooks = "Showcase_Characters_HarleyThug";
    const string GangMesh = "Thug_";

    // A goon is only dressed once the game has finished building them. Dressed as they arrive,
    // the new look doesn't take.
    const float DressAfter = 1.5f;

    sealed record Piece(SkeletalMesh Mesh, MaterialInterface?[] Materials);

    sealed class Goon
    {
        public required RPawnVillain Body;
        public float DressAt;
        public readonly List<SkeletalMeshComponent> Worn = new();
        public readonly List<PrimitiveComponent> Hid = new();
    }

    static readonly List<Goon> Goons = new();
    static readonly List<List<Piece>> Gang = new();
    static bool _gangTried;
    static int _nextLook;
    static float _nextGoons;

    static Outcome CallGoons()
    {
        if (!Option.On)
        {
            return Outcome.Failed("JOKER IS OFF");
        }

        if (World.Player is null || World.Driving is not null)
        {
            return Outcome.NoPlayer;
        }

        if (World.Now < _nextGoons)
        {
            return Outcome.Failed($"READY IN {(int)MathF.Ceiling(_nextGoons - World.Now)} S");
        }

        if (People.EnemyKey(GoonGroup) is not { } key)
        {
            return Outcome.Failed("NOBODY TO CALL HERE");
        }

        LoadGang();
        var made = Bodyguards.Enlist(key, GoonCount, GoonHealth);
        if (made.Count == 0)
        {
            return Outcome.Failed("NOBODY CAME");
        }

        foreach (var body in made)
        {
            Goons.Add(new Goon { Body = body, DressAt = World.Now + DressAfter });
        }

        _nextGoons = World.Now + GoonsEvery;
        Say("laugh", always: true);
        return Outcome.Done($"{made.Count} GOONS");
    }

    // Each of the gang in the showcase, as the pieces of their look.
    static void LoadGang()
    {
        if (_gangTried)
        {
            return;
        }

        _gangTried = true;
        if (!GameFiles.HasPackage(GangLooks) || !Packages.TryLoad(GangLooks, "Harley's gang for the Joker's goons", "joker.goons"))
        {
            return;
        }

        var prefix = GangLooks + ".";
        var people = new Dictionary<nint, List<Piece>>();
        foreach (var part in Game.FindObjects<SkeletalMeshComponent>())
        {
            if (!part.IsValid || part.HiddenGame || part.SkeletalMesh is not { IsValid: true } mesh || part.Outer is not { IsValid: true } person
                || !part.GetPathName().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !mesh.GetPathName().StartsWith(GangMesh, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var painted = part.Materials;
            var materials = new MaterialInterface?[painted.Count];
            for (var i = 0; i < materials.Length; i++)
            {
                materials[i] = Lasting(painted[i]);
            }

            Kept(mesh);
            if (!people.TryGetValue(person.Ptr, out var pieces))
            {
                people[person.Ptr] = pieces = new List<Piece>();
            }

            pieces.Add(new Piece(mesh, materials));
        }

        Gang.AddRange(people.Values);
        if (Gang.Count == 0)
        {
            Log.Warn("joker.goons_look", "Harley's gang wasn't found, so the Joker's goons keep their own look.");
        }
    }

    // A colour made for the showcase's own character goes when the showcase does, and keeping it
    // would keep the whole showcase. Its parent from the package lasts, so that is used.
    static MaterialInterface? Lasting(MaterialInterface? material)
    {
        for (var at = material; at is { IsValid: true }; at = at is MaterialInstance { Parent: { IsValid: true } parent } ? parent : null)
        {
            var owned = false;
            for (var outer = at.Outer; outer is not null; outer = outer.Outer)
            {
                if (outer is Actor or Level or BmSDK.Engine.World)
                {
                    owned = true;
                    break;
                }
            }

            if (!owned)
            {
                return Kept(at);
            }
        }

        return null;
    }

    // The new look goes on following their body, and everything of their own is hidden.
    static void Dress(Goon goon)
    {
        if (Gang.Count == 0 || goon.Body.Mesh is not { IsValid: true } body)
        {
            return;
        }

        var look = Gang[_nextLook++ % Gang.Count];
        foreach (var piece in look)
        {
            var worn = Made.Tracked(new SkeletalMeshComponent(goon.Body));
            worn.SetActorCollision(false, false);
            worn.SetSkeletalMesh(piece.Mesh, false);
            for (var i = 0; i < piece.Materials.Length && i < worn.GetNumElements(); i++)
            {
                if (piece.Materials[i] is { IsValid: true } material)
                {
                    worn.SetMaterial(i, material);
                }
            }

            worn.SetTickGroup(GameObject.ETickingGroup.TG_PreAsyncWork2);
            worn.ParentAnimComponentMode = Follow.PACM_RelativeToReferencePose;
            worn.SetParentAnimComponent(body);
            worn.bCastDynamicShadow = body.bCastDynamicShadow;
            worn.SetLightingChannels(body.LightingChannels);
            goon.Body.AttachComponent(worn);
            goon.Worn.Add(worn);
        }

        Hide(goon, body);
        var extras = goon.Body.ExtraMeshes;
        for (var i = 0; i < extras.Count; i++)
        {
            if (extras[i] is { IsValid: true } extra && !goon.Worn.Exists(worn => worn.Ptr == extra.Ptr))
            {
                Hide(goon, extra);
            }
        }

        var statics = goon.Body.PropStatics;
        for (var i = 0; i < statics.Count; i++)
        {
            if (statics[i].PropComp is { IsValid: true } prop)
            {
                Hide(goon, prop);
            }
        }

        var skeletals = goon.Body.PropSkeletals;
        for (var i = 0; i < skeletals.Count; i++)
        {
            if (skeletals[i].PropComp is { IsValid: true } prop)
            {
                Hide(goon, prop);
            }
        }
    }

    static void Hide(Goon goon, PrimitiveComponent part)
    {
        if (!part.HiddenGame)
        {
            part.SetHidden(true);
            goon.Hid.Add(part);
        }
    }

    static void Undress(Goon goon)
    {
        foreach (var worn in goon.Worn)
        {
            if (worn.IsValid && goon.Body.IsValid)
            {
                goon.Body.DetachComponent(worn);
            }
        }

        foreach (var part in goon.Hid)
        {
            if (part.IsValid)
            {
                part.SetHidden(false);
            }
        }

        goon.Worn.Clear();
        goon.Hid.Clear();
    }

    // Goons who have fallen for good or left are forgotten. The rest are dressed once settled.
    static void KeepGoons()
    {
        for (var i = Goons.Count - 1; i >= 0; i--)
        {
            var goon = Goons[i];
            if (!goon.Body.IsValid || goon.Body.bDeleteMe || Bodyguards.GuardOf(goon.Body) is null)
            {
                Goons.RemoveAt(i);
                continue;
            }

            if (goon.DressAt > 0f && World.Now >= goon.DressAt)
            {
                goon.DressAt = 0f;
                Dress(goon);
            }
        }
    }

    static void DismissGoons()
    {
        foreach (var goon in Goons)
        {
            if (!goon.Body.IsValid || goon.Body.bDeleteMe)
            {
                continue;
            }

            Undress(goon);
            Bodyguards.Let(goon.Body);
        }

        Goons.Clear();
        _nextGoons = 0f;
    }

    static void UnloadGang()
    {
        Gang.Clear();
        _gangTried = false;
        _nextLook = 0;
    }
}
