// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;

namespace Vixen.Rendering.IrradianceFields;

/// <summary>A fixed number of brick-sized slots, and the probes filling them.</summary>
/// <remarks>
///     <para>
///         <b>Four probes cubed, in a footprint of five cubed.</b> Sixty-four probes belong to a
///         brick; a hundred and twenty-five texels hold them. The extra plane on each of the three
///         positive faces holds the <i>neighbouring</i> brick's first probe — the same world position,
///         the same value, stored twice — which is what lets one hardware trilinear fetch cross a
///         brick boundary without knowing there was one. It is Epic's volumetric-lightmap detail, and
///         <c>docs/plan/19</c> § 3 singles it out as the one everybody rediscovers the hard way.
///     </para>
///     <para>
///         <b>The probes are on the lattice, not in the middle of cells.</b> A brick's probe
///         <c>i</c> sits at <c>i/4</c> of the way across it, so probe 4 falls exactly on the far face
///         — exactly where the next brick's probe 0 is. Any other convention makes the border texel an
///         <i>approximation</i> of the neighbour rather than a copy of it, and the seam comes back at
///         a smaller amplitude, which is worse than a visible one.
///     </para>
///     <para>
///         <b>The pool is a volume texture that has not been uploaded yet.</b> Slots are laid out in a
///         3D grid so a slot's origin is integer arithmetic, and <see cref="Texels" /> is in the order
///         a 3D copy wants — X fastest, then Y, then Z. Nothing here touches a device; what does lives
///         above this line, and the dependency runs one way.
///     </para>
///     <para>
///         <b>Capacity is fixed, which is a decision rather than a simplification.</b> A pool that
///         grows reallocates the texture it is a mirror of, mid-frame, at the exact moment a scene got
///         complicated. Doc 19 § 7's platform matrix notes sparse residency as optional precisely
///         because a fixed pool works — running out means the furthest bricks are not resident, which
///         is a quality reduction, not a failure.
///     </para>
/// </remarks>
public sealed class IrradianceBrickPool {
    /// <summary>How many probes along each axis of a brick belong to it.</summary>
    public const int BrickResolution = 4;

    /// <summary>How many texels along each axis a brick occupies, borders included.</summary>
    public const int PaddedResolution = BrickResolution + 1;

    /// <summary>How many texels one brick occupies.</summary>
    public const int TexelsPerBrick = PaddedResolution * PaddedResolution * PaddedResolution;

    readonly IrradianceProbe[] texels;

    /// <summary>Slots not currently handed out, most recently released first.</summary>
    readonly int[] free;

    readonly bool[] taken;

    int freeCount;

    /// <summary>Builds an empty pool of a given shape.</summary>
    /// <param name="slots">How many brick slots along each axis.</param>
    /// <exception cref="ArgumentOutOfRangeException">An axis holds no slots.</exception>
    public IrradianceBrickPool(Int3 slots) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slots.X);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slots.Y);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slots.Z);

        Slots = slots;

        // Checked rather than trusted: the texel count is a hundred and twenty-five times the slot
        // count, so a pool only a few hundred slots along an axis silently wraps an int and produces
        // a negative length — which surfaces as an unrelated exception somewhere far from here.
        Capacity = checked(slots.X * slots.Y * slots.Z);
        texels = new IrradianceProbe[checked((long)Capacity * TexelsPerBrick)];
        free = new int[Capacity];
        taken = new bool[Capacity];

        Clear();
    }

    /// <summary>Builds an empty pool holding at least a given number of bricks.</summary>
    /// <param name="capacity">How many bricks it has to hold.</param>
    /// <returns>The pool, whose capacity is the next cube that fits.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No bricks were asked for.</exception>
    /// <remarks>
    ///     Rounded up to a cube rather than laid out as a row, because the texture this becomes is
    ///     limited along every axis and a pool of a few thousand bricks is fifteen thousand texels
    ///     along one of them otherwise.
    /// </remarks>
    public static IrradianceBrickPool OfCapacity(int capacity) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        var side = (int)Math.Ceiling(Math.Cbrt(capacity));

        return new(new(Math.Max(1, side)));
    }

    /// <summary>How many brick slots there are along each axis.</summary>
    public Int3 Slots { get; }

    /// <summary>How many bricks the pool can hold.</summary>
    public int Capacity { get; }

    /// <summary>How many bricks are currently handed out.</summary>
    public int Count => Capacity - freeCount;

    /// <summary>How many texels the pool is along each axis.</summary>
    public Int3 TexelResolution => Slots * PaddedResolution;

    /// <summary>Every texel, in the order a volume copy wants them.</summary>
    /// <remarks>
    ///     Read-only, because a caller writing here would be writing past the borders that
    ///     <see cref="IrradianceField.SyncBorders" /> maintains. Probes go in through the indexer.
    /// </remarks>
    public ReadOnlySpan<IrradianceProbe> Texels => texels;

    /// <summary>Hands out a slot.</summary>
    /// <param name="slot">The slot, if one was free.</param>
    /// <returns>Whether one was.</returns>
    /// <remarks>
    ///     False rather than an exception: running out of pool is an expected state of a scene bigger
    ///     than its budget, and the caller's answer is to leave a brick unallocated, not to stop.
    /// </remarks>
    public bool TryAllocate(out int slot) {
        if (freeCount == 0) {
            slot = -1;

            return false;
        }

        slot = free[--freeCount];
        taken[slot] = true;

        return true;
    }

    /// <summary>Gives a slot back, and forgets what was in it.</summary>
    /// <param name="slot">The slot.</param>
    /// <exception cref="ArgumentOutOfRangeException">There is no such slot.</exception>
    /// <exception cref="InvalidOperationException">The slot was not handed out.</exception>
    /// <remarks>
    ///     <b>Cleared on release rather than on allocation</b>, so a slot never holds a previous
    ///     brick's lighting while it waits. Handing out a dirty slot shows as one frame of somewhere
    ///     else's colour in a place light has not reached yet — which reads as a flicker and gets
    ///     blamed on the temporal filter.
    /// </remarks>
    public void Release(int slot) {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, Capacity);

        if (!taken[slot]) {
            throw new InvalidOperationException($"Slot {slot} was not allocated, so it cannot be released.");
        }

        taken[slot] = false;
        free[freeCount++] = slot;

        var origin = OriginOf(slot);

        for (var z = 0; z < PaddedResolution; z++) {
            for (var y = 0; y < PaddedResolution; y++) {
                for (var x = 0; x < PaddedResolution; x++) {
                    texels[TexelIndex(origin.X + x, origin.Y + y, origin.Z + z)] = IrradianceProbe.Empty;
                }
            }
        }
    }

    /// <summary>Gives every slot back and empties every texel.</summary>
    public void Clear() {
        Array.Clear(texels);
        Array.Clear(taken);

        freeCount = Capacity;

        // Descending, because the stack pops from the end — so the first brick a fresh pool hands
        // out is slot zero, and a test can say which slot went where.
        for (var index = 0; index < Capacity; index++) {
            free[index] = Capacity - 1 - index;
        }
    }

    /// <summary>Whether a slot is currently handed out.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>Whether it is.</returns>
    public bool IsAllocated(int slot) => slot >= 0 && slot < Capacity && taken[slot];

    /// <summary>One texel of one brick.</summary>
    /// <param name="slot">Which brick.</param>
    /// <param name="x">The texel's index along X, 0 to 4.</param>
    /// <param name="y">Along Y.</param>
    /// <param name="z">Along Z.</param>
    /// <returns>The probe.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The slot or a coordinate is out of range.</exception>
    /// <remarks>
    ///     Coordinates run to four, not three: four is the border, and the border is a texel like any
    ///     other. What makes it a border is only that <see cref="IrradianceField.SyncBorders" />
    ///     overwrites it with the neighbour's.
    /// </remarks>
    public IrradianceProbe this[int slot, int x, int y, int z] {
        get {
            var origin = Located(slot, x, y, z);

            return texels[TexelIndex(origin.X + x, origin.Y + y, origin.Z + z)];
        }
        set {
            var origin = Located(slot, x, y, z);

            texels[TexelIndex(origin.X + x, origin.Y + y, origin.Z + z)] = value;
        }
    }

    /// <summary>Where a slot starts, in texels.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The origin of its footprint.</returns>
    public Int3 OriginOf(int slot) {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, Capacity);

        var x = slot % Slots.X;
        var y = slot / Slots.X % Slots.Y;
        var z = slot / (Slots.X * Slots.Y);

        return new Int3(x, y, z) * PaddedResolution;
    }

    /// <summary>The probe anywhere inside a brick, trilinearly interpolated.</summary>
    /// <param name="slot">Which brick.</param>
    /// <param name="local">Where in it, 0 to 1 along each axis.</param>
    /// <returns>The interpolated probe.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such slot.</exception>
    /// <remarks>
    ///     <para>
    ///         <c>local</c> spans the brick, so it reaches probe 4 — the border — at one. That is the
    ///         whole seam argument in one line: a sample at the far face of brick A reads A's copy of
    ///         B's first probe, a sample at the near face of B reads B's own, and the two are the same
    ///         number.
    ///     </para>
    ///     <para>
    ///         Interpolating the coefficients is interpolating the lighting, exactly, because the
    ///         projection is linear — the property <see cref="SphericalHarmonicsL1.Lerp" /> rests on.
    ///     </para>
    /// </remarks>
    public IrradianceProbe Sample(int slot, Vector3 local) {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, Capacity);

        Weights(local.X, out var x0, out var fx);
        Weights(local.Y, out var y0, out var fy);
        Weights(local.Z, out var z0, out var fz);

        var c00 = IrradianceProbe.Lerp(this[slot, x0, y0, z0], this[slot, x0 + 1, y0, z0], fx);
        var c10 = IrradianceProbe.Lerp(this[slot, x0, y0 + 1, z0], this[slot, x0 + 1, y0 + 1, z0], fx);
        var c01 = IrradianceProbe.Lerp(this[slot, x0, y0, z0 + 1], this[slot, x0 + 1, y0, z0 + 1], fx);
        var c11 = IrradianceProbe.Lerp(this[slot, x0, y0 + 1, z0 + 1], this[slot, x0 + 1, y0 + 1, z0 + 1], fx);

        return IrradianceProbe.Lerp(
            IrradianceProbe.Lerp(c00, c10, fy),
            IrradianceProbe.Lerp(c01, c11, fy),
            fz
        );
    }

    /// <summary>Where a point inside a brick sits in a volume texture holding the pool, as 0..1.</summary>
    /// <param name="slot">Which brick.</param>
    /// <param name="local">Where in it, 0 to 1 along each axis.</param>
    /// <returns>The texture coordinate.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The convention the CPU and the shader have to share, written down once.</b> A texel's
    ///         value lives at its centre, so texel <c>i</c> of the pool is at <c>(i + ½)</c> texels —
    ///         and a brick's <c>local</c> of zero is texel zero's centre, not its corner. Dropping the
    ///         half shifts every probe half a texel, which is lighting subtly in the wrong place and a
    ///         defect two implementations tested separately against arithmetic would each pass.
    ///     </para>
    ///     <para>
    ///         <see cref="Sample" /> does not go through this — it interpolates the array directly,
    ///         which is the same arithmetic with the texel grid divided out. That they agree is
    ///         asserted rather than assumed, the same way <c>MeshDistanceField</c>'s is.
    ///     </para>
    /// </remarks>
    public Vector3 TextureCoordinate(int slot, Vector3 local) {
        var origin = OriginOf(slot);
        var resolution = TexelResolution;

        var texel = new Vector3(origin.X, origin.Y, origin.Z)
            + new Vector3(0.5f)
            + (Clamped(local) * BrickResolution);

        return texel / new Vector3(resolution.X, resolution.Y, resolution.Z);
    }

    /// <summary>Where a texel lives in <see cref="Texels" />.</summary>
    /// <param name="x">Its index along X.</param>
    /// <param name="y">Along Y.</param>
    /// <param name="z">Along Z.</param>
    /// <returns>The flat index.</returns>
    internal int TexelIndex(int x, int y, int z) {
        var resolution = TexelResolution;

        return x + (resolution.X * (y + (resolution.Y * z)));
    }

    /// <summary>Checks a slot and a coordinate, and answers where the slot starts.</summary>
    /// <param name="slot">The slot.</param>
    /// <param name="x">The texel's index along X.</param>
    /// <param name="y">Along Y.</param>
    /// <param name="z">Along Z.</param>
    /// <returns>The slot's origin in texels.</returns>
    Int3 Located(int slot, int x, int y, int z) {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, PaddedResolution);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, PaddedResolution);
        ArgumentOutOfRangeException.ThrowIfNegative(z);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(z, PaddedResolution);

        return OriginOf(slot);
    }

    /// <summary>Which two probes a coordinate falls between, and how far.</summary>
    /// <param name="local">Where along the axis, 0 to 1.</param>
    /// <param name="index">The lower probe.</param>
    /// <param name="fraction">How far toward the upper one.</param>
    static void Weights(float local, out int index, out float fraction) {
        var scaled = Math.Clamp(local, 0f, 1f) * BrickResolution;

        index = Math.Clamp((int)MathF.Floor(scaled), 0, BrickResolution - 1);
        fraction = Math.Clamp(scaled - index, 0f, 1f);
    }

    static Vector3 Clamped(Vector3 local) =>
        Vector3.Clamp(local, Vector3.Zero, Vector3.One);
}
