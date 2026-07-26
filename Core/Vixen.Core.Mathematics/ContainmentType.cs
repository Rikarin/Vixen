// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Mathematics;

/// <summary>How one volume sits relative to another.</summary>
/// <remarks>
///     Culling needs all three, not a bool: a fully contained subtree can skip testing its children
///     entirely, which is most of what a hierarchy buys.
/// </remarks>
public enum ContainmentType {
    /// <summary>No overlap at all.</summary>
    Disjoint,

    /// <summary>Entirely inside.</summary>
    Contains,

    /// <summary>Partly inside — straddling the boundary.</summary>
    Intersects
}

/// <summary>Which side of a plane something is on.</summary>
public enum PlaneIntersectionType {
    /// <summary>Entirely behind — the side the normal points away from.</summary>
    Back,

    /// <summary>Entirely in front — the side the normal points toward.</summary>
    Front,

    /// <summary>Crossing the plane.</summary>
    Intersecting
}
