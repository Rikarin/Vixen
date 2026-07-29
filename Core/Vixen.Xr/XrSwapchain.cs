// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;

namespace Vixen.Xr;

/// <summary>What to create an eye buffer as.</summary>
/// <param name="Size">Its size in pixels, per view.</param>
/// <param name="Format">
///     The format to ask for. A runtime supports a list of them and will refuse anything else, so
///     read <see cref="IXrSwapchain.Format" /> back rather than assuming this was granted.
/// </param>
/// <param name="ArrayLayers">
///     How many layers. Two is the multiview case: both eyes in one array texture, rendered in one
///     pass, which is where the halving of draw-call cost comes from.
/// </param>
/// <param name="SampleCount">How many samples per pixel.</param>
/// <param name="Usage">What the images will be used for.</param>
/// <param name="Name">A name for the debugger and captures.</param>
public readonly record struct XrSwapchainDescription(
    Int2 Size,
    PixelFormat Format = PixelFormat.Rgba8UNormSrgb,
    int ArrayLayers = 1,
    int SampleCount = 1,
    TextureUsage Usage = TextureUsage.ColourTarget | TextureUsage.Sampled,
    string Name = ""
);

/// <summary>The images an eye is rendered into.</summary>
/// <remarks>
///     <para>
///         <b>The runtime owns these, and that is the whole difference from
///         <see cref="ISwapChain" />.</b> A window's swapchain is created by the engine on a surface
///         the window system provided; an XR swapchain's images are allocated by the OpenXR runtime,
///         possibly in memory the compositor can reproject without a copy, and handed back as native
///         handles the RHI has to be told to adopt rather than create. Everything else about the
///         cycle — acquire, render, release — looks the same on purpose.
///     </para>
///     <para>
///         <b>There is no <c>Present</c>.</b> Releasing an image says the rendering is done; what
///         actually shows it is <see cref="IXrSession.EndFrame" />, which submits every layer of the
///         frame at once. A game that released an image and never submitted a layer referencing it
///         would render correctly and display nothing, which is the second thing to check when an
///         eye buffer is black.
///     </para>
/// </remarks>
public interface IXrSwapchain : IDisposable {
    /// <summary>The size of one view's image, in pixels.</summary>
    Int2 Size { get; }

    /// <summary>The format the runtime actually granted.</summary>
    PixelFormat Format { get; }

    /// <summary>How many array layers each image has.</summary>
    int ArrayLayers { get; }

    /// <summary>How many images it cycles through. The runtime decides.</summary>
    int ImageCount { get; }

    /// <summary>The index acquired, or <c>-1</c> when none is.</summary>
    int AcquiredIndex { get; }

    /// <summary>One of the images, as a texture the RHI can render into.</summary>
    /// <param name="index">Which image.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such image.</exception>
    TextureHandle Image(int index);

    /// <summary>A view of one of the images.</summary>
    /// <param name="index">Which image.</param>
    /// <returns>The view, covering every array layer.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such image.</exception>
    TextureViewHandle View(int index);

    /// <summary>Takes the next image and waits until the compositor has finished with it.</summary>
    /// <returns>Its index.</returns>
    /// <exception cref="InvalidOperationException">One is already acquired.</exception>
    /// <remarks>
    ///     The wait is not optional and not separable in practice: a runtime hands out an image
    ///     before it is safe to write, and rendering into one that the compositor is still reading is
    ///     tearing that only appears on the headset.
    /// </remarks>
    int AcquireImage();

    /// <summary>Says the acquired image has been rendered.</summary>
    /// <exception cref="InvalidOperationException">Nothing is acquired.</exception>
    void ReleaseImage();
}
