// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.App;
using Vixen.Platform.Desktop;

namespace Vixen.Samples.PbrShowcase;

/// <summary>The desktop way in to the material showcase.</summary>
/// <remarks>
///     <para>
///         Desktop only, unlike <c>Samples/01</c>, and that is a statement about what the sample is
///         for rather than a gap. 01 exists to prove the platform layer works on all six targets and
///         is therefore three projects over one source. This one exists to show what the shading
///         model looks like, which is a thing you look at on a screen you can see.
///     </para>
///     <para>
///         <c>--vixen-frames N</c> still applies, so CI can prove the whole stack starts, renders two
///         passes and stops without a validation error or a hang — which is what makes this a
///         buildable check rather than only a demo.
///     </para>
/// </remarks>
static class Program {
    static int Main(string[] arguments) {
        // The VULKAN window flag has to be asked for at creation time: a window made without it has
        // no surface to present to, and the failure arrives several frames later as "no surface".
        var platform = new DesktopPlatform(new() {
            Organisation = "Vixen",
            Application = "PbrShowcase",
            RequestGpuSurface = true
        });

        using var application = VixenApp.Create(arguments)
            .WithPlatform(platform)
            .Build(new PbrShowcaseGame());

        return application.Run();
    }
}
