// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core.Diagnostics;
using Vixen.Ui.Composition;
using Vixen.Ui.Reactive;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>That a two-way binding which can only ever run once says so.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The narrowness #663 measures without naming.</b> Every <c>bind:</c> attribute in the
///         repository binds <c>Something.Value</c> on a <c>Signal&lt;T&gt;</c>, so the shape an
///         author porting a hand-written panel actually has — a plain model — had never been
///         exercised. The forward leg is an <c>Effect</c>: it re-runs when a signal it <i>read</i>
///         changes, and a property is not one, so it runs once and is finished. The write-back leg
///         is a <c>PropertyChanged</c> subscription and works either way.
///     </para>
///     <para>
///         ⚠ <b>Which makes it strictly worse than the type mismatch that file already refuses.</b>
///         A mismatch produced a control that never moved; this produces a control that moves
///         correctly until anything other than the control writes the model, and then stops. Nothing
///         throws and nothing was logged, because nothing went wrong — the graph is simply not the
///         shape its author thought they wrote.
///     </para>
///     <para>
///         <b>Asserted against a real <see cref="RingBufferSink" />, on
///         <c>StyleDiagnosticDrainTests</c>' rule</b>: that ring is what the editor's Console panel
///         reads, so "a developer will see it" is a claim about this object.
///     </para>
/// </remarks>
public class InertBindingTests {
    /// <summary>A document logging into a ring, and the ring.</summary>
    static (UiDocument Document, RingBufferSink Log) Watched() {
        var sink = new RingBufferSink(64);
        return (new UiDocument(200f, 200f, logger: sink.CreateLogger("Vixen.Ui")), sink);
    }

    static IReadOnlyList<LogRecord> Warnings(RingBufferSink sink) =>
        [.. sink.Snapshot().Where(record => record.Level >= LogLevel.Warning)];

    /// <summary>A binding over a plain property reaches the log, naming what it is on.</summary>
    [Fact]
    public void A_binding_that_reads_nothing_reactive_says_so() {
        var (document, sink) = Watched();
        using var owned = document;

        var component = BuildContext.Build<Plain>(document, document.Root);
        document.Effects.Flush();

        var warning = Assert.Single(Warnings(sink));

        Assert.Equal(7008, warning.EventId.Id);
        Assert.Contains("label", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Text", warning.Message, StringComparison.Ordinal);

        // And the binding still does what it did: one forward write, and a live write-back.
        Assert.Equal("Kick", component.Label.Text);

        component.Label.Text = "Snare";
        Assert.Equal("Snare", component.Model.Name);
    }

    /// <summary>
    ///     ⚠ <b>The instrument check, and the half that matters most.</b>
    /// </summary>
    /// <remarks>
    ///     A rule that warned on every <c>TwoWay</c> would pass the test above with the dependency
    ///     count never consulted, and would put a line in the Console panel for every binding in
    ///     every panel in the editor within a second of opening it — which is how a channel stops
    ///     being read.
    /// </remarks>
    [Fact]
    public void A_binding_over_a_signal_says_nothing() {
        var (document, sink) = Watched();
        using var owned = document;

        var component = BuildContext.Build<Reactive>(document, document.Root);
        document.Effects.Flush();

        Assert.Empty(Warnings(sink));

        // And it is genuinely live in the direction the plain one is not.
        component.Name.Value = "Snare";
        document.Effects.Flush();
        Assert.Equal("Snare", component.Label.Text);
    }

    /// <summary>A model with no signal in it, which is what a hand-written panel already has.</summary>
    sealed class Plain : Component {
        public sealed class Mixer {
            public string? Name { get; set; } = "Kick";
        }

        public Mixer Model { get; } = new();

        public UiElement Label { get; private set; } = null!;

        protected override void Build(BuildContext ctx) {
            Label = ctx.Element(null, "label");
            ctx.TwoWay(Label, "Text", () => Model.Name, value => Model.Name = value);
        }
    }

    /// <summary>The same binding over a signal, which is every <c>bind:</c> in the tree.</summary>
    sealed class Reactive : Component {
        public Signal<string?> Name { get; } = new("Kick");

        public UiElement Label { get; private set; } = null!;

        protected override void Build(BuildContext ctx) {
            Label = ctx.Element(null, "label");
            ctx.TwoWay(Label, "Text", () => Name.Value, value => Name.Value = value);
        }
    }
}
