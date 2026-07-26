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
}
