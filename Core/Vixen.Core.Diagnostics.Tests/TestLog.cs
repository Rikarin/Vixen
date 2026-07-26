// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace Vixen.Core.Diagnostics.Tests;

/// <summary>
///     The logging pattern ADR-008 mandates, used here so the sink is exercised the way the engine
///     will actually call it rather than through the convenience extensions.
/// </summary>
/// <remarks>
///     Worth doing in the tests and not only in production code: the generated method checks
///     <c>IsEnabled</c> and returns before touching its arguments, which is the property that makes
///     leaving log statements in hot-ish code affordable, and a test that called
///     <c>logger.LogWarning(…)</c> instead would not be testing the same path.
/// </remarks>
static partial class TestLog {
    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "Device lost after {Ms} ms")]
    public static partial void DeviceLost(ILogger logger, int ms);

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "debug")]
    public static partial void Debug(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "line {Index}")]
    public static partial void Line(ILogger logger, int index);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "warning")]
    public static partial void Warning(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "error")]
    public static partial void Error(ILogger logger);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Something failed")]
    public static partial void Failed(ILogger logger, Exception exception);
}
