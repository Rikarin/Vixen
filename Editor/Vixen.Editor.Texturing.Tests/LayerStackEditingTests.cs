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
    /// <summary>Where <see cref="Viewed" /> puts a view of its own, beside the module's.</summary>
    const string ViewPanel = "texturing.tests.layer-stack";

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

    /// <summary>⚠ An undo taken outside the panel puts the document's value back on the control.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/933">#933</a>, and it is the defect
    ///         the whole undoable model was built for.</b> Every other test in this file presses a
    ///         control and then reads the <em>document</em> — so all of them are green against a
    ///         panel that never reads the document back. What a person does is press Ctrl+Z, which
    ///         reaches <c>CommandStack.Undo</c> through the editor's own verb and not through
    ///         anything in these rows.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The flush is what a frame does, and it is not test ceremony.</b> Writing a signal
    ///         only queues; <c>EditorShell</c> drains its own document's queue once per frame —
    ///         <c>UiDocument.Effects</c> says why it is the document's queue and not the thread's —
    ///         so a test that asserted without flushing would be asserting that the refresh happened
    ///         at a moment the editor never assigns work to.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_undo_taken_outside_the_panel_reaches_the_controls() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Two());
        var panel = Panel(fixture);

        // The top row's tick, pressed the way an artist presses it.
        Ticks(panel, "layer-stack-enabled")[0].Activate();

        Assert.False(Ticks(panel, "layer-stack-enabled")[0].IsChecked);
        Assert.False(Top(document).Enabled);

        Assert.True(document.Stack.Undo());
        fixture.Shell.Document.Effects.Flush();

        // ⚠ The document, first: an assertion on the tick alone would pass against a panel that had
        // simply failed to write the edit through in the first place.
        Assert.True(Top(document).Enabled);
        Assert.True(Ticks(panel, "layer-stack-enabled")[0].IsChecked);
    }

    /// <summary>⚠ And an undone reorder puts the rows back in the order the file has them.</summary>
    /// <remarks>
    ///     <b>The other half of <a href="https://github.com/Rikarin/Vixen/issues/933">#933</a>, and a
    ///     different code path.</b> A value edit is re-read by the row's own binding; a reorder
    ///     changes the shape signature, so what has to run is the rebuild. A panel that re-read its
    ///     values and never rebuilt would pass the test above and leave the layers on screen in an
    ///     order the file no longer has.
    /// </remarks>
    [Fact]
    public void An_undone_reorder_puts_the_rows_back() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Two());
        var panel = Panel(fixture);

        Buttons(panel, "layer-stack-move-up")[1].Activate();

        Assert.StartsWith("Bottom", Texts(panel, "layer-stack-row-name")[0], StringComparison.Ordinal);

        Assert.True(document.Stack.Undo());
        fixture.Shell.Document.Effects.Flush();

        Assert.StartsWith("Top", Texts(panel, "layer-stack-row-name")[0], StringComparison.Ordinal);
    }

    /// <summary>What a mask row reads can be changed, and the base can be switched off and back on.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/882">#882</a>.</b> The panel listed
    ///         a mask's entries and its base as sentences with no control on them, so an artist could
    ///         see that a layer had a generator mask and could not point it anywhere else.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And back on is the half that decides the row is drawn unconditionally.</b>
    ///         Switching a base off means setting its source to <c>None</c>; the base row used to
    ///         exist only when the source was not <c>None</c>, so the one gesture that turns a mask
    ///         off would have removed the control that turns it back on.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_masks_base_source_changes_and_switches_off_and_back_on() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Masked());
        var panel = Panel(fixture);

        // The entry's editor comes first and the base's is last, which is the order the rows are in.
        var sources = Controls<Select>(panel, "layer-stack-mask-source");

        Assert.Equal(2, sources.Count);
        Assert.Equal(nameof(LayerMaskSource.Bake), sources[1].Value);

        sources[1].Value = nameof(LayerMaskSource.None);

        Assert.Equal(LayerMaskSource.None, Mask(document).Source);
        Assert.Equal(1, document.Stack.Depth.Value);

        // ⚠ The row is still there with its selector on it, which is the whole reason it is drawn
        // for a mask that reads nothing.
        Controls<Select>(panel, "layer-stack-mask-source")[^1].Value = nameof(LayerMaskSource.Bake);

        Assert.Equal(LayerMaskSource.Bake, Mask(document).Source);

        // ⚠ And the map survived the round trip through None: switching a source is not a reset, so
        // an artist who turns a mask off and on again has the mask they had.
        Assert.Equal("curvature", Mask(document).Map);

        Assert.True(document.Stack.Undo());
        Assert.Equal(LayerMaskSource.None, Mask(document).Source);
    }

    /// <summary>A constant mask's number is a slider, and a drag is one undo entry.</summary>
    /// <remarks>
    ///     ⚠ <b>The merge key is per row rather than per layer, which is what the slot in it buys.</b>
    ///     A layer's mask base and its entries all share one <c>LayerPath</c>, so a key of
    ///     <c>mask-value</c> alone would collapse a drag on one row into the drag on another and
    ///     undo both at once.
    /// </remarks>
    [Fact]
    public void A_constant_masks_number_is_editable() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Masked());
        var panel = Panel(fixture);

        var numbers = Controls<Slider>(panel, "layer-stack-mask-value");

        Assert.Equal(0.5f, numbers[0].Value);

        numbers[0].Value = 0.25f;

        Assert.Equal(0.25f, Mask(document).Layers[0].Value);
        Assert.Equal(1, document.Stack.Depth.Value);

        Assert.True(document.Stack.Undo());
        Assert.Equal(0.5f, Mask(document).Layers[0].Value);
    }

    /// <summary>A bake mask's map is typed, and what is typed is what the compile reads.</summary>
    /// <remarks>
    ///     ⚠ <b>A field and not a list of the nine, which is a limit rather than a preference.</b>
    ///     <c>TextureMeshMaps.Known</c> is <c>internal</c> to <c>Vixen.Editor.TextureGraph</c> and
    ///     visible to its own tests alone, so this assembly cannot ask for the names — and writing
    ///     them here would be the second transcription of a known set that five roll calls in this
    ///     workstream have gone red on. The assertion is therefore that a typed name reaches the
    ///     plan's external, which is the thing an artist is actually after.
    /// </remarks>
    [Fact]
    public void A_bake_masks_map_is_typed_and_reaches_the_external() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Masked());
        var panel = Panel(fixture);

        var reference = Controls<TextBox>(panel, "layer-stack-mask-text")[^1];

        Assert.Equal("curvature", reference.Value);

        reference.Value = "thickness";

        Assert.Equal("thickness", Mask(document).Map);

        var compilation = LayerStackCompiler.Compile(document.Document, document.Document.Sets[0]);

        Assert.Contains(compilation.Externals, external => external.Asset.EndsWith("thickness", StringComparison.Ordinal));

        Assert.True(document.Stack.Undo());
        Assert.Equal("curvature", Mask(document).Map);
    }

    /// <summary>⚠ The anchor picker offers the layers whose result exists before this one's.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>An anchor onto a layer at or above its own is a loop</b>, which
    ///         <c>LayerStackGraph.Anchors</c> refuses through the graph model. A picker that offered
    ///         one would be a dropdown every entry of which fails, so what it offers is what the
    ///         model accepts.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Composite order and not row order, and a group is where the two differ.</b> A
    ///         group's blend node is emitted <em>after</em> its children's, so a child may anchor
    ///         onto nothing in its own group — which a picker built on the panel's top-to-bottom
    ///         order would get exactly backwards.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_anchor_picker_offers_only_the_layers_below() {
        using var fixture = new TexturingFixture();

        Open(fixture, Anchored());

        var panel = Panel(fixture);
        // The rows are topmost first, so the mask pickers are the top layer's, the middle's and the
        // bottom's — and only the middle one's mask is an anchor, so only it is filled.
        var picker = Controls<Select>(panel, "layer-stack-mask-anchor")[1];

        var offered = picker.Options.Select(option => option.Value ?? "").ToArray();

        // Neither 'middle', which is itself, nor 'top', which is composited after it.
        Assert.Equal([LayerStackView.NoAnchor, "bottom"], offered);
        Assert.Equal("bottom", picker.Value);

        picker.Value = LayerStackView.NoAnchor;

        var document = fixture.Project.Documents.OfType<LayerStackDocument>().Single();

        Assert.Equal("", document.Document.Sets[0].Layers[1].Mask.Anchor);
    }

    /// <summary>⚠ And the bottom layer of a stack is offered nothing to anchor onto.</summary>
    /// <remarks>
    ///     <b>The predicate that could not be false if the picker simply listed the set.</b> Every
    ///     assertion above is satisfied by a picker that offered every layer except the one holding
    ///     it; the bottom layer is the case where "everything else" and "everything below" differ by
    ///     the whole list.
    /// </remarks>
    [Fact]
    public void The_bottom_layers_anchor_picker_offers_nothing() {
        using var fixture = new TexturingFixture();

        Open(fixture, AnchoredFromTheBottom());

        var picker = Controls<Select>(Panel(fixture), "layer-stack-mask-anchor")[^1];

        // ⚠ The stored anchor is kept as an option even though nothing below can be named, so that
        // the picker does not read as unanchored and then unanchor it on the next click — the mesh
        // picker's three-state rule. What is not there is a layer this one could legally read.
        var offered = picker.Options.Select(option => option.Value ?? "").ToArray();

        Assert.Equal([LayerStackView.NoAnchor, "top"], offered);
        Assert.Empty(LayerStackView.Anchorable(fixture.Project.Documents.OfType<LayerStackDocument>()
            .Single()
            .Document.Sets[0], "bottom"));
    }

    /// <summary>⚠ A layer inside a group is not offered its own group, which is a cycle.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/980">#980</a>: the two tests above
    ///         run on flat stacks, and their own remark says a group is where composite order and row
    ///         order differ.</b> On a flat stack the two <em>are</em> the same list, so neither could
    ///         tell the post-order walk from any other ordering, and <c>Anchorable</c>'s group
    ///         handling — the only non-obvious part of it — was unasserted.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The distinguishing option is the group itself, and nothing else in this fixture
    ///         is.</b> <c>LayerStackGraph.Stack</c> composites a group's children <em>inside</em> the
    ///         group's own composite, so the group's blend node exists only after every child's — a
    ///         child reading it is a loop. A walk that emitted a parent before recursing into it, which
    ///         is the obvious way to write this, offers <c>'g'</c> to <c>'child'</c>; the panel's own
    ///         top-to-bottom row order offers the same set as the correct answer here, so the group is
    ///         the one option that separates right from wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the group <em>is</em> offered its own children, which reads backwards and is
    ///         the same fact.</b> Its blend node is emitted last, so anchoring onto a child of its own
    ///         is no cycle at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_layer_inside_a_group_is_not_offered_its_own_group() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, AnchoredInsideAGroup());
        var set = document.Document.Sets[0];

        // The only anchored row in the fixture, so the only picker with anything in it — found that
        // way rather than by index, because "which row is the child's" is what the walk decides.
        var picker = Assert.Single(
            Controls<Select>(Panel(fixture), "layer-stack-mask-anchor"),
            one => one.Options.Any()
        );

        var offered = picker.Options.Select(option => option.Value ?? "").ToArray();

        // Not 'g', which contains it; not 'top', which is composited after the whole group.
        Assert.Equal([LayerStackView.NoAnchor, "bottom", "inner"], offered);
        Assert.Equal("inner", picker.Value);

        // The reverse reading, off the model: the group may anchor onto the children it holds.
        Assert.Equal(["bottom", "inner", "child"], LayerStackView.Anchorable(set, "g"));

        // And the layer above the whole group is offered every one of them.
        Assert.Equal(["bottom", "inner", "child", "g"], LayerStackView.Anchorable(set, "top"));
    }

    /// <summary>⚠ An anchor picker walks the set once, however often its row is refreshed.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/979">#979</a>.</b> The picker
    ///         cached what it last offered — and the cache guarded only <c>ClearOptions</c>, while the
    ///         walk that <em>produces</em> the options ran first and built the key it was compared on.
    ///         So every anchor-masked row walked the whole layer tree and allocated three collections
    ///         on every refresh, and a refresh is once per frame of an opacity drag.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves, because a picker that stopped building options at all would leave the
    ///         count at zero and read as a perfect result.</b> The options are asserted after the
    ///         refreshes, so "the work was not repeated" is a claim about work that happened.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A count of work rather than a duration.</b> A wall-clock budget calibrated on an
    ///         idle laptop is this repository's largest flake source; what the fix claims is a number
    ///         of walks, so that is what is read.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_anchor_picker_walks_the_set_once_however_often_the_row_refreshes() {
        using var fixture = new TexturingFixture();
        var (view, document) = Viewed(fixture, Anchored());

        Assert.Equal(1, view.AnchorWalks);

        // What an opacity drag does: the document is unchanged, so the rows are not rebuilt and every
        // binding re-reads. Ten frames of one gesture.
        for (var frame = 0; frame < 10; frame++) {
            view.Show(document);
        }

        Assert.Equal(1, view.AnchorWalks);

        var picker = Assert.Single(
            Controls<Select>(view.Root, "layer-stack-mask-anchor"),
            one => one.Options.Any()
        );

        Assert.Equal(
            [LayerStackView.NoAnchor, "bottom"],
            picker.Options.Select(option => option.Value ?? "").ToArray()
        );

        Assert.Equal("bottom", picker.Value);
    }

    /// <summary>⚠ A refresh keeps the artist's zoom, and a different stack is framed afresh.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The second half of <a href="https://github.com/Rikarin/Vixen/issues/979">#979</a>,
    ///         and it is <a href="https://github.com/Rikarin/Vixen/issues/957">#957</a>'s defect in
    ///         the panel #957 did not touch.</b> <c>Show</c> ended in a bare <c>Preview.Fit()</c>,
    ///         which overwrites <c>Zoom</c> and <c>Pan</c> outright — and <c>Show</c> runs on every
    ///         edit, so an artist who had zoomed into a corner of the map to see what an opacity drag
    ///         did lost it on the first frame of the drag.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves, because "never fit" passes the first one on its own</b> — and never
    ///         fitting is the worse defect: <c>Fit</c> answers false before the first layout, which is
    ///         when a panel's first <c>Show</c> runs, so a view that framed once and gave up would
    ///         open every stack at whatever zoom nothing set. The zoom the artist is given is
    ///         deliberately not the fitted one, so "it was left alone" is a statement about a number
    ///         rather than a coincidence.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Refreshing_the_panel_keeps_the_zoom_and_showing_another_stack_refits() {
        using var fixture = new TexturingFixture();
        var (view, document) = Viewed(fixture, Two());

        // ⚠ The instrument, and it is the failure this fix could have introduced. `Viewed`'s own
        // first `Show` runs before the panel is laid out, so `Fit` answered false and framed nothing;
        // the `Show` inside it after the layout is the retry. A view that gave up leaves these equal.
        var unframed = view.Preview.Zoom;

        view.Show(document);

        var framed = view.Preview.Zoom;

        Assert.True(
            framed > 0f && framed != unframed,
            $"the preview was never framed — it is still at {unframed}, which is the zoom nothing set"
        );

        view.Preview.Zoom = framed * 4f;
        view.Preview.Pan = new(11f, 13f);

        // What an edit does: the same stack, recompiled.
        view.Show(document);

        Assert.Equal(framed * 4f, view.Preview.Zoom);
        Assert.Equal(new(11f, 13f), view.Preview.Pan);

        // A different stack is a different picture, and is framed.
        view.Show(Another(fixture, "Tiles", Two()));

        Assert.Equal(framed, view.Preview.Zoom);
    }

    /// <summary>⚠ A row whose id names two layers is listed and carries no controls.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/893">#893</a>'s panel half.</b>
    ///         <c>LayerStackGraph.Duplicates</c> refuses the stack, and a refusal is a message beside
    ///         a list of rows that are still drawn and still clicked — the panel builds its rows from
    ///         the document rather than from a compilation. Against the code before this,
    ///         <c>Buttons(panel, "layer-stack-move-up")[0].Activate()</c> moved the <em>other</em>
    ///         layer, because <c>LayerStackEdit</c> resolves an id to the first match.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both rows are still there, which is half the assertion.</b> Refusing by dropping
    ///         the row would leave an artist with a file whose shape they cannot see, and the shape
    ///         is the thing they have to fix.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_row_whose_id_names_two_layers_is_listed_without_controls() {
        using var fixture = new TexturingFixture();

        Open(fixture, Shared());

        var panel = Panel(fixture);

        Assert.Equal(2, Texts(panel, "layer-stack-row-name").Count);

        // Not one button, one tick, one slider or one selector between them: every control on this
        // row is addressed by the id that names both layers.
        Assert.Empty(All(panel, "layer-stack-move-up"));
        Assert.Empty(All(panel, "layer-stack-move-down"));
        Assert.Empty(All(panel, "layer-stack-enabled"));
        Assert.Empty(All(panel, "layer-stack-opacity"));
        Assert.Empty(All(panel, "layer-stack-blend"));
        Assert.Empty(All(panel, "layer-stack-select"));

        var refusals = Texts(panel, "layer-stack-row-refusal");

        Assert.Equal(2, refusals.Count);
        Assert.Contains("dup", refusals[0], StringComparison.Ordinal);
        Assert.Equal(LayerStackView.Ambiguity("dup"), refusals[0]);
    }

    /// <summary>⚠ And the same panel over a stack with distinct ids is fully editable.</summary>
    /// <remarks>
    ///     <b>The half that makes the assertion above a finding rather than a description of a panel
    ///     that draws no controls at all.</b> Every <c>Assert.Empty</c> above is satisfied by a build
    ///     in which the rows were never populated, which is what a refusal written one line too high
    ///     in <c>Build</c> would produce.
    /// </remarks>
    [Fact]
    public void A_stack_with_distinct_ids_keeps_every_control() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Two());
        var panel = Panel(fixture);

        Assert.Equal(2, Buttons(panel, "layer-stack-move-up").Count);
        Assert.Empty(Texts(panel, "layer-stack-row-refusal"));

        Buttons(panel, "layer-stack-move-up")[1].Activate();

        Assert.Equal(0.25f, TopColour(document));
    }

    /// <summary>⚠ The panel disarms exactly the ids the compiler refuses, and no others.</summary>
    /// <remarks>
    ///     <b>The two are one rule — <c>LayerStackEdit.Ambiguous</c> — and this is what says so.</b>
    ///     A panel with its own copy of the rule can drift into either failure: offering to reorder a
    ///     layer the compiler will not build, or disarming a row of a stack that compiles. The
    ///     third layer here has an id of its own, so the set contains both answers at once.
    /// </remarks>
    [Fact]
    public void The_disarmed_rows_are_the_ones_the_compiler_refuses() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Shared("solo"));
        var panel = Panel(fixture);

        var compilation = LayerStackCompiler.Compile(document.Document, document.Document.Sets[0]);

        Assert.Null(compilation.Plan);

        var refused = compilation.Problems
            .Where(problem => problem.Message.Contains("share the id", StringComparison.Ordinal))
            .Select(problem => problem.Layer)
            .ToArray();

        Assert.Equal(["dup"], refused);

        // Three rows, one of which is the layer nothing shares an id with — and it is the only one
        // with a `Move up` on it.
        Assert.Equal(3, Texts(panel, "layer-stack-row-name").Count);
        Assert.Equal(2, Texts(panel, "layer-stack-row-refusal").Count);
        Assert.Single(Buttons(panel, "layer-stack-move-up"));
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

    /// <summary>Two layers carrying one id, and optionally a third that carries its own.</summary>
    /// <param name="third">
    ///     An id for a layer nothing shares one with, or empty for a stack that is entirely ambiguous.
    /// </param>
    static LayerStackAsset Shared(string third = "") {
        List<LayerAsset> layers = [Fill("dup", "Lower", 0.25f), Fill("dup", "Upper", 0.75f)];

        if (third.Length > 0) {
            layers.Add(Fill(third, "Solo", 0.5f));
        }

        return Stack([new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }], [.. layers]);
    }

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

    /// <summary>Three layers, the middle of which anchors its mask onto the bottom one.</summary>
    /// <remarks>
    ///     ⚠ <b>The middle one, deliberately: for the topmost layer "everything below" and
    ///     "everything but itself" are the same list</b>, so a picker that only excluded self would
    ///     pass a test built on the top row. Here they differ by the layer above.
    /// </remarks>
    static LayerStackAsset Anchored() =>
        Stack(
            [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
            Fill("bottom", "Bottom", 0.25f),
            new LayerAsset {
                Id = "middle",
                Name = "Middle",
                Kind = LayerKind.Fill,
                Values = { ["baseColor"] = [0.5f, 0.5f, 0.5f, 1f] },
                Mask = new() { Source = LayerMaskSource.Anchor, Anchor = "bottom" }
            },
            Fill("top", "Top", 0.75f)
        );

    /// <summary>⚠ The same anchor the wrong way round: the bottom layer reading the top one.</summary>
    /// <remarks>
    ///     A file can say this and the compile refuses it; what it is here for is the picker, whose
    ///     honest answer for the bottom layer of a stack is that there is nothing to offer.
    /// </remarks>
    static LayerStackAsset AnchoredFromTheBottom() =>
        Stack(
            [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
            new LayerAsset {
                Id = "bottom",
                Name = "Bottom",
                Kind = LayerKind.Fill,
                Values = { ["baseColor"] = [0.25f, 0.25f, 0.25f, 1f] },
                Mask = new() { Source = LayerMaskSource.Anchor, Anchor = "top" }
            },
            Fill("top", "Top", 0.75f)
        );

    /// <summary>⚠ A group, whose second child anchors — the case a flat stack cannot express.</summary>
    /// <remarks>
    ///     ⚠ <b>The anchored layer is <em>inside</em> the group and its target is its own sibling</b>,
    ///     because what separates the post-order walk from a parent-first one is a single option: the
    ///     group. Every other layer here is offered identically by both. <c>'top'</c> is above the
    ///     whole group and <c>'bottom'</c> below it, so the group's boundary is crossed in both
    ///     directions.
    /// </remarks>
    static LayerStackAsset AnchoredInsideAGroup() =>
        Stack(
            [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
            Fill("bottom", "Bottom", 0.2f),
            new LayerAsset {
                Id = "g",
                Name = "Group",
                Kind = LayerKind.Group,
                Children = [
                    Fill("inner", "Inner", 0.4f),
                    new LayerAsset {
                        Id = "child",
                        Name = "Child",
                        Kind = LayerKind.Fill,
                        Values = { ["baseColor"] = [0.6f, 0.6f, 0.6f, 1f] },
                        Mask = new() { Source = LayerMaskSource.Anchor, Anchor = "inner" }
                    }
                ]
            },
            Fill("top", "Top", 0.8f)
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

    /// <summary>Opens a stack and puts it in a view this test holds, laid out and framed.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Its own panel, because what these two tests read is the <em>view's</em> and the
    ///         module keeps its own private.</b> Every other test here goes through
    ///         <see cref="Panel" /> and reads the tree, which is the stronger assertion and the one
    ///         this file keeps to — but a zoom and a walk count are not elements, and a module that
    ///         handed its view out would be a seam that exists for xunit.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The first <c>Show</c> is before the layout on purpose.</b> <c>ImageView.Fit</c>
    ///         answers false with no box to fit against, which is the state a panel's first refresh is
    ///         really in; a helper that settled first would hide the retry the caller is asserting on.
    ///     </para>
    /// </remarks>
    static (LayerStackView View, LayerStackDocument Document) Viewed(
        TexturingFixture fixture,
        LayerStackAsset stack
    ) {
        var document = Open(fixture, stack);
        LayerStackView? built = null;

        fixture.Shell.RegisterPanel(
            ViewPanel,
            new StringId("editor.panel." + ViewPanel, "Layers"),
            panel => built = new LayerStackView(panel)
        );

        fixture.Shell.Workspace.Open(ViewPanel);

        Assert.NotNull(built);

        built.Show(document);

        fixture.Shell.Document.Update();
        fixture.Shell.Document.Draw();

        return (built, document);
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

    /// <summary>The only layer's mask, for a stack made by <see cref="Masked" />.</summary>
    static MaskAsset Mask(LayerStackDocument document) => document.Document.Sets[0].Layers[^1].Mask;

    /// <summary>Every control of one kind the panel drew under that tag, in layout order.</summary>
    static List<T> Controls<T>(UiElement root, string tag) where T : UiElement {
        List<T> found = [];

        foreach (var element in All(root, tag)) {
            found.Add(Assert.IsType<T>(element));
        }

        return found;
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
