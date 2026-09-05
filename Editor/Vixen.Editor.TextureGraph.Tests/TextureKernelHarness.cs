// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Tests;

/// <summary>
///     The device, the uploads and the test images the § 4.2 and § 4.3 kernel suites share.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Everything here opens the device the same way <c>TexturePlanDeviceTests</c> does, and
///         for the same reason.</b> Without a real adapter a headless run falls back to the Null
///         device on every platform, exits 0 and prints identical healthy counters — so a texture
///         kernel test that passed there would have proved that a black image equals a black image.
///         <see cref="Open" /> names the adapter into every failure and skips loudly;
///         <c>VIXEN_REQUIRE_VULKAN=1</c> turns the skip into a failure.
///     </para>
///     <para>
///         <b>The patterns are chosen so that an assertion can be an equality.</b>
///         <see cref="Unique" /> gives every texel a different colour, so "this is a copy" is a claim
///         about 4 096 texels rather than about a flat fill that any broken kernel also produces;
///         <see cref="Columns" /> is a one-texel checkerboard whose <em>mean is exactly one half</em>,
///         which is the closed form every minification in § 4.3 is measured against.
///     </para>
/// </remarks>
static class TextureKernelHarness {
    /// <summary>The side of every test image, in texels.</summary>
    public const int Side = 64;

    /// <summary>A device, or a loud skip — or, when one was required, a failure.</summary>
    public static VulkanDevice Open() {
        if (VulkanDevice.TryCreate(new(), out var device, out var reason)) {
            return device!;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan device, so nothing here can be proved");

        throw new InvalidOperationException("unreachable");
    }

    /// <summary>What ran, said in every message so a number is never anonymous.</summary>
    public static string Adapter(VulkanDevice device) =>
        $"{device.Adapter.Name} ({device.Adapter.Kind}, {device.Adapter.DriverVersion})";

    /// <summary>Uploads RGBA8 texels as a texture a plan can read.</summary>
    public static (TextureHandle Texture, BufferHandle Staging) Upload(
        VulkanDevice device,
        byte[] pixels,
        int width,
        int height
    ) {
        var texture = device.CreateTexture(
            new(
                PixelFormat.Rgba8UNorm,
                width,
                height,
                TextureUsage.Sampled | TextureUsage.CopyDestination,
                Name: "kernel test source"
            )
        );

        var staging = device.CreateBuffer(
            new(pixels.Length, BufferUsage.CopySource, MemoryAccess.HostUpload, "kernel test staging")
        );

        device.Write(staging, 0, pixels);
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "upload")) {
            commands.Barrier(
                new BarrierGroup(
                    [],
                    [new TextureBarrier(texture, ResourceState.Undefined, ResourceState.CopyDestination)]
                )
            );

            commands.CopyBufferToTexture(staging, 0, new(texture), new(width, height, 1));

            commands.Barrier(
                new BarrierGroup(
                    [],
                    [new TextureBarrier(texture, ResourceState.CopyDestination, ResourceState.ShaderRead)]
                )
            );

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        return (texture, staging);
    }

    /// <summary>One channel of one texel of a read-back picture.</summary>
    public static byte At(Bitmap picture, int x, int y, int channel) =>
        picture.Pixels[picture.Offset(x, y) + channel];

    /// <summary>Every texel a different colour, so "this is a copy" is a claim about all of them.</summary>
    /// <remarks>
    ///     ⚠ <b>A flat fill would let a kernel that writes a constant pass every copy assertion in
    ///     these suites.</b> Red rises across, green down, blue on the diagonal — 4 096 distinct
    ///     triples on a 64² image, and alpha is a fourth independent field so that a shuffle cannot
    ///     confuse it with one of them.
    /// </remarks>
    public static byte[] Unique(int side) {
        var pixels = new byte[side * side * 4];

        for (var y = 0; y < side; y++) {
            for (var x = 0; x < side; x++) {
                var at = ((y * side) + x) * 4;

                pixels[at] = (byte)(x * 4);
                pixels[at + 1] = (byte)(y * 4);
                pixels[at + 2] = (byte)((x + y) * 2);
                pixels[at + 3] = (byte)(255 - x * 2);
            }
        }

        return pixels;
    }

    /// <summary>A one-texel-wide column checkerboard, whose mean is exactly one half.</summary>
    /// <remarks>
    ///     The closed form every minification in § 4.3 is read off: area-averaged by any integer
    ///     factor this is 0.5 everywhere, and point-sampled it is 0 or 255 everywhere. Those two are
    ///     as far apart as a picture gets.
    /// </remarks>
    public static byte[] Columns(int side) {
        var pixels = new byte[side * side * 4];

        for (var y = 0; y < side; y++) {
            for (var x = 0; x < side; x++) {
                var at = ((y * side) + x) * 4;
                var value = (byte)(x % 2 == 0 ? 0 : 255);

                pixels[at] = value;
                pixels[at + 1] = value;
                pixels[at + 2] = value;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>A horizontal ramp from black to white.</summary>
    public static byte[] Ramp(int side) {
        var pixels = new byte[side * side * 4];

        for (var y = 0; y < side; y++) {
            for (var x = 0; x < side; x++) {
                var at = ((y * side) + x) * 4;
                var value = (byte)(x * 255 / (side - 1));

                pixels[at] = value;
                pixels[at + 1] = value;
                pixels[at + 2] = value;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>A flat fill of one colour.</summary>
    public static byte[] Solid(int side, byte r, byte g, byte b, byte a) {
        var pixels = new byte[side * side * 4];

        for (var texel = 0; texel < side * side; texel++) {
            pixels[texel * 4] = r;
            pixels[(texel * 4) + 1] = g;
            pixels[(texel * 4) + 2] = b;
            pixels[(texel * 4) + 3] = a;
        }

        return pixels;
    }

    /// <summary>Asserts two pictures are the same texel for texel, and says where they first differ.</summary>
    public static void AssertSame(Bitmap expected, Bitmap actual, int channels, string what) {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);

        for (var y = 0; y < expected.Height; y++) {
            for (var x = 0; x < expected.Width; x++) {
                for (var channel = 0; channel < channels; channel++) {
                    if (At(expected, x, y, channel) != At(actual, x, y, channel)) {
                        Assert.Fail(
                            $"{what}: at ({x}, {y}) channel {channel} the answer is "
                            + $"{At(actual, x, y, channel)} and the source is {At(expected, x, y, channel)}."
                        );
                    }
                }
            }
        }
    }
}
