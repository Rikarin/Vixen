// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Core.Mathematics;

namespace Vixen.Foliage;

/// <summary>
///     Instances on disk, beside the scene rather than inside it.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § What the scene sees]: stored beside the scene for the merge-conflict
///         reason a heightfield is.</b> A <c>.vxscene</c> is the file two people touch every day, and
///         fifty thousand transforms in it is a file nobody can merge — one artist thinning a wood
///         and another moving a rock produce a conflict across every line.
///     </para>
///     <para>
///         <b>Binary, and it is the one place in this subsystem where that is not a shortcut.</b>
///         Fifty thousand instances is 1.4 MB of packed floats and about 12 MB of YAML that takes a
///         second and a half to parse. What is <em>not</em> binary is the palette, which is names and
///         numbers an author edits and a diff should show.
///     </para>
///     <para>
///         ⚠ <b>Grouped by cell, and the cell key is written rather than derived.</b> Deriving it on
///         load from the position would be one fewer field and would silently re-cell every instance
///         if the grid size ever changed — a saved forest that moves when somebody edits a setting.
///         Written, a mismatch is detectable; <see cref="Read" /> re-cells deliberately and says so.
///     </para>
/// </remarks>
public static class FoliageStore {
    /// <summary>What the first four bytes of a foliage file are.</summary>
    /// <remarks>
    ///     A magic number, because the alternative to refusing an unrecognised file is interpreting
    ///     one — and a wrong file read as instances is a million trees at random coordinates rather
    ///     than an error.
    /// </remarks>
    public static ReadOnlySpan<byte> Magic => "VXFL"u8;

    /// <summary>Which layout the bytes are in.</summary>
    public const int Version = 1;

    /// <summary>How many bytes one instance occupies.</summary>
    /// <remarks>Three floats of position, four of rotation, one of scale — thirty-two.</remarks>
    public const int InstanceBytes = 32;

    /// <summary>How many bytes a volume's instances would occupy.</summary>
    /// <param name="volume">The volume.</param>
    /// <returns>The count, header and chunk headers included.</returns>
    public static long ByteCount(FoliageVolume volume) {
        ArgumentNullException.ThrowIfNull(volume);

        var total = (long)HeaderBytes;

        foreach (var chunk in volume.Chunks) {
            if (!chunk.IsEmpty) {
                total += ChunkHeaderBytes + ((long)chunk.Count * InstanceBytes);
            }
        }

        return total;
    }

    /// <summary>Writes a volume's instances.</summary>
    /// <param name="volume">The volume.</param>
    /// <param name="destination">Where to put the bytes.</param>
    /// <returns>How many were written.</returns>
    /// <exception cref="ArgumentException">There is not enough room.</exception>
    /// <remarks>
    ///     ⚠ <b>The palette is not written here.</b> It is names, numbers and asset references that an
    ///     author edits and a review should be able to read, so it belongs in whatever text file
    ///     declares the volume. What this holds is the part that is too big to be text.
    /// </remarks>
    public static int Write(FoliageVolume volume, Span<byte> destination) {
        ArgumentNullException.ThrowIfNull(volume);

        var required = ByteCount(volume);

        if (destination.Length < required) {
            throw new ArgumentException(
                $"This volume is {required} bytes and {destination.Length} were given.",
                nameof(destination)
            );
        }

        var chunks = volume.Chunks.Where(chunk => !chunk.IsEmpty).ToArray();
        var at = 0;

        Magic.CopyTo(destination);
        at += Magic.Length;

        BinaryPrimitives.WriteInt32LittleEndian(destination[at..], Version);
        at += sizeof(int);

        BinaryPrimitives.WriteSingleLittleEndian(destination[at..], volume.Grid.CellSize);
        at += sizeof(float);

        BinaryPrimitives.WriteInt32LittleEndian(destination[at..], chunks.Length);
        at += sizeof(int);

        foreach (var chunk in chunks) {
            BinaryPrimitives.WriteInt32LittleEndian(destination[at..], chunk.Type);
            at += sizeof(int);

            BinaryPrimitives.WriteInt32LittleEndian(destination[at..], chunk.Cell.X);
            at += sizeof(int);

            BinaryPrimitives.WriteInt32LittleEndian(destination[at..], chunk.Cell.Z);
            at += sizeof(int);

            BinaryPrimitives.WriteInt32LittleEndian(destination[at..], chunk.Count);
            at += sizeof(int);

            foreach (var instance in chunk.Instances) {
                at += WriteInstance(instance, destination[at..]);
            }
        }

        return at;
    }

    /// <summary>Reads instances into a volume.</summary>
    /// <param name="volume">Where they go. Its palette is used and its cells are replaced.</param>
    /// <param name="bytes">The file.</param>
    /// <returns>How many instances were read.</returns>
    /// <exception cref="ArgumentException">The bytes are not a foliage file this can read.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The volume's own grid wins over the file's, and instances are re-celled.</b> A
    ///         file written with 32 m cells and read into a volume using 64 m ones is a reasonable
    ///         thing to happen — somebody changed a setting — and the alternative to re-celling is a
    ///         forest whose cells no longer match its grid, which culls wrongly and cannot be
    ///         repaired without a rewrite. Positions are the truth; cells are an index over them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A type index past the palette is dropped rather than clamped.</b> Clamping puts
    ///         somebody's oaks into whatever the last palette entry happens to be, silently. Dropping
    ///         loses them visibly, which is what a person can act on.
    ///     </para>
    /// </remarks>
    public static int Read(FoliageVolume volume, ReadOnlySpan<byte> bytes) {
        ArgumentNullException.ThrowIfNull(volume);

        if (bytes.Length < HeaderBytes || !bytes[..Magic.Length].SequenceEqual(Magic)) {
            throw new ArgumentException(
                "These bytes do not begin with a foliage file's magic number. Reading them as "
                + "instances would produce a million trees at random coordinates rather than an error.",
                nameof(bytes)
            );
        }

        var at = Magic.Length;
        var version = BinaryPrimitives.ReadInt32LittleEndian(bytes[at..]);
        at += sizeof(int);

        if (version != Version) {
            throw new ArgumentException(
                $"This is a version {version} foliage file and this build reads version {Version}.",
                nameof(bytes)
            );
        }

        // Read and deliberately not used: the file says what it was celled at, and the volume says
        // what it is celled at now. See the remarks.
        at += sizeof(float);

        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes[at..]);
        at += sizeof(int);

        volume.Clear();

        var read = 0;

        for (var chunk = 0; chunk < count; chunk++) {
            if (bytes.Length - at < ChunkHeaderBytes) {
                throw new ArgumentException($"The file ends inside chunk {chunk}'s header.", nameof(bytes));
            }

            var type = BinaryPrimitives.ReadInt32LittleEndian(bytes[at..]);
            at += sizeof(int);

            // The cell as written, kept for a diagnostic and not for placement.
            at += sizeof(int) * 2;

            var instances = BinaryPrimitives.ReadInt32LittleEndian(bytes[at..]);
            at += sizeof(int);

            if (instances < 0 || bytes.Length - at < (long)instances * InstanceBytes) {
                throw new ArgumentException(
                    $"Chunk {chunk} claims {instances} instances and the file does not hold them.",
                    nameof(bytes)
                );
            }

            for (var index = 0; index < instances; index++) {
                var instance = ReadInstance(bytes[at..]);
                at += InstanceBytes;

                if ((uint)type >= (uint)volume.Palette.Count) {
                    continue;
                }

                volume.Add(type, instance);
                read++;
            }
        }

        return read;
    }

    static int WriteInstance(in FoliageInstance instance, Span<byte> destination) {
        BinaryPrimitives.WriteSingleLittleEndian(destination, instance.Position.X);
        BinaryPrimitives.WriteSingleLittleEndian(destination[4..], instance.Position.Y);
        BinaryPrimitives.WriteSingleLittleEndian(destination[8..], instance.Position.Z);

        BinaryPrimitives.WriteSingleLittleEndian(destination[12..], instance.Rotation.X);
        BinaryPrimitives.WriteSingleLittleEndian(destination[16..], instance.Rotation.Y);
        BinaryPrimitives.WriteSingleLittleEndian(destination[20..], instance.Rotation.Z);
        BinaryPrimitives.WriteSingleLittleEndian(destination[24..], instance.Rotation.W);

        BinaryPrimitives.WriteSingleLittleEndian(destination[28..], instance.Scale);

        return InstanceBytes;
    }

    static FoliageInstance ReadInstance(ReadOnlySpan<byte> bytes) =>
        new(
            new(
                BinaryPrimitives.ReadSingleLittleEndian(bytes),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[8..])
            ),
            new(
                BinaryPrimitives.ReadSingleLittleEndian(bytes[12..]),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[16..]),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[20..]),
                BinaryPrimitives.ReadSingleLittleEndian(bytes[24..])
            ),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[28..])
        );

    /// <summary>Magic, version, cell size, chunk count.</summary>
    const int HeaderBytes = 4 + sizeof(int) + sizeof(float) + sizeof(int);

    /// <summary>Type, cell X, cell Z, instance count.</summary>
    const int ChunkHeaderBytes = sizeof(int) * 4;
}
