#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// Changes who the player is, on the spot. The new character and suit are loaded first, the game's
// first character slot is pointed at them, and the game's own player restart is run from a start
// point put where the player stands. Place, facing, speed and health are carried across.
//
// The save is never told. While someone else is played it goes on naming the story's character
// and the gadget they held, so it loads as if nothing happened.
static class Switcher
{
    const int ArmourPools = 2;

    // The body that was left is only cleared out at once when fewer objects than this are free.
    // Otherwise it waits for the game's own clean-up, so going straight back to it loads nothing
    // and the switch has no clean-up frame.
    const int RoomFloor = 48000;

    // How long a switch waits for a clean-up it asked for before it tries again.
    const int RoomTicks = 3;

    // Who the story has the player as, kept while someone else is played. Empty otherwise.
    static string _story = "";
    static string _playing = "";
    static string _storyGadget = "";
    static string _storyMesh = "";
    static int _storyDamage;

    static (string Character, bool Again, Command Asker)? _waiting;
    static int _waitTicks;

    static bool _adopted;
    static bool _switching;

    public static bool Away => _story.Length > 0;

    public static Outcome To(string character, Command asker) => Change(character, again: false, asker, mayWait: true);

    // The same character again, in the suit that is picked now. Building a switched character's
    // body again in place leaves it in a T-pose, so a suit change comes through here instead.
    public static Outcome Again(Command asker)
    {
        Adopt();
        return Away ? Change(_playing, again: true, asker, mayWait: true) : Outcome.Failed("NOT NOW");
    }

    static Outcome Change(string character, bool again, Command asker, bool mayWait)
    {
        Adopt();
        if (_waiting is not null)
        {
            return Outcome.Unchanged("MAKING ROOM");
        }

        if (World.Player is not { } pawn || World.Controller is not { } controller || !Game.TryGetGameInfo(out var info) || !Game.TryGetGameRI(out var game)
            || !Game.TryGetWorldInfo(out var world) || game.PersistentOptions is not { IsValid: true } options || info.PersistentData is not { IsValid: true } saved)
        {
            return Outcome.NoPlayer;
        }

        if (Refusal(pawn, controller, info, game) is { } refusal)
        {
            return Outcome.Failed(refusal);
        }

        var current = game.PlayerCharacters_0.CharacterName.ToString();
        if (!again && current.Equals(character, StringComparison.OrdinalIgnoreCase))
        {
            return Outcome.Unchanged("ALREADY THEM");
        }

        var story = Away ? _story : current;
        var returning = character.Equals(story, StringComparison.OrdinalIgnoreCase);
        var damage = returning && Away ? _storyDamage : 0;

        RAddContentPlayerCharacter data;
        RAddContentPlayerCharacterMesh suit;

        // The story's character comes back in exactly what they wore. The showcase list does not
        // always name it, like a suit from a mod.
        var mesh = returning && Away && _storyMesh.Length > 0 && Wearable(_storyMesh) ? _storyMesh : Suits.Active(options, character, damage);

        // The body being left stays in memory until the new one has taken over, so both have to
        // fit in the game's object table at once.
        var cost = Packages.Cost<RAddContentPlayerCharacter>(character) + Packages.Cost<RAddContentPlayerCharacterMesh>(mesh);
        var room = ObjectTable.Headroom;
        if (cost > 0 && room < cost + Packages.Spare)
        {
            // A body left earlier may still be in memory. It is cleared out and the switch is
            // tried once more a moment later.
            world.ForceGarbageCollection(true);
            if (mayWait)
            {
                _waiting = (character, again, asker);
                _waitTicks = RoomTicks;
                return Outcome.Done("MAKING ROOM");
            }

            Log.Info($"No room to switch to {character}. It needs {cost} objects and {room} are free.");
            return Outcome.Failed("NOT ENOUGH ROOM");
        }

        try
        {
            data = Packages.Main<RAddContentPlayerCharacter>(character);
            suit = Packages.Main<RAddContentPlayerCharacterMesh>(mesh);
        }
        catch (InvalidOperationException problem)
        {
            Log.Info($"Can't switch to {character}: {problem.Message}.");
            return Outcome.Failed("CAN'T LOAD");
        }

        if (data.PlayerClass is null)
        {
            Log.Warn("switcher.class", $"Can't switch to {character}: their data names no body to play.");
            return Outcome.Failed("CAN'T PLAY THEM");
        }

        // Read before anything changes. The restart fills health and armour.
        var place = pawn.Location;
        var facing = pawn.Rotation;
        var aim = controller.Rotation;
        var speed = pawn.Velocity;
        var airborne = pawn.Physics is Actor.EPhysics.PHYS_Falling or Actor.EPhysics.PHYS_Flying;
        var health = pawn.MaxHealth > 0 ? Math.Clamp(pawn.Health / (float)pawn.MaxHealth, 0f, 1f) : 1f;
        var armour = ArmourOf(pawn);

        if (!Away)
        {
            var held = (pawn.InvManager as RInventoryManager)?.CurrentGadget;
            _story = current;
            _storyDamage = game.PlayerCharacters_0.DamageLevel;
            _storyMesh = game.PlayerCharacters_0.MeshName.ToString() ?? "";
            _storyGadget = held is { IsValid: true } ? held.Class.Name.ToString() : saved.CurrentGadgetName.ToString();
        }

        _playing = character;
        Keep();

        var start = new PlayerStart(place, facing);
        var left = pawn.Ptr;
        try
        {
            ref var slot = ref game.PlayerCharacters_0;
            slot.CharacterName = new FName(character);
            slot.MeshName = GameText.Once(mesh);
            slot.CharacterData = data;
            slot.MeshData = suit;
            slot.DamageLevel = damage;

            var restart = Made.Tracked(new RSeqAct_SwitchPlayerCharacter(world) { PlayerStartPoint = start });
            _switching = true;
            controller.PrepareForPlayerSwitch();

            // A new body picks up the gadget the save names, so the story's character gets back
            // the one they held. Leaving wrote the old body's gadget there.
            if (returning)
            {
                saved.CurrentGadgetName = new FName(_storyGadget);
            }

            restart.RestartPlayer(controller);
        }
        finally
        {
            _switching = false;
            controller.StartSpot = null!;
            start.Destroy();
        }

        // The restart wrote the new character into the save data.
        saved.SetPlayableCharacter(new FName(story));

        if (controller.Pawn is not RPawnPlayer arrived || arrived.Ptr == left)
        {
            Log.Warn("switcher.restart", $"The game did not give a new body for {character}.");
            return Outcome.Failed("FAILED");
        }

        Settle(arrived, controller, place, facing, aim, speed, airborne, health, armour);
        if (returning)
        {
            Forget();
        }

        // The body that left is no longer used by anything, and the game clears it out in its
        // own time. When room is short it is cleared now.
        if (ObjectTable.Headroom < RoomFloor)
        {
            world.ForceGarbageCollection(true);
        }

        // The options still to run this tick should see the new body, not the one that left.
        World.Tick();

        // The suit and battle damage lists are about whoever is played, so they are read again
        // as if the menu had just opened.
        Catalog.MenuOpening();
        Log.Info($"Now playing {character} in {mesh}. It took {cost} objects and {ObjectTable.Headroom} are free.");
        return Outcome.Done(again ? "CHANGED" : "SWITCHED");
    }

    public static Outcome Back(Command asker)
    {
        Adopt();
        return Away ? To(_story, asker) : Outcome.Unchanged("ALREADY THEM");
    }

    static bool Wearable(string mesh) =>
        Game.FindObject<RAddContentPlayerCharacterMesh>($"{mesh}.{mesh}") is { IsValid: true } || GameFiles.HasPackage(mesh);

    // A reload or a death puts the story's character back without asking, and the story can
    // change the player itself. Either way there is nobody left to go back to.
    public static void Tick()
    {
        Adopt();
        if (_waiting is { } waiting && --_waitTicks <= 0)
        {
            _waiting = null;
            waiting.Asker.Report(Change(waiting.Character, waiting.Again, waiting.Asker, mayWait: false));
        }

        if (!Away || _switching || !Game.TryGetGameRI(out var game))
        {
            return;
        }

        ref var slot = ref game.PlayerCharacters_0;
        if (slot.CharacterName.Index == 0 || slot.CharacterData is null)
        {
            return;
        }

        if (!slot.CharacterName.ToString().Equals(_playing, StringComparison.OrdinalIgnoreCase))
        {
            Forget();
        }
    }

    public static void Release() => Keep();

    static string? Refusal(RPawnPlayer pawn, RPlayerController controller, RGameInfo info, RGameRI game)
    {
        if (controller.Pawn is not { } body || body.Ptr != pawn.Ptr)
        {
            return World.Driving is not null ? "GET OUT FIRST" : "NOT NOW";
        }

        if (pawn.bDeleteMe || pawn.Health <= 0 || Loaded.IndexOf(game, pawn) != 0)
        {
            return "NOT NOW";
        }

        if (info.IsChallengeMode())
        {
            return "NOT IN CHALLENGES";
        }

        if (InScene(controller))
        {
            return "IN A SCENE";
        }

        // The story's team fights switch between two bodies and expect to find both.
        if (info.PlayerSwitchPawn is { IsValid: true } partner && partner.Ptr != pawn.Ptr)
        {
            return "PARTNER IS HERE";
        }

        return Remesh.Busy ? "SUIT IS CHANGING" : null;
    }

    // A cutscene, a call or anything else that has taken the player's controls. The free camera
    // takes the move controls itself, so while it flies only the scene signs count.
    public static bool InScene(RPlayerController controller) =>
        controller.bInBink || controller.ControllingDirTrackInst is not null || (controller.IsMoveInputIgnored() && !FreeCamera.TakesMoves)
        || controller.ActiveCinematicMode is { IsValid: true, bHidePlayer: true } or { IsValid: true, bDisableMoveInput: true };

    static (float Melee, float Ballistic) ArmourOf(RPawnPlayer pawn)
    {
        Span<float> share = stackalloc float[ArmourPools];
        var levels = pawn.CurrentArmourLevels;
        var maxima = pawn.CurrentArmourMaxima;
        for (var i = 0; i < Math.Min(ArmourPools, Math.Min(levels.Count, maxima.Count)); i++)
        {
            share[i] = maxima[i] > 0 ? Math.Clamp(levels[i] / (float)maxima[i], 0f, 1f) : 0f;
        }

        return (share[0], share[1]);
    }

    static void Settle(RPawnPlayer arrived, RPlayerController controller, Vector3 place, Rotator facing, Rotator aim, Vector3 speed, bool airborne, float health, (float Melee, float Ballistic) armour)
    {
        arrived.SetLocation(place);
        arrived.SetRotation(facing);
        controller.SetRotation(aim);

        // In the air the new body keeps falling the way the old one moved. The game's own
        // walking state sees the fall and takes it from there.
        if (airborne)
        {
            arrived.SetPhysics(Actor.EPhysics.PHYS_Falling);
            arrived.Velocity = speed;
        }

        // Health and armour go across as the share that was left, since no two characters have
        // the same amount.
        arrived.Health = Math.Max(1, (int)MathF.Round(health * arrived.MaxHealth));
        var maxima = arrived.CurrentArmourMaxima;
        for (var i = 0; i < Math.Min(ArmourPools, maxima.Count); i++)
        {
            if (maxima[i] > 0)
            {
                arrived.SetArmourCurrent((RPawnPlayer.EArmourType)i, (int)MathF.Round((i == 0 ? armour.Melee : armour.Ballistic) * maxima[i]));
            }
        }

        arrived.HealthUpdated();
    }

    static void Forget()
    {
        _story = "";
        _playing = "";
        _storyGadget = "";
        _storyMesh = "";
        _storyDamage = 0;
        Keep();
        Catalog.MenuOpening();
    }

    // A hot reload starts this class over, and the save still has to be kept right after it.
    static void Keep()
    {
        Survive.Set(Survive.SwitchStory, _story);
        Survive.Set(Survive.SwitchPlaying, _playing);
        Survive.Set(Survive.SwitchGadget, _storyGadget);
        Survive.Set(Survive.SwitchDamage, _storyDamage);
        Survive.Set(Survive.SwitchMesh, _storyMesh);
    }

    static void Adopt()
    {
        if (_adopted)
        {
            return;
        }

        _adopted = true;
        _story = Survive.Get(Survive.SwitchStory, "");
        _playing = Survive.Get(Survive.SwitchPlaying, "");
        _storyGadget = Survive.Get(Survive.SwitchGadget, "");
        _storyDamage = Survive.Get(Survive.SwitchDamage, 0);
        _storyMesh = Survive.Get(Survive.SwitchMesh, "");
    }

    // Every save goes through here. While someone else is played, the save data names the
    // story's character and their gadget for the length of the save.
    [Redirect(typeof(RPlayerController), nameof(RPlayerController.SaveGame))]
    static bool SaveGame(RPlayerController self, FString filename, bool optionsOnly, bool memoryOnly)
    {
        RPersistentData? saved = null;
        FName held = default;
        try
        {
            Adopt();
            if (Game.TryGetGameInfo(out var info) && info.PersistentData is { IsValid: true } data)
            {
                if (Away)
                {
                    held = data.CurrentGadgetName;
                    data.SetPlayableCharacter(new FName(_story));
                    data.CurrentGadgetName = new FName(_storyGadget);
                    saved = data;
                }
                else if (Sidekick.Waiting is { } waiting && (waiting.InvManager as RInventoryManager)?.CurrentGadget is { IsValid: true } gadget)
                {
                    // Playing as the sidekick. The swap leaves the save's character alone, but the
                    // gadget it names is whatever the sidekick last picked.
                    held = data.CurrentGadgetName;
                    data.CurrentGadgetName = gadget.Class.Name;
                    saved = data;
                }
            }
        }
        catch (Exception error)
        {
            Log.Error("switcher.save", "The save could not be pointed at the story's character", error);
        }

        try
        {
            return Redirects.RunOriginal<bool>();
        }
        finally
        {
            if (saved is { IsValid: true })
            {
                saved.CurrentGadgetName = held;
            }
        }
    }

    // The story changes the player with this too. When it does, that is who the save is about
    // from then on.
    [Redirect(typeof(RSeqAct_SwitchPlayerCharacter), nameof(RSeqAct_SwitchPlayerCharacter.RestartPlayer))]
    static void RestartPlayer(RSeqAct_SwitchPlayerCharacter self, RPlayerController controller)
    {
        Redirects.RunOriginal();
        if (!_switching)
        {
            Forget();
        }
    }
}
