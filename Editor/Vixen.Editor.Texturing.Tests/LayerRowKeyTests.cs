// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.Core;
using Vixen.Editor.Texturing.Layers;
using Vixen.Ui;
using Vixen.Ui.Composition;
using Vixen.Ui.Controls;
using Vixen.Ui.Reactive;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>What a <c>@for</c> over the layers can and cannot do, measured rather than argued.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/881">#881</a>'s remaining half is the
///         rows, and three slices have now written down in prose what blocks it.</b> This file is
///         that statement as code: the two candidate keys are run against a real
///         <c>LayerStackDocument</c>, edited through the panel's own slider, and each is shown
///         failing in its own way — and then the model that satisfies both halves at once is shown
///         working. A fourth sentence would have been worth nothing; a reader who doubts any of the
///         three can now delete an assertion and watch it go red.
///     </para>
///     <para>
///         ⚠ <b>The two halves a row has to have at the same time.</b> A row must <em>follow</em> its
///         layer — an undo, an evaluation, an edit made from anywhere else has to show up on the
///         controls — and it must <em>survive</em> a value edit, because the control being edited is
///         one the artist has the pointer captured on. <c>LayerStackView.Shape</c> buys both today by
///         rebuilding on structure and re-reading values through <c>bindings</c>, and
///         <c>LayerStackEditingTests.A_value_edit_keeps_the_row_and_a_reorder_rebuilds_it</c> is the
///         production assertion of it.
///     </para>
///     <para>
///         ⚠ <b>Neither key gives both</b>, and that is what these three tests measure.
///         <c>BuildContext.For</c> matches a key, reuses the region and does <em>not</em> re-run the
///         body, so a row keyed on <c>LayerAsset.Id</c> survives and goes stale, while a row keyed on
///         the layer's value follows and is torn down — under the artist's own drag, which is the
///         half no earlier measurement stated. The way out is a row model whose contents notify: a
///         <c>Signal&lt;LayerAsset&gt;</c> per row, keyed on the model object, which is
///         <c>vxml-for-key-rule</c>'s "key on the object only when that object holds signals".
///     </para>
///     <para>
///         ⚠ <b>A fourth blocker turned up while writing this, and it is not in any of the three
///         prose statements</b> — see <see cref="Name" />. A binding that writes <c>Text</c> to an
///         element with children throws, and an <c>Effect</c> answers a throw by suspending itself:
///         the row renders once, keeps what it was given, and never follows anything again, with no
///         diagnostic and no visible failure. Every row shape in the panel is a container, so the
///         port has a label to add per bound string.
///     </para>
///     <para>
///         ⚠ <b>The components here are this file's and there is deliberately no production caller
///         for the third one.</b> Landing a row model that no markup consumes would be exactly the
///         finished-thing-nothing-calls this workstream keeps shipping; what is landed is the
///         measurement and the shape the port has to take, so the slice that writes the markup does
///         not have to rediscover it a fifth time.
///     </para>
/// </remarks>
public class LayerRowKeyTests {
    /// <summary>⚠ A row keyed on the layer's id survives an edit and shows the value it opened with.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Both assertions matter and neither alone says anything.</b> The row being the same
    ///         element is the property the panel needs; the text being the <em>old</em> opacity is
    ///         what that property costs when the item is an immutable record. Assert only the first
    ///         and a loop that rebuilt everything passes; assert only the second and so does a loop
    ///         that drew nothing at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The edit is made through the panel rather than to the asset.</b> A test that
    ///         wrote a new <c>LayerAsset</c> by hand would be measuring <c>For</c> against a fixture's
    ///         idea of an edit; dragging <c>layer-stack-opacity</c> is the gesture the port has to
    ///         keep working, and what comes back off the document is what a real one produces.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_row_keyed_on_the_id_survives_the_edit_and_shows_the_opacity_it_opened_with() {
        using var fixture = new TexturingFixture();

        var stack = Open(fixture);

        using var ui = new UiDocument(200f, 200f);
        var listing = BuildContext.Build<ById>(ui, ui.Root);

        listing.Layers.Value = Layers(stack);
        ui.Effects.Flush();

        var row = Assert.Single(listing.Root.Children);

        Assert.Equal("1", Name(row).Text);

        Edit(fixture, 0.25f);
        Assert.Equal(0.25f, Only(stack).Opacity);

        listing.Layers.Value = Layers(stack);
        ui.Effects.Flush();

        // The key is still there, so the region is — which is the whole reason keys exist.
        Assert.Same(row, Assert.Single(listing.Root.Children));

        // ⚠ And its body was not re-run, so every binding in it is still closed over the layer as it
        // was when the key first appeared. This is the assertion the panel cannot afford: it is the
        // opacity of a layer that no longer has it, on a row nobody rebuilt and nothing will.
        Assert.Equal("1", Name(row).Text);
    }

    /// <summary>⚠ A row keyed on the layer's value follows the edit by being destroyed by it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The escape <c>StatisticsView</c> took is not available here, and this is why.</b>
    ///         Keying an immutable snapshot on its own value is the documented answer for a row of
    ///         read-only data — the key changes when the data does, so the body re-runs and the text
    ///         is right. A layer's row is not read-only: it carries a <see cref="Slider" /> and a
    ///         <c>Select</c>, and the edit that changes the value <em>is</em> the artist holding one
    ///         of them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The control and not just the row, because "the element was replaced" is easy to
    ///         read as cosmetic.</b> What the assertion names is a different <see cref="Slider" />
    ///         object under a pointer that was captured on the first one — the element the drag is
    ///         addressed to has been removed and replaced by a copy of itself, which is the defect
    ///         <c>Shape</c> exists to prevent and which a value key reintroduces on the first
    ///         movement of the mouse.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_row_keyed_on_the_layers_value_is_torn_down_by_the_edit_it_follows() {
        using var fixture = new TexturingFixture();

        var stack = Open(fixture);

        using var ui = new UiDocument(200f, 200f);
        var listing = BuildContext.Build<ByValue>(ui, ui.Root);

        listing.Layers.Value = Layers(stack);
        ui.Effects.Flush();

        var row = Assert.Single(listing.Root.Children);
        var slider = Held(row);

        Assert.Equal("1", Name(row).Text);

        Edit(fixture, 0.25f);

        listing.Layers.Value = Layers(stack);
        ui.Effects.Flush();

        var after = Assert.Single(listing.Root.Children);

        // It follows: the body re-ran, so the text is the new opacity.
        Assert.Equal("0.25", Name(after).Text);

        // ⚠ And that is the cost. A new region, a new row and a new slider — the one the artist is
        // dragging is gone, on the keystroke that changed the number they were dragging it to.
        Assert.NotSame(row, after);
        Assert.NotSame(slider, Held(after));
        Assert.True(row.IsRemoved);
    }

    /// <summary>⚠ A row model holding a signal per layer follows the edit and survives it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The shape the port has to take, and it is a model change before it is a markup
    ///         one.</b> The key is the model object, which is stable across the edit; what changes is
    ///         the <see cref="Signal{T}" /> inside it, and a binding that reads it is re-run by the
    ///         effect graph rather than by the reconciler. Both halves hold at once — the same
    ///         <see cref="Slider" /> object, and the new opacity — which is exactly what
    ///         <c>LayerStackView.Shape</c> plus <c>bindings</c> buys imperatively today.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>LayerAsset</c> holds no signal and must not grow one.</b> It is the file
    ///         format's record, written by <c>LayerStackYaml</c> and held as an undo entry's
    ///         before-image; a signal on it would be shared reactive state inside a value somebody is
    ///         keeping a copy of. The signal belongs to the <em>row</em>, which is a thing the panel
    ///         owns and rebuilds, and the reconciliation that writes it is the panel's own — one
    ///         model per id, its value assigned on every <c>Show</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The write happens before the flush and the read after it, which is the cost
    ///         #881's remaining half has to buy.</b> A markup binding is an <c>Effect</c> and an
    ///         effect never runs on the write — so a panel whose rows are markup answers a
    ///         synchronous reader with the previous frame's numbers until
    ///         <c>EffectScheduler.Flush</c> has run. Six test files read this panel's tree
    ///         synchronously.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_row_model_holding_a_signal_follows_the_edit_and_keeps_its_control() {
        using var fixture = new TexturingFixture();

        var stack = Open(fixture);

        using var ui = new UiDocument(200f, 200f);
        var listing = BuildContext.Build<ByModel>(ui, ui.Root);

        listing.Rows.Value = [.. Layers(stack).Select(layer => new LayerRow(layer))];
        ui.Effects.Flush();

        var row = Assert.Single(listing.Root.Children);
        var slider = Held(row);

        Assert.Equal("1", Name(row).Text);

        Edit(fixture, 0.25f);

        // What a `Show` would do: the same models, re-pointed at what the document now holds. The
        // list itself does not change, so the reconciler matches every key and touches nothing.
        var models = listing.Rows.Value;

        foreach (var (model, layer) in models.Zip(Layers(stack))) {
            model.Layer.Value = layer;
        }

        // ⚠ Nothing has run yet. The assignment queued an effect; the row still says what it said.
        Assert.Equal("1", Name(row).Text);

        ui.Effects.Flush();

        Assert.Same(row, Assert.Single(listing.Root.Children));
        Assert.Same(slider, Held(row));
        Assert.Equal("0.25", Name(row).Text);
    }

    /// <summary>The label a row's opacity is written on.</summary>
    /// <remarks>
    ///     ⚠ <b>A child rather than the row, and this is a trap rather than a preference.</b>
    ///     <c>UiElement.Text</c> on an element that has element children throws from
    ///     <c>LayoutTree.SetMeasureFunction</c> — a node cannot both measure itself and have
    ///     children — and inside an <c>Effect</c> that exception is caught and the effect is
    ///     <em>suspended</em>. So a row that bound its own <c>Text</c> ran exactly once, kept the
    ///     text it was given, and silently stopped following anything for ever, which is a panel
    ///     that renders and then freezes. Every row this file builds puts its text on a label, which
    ///     is what the real panel does too.
    /// </remarks>
    static UiElement Name(UiElement row) => row.Children.Single(child => string.Equals(child.Tag, "name", StringComparison.Ordinal));

    /// <summary>The control on a row that an artist would have the pointer captured on.</summary>
    static Slider Held(UiElement row) => row.Children.OfType<Slider>().Single();

    /// <summary>What the panel's slider does to the only layer's opacity, as a person would.</summary>
    static void Edit(TexturingFixture fixture, float opacity) {
        var panel = fixture.Shell.Workspace.Open(TexturingModule.StackPanel);

        Assert.NotNull(panel);

        Slider? found = null;

        Walk(panel);

        Assert.NotNull(found);

        found.Value = opacity;

        void Walk(UiElement element) {
            if (element is Slider slider && element.HasClass("layer-stack-opacity")) {
                Assert.Null(found);

                found = slider;
            }

            foreach (var child in element.Children) {
                Walk(child);
            }
        }
    }

    /// <summary>The set's layers, exactly as a row loop would read them.</summary>
    static LayerAsset[] Layers(LayerStackDocument document) => [.. document.Document.Sets[0].Layers];

    /// <summary>The one layer this file's stack has.</summary>
    static LayerAsset Only(LayerStackDocument document) => Assert.Single(document.Document.Sets[0].Layers);

    /// <summary>One fill layer at full opacity, opened through the module's own verb.</summary>
    static LayerStackDocument Open(TexturingFixture fixture) {
        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, "Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var document = fixture.Project.Documents.OfType<LayerStackDocument>().Single();

        document.Document = new() {
            Name = "Hull",
            BaseWidth = 32,
            BaseHeight = 32,
            Seed = 7u,
            Sets = [
                new() {
                    Name = "S",
                    Channels = [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
                    Layers = [
                        new() {
                            Id = "bottom",
                            Name = "Bottom",
                            Kind = LayerKind.Fill,
                            Opacity = 1f,
                            Values = { ["baseColor"] = [0.25f, 0.25f, 0.25f, 1f] }
                        }
                    ]
                }
            ]
        };

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        return document;
    }

    /// <summary>An opacity as a row would print it.</summary>
    static string Said(LayerAsset layer) => layer.Opacity.ToString(CultureInfo.InvariantCulture);

    /// <summary>One row per layer, keyed on the id — what #881 asks for, and what it costs.</summary>
    sealed class ById : Component {
        public Signal<LayerAsset[]> Layers { get; } = new([]);

        protected override void Build(BuildContext ctx) =>
            ctx.For(
                null,
                () => Layers.Value,
                static layer => layer.Id,
                static (inner, parent, layer) => {
                    var row = inner.Element(parent, "row");
                    var name = inner.Element(row, "name");

                    inner.Child<Slider>(row, null);
                    inner.Bind(() => name.Text = Said(layer));
                }
            );
    }

    /// <summary>The same, keyed on the layer's value — <c>StatisticsView</c>'s answer.</summary>
    sealed class ByValue : Component {
        public Signal<LayerAsset[]> Layers { get; } = new([]);

        protected override void Build(BuildContext ctx) =>
            ctx.For(
                null,
                () => Layers.Value,
                static layer => layer,
                static (inner, parent, layer) => {
                    var row = inner.Element(parent, "row");
                    var name = inner.Element(row, "name");

                    inner.Child<Slider>(row, null);
                    inner.Bind(() => name.Text = Said(layer));
                }
            );
    }

    /// <summary>The same, over a model whose contents notify.</summary>
    sealed class ByModel : Component {
        public Signal<LayerRow[]> Rows { get; } = new([]);

        protected override void Build(BuildContext ctx) =>
            ctx.For(
                null,
                () => Rows.Value,
                static model => model,
                static (inner, parent, model) => {
                    var row = inner.Element(parent, "row");
                    var name = inner.Element(row, "name");

                    inner.Child<Slider>(row, null);
                    inner.Bind(() => name.Text = Said(model.Layer.Value));
                }
            );
    }

    /// <summary>One row's model: a stable identity holding a layer that notifies.</summary>
    /// <param name="layer">What the layer is now.</param>
    sealed class LayerRow(LayerAsset layer) {
        /// <summary>The layer this row draws, re-pointed on every refresh.</summary>
        public Signal<LayerAsset> Layer { get; } = new(layer);
    }
}
