#nullable enable

using System.Diagnostics;
using BmSDK;
using BmSDK.BmGame;

namespace Knightfall;

// Croc's HUD, his own eye up close. The scales of its lids are his health, going dark and then
// bloody as he is hurt. The slit pupil breathes, drawn thin when his charge is ready and glowing
// while he charges, and the iris flashes red when he is hit. Every few seconds his third eyelid
// sweeps across. Flecks in the iris are his crew, green for each recruit. Beside it, what he is
// doing, his charge and roar, and the thugs he has thrown.
static partial class KillerCroc
{
    static readonly Rgba Skin = new(37, 50, 32);
    static readonly Rgba Scale = new(63, 85, 50);
    static readonly Rgba ScaleDark = new(42, 52, 36);
    static readonly Rgba ScaleBlood = new(92, 38, 32);
    static readonly Rgba Rim = new(20, 28, 16);
    static readonly Rgba White = new(138, 123, 34);
    static readonly Rgba Iris = new(184, 163, 50);
    static readonly Rgba IrisCore = new(211, 189, 72);
    static readonly Rgba Streak = new(125, 110, 30);
    static readonly Rgba Pupil = new(9, 10, 6);
    static readonly Rgba Gleam = new(251, 243, 211);
    static readonly Rgba Amber = new(226, 160, 60);
    static readonly Rgba AmberDim = new(90, 67, 32);
    static readonly Rgba Bone = new(217, 211, 189);
    static readonly Rgba Faded = new(143, 138, 118);
    static readonly Rgba Empty = new(42, 44, 38);
    static readonly Rgba Moss = new(127, 163, 90);
    static readonly Rgba Blood = new(194, 58, 44);
    static readonly Rgba Membrane = new(169, 179, 154, 120);

    // Laid out on the 1080-high screen.
    const float HudLeft = 36f;
    const float HudBottom = 36f;
    const float HudHeight = 150f;
    const float EyeHalfWidth = 96f;
    const float EyeHalfHeight = 52f;
    const float LidHalfWidth = 112f;
    const float LidHalfHeight = 70f;
    const float IrisRadius = 46f;
    const float CoreRadius = 30f;
    const float SideGap = 132f;
    const int CurvePoints = 24;

    // The pupil breathes this wide on either side of its middle, thinner when the charge is ready.
    const float PupilWide = 9f;
    const float PupilThin = 3.5f;
    const float BreathSeconds = 3f;

    // His third eyelid crosses every so often and takes this long over it.
    const float BlinkEvery = 5f;
    const float BlinkSeconds = 0.45f;

    // A hit flashes the iris red, a new mark on the tally flares, and a new state flashes its tag.
    const float HitFlash = 0.35f;
    const float TagFlash = 0.4f;

    const int LidScales = 12;
    const int CrewFlecks = 12;
    const int Blocks = 8;

    static float _damagedAt = -10f;
    static float _thrownAt = -10f;
    static int _thrown;
    static string _tag = "";
    static float _tagAt = -10f;

    static void ShowHud() => VillainHud.Show(Hud.On ? DrawHud : null);

    static void DrawHud(float rise, float seconds)
    {
        if (World.Player is not { IsValid: true } player)
        {
            return;
        }

        var time = (float)(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        var cx = HudLeft + LidHalfWidth;
        var cy = Draw.ScreenHeight - HudBottom - HudHeight / 2f + rise;
        var health = player.HealthMax > 0 ? Math.Clamp(player.Health / (float)player.HealthMax, 0f, 1f) : 1f;

        Lids(cx, cy, health);
        Eyeball(cx, cy, time);
        Lashline(cx, cy);
        Side(cx + LidHalfWidth + 14f, cy - HudHeight / 2f + 8f, time);
    }

    // Half the eye's height at a distance across from its middle.
    static float Opening(float across, float halfWidth, float halfHeight)
    {
        var t = Math.Clamp(1f - across * across / (halfWidth * halfWidth), 0f, 1f);
        return halfHeight * MathF.Pow(t, 0.8f);
    }

    // The skin round the eye, and its scales: health runs from the outer corner round the top
    // and along the bottom, and the lost ones go dark, the last lost bloody.
    static void Lids(float cx, float cy, float health)
    {
        for (var i = 0; i < CurvePoints; i++)
        {
            var a = -LidHalfWidth + LidHalfWidth * 2f * i / CurvePoints;
            var b = -LidHalfWidth + LidHalfWidth * 2f * (i + 1) / CurvePoints;
            var ha = Opening(a, LidHalfWidth, LidHalfHeight);
            var hb = Opening(b, LidHalfWidth, LidHalfHeight);
            Draw.Quad(cx + a, cy - ha, cx + b, cy - hb, cx + b, cy + hb, cx + a, cy + ha, Skin);
        }

        var whole = (int)MathF.Round(health * LidScales);
        for (var i = 0; i < LidScales; i++)
        {
            var color = i < whole ? Scale : i == whole ? ScaleBlood : ScaleDark;
            var top = i < 8;
            var slot = top ? i : i - 8;
            var count = top ? 8 : 4;
            var from = -EyeHalfWidth * 0.85f + EyeHalfWidth * 1.7f * slot / count;
            var to = from + EyeHalfWidth * 1.7f / count - 4f;
            var side = top ? -1f : 1f;
            var innerFrom = Opening(from, EyeHalfWidth, EyeHalfHeight) + 3f;
            var innerTo = Opening(to, EyeHalfWidth, EyeHalfHeight) + 3f;
            var outerFrom = (Opening(from, LidHalfWidth, LidHalfHeight) + innerFrom) / 2f + 4f;
            var outerTo = (Opening(to, LidHalfWidth, LidHalfHeight) + innerTo) / 2f + 4f;
            Draw.Quad(cx + from, cy + side * innerFrom, cx + to, cy + side * innerTo, cx + to, cy + side * outerTo, cx + from, cy + side * outerFrom, color);
        }
    }

    static void Eyeball(float cx, float cy, float time)
    {
        for (var i = 0; i < CurvePoints; i++)
        {
            var a = -EyeHalfWidth + EyeHalfWidth * 2f * i / CurvePoints;
            var b = -EyeHalfWidth + EyeHalfWidth * 2f * (i + 1) / CurvePoints;
            var ha = Opening(a, EyeHalfWidth, EyeHalfHeight);
            var hb = Opening(b, EyeHalfWidth, EyeHalfHeight);
            Draw.Quad(cx + a, cy - ha, cx + b, cy - hb, cx + b, cy + hb, cx + a, cy + ha, White);
        }

        var charging = _move == Move.Charge;
        var hurt = Since(_damagedAt) < HitFlash ? 1f - Since(_damagedAt) / HitFlash : 0f;
        var iris = Rgba.Mix(charging ? Rgba.Mix(Iris, Amber, 0.5f) : Iris, Blood, hurt);
        var core = Rgba.Mix(charging ? Amber : IrisCore, Blood, hurt * 0.8f);

        Shapes.Under = White;
        Draw.Place(cx, cy, 1f);
        Shapes.Disc(IrisRadius, iris);
        Shapes.Disc(CoreRadius, core);
        Draw.Place(0f, 0f, 1f);
        for (var i = 0; i < 8; i++)
        {
            var (sin, cos) = MathF.SinCos(i * MathF.PI / 4f + 0.3f);
            Draw.Line(cx + cos * (CoreRadius + 3f), cy + sin * (CoreRadius + 3f), cx + cos * (IrisRadius - 2f), cy + sin * (IrisRadius - 2f), Streak);
        }

        var crew = Math.Min(Bodyguards.All.Count, CrewFlecks);
        for (var i = 0; i < CrewFlecks; i++)
        {
            var (sin, cos) = MathF.SinCos(i * MathF.PI * 2f / CrewFlecks - MathF.PI / 2f);
            var at = (CoreRadius + IrisRadius) / 2f;
            Draw.Rect(cx + cos * at - 2f, cy + sin * at - 2f, 4f, 4f, i < crew ? Moss : Streak);
        }

        // Narrow when the charge is ready, narrowest while he charges, breathing all the while.
        var ready = charging || World.Now >= _nextCharge;
        var breath = 0.5f + 0.5f * MathF.Sin(time * MathF.PI * 2f / BreathSeconds);
        var wide = charging ? PupilThin * 0.6f : ready ? PupilThin + breath * 1.5f : PupilWide + breath * 2f;
        PupilSlit(cx, cy, wide);

        Shapes.Under = core;
        Draw.Place(cx - 17f, cy - 17f, 1f);
        Shapes.Disc(5f, Gleam, 0.7f);
        Draw.Place(0f, 0f, 1f);

        // The third eyelid, a pale film sweeping across.
        var phase = time % BlinkEvery;
        if (phase < BlinkSeconds)
        {
            var edge = -EyeHalfWidth + (EyeHalfWidth * 2f + 60f) * phase / BlinkSeconds;
            for (var across = -EyeHalfWidth; across < EyeHalfWidth; across += 4f)
            {
                if (across > edge || across < edge - 60f)
                {
                    continue;
                }

                var open = Opening(across + 2f, EyeHalfWidth, EyeHalfHeight);
                Draw.Rect(cx + across, cy - open, 4f, open * 2f, Membrane);
            }
        }
    }

    // A slit from the top of the iris to the bottom, widest at its middle.
    static void PupilSlit(float cx, float cy, float halfWidth)
    {
        const int steps = 10;
        for (var i = 0; i < steps; i++)
        {
            var ya = -IrisRadius + 2f + (IrisRadius - 2f) * 2f * i / steps;
            var yb = -IrisRadius + 2f + (IrisRadius - 2f) * 2f * (i + 1) / steps;
            var wa = halfWidth * MathF.Sin(MathF.PI * i / steps);
            var wb = halfWidth * MathF.Sin(MathF.PI * (i + 1) / steps);
            Draw.Quad(cx - wa, cy + ya, cx + wa, cy + ya, cx + wb, cy + yb, cx - wb, cy + yb, Pupil);
        }
    }

    // A dark line round the opening, so the lid edge reads.
    static void Lashline(float cx, float cy)
    {
        for (var i = 0; i < CurvePoints; i++)
        {
            var a = -EyeHalfWidth + EyeHalfWidth * 2f * i / CurvePoints;
            var b = -EyeHalfWidth + EyeHalfWidth * 2f * (i + 1) / CurvePoints;
            var ha = Opening(a, EyeHalfWidth, EyeHalfHeight);
            var hb = Opening(b, EyeHalfWidth, EyeHalfHeight);
            Stroke(cx + a, cy - ha, cx + b, cy - hb, 3f, Rim);
            Stroke(cx + a, cy + ha, cx + b, cy + hb, 3f, Rim);
        }
    }

    // What he is doing, then his charge and roar as block meters on their keys, then his crew and
    // the thugs he has thrown.
    static void Side(float x, float y, float time)
    {
        var tag = Doing();
        if (tag != _tag)
        {
            _tag = tag;
            _tagAt = World.Now;
        }

        var flash = Since(_tagAt) < TagFlash ? 1f - Since(_tagAt) / TagFlash : 0f;
        Draw.Tracked(tag, x, y, 24f, 3f, Rgba.Mix(_move == Move.None ? Faded : Amber, Bone, flash));

        var charge = _move == Move.Charge ? 1f : Readiness(_nextCharge, ChargeWait);
        var roar = _move == Move.Roar ? 1f : Readiness(_nextRoar, RoarWait);
        Meter(x, y + 36f, "CHARGE", charge, Hotkeys.KeyText(ChargeKey), _move == Move.Charge && (time * 4f % 1f) < 0.5f);
        Meter(x, y + 56f, "ROAR", roar, Hotkeys.KeyText(RoarKey), false);

        Draw.Rect(x, y + 78f, SideGap + 60f, 1f, Empty);
        var crew = Math.Min(Bodyguards.All.Count, CrewFlecks);
        var crewKey = Hotkeys.KeyText(RecruitKey);
        Draw.Tracked(crewKey.Length > 0 ? $"CREW {crew} / {CrewFlecks} · {crewKey}" : $"CREW {crew} / {CrewFlecks}", x, y + 84f, 11f, 2f, Moss);
        var newest = Since(_thrownAt) < HitFlash;
        Draw.Tracked($"THROWN {_thrown}", x, y + 100f, 11f, 2f, newest ? Blood : Bone);
    }

    static void Meter(float x, float y, string name, float filled, string key, bool pulse)
    {
        var ready = filled >= 1f;
        Draw.Tracked(name, x, y, 12f, 1f, ready ? Amber : Faded);
        var left = x + 58f;
        var width = SideGap - 58f;
        var block = (width - (Blocks - 1) * 2f) / Blocks;
        var lit = (int)MathF.Floor(filled * Blocks);
        for (var i = 0; i < Blocks; i++)
        {
            var color = i < lit ? (pulse ? Bone : ready ? Amber : Bone) : Empty;
            Draw.Rect(left + i * (block + 2f), y + 3f, block, 8f, color);
        }

        if (key.Length > 0)
        {
            var keyX = left + width + 6f;
            var keyWidth = Draw.TextWidth(key, 10f) + 8f;
            Draw.Frame(keyX, y, keyWidth, 14f, 1f, ready ? AmberDim : Empty);
            Draw.Text(key, keyX + 4f, y + 1f, 10f, ready ? Amber : Faded);
        }
    }

    static string Doing() => _move switch
    {
        Move.Swipe => "SWIPING",
        Move.Charge => "CHARGING",
        Move.Recruit => _walkingUp ? "STALKING" : "HAULING",
        Move.Roar => "ROARING",
        Move.Hurt => "HURT",
        _ => World.Controller is { IsValid: true } controller && controller.IsInCombat(false) ? "HUNTING" : "PROWLING",
    };

    // From 0 as a move is used to 1 once it can be used again.
    static float Readiness(float readyAt, float wait) => Math.Clamp(1f - (readyAt - World.Now) / wait, 0f, 1f);

    static float Since(float at) => World.Now >= at ? World.Now - at : float.MaxValue;

    // A line of some thickness, as a thin quad.
    static void Stroke(float x0, float y0, float x1, float y1, float thick, Rgba color)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 0.01f)
        {
            return;
        }

        var nx = -dy / length * thick / 2f;
        var ny = dx / length * thick / 2f;
        Draw.Quad(x0 + nx, y0 + ny, x1 + nx, y1 + ny, x1 - nx, y1 - ny, x0 - nx, y0 - ny, color);
    }
}
