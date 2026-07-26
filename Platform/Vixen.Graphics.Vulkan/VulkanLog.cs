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
}
