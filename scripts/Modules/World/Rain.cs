#nullable enable

using BmSDK;
using BmSDK.AkAudio;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// WORLD > WEATHER, the RAIN rows, and the dry weather PRE-SCARECROW ERA and SNOW need. This is
// the only place that writes the rain volumes. It keeps each one's own values once and works
// every row out from those, so the era, snow and the rows can't undo each other, and each gets
// back exactly what it had.
static class Rain
{
    // New rain volumes and the story's own changes are picked up this often.
    const float LookEvery = 1f;

    // The game's own rain sound level, which follows the rain where you stand.
    const string RainLevel = "Ww_Parameters.RainDensity";

    // After the rain is let back on, the sound level is set from the rain around you once this
    // long later, when the volumes have settled.
    const float SoundBackAfter = 1f;

    enum F
    {
        Density, Box, Wet, Damp, Dark, Wake, Puddle, DampLine, FarDrawn,
        Count,
    }

    sealed record Entry(RainVolume Volume, float[] Was, float[] Wrote);

    static readonly Dictionary<nint, Entry> Volumes = new();

    // Each rain volume's own sound, and the parts that make rain sound off surfaces and emitters.
    static readonly Held<RRainComponent, AkEvent?> Bed = new(
        "rain sound", rain => rain.RainSettings.RainSoundBed is { IsValid: true } bed ? bed : null, SetBed);

    static readonly Held<AkEmitter, bool> Emitters = new(
        "rain emitter", emitter => emitter.bEmitterEnabled, (emitter, on) =>
        {
            if (on)
            {
                emitter.EnableEmitter();
            }
            else
            {
                emitter.DisableEmitter();
            }
        });

    static readonly Held<PhysicalMaterial, AkEvent?>[] Surfaces =
    {
        Surface(m => m.SurfaceRainSoundLightNear, (m, e) => m.SurfaceRainSoundLightNear = e!),
        Surface(m => m.SurfaceRainSoundLightMid, (m, e) => m.SurfaceRainSoundLightMid = e!),
        Surface(m => m.SurfaceRainSoundLightFar, (m, e) => m.SurfaceRainSoundLightFar = e!),
        Surface(m => m.SurfaceRainSoundModerateNear, (m, e) => m.SurfaceRainSoundModerateNear = e!),
        Surface(m => m.SurfaceRainSoundModerateMid, (m, e) => m.SurfaceRainSoundModerateMid = e!),
        Surface(m => m.SurfaceRainSoundModerateFar, (m, e) => m.SurfaceRainSoundModerateFar = e!),
        Surface(m => m.SurfaceRainSoundHeavyNear, (m, e) => m.SurfaceRainSoundHeavyNear = e!),
        Surface(m => m.SurfaceRainSoundHeavyMid, (m, e) => m.SurfaceRainSoundHeavyMid = e!),
        Surface(m => m.SurfaceRainSoundHeavyFar, (m, e) => m.SurfaceRainSoundHeavyFar = e!),
    };

    static readonly Held<ParticleSystemComponent, bool> Mist = new(
        "rain mist", mist => mist.HiddenGame, (mist, hidden) =>
        {
            if (hidden)
            {
                mist.DeactivateSystem();
                mist.SetHidden(true);
            }
            else
            {
                mist.SetHidden(false);
                mist.ActivateSystem(false, false);
            }
        });

    static readonly Held<StaticMeshComponent, bool> FarHidden = new(
        "distant rain", far => far.HiddenGame, (far, hidden) => far.SetHidden(hidden));

    static float _lookedAt = -1f;
    static string _applied = "";
    static bool _silent;
    static float _soundBackAt = -1f;
    static AkParameterName? _rainLevel;

    public static readonly Slider Amount = new(
        "world.weather.rain_amount", "RAIN AMOUNT",
        "How hard it rains. OFF stops the rain, dries the streets and silences it.",
        min: 0, max: 4, step: 0.25, value: 1, unit: "X", zero: "OFF");

    public static readonly Slider Wetness = new(
        "world.weather.street_wetness", "STREET WETNESS",
        "How wet and shiny the streets, roofs and suits look, puddles too. 0X is completely dry.",
        min: 0, max: 2, step: 0.1, value: 1, unit: "X");

    public static readonly Slider Spread = new(
        "world.weather.rain_spread", "RAIN SPREAD",
        "How far round you the rain falls. Smaller packs the drops close round the camera, bigger spreads them out thinner.",
        min: 0.25, max: 3, step: 0.25, value: 1, unit: "X");

    public static readonly Toggle NoMist = new(
        "world.weather.no_rain_mist", "NO RAIN MIST",
        "Takes away the spray of mist that hangs round you in the rain.");

    public static readonly Toggle NoFarRain = new(
        "world.weather.no_distant_rain", "NO DISTANT RAIN",
        "Takes away the sheets of rain drawn far off across the city, so the skyline stays sharp.");

    static readonly Feature[] All = { Amount, Wetness, Spread, NoMist, NoFarRain };

    public static readonly Catalog.Section Section = new("RAIN", Amount, Wetness, Spread, NoMist, NoFarRain);

    // Rain off, from RAIN AMOUNT, the era or snow.
    static bool Dry => Amount.Value <= 0 || Atmosphere.Era.On || Snow.Snowing.On;

    public static void Tick()
    {
        SoundBack();

        var wanted = Wanted();
        if (wanted.Length == 0)
        {
            if (Volumes.Count > 0 || _silent)
            {
                Release();
            }

            return;
        }

        if (wanted == _applied && MathF.Abs(World.Now - _lookedAt) < LookEvery)
        {
            return;
        }

        _lookedAt = World.Now;
        _applied = wanted;
        if (!NoMist.On)
        {
            Mist.Release();
        }

        if (!NoFarRain.On)
        {
            FarHidden.Release();
        }

        foreach (var volume in Game.FindObjects<RainVolume>())
        {
            // The era's chapter package has a rain volume of its own that isn't in the world.
            if (volume.IsValid && !volume.IsClassDefaultObject && volume.RainComponent is { IsValid: true } && !CitySky.Owns(volume))
            {
                Apply(volume);
            }
        }

        if (Dry)
        {
            Silence();
            DryPeople();
        }
        else if (_silent)
        {
            Unsilence();
        }
    }

    public static void Release()
    {
        foreach (var entry in Volumes.Values)
        {
            if (entry.Volume.IsValid && entry.Volume.RainComponent is { IsValid: true } rain)
            {
                // Without SetRainSettings the values go back but the rain stays as we left it.
                Write(entry.Volume, rain, entry.Volume.RainMap, entry.Was);
                entry.Volume.SetRainSettings();
            }
        }

        Volumes.Clear();
        Mist.Release();
        FarHidden.Release();
        if (_silent)
        {
            Unsilence();
        }

        _applied = "";
    }

    static string Wanted()
    {
        var any = Atmosphere.Era.On || Snow.Snowing.On;
        foreach (var row in All)
        {
            any |= !row.IsDefault;
        }

        return any ? string.Join(",", Array.ConvertAll(All, row => row.ValueText)) + (Atmosphere.Era.On ? ",era" : "") + (Snow.Snowing.On ? ",snow" : "") : "";
    }

    static void Apply(RainVolume volume)
    {
        var rain = volume.RainComponent;
        var map = volume.RainMap;
        var now = Read(volume, rain, map);
        if (!Volumes.TryGetValue(volume.Ptr, out var entry) || !ReferenceEquals(entry.Volume, volume))
        {
            entry = new Entry(volume, now, now);
        }
        else if (!Same(now, entry.Wrote))
        {
            // The story changed this one's rain. Only the values it changed are new ones to go
            // back to. The rest are still ours, like the era's zero.
            var was = (float[])entry.Was.Clone();
            for (var i = 0; i < was.Length; i++)
            {
                if (MathF.Abs(now[i] - entry.Wrote[i]) > 0.0001f)
                {
                    was[i] = now[i];
                }
            }

            entry = entry with { Was = was };
        }

        var want = Compose(entry.Was);
        if (!Same(now, want))
        {
            Write(volume, rain, map, want);
            volume.SetRainSettings();
        }

        Volumes[volume.Ptr] = entry with { Wrote = want };

        if (volume.NearMist is { IsValid: true } mist && NoMist.On)
        {
            Mist.Hold(mist, true);
        }

        if (volume.FarRainMesh is { IsValid: true } far && NoFarRain.On)
        {
            FarHidden.Hold(far, true);
        }

        if (Dry)
        {
            Bed.Hold(rain, (AkEvent?)null);
        }
    }

    static float[] Compose(float[] was)
    {
        var to = (float[])was.Clone();
        var amount = Dry ? 0f : (float)Amount.Value;
        var wet = Dry ? 0f : (float)Wetness.Value;
        var damp = Math.Min(wet, 1f);

        to[(int)F.Density] = was[(int)F.Density] * amount;
        to[(int)F.Wake] = was[(int)F.Wake] * Math.Min(amount, 1f);
        to[(int)F.Box] = was[(int)F.Box] * (float)Spread.Value;
        to[(int)F.Wet] = was[(int)F.Wet] * wet;
        to[(int)F.Damp] = was[(int)F.Damp] * damp;
        to[(int)F.Dark] = was[(int)F.Dark] * damp;
        to[(int)F.Puddle] = Puddles(was[(int)F.Puddle], wet);
        to[(int)F.DampLine] = Puddles(was[(int)F.DampLine], wet);
        if (NoFarRain.On)
        {
            to[(int)F.FarDrawn] = 0f;
        }

        return to;
    }

    // The game's puddle and damp lines sit at the top, 1, so lower values give more puddles.
    // Drier than normal raises the line past the top, wetter brings it down to nothing.
    static float Puddles(float was, float wet)
    {
        var top = Math.Max(was, 1f);
        return wet <= 1f ? was + (top * 2f - was) * (1f - wet) : was * (2f - Math.Min(wet, 2f));
    }

    static float[] Read(RainVolume volume, RRainComponent rain, RainMapComponent? map)
    {
        var at = new float[(int)F.Count];
        at[(int)F.Density] = rain.RainSettings.Density;
        at[(int)F.Box] = rain.BoxSize;
        at[(int)F.FarDrawn] = volume.bDrawFarRainMesh ? 1f : 0f;
        if (map is { IsValid: true })
        {
            at[(int)F.Wet] = map.WetnessGlobalScale;
            at[(int)F.Damp] = map.MinimumDampAmount;
            at[(int)F.Dark] = map.Darkening;
            at[(int)F.Wake] = map.RainWakeAmount;
            at[(int)F.Puddle] = map.PuddleThreshold;
            at[(int)F.DampLine] = map.DampThreshold;
        }

        return at;
    }

    static void Write(RainVolume volume, RRainComponent rain, RainMapComponent? map, float[] at)
    {
        rain.RainSettings.Density = at[(int)F.Density];
        rain.BoxSize = at[(int)F.Box];
        volume.bDrawFarRainMesh = at[(int)F.FarDrawn] > 0.5f;
        if (map is not { IsValid: true })
        {
            return;
        }

        map.WetnessGlobalScale = at[(int)F.Wet];
        map.MinimumDampAmount = at[(int)F.Damp];
        map.Darkening = at[(int)F.Dark];
        map.RainWakeAmount = at[(int)F.Wake];
        map.PuddleThreshold = at[(int)F.Puddle];
        map.DampThreshold = at[(int)F.DampLine];

        // The rain map only reaches the renderer when it is rebuilt.
        map.QueueReattach(false);
    }

    // Rain is heard from three places besides each volume's own sound: the game's rain sound
    // level, rain sounds on surfaces, and emitters that play rain.
    static void Silence()
    {
        _silent = true;
        _soundBackAt = -1f;
        if (RainLevel_() is { } level)
        {
            Actor.SetGlobalAudioParameter(level, 0f);
        }

        foreach (var material in Game.FindObjects<PhysicalMaterial>())
        {
            if (!material.IsValid || material.IsClassDefaultObject)
            {
                continue;
            }

            foreach (var surface in Surfaces)
            {
                surface.Hold(material, (AkEvent?)null);
            }
        }

        foreach (var emitter in Game.FindObjects<AkEmitter>())
        {
            if (emitter.IsValid && !emitter.IsClassDefaultObject && emitter.bEmitterEnabled && PlaysRain(emitter))
            {
                Emitters.Hold(emitter, false);
            }
        }
    }

    static void Unsilence()
    {
        _silent = false;
        Bed.Release();
        foreach (var surface in Surfaces)
        {
            surface.Release();
        }

        Emitters.Release();
        _soundBackAt = World.Now + SoundBackAfter;
    }

    static void SoundBack()
    {
        if (_soundBackAt < 0f || World.Now < _soundBackAt && World.Now >= _soundBackAt - SoundBackAfter)
        {
            return;
        }

        _soundBackAt = -1f;
        if (RainLevel_() is { } level)
        {
            Actor.SetGlobalAudioParameter(level, AkSoundDirector.LocationRainDensity(AkWwise.GetActiveListenerPosition()));
        }
    }

    static AkParameterName? RainLevel_()
    {
        if (_rainLevel is not { IsValid: true })
        {
            _rainLevel = Game.FindObject<AkParameterName>(RainLevel);
        }

        return _rainLevel is { IsValid: true } level ? level : null;
    }

    static bool PlaysRain(AkEmitter emitter)
    {
        foreach (var sound in emitter.EmitterEvents)
        {
            if (sound is { IsValid: true } && (sound.Name.ToString() ?? "").Contains("Rain", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // People and the car build up their own wetness, which a dry street doesn't take away.
    static void DryPeople()
    {
        foreach (var body in Game.FindObjects<RPawnCharacter>())
        {
            if (body.IsValid && !body.IsClassDefaultObject && (body.WetnessAccumulationAlpha > 0f || body.RainAccumulationAlpha > 0f))
            {
                body.WetnessAccumulationAlpha = 0f;
                body.RainAccumulationAlpha = 0f;
            }
        }

        if (World.Driving is { } car)
        {
            car.WetnessAccumulationAlpha = 0f;
            car.RainAccumulationAlpha = 0f;
        }
    }

    static void SetBed(RRainComponent rain, AkEvent? bed)
    {
        if (bed is null)
        {
            rain.DisableRainSound();
        }

        rain.RainSettings.RainSoundBed = bed!;
        if (bed is not null)
        {
            rain.EnableRainSound();
        }
    }

    static Held<PhysicalMaterial, AkEvent?> Surface(Func<PhysicalMaterial, AkEvent?> read, Action<PhysicalMaterial, AkEvent?> write) =>
        new("surface rain sound", material => read(material) is { IsValid: true } sound ? sound : null, write);

    static bool Same(float[] a, float[] b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            if (MathF.Abs(a[i] - b[i]) > 0.0001f)
            {
                return false;
            }
        }

        return true;
    }
}
