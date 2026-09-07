// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.TextureGraph;
using Vixen.Graphics;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>What a test that builds a preview of its own lends it, standing in for the module.</summary>
/// <remarks>
///     <para>
///         <b>A preview does not own its evaluator</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/820">#820</a>. <c>TexturingModule</c>
///         holds one and lends it to both panes, because an evaluator is a pipeline cache per kernel
///         and output format and two of them over one device compile the whole overlap twice. So a
///         test constructing a preview directly has to supply the lender, and this is the smallest
///         honest one: it owns exactly one and gives it back.
///     </para>
///     <para>
///         ⚠ <b>It counts, for the same reason the module does.</b> A lender that quietly built one
///         per call would make every test using it pass while reproducing the defect the issue is
///         about — a double more permissive than the runtime, which is the failure this repository
///         names by that phrase.
///     </para>
///     <para>
///         ⚠ <b>Two counters and not one, because <see cref="Built" /> alone cannot see the other
///         direction</b> — <a href="https://github.com/Rikarin/Vixen/issues/988">#988</a>. A pane
///         that stopped asking and constructed its own evaluator leaves <see cref="Built" /> at one
///         and every assertion about it green, which is #978's monotone-the-wrong-way shape moved
///         one type along. <see cref="Asks" /> counts the questions rather than the answers, so an
///         exact expectation goes red whichever borrower stops asking.
///     </para>
///     <para>
///         ⚠ <b>And both are read, which is what the paragraph above was claiming and nothing did.</b>
///         <c>PreviewLeaseTests</c> is the reader; before it, <see cref="Built" /> was a guard with
///         no assertion anywhere in the assembly — a counter that could not fail, in a double whose
///         whole justification is that a double which cannot fail is the defect.
///     </para>
/// </remarks>
sealed class LentEvaluator : IDisposable {
    TexturePlanEvaluator? evaluator;

    /// <summary>How many were built, which should be one however many panes borrowed it.</summary>
    public int Built { get; private set; }

    /// <summary>How many times a borrower asked for one at all.</summary>
    public int Asks { get; private set; }

    /// <summary>The lease to hand a preview.</summary>
    public TextureEvaluatorLease Lease => For;

    /// <inheritdoc />
    public void Dispose() {
        evaluator?.Dispose();
        evaluator = null;
    }

    TexturePlanEvaluator For(IGraphicsDevice device) {
        Asks++;

        if (evaluator is not null) {
            return evaluator;
        }

        Built++;

        return evaluator = new TexturePlanEvaluator(device);
    }
}
