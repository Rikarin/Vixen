// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Vixen.Ui.Reactive;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>That a frame drains the document's bindings, and drains them in the right place.</summary>
/// <remarks>
///     <b>The gap these close was invisible for the reason gaps in a reactive layer usually are:
///     every test flushed by hand.</b> A queue nobody drains behaves exactly like a queue that is
///     always empty, right up until a host that did not know to drain it draws an interface whose
///     bindings never ran. Asserting on <see cref="UiDocument.Update" /> rather than on
///     <see cref="EffectScheduler.Flush" /> is the whole of what makes these a gate: the subject is
///     the frame, not the scheduler.
/// </remarks>
public class EffectPassTests {
    /// <summary>A component whose text and class are both bindings.</summary>
    sealed class Bound : Component {
        public Signal<int> Count { get; } = new(0);

        protected override void Build(BuildContext ctx) {
            var label = ctx.Text(null, () => Count.Value);
            ctx.Bind(label, "class", () => Count.Value == 0 ? "zero" : "some");
        }
    }

    /// <summary>What every host had to know and only one of them did.</summary>
    [Fact]
    public void A_pass_runs_the_bindings_queued_since_the_last_one() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Bound>(document, document.Root);
        var label = component.Root.Children[0];

        // Not flushed by hand anywhere in this test, which is the point of it.
        document.Update();
        Assert.Equal("0", label.Text);

        component.Count.Value = 7;
        document.Update();
        Assert.Equal("7", label.Text);
    }

    /// <summary>
    ///     The case the early return used to swallow: a signal write leaves the document clean, so a
    ///     pass that checked <c>dirty</c> before draining would go home and read the write next frame.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>One <see cref="UiDocument.Update" />, and the assertion is on the same frame.</b> Two
    ///     passes would hide the defect entirely — the effect from the first would be picked up by
    ///     the second — which is why this asserts after exactly one, and why it looks at a width the
    ///     cascade has to have re-resolved rather than at the text the effect wrote directly.
    /// </remarks>
    [Fact]
    public void A_signal_written_between_passes_is_read_by_the_next_one_and_not_the_one_after() {
        using var document = new UiDocument(200f, 200f);
        document.Load(".zero { width: 10px; } .some { width: 40px; }");

        var component = BuildContext.Build<Bound>(document, document.Root);
        var label = component.Root.Children[0];

        document.Update();
        Assert.Equal(10f, label.Width, 0.001f);

        component.Count.Value = 3;

        // The write dirtied nothing on the element — it dirtied a signal — so this pass is the one
        // that has to notice, and it can only notice by draining before it decides there is no work.
        document.Update();
        Assert.Equal(40f, label.Width, 0.001f);
    }

    /// <summary>A host that drains at its own point in the frame is not made to drain twice.</summary>
    [Fact]
    public void A_host_that_flushed_already_leaves_the_pass_nothing_to_do() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Bound>(document, document.Root);

        component.Count.Value = 2;
        Assert.NotEqual(0, document.Effects.Flush());

        // Empty, not refused: flushing twice is a second read of a queue rather than an error, which
        // is what lets `EditorShell` keep its own drain where its frame wants it.
        Assert.Equal(0, document.Effects.PendingCount);
        document.Update();
        Assert.Equal(0, document.Effects.PendingCount);
    }

    /// <summary>The drain costs nothing on a document with nothing queued.</summary>
    /// <remarks>
    ///     ⚠ <b>Every frame now pays for this, so what it costs when there is nothing to do is the
    ///     number that matters.</b> Doc 09's steady-state budget is zero bytes and
    ///     <c>Samples/02</c> measures it at 8 001 elements — a drain that allocated an enumerator or
    ///     a closure per frame would spend that budget on an empty queue. The path is a field read, a
    ///     depth check and two empty-collection tests, and this is the assertion that keeps it one.
    /// </remarks>
    [Fact]
    public void An_empty_queue_costs_a_settled_frame_nothing() {
        using var document = new UiDocument(200f, 200f);
        BuildContext.Build<Bound>(document, document.Root);

        // Settle first: what is measured is a frame with no work in it, not the frame that built the
        // component and shaped its text.
        for (var warmup = 0; warmup < 4; warmup++) {
            document.Update();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var frame = 0; frame < 64; frame++) {
            document.Update();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    ///     ⚠ <b>An effect must not run from inside a pass</b>, which is the reason the drain is under
    ///     the nested-call guard and not above it.
    /// </summary>
    /// <remarks>
    ///     A <c>LayoutFinished</c> handler runs inside <c>Settle</c>, and the controls that hang a
    ///     refresh on it re-enter <c>Update</c> — which the guard refuses. Were the drain outside
    ///     that guard, the refused call would still run every binding in the document while the
    ///     settle loop was walking the tree. What it does instead is nothing at all.
    /// </remarks>
    [Fact]
    public void A_pass_re_entered_from_a_handler_runs_no_bindings() {
        using var document = new UiDocument(200f, 200f);
        var component = BuildContext.Build<Bound>(document, document.Root);
        var label = component.Root.Children[0];

        document.Update();

        var nested = 0;

        document.LayoutFinished += _ => {
            if (nested++ > 0) {
                return;
            }

            component.Count.Value = 5;
            var queued = document.Effects.PendingCount;

            // Two, because `Bound` binds a text and a class and the write dirties both — asserted as
            // "what was queued is still queued" rather than as a number, so the claim survives the
            // fixture gaining a third binding.
            Assert.NotEqual(0, queued);

            // Refused, and therefore drains nothing: the queued bindings are still queued when this
            // returns, and the text is still what the last completed pass wrote.
            Assert.False(document.Update());
            Assert.Equal(queued, document.Effects.PendingCount);
            Assert.Equal("0", label.Text);
        };

        document.Root.Add("div");
        document.Update();

        // And the next frame is where it lands, which is the contract rather than a consolation.
        document.Update();
        Assert.Equal("5", label.Text);
    }
}
