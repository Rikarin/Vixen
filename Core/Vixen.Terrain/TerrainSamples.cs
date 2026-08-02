// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Terrain;

/// <summary>
///     One grid of 16-bit height samples covering a whole terrain.
/// </summary>
/// <remarks>
///     <para>
///         <b>One grid, not one per tile, and that is what makes [docs/plan/31 § D2]'s warning
///         structural.</b> The warning says tile boundaries are shared vertices and that storing them
///         twice is how a terrain grows a seam which appears only after somebody edits one side.
///         Storing the terrain as an array of per-tile grids makes that a rule every tool has to
///         remember; storing it as one grid that tiles are <em>windows</em> into makes it impossible
///         to break, because there is only ever one copy of the sample to write.
///     </para>
///     <para>
///         The cost is that a tile is not contiguous, which matters to exactly one consumer — the
///         thing uploading a tile's texture, which copies row by row. That is a loop in the renderer
///         rather than a class of bug in every tool.
///     </para>
///     <para>
///         <b>16-bit unsigned over the description's own range</b>, so the quantum is
///         <see cref="TerrainDescription.MetresPerStep" /> and a 40 m landscape gets sub-millimetre
///         precision rather than the centimetres a fixed window would give it.
///     </para>
/// </remarks>
public sealed class TerrainSamples {
    /// <summary>The largest a stored sample can be.</summary>
    public const int MaxHeight = ushort.MaxValue;

    readonly ushort[] heights;

    /// <summary>Allocates a grid, every sample at <paramref name="fill" />.</summary>
    /// <param name="description">The terrain's shape.</param>
    /// <param name="fill">What every sample starts at.</param>
    /// <exception cref="ArgumentException">The description is not one a terrain can be built from.</exception>
    public TerrainSamples(TerrainDescription description, ushort fill = 0) {
        if (description.Validate() is { } reason) {
            throw new ArgumentException(reason, nameof(description));
        }

        Description = description;
        heights = new ushort[description.SampleCount];

        if (fill != 0) {
            Array.Fill(heights, fill);
        }
    }

    /// <summary>The terrain's shape.</summary>
    public TerrainDescription Description { get; }

    /// <summary>How many samples there are along X.</summary>
    public int Width => Description.SamplesX;

    /// <summary>How many samples there are along Z.</summary>
    public int Height => Description.SamplesZ;

    /// <summary>Everything covered by the grid.</summary>
    public TerrainRect Bounds => new(0, 0, Width, Height);

    /// <summary>The raw samples, row-major in Z then X.</summary>
    public Span<ushort> Span => heights;

    /// <summary>One sample.</summary>
    /// <param name="x">Its X index.</param>
    /// <param name="z">Its Z index.</param>
    /// <remarks>
    ///     Clamped on read and ignored on write for an index outside the grid, because every kernel
    ///     here reads its neighbours and the edge of a terrain is where half of them are outside. A
    ///     throw would make every kernel carry its own bounds check; clamping makes the edge behave
    ///     as if the terrain continued flat, which is what a smooth at the boundary should do.
    /// </remarks>
    public ushort this[int x, int z] {
        get => heights[(Math.Clamp(z, 0, Height - 1) * Width) + Math.Clamp(x, 0, Width - 1)];

        set {
            if ((uint)x < (uint)Width && (uint)z < (uint)Height) {
                heights[(z * Width) + x] = value;
            }
        }
    }

    /// <summary>One sample, in metres.</summary>
    /// <param name="x">Its X index.</param>
    /// <param name="z">Its Z index.</param>
    /// <returns>The height in metres.</returns>
    public float MetresAt(int x, int z) => Description.HeightOf(this[x, z]);

    /// <summary>Copies a rectangle out.</summary>
    /// <param name="rect">Which samples. Clipped to the grid.</param>
    /// <param name="destination">Where to put them, row-major, at least <c>rect.Count</c> long.</param>
    /// <returns>How many samples were copied.</returns>
    public int Read(TerrainRect rect, Span<ushort> destination) {
        var clipped = rect.Clip(Bounds);

        if (clipped.IsEmpty) {
            return 0;
        }

        if (destination.Length < clipped.Count) {
            throw new ArgumentException(
                $"{clipped.Count} samples need {clipped.Count} slots, not {destination.Length}.",
                nameof(destination)
            );
        }

        for (var row = 0; row < clipped.Height; row++) {
            var source = ((clipped.Z + row) * Width) + clipped.X;
            heights.AsSpan(source, clipped.Width).CopyTo(destination[(row * clipped.Width)..]);
        }

        return clipped.Count;
    }

    /// <summary>Copies a rectangle in.</summary>
    /// <param name="rect">Which samples. Clipped to the grid.</param>
    /// <param name="source">The samples, row-major over the <em>unclipped</em> rectangle.</param>
    /// <remarks>
    ///     ⚠ <b>The source is indexed against the rectangle it was asked for, not the clipped one.</b>
    ///     An undo record holds a rectangle that may hang off the edge — a stroke near the boundary
    ///     does — and restoring it by walking the clipped rectangle's stride would shear the data
    ///     sideways by the amount that was clipped.
    /// </remarks>
    public void Write(TerrainRect rect, ReadOnlySpan<ushort> source) {
        var clipped = rect.Clip(Bounds);

        if (clipped.IsEmpty) {
            return;
        }

        for (var row = 0; row < clipped.Height; row++) {
            var offset = ((clipped.Z - rect.Z + row) * rect.Width) + (clipped.X - rect.X);
            var destination = ((clipped.Z + row) * Width) + clipped.X;

            source.Slice(offset, clipped.Width).CopyTo(heights.AsSpan(destination, clipped.Width));
        }
    }

    /// <summary>The lowest and highest sample in a rectangle.</summary>
    /// <param name="rect">Which samples. Clipped to the grid.</param>
    /// <returns>The range, or <c>(MaxHeight, 0)</c> for an empty rectangle.</returns>
    /// <remarks>
    ///     What a tile's bounding box is maintained from. Inverted for an empty rectangle rather than
    ///     zeroed, so a caller reducing several rectangles together does not have an empty one drag
    ///     the minimum to the bottom of the range.
    /// </remarks>
    public (ushort Minimum, ushort Maximum) RangeOf(TerrainRect rect) {
        var clipped = rect.Clip(Bounds);

        if (clipped.IsEmpty) {
            return (ushort.MaxValue, ushort.MinValue);
        }

        var minimum = ushort.MaxValue;
        var maximum = ushort.MinValue;

        for (var row = 0; row < clipped.Height; row++) {
            var start = ((clipped.Z + row) * Width) + clipped.X;

            foreach (var sample in heights.AsSpan(start, clipped.Width)) {
                minimum = Math.Min(minimum, sample);
                maximum = Math.Max(maximum, sample);
            }
        }

        return (minimum, maximum);
    }

    /// <summary>A copy of this grid.</summary>
    /// <returns>The copy, sharing nothing.</returns>
    public TerrainSamples Clone() {
        var copy = new TerrainSamples(Description);
        heights.CopyTo(copy.heights, 0);
        return copy;
    }

    /// <summary>
    ///     Fills a tile's samples for a physics height field, in metres, with holes punched out.
    /// </summary>
    /// <param name="tileX">The tile's X index.</param>
    /// <param name="tileZ">The tile's Z index.</param>
    /// <param name="holes">Which samples are holes, or null for none.</param>
    /// <param name="noCollision">
    ///     What a hole is written as. <c>PhysicsShapes.NoCollisionHeight</c>, passed in because this
    ///     assembly does not reference the physics one.
    /// </param>
    /// <param name="destination">
    ///     <c>TileSamples²</c> floats, row-major, which is what a height-field shape takes.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         <b>The seam between the kernel and [docs/plan/31 § D10].</b> `Vixen.Terrain` references
    ///         only the mathematics assembly — [§ D1] — so it cannot name a <c>ShapeDescription</c>,
    ///         and `Vixen.Physics` has no business knowing what an edit layer is. What they can agree
    ///         about is an array of floats and a sentinel, which is what a height field <em>is</em>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Metres, not stored samples.</b> A height field's Y scale is one number for the
    ///         whole shape, and the terrain's is one number for the whole terrain, so passing stored
    ///         samples would mean the physics side re-deriving a range it has no reason to know. The
    ///         cost is four bytes a sample in a buffer that is filled and handed straight on.
    ///     </para>
    /// </remarks>
    public void FillCollisionSamples(
        int tileX,
        int tileZ,
        TerrainHoles? holes,
        float noCollision,
        Span<float> destination
    ) {
        var rect = Description.SamplesOf(tileX, tileZ);

        if (destination.Length < rect.Count) {
            throw new ArgumentException(
                $"A tile of {Description.TileSamples} samples needs {rect.Count} floats, "
                + $"not {destination.Length}.",
                nameof(destination)
            );
        }

        for (var row = 0; row < rect.Height; row++) {
            for (var column = 0; column < rect.Width; column++) {
                var x = rect.X + column;
                var z = rect.Z + row;

                destination[(row * rect.Width) + column] = holes?.IsHole(x, z) == true
                    ? noCollision
                    : MetresAt(x, z);
            }
        }
    }
}
