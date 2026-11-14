#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using View = BmSDK.GameObject.FTPOV;

namespace Knightfall;

// What the shared code needs from one villain: the switch that turns them on and their rows on
// the tab. Fit is their say over the camera's view and BeforeCamera runs ahead of the game's
// camera update, for one that moves the view. Keys adds the hotkeys they come with. Marking says
// whether they have marks in the world to show, which Marks draws in their own style.
sealed record Villain(Toggle Option, Catalog.Section[] Sections, Villain.Fitter? Fit = null, Action? BeforeCamera = null, Action? Keys = null,
    Func<bool>? Marking = null, Action<float>? Marks = null)
{
    public delegate void Fitter(ref View view, float deltaTime);
}

// What every villain on PLAYER > VILLAINS shares. Only one is on at a time. While one is on,
// Batman's gadgets are off, since the villain's kit takes their buttons, and his cape is quiet.
static class Villains
{
    // The game's own switch for gadgets. It stops a gadget being readied or picked, and leaves
    // counters alone.
    static readonly Held<RGameInfo, bool> NoGadgets = new("villain gadgets", game => game.bNoGadgets, (game, off) => game.bNoGadgets = off);

    // No villain has Batman's cape, so its sounds are emptied: the cape's own flutter and rain
    // and the glide's cape loop, which the game adds as each glide starts.
    static readonly Held<RCapeComponent, AkEvent?> CapeFlutter = new("villain cape flutter", cape => cape.GenericCapeSound, (cape, sound) => cape.GenericCapeSound = sound!);
    static readonly Held<RCapeComponent, AkEvent?> CapeRain = new("villain cape rain", cape => cape.CapeRainSoundCentre, (cape, sound) => cape.CapeRainSoundCentre = sound!);
    static readonly Held<RCapeComponent, AkEvent?> CapeRainGliding = new("villain cape rain gliding", cape => cape.CapeRainSoundGlide, (cape, sound) => cape.CapeRainSoundGlide = sound!);
    static readonly Held<RPawnPlayer, AkEvent?> GlideLoop = new("villain glide loop", pawn => pawn.GlideBoostSoundEvent, (pawn, sound) => pawn.GlideBoostSoundEvent = sound!);

    // Thugs pick what they shout by who the player is, and for Batman that is lines about the
    // Bat. The game knows the Joker by name. Nobody has lines about Azrael, so under his name
    // thugs keep to the lines that fit anyone. A woman is still called "her".
    public enum Known
    {
        Stranger,
        Joker,
        Woman,
    }

    static readonly FName JokerName = new("FV_PLAYERNAME_Joker");
    static readonly FName StrangerName = new("FV_PLAYERNAME_Azrael");
    static readonly FName Female = new("FV_PLAYERGENDER_Female");

    static readonly Held<RPawnPlayerCombat, FName> BarkName = new("villain bark name", pawn => pawn.CharacterNameFlag, (pawn, name) => pawn.CharacterNameFlag = name);
    static readonly Held<RPawnPlayerCombat, FName> BarkGender = new("villain bark gender", pawn => pawn.CharacterGender, (pawn, gender) => pawn.CharacterGender = gender);

    static Villain[]? _roster;

    // Every villain, in the order the tab lists them. A new one is added here and nowhere else.
    // Made on first use, since each villain's own rows have to exist before it.
    static Villain[] Roster => _roster ??= new Villain[]
    {
        new(Firefly.Option, Firefly.Sections, Firefly.AimView, Firefly.KeepAimFacing, Firefly.DefaultKeys, Firefly.Marking, Firefly.Marks),
        new(RiddlerMech.Option, RiddlerMech.Sections, RiddlerMech.Fit, Keys: RiddlerMech.DefaultKeys),
        new(MrFreeze.Option, MrFreeze.Sections, MrFreeze.AimView, MrFreeze.KeepAimFacing, Marking: MrFreeze.Marking, Marks: MrFreeze.Marks),
        new(JokerVillain.Option, JokerVillain.Sections, JokerVillain.AimView, JokerVillain.KeepAimFacing, Marking: JokerVillain.Marking, Marks: JokerVillain.Marks),
        new(PoisonIvy.Option, PoisonIvy.Sections, PoisonIvy.AimView, PoisonIvy.KeepAimFacing, Marking: PoisonIvy.Marking, Marks: PoisonIvy.Marks),
        new(ArkhamKnight.Option, ArkhamKnight.Sections, ArkhamKnight.AimView, ArkhamKnight.KeepAimFacing, ArkhamKnight.DefaultKeys, ArkhamKnight.Marking, ArkhamKnight.Marks),
    };

    // PLAYER > VILLAINS, each villain's rows under their own heading.
    public static Catalog.Section[] Sections => Roster.SelectMany(villain => villain.Sections).ToArray();

    public static bool On
    {
        get
        {
            foreach (var villain in Roster)
            {
                if (villain.Option.On)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public static void DefaultKeys()
    {
        foreach (var villain in Roster)
        {
            villain.Keys?.Invoke();
        }
    }

    // Ahead of the game's camera update, every frame.
    public static void BeforeCamera()
    {
        foreach (var villain in Roster)
        {
            villain.BeforeCamera?.Invoke();
        }
    }

    // Whether the villain who is on has marks in the world to show, like where a move will land
    // while they aim.
    public static bool Marking()
    {
        foreach (var villain in Roster)
        {
            if (villain.Option.On && villain.Marking?.Invoke() == true)
            {
                return true;
            }
        }

        return false;
    }

    public static void Marks(float time)
    {
        foreach (var villain in Roster)
        {
            if (villain.Option.On && villain.Marking?.Invoke() == true)
            {
                villain.Marks?.Invoke(time);
            }
        }
    }

    // Each villain's say over the view the game's camera worked out. One that is off leaves it.
    public static void Fit(ref View view, float deltaTime)
    {
        foreach (var villain in Roster)
        {
            villain.Fit?.Invoke(ref view, deltaTime);
        }
    }

    // The villain turned on last wins. The others are switched off. False means one of them was
    // turned on after this one, so this one should switch itself off.
    public static bool Claim(Toggle mine)
    {
        foreach (var villain in Roster)
        {
            var other = villain.Option;
            if (ReferenceEquals(other, mine) || !other.On)
            {
                continue;
            }

            if (other.TouchedAt > mine.TouchedAt)
            {
                return false;
            }

            other.SetTo(false);
        }

        return true;
    }

    // How long a line is given to play before the speaker goes back to the villain's own voice.
    const float LineSeconds = 8f;

    static string? _voicePath;
    static AkDialogueVoice? _voice;

    // The player speaks with the villain's voice the whole time, not only for his lines. The
    // game picks its own sounds for a speaker by voice, like the grunt when he is hit, so those
    // are his own where he has them and silent where he doesn't, and never Batman's.
    static void Speak(RPawnPlayer player, string? path)
    {
        if (path is null || player.GetAkDialogueComponent(false) is not { IsValid: true } speaker)
        {
            return;
        }

        if (path != _voicePath || _voice is not { IsValid: true })
        {
            _voicePath = path;
            _voice = Game.FindObject<AkDialogueVoice>(path);
        }

        var since = World.Now - VillainVoice.SpokeAt;
        if (_voice is { IsValid: true } voice && (since < 0f || since > LineSeconds))
        {
            VillainVoice.Speaker.Hold(speaker, voice);
        }
    }

    static int _epoch = -1;

    // A new world has a new player, and its clock starts over. The villain being played is
    // switched off and on again there, so its waits, effects and kit all start fresh. Everything
    // it held from the last world reads as gone by then.
    public static void Watch()
    {
        var epoch = World.Epoch;
        if (epoch == _epoch)
        {
            return;
        }

        var first = _epoch < 0;
        _epoch = epoch;
        if (first)
        {
            return;
        }

        foreach (var villain in Roster)
        {
            if (villain.Option.On && villain.Option.Restore is { } restart)
            {
                Kernel.Run(villain.Option.Id, restart);
            }
        }
    }

    // Every tick while a villain is on and on foot.
    public static void Keep(RPawnPlayer player, RPlayerController controller, string? voice = null, Known known = Known.Stranger)
    {
        Hush(player);
        Speak(player, voice);
        if (player is RPawnPlayerCombat fighter)
        {
            BarkName.Hold(fighter, known == Known.Joker ? JokerName : StrangerName);
            if (known == Known.Woman)
            {
                BarkGender.Hold(fighter, Female);
            }
        }

        if (World.Info is { IsValid: true } game)
        {
            NoGadgets.Hold(game, true);
        }

        // The aim button also raises a flag the gadget in hand reads for itself, past the
        // switch above. It is put down each tick before the game looks. The button's own
        // presses still reach the game, so counters and takedowns on it are untouched.
        if (controller.PlayerInput is RPlayerInput { IsValid: true } input)
        {
            if (input.bReadyGadgetButton != 0)
            {
                input.bReadyGadgetButton = 0;
            }

            if (input.aLeftTrigger != 0f)
            {
                input.aLeftTrigger = 0f;
            }
        }
    }

    public static void Release()
    {
        NoGadgets.Release();
        BarkName.Release();
        BarkGender.Release();
        VillainVoice.Speaker.Release();
        CapeFlutter.Release();
        CapeRain.Release();
        CapeRainGliding.Release();
        GlideLoop.Release();
    }

    // Batman's idle fidgets, like straightening his gauntlet once a fight is over, play over the
    // body and cut into a villain's own pose. The game asks this each tick and holds them off
    // the same way during a cutscene, so while a villain is on the answer is always blocked.
    [Redirect(typeof(RPlayerController), nameof(RPlayerController.GetIdleOverlaysBlocked))]
    static RAnimUtil_PosePlayer.ERandomOverlayBlockType GetIdleOverlaysBlocked(RPlayerController self)
    {
        try
        {
            return On ? RAnimUtil_PosePlayer.ERandomOverlayBlockType.ROBT_AllBlocked : self.GetIdleOverlaysBlocked();
        }
        catch (Exception error)
        {
            Log.Error("villains.idle_overlays", "The idle fidget check failed", error);
            return RAnimUtil_PosePlayer.ERandomOverlayBlockType.ROBT_None;
        }
    }

    // When a fight ends the game starts its finished-fight move, a flourish like straightening
    // Batman's gauntlet that also ends a villain's aim. It isn't an idle fidget, so the block
    // above never reaches it. While a villain is on it isn't started.
    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.SpawnFinishedCombatMove))]
    static void SpawnFinishedCombatMove(RPlayerControllerCombat self)
    {
        try
        {
            if (!On)
            {
                self.SpawnFinishedCombatMove();
            }
        }
        catch (Exception error)
        {
            Log.Error("villains.finished_combat", "The finished-fight move check failed", error);
        }
    }

    // When a gunman spots Batman the game slows time, sends a flurry of bats past the camera and
    // offers a smoke pellet, a batarang or the grapnel. None of that is a villain's, so while one
    // is on a gunman who spots him just opens fire.
    [Redirect(typeof(RPlayerController), nameof(RPlayerController.SpottedDuringPredator))]
    static void SpottedDuringPredator(RPlayerController self, RPawnVillain spotter)
    {
        if (!On)
        {
            Redirects.RunOriginal();
        }
    }

    // Under fire the game also offers Batman's smoke pellet, with or without the slowed time.
    [Redirect(typeof(RSmokeBomb), nameof(RSmokeBomb.CanShowUnderFireHelpPrompt))]
    static bool CanShowUnderFireHelpPrompt(RSmokeBomb self)
    {
        try
        {
            return !On && self.CanShowUnderFireHelpPrompt();
        }
        catch (Exception error)
        {
            Log.Error("villains.smoke_prompt", "The smoke pellet prompt check failed", error);
            return false;
        }
    }

    // A glide already going has added its loop, and the game takes off whatever the field holds
    // when the glide ends. So a playing loop is taken off here before the field is emptied.
    static void Hush(RPawnPlayer player)
    {
        if (player is RPawnPlayerBmBase { CapeComponent: { IsValid: true } cape })
        {
            CapeFlutter.Hold(cape, (AkEvent?)null);
            CapeRain.Hold(cape, (AkEvent?)null);
            CapeRainGliding.Hold(cape, (AkEvent?)null);
        }

        if (player.GlideBoostSoundEvent is { IsValid: true } loop && player.bGliding)
        {
            player.RemoveLoopingAudioEvent(loop);
        }

        GlideLoop.Hold(player, (AkEvent?)null);
    }

    // The game's hit, heavy enough to stagger. Push is the way to throw them, and force how hard.
    // One that should go down is put down the ways SUPER PUNCH does, and pushed along. True when
    // they went down. Someone riding in a vehicle stays in it.
    public static bool Knock(RPawnPlayer player, RPawnVillain thug, int damage, Vector3 push, float force, bool fall)
    {
        if (thug.DrivenVehicle is { IsValid: true } || thug.Base is Vehicle)
        {
            return false;
        }

        thug.TakeDamage(damage, player.Controller, thug.Location, push * (fall ? force : force * 0.2f), RDmgType_Explosion.StaticClass(), default, player);
        if (!fall || !thug.IsValid || thug.bDeleteMe || thug.IsRagdoll())
        {
            return false;
        }

        var info = new RPawnCombat.FDamageInfo
        {
            Attacker = player,
            DamageAmount = damage,
            DamageType = RDmgType_Explosion.StaticClass(),
            bHeavyHit = true,
        };

        thug.PlayRagdoll(info, RGameInfo.RagdollStunType.RAGSTUN_GlideBoostTackle, false);
        if (!thug.IsRagdoll())
        {
            thug.GenericGoRagdoll();
        }

        if (thug.IsRagdoll() && thug.Mesh is { IsValid: true } mesh)
        {
            mesh.WakeRigidBody(FName.None);
            mesh.SetRBLinearVelocity(push * force, true);
        }

        return true;
    }

    // Whether a line goes through a thug's body, an upright cylinder made this much bigger by
    // slack. Enter and leave are how far along the line that is. Checking only the line's nearest
    // point to his middle misses shots from above that go through his head or shoulders.
    public static bool Crosses(RPawnVillain thug, Vector3 from, Vector3 way, float slack, out float enter, out float leave)
    {
        var radius = (thug.CylinderComponent is { IsValid: true } body ? body.CollisionRadius : 35f) + slack;
        var height = (thug.CylinderComponent is { IsValid: true } tall ? tall.CollisionHeight : 90f) + slack;
        var off = from - thug.Location;
        enter = 0f;
        leave = float.MaxValue;

        // Seen from above, the stretch of the line inside his circle.
        var flat = way.X * way.X + way.Y * way.Y;
        var outside = off.X * off.X + off.Y * off.Y - radius * radius;
        if (flat < 1e-8f)
        {
            if (outside > 0f)
            {
                return false;
            }
        }
        else
        {
            var half = off.X * way.X + off.Y * way.Y;
            var root = half * half - flat * outside;
            if (root < 0f)
            {
                return false;
            }

            root = MathF.Sqrt(root);
            enter = MathF.Max(enter, (-half - root) / flat);
            leave = MathF.Min(leave, (-half + root) / flat);
        }

        // And the stretch between his feet and the top of his head.
        if (MathF.Abs(way.Z) < 1e-4f)
        {
            if (MathF.Abs(off.Z) > height)
            {
                return false;
            }
        }
        else
        {
            var low = (-height - off.Z) / way.Z;
            var high = (height - off.Z) / way.Z;
            enter = MathF.Max(enter, MathF.Min(low, high));
            leave = MathF.Min(leave, MathF.Max(low, high));
        }

        return enter <= leave;
    }

    // As the game's guns do when fired: thugs in earshot see the shooter at once, and the nearest
    // fight joins in.
    public static void Alert(RPawnPlayer player, Vector3 from, float hearing)
    {
        RBMAIController? last = null;
        foreach (var thug in Crowd.StandingEnemies())
        {
            if (Vector3.DistanceSquared(thug.Location, from) > hearing * hearing || thug.Controller is not RBMAIController { IsValid: true } brain)
            {
                continue;
            }

            if (brain.FindAlertFor(player) is { IsValid: true } alert)
            {
                alert.SightLevel = brain.SpottedLimit;
                alert.UpdateAlert(player.Location, 0f);
            }

            last = brain;
        }

        if (last is not null && player.V2AttackCoord is { IsValid: true } fight)
        {
            fight.AlertAndAddAttacker(last, false, (RAEC_Attack_Base.PredAttackEscalationType)1);
        }
    }

    // An effect from the game's pool, spawned once and kept where it is wanted. The pool hands
    // a finished effect to someone else, so one that shows another effect now is let go.
    public static void Show(ref Emitter? effect, ParticleSystem? look, Vector3 at, Rotator turn, Vector3? end = null)
    {
        if (look is not { IsValid: true })
        {
            return;
        }

        if (effect is { IsValid: true } && effect.ParticleSystemComponent is { IsValid: true } held && held.Template is { } shows && shows.Ptr != look.Ptr)
        {
            effect = null;
        }

        if (effect is not { IsValid: true })
        {
            effect = Game.TryGetWorldInfo(out var world) && world.GRI?.ObjectPool is { IsValid: true } pool ? pool.SpawnEmitter(look, at, turn, 0, 1f) : null;
        }

        if (effect is not { IsValid: true })
        {
            return;
        }

        effect.SetLocation(at);
        effect.SetRotation(turn);

        // A beam that is told where it ends reaches there. One that isn't runs out along its turn.
        if (end is { } point && effect.ParticleSystemComponent is { IsValid: true } beam)
        {
            var emitters = look.Emitters.Count;
            for (var i = 0; i < emitters; i++)
            {
                beam.SetBeamEndPoint(i, point);
                beam.SetBeamTargetPoint(i, point, 0);
            }
        }
    }

    public static void PutOut(ref Emitter? effect)
    {
        if (effect is { IsValid: true } && effect.ParticleSystemComponent is { IsValid: true } shown)
        {
            shown.DeactivateSystem();
        }

        effect = null;
    }

    // Vehicles near a spot take a hit as an explosion, which is what their armour rules are
    // written for. Street cars, drones and tanks lose a share of their full health. A parked car
    // is set alight the way the riots do it, then damaged, unless parked is 0, which leaves
    // parked cars alone. Hit gathers each vehicle struck, so a long attack only hits it once.
    // Returns how many were hit.
    public static int Wreck(RPawnPlayer player, Vector3 at, float reach, float share, int parked, HashSet<nint>? hit = null)
    {
        var count = 0;
        foreach (var car in Game.FindObjects<RVehicleNPC>())
        {
            if (car is not { IsValid: true, IsClassDefaultObject: false, bDeleteMe: false, bHidden: false, DroneHacked: false, IsFriendly: false, Health: > 0 }
                || !Near(car.Location, at, reach) || (hit is not null && !hit.Add(car.Ptr)))
            {
                continue;
            }

            var damage = Math.Max(1, (int)MathF.Round(Math.Max(car.Health, car.HealthMax) * share));
            car.TakeDamage(damage, player.Controller, car.Location, Push(car.Location, at), RDmgType_Explosion.StaticClass(), default, player);
            count++;
        }

        if (parked <= 0)
        {
            return count;
        }

        foreach (var car in Game.FindObjects<RAbandonedVehicle>())
        {
            if (car is not { IsValid: true, IsClassDefaultObject: false, bDeleteMe: false, bHidden: false, Health: > 0 }
                || !Near(car.Location, at, reach) || (hit is not null && !hit.Add(car.Ptr)))
            {
                continue;
            }

            if (!car.bOnFire)
            {
                car.MolotovExplode();
            }

            car.TakeDamage(parked, player.Controller, car.Location, Push(car.Location, at), RDmgType_Explosion.StaticClass(), default, player);
            count++;
        }

        return count;
    }

    // Everything in the world that breaks, gathered at most this often, since a beam asks every
    // tick.
    const float BreakablesEvery = 0.5f;
    const int BreakDamage = 1000;
    const float BreakPush = 500f;

    // The falloff the game's own explosions pass.
    const float Falloff = 1f;

    // Smashable props and TVs already broken. There is nothing left of them to break.
    static readonly HashSet<nint> Broken = new();
    static readonly HashSet<nint> Present = new();
    static readonly List<Actor> Breakables = new();
    static float _nextBreakables;

    // Breaks what is near a spot the way the game breaks each kind. Street furniture and other
    // destructibles take an explosion's radius damage. Weak walls only break for the damage types
    // made to break them, like the explosive gel's. Smashable props and TVs break as a batarang
    // breaks them. Sentry guns blow up and turrets shut down. Hit gathers each one struck, so a
    // long attack only breaks it once. Returns how many were hit.
    //
    // A destructible's plain damage reads which piece was hit, and given no piece it crashes the
    // game. Radius damage picks the pieces itself, which is how explosions break them. It is
    // given no causer, as an explosion's causer is its projectile, never a person.
    public static int Break(RPawnPlayer player, Vector3 at, float reach, HashSet<nint>? hit = null)
    {
        if (World.Now >= _nextBreakables)
        {
            _nextBreakables = World.Now + BreakablesEvery;
            Breakables.Clear();
            Gather<ApexDestructibleActor>();
            Gather<FracturedStaticMeshActor>();
            Gather<RSmashableProp>();
            Gather<RBreakableTV>();
            Gather<RSentryGunBase>();
            Gather<RTurretBase>();

            // Something unloaded can have its place taken by something new.
            Present.Clear();
            foreach (var thing in Breakables)
            {
                Present.Add(thing.Ptr);
            }

            Broken.RemoveWhere(pointer => !Present.Contains(pointer));
        }

        var count = 0;
        foreach (var thing in Breakables)
        {
            if (!thing.IsValid || thing.bDeleteMe || !Near(thing.Location, at, reach) || (hit is not null && !hit.Add(thing.Ptr)))
            {
                continue;
            }

            var push = Push(thing.Location, at) * 10f;
            switch (thing)
            {
                case RSentryGunBase gun:
                    if (gun.bGoneRagdoll)
                    {
                        continue;
                    }

                    gun.Explode(true);
                    if (!gun.bGoneRagdoll)
                    {
                        gun.GoPhysicsAndTriggerEvent();
                    }

                    break;
                case RTurretBase turret:
                    if (turret.bDisabled)
                    {
                        continue;
                    }

                    turret.Disrupted();
                    break;
                case RSmashableProp prop:
                    if (!Broken.Add(prop.Ptr))
                    {
                        continue;
                    }

                    prop.BreakProp();
                    break;
                case RBreakableTV screen:
                    if (!Broken.Add(screen.Ptr))
                    {
                        continue;
                    }

                    screen.TakeDamage(1, player.Controller, at, push, RDmgType_Batarang.StaticClass(), default, player);
                    break;

                // A weak wall breaks a part at a time, so it can be hit again while it stands.
                case RFractureWallBase wall:
                    if (!wall.bCollideActors)
                    {
                        continue;
                    }

                    wall.TakeRadiusDamage(player.Controller, BreakDamage, reach + VehicleSize, RDmgType_ExplosiveGel.StaticClass(), BreakPush, at, true, null!, Falloff);
                    break;
                // A prop damaged for the first time in a stage sets its mesh falling, and the game
                // never checks it has one. A prop drawn by its destructible part alone has none,
                // and damaging it crashes the game, so it is left alone.
                case RDestructibleProp prop when prop.MeshComp is not { IsValid: true } || !TakesExplosions(prop) || prop.Health <= 0f:
                    continue;
                case ApexDestructibleActor or FracturedStaticMeshActor:
                    thing.TakeRadiusDamage(player.Controller, BreakDamage, reach + VehicleSize, RDmgType_Explosion.StaticClass(), BreakPush, at, true, null!, Falloff);
                    break;
                default:
                    continue;
            }

            count++;
        }

        return count;
    }

    // A prop can list the only damage that breaks it, like a lamp post only the Batmobile knocks
    // down. Those are left alone.
    static bool TakesExplosions(RDestructibleProp prop)
    {
        var listed = prop.VulnerableDamageTypes;
        if (listed.Count == 0)
        {
            return true;
        }

        var explosion = RDmgType_Explosion.StaticClass();
        for (var i = 0; i < listed.Count; i++)
        {
            if (listed[i] is { IsValid: true } kind && kind.Ptr == explosion.Ptr)
            {
                return true;
            }
        }

        return false;
    }

    static void Gather<T>()
        where T : Actor
    {
        foreach (var thing in Game.FindObjects<T>())
        {
            if (thing is { IsValid: true, IsClassDefaultObject: false, bDeleteMe: false, bHidden: false })
            {
                Breakables.Add(thing);
            }
        }
    }

    // A vehicle's middle sits well inside it, so it is reached this much further.
    const float VehicleSize = 200f;

    static bool Near(Vector3 car, Vector3 at, float reach)
    {
        var off = car - at;
        var most = reach + VehicleSize;
        return off.X * off.X + off.Y * off.Y <= most * most && MathF.Abs(off.Z) <= most;
    }

    static Vector3 Push(Vector3 car, Vector3 at)
    {
        var away = car - at;
        away.Z = 0f;
        return away.LengthSquared() > 1f ? Vector3.Normalize(away) * 100f : Vector3.Zero;
    }

    // The switch alone isn't enough. These four reach the gadget in hand with no check of it,
    // so they are skipped while a villain is on.
    [Redirect(typeof(RPlayerController), nameof(RPlayerController.ReadyGadget))]
    static void ReadyGadget(RPlayerController self, bool cantQuickThrow)
    {
        if (!On)
        {
            Redirects.RunOriginal();
        }
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.GadgetPressed))]
    static void GadgetPressed(RPlayerController self)
    {
        if (!On)
        {
            Redirects.RunOriginal();
        }
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.SecondaryFireGadget))]
    static void SecondaryFireGadget(RPlayerController self)
    {
        if (!On)
        {
            Redirects.RunOriginal();
        }
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.FireQuickGadgetType))]
    static void FireQuickGadgetType(RPlayerController self, RPawnPlayer.EQuickGadgetType gadgetType, bool overrideCombatMove)
    {
        if (!On)
        {
            Redirects.RunOriginal();
        }
    }
}
