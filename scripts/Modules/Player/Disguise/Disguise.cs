#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using Follow = BmSDK.Engine.RSkeletalMeshComponent_Export.EParentAnimComponentMode;

namespace Knightfall;

// PLAYER > CHARACTER, the DISGUISE rows. The looks come from the game's showcase, so every one
// of them is there wherever the player is, loaded when it is picked.
//
// A character in the showcase is a small level package with the model standing in it. Only that
// page of the showcase is used. Its players page is the playable characters and their suits, and
// PLAY AS and SUIT put those on for real, with their own cape and belt.
//
// A look is worn the way the game does its Joker hallucination. Each part of the model is a new
// mesh on the player that follows the player's own skeleton, and the player's own body, parts and
// cape are hidden. The player's body goes on animating underneath, so moves, gadgets and sockets
// all still work.
//
// A character's body is worn in two steps, because of how the engine treats a mesh that follows
// another. It gives it the other's pose and nothing more. The controllers a mesh carries, which
// place its helper bones as the body bends, only run on a mesh that poses itself. So a
// hidden copy of the body follows the player, and the body on show is its own mesh that is handed
// the hidden copy's pose every tick and then runs its controllers on it like any character.
static partial class Disguise
{
    const string Lender = "disguise";
    const string Ambient = "Showcase_Ambient";
    const string RiddlerMech = "Showcase_Characters_RiddlerMech";

    // WEAR AIMED CHARACTER's look sits at the top of LOOK under this id until the next one.
    const string AimedLook = "aimed";
    const string AimedLender = "disguise aimed";

    // A playable character's suit worn as a look. Its id is this followed by the suit's name.
    const string SuitLook = "suit:";

    const string NoRoom = "NOT ENOUGH ROOM";
    const string NoLoad = "CAN'T LOAD";
    const string NoFit = "WON'T FIT";

    // Why the last look didn't go on, for the note.
    static string _why = NoLoad;

    // A model has to have most of these to sit on a person's skeleton.
    static readonly FName[] PersonBones =
    {
        new("Bip01_Pelvis"), new("Bip01_Spine2"), new("Bip01_Head"),
        new("Bip01_L_Hand"), new("Bip01_R_Hand"), new("Bip01_L_Foot"), new("Bip01_R_Foot"),
    };

    const int PersonBonesNeeded = 5;

    // The tick group of every part on show, which is the player's own mesh's. A jacket or a rag
    // can be simulated cloth, and the engine works cloth out against where its mesh was when that
    // mesh was last updated. The pawn moves its own cloth meshes into this group for that reason.
    // In a later group the cloth was worked out a frame behind the player and trailed after them.
    const GameObject.ETickingGroup Shown = GameObject.ETickingGroup.TG_PreAsyncWork2;

    // Every worn mesh is held at its best detail level. 0 lets the engine pick, and on a lower
    // Level of Detail setting it picks lower levels, where parts of a look can drop out.
    const int BestDetail = 1;

    // Follows is how a part followed the character's body in the showcase.
    //
    // A mesh can carry controllers of its own, which place its helper bones. Controls is whether
    // the part ran them in the showcase.
    sealed record Part(SkeletalMesh Mesh, MaterialInterface?[] Materials, PhysicsAsset? Physics, Follow Follows, bool Flaps, bool Controls);

    // A thing the character has with them in the showcase that isn't part of the model, like a hat
    // or a gun. On is the part it hangs from. Follows is set for one that took that part's pose,
    // and the rest hang from Bone, placed by At, Turn and Size. Inner is the mesh's own place in
    // the prop.
    sealed record Prop(
        string Name, StaticMesh? Solid, SkeletalMesh? Skinned, MaterialInterface?[] Materials, int On, Follow? Follows,
        FName Bone, Vector3 At, Rotator Turn, Vector3 Size, (Vector3 At, Rotator Turn, Vector3 Size) Inner);

    sealed record Model(List<Part> Parts, List<Prop> Props, bool Caped, int People);

    static readonly Cover Hidden = new("disguise");

    static readonly List<(SkeletalMeshComponent Piece, Part Part)> Worn = new();
    static readonly HashSet<nint> WornAt = new();
    static readonly Func<nint, bool> Ours = WornAt.Contains;

    // Set when the camera hook hid the player's body and posed the worn one this frame.
    static bool _caught;
    static readonly HashSet<string> Unfit = new(StringComparer.OrdinalIgnoreCase);

    // Props on show, and the worn part each one hangs from.
    static readonly List<(PrimitiveComponent Shown, SkeletalMeshComponent Holder, bool Follows)> Carried = new();

    static (string Id, string Label)[] _looks = Array.Empty<(string, string)>();

    // The showcase's characters page is fixed config, so it is only read again when its length
    // changes.
    static (string Id, string Key)[] _showcase = Array.Empty<(string, string)>();
    static int _showcaseCount = -1;

    static Model? _model;
    static bool _propsOn;
    static RPawnPlayer? _wearer;

    // The player's mesh and its model when the look went on.
    static nint _ownAt;
    static nint _ownModel;

    // How long the LOOK pick has to rest before a worn look is swapped for it, in seconds.
    const double SwapAfter = 0.35;

    // Set by anything that puts a look on itself, which isn't the player stepping through the list.
    static bool _swapNow;

    static SkeletalMeshComponent? _body;
    static SkeletalMeshComponent? _frame;
    static RAnimNode_AnimCapture? _pose;
    static bool _caped;
    static string _wornLook = "";
    static int _wornVariant = -1;
    static string _variantsOf = "";
    static string _failed = "";
    static Model? _aimed;
    static string _aimedLabel = "";
    static readonly List<(string Id, string Label)> Suits = new();

    public static readonly Choice Look = new(
        "player.character.disguise_look", "LOOK",
        "Every character in the game's showcase. Pick one, then turn DISGUISE on. While it is on, picking another swaps to it. Playable ones are in PLAY AS.",
        new[] { ("", "OPEN IN GAME") }, saved: false)
    {
        Opening = Refresh,
    };

    public static readonly Choice Variant = new(
        "player.character.disguise_variant", "VARIANT",
        "Some looks are a group, like the militia or the police. This steps through the different people in it.",
        new[] { ("0", "1") }, saved: false)
    {
        Block = static () => !Option!.On ? "DISGUISE IS OFF" : Variant!.Count < 2 ? "ONLY ONE" : null,
    };

    public static readonly Toggle Option = new(
        "player.character.disguise", "DISGUISE",
        "Wears the picked look. Your moves, fighting and gadgets stay your own. It comes off when you turn it off or start the game again.",
        saved: false)
    {
        // Never locked while on, so it can always be turned off.
        Block = static () => Option!.On ? null : Look.CurrentId.Length == 0 ? "NO LOOK PICKED" : Unfit.Contains(Look.CurrentId) ? NoFit : null,
        Tick = Keep,
        Restore = TakeOff,
    };

    public static readonly Toggle Props = new(
        "player.character.disguise_props", "PROPS",
        "Also wears the hats, guns and other things the look has with it. Showcase ones sit where the character's rest pose holds them, so some are a little off.");

    public static readonly Command WearAimed = new(
        "player.character.disguise_wear_aimed", "WEAR AIMED CHARACTER",
        "Wears the exact look of the person in the middle of the screen, their hat and colours included. It stays at the top of LOOK until you aim at someone else.",
        WearAimedLook)
    {
        Preview = WorldMarks.Picks(ToWear, "WEAR THIS LOOK", "THEIR EXACT LOOK"),
    };

    // Whose look WEAR AIMED CHARACTER would take.
    static WorldMarks.Picked ToWear()
    {
        if (World.Player is null)
        {
            return new WorldMarks.Picked(Outcome.NoPlayer, null);
        }

        return Editor.Aimed(null, static kind => kind is Things.Kind.Enemy or Things.Kind.Friendly) is Pawn person && Things.Alive(person)
            ? new WorldMarks.Picked(null, person)
            : new WorldMarks.Picked(Outcome.Failed("AIM AT A PERSON"), null);
    }

    public static readonly Command Shuffle = new(
        "player.character.disguise_random", "RANDOM LOOK",
        "Puts on a look picked at random from the list.",
        WearRandom);

    // The worn body, while it is on the character being played.
    public static SkeletalMeshComponent? WornBody =>
        _wearer is { IsValid: true } wearer && World.Player is { } player && player.Ptr == wearer.Ptr && _body is { IsValid: true } body ? body : null;

    // Puts a look on for another feature, like a villain's kit. False while the showcase list
    // isn't there yet or has no such look.
    public static bool Put(string package)
    {
        Refresh();
        var index = Array.FindIndex(_looks, look => look.Id.Equals(package, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        if (Look.Index != index)
        {
            Look.SetTo(index);
        }

        _swapNow = true;
        Option.SetTo(true);
        return true;
    }

    // Takes off a look another feature put on. A feature is switched off a tick after the next
    // one claims the player, so by then a different look may be on, and that one stays.
    public static void TakeOffLook(string package)
    {
        if (Option.On && Look.CurrentId.Equals(package, StringComparison.OrdinalIgnoreCase))
        {
            Option.SetTo(false);
        }
    }

    public static void TakeOffSuit(string suit) => TakeOffLook(SuitLook + suit);

    // Worn parts have no physics bodies for cloth to collide with, since a ragdoll on a part took
    // the game down, so a long coat sinks into the body. With its simulation off the cloth moves
    // with the bones it is skinned to.
    static readonly HashSet<nint> Stilled = new();

    public static void StillCloth()
    {
        foreach (var (piece, part) in Worn)
        {
            if (piece.IsValid && part.Mesh.IsValid && part.Mesh.ClothingAssets.Count > 0 && Stilled.Add(piece.Ptr))
            {
                piece.SetEnableClothingSimulation(false);
            }
        }
    }


    public static void LetCloth()
    {
        foreach (var (piece, _) in Worn)
        {
            if (piece.IsValid && Stilled.Contains(piece.Ptr))
            {
                piece.SetEnableClothingSimulation(true);
            }
        }

        Stilled.Clear();
    }

    // Puts a playable character's suit on as a look. A showcase model is made for cutscenes, and
    // some come apart once they move like a player. The suit then stays in LOOK under the label.
    public static bool PutSuit(string suit, string label)
    {
        var id = SuitLook + suit;
        if (!Suits.Exists(entry => entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
        {
            Suits.Add((id, label));
        }

        return Put(id);
    }

    // The worn part that has this socket, for things that hang off the look, like jet flames.
    public static SkeletalMeshComponent? WornWith(FName socket)
    {
        if (WornBody is null)
        {
            return null;
        }

        foreach (var (piece, _) in Worn)
        {
            if (piece.IsValid && piece.GetSocketByName(socket) is { IsValid: true })
            {
                return piece;
            }
        }

        return null;
    }

    // A worn prop by its mesh's name, like a villain's gun. Null while PROPS hasn't put it on.
    public static SkeletalMeshComponent? Carrying(string mesh)
    {
        if (WornBody is null)
        {
            return null;
        }

        foreach (var (shown, _, _) in Carried)
        {
            if (shown is SkeletalMeshComponent { IsValid: true, SkeletalMesh: { IsValid: true } skinned } prop
                && (skinned.Name.ToString() ?? "").Equals(mesh, StringComparison.OrdinalIgnoreCase))
            {
                return prop;
            }
        }

        return null;
    }

    // The showcase's own list. An entry names the levels it shows, and the last of them is the
    // package with the model in it.
    static void Refresh()
    {
        if (World.Info is not { } info)
        {
            return;
        }

        var items = info.ShowcaseItems;
        if (items.Count != _showcaseCount)
        {
            var count = items.Count;
            info.ShowcaseValidateSorting();
            var found = new List<(string Id, string Key)>();
            foreach (var item in info.ShowcaseItems)
            {
                var key = item.Item.ToString() ?? "";
                if (key.Length == 0 || !"Characters".Equals(item.Page.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var id = "";
                foreach (var level in (item.Levels.ToString() ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!level.StartsWith(Ambient, StringComparison.OrdinalIgnoreCase) && GameFiles.HasPackage(level))
                    {
                        id = level;
                    }
                }

                // The mech's level only shows Riddler sat in it. The mech itself isn't shaped like
                // a person, so the entry would just be Riddler again.
                if (id.Equals(RiddlerMech, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (id.Length == 0 || found.Exists(entry => entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                found.Add((id, key));
            }

            _showcase = found.ToArray();
            _showcaseCount = count;
        }

        var list = new List<(string Id, string Label)>();
        foreach (var (id, key) in _showcase)
        {
            // A look that was tried and isn't shaped like a person stays in the list, marked, so
            // it is clear why it didn't go on.
            var label = Showcase.Label(key, key);
            list.Add((id, Unfit.Contains(id) ? label + " (WON'T FIT)" : label));
        }

        if (list.Count == 0)
        {
            return;
        }

        list.Sort((a, b) => string.CompareOrdinal(a.Label, b.Label));
        list.InsertRange(0, Suits);
        if (_aimed is not null)
        {
            list.Insert(0, (AimedLook, _aimedLabel));
        }

        var looks = list.ToArray();
        if (looks.AsSpan().SequenceEqual(_looks))
        {
            return;
        }

        var picked = Look.CurrentId;
        _looks = looks;
        Look.SetOptions(looks, Math.Max(0, Array.FindIndex(looks, entry => entry.Id.Equals(picked, StringComparison.OrdinalIgnoreCase))));
    }

    static void Keep()
    {
        if (World.Player is not { } player || player.Mesh is not { IsValid: true, SkeletalMesh: { IsValid: true } model } own)
        {
            return;
        }

        if (_wearer is not null && (!_wearer.IsValid || _wearer.Ptr != player.Ptr))
        {
            // A body the player left that is still in the world, like a partner in a team fight or a
            // swapped sidekick, gets its own look back. One that was destroyed took its parts with it.
            if (_wearer is { IsValid: true, bDeleteMe: false, bPendingDelete: false })
            {
                Strip();
                Uncover();
            }

            Drop();
        }

        // A suit change gives the player's mesh a new model. The parts that follow it keep the bone
        // map they worked out for the old one, so the look is put on again from scratch.
        if (_wearer is not null && (own.Ptr != _ownAt || model.Ptr != _ownModel))
        {
            Strip();
            _wornLook = "";
            _swapNow = true;
        }

        var look = Look.CurrentId;
        if (look.Length == 0)
        {
            return;
        }

        // Stepping through LOOK passes many looks, and each one is a package to load. The look that
        // is on stays until the pick has rested.
        if (_wearer is not null && !_swapNow && !look.Equals(_wornLook, StringComparison.OrdinalIgnoreCase)
            && System.Diagnostics.Stopwatch.GetElapsedTime(Look.TouchedAt).TotalSeconds < SwapAfter)
        {
            Maintain(player, own);
            return;
        }

        _swapNow = false;

        // A new look starts on its first person.
        if (!look.Equals(_variantsOf, StringComparison.OrdinalIgnoreCase))
        {
            _variantsOf = look;
            Variant.SetOptions(new[] { ("0", "1") }, 0);
        }

        var variant = Variant.Index;
        KeepStance(look);
        if (_wearer is not null && look.Equals(_wornLook, StringComparison.OrdinalIgnoreCase) && variant == _wornVariant)
        {
            if (Props.On != _propsOn)
            {
                TakeOffProps();
                _propsOn = Props.On;
                if (_propsOn)
                {
                    PutOnProps(player, own);
                }
            }

            Maintain(player, own);
            return;
        }

        // Tried once. A look that can't be worn is not tried again every tick.
        var wanted = $"{look}#{variant}";
        if (wanted == _failed)
        {
            return;
        }

        // A look that can't be worn switches the option back off, so it never reads ON with
        // nothing on.
        if (!Wear(player, own, look, variant))
        {
            _failed = wanted;
            Toasts.Show(Option.Plain, _why, OutcomeKind.Failed, Option, always: true);
            Option.Reset();
        }
    }

    static bool Wear(RPawnPlayer player, SkeletalMeshComponent own, string look, int variant)
    {
        _why = NoLoad;
        var model = Character(look, variant);
        if (model is null || model.Parts.Count == 0)
        {
            Log.Info($"The look {look} could not be loaded.");
            return false;
        }

        if (model.People != Variant.Count)
        {
            var steps = new (string Id, string Label)[model.People];
            for (var i = 0; i < steps.Length; i++)
            {
                steps[i] = (i.ToString(), (i + 1).ToString());
            }

            variant = Math.Clamp(variant, 0, steps.Length - 1);
            Variant.SetOptions(steps, variant);
        }

        // The aimed look's meshes belong to the world they were copied in.
        if (!model.Parts[0].Mesh.IsValid)
        {
            Log.Info($"The look {look} is no longer in memory.");
            return false;
        }

        Strip();

        // A look that has a cape of its own keeps the player's cape on show.
        _caped = model.Caped;
        if (_caped)
        {
            Hidden.ShowCape();
        }

        foreach (var part in model.Parts)
        {
            if (!part.Mesh.IsValid)
            {
                continue;
            }

            var piece = Made.Tracked(new SkeletalMeshComponent(player));
            piece.SetActorCollision(false, false);
            piece.SetSkeletalMesh(part.Mesh, false);
            piece.ForcedLodModel = BestDetail;

            // The first part is the body. One that isn't shaped like a person can't follow a
            // person's skeleton.
            if (Worn.Count == 0 && !Person(piece))
            {
                Log.Info($"The look {look} isn't shaped like a person, so it can't be worn.");
                _why = NoFit;
                Unfit.Add(look);
                Catalog.MenuOpening();
                return false;
            }

            if (part.Physics is { IsValid: true } physics)
            {
                piece.SetPhysicsAsset(physics, false);
            }

            var slots = Math.Min(part.Materials.Length, piece.GetNumElements());
            for (var i = 0; i < slots; i++)
            {
                if (part.Materials[i] is { IsValid: true } material)
                {
                    piece.SetMaterial(i, material);
                }
            }

            // The body is worn in two steps. A hidden copy follows the player from its own rest
            // pose, so it keeps its own shape and size. The body on show is handed that pose.
            var posesItself = _body is null;
            if (posesItself)
            {
                var frame = Made.Tracked(new SkeletalMeshComponent(player));
                frame.SetActorCollision(false, false);
                frame.SetSkeletalMesh(part.Mesh, false);
                frame.ForcedLodModel = BestDetail;
                frame.SetTickGroup(GameObject.ETickingGroup.TG_DuringAsyncWork);
                frame.ParentAnimComponentMode = Follow.PACM_RelativeToReferencePose;
                frame.SetParentAnimComponent(own);
                frame.bUpdateSkelWhenNotRendered = true;
                frame.bCastDynamicShadow = false;
                frame.SetHidden(true);
                player.AttachComponent(frame);
                _frame = frame;
                WornAt.Add(frame.Ptr);

                // The game's own node for playing back a pose taken from a mesh. It has to be on
                // the part before the part is attached.
                _pose = Made.Tracked(new RAnimNode_AnimCapture(piece));
                piece.Animations = _pose;
                piece.SetTickGroup(Shown);
                Place(piece, own);
                Shaping(player, own, piece);
            }
            else
            {
                // Clothes are made on a standard skeleton and take the body's bones as they are,
                // which is what fits them to a big or small character. So they follow the worn
                // body the way they did in the showcase, and are worked out after it. Measured
                // in game, every bone they share then sits exactly on the body's.
                //
                // The worn body is shown where the player's mesh is. Clothes that took their
                // place from what they follow would get the worn body's own place, which is the
                // pawn's middle. So they don't, and are given the player's mesh's place.
                //
                // They go on after the body, and parts in one tick group are worked out in the
                // order they went on.
                //
                // A part with bones of its own that the body hasn't got, like a collar or a tie,
                // follows in the fill-in mode, which works those bones out from the part's own
                // rest pose. The showcase's mode left them unset and the cloth on them stretched
                // off into the distance.
                piece.SetTickGroup(Shown);
                piece.ParentAnimComponentMode = Unshared(piece, _body!) ? Follow.PACM_FillIn : part.Follows;
                piece.bTransformFromAnimParent = 0;
                piece.SetParentAnimComponent(_body!);
                Place(piece, own);
            }

            // Without its mesh's own controllers a part's helper bones are left where they
            // rest, and what is skinned to them slides out of place as the body moves.
            piece.bEnableSkeletalMeshSkelControls = part.Controls;

            // No part is given a ragdoll of its own. Asking for one on a part that poses itself
            // took the game down, and a part that follows another never used it.
            //
            // Laces, earphones and the like hang by a simulation that runs on the body and
            // lives in its physics asset. The showcase plays a baked animation and leaves it
            // off, so a character's body has it switched on here. The clothes take the
            // simulated bones from the body.
            var hangs = posesItself ? part.Physics is { IsValid: true } : part.Flaps;
            piece.SetAllowRFlapsAssetInstance(hangs);
            piece.bCastDynamicShadow = own.bCastDynamicShadow;
            piece.SetLightingChannels(own.LightingChannels);

            // The player's own depth group. In the world's group, cinematic skin renders black.
            piece.SetDepthPriorityGroup(own.DepthPriorityGroup);
            piece.SetOwnerNoSee(own.bOwnerNoSee);
            player.AttachComponent(piece);

            // Asked for before it was attached, the simulation was left off. It is asked for
            // again now that the body is part of the world.
            if (posesItself && hangs && !piece.bAllowRFlapsAssetInstance)
            {
                piece.SetAllowRFlapsAssetInstance(true);
            }

            Worn.Add((piece, part));
            WornAt.Add(piece.Ptr);
            _body ??= piece;
        }

        _model = model;
        _propsOn = Props.On;
        if (_propsOn)
        {
            PutOnProps(player, own);
        }

        _wearer = player;
        _ownAt = own.Ptr;
        _ownModel = own.SkeletalMesh?.Ptr ?? 0;
        _wornLook = look;
        _wornVariant = variant;
        _failed = "";
        Maintain(player, own);
        return true;
    }

    // A showcase level holds the character, sometimes several, and loose props beside them. The
    // characters are the game's cinematic actors. They are kept while the look is picked, so
    // stepping through them loads nothing.
    static Model? Character(string package, int variant)
    {
        if (package.Equals(AimedLook, StringComparison.Ordinal))
        {
            return _aimed;
        }

        if (package.StartsWith(SuitLook, StringComparison.Ordinal))
        {
            return Suit(package[SuitLook.Length..]);
        }

        // A villain puts its look on without asking the row's lock, so one known not to fit is
        // turned down here before its package is loaded again.
        if (Unfit.Contains(package))
        {
            _why = NoFit;
            return null;
        }

        var people = Find(package);
        if (people.Count == 0)
        {
            if (!Packages.HasRoom(package))
            {
                _why = NoRoom;
                Log.Info($"No room to load the look {package}.");
                return null;
            }

            if (!Packages.TryLoad(package, $"the look {package}", "disguise.load"))
            {
                return null;
            }

            people = Find(package);
        }

        Borrowed.Let(Lender);
        foreach (var kept in people)
        {
            Borrowed.Keep(kept, Lender);
        }

        if (people.Count == 0)
        {
            _why = NoFit;
            Unfit.Add(package);
            Catalog.MenuOpening();
            Log.Info($"The look {package} has nobody shaped like a person in it.");
            return null;
        }

        // The body first, which is the first part shaped like a person, then the rest.
        var person = people[Math.Clamp(variant, 0, people.Count - 1)];
        var sources = Sources(person);
        var body = sources.FindIndex(Person);
        var parts = new List<Part>();
        Add(parts, sources[body]);
        for (var i = 0; i < sources.Count; i++)
        {
            if (i != body)
            {
                Add(parts, sources[i]);
            }
        }

        var caped = person is RCinematicBatmanBase { EnableCape: true, CapeSkeletalMesh: { IsValid: true, SkeletalMesh: { IsValid: true } } };
        return new Model(parts, PropsOf(package, person, sources, body), caped, people.Count);
    }

    // The props in a showcase level are actors stood on the character, each one on a part and a
    // bone of it.
    static List<Prop> PropsOf(string package, RCinematicActor person, List<SkeletalMeshComponent> sources, int body)
    {
        var prefix = package + ".";
        var props = new List<Prop>();
        foreach (var actor in Game.FindObjects<Actor>())
        {
            if (!actor.IsValid || actor.bHidden || actor.Base is not { IsValid: true } stood || stood.Ptr != person.Ptr
                || (actor is RCinematicActor other && Sources(other).Exists(Person)) || !actor.GetPathName().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Parts are worn body first, then the rest in their own order.
            var source = actor.BaseSkelComponent is { IsValid: true } part ? sources.FindIndex(each => each.Ptr == part.Ptr) : body;
            if (source < 0)
            {
                continue;
            }

            var on = source == body ? 0 : source < body ? source + 1 : source;
            MeshComponent? shown = actor switch
            {
                DynamicSMActor { StaticMeshComponent: { IsValid: true, StaticMesh: { IsValid: true } } solid } => solid,
                SkeletalMeshActor { SkeletalMeshComponent: { IsValid: true, SkeletalMesh: { IsValid: true } } skinned } => skinned,
                _ => null,
            };

            if (shown is null || shown.HiddenGame)
            {
                continue;
            }

            var painted = shown.Materials;
            var materials = new MaterialInterface?[painted.Count];
            for (var i = 0; i < materials.Length; i++)
            {
                materials[i] = painted[i];
            }

            var skinnedMesh = shown is SkeletalMeshComponent skeletal ? skeletal.SkeletalMesh : null;
            Follow? follows = shown is SkeletalMeshComponent { ParentAnimComponent: { IsValid: true } } follower ? follower.ParentAnimComponentMode : null;
            props.Add(new Prop(
                skinnedMesh?.Name.ToString() ?? (shown as StaticMeshComponent)?.StaticMesh.Name.ToString() ?? "?",
                (shown as StaticMeshComponent)?.StaticMesh, skinnedMesh, materials, on, follows,
                actor.BaseBoneName, actor.RelativeLocation, actor.RelativeRotation, actor.DrawScale3D * actor.DrawScale,
                (shown.Translation, shown.Rotation, shown.Scale3D * shown.Scale)));
        }

        return props;
    }

    // A prop that took its part's pose in the showcase follows that part here too. The rest hang
    // from their bone of it, where the showcase had them.
    static void PutOnProps(RPawnPlayer player, SkeletalMeshComponent own)
    {
        if (_model is null)
        {
            return;
        }

        foreach (var prop in _model.Props)
        {
            if (prop.On >= Worn.Count || Worn[prop.On].Piece is not { IsValid: true } holder)
            {
                continue;
            }

            var bone = prop.Bone.Index != 0 ? prop.Bone : holder.GetBoneName(0);
            if (prop.Follows is null && holder.MatchRefBone(bone) < 0)
            {
                Log.Info($"The prop {prop.Name} hangs from {bone}, which the worn body hasn't got.");
                continue;
            }

            MeshComponent shown;
            if (prop.Skinned is { IsValid: true } skinned)
            {
                var mesh = Made.Tracked(new SkeletalMeshComponent(player));
                mesh.SetSkeletalMesh(skinned, false);
                mesh.ForcedLodModel = BestDetail;
                shown = mesh;
            }
            else if (prop.Solid is { IsValid: true } solid)
            {
                var mesh = Made.Tracked(new StaticMeshComponent(player));
                mesh.SetStaticMesh(solid, false);
                shown = mesh;
            }
            else
            {
                continue;
            }

            shown.SetActorCollision(false, false);
            var slots = Math.Min(prop.Materials.Length, shown.GetNumElements());
            for (var i = 0; i < slots; i++)
            {
                if (prop.Materials[i] is { IsValid: true } material)
                {
                    shown.SetMaterial(i, material);
                }
            }

            shown.bCastDynamicShadow = own.bCastDynamicShadow;
            shown.SetLightingChannels(own.LightingChannels);
            shown.SetDepthPriorityGroup(own.DepthPriorityGroup);
            shown.SetOwnerNoSee(own.bOwnerNoSee);

            if (prop.Follows is { } mode && shown is SkeletalMeshComponent follower)
            {
                // Like the clothes, in step with the body and given the player's mesh's place.
                follower.SetTickGroup(Shown);
                follower.ParentAnimComponentMode = mode;
                follower.bTransformFromAnimParent = 0;
                follower.SetParentAnimComponent(holder);
                player.AttachComponent(follower);
                Place(follower, own);
                Carried.Add((follower, holder, true));
            }
            else
            {
                shown.SetTranslation(prop.Inner.At);
                shown.SetRotation(prop.Inner.Turn);
                shown.SetScale3D(prop.Inner.Size);
                holder.AttachComponent(shown, bone, prop.At, prop.Turn, prop.Size);
                Carried.Add((shown, holder, false));
            }

            WornAt.Add(shown.Ptr);
        }
    }

    static void TakeOffProps()
    {
        var wearer = _wearer is { IsValid: true, bDeleteMe: false } alive ? alive : null;
        foreach (var (shown, holder, follows) in Carried)
        {
            if (!shown.IsValid)
            {
                continue;
            }

            shown.SetHidden(true);
            if (follows && wearer is not null && shown.Owner is { } owner && owner.Ptr == wearer.Ptr)
            {
                wearer.DetachComponent(shown);
            }
            else if (!follows && holder.IsValid)
            {
                holder.DetachComponent(shown);
            }

            WornAt.Remove(shown.Ptr);
        }

        Carried.Clear();
    }

    // A cinematic actor's main part and whichever of its five extra parts it uses.
    public static List<SkeletalMeshComponent> Sources(RCinematicActor actor)
    {
        var sources = new List<SkeletalMeshComponent>();
        foreach (var part in new[]
        {
            actor.SkeletalMeshComponent, actor.ExtraSkeletalMeshComponent1, actor.ExtraSkeletalMeshComponent2,
            actor.ExtraSkeletalMeshComponent3, actor.ExtraSkeletalMeshComponent4, actor.ExtraSkeletalMeshComponent5,
        })
        {
            if (part is { IsValid: true, HiddenGame: false, SkeletalMesh: { IsValid: true } })
            {
                sources.Add(part);
            }
        }

        return sources;
    }

    // A person in the world has their body as the pawn's mesh, and the rest, like a head or a
    // coat, as parts that follow it.
    static List<SkeletalMeshComponent> PawnSources(Pawn pawn)
    {
        var sources = new List<SkeletalMeshComponent>();
        if (pawn.Mesh is not { IsValid: true, HiddenGame: false, SkeletalMesh: { IsValid: true } } body)
        {
            return sources;
        }

        sources.Add(body);
        var components = pawn.Components;
        for (var i = 0; i < components.Count; i++)
        {
            if (components[i] is SkeletalMeshComponent { IsValid: true, HiddenGame: false, SkeletalMesh: { IsValid: true } } part
                && part.Ptr != body.Ptr && part.ParentAnimComponent is { } parent && parent.Ptr == body.Ptr)
            {
                sources.Add(part);
            }
        }

        return sources;
    }

    // What a person in the world has on that isn't part of the body, like a hat. Each one hangs
    // from a bone of the body. A gun belongs to the weapon and not to them, so it is left.
    static List<Prop> PawnProps(Pawn pawn, SkeletalMeshComponent body)
    {
        var props = new List<Prop>();
        var attached = body.Attachments;
        for (var i = 0; i < attached.Count; i++)
        {
            var entry = attached[i];
            if (entry.Component is not MeshComponent { IsValid: true, HiddenGame: false } shown || shown.Outer is not { } owner || owner.Ptr != pawn.Ptr
                || entry.BoneName.Index == 0 || shown is SkeletalMeshComponent { ParentAnimComponent: { IsValid: true } })
            {
                continue;
            }

            var solid = shown is StaticMeshComponent { StaticMesh: { IsValid: true } solidMesh } ? solidMesh : null;
            var skinned = shown is SkeletalMeshComponent { SkeletalMesh: { IsValid: true } skinnedMesh } ? skinnedMesh : null;
            if (solid is null && skinned is null)
            {
                continue;
            }

            var painted = shown.Materials;
            var materials = new MaterialInterface?[painted.Count];
            for (var m = 0; m < materials.Length; m++)
            {
                materials[m] = painted[m];
            }

            var size = entry.RelativeScale == Vector3.Zero ? Vector3.One : entry.RelativeScale;
            props.Add(new Prop(
                skinned?.Name.ToString() ?? solid?.Name.ToString() ?? "?",
                solid, skinned, materials, 0, null,
                entry.BoneName, entry.RelativeLocation, entry.RelativeRotation, size,
                (shown.Translation, shown.Rotation, shown.Scale3D * shown.Scale)));
        }

        return props;
    }

    // Copies the aimed person's parts as they are, so a random hat or colour comes too. What they
    // use is kept loaded, since the person can walk off and be cleaned up while it's worn.
    static Outcome WearAimedLook()
    {
        var pick = ToWear();
        if (pick.Refused is { } why)
        {
            return why;
        }

        var person = (Pawn)pick.Who!;

        var sources = PawnSources(person);
        var body = sources.FindIndex(Person);
        if (body < 0)
        {
            return Outcome.Failed("WON'T FIT");
        }

        var parts = new List<Part>();
        Add(parts, sources[body]);
        for (var i = 0; i < sources.Count; i++)
        {
            if (i != body)
            {
                Add(parts, sources[i]);
            }
        }

        var props = PawnProps(person, sources[body]);
        Borrowed.Let(AimedLender);
        foreach (var part in parts)
        {
            KeepAsset(part.Mesh);
            KeepAsset(part.Physics);
            foreach (var material in part.Materials)
            {
                KeepAsset(material);
            }
        }

        foreach (var prop in props)
        {
            KeepAsset(prop.Solid);
            KeepAsset(prop.Skinned);
            foreach (var material in prop.Materials)
            {
                KeepAsset(material);
            }
        }

        var name = Things.NameOf(person);
        _aimed = new Model(parts, props, false, 1);
        _aimedLabel = "AIMED: " + name;
        _looks = _looks.Where(entry => entry.Id != AimedLook).Prepend((AimedLook, _aimedLabel)).ToArray();
        Look.SetOptions(_looks, 0);

        // Worn again from scratch, even when the last aimed look is on.
        _wornLook = "";
        _swapNow = true;
        _failed = "";
        if (!Option.On)
        {
            Option.Press();
        }

        return Outcome.Done(name);
    }

    // The aimed look and the suits villains add are left out, as they aren't what the list is for.
    static Outcome WearRandom()
    {
        if (World.Player is null)
        {
            return Outcome.NoPlayer;
        }

        Refresh();
        var picked = Look.CurrentId;
        var open = new List<int>();
        for (var i = 0; i < _looks.Length; i++)
        {
            var id = _looks[i].Id;
            if (id.Length > 0 && !id.Equals(picked, StringComparison.OrdinalIgnoreCase) && !Unfit.Contains(id)
                && !id.Equals(AimedLook, StringComparison.Ordinal) && !id.StartsWith(SuitLook, StringComparison.Ordinal))
            {
                open.Add(i);
            }
        }

        if (open.Count == 0)
        {
            return Outcome.Failed("NO LOOKS YET");
        }

        var index = open[System.Random.Shared.Next(open.Count)];
        Look.SetTo(index);
        _swapNow = true;
        if (!Option.On)
        {
            Option.SetTo(true);
        }

        return Outcome.Done(_looks[index].Label);
    }

    // A suit is the game's own data for a playable character's look: the body, the meshes worn
    // with it and the body's physics. Each part keeps its mesh's own colours and follows the body
    // as the game's pawn has its extra meshes do.
    static Model? Suit(string suit)
    {
        RAddContentPlayerCharacterMesh data;
        var room = ObjectTable.Headroom >= Packages.Cost<RAddContentPlayerCharacterMesh>(suit) + Packages.Spare;
        try
        {
            data = Packages.Main<RAddContentPlayerCharacterMesh>(suit);
        }
        catch (Exception error)
        {
            _why = room ? NoLoad : NoRoom;
            Log.Info($"The suit {suit} could not be loaded: {error.Message}");
            return null;
        }

        Borrowed.Let(Lender);
        Borrowed.Keep(data, Lender);

        var meshes = new List<SkeletalMesh>();
        void Take(SkeletalMesh? mesh)
        {
            if (mesh is { IsValid: true } && !meshes.Exists(known => known.Ptr == mesh.Ptr))
            {
                meshes.Add(mesh);
            }
        }

        Take(data.MainMesh);
        Take(data.BodyMesh);
        var extras = data.ExtraMeshes;
        for (var i = 0; i < extras.Count; i++)
        {
            Take(extras[i]);
        }

        var parts = new List<Part>();
        foreach (var mesh in meshes)
        {
            parts.Add(new Part(mesh, Array.Empty<MaterialInterface?>(), parts.Count == 0 ? data.PhysicsAsset : null, Follow.PACM_Original, false, true));
        }

        return new Model(parts, new List<Prop>(), data.CapeMesh is { IsValid: true }, 1);
    }

    // Only assets from packages are kept. A colour made at run time belongs to the person, and
    // keeping it would keep them, their level and the whole world alive past a map change, which
    // the engine treats as fatal. While worn, the worn parts hold it anyway.
    static void KeepAsset(GameObject? asset)
    {
        if (asset is not { IsValid: true })
        {
            return;
        }

        for (var outer = asset.Outer; outer is not null; outer = outer.Outer)
        {
            if (outer is Actor or Level or BmSDK.Engine.World)
            {
                return;
            }
        }

        Borrowed.Keep(asset, AimedLender);
    }

    static List<RCinematicActor> Find(string package)
    {
        var prefix = package + ".";
        var people = new List<RCinematicActor>();
        foreach (var actor in Game.FindObjects<RCinematicActor>())
        {
            // A prop on show beside the character, like a gun, is a cinematic actor too.
            if (actor.IsValid && actor.GetPathName().StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Sources(actor).Exists(Person))
            {
                people.Add(actor);
            }
        }

        people.Sort((a, b) => string.CompareOrdinal(a.Name.ToString(), b.Name.ToString()));
        return people;
    }

    static void Add(List<Part> parts, SkeletalMeshComponent? source)
    {
        if (source is not { IsValid: true, HiddenGame: false } || source.SkeletalMesh is not { IsValid: true } mesh)
        {
            return;
        }

        var painted = source.Materials;
        var materials = new MaterialInterface?[painted.Count];
        for (var i = 0; i < materials.Length; i++)
        {
            materials[i] = painted[i];
        }

        parts.Add(new Part(mesh, materials, source.PhysicsAsset, source.ParentAnimComponentMode, source.bAllowRFlapsAssetInstance, source.bEnableSkeletalMeshSkelControls));
    }

    public static bool Unshared(SkeletalMeshComponent piece, SkeletalMeshComponent body)
    {
        var count = piece.SkeletalMesh?.RefSkeleton.Count ?? 0;
        for (var i = 0; i < count; i++)
        {
            if (body.MatchRefBone(piece.GetBoneName(i)) < 0)
            {
                return true;
            }
        }

        return false;
    }

    public static bool Person(SkeletalMeshComponent body)
    {
        var found = 0;
        foreach (var bone in PersonBones)
        {
            found += body.MatchRefBone(bone) >= 0 ? 1 : 0;
        }

        return found >= PersonBonesNeeded;
    }

    static void Maintain(RPawnPlayer player, SkeletalMeshComponent own)
    {
        // The hidden copy's pose goes to the body on show, which is placed where the player's
        // mesh is.
        if (_frame is { IsValid: true } frame && (frame.ParentAnimComponent is not { IsValid: true } followed || followed.Ptr != own.Ptr))
        {
            frame.SetParentAnimComponent(own);
        }

        var posed = _frame is { IsValid: true } && _pose is { IsValid: true } && _body is { IsValid: true };
        if (posed)
        {
            ReadRest(player, own);
        }

        // The camera hook hides the player's body and poses the worn one after every mesh has moved.
        // This does both for a frame the hook didn't run in.
        if (!_caught)
        {
            Hidden.Hide(player, own, Ours, _caped);
            if (posed)
            {
                _pose!.Capture(_frame!);
                var atoms = _pose.SavedAtoms;
                Shape(atoms, own);
                Repose?.Invoke(_body!, atoms);
            }
        }

        _caught = false;
        if (posed)
        {
            Place(_body!, own);
        }

        var unseen = Unseen();
        foreach (var (piece, _) in Worn)
        {
            if (!piece.IsValid)
            {
                continue;
            }

            if (piece.HiddenGame != unseen)
            {
                piece.SetHidden(unseen);
            }

            if (piece.bOwnerNoSee != own.bOwnerNoSee)
            {
                piece.SetOwnerNoSee(own.bOwnerNoSee);
            }

            if (piece.DepthPriorityGroup != own.DepthPriorityGroup)
            {
                piece.SetDepthPriorityGroup(own.DepthPriorityGroup);
            }

            if (_body is not { IsValid: true } worn || piece.Ptr == worn.Ptr)
            {
                continue;
            }

            if (piece.ParentAnimComponent is not { IsValid: true } parent || parent.Ptr != worn.Ptr)
            {
                piece.SetParentAnimComponent(worn);
            }

            Place(piece, own);
        }

        foreach (var (shown, holder, follows) in Carried)
        {
            if (!shown.IsValid)
            {
                continue;
            }

            if (shown.HiddenGame != unseen)
            {
                shown.SetHidden(unseen);
            }

            if (shown.bOwnerNoSee != own.bOwnerNoSee)
            {
                shown.SetOwnerNoSee(own.bOwnerNoSee);
            }

            if (shown.DepthPriorityGroup != own.DepthPriorityGroup)
            {
                shown.SetDepthPriorityGroup(own.DepthPriorityGroup);
            }

            if (follows && shown is SkeletalMeshComponent follower && holder.IsValid)
            {
                if (follower.ParentAnimComponent is not { IsValid: true } parent || parent.Ptr != holder.Ptr)
                {
                    follower.SetParentAnimComponent(holder);
                }

                Place(follower, own);
            }
        }
    }

    const string FirstPersonState = "FirstPerson";
    static int _cameraState = -1;
    static bool _unseen;

    // In its own first person views, like a wrist dart shot, the game hides the player's mesh in
    // ways that don't reach the worn parts. The camera has a state for each of those views, and
    // the name only changes with the state.
    static bool Unseen()
    {
        if (World.Controller?.PlayerCamera is not R3rdPersonCamera { IsValid: true } camera)
        {
            return false;
        }

        var state = camera.GetStateName();
        if (state.Index != _cameraState)
        {
            _cameraState = state.Index;
            _unseen = (state.ToString() ?? "").StartsWith(FirstPersonState, StringComparison.OrdinalIgnoreCase);
        }

        return _unseen;
    }

    // Gives a part the place of the player's mesh, which sits lower than the pawn.
    // Crouching moves it, so it is kept up to date.
    static void Place(SkeletalMeshComponent piece, SkeletalMeshComponent own)
    {
        if (piece.Translation != own.Translation)
        {
            piece.SetTranslation(own.Translation);
        }

        var turned = piece.Rotation;
        var turn = own.Rotation;
        if (turned.Pitch != turn.Pitch || turned.Yaw != turn.Yaw || turned.Roll != turn.Roll)
        {
            piece.SetRotation(turn);
        }

        if (piece.Scale != own.Scale)
        {
            piece.SetScale(own.Scale);
        }

        if (piece.Scale3D != own.Scale3D)
        {
            piece.SetScale3D(own.Scale3D);
        }
    }

    static void TakeOff()
    {
        Strip();
        Uncover();
        Drop();
        Borrowed.Let(Lender);
    }

    // Shows the player's own body, parts and cape again.
    static void Uncover() => Hidden.Show();

    // Takes the worn parts off. A part is only touched while it is still on the body it was put
    // on, because a body that was destroyed took its parts with it.
    static void Strip()
    {
        TakeOffProps();
        TakeOffRest(_wearer);
        if (_wearer is { IsValid: true, bDeleteMe: false } wearer)
        {
            foreach (var (piece, _) in Worn)
            {
                if (piece.IsValid && piece.Owner is { } owner && owner.Ptr == wearer.Ptr)
                {
                    piece.SetHidden(true);
                    wearer.DetachComponent(piece);
                }
            }

            if (_frame is { IsValid: true } frame && frame.Owner is { } holder && holder.Ptr == wearer.Ptr)
            {
                wearer.DetachComponent(frame);
            }
        }

        Forget();
    }

    static void Forget()
    {
        Worn.Clear();
        WornAt.Clear();
        Carried.Clear();
        _model = null;
        _turns = Array.Empty<(int, int, bool, int[])>();
        _maybe = new();
        _holds = Array.Empty<int>();
        HoldAt.Clear();
        _hips = -1;
        _wornRest = null;
        _mineRest = null;
        _body = null;
        _frame = null;
        _pose = null;
        _caught = false;
        Stilled.Clear();
        _cameraState = -1;
        _unseen = false;
    }

    static void Drop()
    {
        Forget();
        _wearer = null;
        _ownAt = 0;
        _ownModel = 0;
        _swapNow = false;
        _wornLook = "";
        _wornVariant = -1;
        _failed = "";
    }
}
