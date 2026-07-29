// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Video.Tests;

/// <summary>The pictures these tests decode, stated rather than generated from the code under test.</summary>
static class VideoTestContent {
    /// <summary>A solid-colour 4:2:0 frame, planes in I420 order.</summary>
    public static byte[] I420(int width, int height, byte luma, byte blue = 128, byte red = 128) {
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;
        var bytes = new byte[(width * height) + (2 * chromaWidth * chromaHeight)];

        bytes.AsSpan(0, width * height).Fill(luma);
        bytes.AsSpan(width * height, chromaWidth * chromaHeight).Fill(blue);
        bytes.AsSpan((width * height) + (chromaWidth * chromaHeight)).Fill(red);

        return bytes;
    }

    /// <summary>The same picture with the two chroma planes the other way round, which is YV12.</summary>
    public static byte[] Yv12(int width, int height, byte luma, byte blue, byte red) =>
        I420(width, height, luma, red, blue);

    /// <summary>A file of solid frames, one per cluster, at a stated tick spacing.</summary>
    public static WebMBuilder Video(
        int width,
        int height,
        int frames,
        long ticksPerFrame = 40,
        bool cues = false
    ) {
        var builder = new WebMBuilder()
            .VideoTrack(1, width, height, defaultDurationNanoseconds: ticksPerFrame * 1_000_000)
            .Duration(frames * ticksPerFrame);

        if (cues) {
            builder.Cues();
        }

        for (var index = 0; index < frames; index++) {
            builder
                .Cluster(index * ticksPerFrame)
                .SimpleBlock(1, 0, keyFrame: true, I420(width, height, (byte)(16 + index)));
        }

        return builder;
    }

    /// <summary>Interleaved 32-bit float PCM, one sample per frame per channel.</summary>
    public static byte[] FloatPcm(int frames, int channels, float value) {
        var bytes = new byte[frames * channels * 4];

        for (var index = 0; index < frames * channels; index++) {
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * 4), value);
        }

        return bytes;
    }
}
