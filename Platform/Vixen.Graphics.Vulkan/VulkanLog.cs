// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace Vixen.Graphics.Vulkan;

/// <summary>What the Vulkan backend logs, with the ids from docs/manual/log-events.md.</summary>
static partial class VulkanLog {
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Warning,
        Message = "The Vulkan validation layers were asked for and are not installed. Install the Vulkan "
            + "SDK; without them a backend's first mistake goes unnoticed."
    )]
    public static partial void ValidationLayersMissing(ILogger logger);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Warning,
        Message = "The Vulkan validation layer was found but would not load, so the instance was created "
            + "without it. {Hint}"
    )]
    public static partial void ValidationLayerWouldNotLoad(ILogger logger, string hint);

    /// <summary>
    ///     The one line worth having in every bug report.
    /// </summary>
    /// <remarks>
    ///     Which GPU, which driver version, which render path, and whether validation was on: four
    ///     facts that between them explain most "it works here" reports, and none of which anyone
    ///     thinks to ask for until they are already needed.
    /// </remarks>
    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Information,
        Message = "Vulkan device created on '{Adapter}' ({Kind}, Vulkan {ApiVersion}) using {RenderPath}; "
            + "validation {ValidationEnabled}."
    )]
    public static partial void DeviceCreated(
        ILogger logger,
        string adapter,
        string kind,
        string apiVersion,
        string renderPath,
        bool validationEnabled
    );

    /// <summary>
    ///     What the layers said over the life of a device, written where a gate can read it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The counts are useless without <c>ValidationActive</c> beside them, and that is
    ///         the whole reason this is one record with three properties rather than an error count
    ///         somebody greps for.</b> A validation layer that is not installed, or is installed and
    ///         will not load, does not stop the instance being created — <c>VulkanInstance.TryCreate</c>
    ///         retries without it and logs a warning — so a run with no layers at all reports zero
    ///         errors, which is exactly what a clean run reports. A gate reading only the count would
    ///         be green on the day the instrument was absent.
    ///     </para>
    ///     <para>
    ///         Information rather than Error even when the count is not zero: what the errors *were*
    ///         is on the console, this is the summary, and a run whose device is torn down cleanly
    ///         has not itself failed. The assertion belongs to whoever is reading — the
    ///         <c>SampleFrame</c> target, which fails on a non-zero count.
    ///     </para>
    /// </remarks>
    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Information,
        Message = "Vulkan validation was {ValidationActive} and reported {ValidationErrors} error(s) "
            + "and {ValidationWarnings} warning(s) over the life of this device."
    )]
    public static partial void ValidationSummary(
        ILogger logger,
        bool validationActive,
        int validationErrors,
        int validationWarnings
    );
}
