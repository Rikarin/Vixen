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
///         dimensions, plane sizes and the samples the format calls for — and no more. It does not
///         attempt the descriptor's fuller vocabulary, because nothing in this engine reads it: the
///         <c>VkFormat</c> number is what <see cref="Ktx2.Read" /> uses, and the descriptor is
///         written for other people's tools.
///     </para>
///     <para>
///         ⚠ <b>Written for other people's tools is why this was wrong for so long.</b> Nothing here
///         consumes the descriptor, so nothing here noticed that alpha was labelled channel 3 rather
///         than 15, that float channels carried neither the <c>SIGNED</c> nor the <c>FLOAT</c>
///         qualifier and claimed an integer's range, that an sRGB format's alpha channel needs the
///         <c>LINEAR</c> qualifier because alpha is not sRGB-encoded, or that BC3 and BC5 are two
///         samples rather than one. <c>Ktx2ConformanceTests</c> found all of it in one run; the
///         values below are the ones Khronos's own writer produces, checked format by format.
///     </para>
/// </remarks>
public static class DataFormatDescriptor {
    /// <summary>How long a descriptor block's fixed part is, before its samples.</summary>
    public const int BasicBlockLength = 24;

    /// <summary>How long one sample is.</summary>
    public const int SampleLength = 16;

    /// <summary>The channel is stored linearly whatever the block's transfer function says.</summary>
    /// <remarks>An sRGB format's alpha channel: the colour channels are encoded, alpha never is.</remarks>
    const byte Linear = 0x10;

    /// <summary>The channel's values are signed.</summary>
    const byte Signed = 0x40;

    /// <summary>The channel's values are floating point.</summary>
    const byte Float = 0x80;

    /// <summary><c>-1.0f</c>, which is what a float channel's <c>sampleLower</c> is.</summary>
    const uint MinusOne = 0xBF80_0000;

    /// <summary><c>+1.0f</c>, which is what a float channel's <c>sampleUpper</c> is.</summary>
    const uint PlusOne = 0x3F80_0000;

    /// <summary>Alpha's channel id, which is 15 and not 3.</summary>
    const byte Alpha = 15;

    /// <summary>One channel of a texel block.</summary>
    /// <param name="BitOffset">Where the channel starts in the block.</param>
    /// <param name="BitLength">How many bits it is, less one.</param>
    /// <param name="ChannelType">The channel id in the low nibble, the qualifiers in the high one.</param>
    /// <param name="Lower">What the channel's minimum value means.</param>
    /// <param name="Upper">What the channel's maximum value means.</param>
    readonly record struct Sample(ushort BitOffset, byte BitLength, byte ChannelType, uint Lower, uint Upper);

    /// <summary>Builds the descriptor for a format.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The bytes, starting with the total size.</returns>
    public static byte[] Build(PixelFormat format) {
        var compressed = format.IsCompressed();
        var (blockWidth, blockHeight) = format.BlockExtent();
        var samples = compressed ? CompressedSamplesOf(format) : UncompressedSamplesOf(format);

        // Alpha is stored linearly even in an sRGB format and has to say so, whether it is a channel
        // of a plain texel or the alpha half of a BC3 block. Only a sample that is *identified* as
        // alpha qualifies: BC1's lone sample carries the BC1A model's channel 1, which is a
        // different vocabulary and takes no qualifier.
        if (format.IsSrgb()) {
            for (var index = 0; index < samples.Length; index++) {
                if ((samples[index].ChannelType & 0x0F) == Alpha) {
                    samples[index] = samples[index] with {
                        ChannelType = (byte)(samples[index].ChannelType | Linear)
                    };
                }
            }
        }

        var blockLength = BasicBlockLength + (samples.Length * SampleLength);
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
        block[11] = 0;                                              // straight alpha

        block[12] = (byte)(blockWidth - 1);
        block[13] = (byte)(blockHeight - 1);
        block[14] = 0;
        block[15] = 0;

        // bytesPlane0 is the size of one texel block; the other seven planes are unused.
        block[16] = (byte)format.BlockSize();

        for (var index = 0; index < samples.Length; index++) {
            var sample = samples[index];
            var bytes = block[(BasicBlockLength + (index * SampleLength))..];

            BinaryPrimitives.WriteUInt16LittleEndian(bytes, sample.BitOffset);
            bytes[2] = sample.BitLength;
            bytes[3] = sample.ChannelType;

            // samplePosition0..3 stay zero: one sample per channel, all at the block's origin.
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], sample.Lower);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], sample.Upper);
        }

        return descriptor;
    }

    /// <summary>The samples of a plain, one-sample-per-channel format.</summary>
    /// <remarks>
    ///     Channels are described in the order their bytes appear, which for BGRA means the first
    ///     sample is blue. What identifies a channel is its <c>channelType</c>, not its position, and
    ///     alpha's is 15 — a value the specification puts at the top of the nibble with depth and
    ///     stencil rather than after blue, which is exactly the sort of thing a writer that nothing
    ///     reads gets wrong quietly.
    /// </remarks>
    static Sample[] UncompressedSamplesOf(PixelFormat format) {
        var channels = ChannelCountOf(format);
        var bits = format.BlockSize() * 8 / channels;
        var isFloat = IsFloat(format);
        var samples = new Sample[channels];

        for (var index = 0; index < channels; index++) {
            var qualifiers = (byte)(isFloat ? Signed | Float : 0);

            samples[index] = new(
                (ushort)(index * bits),
                (byte)(bits - 1),
                (byte)(ChannelIdOf(format, index) | qualifiers),
                isFloat ? MinusOne : 0,
                isFloat ? PlusOne : (uint)((1L << bits) - 1)
            );
        }

        return samples;
    }

    /// <summary>The samples of a block-compressed format.</summary>
    /// <remarks>
    ///     <para>
    ///         A compressed format's samples describe the <i>block</i>, not the texels in it, so
    ///         there is one per independently coded part rather than one per colour channel. BC3 is
    ///         a BC4 alpha block followed by a BC1 colour block and is described as two 64-bit
    ///         samples; BC5 is two BC4 blocks and is described the same way. BC1's single sample
    ///         carries channel id 1 rather than 0 — the model is <c>BC1A</c>, and naming the alpha
    ///         channel is how a punch-through-alpha BC1 is distinguished from an opaque one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>BC6H's <c>sampleLower</c> is where unsigned and signed differ in the
    ///         descriptor.</b> Both carry the <c>FLOAT</c> qualifier and an upper of <c>+1.0f</c>;
    ///         only the signed form adds <c>SIGNED</c> and a lower of <c>-1.0f</c>. This engine
    ///         writes the unsigned form, and <see cref="VkFormats" /> used to name the signed one.
    ///     </para>
    /// </remarks>
    static Sample[] CompressedSamplesOf(PixelFormat format) {
        // A whole block as one sample: the block size in bits, less one, and the full integer range.
        var whole = (byte)((format.BlockSize() * 8) - 1);

        return format switch {
            PixelFormat.Bc1RgbaUNorm or PixelFormat.Bc1RgbaUNormSrgb => [new(0, whole, 1, 0, uint.MaxValue)],
            PixelFormat.Bc3RgbaUNorm or PixelFormat.Bc3RgbaUNormSrgb => [
                new(0, 63, Alpha, 0, uint.MaxValue),
                new(64, 63, 0, 0, uint.MaxValue)
            ],
            PixelFormat.Bc4RUNorm => [new(0, whole, 0, 0, uint.MaxValue)],
            PixelFormat.Bc5RgUNorm => [
                new(0, 63, 0, 0, uint.MaxValue),
                new(64, 63, 1, 0, uint.MaxValue)
            ],
            PixelFormat.Bc6HRgbUFloat => [new(0, whole, Float, 0, PlusOne)],
            PixelFormat.Etc2Rgb8A1UNorm => [
                new(0, 63, 2, 0, uint.MaxValue),
                new(0, 63, Alpha, 0, uint.MaxValue)
            ],
            PixelFormat.Etc2Rgba8UNorm => [
                new(0, 63, Alpha, 0, uint.MaxValue),
                new(64, 63, 2, 0, uint.MaxValue)
            ],
            _ => [new(0, whole, 0, 0, uint.MaxValue)]   // BC7 and ASTC: one sample, channel 0
        };
    }

    static bool IsFloat(PixelFormat format) =>
        format is PixelFormat.Rgba16Float
            or PixelFormat.Rg16Float
            or PixelFormat.R16Float
            or PixelFormat.Rgba32Float
            or PixelFormat.Rg32Float
            or PixelFormat.R32Float;

    static int ChannelCountOf(PixelFormat format) =>
        format switch {
            PixelFormat.R8UNorm or PixelFormat.R32Float => 1,
            PixelFormat.Rg8UNorm => 2,
            _ => 4
        };

    /// <summary>Which channel a sample describes, in the order the bytes appear.</summary>
    /// <remarks>
    ///     BGRA stores blue first, so its samples are 2, 1, 0, 15 rather than 0, 1, 2, 15. Writing
    ///     them in memory order and labelling them correctly is the whole point of the descriptor.
    /// </remarks>
    static byte ChannelIdOf(PixelFormat format, int index) {
        var channel = format is PixelFormat.Bgra8UNorm or PixelFormat.Bgra8UNormSrgb
            ? index switch { 0 => 2, 2 => 0, _ => index }
            : index;

        return (byte)(channel == 3 ? Alpha : channel);
    }

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
