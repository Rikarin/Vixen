// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Core;

namespace Vixen.Terrain;

/// <summary>How a raw heightmap's bytes are laid out.</summary>
/// <param name="Width">How many samples across.</param>
/// <param name="Height">How many samples down.</param>
/// <param name="BigEndian">Whether each sample's two bytes are most-significant first.</param>
/// <remarks>
///     <b>A raw file says nothing about itself</b>, which is why every tool that reads one asks for
///     these three. Unreal's raw import has the same form and the same reason. Getting the endianness
///     wrong produces a terrain that looks like static rather than like terrain, which at least fails
///     loudly; getting the width wrong shears it diagonally, which does not.
/// </remarks>
[DataContract]
public readonly record struct TerrainHeightmapFormat(int Width, int Height, bool BigEndian = false) {
    /// <summary>How many bytes a file of this shape is.</summary>
    public long ByteCount => (long)Width * Height * sizeof(ushort);

    /// <summary>The format a terrain of this shape exports as.</summary>
    /// <param name="description">The terrain's shape.</param>
    /// <returns>The format, little-endian.</returns>
    public static TerrainHeightmapFormat Of(TerrainDescription description) =>
        new(description.SamplesX, description.SamplesZ);
}

/// <summary>
///     Reading and writing 16-bit raw heightmaps.
/// </summary>
/// <remarks>
///     <para>
///         <b>Raw only, and PNG deliberately elsewhere.</b> [docs/plan/31 § D1] gives this assembly
///         one project reference and it is to the mathematics. Raw 16-bit is bytes and needs nothing;
///         16-bit PNG needs <c>Vixen.Core.Imaging</c>, so it belongs with the importer, which already
///         depends on it. Splitting them costs a sentence of documentation and keeps the kernel
///         something a game can carry.
///     </para>
///     <para>
///         <b>Resampling is not optional.</b> A terrain of four 128-sample tiles is 509 samples
///         across; heightmaps come out of World Machine and Gaea at 512, 1024, 2049. They will
///         essentially never match, so an importer that demanded they match would be an importer
///         nobody could use. Bilinear, and the corners are pinned — see <see cref="Import" />.
///     </para>
///     <para>
///         ⚠ <b>Import writes an edit layer by default, not the base.</b> That is what makes a
///         terrain imported from an external tool sculptable on top of without being destroyed, and
///         re-importable without losing the sculpt — the whole return on
///         [§ D4](../../docs/plan/31-terrain-grass-and-trees.md). Writing the base is available and
///         is what the create dialog does.
///     </para>
/// </remarks>
public static class TerrainHeightmap {
    /// <summary>Reads a raw heightmap into a terrain.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="bytes">The file's bytes.</param>
    /// <param name="format">How they are laid out.</param>
    /// <param name="layer">
    ///     Which layer receives it, or null to replace the base. A layer is given the deltas that
    ///     make the composite equal the imported heights, so whatever is underneath survives being
    ///     hidden.
    /// </param>
    /// <exception cref="ArgumentException">The bytes do not match the format.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The corners are pinned and the edges are exact.</b> Resampling maps sample 0 to
    ///         source 0 and the last sample to the last source sample, so a heightmap's edges land on
    ///         the terrain's edges. Mapping by scale factor instead leaves a fractional strip at the
    ///         far edge that reads whatever the clamp gives it — a flat lip along two sides of every
    ///         imported terrain, which is subtle enough to ship.
    ///     </para>
    ///     <para>
    ///         A layer import <em>replaces</em> that layer's contents rather than adding to it, so
    ///         re-importing a revised heightmap does not stack two copies of the mountain.
    ///     </para>
    /// </remarks>
    public static void Import(
        Terrain terrain,
        ReadOnlySpan<byte> bytes,
        in TerrainHeightmapFormat format,
        TerrainEditLayer? layer = null
    ) {
        ArgumentNullException.ThrowIfNull(terrain);
        Validate(bytes, in format);

        if (layer is { AcceptsBrush: false }) {
            throw new ArgumentException(
                $"The layer '{layer.Name}' does not accept edits.",
                nameof(layer)
            );
        }

        var description = terrain.Description;
        layer?.Clear();

        for (var z = 0; z < description.SamplesZ; z++) {
            for (var x = 0; x < description.SamplesX; x++) {
                var value = SampleBilinear(bytes, in format, x, z, description.SamplesX, description.SamplesZ);

                if (layer is null) {
                    terrain.Base[x, z] = value;
                } else {
                    // The delta that makes the composite equal the import, measured against
                    // everything below this layer — which is the composite with this layer empty,
                    // and it is empty because Clear ran above.
                    var beneath = terrain.CompositeAt(x, z);
                    layer.SetDelta(x, z, (short)Math.Clamp(value - beneath, short.MinValue, short.MaxValue));
                }
            }
        }

        terrain.InvalidateAll();
    }

    /// <summary>Writes a terrain's composited heights as a raw heightmap.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="destination">Where to put the bytes.</param>
    /// <param name="bigEndian">Whether each sample's two bytes go most-significant first.</param>
    /// <returns>How many bytes were written.</returns>
    /// <exception cref="ArgumentException">There is not enough room.</exception>
    /// <remarks>
    ///     The composite, not the base — what is exported is what the world looks like, which is what
    ///     somebody round-tripping through an external tool means. Exporting the base would hand them
    ///     a terrain missing every edit layer, silently.
    /// </remarks>
    public static int Export(Terrain terrain, Span<byte> destination, bool bigEndian = false) {
        ArgumentNullException.ThrowIfNull(terrain);
        terrain.Resolve();

        var description = terrain.Description;
        var required = (int)(description.SampleCount * sizeof(ushort));

        if (destination.Length < required) {
            throw new ArgumentException(
                $"A {description.SamplesX}×{description.SamplesZ} heightmap is {required} bytes, "
                + $"and there is room for {destination.Length}.",
                nameof(destination)
            );
        }

        var offset = 0;

        for (var z = 0; z < description.SamplesZ; z++) {
            for (var x = 0; x < description.SamplesX; x++) {
                var value = terrain.Composite[x, z];
                var slice = destination.Slice(offset, sizeof(ushort));

                if (bigEndian) {
                    BinaryPrimitives.WriteUInt16BigEndian(slice, value);
                } else {
                    BinaryPrimitives.WriteUInt16LittleEndian(slice, value);
                }

                offset += sizeof(ushort);
            }
        }

        return required;
    }

    /// <summary>How many bytes a terrain exports as.</summary>
    /// <param name="description">The terrain's shape.</param>
    /// <returns>The byte count.</returns>
    public static long ExportByteCount(TerrainDescription description) =>
        description.SampleCount * sizeof(ushort);

    static void Validate(ReadOnlySpan<byte> bytes, in TerrainHeightmapFormat format) {
        if (format.Width < 2 || format.Height < 2) {
            throw new ArgumentException(
                $"A heightmap must be at least two samples along each axis; it was "
                + $"{format.Width}×{format.Height}.",
                nameof(format)
            );
        }

        if (bytes.Length < format.ByteCount) {
            throw new ArgumentException(
                $"A {format.Width}×{format.Height} 16-bit heightmap is {format.ByteCount} bytes; "
                + $"{bytes.Length} were given. Check the width — a raw file says nothing about "
                + "itself, and the wrong width shears the terrain diagonally rather than failing.",
                nameof(bytes)
            );
        }
    }

    /// <summary>Reads the source at a destination sample, resampling if the sizes differ.</summary>
    static ushort SampleBilinear(
        ReadOnlySpan<byte> bytes,
        in TerrainHeightmapFormat format,
        int x,
        int z,
        int width,
        int height
    ) {
        // Edge-to-edge rather than by scale factor: sample 0 maps to source 0 and the last maps to
        // the last, so the heightmap's edges land on the terrain's. A one-sample destination axis
        // would divide by zero, which Validate has already made impossible on the source side and
        // TerrainDescription on this one.
        var sx = width > 1 ? x / (float)(width - 1) * (format.Width - 1) : 0f;
        var sz = height > 1 ? z / (float)(height - 1) * (format.Height - 1) : 0f;

        var x0 = (int)MathF.Floor(sx);
        var z0 = (int)MathF.Floor(sz);
        var fx = sx - x0;
        var fz = sz - z0;

        var a = Read(bytes, in format, x0, z0);
        var b = Read(bytes, in format, x0 + 1, z0);
        var c = Read(bytes, in format, x0, z0 + 1);
        var d = Read(bytes, in format, x0 + 1, z0 + 1);

        var top = (a * (1f - fx)) + (b * fx);
        var bottom = (c * (1f - fx)) + (d * fx);

        return (ushort)Math.Clamp(MathF.Round((top * (1f - fz)) + (bottom * fz)), 0f, ushort.MaxValue);
    }

    static ushort Read(ReadOnlySpan<byte> bytes, in TerrainHeightmapFormat format, int x, int z) {
        var clampedX = Math.Clamp(x, 0, format.Width - 1);
        var clampedZ = Math.Clamp(z, 0, format.Height - 1);
        var offset = (((clampedZ * format.Width) + clampedX) * sizeof(ushort));
        var slice = bytes.Slice(offset, sizeof(ushort));

        return format.BigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(slice)
            : BinaryPrimitives.ReadUInt16LittleEndian(slice);
    }
}
