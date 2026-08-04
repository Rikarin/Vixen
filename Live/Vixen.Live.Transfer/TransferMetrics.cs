// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Live.Transfer;

/// <summary>What transfers are costing. Doc 27 § Tick rebasing's four numbers.</summary>
/// <remarks>
///     ⚠ <b>A transfer that degrades is one that stops being seamless quietly</b>, which is the whole
///     argument for measuring it. The overlap getting longer is a content-delivery problem; the
///     commit latency getting longer is a control-plane problem; the abort histogram growing a new
///     tallest bar is whichever of them just broke. None of the three is visible without the numbers,
///     because every one of them still <em>works</em> — it just works worse.
/// </remarks>
public sealed class TransferMetrics {
    readonly Lock gate = new();
    readonly int[] aborts = new int[Enum.GetValues<TransferAbort>().Length];
    readonly List<double> overlaps = [];
    readonly List<double> commits = [];

    /// <summary>How many transfers finished.</summary>
    public int Committed { get; private set; }

    /// <summary>How many did not.</summary>
    public int Aborted { get; private set; }

    /// <summary>How many prediction resets clients have reported.</summary>
    public int PredictionResets { get; private set; }

    /// <summary>How many were attempted.</summary>
    public int Attempted => Committed + Aborted;

    /// <summary>The fraction that finished, or one when none has been attempted.</summary>
    /// <remarks>
    ///     One rather than zero for an empty fleet: a map nobody has left yet has not failed at
    ///     anything, and a dashboard opening on 0 % would say the opposite.
    /// </remarks>
    public double SuccessRate => Attempted == 0 ? 1 : (double)Committed / Attempted;

    /// <summary>Records a transfer that finished.</summary>
    /// <param name="transfer">It.</param>
    /// <exception cref="ArgumentNullException"><paramref name="transfer" /> is null.</exception>
    public void Record(SourceTransfer transfer) {
        ArgumentNullException.ThrowIfNull(transfer);

        lock (gate) {
            if (transfer.Phase == TransferPhase.Committed) {
                Committed++;
                overlaps.Add(transfer.Overlap.TotalMilliseconds);
                commits.Add(transfer.CommitLatency.TotalMilliseconds);
            } else if (transfer.Phase == TransferPhase.Aborted) {
                Aborted++;
                aborts[(int)transfer.Abort]++;
            }
        }
    }

    /// <summary>Records a client's report of its own reset.</summary>
    /// <param name="count">How many.</param>
    public void RecordResets(int count) {
        lock (gate) {
            PredictionResets += Math.Max(0, count);
        }
    }

    /// <summary>How many transfers gave up for a reason.</summary>
    /// <param name="reason">Which.</param>
    /// <returns>The count.</returns>
    public int AbortsFor(TransferAbort reason) {
        lock (gate) {
            return aborts[(int)reason];
        }
    }

    /// <summary>The abort histogram, worst first — the shape doc 27 § Diagnostics asks for.</summary>
    /// <returns>Every reason that has happened at least once.</returns>
    public IReadOnlyList<(TransferAbort Reason, int Count)> AbortHistogram() {
        lock (gate) {
            return [
                .. Enum.GetValues<TransferAbort>()
                    .Where(reason => reason != TransferAbort.None && aborts[(int)reason] > 0)
                    .Select(reason => (reason, aborts[(int)reason]))
                    .OrderByDescending(entry => entry.Item2)
                    .ThenBy(entry => entry.reason)
            ];
        }
    }

    /// <summary>How long the overlap lasts at a percentile.</summary>
    /// <param name="percentile">Between 0 and 1. <c>0.99</c> is the one that matters.</param>
    /// <returns>Milliseconds, or zero if nothing has finished.</returns>
    public double OverlapAt(double percentile) => Percentile(overlaps, percentile);

    /// <summary>How long the whole transfer takes at a percentile.</summary>
    /// <param name="percentile">Between 0 and 1.</param>
    /// <returns>Milliseconds, or zero if nothing has finished.</returns>
    public double CommitLatencyAt(double percentile) => Percentile(commits, percentile);

    /// <inheritdoc />
    public override string ToString() {
        lock (gate) {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{Committed} committed, {Aborted} aborted ({SuccessRate:P1}), overlap p99 {Percentile(overlaps, 0.99):F0} ms"
            );
        }
    }

    /// <summary>Nearest-rank, which is the same definition <c>RealmHealth</c> uses for its tick p99.</summary>
    double Percentile(List<double> samples, double percentile) {
        lock (gate) {
            if (samples.Count == 0) {
                return 0;
            }

            var sorted = new List<double>(samples);

            sorted.Sort();

            var rank = (int)Math.Ceiling(Math.Clamp(percentile, 0, 1) * sorted.Count);

            return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
        }
    }
}
