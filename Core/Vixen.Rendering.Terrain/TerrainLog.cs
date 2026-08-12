// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace Vixen.Rendering.Terrain;

/// <summary>What the ground says out loud, with the ids from docs/manual/log-events.md.</summary>
/// <remarks>
///     <para>
///         <b>A renderer that quietly draws something else is worse than one that refuses.</b> The
///         terrain stack has two shading paths and picks between them from what the frame provides —
///         which is the right design, because an editor pane has to draw ground before a game exists.
///         What it did not have was any way to tell the two apart from outside: the preview fragment
///         returns a reflectance in [0, 1] and the frame is metered in cd/m², so under a daylight sky
///         the ground is about one nit in a picture exposed for thousands. That draws as black
///         ground under a correct sky, at every viewpoint and every hour, with
///         <c>TerrainSceneRenderer.TerrainsDrawn</c> reporting the terrain drawn — because it was.
///     </para>
///     <para>
///         <b>Nothing here is called per frame.</b> The node latches what it has said and says it
///         again only after the input comes back and goes away a second time, so a game that never
///         provides a camera pays one line for its whole run.
///     </para>
///     <para>
///         The 4 000 range is <c>Vixen.Rendering</c>'s and this assembly is inside it — see the range
///         table, which owns a prefix rather than one assembly name.
///     </para>
/// </remarks>
static partial class TerrainLog {
    [LoggerMessage(
        EventId = 4003,
        Level = LogLevel.Warning,
        Message = "'{Node}' is drawing the ground with the preview shaders because {Missing}. The "
            + "preview fragment returns a reflectance in [0, 1] rather than a luminance in cd/m², so "
            + "under a physically metered sky the ground is roughly one nit in a frame exposed for "
            + "thousands — it will look black rather than look missing, and every draw counter will "
            + "say it was drawn."
    )]
    public static partial void GroundIsPreviewShaded(ILogger logger, string node, string missing);
}
