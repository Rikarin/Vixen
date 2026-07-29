// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Rendering.DistanceFields;

/// <summary>
///     How far every point in a box is from a mesh's surface, and which side of it the point is on.
/// </summary>
/// <remarks>
///     <para>
///         Negative inside, positive outside, zero on the surface, in world units. That sign
///         convention is the one every published tracing formulation assumes, and getting it
///         backwards is a mesh that repels rays instead of stopping them.
///     </para>
///     <para>
///         <b>Samples sit on grid points, not in voxel centres.</b> The first sample along an axis is
///         exactly at <see cref="Bounds" />' minimum and the last exactly at its maximum, so the
///         field is defined on the whole box rather than on a slightly smaller one inset by half a
///         voxel. Trilinear interpolation between grid points then covers the box with no
///         extrapolation anywhere, which is what makes <see cref="Sample" /> exact at the corners and
///         continuous everywhere between them.
///     </para>
///     <para>
///         <b>The field is only as true as it is dense.</b> Between samples it is a trilinear
///         interpolation of distances, and a trilinear interpolation of a distance function is not a
///         distance function: it under-estimates near a concave corner and over-estimates near a
///         convex one. A sphere tracer reading it therefore takes conservative steps rather than
///         exact ones, which is the standard trade and the reason a step scale below one exists at
///         all.
///     </para>
///     <para>
///         Stored as <see cref="float" /> rather than quantised into a narrow band. Correctness
///         first: the exit criterion in <c>docs/plan/19</c> is agreement with a closed form, and a
///         quantisation would be measured against this rather than instead of it.
///     </para>
/// </remarks>
[DataContract("MeshDistanceField")]
public sealed class MeshDistanceField : IDistanceField {
    /// <summary>Reconstructs a field from its parts.</summary>
    /// <param name="bounds">The box the field covers.</param>
    /// <param name="resolution">How many samples along each axis.</param>
    /// <param name="distances">The samples, X fastest then Y then Z.</param>
    public MeshDistanceField(BoundingBox bounds, Int3 resolution, float[] distances) {
        Bounds = bounds;
        Resolution = resolution;
        Distances = distances;
    }

    /// <summary>Creates an empty field, for the serializer.</summary>
    public MeshDistanceField() { }

    /// <summary>The box the field covers, in the mesh's own space.</summary>
    /// <remarks>
    ///     Grown past the mesh by <see cref="DistanceFieldBuildSettings.BoundsExpansion" />, so the
    ///     surface is inside the volume rather than on its face.
    /// </remarks>
    public BoundingBox Bounds { get; init; }

    /// <summary>How many samples along each axis. At least two everywhere.</summary>
    public Int3 Resolution { get; init; }

    /// <summary>
    ///     The samples, <see cref="Resolution" />'s volume of them, with X varying fastest and Z
    ///     slowest.
    /// </summary>
    public float[] Distances { get; init; } = [];

    /// <summary>The world-space size of one cell along each axis.</summary>
    /// <remarks>
    ///     <see cref="Resolution" /> minus one, not <see cref="Resolution" />, because the samples are
    ///     on the grid points and a grid of <i>n</i> points has <i>n − 1</i> gaps between them.
    /// </remarks>
    public Vector3 CellSize =>
        Bounds.Size / new Vector3(Resolution.X - 1, Resolution.Y - 1, Resolution.Z - 1);

    /// <summary>The signed distance at one grid point.</summary>
    /// <param name="x">The sample's index along X.</param>
    /// <param name="y">Along Y.</param>
    /// <param name="z">Along Z.</param>
    /// <returns>The distance, negative inside.</returns>
    public float this[int x, int y, int z] => Distances[Index(x, y, z)];

    /// <summary>Where one grid point is, in the field's own space.</summary>
    /// <param name="x">The sample's index along X.</param>
    /// <param name="y">Along Y.</param>
    /// <param name="z">Along Z.</param>
    /// <returns>The position.</returns>
    public Vector3 PositionOf(int x, int y, int z) =>
        Bounds.Minimum + (CellSize * new Vector3(x, y, z));

    /// <summary>Where a point sits in a volume texture holding <see cref="Distances" />, as 0..1.</summary>
    /// <param name="position">The point, in the field's own space.</param>
    /// <returns>The texture coordinate.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The one convention the CPU and the shader have to share, so it is written down once
    ///         here and read from both sides.</b> A sample lives at the <i>centre</i> of its texel and
    ///         sample <c>i</c> is grid point <c>i</c>, so grid point <c>i</c> is at
    ///         <c>(i + ½) / count</c>. Drop the half and the whole field shifts half a cell along
    ///         every axis — geometry subtly in the wrong place, invisible in a still frame, and
    ///         exactly the defect two implementations tested separately against arithmetic would each
    ///         pass.
    ///     </para>
    ///     <para>
    ///         <see cref="Sample" /> does not go through this: it interpolates the array directly,
    ///         which is the same arithmetic with the texel grid divided out. That they agree is
    ///         asserted rather than assumed.
    ///     </para>
    /// </remarks>
    public Vector3 TextureCoordinate(Vector3 position) {
        var grid = ToGrid(position);
        var count = new Vector3(Resolution.X, Resolution.Y, Resolution.Z);

        return (grid + new Vector3(0.5f)) / count;
    }

    /// <summary>The signed distance at any point, trilinearly interpolated.</summary>
    /// <param name="position">The point, in the field's own space.</param>
    /// <returns>The distance, negative inside.</returns>
    /// <remarks>
    ///     A point outside <see cref="Bounds" /> is clamped to it rather than extrapolated, which
    ///     under-reports its distance — it is at least as far away as the answer says. That is the
    ///     safe direction for a tracer: a step that is too short costs an iteration, a step that is
    ///     too long costs a missed surface.
    /// </remarks>
    public float Sample(Vector3 position) {
        var grid = ToGrid(position);
        var x0 = (int)MathF.Floor(grid.X);
        var y0 = (int)MathF.Floor(grid.Y);
        var z0 = (int)MathF.Floor(grid.Z);

        x0 = Math.Clamp(x0, 0, Resolution.X - 2);
        y0 = Math.Clamp(y0, 0, Resolution.Y - 2);
        z0 = Math.Clamp(z0, 0, Resolution.Z - 2);

        var tx = Math.Clamp(grid.X - x0, 0f, 1f);
        var ty = Math.Clamp(grid.Y - y0, 0f, 1f);
        var tz = Math.Clamp(grid.Z - z0, 0f, 1f);

        var c00 = Lerp(this[x0, y0, z0], this[x0 + 1, y0, z0], tx);
        var c10 = Lerp(this[x0, y0 + 1, z0], this[x0 + 1, y0 + 1, z0], tx);
        var c01 = Lerp(this[x0, y0, z0 + 1], this[x0 + 1, y0, z0 + 1], tx);
        var c11 = Lerp(this[x0, y0 + 1, z0 + 1], this[x0 + 1, y0 + 1, z0 + 1], tx);

        return Lerp(Lerp(c00, c10, ty), Lerp(c01, c11, ty), tz);
    }

    /// <summary>Which way the distance grows fastest at a point — the surface normal, near one.</summary>
    /// <param name="position">The point, in the field's own space.</param>
    /// <returns>The normalised gradient, or zero where the field is flat.</returns>
    /// <remarks>
    ///     Central differences one cell wide. Wider is smoother and less true; narrower is not
    ///     available, because a difference inside one cell of a trilinear field is the cell's own
    ///     constant gradient and carries no information the sample did not already have.
    /// </remarks>
    public Vector3 SampleGradient(Vector3 position) {
        var cell = CellSize;
        var gradient = new Vector3(
            Sample(position + new Vector3(cell.X, 0, 0)) - Sample(position - new Vector3(cell.X, 0, 0)),
            Sample(position + new Vector3(0, cell.Y, 0)) - Sample(position - new Vector3(0, cell.Y, 0)),
            Sample(position + new Vector3(0, 0, cell.Z)) - Sample(position - new Vector3(0, 0, cell.Z))
        );

        var length = gradient.Length();

        return length > MathUtil.ZeroTolerance ? gradient / length : Vector3.Zero;
    }

    /// <summary>Throws if the field's parts do not describe a field.</summary>
    /// <exception cref="InvalidOperationException">They do not.</exception>
    public void Validate() {
        if (Resolution.X < 2 || Resolution.Y < 2 || Resolution.Z < 2) {
            throw new InvalidOperationException(
                $"A field needs at least two samples along every axis, not {Resolution}."
            );
        }

        if (Distances.Length != Resolution.Volume) {
            throw new InvalidOperationException(
                $"{Resolution} is {Resolution.Volume} samples, but {Distances.Length} were given."
            );
        }

        if (Bounds.IsEmpty) {
            throw new InvalidOperationException("A field over an empty box describes nothing.");
        }
    }

    /// <summary>Where a sample lives in <see cref="Distances" />.</summary>
    /// <param name="x">The sample's index along X.</param>
    /// <param name="y">Along Y.</param>
    /// <param name="z">Along Z.</param>
    /// <returns>The flat index.</returns>
    internal int Index(int x, int y, int z) => x + (Resolution.X * (y + (Resolution.Y * z)));

    /// <summary>A position in continuous grid coordinates, where one unit is one cell.</summary>
    /// <param name="position">The point, in the field's own space.</param>
    /// <returns>The grid coordinate, not yet clamped.</returns>
    Vector3 ToGrid(Vector3 position) {
        var cell = CellSize;
        var local = position - Bounds.Minimum;

        return new(
            cell.X > 0 ? local.X / cell.X : 0,
            cell.Y > 0 ? local.Y / cell.Y : 0,
            cell.Z > 0 ? local.Z / cell.Z : 0
        );
    }

    static float Lerp(float from, float to, float amount) => from + ((to - from) * amount);
}
