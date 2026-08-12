// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace Vixen.Rendering.Diagnostics;

/// <summary>What the render system logs, with the ids from docs/manual/log-events.md.</summary>
/// <remarks>
///     <b>Nothing here is called per frame.</b> Every line below describes a frame that drew and
///     quietly drew less than it was asked for — the class of wrongness no exception ever reaches, and
///     therefore the class that has to be a log line rather than a counter nobody reads. What keeps
///     that affordable is that the counters are compared and the call site is reached only when one
///     has moved, and then at most once every few seconds. See <see cref="PageResidency" />'s own
///     reporting, which is two longs and a compare when the frame is healthy.
/// </remarks>
static partial class RenderingLog {
    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Warning,
        Message = "The page pool refused {Refusals} request(s): {Resident} of {Capacity} page(s) are "
            + "resident and {Pinned} of those are pinned, so there was nothing left to evict. The "
            + "frame drew something coarser than it asked for; raise the pool's slot count if it "
            + "keeps happening."
    )]
    public static partial void PagesRefused(ILogger logger, long refusals, int resident, int capacity, int pinned);

    /// <summary>
    ///     Error rather than warning, and the difference is permanence. A refused request is a coarser
    ///     frame and the next frame asks again; a refused <em>pin</em> is a page something is relying
    ///     on being resident, and the only thing that pins is a registration that has already happened
    ///     and will not happen twice.
    /// </summary>
    [LoggerMessage(
        EventId = 4002,
        Level = LogLevel.Error,
        Message = "{Refusals} pinned page(s) could not be given a slot: {Pinned} of {Capacity} page(s) "
            + "are pinned already. A pinned page is never evicted, so whatever those pages belong to "
            + "will draw nothing until the pool is bigger. The request is still queued."
    )]
    public static partial void PinnedPageRefused(ILogger logger, long refusals, int pinned, int capacity);

    /// <summary>
    ///     The froxel grid was never told which camera it is for, so the shader used its own defaults.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Once per degrade, not once per pass and never per frame.</b>
    ///         <see cref="Lighting.SceneLighting.Extract" /> runs for every shading pass of every
    ///         frame; the first pass that notices says this and the rest are silent until a camera
    ///         arrives and goes away again.
    ///     </para>
    ///     <para>
    ///         ⚠ The consequence is <em>not</em> zeros. <c>ClusteredShading.rvn</c> declares
    ///         <c>tanHalfFov = float2(1, 0.5625)</c>, <c>nearPlane = 0.1</c> and
    ///         <c>farPlane = 1000</c>, which is exactly a 16:9 camera at a ninety-degree horizontal
    ///         field of view — a plausible grid for a camera nobody has, which is why nothing about
    ///         the picture says the numbers are missing.
    ///     </para>
    /// </remarks>
    [LoggerMessage(
        EventId = 4004,
        Level = LogLevel.Warning,
        Message = "SceneLighting.Camera is null, so nothing wrote the froxel grid's half-tangents or "
            + "planes for pass '{Pass}'. A clustered frame still shades: it looks every fragment up "
            + "in the grid with the shader's declared defaults — a 16:9 camera at 90° horizontal "
            + "with planes at 0.1 and 1000 — so lights land in the wrong froxel rather than nowhere, "
            + "which draws as flicker near the screen edges rather than as an error. Set it beside "
            + "the frame's view camera."
    )]
    public static partial void LightingCameraMissing(ILogger logger, string pass);
}
