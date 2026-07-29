// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.App;
using Vixen.Platform.Desktop;

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
    static int Main(string[] arguments) {
        // As in 01: a Vulkan surface has to be asked for before the window exists, because SDL needs
        // the flag at creation time and a window made without it has nothing to present to.
        var platform = new DesktopPlatform(new() {
            Organisation = "Vixen",
            Application = "VideoPlayback",
            RequestGpuSurface = true
        });

        using var application = VixenApp.Create(arguments)
            .WithPlatform(platform)
            .Build(new VideoGame());

        return application.Run();
    }
}
