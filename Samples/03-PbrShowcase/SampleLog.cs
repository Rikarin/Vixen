// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Graphics;

namespace Vixen.Samples.PbrShowcase;

/// <summary>What the sample logs, with the ids from <c>docs/manual/log-events.md</c>.</summary>
/// <remarks>
///     Its own ids rather than 01's, even though two of the messages read the same. A shared id would
///     make the register ambiguous the first time somebody greps for one in a support log, and the
///     register is only useful if an id names exactly one call site.
/// </remarks>
static partial class SampleLog {
    [LoggerMessage(
        EventId = 14011,
        Level = LogLevel.Information,
        Message = "Showing {Rows}×{Columns} materials on {Adapter} ({Kind}), rendering HDR at "
            + "{Width}×{Height} and presenting {Format}."
    )]
    public static partial void SceneReady(
        ILogger logger,
        int rows,
        int columns,
        string adapter,
        AdapterKind kind,
        int width,
        int height,
        PixelFormat format
    );

    [LoggerMessage(
        EventId = 14012,
        Level = LogLevel.Error,
        Message = "There is no window to present to. This sample needs a real display."
    )]
    public static partial void NoWindow(ILogger logger);

    [LoggerMessage(
        EventId = 14013,
        Level = LogLevel.Error,
        Message = "The device was lost. Recreating it is Phase 2's job; stopping."
    )]
    public static partial void DeviceLost(ILogger logger);

    [LoggerMessage(
        EventId = 14014,
        Level = LogLevel.Information,
        Message = "The swapchain was out of date and has been rebuilt at {Width}×{Height}."
    )]
    public static partial void SwapChainRebuilt(ILogger logger, int width, int height);
}
