#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// PLAYER > MOVEMENT > CLUMSY BATMAN. Running into a wall or a prop, catching a kerb or a step, or
// taking a hit sometimes drops him into a ragdoll. It is the game's own clumsy cheat in how it falls and gets
// up, with the trips decided here.
static class Clumsy
{
    // Below this he is walking, and walking never trips.
    const float RunPace = 450f;

    // How far the ground has to jump in one tick to count as a kerb or a step.
    const float StepRise = 10f;
    const float StepTop = 60f;

    // A step lifts him faster than any slope he can walk up, which is how the two are told apart.
    const float StepSteep = 0.8f;

    // A hit this long ago still counts, because the game reports it during the move, after our tick.
    const float HitWindow = 0.15f;

    // The game reports a bump every frame while he leans on something. They count as one hit
    // until this long has passed with none.
    const float ContactGap = 0.3f;

    const float QuietAfter = 4f;
    const float LeastDown = 1.5f;
    const float MostDown = 6f;
    const float StillPace = 50f;

    static readonly FName Reason = new("KnightfallClumsy");

    static float _speed;
    static Vector3 _stood = new(float.NaN);
    static float _hitAt = float.NegativeInfinity;
    static float _struckAt = float.NegativeInfinity;
    static float _contactAt = float.NegativeInfinity;
    static float _fellAt = float.NegativeInfinity;
    static float _quietUntil;
    static bool _down;
    static int _epoch;

    public static readonly Toggle Trips = new(
        "player.movement.clumsy", "CLUMSY BATMAN",
        "Running into a wall or a prop, catching a kerb or a step at a run, or taking a hit can knock you into a ragdoll. You get back up on your own. Walking is safe.")
    {
        Block = static () => World.Player is null ? "NO PLAYER" : Villains.On ? "NOT AS A VILLAIN" : null,
        Tick = Tick,
        Restore = Release,
    };

    public static readonly Slider Odds = new(
        "player.movement.clumsy_odds", "CLUMSINESS",
        "How often a bump, a step or a hit knocks you down.",
        min: 5, max: 100, step: 5, value: 50, unit: "%");

    [Redirect(typeof(RPawnPlayer), nameof(RPawnPlayer.Bump))]
    static void Bump(RPawnPlayer self, Actor other, PrimitiveComponent otherComp, Vector3 hitNormal)
    {
        Hit(other);
        Redirects.RunOriginal();
    }

    [Redirect(typeof(RPawnPlayer), nameof(RPawnPlayer.HitWall))]
    static void HitWall(RPawnPlayer self, Vector3 hitNormal, Actor wall, PrimitiveComponent wallComp)
    {
        Hit(wall);
        Redirects.RunOriginal();
    }

    // Only noted here. The fall starts on the next tick, outside the game's move.
    static void Hit(Actor? other)
    {
        if (Trips.On && other is not Pawn)
        {
            if (World.Now - _contactAt > ContactGap)
            {
                _hitAt = World.Now;
            }

            _contactAt = World.Now;
        }
    }

    // Called after the game has taken a hit on the player. Noted here like a bump.
    public static void Struck(RPawnPlayer pawn, int damage)
    {
        if (Trips.On && damage > 0 && pawn.Health > 0)
        {
            _struckAt = World.Now;
        }
    }

    static void Tick()
    {
        if (_epoch != World.Epoch)
        {
            _epoch = World.Epoch;
            _down = false;
            _stood = new(float.NaN);
            _hitAt = float.NegativeInfinity;
            _struckAt = float.NegativeInfinity;
        }

        if (World.Paused || World.Player is not { IsValid: true, IsAlive: true } pawn || World.Controller is not { IsValid: true } controller)
        {
            return;
        }

        if (_down)
        {
            GetUp(pawn);
            return;
        }

        var level = new Vector3(pawn.Velocity.X, pawn.Velocity.Y, 0f).Length();
        var moved = float.IsNaN(_stood.Z) ? Vector3.Zero : pawn.Location - _stood;
        var rise = moved.Z;
        var along = new Vector3(moved.X, moved.Y, 0f).Length();
        var pace = _speed;
        _stood = pawn.Location;
        _speed = level;

        var calm = pawn.Health > 0 && !pawn.IsRagdoll() && !Villains.On && !Switcher.InScene(controller) && World.Driving is null && World.Now > _quietUntil;

        // A hit lands while its stagger is playing, so it doesn't wait for a free moment.
        var struck = World.Now - _struckAt < HitWindow;
        _struckAt = float.NegativeInfinity;
        if (struck && calm && pawn.Physics is Actor.EPhysics.PHYS_Walking or Actor.EPhysics.PHYS_Falling)
        {
            Roll(pawn);
            return;
        }

        var free = calm && pawn.Physics == Actor.EPhysics.PHYS_Walking && pawn.CurrentSpecialMove is not { IsValid: true };
        if (!free || Math.Max(pace, level) < RunPace)
        {
            return;
        }

        var bumped = World.Now - _hitAt < HitWindow && pace >= RunPace;
        var tripped = rise is > StepRise and < StepTop && rise > along * StepSteep;
        if (!bumped && !tripped)
        {
            return;
        }

        _hitAt = float.NegativeInfinity;
        Roll(pawn);
    }

    static void Roll(RPawnPlayer pawn)
    {
        if (Random.Shared.NextDouble() * 100 < Odds.Value)
        {
            Fall(pawn);
        }
    }

    static void Fall(RPawnPlayer pawn)
    {
        pawn.PushRagdollOn(Reason, 0f, 0f, 0.5f);
        _down = true;
        _fellAt = World.Now;
    }

    // Up again once the body has settled, or after a while whatever it does.
    static void GetUp(RPawnPlayer pawn)
    {
        var down = World.Now - _fellAt;
        var still = pawn.Mesh is { IsValid: true } mesh && mesh.GetRBLinearVelocity().Length() < StillPace;
        if (down < LeastDown || (!still && down < MostDown))
        {
            return;
        }

        Release();
    }

    // Also the row going off, or everything being put back, so he is never left on the floor.
    static void Release()
    {
        if (_epoch == World.Epoch && _down && World.Player is { IsValid: true, IsAlive: true } pawn && pawn.Anim is { IsValid: true } anim && pawn.IsRagdoll())
        {
            anim.PopRagdoll(Reason);
        }

        _down = false;
        _quietUntil = World.Now + QuietAfter;
        _hitAt = float.NegativeInfinity;
        _struckAt = float.NegativeInfinity;
    }
}
