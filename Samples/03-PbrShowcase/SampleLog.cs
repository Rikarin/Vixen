// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace Vixen.Samples.PbrShowcase;

/// <summary>What the sample logs, with the ids from <c>docs/manual/log-events.md</c>.</summary>
/// <remarks>
///     Its own ids rather than 13's, even though three of the messages rhyme with that sample's. A
///     shared id would make the register ambiguous the first time somebody greps for one in a
///     support log, and the register is only useful if an id names exactly one call site.
/// </remarks>
static partial class SampleLog {
    [LoggerMessage(
        EventId = 14011,
        Level = LogLevel.Information,
        Message = "Showing {Rows}×{Columns} materials through the standard frame, with {Instances} "
            + "distance-field instance(s) behind the occlusion march."
    )]
    public static partial void SceneReady(ILogger logger, int rows, int columns, int instances);

    [LoggerMessage(
        EventId = 14012,
        Level = LogLevel.Warning,
        Message = "No Raven/Library above the binary and no baked shaders, so every material will resolve to a "
            + "miss and the screen will be black. This is a development build run from outside the repository."
    )]
    public static partial void NoShaderLibrary(ILogger logger);

    [LoggerMessage(
        EventId = 14013,
        Level = LogLevel.Error,
        Message = "The grid's material would not compile, so every sphere will draw with nothing: {Diagnostics}"
    )]
    public static partial void NoMaterial(ILogger logger, string diagnostics);

    [LoggerMessage(
        EventId = 14014,
        Level = LogLevel.Information,
        Message = "Rebuilt '{Address}' with the distance field in it. The first build ran before OnInitialise "
            + "and the clipmap node captured a null."
    )]
    public static partial void FrameRebuilt(ILogger logger, string address);

    [LoggerMessage(
        EventId = 14015,
        Level = LogLevel.Information,
        Message = "Stopping after {Frames} frame(s): {Objects} object(s) extracted, {Variants} shader "
            + "variant(s) compiled."
    )]
    public static partial void FrameReport(ILogger logger, long frames, int objects, int variants);

    [LoggerMessage(
        EventId = 14016,
        Level = LogLevel.Information,
        Message = "The ground: {Terrains} terrain(s) and {Fields} grass field(s) drawn in the last frame; "
            + "extraction saw {Extracted} terrain(s), {Waiting} still loading, {Refused} refused grass rule(s)."
    )]
    public static partial void GroundReport(
        ILogger logger,
        int terrains,
        int fields,
        int extracted,
        int waiting,
        int refused
    );

    [LoggerMessage(
        EventId = 14017,
        Level = LogLevel.Information,
        Message = "The pines: {Volumes} foliage volume(s) drawn in the last frame, {Missing} mesh(es) still "
            + "missing; extraction saw {Extracted} volume(s), {Refused} refused."
    )]
    public static partial void FoliageReport(ILogger logger, int volumes, int missing, int extracted, int refused);
}
