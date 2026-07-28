// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Messaging;
using Vixen.Net.Time;

namespace Vixen.Net.Prediction;

/// <summary>What the server has seen of one client's input, sent back so the client can steer.</summary>
/// <remarks>
///     <para>
///         <b>A broadcast rather than a payload kind of its own</b>, because it is not about a
///         networked object — it is about a connection, which is the case broadcasts exist for. The
///         numbers are deltas since the last report rather than lifetime totals, so a controller does
///         not have to remember what it was told last time to work out what just happened.
///     </para>
///     <para>
///         <b>It is the only thing that knows the answer.</b> A client can measure a round trip, and a
///         round trip is a good estimate of the wrong thing: what matters is whether its input reached
///         the server before the tick it was for, which is a fact about the server's buffer and about
///         nothing the client can observe.
///     </para>
/// </remarks>
/// <param name="Depth">How many ticks of input the server is holding for this client.</param>
/// <param name="Starved">Ticks it simulated with no input since the last report.</param>
/// <param name="Late">Inputs that arrived too late to use since the last report.</param>
public readonly record struct PredictionHealth(int Depth, int Starved, int Late)
    : IBroadcast<PredictionHealth> {
    /// <inheritdoc />
    public static string BroadcastName => "Vixen.Net.Prediction.PredictionHealth";

    /// <inheritdoc />
    public void Write(ref BitWriter writer) {
        // Six bits each, saturating. A depth past sixty-three is a client so far ahead that the exact
        // number stopped being information — what the controller does with it is the same either way.
        writer.Write((uint)Math.Clamp(Depth, 0, 63), 6);
        writer.Write((uint)Math.Clamp(Starved, 0, 63), 6);
        writer.Write((uint)Math.Clamp(Late, 0, 63), 6);
    }

    /// <inheritdoc />
    public static bool TryRead(ref BitReader reader, out PredictionHealth value) {
        value = default;

        if (!reader.TryRead(6, out var depth) || !reader.TryRead(6, out var starved)
            || !reader.TryRead(6, out var late)) {
            return false;
        }

        value = new((int)depth, (int)starved, (int)late);

        return true;
    }
}

/// <summary>Turns one client's input-buffer health into a report, once in a while. Server-side.</summary>
/// <remarks>
///     Not every tick. The controller on the other end moves by one tick at a time and waits to see
///     what happened, so reporting faster than it can act would be bandwidth spent on numbers nobody
///     reads — and a report is only useful once enough ticks have passed for starvation to mean
///     something.
/// </remarks>
public sealed class PredictionHealthReporter {
    long starved;
    long late;
    int sinceReport;

    /// <summary>How many ticks between reports.</summary>
    public int Period { get; init; } = 30;

    /// <summary>Reports produced.</summary>
    public long ReportCount { get; private set; }

    /// <summary>Takes a tick's worth of the buffer's state, and says whether there is a report.</summary>
    /// <param name="health">What the buffer says now, with lifetime totals.</param>
    /// <param name="report">The report, if one is due.</param>
    /// <returns>Whether one is.</returns>
    public bool TryAdvance(in InputHealth health, out PredictionHealth report) {
        report = default;

        if (++sinceReport < Period) {
            return false;
        }

        // Deltas since the last report. The buffer counts for ever, and "has starved twice since the
        // match began" is not a thing to steer by.
        report = new(health.Depth, (int)(health.Starved - starved), (int)(health.Late - late));
        starved = health.Starved;
        late = health.Late;
        sinceReport = 0;
        ReportCount++;

        return true;
    }
}

/// <summary>Moves the client's tick lead to whatever the server says it needs to be.</summary>
/// <remarks>
///     <para>
///         <b>The feedback loop prediction was missing.</b> Everything else was in place: the buffer
///         measures, <c>TickManager.LeadBias</c> adjusts, and nothing carried one to the other — so a
///         client's lead was whatever the round-trip estimator produced for interpolation, which is an
///         estimate of a different quantity.
///     </para>
///     <para>
///         <b>One tick at a time, and never on one report.</b> Changing the lead moves every input the
///         client has not sent yet, so a controller that reacted to a single starved tick would spend
///         its life oscillating — and each oscillation is a visible correction, because a shifted lead
///         means inputs arriving for ticks the server has already simulated. It waits for a run of
///         reports agreeing, then moves by one.
///     </para>
///     <para>
///         <b>It is asymmetric on purpose.</b> Starvation is corrected quickly and depth is given up
///         slowly, because being too far ahead costs a little input latency and being too far behind
///         costs corrections the player sees. Cheap mistake, expensive mistake.
///     </para>
/// </remarks>
public sealed class TickLeadController {
    int agreeing;
    int direction;

    /// <summary>How deep the buffer should be. Matches <c>InputBuffer.TargetDepth</c>.</summary>
    public int TargetDepth { get; init; } = 2;

    /// <summary>How many reports must agree before the lead moves.</summary>
    /// <remarks>
    ///     Two to grow, because starvation is the expensive direction and one report of it is already
    ///     a player seeing something. Shrinking asks for more — see <see cref="PatienceToShrink" />.
    /// </remarks>
    public int PatienceToGrow { get; init; } = 2;

    /// <summary>How many must agree before it is given up.</summary>
    public int PatienceToShrink { get; init; } = 6;

    /// <summary>The most the bias may be pushed either way.</summary>
    /// <remarks>
    ///     A bound rather than a policy: past this the round-trip estimate is wrong by more than a
    ///     controller should be papering over, and the right answer is that the connection is not one
    ///     prediction helps with.
    /// </remarks>
    public int MaxBias { get; init; } = 8;

    /// <summary>Times the lead was increased.</summary>
    public long GrewCount { get; private set; }

    /// <summary>Times it was decreased.</summary>
    public long ShrankCount { get; private set; }

    /// <summary>Takes a report and adjusts the clock.</summary>
    /// <param name="ticks">The client's clock.</param>
    /// <param name="report">What the server said.</param>
    /// <returns>How the bias moved: −1, 0 or +1.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ticks" /> is null.</exception>
    public int Apply(TickManager ticks, in PredictionHealth report) {
        ArgumentNullException.ThrowIfNull(ticks);

        // Lateness is the loud signal: an input that arrived after its tick means the client is not
        // far enough ahead, whatever the depth happened to be when the report was written.
        var wanted = report.Late > 0 || report.Starved > 0 ? 1
            : report.Depth > TargetDepth + 1 ? -1
            : 0;

        if (wanted == 0 || wanted != direction) {
            direction = wanted;
            agreeing = wanted == 0 ? 0 : 1;

            return 0;
        }

        if (++agreeing < (wanted > 0 ? PatienceToGrow : PatienceToShrink)) {
            return 0;
        }

        agreeing = 0;
        var moved = Math.Clamp(ticks.LeadBias + wanted, -MaxBias, MaxBias);

        if (moved == ticks.LeadBias) {
            return 0;
        }

        ticks.LeadBias = moved;

        if (wanted > 0) {
            GrewCount++;
        } else {
            ShrankCount++;
        }

        return wanted;
    }

    /// <summary>Forgets what it was waiting for, after a snap or a reconnect.</summary>
    public void Reset() {
        agreeing = 0;
        direction = 0;
    }
}
