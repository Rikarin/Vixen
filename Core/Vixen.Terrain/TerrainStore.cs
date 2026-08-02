// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;

namespace Vixen.Terrain;

/// <summary>
///     Reading and writing a whole terrain: its description, its edit layers, its paint channels and
///     its holes.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T3]'s owed <c>.vxterrain</c>.</b> <c>TerrainMode.Created</c> hands a
///         <see cref="Terrain" /> to whoever asked for one and deliberately does not write it — a mode
///         has no asset database. This is the format it would be written in, and it is binary for
///         [§ D2]'s reason: a 4 km² terrain is tens of megabytes of samples, and a scene file is the
///         one two people touch every day.
///     </para>
///     <para>
///         ⚠ <b>The layers are stored and the composite is not.</b> A composite is a cache — the
///         layer stack is the definition, and writing both would be writing a number twice and
///         guaranteeing they disagree the first time somebody edits the file by hand. Reading calls
///         <see cref="Terrain.Resolve" />, which is the same code the editor runs.
///     </para>
///     <para>
///         ⚠ <b>Only the samples a layer has touched.</b> An edit layer over a 4 km² terrain that
///         somebody sculpted one hill into is sixteen million zeroes and a hundred thousand numbers;
///         storing its occupied chunks makes the file the size of the edit rather than the size of the
///         world, which is what makes a stack of layers affordable at all.
///     </para>
///     <para>
///         ⚠ <b>A version, first, and it is checked.</b> A heightfield read with the wrong field order
///         is not a parse error — it is a terrain that loads and looks like static, and the person
///         seeing it has no reason to suspect the format.
///     </para>
/// </remarks>
public static class TerrainStore {
    /// <summary>What every file starts with.</summary>
    public static ReadOnlySpan<byte> Magic => "VXTERRA1"u8;

    /// <summary>The format this build writes.</summary>
    public const int Version = 1;

    /// <summary>Writes a terrain.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <returns>The file.</returns>
    /// <exception cref="ArgumentNullException">There is no terrain.</exception>
    public static byte[] Write(Terrain terrain) {
        ArgumentNullException.ThrowIfNull(terrain);

        var file = new MemoryStream();
        var description = terrain.Description;

        file.Write(Magic);

        WriteInt(file, Version);
        WriteInt(file, description.TileSamples);
        WriteInt(file, description.TilesX);
        WriteInt(file, description.TilesZ);
        WriteFloat(file, description.MetresPerQuad);
        WriteFloat(file, description.MinHeight);
        WriteFloat(file, description.MaxHeight);

        // The base heightfield, whole. It is the one thing that is never sparse: every sample of it
        // exists whether or not anybody touched it.
        var samples = (int)description.SampleCount;
        var bytes = new byte[samples * sizeof(ushort)];

        var at = 0;

        for (var z = 0; z < description.SamplesZ; z++) {
            for (var x = 0; x < description.SamplesX; x++) {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), terrain.Base[x, z]);
                at += sizeof(ushort);
            }
        }

        file.Write(bytes);

        WriteLayers(file, terrain);
        WriteWeights(file, terrain);
        WriteHoles(file, terrain, description);

        return file.ToArray();
    }

    /// <summary>Reads a terrain.</summary>
    /// <param name="data">The file.</param>
    /// <returns>The terrain, composited.</returns>
    /// <exception cref="ArgumentException">It is not a terrain this build can read.</exception>
    public static Terrain Read(ReadOnlySpan<byte> data) {
        if (data.Length < Magic.Length || !data[..Magic.Length].SequenceEqual(Magic)) {
            throw new ArgumentException("That is not a terrain: the header is missing.", nameof(data));
        }

        var at = Magic.Length;
        var version = ReadInt(data, ref at);

        if (version != Version) {
            throw new ArgumentException(
                $"That terrain is version {version} and this build writes {Version}. A heightfield read "
                + "with the wrong field order is not a parse error — it is a terrain that loads and "
                + "looks like static.",
                nameof(data)
            );
        }

        var description = new TerrainDescription {
            TileSamples = ReadInt(data, ref at),
            TilesX = ReadInt(data, ref at),
            TilesZ = ReadInt(data, ref at),
            MetresPerQuad = ReadFloat(data, ref at),
            MinHeight = ReadFloat(data, ref at),
            MaxHeight = ReadFloat(data, ref at)
        };

        if (description.Validate() is { } refusal) {
            throw new ArgumentException($"That terrain cannot be built: {refusal}", nameof(data));
        }

        var terrain = new Terrain(description);
        var samples = (int)description.SampleCount;

        for (var z = 0; z < description.SamplesZ; z++) {
            for (var x = 0; x < description.SamplesX; x++) {
                terrain.Base[x, z] = BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
                at += sizeof(ushort);
            }
        }

        ReadLayers(data, ref at, terrain);
        ReadWeights(data, ref at, terrain);
        ReadHoles(data, ref at, terrain, description);

        // ⚠ Composited on the way in, because the composite is a cache and a cache read from a file
        // is a cache nobody can prove matches its layers.
        terrain.Invalidate(new(0, 0, description.SamplesX, description.SamplesZ));
        terrain.Resolve();

        return terrain;
    }

    /// <summary>How many bytes a terrain would be, before its layers and paint.</summary>
    /// <param name="description">The terrain's shape.</param>
    /// <returns>The count.</returns>
    /// <remarks>
    ///     The derived number a create form shows, and the reason it is here rather than in the panel:
    ///     the format decides it.
    /// </remarks>
    public static long BaseByteCount(in TerrainDescription description) =>
        Magic.Length + (7 * sizeof(int)) + ((long)description.SampleCount * sizeof(ushort));

    static void WriteLayers(Stream file, Terrain terrain) {
        WriteInt(file, terrain.Layers.Count);

        foreach (var layer in terrain.Layers) {
            WriteString(file, layer.Name);
            WriteInt(file, (int)layer.Kind);
            WriteFloat(file, layer.HeightAlpha);
            WriteInt(file, layer.IsVisible ? 1 : 0);
            WriteInt(file, layer.IsLocked ? 1 : 0);

            // ⚠ Only the chunks the layer has touched. An edit layer over a 4 km² terrain that
            // somebody sculpted one hill into is sixteen million zeroes and a hundred thousand
            // numbers, and storing the zeroes is what makes a stack of layers unaffordable.
            var occupied = layer.OccupiedChunks().ToArray();

            WriteInt(file, occupied.Length);

            foreach (var chunk in occupied) {
                WriteInt(file, chunk.X);
                WriteInt(file, chunk.Z);

                for (var z = 0; z < TerrainEditLayer.ChunkSize; z++) {
                    for (var x = 0; x < TerrainEditLayer.ChunkSize; x++) {
                        WriteInt(
                            file,
                            layer.DeltaAt((chunk.X * TerrainEditLayer.ChunkSize) + x, (chunk.Z * TerrainEditLayer.ChunkSize) + z)
                        );
                    }
                }
            }
        }
    }

    static void ReadLayers(ReadOnlySpan<byte> data, ref int at, Terrain terrain) {
        var count = ReadInt(data, ref at);

        for (var index = 0; index < count; index++) {
            var name = ReadString(data, ref at);
            var kind = (TerrainLayerKind)ReadInt(data, ref at);
            var alpha = ReadFloat(data, ref at);
            var visible = ReadInt(data, ref at) != 0;
            var locked = ReadInt(data, ref at) != 0;

            var layer = terrain.AddLayer(name, kind);

            layer.HeightAlpha = alpha;
            layer.IsVisible = visible;
            layer.IsLocked = locked;

            var chunks = ReadInt(data, ref at);

            for (var chunk = 0; chunk < chunks; chunk++) {
                var chunkX = ReadInt(data, ref at);
                var chunkZ = ReadInt(data, ref at);

                for (var z = 0; z < TerrainEditLayer.ChunkSize; z++) {
                    for (var x = 0; x < TerrainEditLayer.ChunkSize; x++) {
                        layer.SetDelta(
                            (chunkX * TerrainEditLayer.ChunkSize) + x,
                            (chunkZ * TerrainEditLayer.ChunkSize) + z,
                            (short)ReadInt(data, ref at)
                        );
                    }
                }
            }
        }
    }

    static void WriteWeights(Stream file, Terrain terrain) {
        var weights = terrain.Weights;

        WriteInt(file, weights.LayerCount);

        for (var layer = 0; layer < weights.LayerCount; layer++) {
            var description = weights.LayerOf(layer);

            WriteString(file, description.Name);
            WriteString(file, description.Albedo);
            WriteString(file, description.Normal);
            WriteString(file, description.Surface);
            WriteFloat(file, description.TilingMetres);
            WriteInt(file, (int)description.Blend);
            WriteFloat(file, description.HeightContrast);
            WriteString(file, description.PhysicsMaterial);

            file.Write(weights.ChannelOf(layer));
        }
    }

    static void ReadWeights(ReadOnlySpan<byte> data, ref int at, Terrain terrain) {
        var count = ReadInt(data, ref at);

        for (var index = 0; index < count; index++) {
            var description = new TerrainLayerDescription(
                ReadString(data, ref at),
                ReadString(data, ref at),
                ReadString(data, ref at),
                ReadString(data, ref at),
                ReadFloat(data, ref at),
                default,
                (TerrainLayerBlend)ReadInt(data, ref at),
                ReadFloat(data, ref at),
                ReadString(data, ref at)
            );

            terrain.Weights.AddLayer(description);

            // ⚠ Through `SetWeight` rather than into the channel's span, because the channel is
            // exposed read-only — and it is read-only because writing one directly is what breaks
            // [§ D5]'s sum-to-one invariant without anything noticing.
            for (var z = 0; z < terrain.Description.SamplesZ; z++) {
                for (var x = 0; x < terrain.Description.SamplesX; x++) {
                    terrain.Weights.SetWeight(index, x, z, data[at]);
                    at++;
                }
            }
        }
    }

    static void WriteHoles(Stream file, Terrain terrain, in TerrainDescription description) {
        WriteInt(file, terrain.Holes.HoleCount);

        if (terrain.Holes.IsEmpty) {
            return;
        }

        // ⚠ Coordinates rather than a bitmask. A terrain with three holes in it is the normal case,
        // and a bitmask over sixteen million samples is two megabytes to say "three".
        for (var z = 0; z < description.SamplesZ; z++) {
            for (var x = 0; x < description.SamplesX; x++) {
                if (terrain.Holes.IsHole(x, z)) {
                    WriteInt(file, x);
                    WriteInt(file, z);
                }
            }
        }
    }

    static void ReadHoles(ReadOnlySpan<byte> data, ref int at, Terrain terrain, in TerrainDescription description) {
        var count = ReadInt(data, ref at);

        for (var index = 0; index < count; index++) {
            var x = ReadInt(data, ref at);
            var z = ReadInt(data, ref at);

            if (x >= 0 && z >= 0 && x < description.SamplesX && z < description.SamplesZ) {
                terrain.Holes.SetHole(x, z, true);
            }
        }
    }

    static void WriteInt(Stream file, int value) {
        Span<byte> bytes = stackalloc byte[4];

        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        file.Write(bytes);
    }

    static void WriteFloat(Stream file, float value) {
        Span<byte> bytes = stackalloc byte[4];

        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        file.Write(bytes);
    }

    static void WriteString(Stream file, string value) {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty);

        WriteInt(file, bytes.Length);
        file.Write(bytes);
    }

    static int ReadInt(ReadOnlySpan<byte> data, ref int at) {
        var value = BinaryPrimitives.ReadInt32LittleEndian(data[at..]);

        at += 4;

        return value;
    }

    static float ReadFloat(ReadOnlySpan<byte> data, ref int at) {
        var value = BinaryPrimitives.ReadSingleLittleEndian(data[at..]);

        at += 4;

        return value;
    }

    static string ReadString(ReadOnlySpan<byte> data, ref int at) {
        var length = ReadInt(data, ref at);
        var value = System.Text.Encoding.UTF8.GetString(data.Slice(at, length));

        at += length;

        return value;
    }
}
