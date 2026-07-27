// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Graphics;

namespace Vixen.Core.Imaging;

/// <summary>The Khronos data format descriptor a KTX2 file carries.</summary>
/// <remarks>
///     <para>
///         A KTX2 file says what its bytes are twice: once as a <c>VkFormat</c> number in the header,
///         and once as a descriptor that spells out the channels, their bit positions and the
///         transfer function. The second exists so a reader that has never heard of the number can
///         still work out what it is holding, and the specification requires it even when the number
///         says everything.
///     </para>
///     <para>
///         This writes the basic block — colour model, primaries, transfer function, texel block
///         dimensions, plane sizes and one sample per channel — which is what the format calls for
///         and no more. It does not attempt the descriptor's fuller vocabulary, because nothing in
///         this engine reads it: the <c>VkFormat</c> number is what <see cref="Ktx2.Read" /> uses,
///         and the descriptor is written for other people's tools.
///     </para>
/// </remarks>
public static class DataFormatDescriptor {
    /// <summary>How long a descriptor block's fixed part is, before its samples.</summary>
    public const int BasicBlockLength = 24;

    /// <summary>How long one sample is.</summary>
    public const int SampleLength = 16;

    /// <summary>Builds the descriptor for a format.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The bytes, starting with the total size.</returns>
    public static byte[] Build(PixelFormat format) {
        var compressed = format.IsCompressed();
        var (blockWidth, blockHeight) = format.BlockExtent();
        var channels = compressed ? 1 : ChannelCountOf(format);
        var bytesPerChannel = compressed ? 0 : format.BlockSize() / channels;

        var blockLength = BasicBlockLength + (channels * SampleLength);
        var descriptor = new byte[4 + blockLength];
        var span = descriptor.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)descriptor.Length);

        var block = span[4..];

        // vendorId 0 (Khronos), descriptorType 0 (basic), in the low 17 and high 15 bits.
        BinaryPrimitives.WriteUInt32LittleEndian(block, 0);

        // versionNumber 2 in the low 16, descriptorBlockSize in the high 16.
        BinaryPrimitives.WriteUInt32LittleEndian(block[4..], 2u | ((uint)blockLength << 16));

        block[8] = (byte)(compressed ? ColorModelOf(format) : 1);   // RGBSDA is 1
        block[9] = 1;                                               // BT.709 primaries
        block[10] = (byte)(format.IsSrgb() ? 2 : 1);                // sRGB is 2, linear is 1
        block[11] = 0;                                              // no alpha flags

        block[12] = (byte)(blockWidth - 1);
        block[13] = (byte)(blockHeight - 1);
        block[14] = 0;
        block[15] = 0;

        // bytesPlane0 is the size of one texel block; the other seven planes are unused.
        block[16] = (byte)format.BlockSize();

        for (var channel = 0; channel < channels; channel++) {
            var sample = block[(BasicBlockLength + (channel * SampleLength))..];
            var bits = compressed ? format.BlockSize() * 8 : bytesPerChannel * 8;
            var offset = compressed ? 0 : channel * bits;

            BinaryPrimitives.WriteUInt16LittleEndian(sample, (ushort)offset);
            sample[2] = (byte)(bits - 1);
            sample[3] = (byte)(compressed ? 0 : ChannelIdOf(format, channel));

            // samplePosition0..3 stay zero; the low and high values say what the channel's range
            // means, which for an unsigned normalised channel is 0 to its maximum.
            BinaryPrimitives.WriteUInt32LittleEndian(sample[8..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(sample[12..], compressed ? uint.MaxValue : (uint)((1L << bits) - 1));
        }

        return descriptor;
    }

    static int ChannelCountOf(PixelFormat format) =>
        format switch {
            PixelFormat.R8UNorm or PixelFormat.R32Float => 1,
            PixelFormat.Rg8UNorm => 2,
            _ => 4
        };

    /// <summary>Which channel index a sample describes, in the order the bytes appear.</summary>
    /// <remarks>
    ///     BGRA stores blue first, so its samples are 2, 1, 0, 3 rather than 0, 1, 2, 3. Writing
    ///     them in memory order and labelling them correctly is the whole point of the descriptor.
    /// </remarks>
    static int ChannelIdOf(PixelFormat format, int index) =>
        format is PixelFormat.Bgra8UNorm or PixelFormat.Bgra8UNormSrgb
            ? index switch { 0 => 2, 2 => 0, _ => index }
            : index;

    static byte ColorModelOf(PixelFormat format) =>
        format switch {
            PixelFormat.Bc1RgbaUNorm or PixelFormat.Bc1RgbaUNormSrgb => 128,
            PixelFormat.Bc3RgbaUNorm or PixelFormat.Bc3RgbaUNormSrgb => 130,
            PixelFormat.Bc4RUNorm => 131,
            PixelFormat.Bc5RgUNorm => 132,
            PixelFormat.Bc6HRgbUFloat => 133,
            PixelFormat.Bc7RgbaUNorm or PixelFormat.Bc7RgbaUNormSrgb => 134,
            PixelFormat.Etc2Rgb8A1UNorm or PixelFormat.Etc2Rgba8UNorm => 161,
            _ => 162   // ASTC
        };
}
