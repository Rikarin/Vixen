// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Graphics;

namespace Vixen.Core.Imaging;

/// <summary>A file that is not a DDS this reads, or a texture this cannot write as one.</summary>
public sealed class DdsException(string message) : Exception(message);

/// <summary>Writes DDS with the DX10 header, and reads back exactly what it writes.</summary>
/// <remarks>
///     <para>
///         KTX2 is what a Vixen build ships. This exists for the other end of a pipeline: a tool that
///         hands a texture to something outside the engine, where DDS is what that something opens.
///     </para>
///     <para>
///         <b>Always the DX10 header.</b> The older header has no way to say BC6H, BC7, sRGB or most
///         of the wide formats, and every reader that cares about those has the extension. A file
///         here is magic, header, extension, then pixels: nothing in it needs a second opinion from
///         the four-character codes the format grew by accident.
///     </para>
///     <para>
///         ⚠ <b>DDS orders by element and KTX2 by level.</b> A texture array or cube map in a DDS is
///         every mip of the first face, then every mip of the second; <see cref="TextureData" /> is
///         every face of the largest level, then every face of the next. A plain 2D texture is the
///         same either way, which is why the transpose below is easy to leave out and still pass.
///     </para>
///     <para>
///         <b>What it does not do.</b> <see cref="Read" /> is not a reader of arbitrary DDS: a legacy
///         header, a volume and a format not in <see cref="Table" /> are refused by name, because
///         guessing at one gives a texture that is wrong in a way nobody notices. The tolerant reader
///         for files other tools wrote is <c>Vixen.Interchange.DdsDecoder</c>, which is import-time
///         code and lives with the importers. Volumes are not written either.
///     </para>
/// </remarks>
public static class DdsCodec {
    const uint Magic = 0x20534444; // 'DDS '
    const uint FourCcDx10 = 0x30315844; // 'DX10'
    const int HeaderLength = 124;
    const int PixelFormatLength = 32;
    const int ExtensionLength = 20;
    const int PayloadOffset = 4 + HeaderLength + ExtensionLength;

    const uint FlagCaps = 0x1;
    const uint FlagHeight = 0x2;
    const uint FlagWidth = 0x4;
    const uint FlagPitch = 0x8;
    const uint FlagPixelFormat = 0x1000;
    const uint FlagMipMapCount = 0x20000;
    const uint FlagLinearSize = 0x80000;
    const uint PixelFormatFourCc = 0x4;
    const uint CapsComplex = 0x8;
    const uint CapsTexture = 0x1000;
    const uint CapsMipMap = 0x400000;
    const uint Caps2CubeAllFaces = 0xFE00;
    const uint ResourceTexture2D = 3;
    const uint MiscTextureCube = 0x4;

    /// <summary>Each format and its <c>DXGI_FORMAT</c> number.</summary>
    static readonly (PixelFormat Format, uint Dxgi)[] Table = [
        (PixelFormat.Rgba32Float, 2), // R32G32B32A32_FLOAT
        (PixelFormat.Rgba16Float, 10), // R16G16B16A16_FLOAT
        (PixelFormat.Rgba16UNorm, 11), // R16G16B16A16_UNORM
        (PixelFormat.Rg32Float, 16), // R32G32_FLOAT
        (PixelFormat.Rgba8UNorm, 28), // R8G8B8A8_UNORM
        (PixelFormat.Rgba8UNormSrgb, 29), // R8G8B8A8_UNORM_SRGB
        (PixelFormat.Rg16Float, 34), // R16G16_FLOAT
        (PixelFormat.Rg16UNorm, 35), // R16G16_UNORM
        (PixelFormat.R32Float, 41), // R32_FLOAT
        (PixelFormat.Rg8UNorm, 49), // R8G8_UNORM
        (PixelFormat.R16Float, 54), // R16_FLOAT
        (PixelFormat.R16UNorm, 56), // R16_UNORM
        (PixelFormat.R8UNorm, 61), // R8_UNORM
        (PixelFormat.Bc1RgbaUNorm, 71), // BC1_UNORM
        (PixelFormat.Bc1RgbaUNormSrgb, 72), // BC1_UNORM_SRGB
        (PixelFormat.Bc3RgbaUNorm, 77), // BC3_UNORM
        (PixelFormat.Bc3RgbaUNormSrgb, 78), // BC3_UNORM_SRGB
        (PixelFormat.Bc4RUNorm, 80), // BC4_UNORM
        (PixelFormat.Bc5RgUNorm, 83), // BC5_UNORM
        (PixelFormat.Bgra8UNorm, 87), // B8G8R8A8_UNORM
        (PixelFormat.Bgra8UNormSrgb, 91), // B8G8R8A8_UNORM_SRGB
        (PixelFormat.Bc6HRgbUFloat, 95), // BC6H_UF16
        (PixelFormat.Bc6HRgbSFloat, 96), // BC6H_SF16
        (PixelFormat.Bc7RgbaUNorm, 98), // BC7_UNORM
        (PixelFormat.Bc7RgbaUNormSrgb, 99) // BC7_UNORM_SRGB
    ];

    /// <summary>Every format that can be written.</summary>
    public static IEnumerable<PixelFormat> Supported => Table.Select(static entry => entry.Format);

    /// <summary>Whether a format can be written.</summary>
    /// <param name="format">The format.</param>
    /// <returns>Whether <see cref="Write" /> takes it.</returns>
    public static bool CanWrite(PixelFormat format) => Array.Exists(Table, entry => entry.Format == format);

    /// <summary>Writes a texture.</summary>
    /// <param name="texture">The texture: 2D, with any mip chain, as an array or a cube map if it likes.</param>
    /// <returns>The file's bytes.</returns>
    /// <exception cref="DdsException">The format has no entry here, or the texture is a volume.</exception>
    public static byte[] Write(TextureData texture) {
        ArgumentNullException.ThrowIfNull(texture);

        if (texture.Depth > 1) {
            throw new DdsException("A volume is not written here. DDS can hold one; nothing has needed it.");
        }

        if (texture.FaceCount is not (1 or 6)) {
            throw new DdsException($"{texture.FaceCount} faces is neither a plain texture nor a cube map.");
        }

        var dxgi = DxgiOf(texture.Format);
        var file = new byte[PayloadOffset + texture.ByteLength];
        var compressed = texture.Format.IsCompressed();
        var levelZeroBytes = (uint)texture.Format.LevelSize(texture.Width, 1);

        var flags = FlagCaps
            | FlagHeight
            | FlagWidth
            | FlagPixelFormat
            | (compressed ? FlagLinearSize : FlagPitch)
            | (texture.LevelCount > 1 ? FlagMipMapCount : 0);

        var caps = CapsTexture
            | (texture.LevelCount > 1 ? CapsMipMap | CapsComplex : 0)
            | (texture.FaceCount == 6 || texture.LayerCount > 1 ? CapsComplex : 0);

        var pitchOrLinearSize =
            compressed ? (uint)texture.Format.LevelSize(texture.Width, texture.Height) : levelZeroBytes;

        var span = file.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);

        var header = span[4..];

        BinaryPrimitives.WriteUInt32LittleEndian(header, HeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)texture.Height);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], (uint)texture.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], pitchOrLinearSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)texture.LevelCount);

        var pixelFormat = header[72..];

        BinaryPrimitives.WriteUInt32LittleEndian(pixelFormat, PixelFormatLength);
        BinaryPrimitives.WriteUInt32LittleEndian(pixelFormat[4..], PixelFormatFourCc);
        BinaryPrimitives.WriteUInt32LittleEndian(pixelFormat[8..], FourCcDx10);

        BinaryPrimitives.WriteUInt32LittleEndian(header[104..], caps);
        BinaryPrimitives.WriteUInt32LittleEndian(header[108..], texture.FaceCount == 6 ? Caps2CubeAllFaces : 0);

        var extension = span[(4 + HeaderLength)..];

        BinaryPrimitives.WriteUInt32LittleEndian(extension, dxgi);
        BinaryPrimitives.WriteUInt32LittleEndian(extension[4..], ResourceTexture2D);
        BinaryPrimitives.WriteUInt32LittleEndian(extension[8..], texture.FaceCount == 6 ? MiscTextureCube : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(extension[12..], (uint)texture.LayerCount);

        var images = texture.LayerCount * texture.FaceCount;
        var cursor = PayloadOffset;

        for (var image = 0; image < images; image++) {
            for (var level = 0; level < texture.LevelCount; level++) {
                var length = texture.Levels[level].Length / images;

                texture.Level(level).Slice(image * length, length).CopyTo(span[cursor..]);
                cursor += length;
            }
        }

        return file;
    }

    /// <summary>Reads a file this wrote.</summary>
    /// <param name="file">The file's bytes.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="DdsException">It is not a DX10-header 2D texture of a format in the table, or it is cut short.</exception>
    public static TextureData Read(ReadOnlySpan<byte> file) {
        if (file.Length < PayloadOffset || BinaryPrimitives.ReadUInt32LittleEndian(file) != Magic) {
            throw new DdsException("Not a DDS: the magic number does not match.");
        }

        var header = file[4..];

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != HeaderLength) {
            throw new DdsException("Not a DDS: the header does not say it is 124 bytes.");
        }

        var pixelFormat = header[72..];

        if ((BinaryPrimitives.ReadUInt32LittleEndian(pixelFormat[4..]) & PixelFormatFourCc) == 0
            || BinaryPrimitives.ReadUInt32LittleEndian(pixelFormat[8..]) != FourCcDx10) {
            throw new DdsException(
                "This DDS has no DX10 header. This reader takes what DdsCodec.Write writes; "
                + "Vixen.Interchange.DdsDecoder is the one for files from other tools."
            );
        }

        var extension = file[(4 + HeaderLength)..];
        var format = FormatOf(BinaryPrimitives.ReadUInt32LittleEndian(extension));

        if (BinaryPrimitives.ReadUInt32LittleEndian(extension[4..]) != ResourceTexture2D) {
            throw new DdsException("Only a 2D resource is read here; a volume or a 1D texture is refused.");
        }

        var cube = (BinaryPrimitives.ReadUInt32LittleEndian(extension[8..]) & MiscTextureCube) != 0;
        var height = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        var width = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        var levels = Math.Max(1, BinaryPrimitives.ReadInt32LittleEndian(header[24..]));
        var layers = Math.Max(1, BinaryPrimitives.ReadInt32LittleEndian(extension[12..]));

        if (width <= 0 || height <= 0) {
            throw new DdsException($"A {width}x{height} texture is not a texture.");
        }

        var texture = new TextureData(format, width, height, levels, layerCount: layers, faceCount: cube ? 6 : 1);
        var payload = file[PayloadOffset..];

        if (payload.Length < texture.ByteLength) {
            throw new DdsException($"The file is cut short: {payload.Length} bytes of pixels of {texture.ByteLength}.");
        }

        var images = layers * (cube ? 6 : 1);
        var cursor = 0;

        for (var image = 0; image < images; image++) {
            for (var level = 0; level < levels; level++) {
                var length = texture.Levels[level].Length / images;

                payload.Slice(cursor, length).CopyTo(texture.LevelSpan(level).Slice(image * length, length));
                cursor += length;
            }
        }

        return texture;
    }

    static uint DxgiOf(PixelFormat format) {
        foreach (var (candidate, dxgi) in Table) {
            if (candidate == format) {
                return dxgi;
            }
        }

        throw new DdsException($"{format} has no DXGI number here, so it cannot be written as DDS.");
    }

    static PixelFormat FormatOf(uint dxgi) {
        foreach (var (format, candidate) in Table) {
            if (candidate == dxgi) {
                return format;
            }
        }

        throw new DdsException($"DXGI format {dxgi} is not one this reads.");
    }
}
