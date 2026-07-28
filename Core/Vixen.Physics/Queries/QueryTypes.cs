// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Physics.Bodies;

namespace Vixen.Physics.Queries;

/// <summary>Which bodies a query is allowed to see.</summary>
/// <remarks>
///     <para>
///         <see cref="IgnoreBody" /> is here rather than left to the caller because the caller cannot
///         do it: a "closest hit" query that returns the asker has thrown away the hit that was
///         wanted, and filtering afterwards gives an empty result rather than the second-closest.
///     </para>
///     <para>
///         Sensors are excluded by default. A trigger volume is not geometry — a line of sight test
///         that stops at the doorway's trigger is the bug, and the caller who does want them says so.
///     </para>
///     <para>
///         <b>The two fields are stored inverted, and that is the whole design.</b> Every query takes
///         this as an optional parameter, so <c>default</c> has to mean "no filtering". Stored
///         plainly, a default filter would carry an empty layer mask and body zero — a query that
///         hits nothing and excludes a real body — and the failure would be silent. Holding the
///         complement of the mask and the successor of the handle makes the all-zero value mean
///         exactly "every layer, no exclusion", so <c>Raycast(a, b, c, out var hit)</c> does the
///         obvious thing and <c>QueryFilter.On(PhysicsLayerMask.None)</c> still honestly means none.
///     </para>
/// </remarks>
public readonly record struct QueryFilter {
    readonly uint excludedLayers;
    readonly uint ignorePlusOne;

    /// <summary>The layers it considers.</summary>
    public PhysicsLayerMask Layers {
        get => new(~excludedLayers);
        init => excludedLayers = ~value.Bits;
    }

    /// <summary>One body to skip — almost always the one asking.</summary>
    public BodyHandle IgnoreBody {
        get => ignorePlusOne == 0 ? BodyHandle.None : new(ignorePlusOne - 1);
        init => ignorePlusOne = value.IsNone ? 0u : value.Value + 1u;
    }

    /// <summary>Whether trigger volumes count as hits.</summary>
    public bool IncludeSensors { get; init; }

    /// <summary>Everything solid, on every layer. The same value as <c>default</c>.</summary>
    public static QueryFilter Default => default;

    /// <summary>Everything on some layers.</summary>
    /// <param name="layers">The layers.</param>
    /// <returns>The filter.</returns>
    public static QueryFilter On(PhysicsLayerMask layers) => new() { Layers = layers };

    /// <summary>Everything solid except one body.</summary>
    /// <param name="body">The body to skip.</param>
    /// <returns>The filter.</returns>
    public static QueryFilter Excluding(BodyHandle body) => new() { IgnoreBody = body };

    /// <summary>Everything, sensors included.</summary>
    /// <returns>The filter.</returns>
    public static QueryFilter WithSensors() => new() { IncludeSensors = true };
}

/// <summary>Where a ray met a body.</summary>
/// <param name="Body">What it hit.</param>
/// <param name="Position">Where, in world space.</param>
/// <param name="Normal">The surface normal there, pointing out of the body.</param>
/// <param name="Distance">How far along the ray, in metres.</param>
/// <param name="Fraction">How far along the ray, from 0 at its origin to 1 at its end.</param>
public readonly record struct RayHit(
    BodyHandle Body,
    Vector3 Position,
    Vector3 Normal,
    float Distance,
    float Fraction
);

/// <summary>A body a shape query found overlapping.</summary>
/// <param name="Body">What was found.</param>
/// <param name="Position">The deepest point of the overlap, on the queried shape.</param>
/// <param name="Normal">
///     The direction the query shape would have to move to stop overlapping, normalised.
/// </param>
/// <param name="Depth">How far it would have to move, in metres.</param>
public readonly record struct ShapeOverlap(BodyHandle Body, Vector3 Position, Vector3 Normal, float Depth);

/// <summary>Where a swept shape first met a body.</summary>
/// <param name="Body">What it hit.</param>
/// <param name="Position">Where they touched, in world space.</param>
/// <param name="Normal">The surface normal there.</param>
/// <param name="Fraction">
///     How far along the sweep, from 0 to 1. Zero means the shape was already overlapping when the
///     sweep started, and <see cref="Normal" /> is then the separating direction rather than a
///     surface normal.
/// </param>
public readonly record struct ShapeCastHit(BodyHandle Body, Vector3 Position, Vector3 Normal, float Fraction);
