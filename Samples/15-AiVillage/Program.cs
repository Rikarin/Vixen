// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.App;
using Vixen.Platform.Desktop;

namespace Vixen.Samples.AiVillage;

/// <summary>The desktop way in to the village.</summary>
/// <remarks>
///     <para>
///         Everything worth looking at here is drawn by <c>AiOverlaySystem</c> into the frame's
///         <c>DebugDraw</c>, so the sample needs a device and a surface like any other — and
///         <c>--vixen-frames N --vixen-capture &lt;dir&gt;</c> writes the last one to a PNG.
///     </para>
///     <para>
///         ⚠ <b>The decision log is the evidence, and it does not need a picture.</b> A run with
///         <c>--vixen-headless</c> and no capture path falls through to a device that draws nothing,
///         and every line of <c>SampleLog.Decided</c> is still produced — because what the sample is
///         asserting happened in <c>SystemPhase.Update</c>, several phases before anything drew.
///     </para>
/// </remarks>
static class Program {
    static int Main(string[] arguments) {
        var platform = new DesktopPlatform(new() {
            Organisation = "Vixen",
            Application = "AiVillage",
            RequestGpuSurface = true
        });

        using var application = VixenApp.Create(arguments)
            .WithPlatform(platform)
            .Build(new AiVillageGame());

        return application.Run();
    }
}
