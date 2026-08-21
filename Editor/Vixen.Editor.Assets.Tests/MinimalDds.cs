// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Text;

namespace Vixen.Editor.Assets.Tests;

/// <summary>Writes a DDS, from the specification, so that the decoder's input is not its own output.</summary>
/// <remarks>
///     <para>
///         The same argument <see cref="MinimalPng" /> makes: a fixture produced by the code under
///         test agrees with it by construction and proves nothing. This is DirectDraw's own container
///         — the four-byte magic, the 124-byte header, its 32-byte pixel format, and optionally the
///         20-byte DX10 extension — laid out by hand from the published field order.
///     </para>
///     <para>
///         It writes headers the decoder is expected to <b>refuse</b> as readily as ones it is
///         expected to read, because "claimed and refused" is only a promise if a test can build the
///         file that triggers it.
///     </para>
/// </remarks>
static class MinimalDds {
    /// <summary>DDSCAPS2_CUBEMAP together with all six face bits.</summary>
    public const uint CubeMapCaps = 0x200 | 0x400 | 0x800 | 0x1000 | 0x2000 | 0x4000 | 0x8000;

    /// <summary>DDSCAPS2_VOLUME.</summary>
    public const uint VolumeCaps = 0x200000;

    /// <summary>Writes a file whose format is stated by the DX10 extension header.</summary>
    /// <param name="width">The largest level's width.</param>
    /// <param name="height">The largest level's height.</param>
    /// <param name="dxgiFormat">The DXGI format number.</param>
    /// <param name="payload">Everything after the headers.</param>
    /// <param name="mipCount">How many mip levels the header declares.</param>
    /// <param name="arraySize">How many array elements the extension header declares.</param>
    /// <param name="miscFlag">The extension header's misc flag — 4 is D3D10_RESOURCE_MISC_TEXTURECUBE.</param>
    /// <param name="dimension">The resource dimension — 3 is 2D, 4 is 3D.</param>
    /// <param name="depth">The largest level's depth.</param>
    /// <param name="caps2">The header's second caps field, for a legacy cube or volume declaration.</param>
    /// <returns>The file's bytes.</returns>
    public static byte[] Write(
        int width,
        int height,
        uint dxgiFormat,
        ReadOnlySpan<byte> payload,
        int mipCount = 1,
        uint arraySize = 1,
        uint miscFlag = 0,
        uint dimension = 3,
        int depth = 1,
        uint caps2 = 0
    ) {
        var file = new byte[148 + payload.Length];
        Header(file, width, height, mipCount, depth, caps2);

        // The pixel format says "look in the extension header" and nothing else.
        var pixelFormat = file.AsSpan(0x4C);
        BinaryPrimitives.WriteUInt32LittleEndian(pixelFormat, 32);
        BinaryPrimitives.WriteUInt32LittleEndian(pixelFormat[4..], 0x4);   // DDPF_FOURCC
        Encoding.ASCII.GetBytes("DX10", pixelFormat[8..]);

        var extension = file.AsSpan(128);
        BinaryPrimitives.WriteUInt32LittleEndian(extension, dxgiFormat);
        BinaryPrimitives.WriteUInt32LittleEndian(extension[4..], dimension);
        BinaryPrimitives.WriteUInt32LittleEndian(extension[8..], miscFlag);
        BinaryPrimitives.WriteUInt32LittleEndian(extension[12..], arraySize);
        BinaryPrimitives.WriteUInt32LittleEndian(extension[16..], 0);

        payload.CopyTo(file.AsSpan(148));

        return file;
    }

    /// <summary>Writes a file named by a legacy four-character code, as every pre-D3D10 DXT file was.</summary>
    /// <param name="width">The largest level's width.</param>
    /// <param name="height">The largest level's height.</param>
    /// <param name="fourCc">The code, four ASCII characters.</param>
    /// <param name="payload">Everything after the header.</param>
    /// <param name="mipCount">How many mip levels the header declares.</param>
    /// <returns>The file's bytes.</returns>
    public static byte[] WriteFourCc(
        int width,
        int height,
        string fourCc,
        ReadOnlySpan<byte> payload,
        int mipCount = 1
    ) {
        var file = new byte[128 + payload.Length];
        Header(file, width, height, mipCount, depth: 1, caps2: 0);

        var pixelFormat = file.AsSpan(0x4C);
        BinaryPrimitives.WriteUInt32LittleEndian(pixelFormat, 32);
        BinaryPrimitives.WriteUInt32LittleEndian(pixelFormat[4..], 0x4);   // DDPF_FOURCC
        Encoding.ASCII.GetBytes(fourCc, pixelFormat[8..]);

        payload.CopyTo(file.AsSpan(128));

        return file;
    }

    /// <summary>Writes a file whose format is a set of channel masks, as an uncompressed legacy file is.</summary>
    /// <param name="width">The largest level's width.</param>
    /// <param name="height">The largest level's height.</param>
    /// <param name="bitCount">Bits per pixel.</param>
    /// <param name="masks">The red, green, blue and alpha masks, in that order.</param>
    /// <param name="payload">Everything after the header.</param>
    /// <returns>The file's bytes.</returns>
    public static byte[] WriteMasked(
        int width,
        int height,
        uint bitCount,
        (uint Red, uint Green, uint Blue, uint Alpha) masks,
        ReadOnlySpan<byte> payload
    ) {
        var file = new byte[128 + payload.Length];
        Header(file, width, height, mipCount: 1, depth: 1, caps2: 0);

        var pixelFormat = file.AsSpan(0x4C);
        BinaryPrimitives.WriteUInt32LittleEndian(pixelFormat, 32);

        // DDPF_RGB, plus DDPF_ALPHAPIXELS when there is an alpha mask to believe.
        BinaryPrimitives.WriteUInt32LittleEndian(pixelFormat[4..], masks.Alpha == 0 ? 0x40u : 0x41u);
        BinaryPrimitives.WriteUInt32LittleEndian(pixelFormat[12..], bitCount);
        BinaryPrimitives.WriteUInt32LittleEndian(pixelFormat[16..], masks.Red);
        BinaryPrimitives.WriteUInt32LittleEndian(pixelFormat[20..], masks.Green);
        BinaryPrimitives.WriteUInt32LittleEndian(pixelFormat[24..], masks.Blue);
        BinaryPrimitives.WriteUInt32LittleEndian(pixelFormat[28..], masks.Alpha);

        payload.CopyTo(file.AsSpan(128));

        return file;
    }

    static void Header(Span<byte> file, int width, int height, int mipCount, int depth, uint caps2) {
        Encoding.ASCII.GetBytes("DDS ", file);

        BinaryPrimitives.WriteUInt32LittleEndian(file[4..], 124);                    // dwSize

        // DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT, plus DDSD_MIPMAPCOUNT and
        // DDSD_DEPTH when there is anything to say. The flags are advisory in practice — every
        // reader in the world trusts the fields — but a fixture that leaves them out is not a file
        // the format describes.
        var flags = 0x1u | 0x2u | 0x4u | 0x1000u
            | (mipCount > 1 ? 0x20000u : 0u)
            | (depth > 1 ? 0x800000u : 0u);

        BinaryPrimitives.WriteUInt32LittleEndian(file[8..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(file[12..], (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(file[16..], (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(file[20..], 0);                     // dwPitchOrLinearSize
        BinaryPrimitives.WriteUInt32LittleEndian(file[24..], (uint)depth);
        BinaryPrimitives.WriteUInt32LittleEndian(file[28..], (uint)mipCount);

        // 0x20..0x4B is dwReserved1[11], and stays zero.
        // 0x4C..0x6B is the pixel format, which the caller fills in.
        BinaryPrimitives.WriteUInt32LittleEndian(file[0x6C..], 0x1000);              // DDSCAPS_TEXTURE
        BinaryPrimitives.WriteUInt32LittleEndian(file[0x70..], caps2);
    }
}
