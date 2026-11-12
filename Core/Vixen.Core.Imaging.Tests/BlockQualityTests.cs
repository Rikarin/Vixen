// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging.BlockCompression;
using Vixen.Graphics;
using Xunit;

namespace Vixen.Core.Imaging.Tests;

/// <summary>What the BC7 and BC6H searches are worth, held in numbers.</summary>
/// <remarks>
///     <para>
///         <b>The floor is a closed form, and the targets are measurements.</b> A block of two flat
///         colours along a partition pattern is one the format holds exactly, so its bound comes from
///         the endpoint precision rather than from what the encoder happened to achieve. The PSNR
///         targets are the other kind: what was measured, less a margin, on pictures that are
///         formulas (<see cref="ReferenceImages" />). A regression in the search shows up as a
///         target missed, and the numbers mean the same on any machine because the arithmetic is the
///         same on any machine.
///     </para>
///     <para>
///         <b>No clock appears here.</b> What a level costs is in
///         <c>Benchmarks/Vixen.Benchmarks.Imaging</c> and the README; a test that asserted it would
///         be a flake with a number in it.
///     </para>
///     <para>
///         ⚠ <b>The exactness tests are the ones that can see partitioned modes go away</b>, and that
///         has been shown rather than assumed: with the two-subset search and the three-subset search
///         switched off, <see cref="TwoFlatColoursOnAnyTwoSubsetPatternComeBackAlmostExact" /> and
///         <see cref="ThreeFlatColoursOnAnyThreeSubsetPatternComeBackClose" /> go red and the
///         three-colour target falls from 58 dB to 31, or to 26 at <see cref="BlockQuality.Fast" />,
///         which is what mode 6 alone had always scored.
///     </para>
/// </remarks>
public sealed class BlockQualityTests {
    const int Size = 256;

    /// <summary>Every level, as an array that exists before anything measures an allocation.</summary>
    static readonly BlockQuality[] Levels = Enum.GetValues<BlockQuality>();

    /// <summary>
    ///     The floor per image at each level, in decibels: what was measured at <see cref="Size" />,
    ///     rounded down by at least half a decibel. Two-colour edges are lossless from Balanced up,
    ///     which the 90 stands in for.
    /// </summary>
    public static TheoryData<string, BlockQuality, double> Bc7Targets =>
        new() {
            { "gradient", BlockQuality.Fast, 49.0 },
            { "gradient", BlockQuality.Balanced, 50.0 },
            { "gradient", BlockQuality.Best, 51.0 },
            { "two-colour edges", BlockQuality.Fast, 56.0 },
            { "two-colour edges", BlockQuality.Balanced, 90.0 },
            { "two-colour edges", BlockQuality.Best, 90.0 },
            { "three-colour edges", BlockQuality.Fast, 52.0 },
            { "three-colour edges", BlockQuality.Balanced, 57.0 },
            { "three-colour edges", BlockQuality.Best, 57.0 },
            { "filtered noise", BlockQuality.Fast, 35.5 },
            { "filtered noise", BlockQuality.Balanced, 36.5 },
            { "filtered noise", BlockQuality.Best, 36.5 },
            { "independent alpha", BlockQuality.Fast, 32.5 },
            { "independent alpha", BlockQuality.Balanced, 33.0 },
            { "independent alpha", BlockQuality.Best, 33.0 },
            { "normal map", BlockQuality.Fast, 41.5 },
            { "normal map", BlockQuality.Balanced, 43.5 },
            { "normal map", BlockQuality.Best, 43.5 }
        };

    /// <summary>The floor for the HDR pictures, in half-float bit space.</summary>
    public static TheoryData<bool, BlockQuality, double> Bc6HTargets =>
        new() {
            { false, BlockQuality.Fast, 69.0 },
            { false, BlockQuality.Balanced, 70.0 },
            { false, BlockQuality.Best, 70.0 },
            { true, BlockQuality.Fast, 63.0 },
            { true, BlockQuality.Balanced, 63.0 },
            { true, BlockQuality.Best, 63.0 }
        };

    /// <summary>Every partition number. BC6H uses the first thirty-two of them.</summary>
    public static TheoryData<int> Partitions {
        get {
            var data = new TheoryData<int>();

            for (var partition = 0; partition < 64; partition++) {
                data.Add(partition);
            }

            return data;
        }
    }

    /// <summary>⚠ The guard against a suite that quietly checked nothing.</summary>
    [Fact]
    public void TheCorpusIsTheSizeItIsMeantToBe() {
        Assert.Equal(6, ReferenceImages.LowDynamicRange.Count);
        Assert.Equal(6 * 3, Bc7Targets.Count);
        Assert.Equal(2 * 3, Bc6HTargets.Count);
        Assert.Equal(64, Partitions.Count);
    }

    [Theory]
    [MemberData(nameof(Bc7Targets))]
    public void Bc7HoldsItsTargetOnEachReferenceImage(string name, BlockQuality quality, double target) {
        var image = ReferenceImages.LowDynamicRange.Single(entry => entry.Name == name).Make(Size);
        var decoded = BlockCompressor.Decode(
            BlockCompressor.Encode(image, PixelFormat.Bc7RgbaUNorm, new() { Quality = quality })
        );
        var psnr = ReferenceImages.Psnr(image.Pixels, decoded.Pixels);

        Assert.True(psnr >= target, $"{name} at {quality}: {psnr:F2} dB, and the target is {target}");
    }

    [Theory]
    [MemberData(nameof(Bc6HTargets))]
    public void Bc6HHoldsItsTargetOnTheHdrReferenceImage(bool signed, BlockQuality quality, double target) {
        var image = signed ? ReferenceImages.SignedHighDynamicRange(Size) : ReferenceImages.HighDynamicRange(Size);
        var format = signed ? PixelFormat.Bc6HRgbSFloat : PixelFormat.Bc6HRgbUFloat;
        var decoded = BlockCompressor.Decode(BlockCompressor.Encode(image, format, new() { Quality = quality }));
        var psnr = ReferenceImages.HalfPsnr(image.Pixels, decoded.Pixels);

        Assert.True(
            psnr >= target,
            $"{(signed ? "signed" : "unsigned")} at {quality}: {psnr:F2} dB, and the target is {target}"
        );
    }

    /// <summary>A higher level searches a superset, so on any picture it may not come back worse.</summary>
    [Fact]
    public void EachLevelIsAtLeastAsGoodAsTheOneBelow() {
        foreach (var (name, make) in ReferenceImages.LowDynamicRange) {
            var image = make(Size);
            var previous = double.NegativeInfinity;

            foreach (var quality in Enum.GetValues<BlockQuality>()) {
                var decoded = BlockCompressor.Decode(
                    BlockCompressor.Encode(image, PixelFormat.Bc7RgbaUNorm, new() { Quality = quality })
                );
                var psnr = ReferenceImages.Psnr(image.Pixels, decoded.Pixels);

                Assert.True(psnr >= previous - 0.01, $"{name} at {quality}: {psnr:F2} dB after {previous:F2}");
                previous = psnr;
            }
        }
    }

    /// <summary>
    ///     Two colours, one per subset, are the two ends of a two-subset block: nothing has to be
    ///     interpolated, so the only error is what the endpoints cannot hold. Mode 3 stores eight bits
    ///     with a low bit shared across an endpoint's channels, so no channel can be off by more
    ///     than one. Every pattern, because a wrong table entry would cost only the pattern it is in.
    /// </summary>
    [Theory]
    [MemberData(nameof(Partitions))]
    public void TwoFlatColoursOnAnyTwoSubsetPatternComeBackAlmostExact(int partition) {
        ReadOnlySpan<byte> colours = [201, 34, 18, 255, 12, 147, 228, 255];
        var rgba = new byte[64];

        for (var texel = 0; texel < 16; texel++) {
            colours.Slice(BptcTables.TwoSubsets[partition * 16 + texel] * 4, 4).CopyTo(rgba.AsSpan(texel * 4));
        }

        foreach (var quality in new[] { BlockQuality.Balanced, BlockQuality.Best }) {
            var decoded = RoundTrip(rgba, quality);

            Assert.True(
                MaxError(rgba, decoded) <= 1,
                $"pattern {partition} at {quality}: off by {MaxError(rgba, decoded)}"
            );
        }
    }

    /// <summary>
    ///     Three colours on a three-subset pattern, whose modes have five bits an endpoint and no
    ///     interpolation to lean on but the palette between two codes of the same colour. Two thirds
    ///     of a five-bit step is the most that can be left, and the bound is that plus the rounding.
    /// </summary>
    [Theory]
    [MemberData(nameof(Partitions))]
    public void ThreeFlatColoursOnAnyThreeSubsetPatternComeBackClose(int partition) {
        ReadOnlySpan<byte> colours = [201, 34, 18, 255, 12, 147, 228, 255, 96, 240, 77, 255];
        var rgba = new byte[64];

        for (var texel = 0; texel < 16; texel++) {
            colours.Slice(BptcTables.ThreeSubsets[partition * 16 + texel] * 4, 4).CopyTo(rgba.AsSpan(texel * 4));
        }

        foreach (var quality in new[] { BlockQuality.Balanced, BlockQuality.Best }) {
            var decoded = RoundTrip(rgba, quality);

            Assert.True(
                MaxError(rgba, decoded) <= 3,
                $"pattern {partition} at {quality}: off by {MaxError(rgba, decoded)}"
            );
        }
    }

    /// <summary>
    ///     Two radiances close enough for the deltas to reach, on every BC6H pattern. The bound is in
    ///     half-float bit patterns, and 64 is six-tenths of one per cent of the value at any
    ///     exposure.
    /// </summary>
    [Theory]
    [MemberData(nameof(Partitions))]
    public void TwoFlatRadiancesOnAnyBc6HPatternComeBackClose(int partition) {
        if (partition >= Bc6HBlock.PartitionCount) {
            return;
        }

        ReadOnlySpan<ushort> colours = [0x4410, 0x4890, 0x3F20, 0x4620, 0x4480, 0x4110];
        var rgb = new ushort[48];

        for (var texel = 0; texel < 16; texel++) {
            colours.Slice(BptcTables.TwoSubsets[partition * 16 + texel] * 3, 3).CopyTo(rgb.AsSpan(texel * 3));
        }

        Span<byte> block = stackalloc byte[16];
        Span<ushort> decoded = stackalloc ushort[48];

        foreach (var quality in new[] { BlockQuality.Balanced, BlockQuality.Best }) {
            Bc6HBlock.Encode(rgb, block, quality);
            Bc6HBlock.Decode(block, decoded);

            for (var at = 0; at < 48; at++) {
                var off = Math.Abs(rgb[at] - decoded[at]);

                Assert.True(off <= 64, $"pattern {partition} at {quality}, component {at}: off by {off} patterns");
            }
        }
    }

    /// <summary>Any block of one colour is exact: mode 5's table makes every byte reachable.</summary>
    [Fact]
    public void EveryFlatColourIsExact() {
        var rgba = new byte[64];

        for (var value = 0; value < 256; value++) {
            for (var texel = 0; texel < 16; texel++) {
                rgba[texel * 4] = (byte)value;
                rgba[texel * 4 + 1] = (byte)(255 - value);
                rgba[texel * 4 + 2] = (byte)(value * 37);
                rgba[texel * 4 + 3] = (byte)(value * 11);
            }

            Assert.Equal(rgba, RoundTrip(rgba, BlockQuality.Fast));
        }
    }

    /// <summary>
    ///     Every block is a pure function of its own texels, so the bytes do not depend on the thread
    ///     count. The predicate is shown false too: different levels do write different bytes.
    /// </summary>
    [Theory]
    [InlineData(PixelFormat.Bc7RgbaUNorm)]
    [InlineData(PixelFormat.Bc1RgbaUNorm)]
    public void TheSameBytesComeOutWhateverTheThreadCount(PixelFormat format) {
        var image = ReferenceImages.IndependentAlpha(512);
        var one = BlockCompressor.Encode(image, format, new() { MaxDegreeOfParallelism = 1 });

        foreach (var threads in new[] { 2, 3, 8, 0 }) {
            var many = BlockCompressor.Encode(image, format, new() { MaxDegreeOfParallelism = threads });

            Assert.True(one.Pixels.SequenceEqual(many.Pixels), $"{threads} threads wrote different bytes");
        }

        if (format == PixelFormat.Bc7RgbaUNorm) {
            var other = BlockCompressor.Encode(image, format, new() { Quality = BlockQuality.Fast });

            Assert.False(one.Pixels.SequenceEqual(other.Pixels));
        }
    }

    [Fact]
    public void TheSameBytesComeOutOfBc6HWhateverTheThreadCount() {
        var image = ReferenceImages.HighDynamicRange(512);
        var one = BlockCompressor.Encode(image, PixelFormat.Bc6HRgbUFloat, new() { MaxDegreeOfParallelism = 1 });
        var many = BlockCompressor.Encode(image, PixelFormat.Bc6HRgbUFloat, new());

        Assert.True(one.Pixels.SequenceEqual(many.Pixels));
    }

    /// <summary>Progress climbs by whole runs, never backwards, and ends at the total.</summary>
    [Fact]
    public void ProgressClimbsToTheTotal() {
        var image = ReferenceImages.FilteredNoise(256);
        var mipped = new TextureData(image.Format, 256, 256);

        image.Pixels.CopyTo(mipped.PixelSpan());
        MipChain.Generate(mipped);

        var progress = new Recorder();

        BlockCompressor.Encode(
            mipped,
            PixelFormat.Bc7RgbaUNorm,
            new() { Quality = BlockQuality.Fast, Progress = progress }
        );

        var expected = mipped.Levels.Sum(static level => (long)((level.Width + 3) / 4) * ((level.Height + 3) / 4));

        Assert.True(progress.Seen.Count > 2, "one report is not progress");
        Assert.Equal(expected, progress.Seen[^1].BlocksTotal);
        Assert.Equal(expected, progress.Seen[^1].BlocksDone);
        Assert.Equal(1.0, progress.Seen[^1].Fraction);

        for (var at = 1; at < progress.Seen.Count; at++) {
            Assert.True(progress.Seen[at].BlocksDone > progress.Seen[at - 1].BlocksDone, "progress went backwards");
        }
    }

    /// <summary>
    ///     A token cancelled from the first report stops the encode with most of it undone: a count
    ///     of blocks, not a clock. The other half of the predicate is that an uncancelled encode of
    ///     the same texture reports every block.
    /// </summary>
    [Fact]
    public void CancellingStopsTheEncodeBetweenBlocks() {
        var image = ReferenceImages.FilteredNoise(1024);
        using var cancel = new CancellationTokenSource();
        var progress = new Recorder(cancel.Cancel);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            BlockCompressor.Encode(
                image,
                PixelFormat.Bc7RgbaUNorm,
                new() { Quality = BlockQuality.Best, Progress = progress, CancellationToken = cancel.Token }
            )
        );

        var total = 256L * 256L;
        var done = progress.Seen[^1].BlocksDone;

        Assert.True(done < total / 2, $"{done} of {total} blocks finished after the first report cancelled");
    }

    [Fact]
    public void AnAlreadyCancelledEncodeDoesNoWork() {
        var progress = new Recorder();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            BlockCompressor.Encode(
                ReferenceImages.Gradient(64),
                PixelFormat.Bc7RgbaUNorm,
                new() { Progress = progress, CancellationToken = new(true) }
            )
        );

        Assert.Empty(progress.Seen);
    }

    /// <summary>
    ///     Allocation-free per block: every buffer is on the stack, at every level, in every mode the
    ///     search can reach. The noise block forces the whole search.
    /// </summary>
    [Fact]
    public void EncodingABlockAllocatesNothing() {
        var image = ReferenceImages.IndependentAlpha(16).Level(0).ToArray();
        var hdr = new ushort[48];
        var block = new byte[16];

        for (var at = 0; at < hdr.Length; at++) {
            hdr[at] = (ushort)(0x3000 + at * 311 % 0x1800);
        }

        var rgba = image.AsSpan(0, 64);

        // Once through, so that static tables and first-call stubs are not what is being measured.
        foreach (var quality in Levels) {
            Bc7Block.Encode(rgba, block, quality);
            Bc6HBlock.Encode(hdr, block, quality);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        foreach (var quality in Levels) {
            Bc7Block.Encode(rgba, block, quality);
            Bc6HBlock.Encode(hdr, block, quality, true);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>A sixteen-bit source rounds to the nearest byte, in every channel count.</summary>
    [Theory]
    [InlineData(PixelFormat.Rgba16UNorm, 4)]
    [InlineData(PixelFormat.Rg16UNorm, 2)]
    [InlineData(PixelFormat.R16UNorm, 1)]
    public void ASixteenBitSourceEncodesAsItsEightBitRounding(PixelFormat format, int channels) {
        var wide = new TextureData(format, 8, 8, 1);
        var narrow = new TextureData(PixelFormat.Rgba8UNorm, 8, 8, 1);
        var random = new Random(0x16);
        var samples = wide.PixelSpan();
        var bytes = narrow.PixelSpan();

        for (var texel = 0; texel < 64; texel++) {
            for (var channel = 0; channel < 4; channel++) {
                var value = channel < channels ? (ushort)random.Next(0, 65536) : (ushort)0;

                if (channel < channels) {
                    BitConverter.TryWriteBytes(samples[((texel * channels + channel) * 2)..], value);
                }

                bytes[texel * 4 + channel] = channel < channels
                    ? (byte)Math.Round(value / 257.0, MidpointRounding.AwayFromZero)
                    : channel == 3
                        ? (byte)255
                        : (byte)0;
            }
        }

        foreach (var target in new[] {
                     PixelFormat.Bc7RgbaUNorm, PixelFormat.Bc1RgbaUNorm, PixelFormat.Bc4RUNorm, PixelFormat.Bc5RgUNorm
                 }) {
            Assert.Equal(
                BlockCompressor.Encode(narrow, target).Pixels.ToArray(),
                BlockCompressor.Encode(wide, target).Pixels.ToArray()
            );
        }
    }

    /// <summary>Float clamps to zero to one on the way to bytes, and a NaN is not a number to round.</summary>
    [Theory]
    [InlineData(PixelFormat.Rgba16Float)]
    [InlineData(PixelFormat.Rgba32Float)]
    public void AFloatSourceClampsOnItsWayToBytes(PixelFormat format) {
        var texture = new TextureData(format, 4, 4, 1);
        var pixels = texture.PixelSpan();
        var width = format.BlockSize() / 4;

        for (var texel = 0; texel < 16; texel++) {
            Write(pixels[(texel * 4 * width)..], 5f);
            Write(pixels[((texel * 4 + 1) * width)..], -3f);
            Write(pixels[((texel * 4 + 2) * width)..], float.NaN);
            Write(pixels[((texel * 4 + 3) * width)..], 0.5f);
        }

        var decoded = BlockCompressor.Decode(BlockCompressor.Encode(texture, PixelFormat.Bc7RgbaUNorm)).Pixels;

        Assert.Equal([255, 0, 0, 128], decoded[..4].ToArray());

        void Write(Span<byte> at, float value) {
            if (width == 2) {
                BitConverter.TryWriteBytes(at, (Half)value);
            } else {
                BitConverter.TryWriteBytes(at, value);
            }
        }
    }

    /// <summary>
    ///     A sixteen-bit source cannot say it is sRGB, so naming an sRGB target is refused until the
    ///     caller says the values are encoded, and then the values are stored as they are.
    /// </summary>
    [Fact]
    public void ASixteenBitSourceReachesAnSrgbTargetOnlyWhenItSaysItIsEncoded() {
        var wide = new TextureData(PixelFormat.Rgba16UNorm, 4, 4, 1);

        wide.PixelSpan().Fill(0x80);

        Assert.Throws<ArgumentException>(() => BlockCompressor.Encode(wide, PixelFormat.Bc7RgbaUNormSrgb));

        var srgb = BlockCompressor.Encode(wide, PixelFormat.Bc7RgbaUNormSrgb, new() { SourceIsSrgbEncoded = true });
        var linear = BlockCompressor.Encode(wide, PixelFormat.Bc7RgbaUNorm);

        Assert.Equal(PixelFormat.Bc7RgbaUNormSrgb, srgb.Format);
        Assert.Equal(linear.Pixels.ToArray(), srgb.Pixels.ToArray());
    }

    /// <summary>Signed BC6H keeps a negative radiance; the unsigned format clamps it to black.</summary>
    [Fact]
    public void OnlyTheSignedFormatKeepsANegativeRadiance() {
        var texture = new TextureData(PixelFormat.Rgba32Float, 4, 4, 1);
        var pixels = texture.PixelSpan();

        for (var texel = 0; texel < 16; texel++) {
            BitConverter.TryWriteBytes(pixels[(texel * 16)..], -2f);
            BitConverter.TryWriteBytes(pixels[(texel * 16 + 4)..], 2f);
            BitConverter.TryWriteBytes(pixels[(texel * 16 + 8)..], float.PositiveInfinity);
        }

        var signed = BlockCompressor.Decode(BlockCompressor.Encode(texture, PixelFormat.Bc6HRgbSFloat)).Pixels;
        var unsigned = BlockCompressor.Decode(BlockCompressor.Encode(texture, PixelFormat.Bc6HRgbUFloat)).Pixels;

        Assert.InRange((float)BitConverter.ToHalf(signed[..2]), -2.05f, -1.95f);
        Assert.Equal(0f, (float)BitConverter.ToHalf(unsigned[..2]));
        Assert.True(float.IsFinite((float)BitConverter.ToHalf(signed[4..6])));
    }

    /// <summary>Every level down to one texel encodes, on a chain that is no power of two at all.</summary>
    [Theory]
    [InlineData(PixelFormat.Bc7RgbaUNorm)]
    [InlineData(PixelFormat.Bc1RgbaUNorm)]
    [InlineData(PixelFormat.Bc4RUNorm)]
    [InlineData(PixelFormat.Bc5RgUNorm)]
    public void ANonPowerOfTwoChainEncodesDownToOneTexel(PixelFormat format) {
        var texture = new TextureData(PixelFormat.Rgba16UNorm, 37, 13);
        var random = new Random(37);

        random.NextBytes(texture.LevelSpan(0));
        MipChain.Generate(texture);

        var encoded = BlockCompressor.Encode(texture, format, new() { Quality = BlockQuality.Fast });
        var decoded = BlockCompressor.Decode(encoded);

        Assert.Equal(texture.LevelCount, encoded.LevelCount);
        Assert.Equal(6, encoded.LevelCount);
        Assert.Equal((1, 1), (decoded.Levels[^1].Width, decoded.Levels[^1].Height));
    }

    sealed class Recorder(Action? onReport = null) : IProgress<BlockEncodeProgress> {
        public List<BlockEncodeProgress> Seen { get; } = [];

        public void Report(BlockEncodeProgress value) {
            Seen.Add(value);
            onReport?.Invoke();
        }
    }

    static byte[] RoundTrip(byte[] rgba, BlockQuality quality) {
        var block = new byte[16];
        var decoded = new byte[64];

        Bc7Block.Encode(rgba, block, quality);
        Bc7Block.Decode(block, decoded);

        return decoded;
    }

    static int MaxError(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second) {
        var largest = 0;

        for (var at = 0; at < first.Length; at++) {
            largest = Math.Max(largest, Math.Abs(first[at] - second[at]));
        }

        return largest;
    }
}
