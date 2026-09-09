// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Texturing.Layers;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>The panel's layout comes out of a stylesheet, and the panel still has one.</summary>
/// <remarks>
///     <para>
///         <b>The first half of <a href="https://github.com/Rikarin/Vixen/issues/881">#881</a>.</b>
///         Nine call sites in <c>LayerStackView</c>'s constructor wrote <c>display</c>,
///         <c>flex-direction</c>, <c>flex-grow</c> and a width through <c>SetStyle</c>; they are
///         <c>TexturingTheme.vcss</c> now, installed by the view into the document its host is in.
///     </para>
///     <para>
///         ⚠ <b>The assertions are the ones a missing sheet makes false, which took choosing.</b>
///         <c>NodeGraphThemeTests</c> records the trap in the same shape: an element with no
///         stylesheet takes CSS's initial <c>flex-direction: row</c>, so
///         <c>layer-stack { flex-direction: row }</c> is a rule whose absence changes nothing and a
///         test asserting it passes with the install deleted. What the sheet decides here is the
///         <em>column</em> — the left column stacks its binding row above its list — and the preview
///         column's fixed width. Both are read as geometry after a layout pass rather than as
///         declarations, which is the difference between "the rule is there" and "the rule reached
///         the panel".
///     </para>
/// </remarks>
public class LayerStackThemeTests {
    /// <summary>⚠ The left column stacks and the preview column is 280 wide, after a real layout.</summary>
    [Fact]
    public void The_sheet_reaches_the_panel_the_module_builds() {
        using var fixture = new TexturingFixture();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, "Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var panel = fixture.Shell.Workspace.Open(TexturingModule.StackPanel);

        Assert.NotNull(panel);

        fixture.Shell.Document.Update();
        fixture.Shell.Document.Draw();

        var preview = Only(panel, "layer-stack-preview");
        var binding = Only(panel, "layer-stack-binding");
        var list = Only(panel, "layer-stack-list");

        // ⚠ The declaration with no initial value behind it: a column of a fixed width, beside a
        // column that grows. Without the sheet this element is as wide as its widest child.
        Assert.Equal(280f, preview.Width);

        // ⚠ And `flex-direction: column` on the left column, read as the thing it decides. The
        // binding row is *above* the list and starts at the same left edge; the initial `row` would
        // put them side by side at the same top, so this is false in exactly the way a missing sheet
        // makes it false rather than being true of any laid-out panel.
        Assert.True(
            binding.Top < list.Top,
            $"the binding row is at y={binding.Top} and the layer list at y={list.Top}: the left "
            + "column is laying its children out in a row, which is CSS's initial direction and what "
            + "`layer-stack-rows { flex-direction: column }` in TexturingTheme.vcss is for. The sheet "
            + "did not reach this panel."
        );

        Assert.Equal(binding.Left, list.Left);

        // The whole view is still a row — asserted last and as a consequence, because it is the one
        // declaration the initial value would have given for nothing.
        Assert.True(preview.Left > list.Left, "the preview column is not beside the rows.");
    }

    /// <summary>⚠ And a second panel build does not load a second copy of the sheet.</summary>
    /// <remarks>
    ///     <b>A panel's factory really does re-run</b>: opening any other panel relays the workspace
    ///     out and rebuilds this one, which is why <c>LayerStackView</c> is disposed and replaced
    ///     there. <c>UiDocument.Load</c> appends and has no notion of a sheet it already holds, so an
    ///     unguarded install would grow the editor's stylesheet for as long as the session lasted.
    /// </remarks>
    [Fact]
    public void The_sheet_is_loaded_once_per_document_however_often_the_panel_is_rebuilt() {
        using var fixture = new TexturingFixture();

        Assert.True(TexturingTheme.Install(fixture.Shell.Document));
        Assert.False(TexturingTheme.Install(fixture.Shell.Document));

        using var second = new TexturingFixture();

        // A different document is a different answer, which is what makes the guard a guard rather
        // than a one-shot flag: two editor windows are two documents.
        Assert.True(TexturingTheme.Install(second.Shell.Document));
    }

    /// <summary>⚠ No typed control in the panel is renamed out of the theme that styles it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/1071">#1071</a>.</b>
    ///         <c>UiElement.Add&lt;T&gt;(string)</c>'s first parameter is the <em>tag</em>, so
    ///         <c>row.Add&lt;Slider&gt;("layer-stack-opacity")</c> — the convention twenty-eight
    ///         controls in this panel were built with — produced an element that
    ///         <c>ControlTheme.vcss</c>'s <c>slider</c>, <c>button</c>, <c>checkbox</c>,
    ///         <c>select</c> and <c>textbox</c> rules no longer matched.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Derived rather than listed, which is what makes it hold for the twenty-ninth
    ///         control.</b> A plain <c>Add(string)</c> container is exactly a
    ///         <see cref="UiElement" />, and a control carries a tag of its own that a stylesheet is
    ///         written against — so the rule is "a control may not answer to a <c>layer-stack-</c>
    ///         name", and no roll call of the twenty-eight has to be maintained beside the view.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>"Anything that is not exactly <see cref="UiElement" /> is a control" is what this
    ///         used to say, and it stopped being true when the panel became markup.</b> A
    ///         <c>.vxml</c> with <c>@inherits Vixen.Ui.UiElement</c> and <c>@tag layer-stack-…</c>
    ///         compiles to a subclass whose whole purpose is to answer to that name in place of the
    ///         <c>rows.Add("layer-stack-row")</c> it replaced — <c>LayerRowView</c> is one and
    ///         <c>LayerStackChrome</c> was already another, which escaped only because
    ///         <c>layer-stack</c> has no trailing dash to match on. There is no
    ///         <c>ControlTheme.vcss</c> selector for either to be renamed out of.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So the exemption is the declaring assembly and not a list of names.</b> A type
    ///         declared beside the view is this panel's own part and owns its tag; a type from
    ///         <c>Vixen.Ui.Controls</c> answering to a <c>layer-stack-</c> name is #1071 exactly, and
    ///         is what the sabotage — putting one <c>Add&lt;Slider&gt;("layer-stack-opacity")</c>
    ///         back — still turns red.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And a measurement, because the sentence above is satisfied by a panel that draws
    ///         no controls at all.</b> <c>slider { height: 20px; min-width: 80px }</c> in
    ///         <c>ControlTheme.vcss</c>, narrowed to 18 by <c>EditorTheme.vcss</c>, are both type
    ///         selectors with no other source — so a renamed slider stretches to its row instead,
    ///         which measures 30 here. That height, read after a layout pass, is the theme arriving
    ///         rather than the theme being declared.
    ///     </para>
    /// </remarks>
    [Fact]
    public void No_typed_control_in_the_panel_answers_to_a_layer_stack_tag() {
        using var fixture = new TexturingFixture();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, "Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var panel = fixture.Shell.Workspace.Open(TexturingModule.StackPanel);

        Assert.NotNull(panel);

        fixture.Shell.Document.Update();
        fixture.Shell.Document.Draw();

        List<string> renamed = [];
        var controls = 0;

        Walk(panel);

        // The instrument first: a panel that built nothing would satisfy the emptiness below.
        Assert.True(controls > 20, $"the panel drew {controls} controls, so the check below is vacuous.");

        Assert.True(
            renamed.Count == 0,
            $"these controls answer to a tag of their own naming — {string.Join(", ", renamed)} — so every "
            + "type selector in ControlTheme.vcss stops matching them. Pass the name as a class instead: "
            + "Add<T>(null, null, \"layer-stack-…\"). #1071."
        );

        // ⚠ The half that is geometry rather than a name. `slider { height: 20px; min-width: 80px }`
        // is ControlTheme.vcss and `slider { height: 18px }` is EditorTheme.vcss narrowing it — both
        // type selectors, and neither has any other source, so a renamed slider takes its height and
        // its width from nothing at all. 18 rather than 20 because the editor's sheet wins, which is
        // itself worth asserting: this reads the document the panel is really in.
        var opacity = Assert.IsType<Slider>(Only(panel, "layer-stack-opacity"));

        Assert.Equal(18f, opacity.Height);
        Assert.True(opacity.Width >= 80f, $"the opacity slider is {opacity.Width} wide, under the 80px minimum.");

        void Walk(UiElement element) {
            var type = element.GetType();

            if (type != typeof(UiElement)) {
                controls++;

                // ⚠ A part's tag *is* its identity, and this asks exactly that: does the element
                // answer to the name its own type declares? `LayerRowView` answers to
                // `layer-stack-row` because that is the element it replaced, so it passes; a control
                // handed a `layer-stack-` name at a call site does not, which is #1071's defect.
                //
                // ⚠ **And the exemption is the declaring assembly, which is broader than the
                // sentence above.** `UiElement.Add<T>`'s first argument is a tag override, so
                // `rows.Add<LayerRowView>("layer-stack-fill")` would rename a local part out of its
                // own rule — the very thing this rule is about — and this would call it exempt. The
                // narrow test is the tag the type itself answers to, and `UiElement.TagName` is
                // `protected`, so it is not reachable from a test assembly: #1131. No such call
                // exists today, so this is a hole in the instrument rather than a live defect —
                // written down because a hole nobody wrote down is how the next one gets through.
                if (element.Tag.StartsWith("layer-stack-", StringComparison.Ordinal)
                    && type.Assembly != typeof(LayerStackView).Assembly) {
                    renamed.Add($"{type.Name} as '{element.Tag}'");
                }
            }

            foreach (var child in element.Children) {
                Walk(child);
            }
        }
    }

    /// <summary>⚠ A layer's row is the markup part, in the place and the order the C# one held.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The rows half of <a href="https://github.com/Rikarin/Vixen/issues/881">#881</a>,
    ///         and what makes it a port rather than a rewrite.</b> <c>LayerRowView</c>'s host tag
    ///         <em>is</em> <c>layer-stack-row</c>, so it stands where <c>rows.Add("layer-stack-row")</c>
    ///         stood: the same direct child of <c>layer-stack-list</c>, reached by the same rule.
    ///         Asserting the type alone would be a tautology about a line of markup; asserting the
    ///         place and the order is the equivalence the swap actually claims.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The order is the assertion nothing else in the tree makes.</b> Every other test
    ///         finds a control by its class, wherever it is, so a <c>.vxml</c> edit that moved the
    ///         opacity slider in front of the blend picker — or dropped a control and left its class
    ///         on another — would be green everywhere. What an artist reads left to right is not
    ///         recoverable from a class search, and it is now the one thing a markup file can silently
    ///         change.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the channel strip is asserted to be populated, because it is the one part of
    ///         the row a region builds.</b> The ticks are a <c>@for</c>, so they are made by an
    ///         <c>Effect</c> and do not exist until the queue is drained — which is why
    ///         <c>LayerStackView.Build</c> drains it before restating. A strip with no ticks is the
    ///         shape that failure takes, and it looks like a set with no channels rather than like a
    ///         bug.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_layers_row_is_the_markup_part_in_the_place_and_the_order_the_hand_built_one_held() {
        using var fixture = new TexturingFixture();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, "Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var panel = fixture.Shell.Workspace.Open(TexturingModule.StackPanel);

        Assert.NotNull(panel);

        var list = Only(panel, "layer-stack-list");
        var row = Assert.IsType<LayerRowView>(list.Children.First(child => child is LayerRowView));

        // The place: still the element the sheet's `layer-stack-row` rule is written against, and
        // still a direct child of the list rather than wrapped in a component's own host box.
        Assert.Equal("layer-stack-row", row.Tag);
        Assert.Same(list, row.Parent);

        // ⚠ The order, read off the row by position rather than searched for by class. A named layer
        // draws no refusal, so these nine are the whole row an artist sees, left to right. The type
        // is asserted with the class because #1071's answer was to name controls by class — so the
        // class alone would be satisfied by a container carrying it.
        Assert.Equal(9, row.Children.Count);

        Named<Button>(row.Children[0], "layer-stack-select");
        Named<Button>(row.Children[1], "layer-stack-move-up");
        Named<Button>(row.Children[2], "layer-stack-move-down");
        Named<Button>(row.Children[3], "layer-stack-delete");
        Named<CheckBox>(row.Children[4], "layer-stack-enabled");
        Assert.Equal("layer-stack-row-name", row.Children[5].Tag);
        Named<Select>(row.Children[6], "layer-stack-blend");
        Named<Slider>(row.Children[7], "layer-stack-opacity");
        Assert.Equal("layer-stack-channels", row.Children[8].Tag);

        // ⚠ And the region really ran: one tick per channel of the set, which is what the drain in
        // `Build` buys and what nothing about the row's own shape would reveal.
        var ticks = row.Ticks();

        Assert.NotEmpty(ticks);
        Assert.Equal(ticks.Count, row.Children[8].Children.Count);
        Assert.All(ticks, tick => Assert.True(tick.HasClass("layer-stack-channel")));
    }

    /// <summary>⚠ The fill and filter rows are the markup parts, in place, in order, and still bound.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The next two row kinds of <a href="https://github.com/Rikarin/Vixen/issues/881">#881</a>,
    ///         held the way the layer's row is held.</b> A class search finds a control wherever it
    ///         has drifted to, so what a <c>.vxml</c> can silently change — the order an artist reads
    ///         left to right, a control dropped with its class left on another — is only recoverable
    ///         by position.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the <em>values</em>, because the shape half alone is satisfied by a port that
    ///         deleted the binding.</b> Moving five <c>Add&lt;T&gt;</c> calls into markup does not
    ///         touch <c>bindings</c>, and nothing about a correctly ordered row would say if it had:
    ///         the pickers would sit there showing their first option, which is <c>Constant</c> and
    ///         <c>Uv</c> and <c>Levels</c> — three plausible values. So this stack says <c>Graph</c>,
    ///         <c>Planar</c>, <c>Z</c> and <c>Blur</c>, none of which is any picker's default, and
    ///         the assertion is that the document reached the control.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The two conditional fields are read as geometry rather than as a style.</b>
    ///         Whether the graph path and the axis are shown is written by <c>SetStyle</c> from the
    ///         same binding, and <c>display: none</c> is the one state that occupies no box — so a
    ///         width says the write arrived, where reading the style back would say only that this
    ///         test knows what the view wrote. Planar is deliberately the projection here: it is the
    ///         one that shows the axis, so both fields are visible and their widths are the
    ///         measurement rather than the absence of one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The two labels are read through <c>Said</c> and not off their own <c>Text</c>,
    ///         because a markup port is <em>not</em> element for element where literal text is
    ///         concerned.</b> <c>label.Text = "Fill"</c> put the word on the label;
    ///         <c>&lt;layer-stack-fill-label&gt;Fill&lt;/…&gt;</c> compiles to
    ///         <c>BuildContext.Text</c>, which makes a child element tagged <c>text</c> and puts the
    ///         word on <em>that</em> — so the label's own <c>Text</c> is null and the row has one
    ///         element more than the C# it replaced. ⚠ <b>The already-landed
    ///         <c>LayerStackChrome.vxml</c> changed its three binding labels this way and nothing
    ///         noticed</b>, because <c>Said</c> walks children and every reader in this assembly goes
    ///         through it. It is a difference rather than a defect, and the difference favours the
    ///         markup: <c>ControlTheme.vcss</c> has <c>text { color: var(--text) }</c> and no rule
    ///         any label element matches, so the word is themed now and was not before.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_fill_row_and_a_filter_row_are_the_markup_parts_in_the_order_the_hand_built_ones_held() {
        using var fixture = new TexturingFixture();

        var panel = Opened(fixture);
        var list = Only(panel, "layer-stack-list");

        // The instrument first: a walk that emitted neither row kind would satisfy every `First`
        // below by throwing rather than by passing, but a *set* with no channels would leave the
        // fill's own row there and nothing under it — which is the shape a broken fixture takes.
        Assert.Equal(2, list.Children.OfType<LayerRowView>().Count());

        var fill = list.Children.OfType<FillRowView>().Single();

        // The place: still the element `layer-stack-fill-row` is written against in the sheet, and
        // still a direct child of the list rather than wrapped in a component's own host box.
        Assert.Equal("layer-stack-fill-row", fill.Tag);
        Assert.Same(list, fill.Parent);

        Assert.Equal(5, fill.Children.Count);
        Assert.Equal("layer-stack-fill-label", fill.Children[0].Tag);
        Assert.Equal("Fill", LayerStackPanelTests.Said(fill.Children[0]));
        Named<Select>(fill.Children[1], "layer-stack-fill-source");
        Named<TextBox>(fill.Children[2], "layer-stack-fill-graph");
        Named<Select>(fill.Children[3], "layer-stack-fill-projection");
        Named<Select>(fill.Children[4], "layer-stack-fill-axis");

        // The values, none of which is the first option of its own picker.
        Assert.Equal(nameof(LayerFillSource.Graph), fill.Kind.Value);
        Assert.Equal("Compounds/Noise", fill.Graph.Value);
        Assert.Equal(nameof(LayerProjection.Planar), fill.Projection.Value);
        Assert.Equal(nameof(LayerAxis.Z), fill.Axis.Value);

        Assert.True(fill.Graph.Width > 0f, "the graph path is display:none on a Graph fill.");
        Assert.True(fill.Axis.Width > 0f, "the axis picker is display:none on a Planar projection.");

        var filter = list.Children.OfType<FilterRowView>().Single();

        Assert.Equal("layer-stack-filter-row", filter.Tag);
        Assert.Same(list, filter.Parent);

        Assert.Equal(4, filter.Children.Count);
        Assert.Equal("layer-stack-filter-label", filter.Children[0].Tag);
        Assert.Equal("Filter", LayerStackPanelTests.Said(filter.Children[0]));
        Named<Select>(filter.Children[1], "layer-stack-filter-source");
        Named<Select>(filter.Children[2], "layer-stack-filter-kind");
        Named<TextBox>(filter.Children[3], "layer-stack-filter-node");

        Assert.Equal(LayerStackView.PresetFilter, filter.Source.Value);
        Assert.Equal(nameof(LayerFilterKind.Blur), filter.Kind.Value);

        Assert.True(filter.Kind.Width > 0f, "the preset picker is display:none while a preset is in force.");
        Assert.Equal(0f, filter.Node.Width);
    }

    /// <summary>A stack with one graph fill and one preset filter, drawn by the module's own panel.</summary>
    /// <param name="fixture">The shell to open it in.</param>
    /// <returns>The panel, laid out.</returns>
    /// <remarks>
    ///     ⚠ <b>Opened twice, which is how the document's own contents are replaced through the verb
    ///     rather than around it.</b> The first execute makes the <c>LayerStackDocument</c>; the
    ///     second re-shows it with the stack this test wants, so what the panel draws is what
    ///     <c>Show</c> produces from a document rather than what a fixture built by hand.
    /// </remarks>
    static UiElement Opened(TexturingFixture fixture) {
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
                            Fill = LayerFillSource.Graph,
                            Graph = "Compounds/Noise",
                            Projection = LayerProjection.Planar,
                            PlanarAxis = LayerAxis.Z
                        },
                        new() {
                            Id = "top",
                            Name = "Top",
                            Kind = LayerKind.Filter,
                            Opacity = 1f,
                            Filter = LayerFilterKind.Blur
                        }
                    ]
                }
            ]
        };

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var panel = fixture.Shell.Workspace.Open(TexturingModule.StackPanel);

        Assert.NotNull(panel);

        fixture.Shell.Document.Update();
        fixture.Shell.Document.Draw();

        return panel;
    }

    /// <summary>Asserts one child is the control it should be, under the class it answers to.</summary>
    /// <typeparam name="T">What the control has to be.</typeparam>
    /// <param name="element">The child.</param>
    /// <param name="name">The class the panel addresses it by.</param>
    static void Named<T>(UiElement element, string name) where T : UiElement {
        Assert.IsType<T>(element);
        Assert.True(element.HasClass(name), $"expected a '{name}' here and found <{element.Tag}>.");
    }

    /// <summary>The only element under that name — its tag, or its class.</summary>
    static UiElement Only(UiElement root, string tag) {
        List<UiElement> found = [];

        Walk(root);

        return Assert.Single(found);

        void Walk(UiElement element) {
            if (string.Equals(element.Tag, tag, StringComparison.Ordinal) || element.HasClass(tag)) {
                found.Add(element);
            }

            foreach (var child in element.Children) {
                Walk(child);
            }
        }
    }
}
