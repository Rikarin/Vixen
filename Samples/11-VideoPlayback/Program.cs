// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.App;

namespace Vixen.Samples.VideoPlayback;

/// <summary>A video playing, which is the only way to see that a video plays.</summary>
/// <remarks>
///     <para>
///         <c>Vixen.Video</c>'s own suite asserts every part of the path and none of the whole of it:
///         a container reader can be right about a hundred blocks and still hand the GPU its planes
///         in the wrong order, and nothing but a picture says so. This is where the two halves meet —
///         the module produces three planes and six coefficients, and the sample's own shader is what
///         a renderer's material will eventually be.
///     </para>
///     <para>
///         <b>It carries no content.</b> The engine ships no codec, so a committed fixture would have
///         to be uncompressed, and an uncompressed three seconds is megabytes of binary for something
///         <see cref="GeneratedVideo" /> writes in a hundred lines. It also means the sample runs from
///         a clean clone with no asset pipeline, which is what <c>01-HelloTriangle</c> is careful
///         about too.
///     </para>
/// </remarks>
static class Program {
    // ⚠ As in 01, and for the reason 01 now spells out: this used to hand `WithPlatform` a
    // `DesktopPlatform` of its own, which `AppBuilder.Build` takes ahead of the factory and which
    // therefore made `--vixen-headless` a flag the run parsed and ignored. The Vulkan surface it was
    // built for is already the default — `DesktopPlatformOptions.RequestGpuSurface` is true and
    // `PlatformHost.Create` does not change it.
    static int Main(string[] arguments) => VixenApp.Run<VideoGame>(arguments);
}
