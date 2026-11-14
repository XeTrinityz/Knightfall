#nullable enable

using System.Numerics;
using System.Runtime.CompilerServices;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using BmSDK.Framework;

namespace Knightfall;

// Firefly's flamethrower and fire grenade, on the gadget buttons. Batman's gadgets are switched
// off while he is Firefly, so the aim button readies nothing of his.
//
// The game has no burning, so the flame and the fire knock thugs down with the game's own hits
// and ragdolls. His grenade is his real one from his story level. It only ever hurts the
// Batmobile, so its hit on thugs is added here when its fire bursts out.
static partial class Firefly
{
    // How far the flame reaches, how wide, and how often it hits.
    const float FlameReach = 1400f;
    const float FlameCone = 0.92f;
    const float FlameEvery = 0.2f;
    const float FlameDamage = 1f;

    // Seconds in the flame before a thug goes down.
    const float BurnToFall = 0.8f;

    // The flame comes from in front of his hands. The game has no flame jet, so the stream is
    // made of fire that trails behind what carries it, like a burning car's or a thrown
    // molotov's. Many small ones a second fly from the gun to the flame's reach, each leaving
    // its trail. His flame burst is over in a blink, so it only lights the stream.
    const float NozzleAhead = 60f;
    const float IgniteSize = 1f;
    const float DartEvery = 0.06f;
    const float DartSpeed = 2400f;
    const float DartSize = 0.5f;

    // The first of these the game has loaded is the fire the stream is made of.
    static readonly string[] StreamFires =
    {
        "VFX_Vehicles.Particles.Damage_States.FireTrail_Car",
        "VFX_Riot.Particles.Molotov_Bottle_Fire",
        "VFX_Riot.Particles.Vehicle.Fire_Large",
    };

    // The grenade starts this far out from his hands, clear of his own body.
    const float ThrowAhead = 150f;

    // The grenade's throw, its wait between throws, and the reach of its fire.
    const float ThrowSpeed = 2500f;
    const float ThrowLift = 0.15f;
    const float ThrowWait = 1.5f;
    // His own grenade's blast, damage and push, as his chase sets them on each one.
    const float BlastRadius = 220f;
    const float BlastImpulse = 15000f;
    const float FireReach = BlastRadius + 60f;
    const float FireEvery = 0.5f;
    const float BlastPush = 900f;

    // Fire burns an enemy vehicle itself, and leaves the people in it in their seats. Each hit
    // takes this share of the health it had when it first caught, so a few seconds of fire
    // finishes it. The game's own rules still decide which vehicles fire can hurt at all.
    const float CarBurnShare = 0.08f;
    const float CarBurnEvery = 0.25f;

    // The fire a burning vehicle carries. The riot's burning car fire first, since it burns
    // standing still, then whatever the stream is made of.
    const string CarFirePath = "VFX_Riot.Particles.Vehicle.Fire_Large";

    // A vehicle is bigger than a person, so the fire reaches it from this much further off.
    const float CarRoom = 250f;

    // A burning vehicle shows the game's car fire for this long after its last hit, this big,
    // this far above its middle.
    const float CarFireSeconds = 4f;
    const float CarFireSize = 1.5f;
    const float CarFireLift = 60f;

    // How often the list of thugs is taken again while the flame is on.
    const float LookEvery = 1f;

    const string GrenadeClassPath = "BmScript.RProjectile_GrenadeFirefly";
    const string GrenadeFirePath = "EFX_City.City_Firefly.Particles.Firefly_Grenade";
    const string LaunchSoundPath = "Ww_CHR_Firefly.Play_Firefly_Grenade_Launch";
    const string FireSoundPath = "ww_vfx_fireheavy.Play_VFX_FireHeavy";

    static readonly FName Hands = new("Gundummy");

    // His flamethrower comes with his look's props. It has no muzzle socket, and its bones are
    // only its grip and the grenade launcher. So its barrel is taken as its longest side, and the
    // tip as that side's end away from the grip, both in the gun's own space.
    const string GunMesh = "Firefly_Flamethrower";
    static readonly FName GunGrip = new("Flamethrower_main");
    static SkeletalMeshComponent? _gun;
    static nint _gunTried;
    static Vector3 _tipInGun;
    static Vector3 _barrelInGun;

    static readonly VillainAim Aim = new();
    static readonly Func<Vector3, Vector3> PointAt = from => Aim.PointFor(from);

    static bool _flaming;
    static bool _secondaryWas;
    static float _nextFlameHit;
    static float _nextThrow;
    static float _nextLook;

    sealed class Alight
    {
        public required RVehicleNPC Car;
        public Emitter? Fire;
        public float Until;
        public float NextDamage;
        public int Full;
    }

    static ParticleSystem? _carFire;

    static readonly List<RVehicleNPC> Cars = new();

    // Parked cars are props, not vehicles anyone drives. They burn the way the riots set them
    // alight, with the game's own molotov fire, and blow up once the fire's damage runs out
    // their health, as explosion damage makes them.
    static readonly List<RAbandonedVehicle> Parked = new();
    static readonly Dictionary<nint, float> ParkedNext = new();
    const int ParkedDamage = 10;
    static readonly Dictionary<nint, Alight> OnFire = new();

    static Class? _grenadeClass;
    static AkEvent? _launchSound;
    static AkEvent? _fireSound;
    sealed class Dart
    {
        public required Emitter Fire;
        public Vector3 At;
        public Vector3 Speed;
        public float Until;
    }

    static readonly List<Dart> Darts = new();
    static ParticleSystem? _streamFire;
    static float _nextDart;
    static RPawnPlayer? _fireOn;

    static readonly List<RPawnVillain> Thugs = new();
    static readonly Dictionary<nint, float> Burned = new();

    sealed class Thrown
    {
        public required Actor Grenade;
        public bool Burst;
        public bool Landed;
        public float NextHit;
        public Vector3 At;
        public Vector3 Last;
    }

    // Landing, the game moves a grenade it didn't throw to the middle of the world, since its own
    // throw sets where it sticks. It is put back where it was flying when that happens.
    const float Jumped = 500f;

    static readonly List<Thrown> Grenades = new();

    static void LoadWeapons()
    {
        _grenadeClass ??= Kept(Game.FindObject<Class>(GrenadeClassPath));
        _launchSound ??= Kept(Game.FindObject<AkEvent>(LaunchSoundPath));
        _fireSound ??= Kept(Game.FindObject<AkEvent>(FireSoundPath));
    }

    // True while he aims in the air, so the crouch button fires instead of taking him down.
    static bool Aiming => Aim.On;

    public static void KeepAimFacing() => Aim.Hold();

    // His jets are wide, so his camera sits further back than Batman's, and further again in a
    // flight. It stops short of a wall behind it.
    const float FlightBack = 1.5f;
    const float NearestView = 550f;
    const float BackEase = 0.4f;
    const float WallRoom = 25f;
    static float _back;

    public static void AimView(ref BmSDK.GameObject.FTPOV view, float deltaTime)
    {
        var worn = Option.On && World.Driving is null && World.Player is { IsValid: true } && _body is not null;
        var wantedBack = worn ? (float)CameraBack.Value * Teleport.UnitsPerMetre * (_flying ? FlightBack : 1f) : 0f;

        // The game's walking camera sits much closer than its running and fighting ones. So it
        // is also never let nearer to him than this, which only the walking camera is.
        if (worn && World.Player is { IsValid: true } walker)
        {
            wantedBack = MathF.Max(wantedBack, NearestView - Vector3.Distance(view.Location, walker.Location));
        }
        _back += (wantedBack - _back) * Settle(deltaTime, BackEase);
        if (_back > 1f && World.Player is { IsValid: true } wearer)
        {
            var behind = view.Location - view.Rotation.ToDirection() * _back;
            view.Location = Crosshair.Line(wearer, view.Location, behind, Crosshair.Meets.Level, out var wall)
                ? wall.Spot + view.Rotation.ToDirection() * WallRoom
                : behind;
        }

        Aim.View(ref view, deltaTime);
    }

    static void Weapons(RPawnPlayer player, RPlayerController controller, bool airborne)
    {
        LoadWeapons();

        var now = World.Now;
        Aim.Update(player, controller, airborne);
        var secondary = Aim.Secondary;
        var target = Aim.Target;

        Flame(player, Aim.Fire, target);
        if (secondary && !_secondaryWas && now >= _nextThrow)
        {
            Throw(player, target);
            _nextThrow = now + ThrowWait;
        }

        _secondaryWas = secondary;
        Burning(player);
        CarFires();
    }

    // Where the flame and the grenade leave him. The tip of his flamethrower while he carries it,
    // else just in front of his hands.
    static Vector3 Nozzle(RPawnPlayer player, Vector3 target)
    {
        if (Barrel() is { } barrel)
        {
            return barrel.From;
        }

        var hands = FireflyBody() is { } body ? body.GetBoneLocation(Hands, 0) : player.Location;
        var way = target - hands;
        return way.LengthSquared() > 1f ? hands + Vector3.Normalize(way) * NozzleAhead : hands;
    }

    // The tip of his flamethrower and the way it points, or nothing while he isn't carrying it.
    static (Vector3 From, Vector3 Way)? Barrel()
    {
        if (Gun() is not { HiddenGame: false } gun)
        {
            return null;
        }

        var place = Unsafe.As<GameObject.FMatrix, Matrix4x4>(ref gun.LocalToWorld);
        var way = Vector3.TransformNormal(_barrelInGun, place);
        if (way.LengthSquared() < 1e-6f)
        {
            return null;
        }

        return (Vector3.Transform(_tipInGun, place), Vector3.Normalize(way));
    }

    // The flamethrower his look carries, measured once per gun.
    static SkeletalMeshComponent? Gun()
    {
        if (Disguise.Carrying(GunMesh) is not { } gun)
        {
            _gun = null;
            return null;
        }

        if (_gunTried != gun.Ptr)
        {
            _gunTried = gun.Ptr;
            _gun = Measure(gun) ? gun : null;
        }

        return _gun;
    }

    static bool Measure(SkeletalMeshComponent gun)
    {
        if (gun.SkeletalMesh is not { IsValid: true } mesh)
        {
            return false;
        }

        var bounds = mesh.Bounds;
        var middle = bounds.Origin;
        var size = bounds.BoxExtent;
        var side = size.X >= size.Y && size.X >= size.Z ? Vector3.UnitX : size.Y >= size.Z ? Vector3.UnitY : Vector3.UnitZ;
        var half = Vector3.Dot(size, side);
        if (half < 1f)
        {
            return false;
        }

        var grip = gun.MatchRefBone(GunGrip) >= 0 ? gun.GetBoneLocation(GunGrip, 1) : Vector3.Zero;
        var ends = (middle + side * half, middle - side * half);
        var forward = Vector3.DistanceSquared(ends.Item1, grip) >= Vector3.DistanceSquared(ends.Item2, grip);
        _tipInGun = forward ? ends.Item1 : ends.Item2;
        _barrelInGun = forward ? side : -side;
        return true;
    }

    static void Flame(RPawnPlayer player, bool on, Vector3 target)
    {
        if (on)
        {
            var from = Nozzle(player, target);
            var way = Aim.PointFor(from) - from;
            if (way.LengthSquared() > 1f && Game.TryGetWorldInfo(out var world) && world.GRI?.ObjectPool is { IsValid: true } pool)
            {
                var along = Vector3.Normalize(way);
                var turn = Rotator.FromDirection(along);
                if (!_flaming && _exhaust is { IsValid: true } burst)
                {
                    pool.SpawnEmitter(burst, from, turn, 0, IgniteSize);
                }

                if (World.Now >= _nextDart && StreamFire() is { } fire && pool.SpawnEmitter(fire, from, turn, 0, DartSize) is { IsValid: true } dart)
                {
                    _nextDart = World.Now + DartEvery;
                    Darts.Add(new Dart { Fire = dart, At = from, Speed = along * DartSpeed + player.Velocity, Until = World.Now + FlameReach / DartSpeed });
                }
            }

            FireSound(player, true);
            if (World.Now >= _nextFlameHit)
            {
                _nextFlameHit = World.Now + FlameEvery;
                Scorch(player, from, way);
            }
        }
        else if (_flaming)
        {
            FireSound(player, false);
        }

        if (on && !_flaming)
        {
            Voice.Speak("taunt");
        }

        _flaming = on;
        Stream(player);
    }

    static ParticleSystem? StreamFire()
    {
        if (_streamFire is { IsValid: true })
        {
            return _streamFire;
        }

        foreach (var path in StreamFires)
        {
            if (Kept(Game.FindObject<ParticleSystem>(path)) is { } fire)
            {
                return _streamFire = fire;
            }
        }

        return null;
    }

    // Each dart flies on until the flame's reach or a wall, then is left to burn out.
    static void Stream(RPawnPlayer player)
    {
        var delta = World.Delta;
        for (var i = Darts.Count - 1; i >= 0; i--)
        {
            var dart = Darts[i];
            var next = dart.At + dart.Speed * delta;
            var spent = !dart.Fire.IsValid || dart.Fire.bHidden || World.Now >= dart.Until || !player.FastTrace(next, dart.At, Vector3.Zero, false);
            if (spent)
            {
                Burnt(dart);
                Darts.RemoveAt(i);
                continue;
            }

            dart.At = next;
            dart.Fire.SetLocation(next);
        }
    }

    static void Burnt(Dart dart)
    {
        if (dart.Fire.IsValid && !dart.Fire.bHidden && dart.Fire.ParticleSystemComponent is { IsValid: true } effect)
        {
            effect.DeactivateSystem();
        }
    }

    static void FireSound(RPawnPlayer? player, bool on)
    {
        var wanted = on && player is { IsValid: true } && _fireSound is { IsValid: true } ? player : null;
        if (ReferenceEquals(wanted, _fireOn))
        {
            return;
        }

        if (_fireOn is { IsValid: true } was && _fireSound is { IsValid: true } loop)
        {
            was.RemoveLoopingAudioEvent(loop);
        }

        _fireOn = wanted;
        wanted?.AddLoopingAudioEvent(_fireSound!);
    }

    // Thugs in the flame's cone that it can reach take a hit each time, and go down once they
    // have been in it long enough.
    static void Scorch(RPawnPlayer player, Vector3 from, Vector3 way)
    {
        if (way.LengthSquared() < 1f)
        {
            return;
        }

        var along = Vector3.Normalize(way);
        foreach (var thug in Nearby())
        {
            var to = thug.Location - from;
            var distance = to.Length();
            if (distance > FlameReach || distance < 1f || Vector3.Dot(to / distance, along) < FlameCone || !player.FastTrace(thug.Location, from, Vector3.Zero, false))
            {
                continue;
            }

            Burned.TryGetValue(thug.Ptr, out var burned);
            burned += FlameEvery;
            Burned[thug.Ptr] = burned;
            Hurt(player, thug, along, burned >= BurnToFall);
        }

        foreach (var car in Cars)
        {
            var to = car.Location - from;
            var distance = to.Length();
            if (distance <= FlameReach + CarRoom && distance > 1f && Vector3.Dot(to / distance, along) >= FlameCone)
            {
                Ignite(player, car, along);
            }
        }

        foreach (var car in Parked)
        {
            var to = car.Location - from;
            var distance = to.Length();
            if (distance <= FlameReach + CarRoom && distance > 1f && Vector3.Dot(to / distance, along) >= FlameCone)
            {
                Torch(player, car, along);
            }
        }
    }

    static void Torch(RPawnPlayer player, RAbandonedVehicle car, Vector3 push)
    {
        if (!car.IsValid || car.bDeleteMe || car.Health <= 0)
        {
            return;
        }

        if (!car.bOnFire)
        {
            car.MolotovExplode();
            _carsLit++;
        }

        if (ParkedNext.TryGetValue(car.Ptr, out var next) && World.Now < next)
        {
            return;
        }

        ParkedNext[car.Ptr] = World.Now + CarBurnEvery;
        car.TakeDamage(ParkedDamage, player.Controller, car.Location, push * 100f, RDmgType_Explosion.StaticClass(), default, player);
    }

    static void Throw(RPawnPlayer player, Vector3 target)
    {
        if (_grenadeClass is not { IsValid: true } kind)
        {
            return;
        }

        Voice.Speak("dropped");

        var from = Nozzle(player, target);
        target = Aim.PointFor(from);
        var way = target - from;
        if (way.LengthSquared() < 1f)
        {
            return;
        }

        // He is its thrower, so it passes through him instead of bursting on his own body.
        var heading = Vector3.Normalize(Vector3.Normalize(way) + Vector3.UnitZ * ThrowLift);
        if (player.Spawn(kind, player, FName.None, from + heading * ThrowAhead, Rotator.FromDirection(heading), null!, true) is not { IsValid: true } grenade)
        {
            return;
        }

        grenade.Instigator = player;
        if (grenade is RProjectile_GrenadeBase bomb)
        {
            bomb.ExplodeTime = 999f;
            bomb.proximityExplodeTime = 999f;
            bomb.BlastRadiusOuter = BlastRadius;
            bomb.MinExplosionImpulse = BlastImpulse;
            bomb.MaxExplosionImpulse = BlastImpulse;
            bomb.SetInitialThrow(heading * ThrowSpeed, target);
        }
        else
        {
            grenade.Velocity = heading * ThrowSpeed;
        }
        if (_launchSound is { IsValid: true } launch)
        {
            player.StartAudioEvent(launch, default, default);
        }

        Grenades.Add(new Thrown { Grenade = grenade });
        Threw();
    }

    // Each grenade is watched for its fire bursting out. That blast knocks down every thug near
    // it, and while the fire burns anyone walking in goes down too.
    static void Burning(RPawnPlayer player)
    {
        var now = World.Now;
        for (var i = Grenades.Count - 1; i >= 0; i--)
        {
            var thrown = Grenades[i];
            if (!thrown.Grenade.IsValid || thrown.Grenade.bDeleteMe)
            {
                Grenades.RemoveAt(i);
                continue;
            }

            var grenade = thrown.Grenade;
            if (grenade.Physics == Actor.EPhysics.PHYS_Projectile)
            {
                thrown.Last = grenade.Location;
            }
            else if (!thrown.Landed && thrown.Last != default)
            {
                thrown.Landed = true;
                if (Vector3.Distance(grenade.Location, thrown.Last) > Jumped && !grenade.SetLocation(thrown.Last))
                {
                    grenade.Location = thrown.Last;
                }
            }

            var fire = Fire(grenade);
            if (!thrown.Burst)
            {
                if (fire)
                {
                    thrown.Burst = true;
                    thrown.At = grenade.Location;
                    thrown.NextHit = now;
                }

                continue;
            }

            if (!fire)
            {
                Grenades.RemoveAt(i);
                continue;
            }

            // A vehicle can cross the fire in a moment, so they are looked for every tick.
            Look();
            foreach (var car in Cars)
            {
                var away = car.Location - thrown.At;
                if (away.Length() <= FireReach + CarRoom)
                {
                    Ignite(player, car, Teleport.Flat(away));
                }
            }

            foreach (var car in Parked)
            {
                var away = car.Location - thrown.At;
                if (away.Length() <= FireReach + CarRoom)
                {
                    Torch(player, car, Teleport.Flat(away));
                }
            }

            if (now < thrown.NextHit)
            {
                continue;
            }

            thrown.NextHit = now + FireEvery;
            foreach (var thug in Nearby(thrown.At))
            {
                var away = thug.Location - thrown.At;
                if (away.Length() <= FireReach)
                {
                    Hurt(player, thug, Vector3.Normalize(Teleport.Flat(away) + Vector3.UnitZ * 0.5f), true);
                }
            }
        }
    }

    // The game's own damage on the vehicle, as an explosion, which is what its armour rules
    // are written for. Its fire is lit or kept going.
    static void Ignite(RPawnPlayer player, RVehicleNPC car, Vector3 push)
    {
        if (!car.IsValid || car.bDeleteMe || car.Health <= 0)
        {
            return;
        }

        if (!OnFire.TryGetValue(car.Ptr, out var alight) || !ReferenceEquals(alight.Car, car))
        {
            alight = new Alight { Car = car, Full = Math.Max(car.Health, car.HealthMax) };
            OnFire[car.Ptr] = alight;
            _carsLit++;
        }

        alight.Until = World.Now + CarFireSeconds;
        if (World.Now < alight.NextDamage)
        {
            return;
        }

        alight.NextDamage = World.Now + CarBurnEvery;
        var damage = Math.Max(1, (int)MathF.Round(alight.Full * CarBurnShare));
        car.TakeDamage(damage, player.Controller, car.Location, push * 100f, RDmgType_Explosion.StaticClass(), default, player);
    }

    static ParticleSystem? CarFire() =>
        _carFire is { IsValid: true } ? _carFire : _carFire = Kept(Game.FindObject<ParticleSystem>(CarFirePath)) ?? StreamFire();

    // Each burning vehicle carries its fire with it until it has gone unhit for a while.
    static void CarFires()
    {
        if (OnFire.Count == 0)
        {
            return;
        }

        var pool = Game.TryGetWorldInfo(out var world) ? world.GRI?.ObjectPool : null;
        foreach (var (key, alight) in OnFire.ToArray())
        {
            var car = alight.Car;
            var over = !car.IsValid || car.bDeleteMe || World.Now >= alight.Until;
            if (over)
            {
                if (alight.Fire is { IsValid: true, bHidden: false } old && old.ParticleSystemComponent is { IsValid: true } effect)
                {
                    effect.DeactivateSystem();
                }

                OnFire.Remove(key);
                continue;
            }

            var at = car.Location + Vector3.UnitZ * CarFireLift;
            if ((alight.Fire is null || !alight.Fire.IsValid || alight.Fire.bHidden) && pool is { IsValid: true } && CarFire() is { } fire)
            {
                alight.Fire = pool.SpawnEmitter(fire, at, car.Rotation, 0, CarFireSize);
            }

            if (alight.Fire is { IsValid: true } flames)
            {
                flames.SetLocation(at);
            }
        }
    }

    static bool Fire(Actor grenade)
    {
        foreach (var component in grenade.Components)
        {
            if (component is ParticleSystemComponent { IsValid: true, bIsActive: true } effect
                && effect.Template is { IsValid: true } template
                && template.GetPathName().Equals(GrenadeFirePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    static void Hurt(RPawnPlayer player, RPawnVillain thug, Vector3 push, bool fall)
    {
        if (Villains.Knock(player, thug, (int)FlameDamage, push, BlastPush, fall))
        {
            Burned.Remove(thug.Ptr);
            Torched();
            Voice.Speak("hit");
        }
    }

    // Standing enemies near a spot, or all of them.
    static List<RPawnVillain> Nearby(Vector3? near = null)
    {
        Look();
        if (near is not { } at)
        {
            return Thugs;
        }

        return Thugs.Where(thug => Vector3.DistanceSquared(thug.Location, at) <= FireReach * FireReach * 4f).ToList();
    }

    // Enemies and their vehicles, taken from the game at most once a second and kept tidy between.
    static void Look()
    {
        if (World.Now >= _nextLook)
        {
            _nextLook = World.Now + LookEvery;
            Thugs.Clear();
            foreach (var thug in Game.FindObjects<RPawnVillain>())
            {
                if (thug.IsValid && !thug.IsClassDefaultObject && Crowd.IsEnemy(thug))
                {
                    Thugs.Add(thug);
                }
            }

            // The cars thugs drive around the streets are civilian ones, so being civilian
            // doesn't spare a car. Friendly and hacked ones are spared.
            Cars.Clear();
            foreach (var car in Game.FindObjects<RVehicleNPC>())
            {
                if (car is { IsValid: true, IsClassDefaultObject: false, bDeleteMe: false, bHidden: false, DroneHacked: false, IsFriendly: false, Health: > 0 })
                {
                    Cars.Add(car);
                }
            }

            Parked.Clear();
            foreach (var car in Game.FindObjects<RAbandonedVehicle>())
            {
                if (car is { IsValid: true, IsClassDefaultObject: false, bDeleteMe: false, bHidden: false, Health: > 0 })
                {
                    Parked.Add(car);
                }
            }
        }

        Thugs.RemoveAll(thug => !thug.IsValid || thug.bDeleteMe || thug.Health <= 0 || !Crowd.Standing(thug));
        Cars.RemoveAll(car => !car.IsValid || car.bDeleteMe || car.Health <= 0);
        Parked.RemoveAll(car => !car.IsValid || car.bDeleteMe || car.Health <= 0);
    }

    static void StopWeapons()
    {
        Aim.Release();
        foreach (var dart in Darts)
        {
            Burnt(dart);
        }

        Darts.Clear();
        _streamFire = null;
        FireSound(null, false);
        _flaming = false;
        _secondaryWas = false;
        _grenadeClass = null;
        _gun = null;
        _gunTried = 0;
        _launchSound = null;
        _fireSound = null;
        Grenades.Clear();
        Thugs.Clear();
        Cars.Clear();
        Parked.Clear();
        ParkedNext.Clear();
        _carFire = null;
        foreach (var alight in OnFire.Values)
        {
            if (alight.Fire is { IsValid: true, bHidden: false } fire && fire.ParticleSystemComponent is { IsValid: true } effect)
            {
                effect.DeactivateSystem();
            }
        }

        OnFire.Clear();
        Burned.Clear();
    }
}
