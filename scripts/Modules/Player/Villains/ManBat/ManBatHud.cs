#nullable enable

using System.Numerics;
using BmSDK.BmGame;

namespace Knightfall;

// Man-Bat's HUD is the way he sees in the dark. A fan of sonar opens ahead of the camera, and every
// so often an echo sweeps out across it from him. A thug only shows as the echo passes over him,
// then fades back into the dark. A screech floods the whole fan red. His health is the rim of the
// fan. Beside it, Langstrom's name, what he is doing, his three moves and the prey he has taken.
static partial class ManBat
{
    static readonly Rgba Membrane = new(52, 20, 18);
    static readonly Rgba Dark = new(20, 10, 9);
    static readonly Rgba Dim = new(112, 88, 80);
    static readonly Rgba Ember = new(236, 92, 70);

    // Laid out on the 1080-high screen, from the bottom left.
    const float HudLeft = 40f;
    const float HudBottom = 44f;
    const float FanRadius = 96f;
    const float RimWidth = 5f;
    const float SideGap = 18f;
    const float RowHeight = 17f;
    const float TextSize = 8f;
    const float TitleSize = 13f;
    const float Spacing = 1.5f;

    // How far the fan reaches, how often an echo goes out, how long it takes to reach the rim, and
    // how long a thug stays lit after it passes.
    const float SonarReach = 4000f;
    const float EchoEvery = 1.4f;
    const float EchoTravel = 0.9f;
    const float EchoFade = 0.9f;
    const float BurstSeconds = 0.8f;
    const float HitFlash = 0.35f;

    static float _damagedAt = -10f;
    static readonly string[] Counts = Enumerable.Range(0, 100).Select(n => n.ToString()).ToArray();

    static void ShowHud() => VillainHud.Show(Hud.On ? DrawHud : null);

    static void DrawHud(float rise, float seconds)
    {
        if (World.Player is not { IsValid: true } player || World.Controller is not { } controller)
        {
            return;
        }

        var now = World.Now;
        var cx = HudLeft + FanRadius + RimWidth;
        var cy = Draw.ScreenHeight - HudBottom + rise;
        var health = player.HealthMax > 0 ? Math.Clamp(player.Health / (float)player.HealthMax, 0f, 1f) : 1f;

        Draw.Place(cx, cy, 1f);
        Shapes.Under = Dark;
        Fan(now, health);
        Blips(player, controller.Rotation.Yaw, now);
        Draw.Place(0f, 0f, 1f);
        Side(cx + FanRadius + RimWidth + SideGap, cy - FanRadius - 4f, now);
    }

    // The fan, its range lines, the echo going out and the rim that is his health.
    static void Fan(float now, float health)
    {
        Shapes.Arc(0f, FanRadius, -90f, 90f, Dark, 0.85f);
        Shapes.Dashes(FanRadius / 3f - 0.5f, FanRadius / 3f + 0.5f, -90f, 90f, 3f, 5f, Dim, 0.5f);
        Shapes.Dashes(FanRadius * 2f / 3f - 0.5f, FanRadius * 2f / 3f + 0.5f, -90f, 90f, 3f, 5f, Dim, 0.5f);
        Shapes.Spoke(0f, 6f, FanRadius, 1f, Dim, 0.35f);
        Shapes.Spoke(-45f, 6f, FanRadius, 1f, Dim, 0.2f);
        Shapes.Spoke(45f, 6f, FanRadius, 1f, Dim, 0.2f);

        var echo = now % EchoEvery / EchoTravel;
        if (echo < 1f)
        {
            var r = FanRadius * echo;
            Shapes.Arc(MathF.Max(0f, r - 2f), r, -90f, 90f, Blood, (1f - echo) * 0.9f);
        }

        var burst = (now - _screechAt) / BurstSeconds;
        if (burst is >= 0f and < 1f)
        {
            var r = FanRadius * burst;
            Shapes.Arc(0f, r, -90f, 90f, Blood, (1f - burst) * 0.45f);
            Shapes.Arc(MathF.Max(0f, r - 4f), r, -90f, 90f, Ember, 1f - burst);
        }

        // Health runs round the rim from the left. A hit flashes what is left red.
        var hit = now - _damagedAt < HitFlash;
        var lit = -90f + 180f * health;
        Shapes.Arc(FanRadius, FanRadius + RimWidth, -90f, lit, hit ? Ember : Bone, 0.9f);
        Shapes.Arc(FanRadius, FanRadius + RimWidth, lit, 90f, Membrane, 0.9f);
        Shapes.Ticks(FanRadius + RimWidth, FanRadius + RimWidth + 3f, 24, 0f, 1f, Dark);

        // Him, at the middle, with whoever he carries.
        Shapes.Disc(5f, _carried is not null ? Blood : Bone);
        Shapes.Notch(0f, 7f, 4f, Bone);
    }

    // Each thug ahead of the camera, lit as the echo passes him and fading after. The one he is
    // marking is ringed, and a screech lights everyone red.
    static void Blips(RPawnPlayer player, float facing, float now)
    {
        var burst = now - _screechAt < BurstSeconds * 1.5f;
        foreach (var thug in Crowd.StandingEnemies())
        {
            var off = (thug.Location - player.Location) with { Z = 0f };
            var distance = off.Length();
            if (distance > SonarReach || distance < 1f)
            {
                continue;
            }

            var angle = Wrap(MathF.Atan2(off.Y, off.X) * (180f / MathF.PI) - facing);
            if (MathF.Abs(angle) > 90f)
            {
                continue;
            }

            var share = distance / SonarReach;
            var since = now % EchoEvery - share * EchoTravel;
            if (since < 0f)
            {
                since += EchoEvery;
            }

            var glow = burst ? 1f : MathF.Exp(-since / EchoFade);
            var r = share * FanRadius;
            var (sin, cos) = MathF.SinCos(angle * (MathF.PI / 180f));
            var x = sin * r;
            var y = -cos * r;
            var marked = _aiming && _prey is { } prey && prey.Ptr == thug.Ptr;
            var color = burst ? Ember : marked ? Ember : Bone;
            if (glow > 0.05f || marked)
            {
                Draw.Rect(x - 2f, y - 2f, 4f, 4f, Rgba.Mix(Dark, color, MathF.Max(glow, marked ? 0.8f : 0f)));
            }

            if (marked)
            {
                Draw.Frame(x - 5f, y - 5f, 10f, 10f, 1f, Ember);
            }
        }
    }

    // His name, what he is doing, his moves and his prey.
    static void Side(float x, float y, float now)
    {
        Draw.Tracked("MAN-BAT", x, y, TitleSize, Spacing, Bone);
        Draw.Tracked("K. LANGSTROM", x, y + 16f, TextSize, Spacing, Dim);

        var tag = State();
        var width = Draw.TrackedWidth(tag, TextSize, Spacing) + 10f;
        Draw.Rect(x, y + 30f, width, 13f, Membrane);
        Draw.Tracked(tag, x + 5f, y + 32f, TextSize, Spacing, tag is "CARRYING" or "STRIKING" or "SCREECHING" ? Ember : Bone);

        var row = y + 50f;
        Move(x, row, SwoopKey, "SWOOP", _carried is not null ? "HANDS FULL" : _act is Act.Dive or Act.Strike && !_snatching ? "DIVING" : "READY", 1f);
        var carryLeft = _carried is not null ? 1f - Math.Clamp((now - _carriedAt) / CarryLongest, 0f, 1f) : 1f;
        Move(x, row + RowHeight, SnatchKey, "SNATCH", _carried is not null ? "DROP" : "READY", carryLeft);
        var screech = now >= _nextScreech ? 1f : 1f - (_nextScreech - now) / ScreechWait;
        Move(x, row + RowHeight * 2f, ScreechKey, "SCREECH", screech >= 1f ? "READY" : "RECOVERING", screech);

        Draw.Tracked("PREY", x, row + RowHeight * 3f + 4f, TextSize, Spacing, Dim);
        Draw.Text(Counts[Math.Min(_downed, Counts.Length - 1)], x + 40f, row + RowHeight * 3f + 2f, 11f, Blood);
    }

    // One move: its key, its name, how ready it is as a bar, and a word.
    static void Move(float x, float y, Command key, string name, string note, float ready)
    {
        var keyText = Hotkeys.KeyText(key);
        var chip = MathF.Max(Draw.TextWidth(keyText, TextSize) + 8f, 16f);
        Draw.Rect(x, y, chip, 12f, Membrane);
        Draw.Text(keyText, x + 4f, y + 2f, TextSize, Bone);
        Draw.Tracked(name, x + chip + 6f, y + 2f, TextSize, Spacing, Bone);
        var barX = x + chip + 70f;
        Draw.Rect(barX, y + 4f, 48f, 4f, Dark);
        Draw.Rect(barX, y + 4f, 48f * Math.Clamp(ready, 0f, 1f), 4f, ready >= 1f ? Bone : Blood);
        Draw.Text(note, barX + 54f, y + 2f, TextSize, ready >= 1f && note == "READY" ? Dim : Ember);
    }

    static string State() => _act switch
    {
        Act.Dive => _snatching ? "SNATCHING" : "DIVING",
        Act.Strike => "STRIKING",
        Act.Screech => "SCREECHING",
        _ when _carried is not null => "CARRYING",
        _ when _aiming => "HUNTING",
        _ when _speed > FlySpeed * 0.5f => "FLYING",
        _ => "HOVERING",
    };
}
