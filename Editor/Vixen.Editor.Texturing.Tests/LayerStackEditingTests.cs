// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.TextureGraph;
using Vixen.Editor.Texturing.Layers;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>The layers panel edits, and every edit is on the document's own undo stack.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/819">#819</a>, and the reason it waited
///         for a model.</b> Nothing in the layer stack was routed through
///         <c>EditorDocument.Stack</c>, so a panel that offered a reorder would have offered a gesture
///         with no undo and no dirty flag. What is asserted here is both halves at once: the row
///         does the thing, and the stack remembers it.
///     </para>
///     <para>
///         ⚠ <b>Every test here walks the tree the panel built and presses the control an artist
///         would press.</b> Batch 9 added a public <c>Messages</c> property with no readers and then
///         wrote its own tests against the tree instead, which is the stronger assertion and the one
///         this file keeps to: a view method called only from xunit proves that the method works, not
///         that the panel does.
///     </para>
///     <para>
///         ⚠ <b>And a reorder is checked against a compiled plan rather than against a list.</b> A
///         list assertion is a restatement of the command's own arithmetic; what a reorder actually
///         means is that a different layer's colour arrives at the last composite, and only the
///         compilation says which.
///     </para>
/// </remarks>
public class LayerStackEditingTests {
    /// <summary>Moving a layer up changes which colour the last composite reads.</summary>
    /// <remarks>
    ///     ⚠ <b>The panel draws topmost first and the file stores bottom first, so the button labelled
    ///     <em>up</em> is <c>+1</c> in the file's order.</b> A view that passed <c>-1</c> would leave
    ///     the bottom row's button doing nothing at all — <c>MoveLayerCommand.CanMove</c> answers no
    ///     at index 0 — and a view that reversed the rows instead of the deltas would move the wrong
    ///     layer. Both are invisible in a list of names and both change which uniform feeds the last
    ///     blend.
    /// </remarks>
    [Fact]
    public void Moving_a_layer_up_changes_the_compiled_composite() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Two());
        var panel = Panel(fixture);

        Assert.Equal(0.75f, TopColour(document));

        // Row 0 is the top layer and row 1 is the bottom one, so this is the lower layer being sent
        // over the upper one.
        Buttons(panel, "layer-stack-move-up")[1].Activate();

        Assert.Equal(0.25f, TopColour(document));
    }

    /// <summary>And undoing it puts the composite back.</summary>
    /// <remarks>
    ///     <b>The half that makes the panel worth opening.</b> A reorder that could not be undone is
    ///     the state <a href="https://github.com/Rikarin/Vixen/issues/819">#819</a> refused to ship,
    ///     and the document's dirty flag is the other half of the same claim: a stack edited in this
    ///     panel is a stack the editor knows to write back.
    /// </remarks>
    [Fact]
    public void A_reorder_is_one_undo_entry_and_it_reverses() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Two());
        var panel = Panel(fixture);

        Assert.False(document.IsDirty.Value);

        Buttons(panel, "layer-stack-move-up")[1].Activate();

        Assert.True(document.IsDirty.Value);
        Assert.Equal(1, document.Stack.Depth.Value);
        Assert.Equal(0.25f, TopColour(document));

        Assert.True(document.Stack.Undo());
        Assert.Equal(0.75f, TopColour(document));
        Assert.False(document.IsDirty.Value);
    }

    /// <summary>⚠ Two reorders are two entries, which is the opposite answer from the slider.</summary>
    /// <remarks>
    ///     <b>A predicate that could not be false if <c>MoveLayerCommand</c> merged.</b> An artist who
    ///     moved a layer up twice and pressed undo means to be one step down; a command type that
    ///     absorbed its predecessor would put them back where they started, and the depth would say
    ///     one.
    /// </remarks>
    [Fact]
    public void Two_reorders_are_two_undo_entries() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Three());
        var panel = Panel(fixture);

        // The bottom row twice: once past the middle layer, once past the top one.
        Buttons(panel, "layer-stack-move-up")[2].Activate();
        Buttons(panel, "layer-stack-move-up")[1].Activate();

        Assert.Equal(2, document.Stack.Depth.Value);
        Assert.Equal(0.25f, TopColour(document));

        Assert.True(document.Stack.Undo());
        Assert.Equal(0.75f, TopColour(document));
    }

    /// <summary>Choosing a blend mode puts that operator in the plan.</summary>
    [Fact]
    public void Choosing_a_blend_mode_reaches_the_plan() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Two());
        var panel = Panel(fixture);

        var blend = Find<Select>(panel, "layer-stack-blend");

        Assert.Equal(nameof(LayerBlendMode.Copy), blend.Value);

        blend.Value = nameof(LayerBlendMode.Overlay);

        Assert.Equal(
            (float)(int)LayerBlendMode.Overlay,
            Last(Compile(document), "Blend").Find("mode")!.Value.Value
        );

        Assert.True(document.Stack.Undo());
        Assert.Equal(
            (float)(int)LayerBlendMode.Copy,
            Last(Compile(document), "Blend").Find("mode")!.Value.Value
        );
    }

    /// <summary>Clearing a channel's tick stops the layer writing that channel.</summary>
    /// <remarks>
    ///     ⚠ <b>Counted in the plan rather than read off <c>LayerAsset.Channels</c>.</b> The member is
    ///     a list of names and the thing an artist means by it is a composite that does not happen —
    ///     <c>LayerStackGraph.Layer</c> returns the cursor untouched for a channel a layer does not
    ///     write, so what a cleared tick removes is a whole <c>Blend</c> op from that channel's chain.
    /// </remarks>
    [Fact]
    public void Clearing_a_channel_removes_that_channels_composite() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, TwoChannels());
        var panel = Panel(fixture);

        Assert.Equal(2, Count(Compile(document), "Blend"));

        // The second tick on the only row is 'roughness'.
        Ticks(panel, "layer-stack-channel")[1].Activate();

        Assert.Equal(1, Count(Compile(document), "Blend"));

        var layer = document.Document.Sets[0].Layers[0];

        Assert.Equal("baseColor", Assert.Single(layer.Channels));
        Assert.True(document.Stack.Undo());

        // ⚠ And it comes back as *empty*, not as the two names. Empty means every channel, so a
        // round trip that stored the list would silently stop the layer writing a channel the set
        // gained afterwards — `LayerAsset.Channels`' own argument, from the other end.
        Assert.Empty(document.Document.Sets[0].Layers[0].Channels);
        Assert.Equal(2, Count(Compile(document), "Blend"));
    }

    /// <summary>⚠ The last remaining tick cannot be cleared, and the legend says why.</summary>
    /// <remarks>
    ///     <b>The ambiguity in the file kept out of the panel.</b> Clearing the last tick would leave
    ///     <c>Channels</c> empty — and empty means <em>all</em>, so the gesture an artist reads as
    ///     "and now it writes nothing" would make the layer write everything, including channels it
    ///     was just restricted away from. The control that cannot express it is the answer; the
    ///     sentence under the rows is what makes the disabled box make sense.
    /// </remarks>
    [Fact]
    public void The_last_channel_tick_cannot_be_cleared() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, TwoChannels());
        var panel = Panel(fixture);
        var ticks = Ticks(panel, "layer-stack-channel");

        Assert.All(ticks, tick => Assert.False(tick.Disabled));

        ticks[1].Activate();

        var left = Ticks(panel, "layer-stack-channel");

        Assert.True(left[0].IsChecked);
        Assert.True(left[0].Disabled);
        Assert.False(left[1].IsChecked);
        Assert.False(left[1].Disabled);

        // Pressing it anyway is what a disabled control refuses, so the stack never sees a second
        // entry and the layer still writes one channel.
        left[0].Activate();

        Assert.Equal(1, document.Stack.Depth.Value);
        Assert.Equal("baseColor", Assert.Single(document.Document.Sets[0].Layers[0].Channels));

        var legend = Element(panel, "layer-stack-legend");

        Assert.Contains("unrestricted", legend.Text ?? "", StringComparison.Ordinal);
        Assert.Contains("switched off", legend.Text ?? "", StringComparison.Ordinal);
    }

    /// <summary>A drag of the opacity slider is one undo entry, and the next drag is another.</summary>
    /// <remarks>
    ///     ⚠ <b>Both halves, because either alone is satisfied by a bug.</b> A command that never
    ///     merged would put one entry per frame in the history — three hundred for one drag — and one
    ///     that merged unconditionally would fold every drag an artist ever makes into the first,
    ///     so undo would jump back to the value the layer had when the file was opened.
    ///     <c>CommandStack.Seal</c> is what separates them and it is explicit rather than a time
    ///     window, so the panel has to call it: this is that call, made the way a pointer release
    ///     makes it.
    /// </remarks>
    [Fact]
    public void An_opacity_drag_is_one_undo_entry_and_the_next_drag_is_another() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Two());
        var panel = Panel(fixture);
        var slider = Find<Slider>(panel, "layer-stack-opacity");

        slider.Value = 0.8f;
        slider.Value = 0.6f;
        slider.Value = 0.4f;

        Assert.Equal(1, document.Stack.Depth.Value);
        Assert.Equal(0.4f, Top(document).Opacity, 5);

        document.Stack.Seal();

        slider.Value = 0.2f;

        Assert.Equal(2, document.Stack.Depth.Value);

        // ⚠ The merged entry undoes to the value before the *drag*, not to the value one frame ago.
        Assert.True(document.Stack.Undo());
        Assert.Equal(0.4f, Top(document).Opacity, 5);

        Assert.True(document.Stack.Undo());
        Assert.Equal(1f, Top(document).Opacity, 5);
    }

    /// <summary>⚠ And letting go of the slider is what seals it, which nothing else does.</summary>
    /// <remarks>
    ///     <b>The production half of the test above, and it is a different claim.</b>
    ///     <c>CommandStack.Seal</c> is explicit rather than a time window, so somebody has to call it
    ///     — and until this line nothing in the panel did, which would have folded every drag an
    ///     artist ever made on one layer into the first entry. Raising the pointer event is the route
    ///     a real release takes; asserting on <c>Seal()</c> called by the test would prove only that
    ///     <c>CommandStack</c> works.
    /// </remarks>
    [Fact]
    public void Letting_go_of_the_opacity_slider_starts_a_new_undo_entry() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Two());
        var panel = Panel(fixture);
        var slider = Find<Slider>(panel, "layer-stack-opacity");

        slider.Value = 0.8f;
        slider.Value = 0.6f;

        Assert.Equal(1, document.Stack.Depth.Value);

        // ⚠ Pressed first, and that is the point of this test rather than a detail of it. `Range`
        // marks the release that ends a drag as handled, so a bare Released — no press, `dragging`
        // still false — takes the default branch and reaches a bubbling handler that a real one
        // never would. The seal was registered without `handledEventsToo` and this test passed.
        slider.Raise(new PointerEvent { Action = PointerAction.Pressed, Button = PointerButton.Primary });
        slider.Raise(new PointerEvent { Action = PointerAction.Released, Button = PointerButton.Primary });

        slider.Value = 0.4f;

        Assert.Equal(2, document.Stack.Depth.Value);
    }

    /// <summary>⚠ An edit re-evaluates the map, which is the one line in the module.</summary>
    /// <remarks>
    ///     <b>Without it this whole panel is a finished thing nothing calls.</b>
    ///     <c>LayerStackView</c> holds no evaluator and redraws its own rows, so every assertion above
    ///     passes against a module that never subscribed to <c>Edited</c> — and an artist would see
    ///     the row move and the picture stay. What is read here is a message that can only appear if
    ///     the stack was compiled <em>again</em> after the edit: the layer switched on is a Paint
    ///     layer with no canvas yet, which <c>LayerStackGraph</c> has something to say about by name.
    ///     <para>
    ///         ⚠ <b>A warning rather than an error, and the severity is not incidental.</b> This was
    ///         written when a Paint layer was refused outright; #852 wired the layer kind and chose to
    ///         warn instead, because <c>LayerStackCompiler</c> throws the whole plan away on any
    ///         error — so a Paint layer that refused until its first stroke would blank every other
    ///         layer's preview at the one moment it can happen, which is when a panel has just
    ///         created one. What this test needs is only that the message is <em>new</em>, and a
    ///         warning is as new as an error.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_edit_re_evaluates_the_map() {
        using var fixture = new TexturingFixture(graphics: true);

        Open(
            fixture,
            Stack(
                [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
                Fill("bottom", "Bottom", 0.25f),
                new LayerAsset { Id = "paint", Name = "Paint", Kind = LayerKind.Paint, Enabled = false }
            )
        );

        var panel = Panel(fixture);

        Assert.Empty(Texts(panel, "layer-stack-message"));

        // The top row is the paint layer, and its tick is the first `layer-stack-enabled` in the tree.
        Ticks(panel, "layer-stack-enabled")[0].Activate();

        var message = Assert.Single(Texts(panel, "layer-stack-message"));

        Assert.StartsWith("Warning", message, StringComparison.Ordinal);
        Assert.Contains("paint", message, StringComparison.Ordinal);
    }

    /// <summary>A mask's own entries are rows in the same list, outermost first.</summary>
    /// <remarks>
    ///     <b>Doc 48 § D10's "a mask is itself a small stack", on the screen.</b> The panel listed
    ///     neither the base nor the entries before this, so a layer with a two-entry mask looked
    ///     exactly like a layer with none.
    /// </remarks>
    [Fact]
    public void A_masks_entries_are_rows_under_their_layer() {
        using var fixture = new TexturingFixture();

        Open(fixture, Masked());

        var lines = Texts(Panel(fixture), "layer-stack-mask-name");

        Assert.Equal(2, lines.Count);

        // The entry composites over the base, so it is the outer one and is listed first — the same
        // rule the layer rows follow, and the reverse of the file.
        Assert.StartsWith("Mask — Constant 0.5, Multiply", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("Mask base — Bake 'curvature'", lines[1], StringComparison.Ordinal);
    }

    /// <summary>And switching a mask entry off removes its composite from the plan.</summary>
    [Fact]
    public void Switching_a_mask_entry_off_removes_its_composite() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Masked());
        var panel = Panel(fixture);

        var before = Count(Compile(document), "Blend");

        Ticks(panel, "layer-stack-mask-enabled")[0].Activate();

        var after = Count(Compile(document), "Blend");

        // The entry's own `Colour/Blend` and nothing else: the layer's composite and the mask's
        // product multiply are still there.
        Assert.Equal(before - 1, after);
        Assert.False(document.Document.Sets[0].Layers[0].Mask.Layers[0].Enabled);

        Assert.True(document.Stack.Undo());
        Assert.Equal(before, Count(Compile(document), "Blend"));
    }

    /// <summary>⚠ A group's children are rows too, and they reorder inside the group.</summary>
    /// <remarks>
    ///     <b>A list that stopped at the top level was honest while nothing could be moved.</b> It
    ///     stops being honest the moment there is an <em>up</em> button, because a layer inside a
    ///     group is then one an artist cannot reach — and <c>LayerStackEdit</c> already reorders
    ///     inside whichever list a layer is really in, so the gap was entirely in the view.
    /// </remarks>
    [Fact]
    public void A_group_child_is_a_row_and_moves_inside_its_group() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Grouped());
        var panel = Panel(fixture);

        var rows = Texts(panel, "layer-stack-row-name");

        Assert.Equal(3, rows.Count);
        Assert.StartsWith("Group", rows[0], StringComparison.Ordinal);
        Assert.Contains("Upper", rows[1], StringComparison.Ordinal);
        Assert.Contains("Lower", rows[2], StringComparison.Ordinal);

        Buttons(panel, "layer-stack-move-up")[2].Activate();

        var children = document.Document.Sets[0].Layers[0].Children;

        Assert.Equal("upper", children[0].Id);
        Assert.Equal("lower", children[1].Id);

        // ⚠ Inside the group and not out of it: the group still holds both, which is what makes the
        // move a reorder rather than a reparent — a gesture this panel deliberately does not offer.
        Assert.Equal(2, children.Count);
        Assert.Single(document.Document.Sets[0].Layers);
    }

    /// <summary>The panel's own rows survive an edit that did not change their shape.</summary>
    /// <remarks>
    ///     ⚠ <b>The property a rebuild-on-every-refresh cannot have, and it is not a saving.</b> Every
    ///     refresh runs on every evaluation, so rebuilding unconditionally removes the control under
    ///     the artist's captured pointer and a slider stops mid-drag. What this reads is that the
    ///     element is the same object after an edit that changed only a value, and a different one
    ///     after an edit that changed the row set.
    /// </remarks>
    [Fact]
    public void A_value_edit_keeps_the_row_and_a_reorder_rebuilds_it() {
        using var fixture = new TexturingFixture();

        Open(fixture, Two());

        var panel = Panel(fixture);
        var slider = Find<Slider>(panel, "layer-stack-opacity");

        slider.Value = 0.5f;

        Assert.Same(slider, Find<Slider>(panel, "layer-stack-opacity"));

        Buttons(panel, "layer-stack-move-up")[1].Activate();

        Assert.NotSame(slider, Find<Slider>(panel, "layer-stack-opacity"));
    }

    /// <summary>⚠ An edit made in the panel is in the file after a save.</summary>
    /// <remarks>
    ///     <b>The sentence <a href="https://github.com/Rikarin/Vixen/issues/819">#819</a> was written
    ///     to avoid</b> — "a panel that let an artist drag a row and quietly dropped it on save would
    ///     be worse than one that does not offer the drag". Every other test here reads the document
    ///     in memory, which is exactly the half a dropped save would still satisfy; this one reads
    ///     the bytes back off disk through the same YAML the next session will.
    /// </remarks>
    [Fact]
    public void An_edit_is_in_the_file_after_a_save() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Two());
        var panel = Panel(fixture);

        Buttons(panel, "layer-stack-move-up")[1].Activate();

        document.Save();

        Assert.False(document.IsDirty.Value);

        var written = LayerStackYaml.Read(File.ReadAllText(document.AssetPath));
        var layers = written.Sets[0].Layers;

        Assert.Equal("top", layers[0].Id);
        Assert.Equal("bottom", layers[1].Id);
    }

    /// <summary>⚠ A second stack of the same shape gets its own rows, not the first one's.</summary>
    /// <remarks>
    ///     <b>A shape is not an identity, and the rebuild rule is written on the shape.</b> Every
    ///     control on a row closes over the document it was built for, and two stacks with the same
    ///     layer ids, kinds and channels — which is every pair made from
    ///     <c>LayerStackDocument.Starter</c> — produce the same signature. Without the identity check
    ///     beside it the second stack would be shown with the first one's rows, and every edit would
    ///     land in a file that is no longer open: an edit that appears to do nothing, and a dirty
    ///     flag on the wrong document.
    /// </remarks>
    [Fact]
    public void A_second_stack_of_the_same_shape_gets_its_own_rows() {
        using var fixture = new TexturingFixture();
        var first = Open(fixture, Two());
        var panel = Panel(fixture);

        var second = Another(fixture, "Keel", Two());

        Buttons(panel, "layer-stack-move-up")[1].Activate();

        Assert.Equal(0, first.Stack.Depth.Value);
        Assert.Equal(1, second.Stack.Depth.Value);
        Assert.Equal(0.75f, TopColour(first));
        Assert.Equal(0.25f, TopColour(second));
    }

    /// <summary>The colour the last composite reads, which is what "topmost" means arithmetically.</summary>
    static float TopColour(LayerStackDocument document) {
        var plan = Compile(document);
        var blend = Last(plan, "Blend");

        foreach (var op in plan.Ops) {
            if (string.Equals(op.Kernel, "Uniform", StringComparison.Ordinal) && op.Output == blend.Inputs[1]) {
                return op.Find("red")!.Value.Value;
            }
        }

        Assert.Fail("the last blend's foreground is not a uniform");

        throw new InvalidOperationException("unreachable");
    }

    static LayerAsset Top(LayerStackDocument document) {
        var layers = document.Document.Sets[0].Layers;

        return layers[^1];
    }

    static TexturePlan Compile(LayerStackDocument document) {
        var compilation = LayerStackCompiler.Compile(document.Document, document.Document.Sets[0]);

        Assert.NotNull(compilation.Plan);

        return compilation.Plan;
    }

    static int Count(TexturePlan plan, string kernel) {
        var count = 0;

        foreach (var op in plan.Ops) {
            if (string.Equals(op.Kernel, kernel, StringComparison.Ordinal)) {
                count++;
            }
        }

        return count;
    }

    static TextureOp Last(TexturePlan plan, string kernel) {
        for (var index = plan.Ops.Length - 1; index >= 0; index--) {
            if (string.Equals(plan.Ops[index].Kernel, kernel, StringComparison.Ordinal)) {
                return plan.Ops[index];
            }
        }

        Assert.Fail($"no '{kernel}' op in this plan");

        throw new InvalidOperationException("unreachable");
    }

    static LayerStackAsset Two() =>
        Stack(
            [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
            Fill("bottom", "Bottom", 0.25f),
            Fill("top", "Top", 0.75f)
        );

    static LayerStackAsset Three() =>
        Stack(
            [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
            Fill("bottom", "Bottom", 0.25f),
            Fill("middle", "Middle", 0.5f),
            Fill("top", "Top", 0.75f)
        );

    static LayerStackAsset TwoChannels() =>
        Stack(
            [
                new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] },
                new() { Usage = "roughness", Default = [0f, 0f, 0f, 1f] }
            ],
            new LayerAsset {
                Id = "l",
                Name = "Layer",
                Kind = LayerKind.Fill,
                Values = { ["baseColor"] = [0.25f, 0.25f, 0.25f, 1f], ["roughness"] = [0.5f, 0.5f, 0.5f, 1f] }
            }
        );

    static LayerStackAsset Masked() =>
        Stack(
            [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
            new LayerAsset {
                Id = "l",
                Name = "Layer",
                Kind = LayerKind.Fill,
                Values = { ["baseColor"] = [0.25f, 0.25f, 0.25f, 1f] },
                Mask = new() {
                    Source = LayerMaskSource.Bake,
                    Map = "curvature",
                    Layers = [
                        new() { Source = LayerMaskSource.Constant, Value = 0.5f, Blend = LayerBlendMode.Multiply }
                    ]
                }
            }
        );

    static LayerStackAsset Grouped() =>
        Stack(
            [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
            new LayerAsset {
                Id = "g",
                Name = "Group",
                Kind = LayerKind.Group,
                Children = [Fill("lower", "Lower", 0.25f), Fill("upper", "Upper", 0.75f)]
            }
        );

    static LayerAsset Fill(string id, string name, float grey) =>
        new() {
            Id = id,
            Name = name,
            Kind = LayerKind.Fill,
            Values = { ["baseColor"] = [grey, grey, grey, 1f] }
        };

    static LayerStackAsset Stack(List<ChannelAsset> channels, params LayerAsset[] layers) =>
        new() {
            Name = "Hull",
            BaseWidth = 32,
            BaseHeight = 32,
            Seed = 7u,
            Sets = [new() { Name = "S", Channels = channels, Layers = [.. layers] }]
        };

    /// <summary>Opens a stack through the verb, puts a made one in it, and shows the panel.</summary>
    /// <remarks>
    ///     ⚠ <b>Through the module's own command, twice.</b> Assigning
    ///     <c>LayerStackDocument.Document</c> is what a test can do that a person cannot; running the
    ///     verb again is what puts the assigned stack in front of the panel, and it is the same route
    ///     <c>LayerStackPanelTests</c> takes for the same reason — a view a test constructed would
    ///     pass in an editor where the panel was never registered.
    /// </remarks>
    static LayerStackDocument Open(TexturingFixture fixture, LayerStackAsset stack) {
        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, "Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var document = Assert.IsType<LayerStackDocument>(fixture.Project.Documents.Single());

        document.Document = stack;

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        return document;
    }

    /// <summary>Opens a second stack in the same panel, the way a person opens a second file.</summary>
    /// <remarks>
    ///     ⚠ <b>The file is written with the stack already in it, and that is what makes the test
    ///     about the identity rather than about the shape.</b> Opening an <em>empty</em>
    ///     <c>.vxlayers</c> gives a document holding <c>LayerStackDocument.Starter</c>'s one layer,
    ///     so assigning the real stack afterwards changes the row shape and forces a rebuild for a
    ///     reason that has nothing to do with which document is open. Reading the second file gives
    ///     the panel two stacks of identical shape back to back, which is the state the check exists
    ///     for.
    /// </remarks>
    static LayerStackDocument Another(TexturingFixture fixture, string name, LayerStackAsset stack) {
        var relative = "Assets/" + name + LayerStackDocument.Extension;

        File.WriteAllText(fixture.Paths.Absolute(relative), LayerStackYaml.Write(stack));

        var report = fixture.Project.Assets.Scan();

        Assert.DoesNotContain(report.Issues, issue => issue.Kind != AssetIssueKind.MetaCreated);
        Assert.True(fixture.Project.Assets.TryGetByPath(relative, out var entry));

        fixture.Project.Selection.Set(entry.Guid);

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        return Assert.IsType<LayerStackDocument>(
            fixture.Project.Documents.Single(open => open.Asset == entry.Guid)
        );
    }

    static UiElement Panel(TexturingFixture fixture) {
        var panel = fixture.Shell.Workspace.Open(TexturingModule.StackPanel);

        Assert.NotNull(panel);

        return panel;
    }

    static UiElement Element(UiElement root, string tag) {
        var found = All(root, tag);

        Assert.NotEmpty(found);

        return found[0];
    }

    static T Find<T>(UiElement root, string tag) where T : UiElement {
        var found = All(root, tag);

        Assert.NotEmpty(found);

        return Assert.IsType<T>(found[0]);
    }

    static List<Button> Buttons(UiElement root, string tag) {
        List<Button> found = [];

        foreach (var element in All(root, tag)) {
            found.Add(Assert.IsType<Button>(element));
        }

        return found;
    }

    static List<CheckBox> Ticks(UiElement root, string tag) {
        List<CheckBox> found = [];

        foreach (var element in All(root, tag)) {
            found.Add(Assert.IsType<CheckBox>(element));
        }

        return found;
    }

    static List<string> Texts(UiElement root, string tag) {
        List<string> found = [];

        foreach (var element in All(root, tag)) {
            found.Add(element.Text ?? "");
        }

        return found;
    }

    /// <summary>Every element with that tag, in the order the panel laid them out.</summary>
    static List<UiElement> All(UiElement root, string tag) {
        List<UiElement> found = [];

        Walk(root);

        return found;

        void Walk(UiElement element) {
            if (string.Equals(element.Tag, tag, StringComparison.Ordinal)) {
                found.Add(element);
            }

            foreach (var child in element.Children) {
                Walk(child);
            }
        }
    }
}
