// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.DocGen;

/// <summary>How the written half's problems are reported, and to which stream.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A target that prints <c>[ERR]</c> and then succeeds reads as a gate that passed, and
///         this one has already cost something</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/1018">#1018</a>. <c>./build.sh Docs</c>
///         on a tree with an undocumented public type printed
///         <c>[ERR] Docs: T:… has no guide page and no line in docs/DocsExempt.txt</c>, reported
///         <c>Docs Succeeded</c>, and exited <b>0</b>; <c>CheckDocs</c> on the identical tree exited
///         255. Sixteen batches of one workstream ran the first in their pre-merge sweep believing
///         it was the gate, and every one of those sweeps would have passed a new undocumented
///         public type.
///     </para>
///     <para>
///         <b>The division of labour is right and the <em>word</em> was wrong.</b> <c>Docs</c>
///         generates and <c>CheckDocs</c> gates; that is deliberate and CI runs the second. What is
///         not defensible is calling something an error on a run that will succeed whatever it says
///         — this repository's own convention is <i>ask what a gate prints on the day it does not
///         run, and if the answer is success, fix that first.</i>
///     </para>
///     <para>
///         ⚠ <b>The stream is the whole mechanism, because <c>[ERR]</c> is not this tool's word.</b>
///         Nuke tags a child process's <c>stderr</c> as <c>[ERR]</c> and its <c>stdout</c> as plain
///         output; nothing here chooses that prefix and nothing here can spell it. So "stop saying
///         error" means "write to the other stream", which is what
///         <see cref="Describe" />'s first return value decides — and it is why the decision is a
///         value a test can read rather than a branch around two <c>Console</c> calls.
///     </para>
///     <para>
///         ⚠ <b>The baseline half is untouched and still fails <c>Docs</c>.</b>
///         <c>--verify-baselines</c> is passed by both targets and returns 1 from either, so a graph
///         that disagrees with a <c>PublicAPI</c> baseline is a red <c>Docs</c> today. Only the
///         written half — coverage, page contracts, examples — is the half <c>Docs</c> reports and
///         does not gate, and that asymmetry is exactly what made the printed word misleading.
///     </para>
/// </remarks>
static class ProblemReport {
    /// <summary>The target that fails on what this reports, named in the non-gating wording.</summary>
    /// <remarks>
    ///     ⚠ <b>Named rather than described.</b> "run the check target" is advice a reader has to
    ///     resolve; the two targets differ by six characters and CLAUDE.md was wrong about which was
    ///     which for sixteen batches, so the sentence carries the command it means.
    /// </remarks>
    public const string Gate = "./build.sh CheckDocs";

    /// <summary>How many problems are named before the rest are counted.</summary>
    public const int Limit = 25;

    /// <summary>What to print about the written half, and whether it is an error.</summary>
    /// <param name="problems">What the passes found, in the order they found it.</param>
    /// <param name="gating">Whether this run fails on them — <c>--check-docs</c>.</param>
    /// <param name="limit">How many to name before counting the remainder.</param>
    /// <returns>
    ///     Whether the lines belong on standard error, and the lines. Empty when there is nothing to
    ///     say, in which case the stream does not matter and is reported as <see langword="false" />.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>Both callers get the same list and only the header and the stream differ.</b> A
    ///     non-gating run that also shortened the list would be a second thing to keep in step, and
    ///     the reader of a <c>Docs</c> run is the person about to write the page.
    /// </remarks>
    public static (bool ToStandardError, IReadOnlyList<string> Lines) Describe(
        IReadOnlyList<string> problems,
        bool gating,
        int limit = Limit
    ) {
        ArgumentNullException.ThrowIfNull(problems);

        if (problems.Count == 0) {
            return (false, []);
        }

        var plural = problems.Count == 1 ? "" : "s";

        List<string> lines = [
            "",
            gating
                ? $"{problems.Count} problem{plural} in the written half:"
                : $"{problems.Count} problem{plural} in the written half. This run generates and does "
                + $"not gate: `{Gate}` is the target that fails on these."
        ];

        for (var index = 0; index < problems.Count && index < limit; index++) {
            lines.Add("  " + problems[index]);
        }

        if (problems.Count > limit) {
            lines.Add($"  …and {problems.Count - limit} more");
        }

        return (gating, lines);
    }

    /// <summary>Prints what <see cref="Describe" /> decided.</summary>
    /// <param name="problems">What the passes found.</param>
    /// <param name="gating">Whether this run fails on them.</param>
    /// <remarks>
    ///     The only place the written half reaches a console, so that "which stream" is one decision
    ///     rather than a branch somebody adds a third arm to.
    /// </remarks>
    public static void Write(IReadOnlyList<string> problems, bool gating) {
        var (toStandardError, lines) = Describe(problems, gating);
        var stream = toStandardError ? Console.Error : Console.Out;

        foreach (var line in lines) {
            stream.WriteLine(line);
        }
    }
}
