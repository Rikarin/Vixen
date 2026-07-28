// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Navigation;

/// <summary>The area ids the engine reserves. Everything from 1 to 62 belongs to the game.</summary>
/// <remarks>
///     An area is a small integer stamped onto every polygon at bake time, and it is the only thing a
///     query knows about what the ground <i>is</i>. Water, mud, a road that should be preferred and a
///     stairwell that should not are all one byte and a cost multiplier — see <see cref="NavQueryFilter" />.
/// </remarks>
public static class NavArea {
    /// <summary>Not walkable. A polygon never carries it; a voxel that does is not built into one.</summary>
    public const byte Null = 0;

    /// <summary>The default for anything the bake found walkable and nothing overrode.</summary>
    public const byte Walkable = 63;

    /// <summary>The largest area id, and therefore the size of a cost table.</summary>
    public const int Count = 64;
}

/// <summary>What a polygon may be used for, as a bitmask a filter tests.</summary>
/// <remarks>
///     Area and flags answer different questions and are deliberately separate. The area is
///     <i>what the ground is</i> and drives cost; the flags are <i>who may use it</i> and drive
///     inclusion. A door polygon has an area of its own so that walking through one can be made
///     expensive, and a flag of its own so that an agent without a key never considers it at all.
/// </remarks>
[Flags]
public enum NavPolyFlags : ushort {
    /// <summary>No capability. A polygon with no flags passes no filter that requires any.</summary>
    None = 0,

    /// <summary>Ordinary ground.</summary>
    Walk = 1 << 0,

    /// <summary>Water an agent can swim across.</summary>
    Swim = 1 << 1,

    /// <summary>A doorway.</summary>
    Door = 1 << 2,

    /// <summary>A drop or a leap, rather than a continuous surface.</summary>
    Jump = 1 << 3,

    /// <summary>Temporarily closed. Kept out of every default filter.</summary>
    Disabled = 1 << 4,

    /// <summary>Everything. The include set a filter starts with.</summary>
    All = 0xffff
}

/// <summary>
///     Which polygons a query may cross, and what crossing each one costs.
/// </summary>
/// <remarks>
///     <para>
///         Two independent decisions. <see cref="IncludeFlags" /> and <see cref="ExcludeFlags" /> are
///         a yes-or-no test on capability; the area cost table is a multiplier on distance for the
///         polygons that passed it. Making an area unwalkable by giving it a huge cost is the thing
///         this shape exists to prevent — A* would still search through it, at length, and would
///         still return a path across it if there were no other.
///     </para>
///     <para>
///         A filter is a plain object with no state of its own beyond those three, so one per agent
///         type is the intended usage: build it once, hand the same instance to every query. Nothing
///         here allocates once it is constructed.
///     </para>
/// </remarks>
public sealed class NavQueryFilter {
    readonly float[] areaCosts = new float[NavArea.Count];

    /// <summary>A filter that accepts every enabled polygon at its natural cost.</summary>
    public NavQueryFilter() {
        Array.Fill(areaCosts, 1f);
        IncludeFlags = NavPolyFlags.All;
        ExcludeFlags = NavPolyFlags.Disabled;
    }

    /// <summary>A polygon is considered only if it carries at least one of these.</summary>
    public NavPolyFlags IncludeFlags { get; set; }

    /// <summary>A polygon carrying any of these is never considered.</summary>
    public NavPolyFlags ExcludeFlags { get; set; }

    /// <summary>The default filter: everything except <see cref="NavPolyFlags.Disabled" />, cost 1.</summary>
    /// <remarks>Shared and mutable, like any default. A caller that needs to change costs makes its own.</remarks>
    public static NavQueryFilter Default { get; } = new();

    /// <summary>Reads an area's cost multiplier.</summary>
    /// <param name="area">The area id.</param>
    /// <returns>The multiplier.</returns>
    public float GetAreaCost(int area) => areaCosts[area];

    /// <summary>Sets an area's cost multiplier.</summary>
    /// <param name="area">The area id.</param>
    /// <param name="cost">The multiplier. Must be positive: a zero or negative edge cost makes A* wrong, not fast.</param>
    /// <exception cref="ArgumentOutOfRangeException">The area is out of range, or the cost is not positive.</exception>
    public void SetAreaCost(int area, float cost) {
        ArgumentOutOfRangeException.ThrowIfNegative(area);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(area, NavArea.Count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cost);

        areaCosts[area] = cost;
    }

    /// <summary>Whether a polygon may be crossed.</summary>
    /// <param name="flags">The polygon's flags.</param>
    /// <returns><see langword="true" /> if it passes.</returns>
    public bool Passes(NavPolyFlags flags) => (flags & IncludeFlags) != 0 && (flags & ExcludeFlags) == 0;
}
