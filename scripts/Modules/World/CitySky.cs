#nullable enable

using System.Numerics;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// The sky over the city: PRE-SCARECROW ERA's dusk sky, or the one picked under SCENE > SKY. Each
// sky is cooked into a package of its own. That package is loaded into memory without joining the
// world, so none of its scripts or people start, and copies of the sky's pieces are placed in the
// city. The night dome is hidden with the same event the Gordon chapter fires, once the new sky
// is up. Lightning and rain are held off for the era by their own owners.
static class CitySky
{
    sealed record Dome(string Package, string Mesh, string Name, string Needs);

    // The Gordon section's sky, cooked only into its chapter package.
    static readonly Dome Dusk = new("CityZ_10_Ch2C", "XFX_Merter.dome.Dome_Rise", "dusk sky", "");

    // GCPD Lockdown's sky, placed in city coordinates in the vista of its map.
    static readonly Dome Overcast = new("GSGCPD_01_Vista", "GSGCPD_Content.GSGCPD_Skybox", "overcast sky", "GCPD LOCKDOWN");

    const string SunMaterial = "XFX_Merter.dome.sun_M";

    // The dusk sun is one sphere in this collection of the chapter's static meshes.
    const string SunCollection = "StaticMeshCollectionActor_7042";

    public static readonly Choice Sky = new(
        "world.atmosphere.sky", "SKY",
        "Puts a different sky over the city. OVERCAST is the GCPD Lockdown sky and needs that DLC. Turning on PRE-SCARECROW ERA puts this back to GAME.",
        new[] { ("game", "GAME"), ("overcast", "OVERCAST") })
    {
        Changed = static () =>
        {
            if (Sky!.Index > 0)
            {
                Atmosphere.Era.SetTo(false);
            }
        },
    };

    static readonly List<Actor> Spawned = new();

    // The world the sky was set up in, and which sky. A new world or a new pick needs it again.
    static WorldInfo? _world;
    static Dome? _up;
    static bool _hidden;

    public static void Tick()
    {
        var want = Atmosphere.Era.On ? Dusk : Sky.Index == 1 ? Overcast : null;
        if (want is null)
        {
            if (_world is not null)
            {
                Release();
            }

            return;
        }

        if (!Game.TryGetWorldInfo(out var world) || !world.IsValid)
        {
            return;
        }

        if (ReferenceEquals(world, _world) && ReferenceEquals(want, _up))
        {
            return;
        }

        if (ReferenceEquals(world, _world))
        {
            Remove();
        }
        else
        {
            // The old world took its actors and its hidden dome with it.
            Spawned.Clear();
            _hidden = false;
        }

        _world = world;
        _up = want;
        if (Build(want))
        {
            NightDome(hide: true);
        }
        else if (_hidden)
        {
            NightDome(hide: false);
        }
    }

    public static void Release()
    {
        Remove();
        if (_hidden && _world is { IsValid: true })
        {
            NightDome(hide: false);
        }

        _hidden = false;
        _world = null;
        _up = null;
    }

    // Things from a sky's package, loaded but never part of the world.
    public static bool Owns(GameObject thing)
    {
        var path = thing.GetPathName(null);
        return path.StartsWith(Dusk.Package + ".", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(Overcast.Package + ".", StringComparison.OrdinalIgnoreCase);
    }

    static void Remove()
    {
        foreach (var actor in Spawned)
        {
            if (actor.IsValid)
            {
                actor.Destroy();
            }
        }

        Spawned.Clear();
    }

    // The city's night dome listens for these, and the Gordon chapter fires the first.
    static void NightDome(bool hide)
    {
        World.Info?.ActivateRemoteEvent(new FName(hide ? "Hide_Dome" : "UNHide_Dome"));
        _hidden = hide;
    }

    static bool Build(Dome sky)
    {
        var mesh = Game.FindObject<StaticMesh>(sky.Mesh);
        if (mesh is null)
        {
            if (!GameFiles.HasPackage(sky.Package))
            {
                return Fail(sky, sky.Needs.Length > 0 ? $"it needs the {sky.Needs} DLC" : "its package isn't installed", missing: true);
            }

            var cost = GameFiles.Exports(sky.Package);
            if (ObjectTable.Headroom < (cost > 0 ? cost : 10000) + Packages.Spare)
            {
                return Fail(sky, "there isn't room to load it right now");
            }

            Game.LoadPackage(sky.Package);
            mesh = Game.FindObject<StaticMesh>(sky.Mesh);
            if (mesh is null)
            {
                return Fail(sky, "it didn't load");
            }
        }

        var prefix = sky.Package + ".";
        var withSun = ReferenceEquals(sky, Dusk);
        StaticMeshComponent? domeSource = null;
        StaticMeshComponent? sunSource = null;
        foreach (var part in Game.FindObjects<StaticMeshComponent>())
        {
            if (!part.IsValid || part.IsClassDefaultObject)
            {
                continue;
            }

            // Cheap checks first. Asking for a path is a script call, and there are thousands.
            var isDome = ReferenceEquals(part.StaticMesh, mesh);
            if (!isDome && !(withSun && part.Outer is StaticMeshCollectionActor))
            {
                continue;
            }

            var path = part.GetPathName(null);
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (isDome)
            {
                domeSource = part;
            }
            else if (path.Contains(SunCollection, StringComparison.OrdinalIgnoreCase))
            {
                sunSource = part;
            }
        }

        // The dome hangs off one of the package's toggleable mesh actors, which keeps its own place.
        if (domeSource is null)
        {
            foreach (var holder in Game.FindObjects<RStaticMeshToggleable>())
            {
                if (holder.IsValid && !holder.IsClassDefaultObject && holder.StaticMeshComponent is { IsValid: true } part
                    && ReferenceEquals(part.StaticMesh, mesh) && holder.GetPathName(null).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    domeSource = part;
                    break;
                }
            }
        }

        if (domeSource is null)
        {
            return Fail(sky, "its place wasn't found");
        }

        if (Copy(domeSource) is null)
        {
            return false;
        }

        if (withSun)
        {
            // The sun sphere's own material is a fog shader. The chapter gives it the sun instead.
            if (sunSource is not null && Copy(sunSource) is { StaticMeshComponent: { IsValid: true } sphere }
                && Game.FindObject<MaterialInterface>(SunMaterial) is { IsValid: true } sun)
            {
                sphere.SetMaterial(0, sun);
            }

            Flare(prefix);
        }

        return true;
    }

    // A package loaded outside the world never places its parts, so the place comes from what
    // the package saved: an actor's own place for the dome, or the part's own for the sun.
    static DynamicSMActor? Copy(StaticMeshComponent source)
    {
        Vector3 at;
        Rotator facing;
        Vector3 size;
        if (source.Outer is Actor { IsValid: true } holder and not StaticMeshCollectionActor)
        {
            at = holder.Location + source.Translation;
            facing = holder.Rotation;
            size = holder.DrawScale3D * holder.DrawScale * source.Scale3D * source.Scale;
        }
        else
        {
            at = source.Translation;
            facing = source.Rotation;
            size = source.Scale3D * source.Scale;
        }

        if (Game.SpawnActor(DynamicSMActor_Spawnable.StaticClass(), default, at, facing) is not DynamicSMActor copy)
        {
            Log.Warn("city_sky.place", "A piece of the sky couldn't be placed.");
            return null;
        }

        copy.SetCollision(false, false, false);
        copy.SetDrawScale3D(size);
        if (copy.StaticMeshComponent is { IsValid: true } part)
        {
            part.CastShadow = false;
            part.SetStaticMesh(source.StaticMesh, true);
            for (var i = 0; i < source.GetNumElements(); i++)
            {
                if (source.GetMaterial(i) is { IsValid: true } material)
                {
                    part.SetMaterial(i, material);
                }
            }
        }

        Spawned.Add(copy);
        return copy;
    }

    // The dusk sun's glow is a flare the chapter places, which reuses a Batmobile brake light flare.
    static void Flare(string prefix)
    {
        LensFlareSource? source = null;
        foreach (var flare in Game.FindObjects<LensFlareSource>())
        {
            if (flare.IsValid && !flare.IsClassDefaultObject && flare.GetPathName(null).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                source = flare;
            }
        }

        if (source?.LensFlareComp is not { IsValid: true } from || from.Template is not { IsValid: true } template)
        {
            return;
        }

        if (Game.SpawnActor(LensFlareSource.StaticClass(), default, source.Location, source.Rotation) is not LensFlareSource copy
            || copy.LensFlareComp is not { IsValid: true } to)
        {
            Log.Warn("city_sky.flare", "The dusk sun's glow couldn't be placed.");
            return;
        }

        to.SetTemplate(template, true);
        to.SetIsActive(true);
        copy.SetDrawScale(source.DrawScale);
        copy.SetDrawScale3D(source.DrawScale3D);
        Spawned.Add(copy);
    }

    static bool Fail(Dome sky, string why, bool missing = false)
    {
        if (ReferenceEquals(sky, Dusk))
        {
            Log.Warn("city_sky.dusk", "PRE-SCARECROW ERA: the dusk sky wasn't put up because " + why + ". The look still applies.");
        }
        else
        {
            Log.Warn("city_sky." + sky.Package, $"SKY: the {sky.Name} wasn't put up because {why}.");
            Toasts.Show("SKY", missing && sky.Needs.Length > 0 ? "NEEDS " + sky.Needs : "COULDN'T LOAD", OutcomeKind.Failed, Sky);
        }

        return false;
    }
}
