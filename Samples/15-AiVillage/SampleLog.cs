// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Ai.Diagnostics;
using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Samples.AiVillage;

/// <summary>What the sample logs, with the ids from <c>docs/manual/log-events.md</c>.</summary>
/// <remarks>
///     Its own ids rather than a sibling's, even where a message reads the same — an id is only
///     useful in a support log if it names exactly one call site.
/// </remarks>
static partial class SampleLog {
    [LoggerMessage(
        EventId = 14070,
        Level = LogLevel.Information,
        Message = "The village is up: {Agents} agents choosing from {Actions} registered actions, "
            + "over a {Seconds:0.0} s intrusion. One AiSystem, one registry, one blackboard layout, "
            + "one perception config, one sensor set and one navmesh."
    )]
    public static partial void VillageBuilt(ILogger logger, int agents, int actions, double seconds);

    /// <summary>⚠ The line that says the decision changed, and what the world was doing at the time.</summary>
    /// <remarks>
    ///     Structured rather than a pre-formatted string: a caller that builds the sentence itself
    ///     pays for it whether or not anything is listening, which is what CA1873 is about — and a
    ///     structured record is what a support log can be filtered on.
    /// </remarks>
    [LoggerMessage(
        EventId = 14071,
        Level = LogLevel.Information,
        Message = "frame {Frame} · {Seconds:0.00}s · {Agent} ({Planner}) {From} → {To}, "
            + "intruder {Distance:0.0} m"
    )]
    public static partial void Decided(
        ILogger logger,
        long frame,
        double seconds,
        string agent,
        AiPlanner planner,
        Symbol from,
        Symbol to,
        float distance
    );

    [LoggerMessage(
        EventId = 14072,
        Level = LogLevel.Information,
        Message = "{Changes} change(s) of mind in {Seconds:0.0} s — guard {Guard}, villager "
            + "{Villager}, scavenger {Scavenger} — and {Symptoms} diagnosed symptom(s). ⚠ Zero "
            + "changes after a full script is the failure to expect: the stack ran and decided "
            + "nothing. A symptom count above zero on this village is a defect, because nothing "
            + "here is misbehaving."
    )]
    public static partial void RunSummary(
        ILogger logger,
        int changes,
        int guard,
        int villager,
        int scavenger,
        double seconds,
        int symptoms
    );

    [LoggerMessage(
        EventId = 14073,
        Level = LogLevel.Information,
        Message = "Guard {Guard}, villager {Villager}, scavenger {Scavenger}, intruder {Intruder}."
    )]
    public static partial void WhereTheyEnded(
        ILogger logger,
        Vector3 guard,
        Vector3 villager,
        Vector3 scavenger,
        Vector3 intruder
    );

    [LoggerMessage(
        EventId = 14074,
        Level = LogLevel.Information,
        Message = "The AI overlay is registered on the frame loop — doc 37 § P7's debugger, in an "
            + "application rather than in a test."
    )]
    public static partial void OverlayRegistered(ILogger logger);

    [LoggerMessage(
        EventId = 14075,
        Level = LogLevel.Warning,
        Message = "There is no DebugDraw, so the overlay is not registered. Graphics.Overlays is "
            + "what builds one; a headless run with no capture path has no device to draw through."
    )]
    public static partial void NoOverlay(ILogger logger);

    [LoggerMessage(
        EventId = 14076,
        Level = LogLevel.Information,
        Message = "The overlay drew {Agents} agent(s) and {Rows} row(s) on the last frame. ⚠ Zero "
            + "agents with a village that decided things means the style culled them — Range and "
            + "Viewpoint, in that order."
    )]
    public static partial void OverlayDrew(ILogger logger, int agents, int rows);

    [LoggerMessage(
        EventId = 14077,
        Level = LogLevel.Warning,
        Message = "There is no engine loop, so there is no world and nothing to decide. This is "
            + "what --vixen-frames 1 on a machine with no GPU looks like, and it is not an error."
    )]
    public static partial void NoEngine(ILogger logger);
}
