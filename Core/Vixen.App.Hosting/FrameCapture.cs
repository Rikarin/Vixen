// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Graphics;

namespace Vixen.App;

/// <summary>Reading the frame's final colour target back and writing it as a PNG.</summary>
/// <remarks>
///     <para>
///         <b>What <c>--vixen-capture</c> is.</b> The frame the host lends under
///         <see cref="GraphicsOptions.Output" /> is an ordinary texture whenever there is no
///         swapchain image behind it, so copying it into a host-visible buffer and encoding the
///         result is the whole mechanism — the same one
///         <c>Vixen.Graphics.Golden.Tests</c> has used for a hundred and seventy-odd fixtures.
///     </para>
///     <para>
///         ⚠ <b>Two halves, on opposite sides of the submit, and they cannot be one.</b>
///         <see cref="Record" /> goes on the frame's own command list, because a copy recorded after
///         <c>Finish</c> is a copy on no list at all; <see cref="Write" /> runs after the queue has
///         gone idle, because <c>IGraphicsDevice.Read</c> is immediate and a recorded copy executes
///         at submit. A readback that raced its own frame would not throw — it would write a black
///         or half-drawn PNG and report success, which is the failure this class is most easily
///         written with.
///     </para>
///     <para>
///         ⚠ <b>The wait is <c>WaitIdle</c>, and it is a hammer on purpose.</b> Waiting per frame is
///         exactly what the queue's own documentation says not to do — and this happens once, on the
///         frame the process is about to stop after, where the alternative is a fence the host does
///         not otherwise own.
///     </para>
/// </remarks>
sealed class FrameCapture(IGraphicsDevice device, string directory) {
    BufferHandle readback;
    int width;
    int height;
    PixelFormat format;

    /// <summary>Where the last picture was written, or <see langword="null" /> if none was.</summary>
    public string? LastPath { get; private set; }

    /// <summary>Records the copy out of the frame's target onto the frame's own list.</summary>
    /// <param name="commands">The list the frame was recorded on, before it is finished.</param>
    /// <param name="texture">The target the frame's last pass wrote.</param>
    /// <param name="size">Its size in pixels.</param>
    /// <param name="pixelFormat">What its texels mean.</param>
    /// <remarks>
    ///     No barrier here, and that is not an oversight. The graph transitions an imported resource
    ///     to the exit state it was imported with, and the host imports the frame's target as
    ///     <see cref="ResourceState.CopySource" /> when there is nothing to present to — so by the
    ///     time this records, the texture is already in the layout a copy reads from. Adding one
    ///     would be a barrier from a state to itself.
    /// </remarks>
    public void Record(ICommandList commands, TextureHandle texture, Core.Mathematics.Int2 size, PixelFormat pixelFormat) {
        ArgumentNullException.ThrowIfNull(commands);

        if (!texture.IsValid || size.X <= 0 || size.Y <= 0) {
            return;
        }

        width = size.X;
        height = size.Y;
        format = pixelFormat;

        readback = device.CreateBuffer(
            new(width * height * 4, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "frame capture")
        );

        commands.CopyTextureToBuffer(new(texture), new(width, height, 1), readback, 0);
    }

    /// <summary>Waits for the frame, reads the pixels and writes the file.</summary>
    /// <param name="name">The file's name without an extension.</param>
    /// <returns>Where it was written, or <see langword="null" /> if there was nothing to write.</returns>
    public string? Write(string name) {
        if (!readback.IsValid) {
            return null;
        }

        try {
            device.WaitIdle();

            var pixels = new byte[width * height * 4];
            device.Read(readback, 0, pixels);

            // ⚠ The swapchain's format is BGRA by default and a PNG is RGBA, so the channels are
            // swapped here rather than by asking for a different target format. Capturing the
            // format the frame actually renders into is the point: a capture that quietly changed
            // it would be a picture of a frame nobody runs, and the sRGB encoding — which is where a
            // "washed out" report usually lives — would move with it.
            if (format is PixelFormat.Bgra8UNorm or PixelFormat.Bgra8UNormSrgb) {
                for (var offset = 0; offset < pixels.Length; offset += 4) {
                    (pixels[offset], pixels[offset + 2]) = (pixels[offset + 2], pixels[offset]);
                }
            }

            // Opaque. A frame's alpha is whatever its last pass left there — the tonemap and the
            // vignette have no reason to write one — and a PNG viewer that honours it shows the
            // picture over its own checkerboard, which reads as a rendering bug rather than as an
            // unwritten channel.
            for (var offset = 3; offset < pixels.Length; offset += 4) {
                pixels[offset] = 255;
            }

            var path = Path.Combine(directory, $"{name}.png");
            PngCodec.Save(path, new(width, height, pixels));
            LastPath = path;

            return path;
        } finally {
            device.Destroy(readback);
            readback = BufferHandle.Null;
        }
    }
}
