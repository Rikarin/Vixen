// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.App;
using Vixen.Platform.Desktop;

namespace Vixen.Samples.VirtualGeometry;

/// <summary>The desktop way in to the virtualized-geometry frame.</summary>
/// <remarks>
///     <para>
///         Desktop only, like <c>Samples/03</c> and for the same reason: this sample exists to show a
///         thing on a screen — the traversal's cut, recolouring itself as the camera moves — and the
///         multi-platform proof lives in <c>Samples/01</c>.
///     </para>
///     <para>
///         <c>--vixen-frames N</c> still applies, so CI can prove the whole stack — window, device,
///         document, traversal, indirect draw, present — starts, runs and stops without a validation
///         error or a hang.
///     </para>
/// </remarks>
static class Program {
    static int Main(string[] arguments) {
        // The VULKAN window flag has to be asked for at creation time: a window made without it has
        // no surface to present to, and the failure arrives several frames later as "no surface".
        var platform = new DesktopPlatform(new() {
            Organisation = "Vixen",
            Application = "VirtualGeometry",
            RequestGpuSurface = true
        });

        using var application = VixenApp.Create(arguments)
            .WithPlatform(platform)
            .Build(new VirtualGeometryGame());

        return application.Run();
    }
}
