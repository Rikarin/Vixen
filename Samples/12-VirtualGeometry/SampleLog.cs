// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Graphics;

namespace Vixen.Samples.VirtualGeometry;

/// <summary>What the sample logs, with the ids from <c>docs/manual/log-events.md</c>.</summary>
/// <remarks>
///     Its own ids rather than a sibling's, even where a message reads the same — an id is only
///     useful in a support log if it names exactly one call site.
/// </remarks>
static partial class SampleLog {
    [LoggerMessage(
        EventId = 14021,
        Level = LogLevel.Information,
        Message = "Built {Triangles} triangles into {Clusters} clusters over {Pages} page(s) on "
            + "{Adapter} ({Kind}). The host never learns how many are drawn."
    )]
    public static partial void MeshBuilt(
        ILogger logger,
        int triangles,
        int clusters,
        int pages,
        string adapter,
        AdapterKind kind
    );

    [LoggerMessage(
        EventId = 14022,
        Level = LogLevel.Error,
        Message = "There is no window to present to. This sample needs a real display."
    )]
    public static partial void NoWindow(ILogger logger);

    [LoggerMessage(
        EventId = 14023,
        Level = LogLevel.Error,
        Message = "The device was lost. Recreating it is Phase 2's job; stopping."
    )]
    public static partial void DeviceLost(ILogger logger);

    [LoggerMessage(
        EventId = 14024,
        Level = LogLevel.Information,
        Message = "The swapchain was out of date and has been rebuilt at {Width}×{Height}."
    )]
    public static partial void SwapChainRebuilt(ILogger logger, int width, int height);

    [LoggerMessage(
        EventId = 14025,
        Level = LogLevel.Information,
        Message = "The last frame's traversal accepted {Visible} of {Clusters} clusters, {Software} of "
            + "them through the software raster, with {Resident} page(s) resident. Zero visible after a "
            + "real run is a frame that drew nothing."
    )]
    public static partial void TraversalSettled(
        ILogger logger,
        int visible,
        int clusters,
        int software,
        int resident
    );
}
