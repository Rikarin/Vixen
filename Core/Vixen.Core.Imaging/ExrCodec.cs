// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Vixen.Core.Imaging;

/// <summary>How wide a channel of an EXR file is.</summary>
public enum ExrPixels {
    /// <summary>16-bit float: what a normal or an occlusion map needs, at half the size.</summary>
    Half = 1,

    /// <summary>32-bit float: what a position or a displacement needs.</summary>
    Float = 2
}

/// <summary>A floating-point picture, row by row from the top, channels interleaved.</summary>
/// <param name="Width">Its width in pixels.</param>
/// <param name="Height">Its height in pixels.</param>
/// <param name="Channels">One, three or four; see <see cref="ExrCodec" />.</param>
/// <param name="Samples">Width × height × channels values.</param>
public sealed record FloatImage(int Width, int Height, int Channels, float[] Samples);

/// <summary>OpenEXR scanline files with ZIP compression, in managed code.</summary>
/// <remarks>
///     <para>
///         <b>A subset on purpose, and small enough to read.</b> A bake's output is a flat image with a
///         few channels, and that needs the scanline layout and one compression method out of the
///         format's many. ZIP is deflate over a reordered, delta-coded block, and
///         <see cref="System.IO.Compression" /> already ships deflate, so this is about two hundred
///         lines, against four native libraries to build and sign on three platforms for the reference
///         implementation. A tiled, deep or multi-part file, or another compression, is refused on read
///         and not guessed at.
///     </para>
///     <para>
///         ⚠ <b>One channel is <c>Y</c>, three are <c>R G B</c>, four add <c>A</c></b>, and the file
///         stores them in name order whatever order they were interleaved in, because the format says
///         so: a reader sorted the other way sees the wrong channel in every position.
///     </para>
///     <para>
///         ⚠ <b>The compressor is deterministic and its blocks do not depend on the thread count.</b>
///         Each block is compressed on its own, and a block that does not shrink is stored raw, as the
///         format requires, which is why a chunk's size says which it is.
///     </para>
/// </remarks>
public static class ExrCodec {
    /// <summary>How many scanlines a ZIP block covers, which the format fixes.</summary>
    const int LinesPerBlock = 16;

    const int Magic = 20000630;
    const byte CompressionNone = 0;
    const byte CompressionZips = 2;
    const byte CompressionZip = 3;

    /// <summary>Writes a picture.</summary>
    /// <param name="image">The picture.</param>
    /// <param name="pixels">How wide to store each channel.</param>
    /// <returns>The file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="image" /> is null.</exception>
    /// <exception cref="ArgumentException">Its size or channel count is one a file cannot name.</exception>
    public static byte[] Encode(FloatImage image, ExrPixels pixels = ExrPixels.Float) {
        ArgumentNullException.ThrowIfNull(image);

        var names = NamesOf(image.Channels);

        if (image.Width <= 0 || image.Height <= 0 || image.Samples.Length != image.Width * image.Height * image.Channels) {
            throw new ArgumentException("The samples do not fill the image.", nameof(image));
        }

        using var file = new MemoryStream();

        WriteHeader(file, image, names, pixels);

        var blocks = (image.Height + LinesPerBlock - 1) / LinesPerBlock;
        var chunks = new byte[blocks][];

        Parallel.For(0, blocks, block => chunks[block] = Chunk(image, names, pixels, block));

        var offset = file.Length + (blocks * sizeof(long));
        Span<byte> entry = stackalloc byte[sizeof(long)];

        foreach (var chunk in chunks) {
            BinaryPrimitives.WriteInt64LittleEndian(entry, offset);
            file.Write(entry);
            offset += chunk.Length + (2 * sizeof(int));
        }

        for (var block = 0; block < blocks; block++) {
            WriteInt(file, block * LinesPerBlock);
            WriteInt(file, chunks[block].Length);
            file.Write(chunks[block]);
        }

        return file.ToArray();
    }

    /// <summary>Reads a picture.</summary>
    /// <param name="data">The file.</param>
    /// <returns>The picture.</returns>
    /// <exception cref="InvalidDataException">It is not a file this reads.</exception>
    public static FloatImage Decode(ReadOnlySpan<byte> data) {
        if (data.Length < 8 || BinaryPrimitives.ReadInt32LittleEndian(data) != Magic) {
            throw new InvalidDataException("Not an EXR: the magic number does not match.");
        }

        var flags = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);

        if ((flags & ~0xFF) != 0) {
            throw new InvalidDataException("Only single-part scanline EXRs are read.");
        }

        var at = 8;
        var channels = new List<(string Name, int Type)>();
        var compression = -1;
        var (left, top, right, bottom) = (0, 0, -1, -1);

        while (data[at] != 0) {
            var name = Text(data, ref at);
            var type = Text(data, ref at);
            var size = BinaryPrimitives.ReadInt32LittleEndian(data[at..]);
            var value = data.Slice(at + 4, size);

            at += 4 + size;

            switch (name) {
                case "channels" when type == "chlist":
                    ReadChannels(value, channels);
                    break;
                case "compression":
                    compression = value[0];
                    break;
                case "dataWindow":
                    left = BinaryPrimitives.ReadInt32LittleEndian(value);
                    top = BinaryPrimitives.ReadInt32LittleEndian(value[4..]);
                    right = BinaryPrimitives.ReadInt32LittleEndian(value[8..]);
                    bottom = BinaryPrimitives.ReadInt32LittleEndian(value[12..]);
                    break;
            }
        }

        at++;

        var (width, height) = (right - left + 1, bottom - top + 1);
        var lines = compression switch {
            CompressionNone or CompressionZips => 1,
            CompressionZip => LinesPerBlock,
            _ => throw new InvalidDataException($"EXR compression {compression} is not read.")
        };

        if (width <= 0 || height <= 0 || channels.Count == 0) {
            throw new InvalidDataException("The EXR names an empty image.");
        }

        var order = Order(channels);
        var output = new FloatImage(width, height, order.Length, new float[width * height * order.Length]);
        var blocks = (height + lines - 1) / lines;

        for (var block = 0; block < blocks; block++) {
            var offset = checked((int) BinaryPrimitives.ReadInt64LittleEndian(data[(at + (block * sizeof(long)))..]));
            var first = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]) - top;
            var size = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 4)..]);
            var count = Math.Min(lines, height - first);
            var expected = count * width * channels.Sum(static channel => channel.Type == (int) ExrPixels.Half ? 2 : 4);
            var raw = size < expected ? Unzip(data.Slice(offset + 8, size), expected) : data.Slice(offset + 8, size).ToArray();

            Unpack(raw, channels, order, output, first, count);
        }

        return output;
    }

    static string[] NamesOf(int channels) => channels switch {
        1 => ["Y"],
        3 => ["B", "G", "R"],
        4 => ["A", "B", "G", "R"],
        _ => throw new ArgumentException($"{channels} channels: one, three or four are written.", nameof(channels))
    };

    /// <summary>Where each channel of the file goes in an interleaved picture.</summary>
    static int[] Order(List<(string Name, int Type)> channels) {
        var names = channels.Select(static channel => channel.Name).ToList();
        string[] wanted = channels.Count switch {
            1 => [names[0]],
            3 => ["R", "G", "B"],
            4 => ["R", "G", "B", "A"],
            _ => throw new InvalidDataException($"An EXR with {channels.Count} channels is not read.")
        };

        var order = new int[wanted.Length];

        for (var slot = 0; slot < wanted.Length; slot++) {
            var found = names.IndexOf(wanted[slot]);

            if (found < 0) {
                throw new InvalidDataException($"The EXR has no {wanted[slot]} channel.");
            }

            order[found] = slot;
        }

        return order;
    }

    static void WriteHeader(Stream file, FloatImage image, string[] names, ExrPixels pixels) {
        WriteInt(file, Magic);
        WriteInt(file, 2);

        using var list = new MemoryStream();

        foreach (var name in names) {
            WriteText(list, name);
            WriteInt(list, (int) pixels);
            WriteInt(list, 0); // pLinear and three reserved bytes
            WriteInt(list, 1);
            WriteInt(list, 1);
        }

        list.WriteByte(0);

        var window = Window(image.Width, image.Height);

        Attribute(file, "channels", "chlist", list.ToArray());
        Attribute(file, "compression", "compression", [CompressionZip]);
        Attribute(file, "dataWindow", "box2i", window);
        Attribute(file, "displayWindow", "box2i", window);
        Attribute(file, "lineOrder", "lineOrder", [0]);
        Attribute(file, "pixelAspectRatio", "float", BitConverter.GetBytes(1f));
        Attribute(file, "screenWindowCenter", "v2f", new byte[8]);
        Attribute(file, "screenWindowWidth", "float", BitConverter.GetBytes(1f));
        file.WriteByte(0);
    }

    static byte[] Window(int width, int height) {
        var window = new byte[16];

        BinaryPrimitives.WriteInt32LittleEndian(window.AsSpan(8), width - 1);
        BinaryPrimitives.WriteInt32LittleEndian(window.AsSpan(12), height - 1);

        return window;
    }

    static void Attribute(Stream file, string name, string type, byte[] value) {
        WriteText(file, name);
        WriteText(file, type);
        WriteInt(file, value.Length);
        file.Write(value);
    }

    static byte[] Chunk(FloatImage image, string[] names, ExrPixels pixels, int block) {
        var first = block * LinesPerBlock;
        var count = Math.Min(LinesPerBlock, image.Height - first);
        var width = pixels == ExrPixels.Half ? 2 : 4;
        var raw = new byte[count * image.Width * names.Length * width];
        var span = raw.AsSpan();
        var at = 0;

        for (var line = first; line < first + count; line++) {
            foreach (var name in names) {
                var lane = Lane(name);

                for (var x = 0; x < image.Width; x++) {
                    var sample = image.Samples[(((line * image.Width) + x) * image.Channels) + (image.Channels == 1 ? 0 : lane)];

                    if (pixels == ExrPixels.Half) {
                        BinaryPrimitives.WriteHalfLittleEndian(span[at..], (Half) sample);
                    } else {
                        BinaryPrimitives.WriteSingleLittleEndian(span[at..], sample);
                    }

                    at += width;
                }
            }
        }

        return Zip(raw);
    }

    static int Lane(string name) => name switch {
        "G" => 1,
        "B" => 2,
        "A" => 3,
        _ => 0
    };

    /// <summary>The format's compression: bytes split in two, delta-coded, then zlib.</summary>
    static byte[] Zip(byte[] raw) {
        var moved = new byte[raw.Length];
        var (one, two) = (0, (raw.Length + 1) / 2);

        for (var at = 0; at < raw.Length; at++) {
            if ((at & 1) == 0) {
                moved[one++] = raw[at];
            } else {
                moved[two++] = raw[at];
            }
        }

        for (var at = moved.Length - 1; at > 0; at--) {
            moved[at] = (byte) (moved[at] - moved[at - 1] + 128);
        }

        using var output = new MemoryStream();

        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, true)) {
            zlib.Write(moved);
        }

        // ⚠ Stored raw when it did not shrink, which is not an option but the format: a reader tells
        // the two apart by the chunk's size alone.
        return output.Length < raw.Length ? output.ToArray() : raw;
    }

    static byte[] Unzip(ReadOnlySpan<byte> compressed, int expected) {
        using var input = new MemoryStream(compressed.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);

        var moved = new byte[expected];

        zlib.ReadExactly(moved);

        for (var at = 1; at < moved.Length; at++) {
            moved[at] = (byte) (moved[at] + moved[at - 1] - 128);
        }

        var raw = new byte[expected];
        var (one, two) = (0, (expected + 1) / 2);

        for (var at = 0; at < expected; at++) {
            raw[at] = (at & 1) == 0 ? moved[one++] : moved[two++];
        }

        return raw;
    }

    static void Unpack(
        byte[] raw,
        List<(string Name, int Type)> channels,
        int[] order,
        FloatImage output,
        int first,
        int count
    ) {
        var span = raw.AsSpan();
        var at = 0;

        for (var line = first; line < first + count; line++) {
            for (var channel = 0; channel < channels.Count; channel++) {
                var type = channels[channel].Type;

                for (var x = 0; x < output.Width; x++) {
                    var index = (((line * output.Width) + x) * output.Channels) + order[channel];

                    switch (type) {
                        case (int) ExrPixels.Half:
                            output.Samples[index] = (float) BinaryPrimitives.ReadHalfLittleEndian(span[at..]);
                            at += 2;
                            break;
                        case (int) ExrPixels.Float:
                            output.Samples[index] = BinaryPrimitives.ReadSingleLittleEndian(span[at..]);
                            at += 4;
                            break;
                        default:
                            throw new InvalidDataException("EXR channels of 32-bit integers are not read.");
                    }
                }
            }
        }
    }

    static void ReadChannels(ReadOnlySpan<byte> list, List<(string Name, int Type)> channels) {
        var at = 0;

        while (list[at] != 0) {
            var name = Text(list, ref at);
            var type = BinaryPrimitives.ReadInt32LittleEndian(list[at..]);

            if (BinaryPrimitives.ReadInt32LittleEndian(list[(at + 8)..]) != 1
                || BinaryPrimitives.ReadInt32LittleEndian(list[(at + 12)..]) != 1) {
                throw new InvalidDataException("Subsampled EXR channels are not read.");
            }

            channels.Add((name, type));
            at += 16;
        }
    }

    static string Text(ReadOnlySpan<byte> data, ref int at) {
        var end = data[at..].IndexOf((byte) 0);
        var text = Encoding.ASCII.GetString(data.Slice(at, end));

        at += end + 1;

        return text;
    }

    static void WriteText(Stream file, string text) {
        file.Write(Encoding.ASCII.GetBytes(text));
        file.WriteByte(0);
    }

    static void WriteInt(Stream file, int value) {
        Span<byte> bytes = stackalloc byte[sizeof(int)];

        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        file.Write(bytes);
    }
}
