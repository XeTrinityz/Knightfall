#nullable enable

using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace Knightfall;

// Lends another character's animations and stances to the player's pawn. The pawn looks names up
// set by set, so a set put first wins and a set put last only fills gaps. The same lists live on
// the mesh, the animation config and every unarmed weapon config, so all of them are changed.
static class Lending
{
    // Several options may lend the same set, so it only leaves when the last of them is done.
    static readonly Dictionary<(nint Pawn, nint Set), HashSet<string>> Lenders = new();

    public static IEnumerable<TArray<AnimSet>> AnimSetLists(RPawnPlayerCombat pawn)
    {
        if (pawn.Mesh is { IsValid: true } mesh)
        {
            yield return mesh.AnimSets;
        }

        foreach (var config in WeaponConfigs(pawn))
        {
            yield return config.AnimSets;
        }
    }

    public static IEnumerable<TArray<RPoseConfig>> PoseLists(RPawnPlayerCombat pawn)
    {
        if (pawn.AnimationConfig is { IsValid: true } config)
        {
            yield return config.PoseConfigs;
        }

        foreach (var weapon in WeaponConfigs(pawn))
        {
            yield return weapon.PoseConfigs;
        }
    }

    static IEnumerable<RWeaponConfig> WeaponConfigs(RPawnPlayerCombat pawn)
    {
        var seen = new HashSet<nint>();
        var pending = new Stack<RWeaponConfig?>();
        pending.Push(pawn.UnarmedWeaponConfig);
        pending.Push(pawn.OverrideUnarmedWeaponConfig);
        while (pending.Count > 0)
        {
            if (pending.Pop() is not { IsValid: true } config || !seen.Add(config.Ptr))
            {
                continue;
            }

            yield return config;
            var extends = config.Extends;
            for (var i = 0; i < extends.Count; i++)
            {
                pending.Push(extends[i]);
            }
        }
    }

    // A borrowed set goes first only when none of its names are already on the pawn, so the
    // player's own animations are never replaced. Ahead puts it first anyway, for an option whose
    // whole point is to replace them.
    public static void AddAnimSet(RPawnPlayerCombat pawn, AnimSet set, string lender, bool ahead = false)
    {
        var key = (pawn.Ptr, set.Ptr);
        if (!Lenders.TryGetValue(key, out var lenders))
        {
            lenders = new HashSet<string>(StringComparer.Ordinal);
            Lenders[key] = lenders;
        }

        lenders.Add(lender);
        var first = ahead || !Clashes(pawn, set);
        foreach (var sets in AnimSetLists(pawn))
        {
            if (IndexOf(sets, set) >= 0)
            {
                continue;
            }

            if (first)
            {
                sets.Insert(0, set);
            }
            else
            {
                sets.Push(set);
            }
        }

        Refresh(pawn);
    }

    public static void RemoveAnimSet(RPawnPlayerCombat pawn, AnimSet set, string lender)
    {
        if (Lenders.TryGetValue((pawn.Ptr, set.Ptr), out var lenders))
        {
            lenders.Remove(lender);
            if (lenders.Count > 0)
            {
                return;
            }

            Lenders.Remove((pawn.Ptr, set.Ptr));
        }

        foreach (var sets in AnimSetLists(pawn))
        {
            for (var i = sets.Count - 1; i >= 0; i--)
            {
                if (sets[i] is { } found && found.Ptr == set.Ptr)
                {
                    sets.RemoveAt(i);
                }
            }
        }

        Refresh(pawn);
    }

    public static void AddPoses(RPawnPlayerCombat pawn, RPoseConfig poses)
    {
        foreach (var list in PoseLists(pawn))
        {
            if (IndexOf(list, poses) < 0)
            {
                list.Push(poses);
            }
        }
    }

    public static void RemovePoses(RPawnPlayerCombat pawn, RPoseConfig poses)
    {
        foreach (var list in PoseLists(pawn))
        {
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] is { } found && found.Ptr == poses.Ptr)
                {
                    list.RemoveAt(i);
                }
            }
        }
    }

    // A copy of one of the character's pose configs that holds only the stances asked for. A copy
    // made before a hot reload is found again by name.
    public static RPoseConfig? TrimmedPoses(RPawnPlayerConfig config, string name, Func<FName, bool> keep)
    {
        foreach (var made in Game.FindObjects<RPoseConfig>())
        {
            if (made.IsValid && made.Name.ToString() == name)
            {
                return made;
            }
        }

        var configs = config.PoseConfigs;
        for (var i = 0; i < configs.Count; i++)
        {
            if (configs[i] is not { IsValid: true } source || !HasStance(source, keep))
            {
                continue;
            }

            var copy = Made.Tracked(new RPoseConfig(source.Outer, name, 0, source));
            var poses = copy.Poses;
            for (var p = poses.Count - 1; p >= 0; p--)
            {
                if (!keep(poses[p].MovementStance))
                {
                    poses.RemoveAt(p);
                }
            }

            var transitions = copy.Transitions;
            for (var t = transitions.Count - 1; t >= 0; t--)
            {
                var transition = transitions[t];
                if (!keep(transition.MovementStanceA) && !keep(transition.MovementStanceB))
                {
                    transitions.RemoveAt(t);
                }
            }

            return copy;
        }

        return null;
    }

    static bool HasStance(RPoseConfig config, Func<FName, bool> keep)
    {
        var poses = config.Poses;
        for (var i = 0; i < poses.Count; i++)
        {
            if (keep(poses[i].MovementStance))
            {
                return true;
            }
        }

        return false;
    }

    static bool Clashes(RPawnPlayerCombat pawn, AnimSet set)
    {
        if (pawn.Mesh is not { IsValid: true } mesh)
        {
            return false;
        }

        var sequences = set.Sequences;
        for (var i = 0; i < sequences.Count; i++)
        {
            if (sequences[i] is { IsValid: true } sequence && mesh.FindAnimSequence(sequence.SequenceName) is { IsValid: true })
            {
                return true;
            }
        }

        return false;
    }

    static int IndexOf<T>(TArray<T> list, T item)
        where T : GameObject
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is { } found && found.Ptr == item.Ptr)
            {
                return i;
            }
        }

        return -1;
    }

    static void Refresh(RPawnPlayerCombat pawn)
    {
        if (pawn.Mesh is { IsValid: true } mesh)
        {
            mesh.UpdateAnimations();
        }
    }
}
